using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ComponentManager = Autodesk.Windows.ComponentManager;
using RibbonCommandItem = Autodesk.Windows.RibbonCommandItem;

namespace BotPlus.Eom;

/// <summary>Запускает штатные действия Revit для системы коммутации светильников.</summary>
internal static class LightingSwitchConnection
{
    // В Revit 2026 система коммутации не создаётся через ElectricalSystem.Create.
    // Идентификаторы контекстных кнопок одинаковы в разных локализациях Revit.
    const string CreateSystem = "ID_RBS_CREATE_SYSTEM_GROUP";
    const string SelectSwitch = "Dialog_BuildingSystems_RbsEditSwitchSystem:Control_BuildingSystems_RbsEditSwitchSystemSelectSwitch";

    sealed record Request(Document Document, ElementId ViewId, ElementId[] Fixtures, long Deadline);
    static Request? pending;

    internal static void Start(UIDocument ui, ElementId[] fixtures)
    {
        if (pending != null) return;
        var request = new Request(ui.Document, ui.ActiveView.Id, fixtures, Environment.TickCount64 + 3000);
        pending = request;
        RunOnRibbon(CreateSystem, "Revit не может создать систему коммутации для выбранных светильников. Проверьте их электрические соединители и существующие системы коммутации.", () =>
        {
            if (pending == request) pending = null;
        });
    }

    internal static bool Advance(UIDocument ui, IdlingEventArgs args)
    {
        if (pending is not { } request) return false;
        if (!request.Document.Equals(ui.Document) || request.ViewId != ui.ActiveView.Id)
        {
            pending = null;
            return false;
        }

        var ids = ui.Selection.GetElementIds();
        // После создания Revit сам выделяет систему вместо её светильников.
        if (ids.Count == 1 && ui.Document.GetElement(ids.Single())?.Category?.BuiltInCategory == BuiltInCategory.OST_SwitchSystem)
        {
            pending = null;
            RunOnRibbon(SelectSwitch, "Система коммутации создана. На её контекстной вкладке нажмите «Выбрать переключатель».");
            return true;
        }
        if (!ids.OrderBy(id => id.Value).SequenceEqual(request.Fixtures))
        {
            pending = null;
            return false;
        }
        if (Environment.TickCount64 >= request.Deadline)
        {
            pending = null;
            TaskDialog.Show("BotPlus ЭОМ", "Revit не выделил созданную систему коммутации. Проверьте, доступно ли для этих светильников создание системы.");
            return false;
        }

        args.SetRaiseWithoutDelay();
        return true;
    }

    internal static void Reset() => pending = null;

    static void RunOnRibbon(string id, string unavailableMessage, Action? onFailure = null)
    {
        void Fail()
        {
            onFailure?.Invoke();
            TaskDialog.Show("BotPlus ЭОМ", unavailableMessage);
        }

        if (ComponentManager.Ribbon is not { } ribbon) { Fail(); return; }
        // Штатная команда запускается после выхода из ExternalEvent или Idling.
        ribbon.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try
            {
                var item = ribbon.FindItem(id, searchActiveTabOnly: true, searchSubItems: true);
                if (item == null)
                {
                    var candidate = ribbon.FindItem(id, false, out _, out var tab, true);
                    if (tab is { IsVisible: true }) item = candidate;
                }
                if (item is RibbonCommandItem { IsVisible: true } button)
                {
                    var command = button.CommandHandler ?? ribbon.CommandHandler ?? ComponentManager.CommandHandler;
                    var parameter = button.CommandParameter ?? button;
                    if (command?.CanExecute(parameter) == true)
                    {
                        command.Execute(parameter);
                        return;
                    }
                }
            }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            Fail();
        }));
    }
}

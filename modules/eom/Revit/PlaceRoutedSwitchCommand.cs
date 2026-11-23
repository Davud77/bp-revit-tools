using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BotPlus.Eom;

[Transaction(TransactionMode.Manual)]
public sealed class PlaceRoutedSwitchCommand : EomCommand
{
    protected override Result Run(UIDocument ui) => ConduitThroughLightsCommand.PlaceRoutedSwitch(ui);
}

public sealed partial class ConduitThroughLightsCommand
{
    internal static Result PlaceRoutedSwitch(UIDocument ui) => PlaceRoutedSwitch(ui,
        PickBody(ui, e => IsLight(e) || e is Conduit || IsFitting(e), null,
            "Выберите светильник, свободный конец короба или распределительную коробку."));

    static Result PlaceRoutedSwitch(UIDocument ui, Pick pick)
    {
        var doc = ui.Document;
        var level = ui.ActiveView.GenLevel
            ?? throw new InvalidOperationException("Откройте план этажа для установки выключателя на проектной высоте.");
        var settings = EomStore.Settings(doc);
        var source = pick.Element ?? throw new InvalidOperationException("Выберите начальный вывод для подключения выключателя.");
        var wall = SwitchBoxPlacement.PickWall(ui);
        if (ChooseSwitchType(ui, wall) is not { } symbol) return Result.Cancelled;

        var type = ChooseType(doc, source);
        var boxTypes = BoxTypes(doc);
        var elbowType = FittingType(doc, ElbowFamily);
        double? diameter = source is Conduit conduit ? conduit.Diameter
            : Nearest(FreePorts(source, null), pick.Point, true).FirstOrDefault()?.Diameter;

        using var group = new TransactionGroup(doc, "ЭОМ: выключатель на стене и спуск");
        if (group.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit не смог начать установку выключателя.");

        FamilyInstance device;
        using (var place = new Transaction(doc, "ЭОМ: выключатель на выбранной стене"))
        {
            var failures = StartRouteTransaction(place);
            device = SwitchBoxPlacement.PlaceSwitch(doc, symbol, level, wall, Feet(settings.SwitchHeightMm));
            FitPorts(doc, device, diameter);
            FinishRouteTransaction(place, failures, "Revit не смог поставить выбранный выключатель на стену.");
        }
        var above = SwitchBoxPlacement.Find(ui, device, wall, Feet(settings.SwitchBoxOffsetMm));
        var choices = PrepareLeg(ui, source, pick.Point, device, FixturePoint(device), above, diameter, 0, false, sourcePortId: pick.PortId);
        var verified = VerifySwitchBoxChoices(ui, type, boxTypes, elbowType, source, device, choices);
        var leg = ReviewLeg(ui, verified, true, false);
        if (leg == null) return Result.Cancelled;

        RouteCommand.PrepareWiring(ui);
        CommitSwitchConnection(ui, type, boxTypes, elbowType, source, device, leg);
        if (group.Assimilate() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit отменил установку и подключение выключателя.");
        ui.RefreshActiveView();
        return Result.Succeeded;
    }

    static FamilySymbol? ChooseSwitchType(UIDocument ui, SwitchBoxPlacement.WallPoint wall)
    {
        var doc = ui.Document;
        bool local = doc.GetElement(wall.Reference.ElementId) is Wall;
        var types = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingDevices)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(type => type.Family.FamilyPlacementType is FamilyPlacementType.OneLevelBased or FamilyPlacementType.WorkPlaneBased
                || local && type.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted)
            .OrderBy(type => type.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
            .ThenByDescending(type => type.FamilyName == "ADSK_Выключатель_Встраиваемый"
                && type.Name.StartsWith("Одноклавишный 1P, IP20", StringComparison.OrdinalIgnoreCase))
            .ThenBy(type => type.FamilyName).ThenBy(type => type.Name).ToArray();
        if (types.Length == 0)
            throw new InvalidOperationException("Загрузите в проект тип выключателя, который можно поставить на выбранную стену.");

        var window = EomUi.Window("Тип выключателя", 560, 175);
        new WindowInteropHelper(window).Owner = ui.Application.MainWindowHandle;
        window.SizeToContent = SizeToContent.Height;
        window.MinHeight = 0;
        var options = new ComboBox { ItemsSource = types.Select(type => $"{type.FamilyName} · {type.Name}").ToArray(), SelectedIndex = 0 };
        var cancel = EomUi.Button("Отмена", () => window.DialogResult = false);
        cancel.IsCancel = true;
        EomUi.Layout(window, options, EomUi.Hint("Выберите загруженный тип выключателя."), EomUi.Buttons(cancel,
            EomUi.Button("Далее", () => window.DialogResult = true, true)));
        return window.ShowDialog() == true ? types[options.SelectedIndex] : null;
    }
}

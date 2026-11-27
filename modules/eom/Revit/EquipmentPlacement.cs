using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace BotPlus.Eom;

internal static class EquipmentPlacement
{
    const double PointTolerance = 1e-5; // футы

    internal static void Run(UIDocument ui, ElementId id, bool fromDrop)
    {
        var doc = ui.Document;
        if (doc.GetElement(id) is not FamilySymbol symbol)
        {
            TaskDialog.Show("BotPlus ЭОМ", "Тип не найден в активном проекте. Нажмите «Обновить» в Базе УГО.");
            return;
        }

        if (!symbol.IsActive)
        {
            using var transaction = new Transaction(doc, "ЭОМ: активация типа");
            transaction.Start();
            symbol.Activate();
            if (transaction.Commit() != TransactionStatus.Committed) return;
        }
        var level = ui.ActiveView.GenLevel;
        if (level != null && symbol.Family.FamilyPlacementType != FamilyPlacementType.ViewBased)
        {
            var defaults = EomStore.Settings(doc);
            double? defaultHeight = symbol.Category?.BuiltInCategory switch
            {
                BuiltInCategory.OST_LightingDevices => defaults.SwitchHeightMm,
                BuiltInCategory.OST_ElectricalFixtures => defaults.ElectricalFixtureHeightMm,
                BuiltInCategory.OST_ElectricalEquipment => defaults.ElectricalEquipmentHeightMm,
                BuiltInCategory.OST_LightingFixtures => defaults.LightingFixtureHeightMm,
                _ => null
            };
            var levelZ = level.ProjectElevation;
            var feetPerMm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
            double? Elevation(double mm)
            {
                if (!double.IsFinite(mm) || mm < 0) return null;
                double z = levelZ + mm * feetPerMm;
                return double.IsFinite(z) && XYZ.IsWithinLengthLimits(new XYZ(0, 0, z)) ? z : null;
            }

            var defaultZ = defaultHeight is { } mm ? Elevation(mm) : null;
            var panel = PlacementHeightWindow.TryShow(ui, defaultZ.HasValue ? defaultHeight : null, Elevation);
            Place(ui, symbol, panel, defaultZ);
            return;
        }

        if (fromDrop) Place(ui, symbol, null, null);
        else ui.PostRequestForElementTypePlacement(symbol);
    }

    static void Place(UIDocument ui, FamilySymbol symbol, PlacementHeightWindow? panel, double? defaultZ)
    {
        var doc = ui.Document;
        var app = ui.Application.Application;
        var added = new Dictionary<ElementId, (double? Height, bool Emergency, ElementId View)>();
        var symbolId = symbol.Id;
        void Capture(object? sender, DocumentChangedEventArgs args)
        {
            if (!args.GetDocument().Equals(doc)) return;
            var settings = (panel?.OverrideZ ?? defaultZ, SystemModeToggle.IsEmergency, doc.ActiveView.Id);
            foreach (var id in args.GetAddedElementIds())
                if (doc.GetElement(id) is FamilyInstance instance && instance.GetTypeId() == symbolId
                    && instance.SuperComponent == null)
                    added.TryAdd(id, settings);
        }

        app.DocumentChanged += Capture;
        try { ui.PromptForFamilyInstancePlacement(symbol); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
        finally
        {
            app.DocumentChanged -= Capture;
            if (panel?.IsVisible == true) panel.Close();
        }

        if (added.Count > 0) ApplyPlacement(doc, added);
    }

    static void ApplyPlacement(Document doc, Dictionary<ElementId, (double? Height, bool Emergency, ElementId View)> added)
    {
        var instances = added.Keys.Select(doc.GetElement).OfType<FamilyInstance>().ToList();
        if (instances.Count == 0) return;

        var skipped = new List<ElementId>();
        using var transaction = new Transaction(doc, "ЭОМ: система и высота вставки оборудования");
        transaction.Start();
        foreach (var instance in instances)
        {
            var settings = added[instance.Id];
            EomStore.SaveRouteMode(instance, settings.Emergency);
            if (doc.GetElement(settings.View) is View view) RouteService.SetRouteColor(view, instance.Id, settings.Emergency);
            if (settings.Height is { } height && !SetHeight(doc, instance, height))
                skipped.Add(instance.Id);
        }
        if (transaction.Commit() != TransactionStatus.Committed)
        {
            TaskDialog.Show("BotPlus ЭОМ", "Revit отменил применение системы и высоты. Проверьте размещённые экземпляры.");
            return;
        }
        if (skipped.Count == 0) return;
        string ids = string.Join(", ", skipped.Take(10).Select(id => id.Value)) + (skipped.Count > 10 ? "…" : "");
        TaskDialog.Show("BotPlus ЭОМ", $"Не удалось выставить высоту у {skipped.Count} экз. (ID: {ids}). Сохранено положение, заданное при размещении. Проверьте ограничения семейства и его основы.");
    }

    static bool SetHeight(Document doc, FamilyInstance instance, double targetZ)
    {
        if (instance.Location is not LocationPoint location) return false;
        XYZ old = location.Point;
        double delta = targetZ - old.Z;
        if (Math.Abs(delta) <= PointTolerance) return true;
        var target = new XYZ(old.X, old.Y, targetZ);
        if (!XYZ.IsWithinLengthLimits(target)) return false;

        if (TryOffset(BuiltInParameter.INSTANCE_ELEVATION_PARAM, delta)) return true;
        // Смещение от основы может отсчитываться вниз, например от нижней грани перекрытия.
        if (TryOffset(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, delta)
            || TryOffset(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, -delta)) return true;

        return TryAdjust(() =>
        {
            ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, delta));
            return true;
        });

        bool TryOffset(BuiltInParameter parameterId, double offset) =>
            instance.get_Parameter(parameterId) is { IsReadOnly: false, StorageType: StorageType.Double } parameter
            && TryAdjust(() => parameter.Set(parameter.AsDouble() + offset));

        bool TryAdjust(Func<bool> change)
        {
            using var trial = new SubTransaction(doc);
            trial.Start();
            bool changed;
            try
            {
                changed = change();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException error)
                when (error is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Семейство или основа могут запрещать изменение отметки.
                changed = false;
            }
            if (changed)
            {
                doc.Regenerate();
                if (instance.Location is LocationPoint adjusted && adjusted.Point.DistanceTo(target) <= PointTolerance)
                    return trial.Commit() == TransactionStatus.Committed;
            }
            trial.RollBack();
            return false;
        }
    }
}

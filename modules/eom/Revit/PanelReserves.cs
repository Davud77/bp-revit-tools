using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BotPlus.Eom;

internal static class PanelReserves
{
    internal static void Add(Document doc, ElementId panelId, double rating)
    {
        using var transaction = new Transaction(doc, "ЭОМ: резерв щита");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Не удалось начать добавление резерва.");
        var schedule = Schedule(doc, panelId) ?? PanelScheduleView.CreateInstanceView(doc, panelId);
        doc.Regenerate();
        var data = (PanelScheduleData)schedule.GetTableData();
        for (int slot = 1; slot <= data.NumberOfSlots; slot++)
        {
            schedule.GetCellsBySlotNumber(slot, out var rows, out var cols);
            var cells = rows.Zip(cols).ToList();
            if (cells.Count == 0 || cells.Any(cell => schedule.GetCircuitByCell(cell.First, cell.Second) != null ||
                schedule.IsSpare(cell.First, cell.Second) || schedule.IsSpace(cell.First, cell.Second))) continue;
            foreach (var (row, col) in cells)
            {
                try { schedule.AddSpare(row, col); }
                catch (Autodesk.Revit.Exceptions.ArgumentOutOfRangeException) { continue; }
                doc.Regenerate();
                if (!schedule.IsSpare(row, col) || schedule.GetCircuitByCell(row, col) is not ElectricalSystem spare)
                    throw new InvalidOperationException("Revit не вернул цепь резерва. Проверьте шаблон спецификации щита.");
                spare.Rating = UnitUtils.ConvertToInternalUnits(rating, UnitTypeId.Amperes);
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил добавление резерва.");
                return;
            }
        }
        throw new InvalidOperationException("В спецификации выбранного щита нет свободной позиции для резерва.");
    }

    internal static void Remove(Document doc, ElementId panelId, ElementId reserveId)
    {
        if (doc.GetElement(reserveId) is not ElectricalSystem { CircuitType: CircuitType.Spare } spare
            || spare.BaseEquipment?.Id != panelId)
            throw new InvalidOperationException("Выберите резервную линию выбранного щита.");

        using var transaction = new Transaction(doc, "ЭОМ: удалить резерв щита");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Не удалось начать удаление резерва.");
        var schedule = Schedule(doc, panelId) ?? PanelScheduleView.CreateInstanceView(doc, panelId);
        doc.Regenerate();
        var data = (PanelScheduleData)schedule.GetTableData();
        for (int slot = 1; slot <= data.NumberOfSlots; slot++)
        {
            schedule.GetCellsBySlotNumber(slot, out var rows, out var cols);
            foreach (var (row, col) in rows.Zip(cols))
            {
                if (!schedule.IsSpare(row, col) || schedule.GetCircuitByCell(row, col)?.Id != reserveId) continue;
                schedule.RemoveSpare(row, col);
                doc.Regenerate();
                if (doc.GetElement(reserveId) is ElectricalSystem { CircuitType: CircuitType.Spare })
                    throw new InvalidOperationException("Revit не освободил позицию резерва.");
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил удаление резерва.");
                return;
            }
        }
        throw new InvalidOperationException("Резервная линия не найдена в спецификации выбранного щита.");
    }

    static PanelScheduleView? Schedule(Document doc, ElementId panelId) =>
        new FilteredElementCollector(doc).OfClass(typeof(PanelScheduleView)).Cast<PanelScheduleView>()
            .FirstOrDefault(v => !v.IsTemplate && !v.IsPanelScheduleTemplate() && v.GetPanel() == panelId);
}

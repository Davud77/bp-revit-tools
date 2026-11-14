using Autodesk.Revit.DB;

namespace BotPlus.Eom;

/// <summary>Однолинейная схема щита на чертёжном виде: шина, отходящие линии с аппаратами и таблица параметров групп.</summary>
internal static class SingleLine
{
    const double Mm = 1 / 304.8, Col = 24, RowH = 8, Label = 42;
    static readonly string[] Rows = ["Аппарат защиты", "Кабель", "Длина, м", "Pу, кВт", "Iр, А", "ΔU, %", "Группа", "Потребитель"];

    /// <summary>Требует транзакции. Возвращает созданный вид.</summary>
    internal static ViewDrafting Draw(Document doc, PanelInfo panel, EomSettings settings, EomCatalog catalog)
    {
        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting)
            ?? throw new InvalidOperationException("В проекте нет типа чертёжного вида.");
        var view = ViewDrafting.Create(doc, type.Id);
        view.Scale = 1;
        string baseName = "ЭОМ — Однолинейная схема " + panel.Name, name = baseName;
        var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToHashSet();
        for (int i = 2; names.Contains(name); i++) name = $"{baseName} ({i})";
        view.Name = name;
        var text = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

        void Line(double x1, double y1, double x2, double y2) => doc.Create.NewDetailCurve(view, Autodesk.Revit.DB.Line.CreateBound(new XYZ(x1 * Mm, y1 * Mm, 0), new XYZ(x2 * Mm, y2 * Mm, 0)));
        void Text(double x, double y, double width, string value)
        {
            if (value.Length == 0) return;
            TextNote.Create(doc, view.Id, new XYZ(x * Mm, y * Mm, 0), Math.Max(width, 8) * Mm, value, new TextNoteOptions(text) { HorizontalAlignment = HorizontalTextAlignment.Left });
        }

        var results = panel.Circuits.Select(c => (Circuit: c, Result: EomCalc.Select(c.Input, settings, catalog))).ToList();
        var load = EomCalc.PanelLoad(panel.Circuits.Select(c => c.Input));
        double current = EomCalc.Current(load.P, load.S > 0 ? load.P / load.S : 1, 3, settings);
        int n = Math.Max(results.Count, 1);
        double busEnd = Label + n * Col;

        Text(0, 60, 200, $"Щит {panel.Name}");
        Text(0, 52, 260, $"Pр = {load.P / 1000:0.##} кВт;  Sр = {load.S / 1000:0.##} кВА;  Iр = {current:0.#} А;  нормы: {settings.LoadNorm}");
        // Ввод и шина.
        Line(Label - 10, 40, Label - 10, 0); Text(Label - 8, 38, 40, "Ввод");
        Line(Label - 14, 0, busEnd, 0); Line(Label - 14, -0.8, busEnd, -0.8);
        double tableTop = -34, tableBottom = tableTop - Rows.Length * RowH;
        for (int i = 0; i < results.Count; i++)
        {
            double x = Label + i * Col + Col / 2;
            Line(x, 0, x, -10);                       // отвод от шины
            Line(x, -10, x + 3.5, -17);               // контакт автомата
            Line(x - 1.5, -8.5, x + 1.5, -11.5); Line(x - 1.5, -11.5, x + 1.5, -8.5); // крест — расцепитель
            Line(x, -18, x, tableTop);                // линия к таблице
            var (c, r) = results[i];
            string breaker = c.RatingA > 0 ? $"{c.RatingA:0} А" : r.Breaker?.Name ?? "—";
            string cable = c.Cable.Length > 0 ? c.Cable : r.Cable?.Name ?? "—";
            string[] cells = [breaker, cable, r.CableLengthM.ToString("0.#"), (c.Input.ActivePowerW / 1000).ToString("0.###"), r.CurrentA.ToString("0.#"),
                r.VoltageDrop.ToString("0.##"), EomSettings.Mark(settings.GroupMask, c.Input.Panel, c.Input.Number), c.Input.Name];
            for (int row = 0; row < cells.Length; row++) Text(Label + i * Col + 1, tableTop - row * RowH - 1.5, Col - 2, cells[row]);
        }
        // Сетка таблицы.
        for (int row = 0; row <= Rows.Length; row++) Line(0, tableTop - row * RowH, busEnd, tableTop - row * RowH);
        Line(0, tableTop, 0, tableBottom);
        for (int i = 0; i <= n; i++) Line(Label + i * Col, tableTop, Label + i * Col, tableBottom);
        for (int row = 0; row < Rows.Length; row++) Text(1, tableTop - row * RowH - 1.5, Label - 2, Rows[row]);
        return view;
    }

    /// <summary>Новый лист с видом. Основная надпись — первая найденная в проекте. Требует транзакции.</summary>
    internal static ViewSheet PlaceOnSheet(Document doc, View view)
    {
        var title = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().FirstElementId();
        var sheet = ViewSheet.Create(doc, title ?? ElementId.InvalidElementId);
        sheet.Name = view.Name.Replace("ЭОМ — ", "");
        Viewport.Create(doc, sheet.Id, view.Id, new XYZ(200 * Mm, 150 * Mm, 0));
        return sheet;
    }
}

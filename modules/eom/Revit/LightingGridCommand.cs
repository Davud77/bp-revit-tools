using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.Eom;

[Transaction(TransactionMode.Manual)]
public sealed class LightingGridCommand : EomCommand
{
    sealed record Choice<T>(T Value, string Label);

    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        var rooms = Rooms.Collect(doc).Select(r => new Choice<RoomRef>(r, $"{r.Number} {r.Name} · {r.Source}")).ToList();
        if (rooms.Count == 0) throw new InvalidOperationException("В проекте и связях нет размещённых помещений.");
        var types = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(Rooms.CanPlace)
            .Select(s => new Choice<FamilySymbol>(s, $"{s.FamilyName}: {s.Name}"))
            .OrderBy(s => s.Label, NaturalComparer.Instance).ToList();
        if (types.Count == 0) throw new InvalidOperationException("Загрузите семейство категории «Осветительные приборы» на уровне без основы.");

        var window = EomUi.Window("Групповая расстановка", 840, 420);
        var roomBox = new ComboBox { Width = 460, ItemsSource = rooms, DisplayMemberPath = "Label", IsTextSearchEnabled = true };
        var typeBox = new ComboBox { Width = 460, ItemsSource = types, DisplayMemberPath = "Label", IsTextSearchEnabled = true };
        var x = EomUi.Number(2); var y = EomUi.Number(2); var height = EomUi.Number(3);
        var note = EomUi.Hint("");
        var stored = EomStore.RoomLights(doc);
        (RoomRef Room, FamilySymbol Symbol, List<XYZ> Points, double Height)? request = null;

        double ReadPositive(TextBox input, string label)
        {
            double value = EomUi.Parse(input, label);
            if (!double.IsFinite(value) || value <= 0) throw new InvalidOperationException($"«{label}» должно быть больше нуля.");
            return value;
        }
        void UpdateCount()
        {
            request = null;
            if (roomBox.SelectedItem is not Choice<RoomRef> room || typeBox.SelectedItem is not Choice<FamilySymbol> type) return;
            try
            {
                double mount = ReadPositive(height, "Высота установки");
                var points = Rooms.SpacedPoints(room.Value, ReadPositive(x, "Шаг по X"), ReadPositive(y, "Шаг по Y"));
                if (points.Count == 0) throw new InvalidOperationException("В контуре нет точек сетки. Уменьшите шаг.");
                request = (room.Value, type.Value, points, mount);
                note.Text = $"Будет добавлено светильников: {points.Count}.";
            }
            catch (InvalidOperationException e) { note.Text = e.Message; }
        }
        roomBox.SelectionChanged += (_, _) =>
        {
            if (roomBox.SelectedItem is Choice<RoomRef> choice)
            {
                double saved = stored.GetValueOrDefault(choice.Value.Key)?.MountM ?? 0;
                height.Text = (saved > 0 ? saved : choice.Value.Geometry().HeightM).ToString("0.###");
            }
            UpdateCount();
        };
        typeBox.SelectionChanged += (_, _) => UpdateCount();
        foreach (var field in new[] { x, y, height }) field.TextChanged += (_, _) => UpdateCount();

        var body = new StackPanel();
        body.Children.Add(EomUi.Row("Помещение", roomBox));
        body.Children.Add(EomUi.Row("Семейство и тип", typeBox));
        body.Children.Add(EomUi.Row("Шаг по X", x, "м"));
        body.Children.Add(EomUi.Row("Шаг по Y", y, "м"));
        body.Children.Add(EomUi.Row("Высота установки от пола", height, "м"));
        body.Children.Add(note);
        var selected = ui.Selection.GetElementIds().Select(doc.GetElement).OfType<Room>().FirstOrDefault();
        int selectedRoom = selected == null ? -1 : rooms.FindIndex(r => r.Value.Room.Id == selected.Id && r.Value.Room.Document.Equals(doc));
        roomBox.SelectedIndex = Math.Max(0, selectedRoom);
        typeBox.SelectedIndex = 0;
        EomUi.Layout(window, body, EomUi.Hint("Сетка центрируется в габарите помещения по осям его файла. Точки вне контура исключаются. Существующие светильники сохраняются."),
            EomUi.Buttons(EomUi.Button("Расставить", () =>
            {
                if (request == null) throw new InvalidOperationException(note.Text);
                window.DialogResult = true;
            }, true), EomUi.Button("Отмена", window.Close)));
        if (window.ShowDialog() != true || request is not { } placement) return Result.Cancelled;

        using var transaction = new Transaction(doc, "ЭОМ: групповая расстановка светильников");
        transaction.Start();
        var box = placement.Room.Room.get_BoundingBox(null) ?? throw new InvalidOperationException("Не определены габариты помещения.");
        var ids = Rooms.PlacePoints(doc, placement.Room, placement.Symbol, box.Min.Z, placement.Points, placement.Height);
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил расстановку.");
        TaskDialog.Show("BotPlus ЭОМ", $"Размещено светильников: {ids.Count}. Помещение {placement.Room.Number} {placement.Room.Name}.");
        return Result.Succeeded;
    }
}

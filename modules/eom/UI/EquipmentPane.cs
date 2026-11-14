using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;

namespace BotPlus.Eom;

/// <summary>
/// Закрепляемая панель «BotPlus База УГО»: типоразмеры загруженных семейств ЭОМ с поиском, эскизом и числом экземпляров.
/// Тип можно перетащить мышью на вид Revit — сразу начнётся его размещение (UIApplication.DoDragDrop + IDropHandler),
/// либо дважды щёлкнуть / нажать «Разместить».
/// </summary>
internal sealed class EquipmentPane : IDockablePaneProvider
{
    internal sealed record Item(ElementId Id, string Category, string Family, string Type)
    {
        public override string ToString() => $"{Family}: {Type}";
    }

    readonly DockPanel root = new();
    readonly ComboBox categories = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly ListBox list = new();
    readonly Image image = new() { Height = 170, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(4) };
    readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
    readonly Dictionary<ElementId, BitmapSource?> previews = [];
    List<Item> items = [];
    string document = "";
    Point? dragStart;

    internal EquipmentPane()
    {
        BotPlusTheme.Apply(root);
        root.Margin = new Thickness(6);
        categories.SelectionChanged += (_, _) => Filter();
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => Describe();
        list.MouseDoubleClick += (_, _) => Place();
        list.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(list);
        list.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
        list.PreviewMouseMove += (_, e) => Drag(e);

        var top = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        void B(string text, Action action, string tip) { var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 2), ToolTip = tip }; b.Click += (_, _) => action(); bar.Children.Add(b); }
        B("Разместить", Place, "Разместить выбранный тип (или перетащите его на вид)");
        B("Загрузить…", Load, "Загрузить семейство .rfa в проект");
        B("Обновить", () => App.Queue?.Post(app => { if (app.ActiveUIDocument is { } ui) Refresh(ui.Document); }), "Перечитать семейства активного проекта");
        top.Children.Add(bar);
        top.Children.Add(new TextBlock { Text = "Категория", Opacity = 0.7 }); top.Children.Add(categories);
        top.Children.Add(new TextBlock { Text = "Поиск", Opacity = 0.7 }); top.Children.Add(search);

        var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var frame = new Border { BorderThickness = new Thickness(1), Child = image, Background = System.Windows.Media.Brushes.White };
        frame.Themed(Border.BorderBrushProperty, "BP.Border");
        bottom.Children.Add(frame); bottom.Children.Add(caption); bottom.Children.Add(status);
        status.Text = "Перетащите тип на план — начнётся размещение. Двойной щелчок — то же самое. Esc завершает размещение.";

        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(list);
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = root;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        data.VisibleByDefault = false;
    }

    /// <summary>Перечитывает типы активного проекта. Вызывать в контексте Revit API.</summary>
    internal void Refresh(Document doc)
    {
        document = doc.PathName + "|" + doc.Title;
        previews.Clear();
        items = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category != null && Network.DeviceCategories.Contains(s.Category.BuiltInCategory))
            .Select(s => new Item(s.Id, s.Category.Name, s.FamilyName, s.Name))
            .OrderBy(i => i.Category).ThenBy(i => i.Family).ThenBy(i => i.Type).ToList();
        // Эскизы считаются сразу: панель немодальная, и читать API из её событий нельзя.
        foreach (var i in items) previews[i.Id] = Preview((ElementType)doc.GetElement(i.Id));
        string? keep = categories.SelectedItem as string;
        categories.Items.Clear(); categories.Items.Add("Все категории");
        foreach (var c in items.Select(i => i.Category).Distinct()) categories.Items.Add(c);
        categories.SelectedItem = keep != null && categories.Items.Contains(keep) ? keep : "Все категории";
        Filter();
        if (items.Count == 0) caption.Text = "В проекте нет семейств ЭОМ. Загрузите .rfa — семейство должно содержать 3D-геометрию и 2D-УГО по ГОСТ для низкой детализации.";
    }

    /// <summary>Обновляет панель, если активным стал другой проект. Вызывается из ViewActivated (контекст API).</summary>
    internal void Follow(Document doc) { if (doc.PathName + "|" + doc.Title != document && !doc.IsFamilyDocument) Refresh(doc); }

    void Filter()
    {
        string q = search.Text.Trim();
        list.ItemsSource = items.Where(i => (categories.SelectedIndex <= 0 || i.Category == (string)categories.SelectedItem)
            && (q.Length == 0 || i.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (list.Items.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0;
    }

    void Describe()
    {
        if (list.SelectedItem is not Item i) { image.Source = null; caption.Text = ""; return; }
        image.Source = previews.TryGetValue(i.Id, out var source) ? source : null;
        caption.Text = string.Join("\n", i.Category, "Семейство: " + i.Family, "Тип: " + i.Type) + (image.Source == null ? "\nУ семейства нет сохранённого эскиза." : "");
        App.Queue?.Post(app =>
        {
            if (app.ActiveUIDocument?.Document is not { } doc || doc.GetElement(i.Id) is not FamilySymbol symbol) return;
            int count = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new FamilyInstanceFilter(doc, i.Id)).GetElementCount();
            string text = $"\nРазмещение: {Placement(symbol.Family.FamilyPlacementType)}\nВ проекте: {count} шт.";
            root.Dispatcher.Invoke(() => { if (Equals(list.SelectedItem, i)) caption.Text += text; });
        });
    }

    void Place()
    {
        if (list.SelectedItem is not Item i) return;
        App.Queue?.Post(app => { if (app.ActiveUIDocument is { } ui) Placement(ui, i.Id, false); });
    }

    void Load()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Семейства Revit (*.rfa)|*.rfa", Title = "Загрузить семейство ЭОМ", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var files = dialog.FileNames;
        App.Queue?.Post(app =>
        {
            if (app.ActiveUIDocument?.Document is not { } doc) return;
            using var t = new Transaction(doc, "ЭОМ: загрузка семейства"); t.Start();
            int loaded = files.Count(f => doc.LoadFamily(f, out _));
            t.Commit();
            Refresh(doc);
            if (loaded < files.Length) TaskDialog.Show("BotPlus ЭОМ", $"Загружено {loaded} из {files.Length}: остальные уже есть в проекте или не загружаются.");
        });
    }

    void Drag(MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || list.SelectedItem is not Item i) return;
        var now = e.GetPosition(list);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragStart = null;
        // Revit сам ведёт перетаскивание; при отпускании над видом вызывает обработчик в контексте API.
        UIApplication.DoDragDrop(new DropData(document, i.Id), new DropHandler());
    }

    sealed record DropData(string Document, ElementId Symbol);

    sealed class DropHandler : IDropHandler
    {
        public void Execute(UIDocument ui, object data)
        {
            if (data is not DropData d) return;
            try { Placement(ui, d.Symbol, true); }
            catch (Exception error) { TaskDialog.Show("BotPlus ЭОМ", error.Message); }
        }
    }

    /// <summary>Размещение типа: при перетаскивании — сразу (PromptForFamilyInstancePlacement), из кнопки — запросом после обработчика.</summary>
    static void Placement(UIDocument ui, ElementId id, bool prompt)
    {
        if (ui.Document.GetElement(id) is not FamilySymbol symbol) { TaskDialog.Show("BotPlus ЭОМ", "Тип не найден в активном проекте. Нажмите «Обновить» в Базе УГО."); return; }
        if (!symbol.IsActive) { using var t = new Transaction(ui.Document, "ЭОМ: активация типа"); t.Start(); symbol.Activate(); t.Commit(); }
        if (!prompt) { ui.PostRequestForElementTypePlacement(symbol); return; }
        try { ui.PromptForFamilyInstancePlacement(symbol); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
    }

    static string Placement(FamilyPlacementType type) => type switch
    {
        FamilyPlacementType.OneLevelBased => "на уровне",
        FamilyPlacementType.OneLevelBasedHosted => "на основе (стена, потолок)",
        FamilyPlacementType.WorkPlaneBased => "на рабочей плоскости / грани",
        FamilyPlacementType.ViewBased => "на виде (2D)",
        _ => type.ToString()
    };

    static BitmapSource? Preview(ElementType type)
    {
        try
        {
            using var bitmap = type.GetPreviewImage(new System.Drawing.Size(200, 200));
            if (bitmap == null) return null;
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch { return null; }
    }
}

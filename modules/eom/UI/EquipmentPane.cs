using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;
using Visibility = System.Windows.Visibility;

namespace BotPlus.Eom;

/// <summary>
/// Закрепляемая панель «BotPlus База УГО»: типоразмеры загруженных семейств ЭОМ с поиском, эскизом и числом экземпляров.
/// Тип можно перетащить мышью на вид Revit — сразу начнётся его размещение (UIApplication.DoDragDrop + IDropHandler),
/// либо дважды щёлкнуть / нажать «Разместить».
/// </summary>
internal sealed class EquipmentPane : IDockablePaneProvider
{
    internal sealed record Item(ElementId Id, BuiltInCategory CategoryId, string Category, string Family, string Type)
    {
        public BitmapSource? Preview { get; init; }
        public override string ToString() => $"{Family}: {Type}";
    }

    readonly Border surface = new() { Padding = new Thickness(6) };
    readonly DockPanel root = new();
    readonly WrapPanel categories = new() { Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly ListBox list = new();
    readonly Image image = new() { Height = 170, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(4) };
    readonly Border previewFrame = new() { BorderThickness = new Thickness(1), Background = Brushes.White };
    readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
    readonly ItemsPanelTemplate listPanel;
    readonly ItemsPanelTemplate gridPanel = new(new FrameworkElementFactory(typeof(EquipmentTileGrid)));
    BuiltInCategory? selectedCategory;
    bool compactHeader;
    List<Item> items = [];
    string document = "";
    Point? dragStart;

    internal EquipmentPane()
    {
        BotPlusTheme.Apply(surface);
        surface.Themed(Border.BackgroundProperty, "BP.Bg");
        surface.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ViewResources));
        surface.Child = root;
        listPanel = list.ItemsPanel;
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => Describe();
        list.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem)
                Place();
        };
        list.PreviewMouseLeftButtonDown += (_, e) =>
            dragStart = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem
                ? e.GetPosition(list) : null;
        list.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
        list.PreviewMouseMove += (_, e) => Drag(e);

        var top = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        void AddAction(string label, string icon, Action action, string tip)
        {
            var button = new Button
            {
                Content = new HeaderIcon(label, icon, compactHeader), ToolTip = tip,
                Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 3)
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action();
            bar.Children.Add(button);
        }
        AddAction("Разместить", "M8,2 V14 M2,8 H14 M5,5 H11 V11 H5 Z", Place,
            "Разместить выбранный тип (или перетащите его на вид)");
        AddAction("Загрузить…", "M2,8 V14 H14 V8 M8,2 V10 M5,7 L8,10 L11,7", Load,
            "Загрузить семейство .rfa в проект");
        AddAction("Обновить", "M13,6 A5,5 0 1 0 13,10 M13,2 V6 H9", () =>
            App.Queue?.Post(app => { if (app.ActiveUIDocument is { } ui) Refresh(ui.Document); }),
            "Перечитать семейства активного проекта");
        AddOption(bar, "Сетка", CategoryIcon(null), true, () => SetGridView(true));
        AddOption(bar, "Список", "M2,3 H14 M2,8 H14 M2,13 H14", false, () => SetGridView(false));
        root.SizeChanged += (_, e) =>
        {
            const double fullHeaderWidth = 520;
            bool compact = e.NewSize.Width < fullHeaderWidth;
            if (compact == compactHeader) return;
            compactHeader = compact;
            foreach (var button in bar.Children.OfType<ButtonBase>().Concat(categories.Children.OfType<ButtonBase>()))
                if (button.Content is HeaderIcon icon) icon.SetCompact(compact);
        };
        top.Children.Add(bar);
        top.Children.Add(new TextBlock { Text = "Категория", Opacity = 0.7 }); top.Children.Add(categories);
        BuildCategoryButtons();
        top.Children.Add(new TextBlock { Text = "Поиск", Opacity = 0.7 }); top.Children.Add(search);

        var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        previewFrame.Child = image;
        previewFrame.Themed(Border.BorderBrushProperty, "BP.Border");
        bottom.Children.Add(previewFrame); bottom.Children.Add(caption); bottom.Children.Add(status);
        status.Text = "Перетащите тип на план — начнётся размещение. Двойной щелчок — то же самое. Esc завершает размещение.";

        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(list);
        SetGridView(true);
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = surface;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        data.VisibleByDefault = false;
    }

    /// <summary>Перечитывает типы активного проекта. Вызывать в контексте Revit API.</summary>
    internal void Refresh(Document doc)
    {
        document = doc.PathName + "|" + doc.Title;
        items = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category != null && Network.DeviceCategories.Contains(s.Category.BuiltInCategory))
            .Select(s => new Item(s.Id, s.Category.BuiltInCategory, s.Category.Name, s.FamilyName, s.Name)
            {
                // Панель немодальная, поэтому эскизы получаем здесь, в контексте Revit API.
                Preview = Preview(s)
            })
            .OrderBy(i => i.Category).ThenBy(i => i.Family).ThenBy(i => i.Type).ToList();
        if (!items.Any(i => i.CategoryId == selectedCategory)) selectedCategory = null;
        BuildCategoryButtons();
        Filter();
        if (items.Count == 0) caption.Text = "В проекте нет семейств ЭОМ. Загрузите .rfa — семейство должно содержать 3D-геометрию и 2D-УГО по ГОСТ для низкой детализации.";
    }

    /// <summary>Обновляет панель, если активным стал другой проект. Вызывается из ViewActivated (контекст API).</summary>
    internal void Follow(Document doc) { if (doc.PathName + "|" + doc.Title != document && !doc.IsFamilyDocument) Refresh(doc); }

    void BuildCategoryButtons()
    {
        categories.Children.Clear();
        AddCategory(null, "Все категории");
        foreach (var item in items.DistinctBy(i => i.CategoryId))
            AddCategory(item.CategoryId, item.Category);

        void AddCategory(BuiltInCategory? id, string name) =>
            AddOption(categories, name, CategoryIcon(id), selectedCategory == id, () =>
            {
                selectedCategory = id;
                Filter();
            });
    }

    void AddOption(Panel group, string label, string icon, bool selected, Action select)
    {
        var button = new RadioButton
        {
            Style = (Style)surface.Resources["OptionButton"], Content = new HeaderIcon(label, icon, compactHeader),
            IsChecked = selected, ToolTip = label
        };
        AutomationProperties.SetName(button, label);
        button.Checked += (_, _) => select();
        group.Children.Add(button);
    }

    sealed class HeaderIcon : StackPanel
    {
        readonly TextBlock text;

        internal HeaderIcon(string label, string icon, bool compact)
        {
            Orientation = Orientation.Horizontal;
            var mark = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(icon), Stretch = Stretch.None, StrokeThickness = 1.3,
                Width = 16, Height = 16
            };
            mark.Themed(System.Windows.Shapes.Path.StrokeProperty, "BP.Text");
            text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            Children.Add(mark);
            Children.Add(text);
            SetCompact(compact);
        }

        internal void SetCompact(bool compact) => text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    static string CategoryIcon(BuiltInCategory? category) => category switch
    {
        null => "M2,2 H6 V6 H2 Z M10,2 H14 V6 H10 Z M2,10 H6 V14 H2 Z M10,10 H14 V14 H10 Z",
        BuiltInCategory.OST_LightingFixtures => "M8,2 A6,6 0 1 1 7.99,2 Z M4,4 L12,12 M12,4 L4,12",
        BuiltInCategory.OST_LightingDevices => "M1,11 H4 M4,11 L12,4 M12,11 H15",
        BuiltInCategory.OST_ElectricalFixtures => "M2,12 A6,6 0 0 1 14,12 Z M8,6 V2",
        BuiltInCategory.OST_ElectricalEquipment => "M2,3 H14 V13 H2 Z M2,13 L14,3",
        BuiltInCategory.OST_CommunicationDevices => "M2,3 L5,2 L7,5 L5,7 Q7,10 10,11 L12,9 L15,11 L14,14 Q4,14 2,3 Z",
        BuiltInCategory.OST_DataDevices => "M2,2 H14 V11 H2 Z M8,11 V14 M5,14 H11",
        BuiltInCategory.OST_FireAlarmDevices => "M8,1 Q7,5 4,8 A4.5,4.5 0 1 0 12,8 Q11,6 10,5 L9,8 Q9,4 8,1 Z",
        BuiltInCategory.OST_SecurityDevices => "M8,2 L14,4 V8 Q13,12 8,14 Q3,12 2,8 V4 Z",
        _ => "M8,2 A6,6 0 1 1 7.99,2 Z M5,11 V5 L8,8 L11,5 V11"
    };

    void SetGridView(bool grid)
    {
        list.ItemTemplate = grid ? (DataTemplate)surface.Resources["EquipmentTile"] : null;
        list.ItemsPanel = grid ? gridPanel : listPanel;
        previewFrame.Visibility = status.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        if (grid)
        {
            list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            // Сетка получает ширину области прокрутки с учётом полосы прокрутки.
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(list, false);
        }
        else
        {
            list.ClearValue(ListBox.HorizontalContentAlignmentProperty);
            list.ClearValue(ScrollViewer.HorizontalScrollBarVisibilityProperty);
            list.ClearValue(ScrollViewer.CanContentScrollProperty);
        }
        if (list.SelectedItem is Item item) list.ScrollIntoView(item);
    }

    void Filter()
    {
        string q = search.Text.Trim();
        list.ItemsSource = items.Where(i => (selectedCategory == null || i.CategoryId == selectedCategory)
            && (q.Length == 0 || i.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (list.Items.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0;
    }

    void Describe()
    {
        if (list.SelectedItem is not Item i) { image.Source = null; caption.Text = ""; return; }
        image.Source = i.Preview;
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
        var target = new DropData(document, i.Id);
        App.Queue?.Post(app => { if (app.ActiveUIDocument is { } ui) PlaceInProject(ui, target, false); });
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
            try { PlaceInProject(ui, d, true); }
            catch (Exception error) { TaskDialog.Show("BotPlus ЭОМ", error.Message); }
        }
    }

    static void PlaceInProject(UIDocument ui, DropData target, bool fromDrop)
    {
        if (ui.Document.PathName + "|" + ui.Document.Title != target.Document)
        {
            TaskDialog.Show("BotPlus ЭОМ", "Активный проект сменился. Выберите тип в Базе УГО заново.");
            return;
        }
        EquipmentPlacement.Run(ui, target.Symbol, fromDrop);
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

    const string ViewResources = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="OptionButton" TargetType="RadioButton">
            <Setter Property="Margin" Value="0,0,4,3"/>
            <Setter Property="Padding" Value="7,3"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                  <Border x:Name="Frame" Padding="{TemplateBinding Padding}" CornerRadius="2" BorderThickness="1"
                          Background="{DynamicResource BP.Button}" BorderBrush="{DynamicResource BP.Border}">
                    <ContentPresenter VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Frame" Property="Background" Value="{DynamicResource BP.Hover}"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Frame" Property="Background" Value="{DynamicResource BP.Selected}"/>
                      <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <DataTemplate x:Key="EquipmentTile">
            <Border Margin="2" Padding="5" BorderThickness="1" CornerRadius="2"
                    BorderBrush="{DynamicResource BP.Border}" ToolTip="{Binding}">
              <StackPanel>
                <Border x:Name="PreviewFrame" Height="64" Background="White" Padding="3">
                  <Grid>
                    <Image Source="{Binding Preview}" Stretch="Uniform"/>
                    <TextBlock x:Name="PreviewPlaceholder" Text="Нет эскиза" Visibility="Collapsed"
                               HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{DynamicResource BP.Muted}"/>
                  </Grid>
                </Border>
                <TextBlock Text="{Binding Type}" TextTrimming="CharacterEllipsis" FontWeight="SemiBold" Margin="0,3,0,0"/>
                <TextBlock Text="{Binding Family}" TextTrimming="CharacterEllipsis" Foreground="{DynamicResource BP.Muted}"/>
              </StackPanel>
            </Border>
            <DataTemplate.Triggers>
              <DataTrigger Binding="{Binding Preview}" Value="{x:Null}">
                <Setter TargetName="PreviewFrame" Property="Background" Value="{DynamicResource BP.Panel}"/>
                <Setter TargetName="PreviewPlaceholder" Property="Visibility" Value="Visible"/>
              </DataTrigger>
            </DataTemplate.Triggers>
          </DataTemplate>
        </ResourceDictionary>
        """;

    sealed class EquipmentTileGrid : UniformGrid
    {
        const double TargetTileWidth = 122;

        public EquipmentTileGrid() => VerticalAlignment = VerticalAlignment.Top;

        protected override Size MeasureOverride(Size availableSize)
        {
            int columns = double.IsFinite(availableSize.Width) ? Math.Max(1, (int)(availableSize.Width / TargetTileWidth)) : 1;
            if (Columns != columns) Columns = columns;
            return base.MeasureOverride(availableSize);
        }
    }
}

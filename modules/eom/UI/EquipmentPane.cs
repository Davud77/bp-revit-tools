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
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using Point = System.Windows.Point;
using Visibility = System.Windows.Visibility;

namespace BotPlus.Eom;

/// <summary>
/// Закрепляемая панель «BotPlus База УГО»: типы из проекта и семейства из пользовательской библиотеки.
/// Тип можно перетащить мышью на вид Revit — сразу начнётся его размещение (UIApplication.DoDragDrop + IDropHandler),
/// либо дважды щёлкнуть / нажать «Разместить».
/// </summary>
internal sealed class EquipmentPane : IDockablePaneProvider
{
    internal sealed record Item(ElementId Id, BuiltInCategory CategoryId, string Category, string Family, string Type)
        : System.ComponentModel.INotifyPropertyChanged
    {
        public BitmapSource? Preview { get; private set; }
        public Geometry? Icon => LibraryPath == null ? null : Geometry.Parse(CategoryIcon(CategoryId));
        public string? LibraryPath { get; init; }
        internal string? FileName { get; init; }
        internal IndexedFamily? LibraryEntry { get; init; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        internal Item SetPreview(BitmapSource? preview)
        {
            Preview = preview;
            PropertyChanged?.Invoke(this, new(nameof(Preview)));
            return this;
        }

        public override string ToString() => LibraryPath == null ? $"{Family}: {Type}" : $"{Family} — {Category}";
    }

    const int PageSize = 96;
    const string LibraryLabel = "Из папки";
    const string PlaceIcon = "M8,2 V14 M2,8 H14 M5,5 H11 V11 H5 Z";
    const string LoadIcon = "M2,8 V14 H14 V8 M8,2 V10 M5,7 L8,10 L11,7";
    readonly Border surface = new() { Padding = new Thickness(6) };
    readonly DockPanel root = new();
    readonly WrapPanel categories = new() { Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly ListBox list = new();
    readonly Image image = new() { Height = 170, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(4) };
    readonly System.Windows.Shapes.Path previewIcon = new()
    {
        Width = 48, Height = 48, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    readonly Border previewFrame = new() { BorderThickness = new Thickness(1), Background = Brushes.White };
    readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
    readonly ItemsPanelTemplate listPanel;
    readonly ItemsPanelTemplate gridPanel = new(new FrameworkElementFactory(typeof(EquipmentTileGrid)));
    readonly ToggleButton loadedOnly = new() { IsChecked = true };
    readonly StackPanel pagination = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Button previousPage = new() { Content = "‹", MinWidth = 30, ToolTip = "Предыдущая страница" };
    readonly Button nextPage = new() { Content = "›", MinWidth = 30, ToolTip = "Следующая страница" };
    readonly TextBlock pageRange = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly Button place;
    readonly EquipmentLibraryIndexer indexer;
    HashSet<BuiltInCategory> shownCategories = EquipmentLibrary.LoadCategories()?.Select(id => (BuiltInCategory)id).ToHashSet()
        ?? Network.DeviceCategories.ToHashSet();
    EquipmentLibraryWindow? settingsWindow;
    BuiltInCategory? selectedCategory;
    bool compactHeader;
    List<Item> loadedItems = [];
    List<Item> visibleItems = [];
    List<Item>? folderItems;
    List<Item> matches = [];
    int page;
    CancellationTokenSource? pagePreviews;
    Dictionary<BuiltInCategory, string> projectCategories = [];
    EquipmentLibrary library = new("", []);
    string document = "";
    Point? dragStart;

    internal EquipmentPane(IntPtr owner)
    {
        indexer = new EquipmentLibraryIndexer(surface.Dispatcher);
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
        Button AddAction(string label, string icon, Action action, string tip)
        {
            var button = new Button
            {
                Content = new HeaderIcon(label, icon, compactHeader), ToolTip = tip,
                Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 3)
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action();
            bar.Children.Add(button);
            return button;
        }
        place = AddAction("Разместить", PlaceIcon, Place,
            "Разместить выбранный тип (или перетащите его на вид)");
        place.IsEnabled = false;
        AddAction("Загрузить…", LoadIcon, Load,
            "Загрузить семейство .rfa в проект");
        AddAction("Обновить", "M13,6 A5,5 0 1 0 13,10 M13,2 V6 H9", () =>
        {
            ReloadLibrary();
            App.Queue?.Post(app => { if (app.ActiveUIDocument?.Document is { IsFamilyDocument: false } doc) Refresh(doc); });
        }, "Перечитать семейства проекта и готовый индекс");
        AddAction("Настройки", "M6,1 H10 L10.5,3.5 L12.8,3.4 L14.8,6.6 L13.3,8 L14.8,9.4 L12.8,12.6 L10.5,12.5 L10,15 H6 L5.5,12.5 L3.2,12.6 L1.2,9.4 L2.7,8 L1.2,6.6 L3.2,3.4 L5.5,3.5 Z M8,5.5 A2.5,2.5 0 1 1 7.99,5.5 Z",
            () => OpenSettings(owner),
            "Настройки Базы УГО: библиотека, категории и высоты вставки");
        loadedOnly.Style = (Style)surface.Resources["SourceToggle"];
        loadedOnly.Content = new HeaderIcon("Только загруженные", "M1,2 H15 L10,8 V13 L6,15 V8 Z M6,5 H10", compactHeader);
        loadedOnly.ToolTip = "Только загруженные. Снимите фильтр, чтобы также показать семейства из папки.";
        AutomationProperties.SetName(loadedOnly, "Только загруженные");
        loadedOnly.Click += (_, _) => RefreshSources();
        bar.Children.Add(loadedOnly);
        AddOption(bar, "Сетка", CategoryIcon(null), true, () => SetGridView(true));
        AddOption(bar, "Список", "M2,3 H14 M2,8 H14 M2,13 H14", false, () => SetGridView(false));
        root.SizeChanged += (_, e) =>
        {
            const double fullHeaderWidth = 700;
            bool compact = e.NewSize.Width < fullHeaderWidth;
            if (compact == compactHeader) return;
            compactHeader = compact;
            foreach (var button in bar.Children.OfType<ButtonBase>().Concat(categories.Children.OfType<ButtonBase>()))
                if (button.Content is HeaderIcon icon) icon.SetCompact(compact);
        };
        top.Children.Add(bar);
        top.Children.Add(new TextBlock { Text = "Категория", Opacity = 0.7 }); top.Children.Add(categories);
        top.Children.Add(new TextBlock { Text = "Поиск", Opacity = 0.7 }); top.Children.Add(search);
        var indexing = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.8, Visibility = Visibility.Collapsed };
        top.Children.Add(indexing);
        indexer.Changed += () =>
        {
            indexing.Text = indexer.Status;
            indexing.Visibility = indexer.Status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        indexer.Completed += indexed =>
        {
            library = indexed;
            folderItems = null;
            RefreshSources();
            settingsWindow?.SetCategories(AvailableCategories());
        };

        var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        previousPage.Click += (_, _) => ShowPage(page - 1);
        nextPage.Click += (_, _) => ShowPage(page + 1);
        AutomationProperties.SetName(previousPage, "Предыдущая страница");
        AutomationProperties.SetName(nextPage, "Следующая страница");
        pagination.Children.Add(previousPage);
        pagination.Children.Add(pageRange);
        pagination.Children.Add(nextPage);
        bottom.Children.Add(pagination);
        var preview = new System.Windows.Controls.Grid();
        preview.Children.Add(image);
        preview.Children.Add(previewIcon);
        previewIcon.Themed(System.Windows.Shapes.Path.StrokeProperty, "BP.Accent");
        previewFrame.Child = preview;
        previewFrame.Themed(Border.BorderBrushProperty, "BP.Border");
        bottom.Children.Add(previewFrame); bottom.Children.Add(caption); bottom.Children.Add(status);
        status.Text = "Перетащите тип на план — начнётся размещение. Двойной щелчок — то же самое. Esc завершает размещение.";

        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(list);
        SetGridView(true);
        RefreshSources();
        ReloadLibrary();
    }

    internal void ReloadLibrary() => indexer.Open(library);

    void OpenSettings(IntPtr owner)
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        App.Queue?.Post(app =>
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            var doc = app.ActiveUIDocument?.Document;
            if (doc?.IsFamilyDocument == true) doc = null;
            var labels = doc?.Settings.Categories.Cast<Category>()
                .Where(c => c.Parent == null && c.BuiltInCategory != BuiltInCategory.INVALID)
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(c => c.BuiltInCategory).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => (int)group.First().BuiltInCategory, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in Network.DeviceCategories.Concat(shownCategories).Distinct())
            {
                string label = LabelUtils.GetLabelFor(category);
                labels.TryAdd(label, (int)category);
                projectCategories.TryAdd(category, label);
            }
            var defaults = doc is { IsReadOnly: false } ? EomStore.Settings(doc) : null;
            var window = new EquipmentLibraryWindow(owner, library, indexer,
                folder => indexer.Start(folder, library, labels), folder => indexer.Open(library, folder),
                AvailableCategories(), shownCategories, selected =>
                {
                    EquipmentLibrary.SaveCategories(selected.Select(id => (int)id).Order().ToArray());
                    shownCategories = selected;
                    folderItems = null;
                    RefreshSources();
                    App.Queue?.Post(ui => { if (ui.ActiveUIDocument?.Document is { IsFamilyDocument: false } active) Refresh(active); });
                }, defaults, doc?.Title, (heights, report) =>
                {
                    App.Queue?.Post(ui =>
                    {
                        if (ui.ActiveUIDocument?.Document is not { IsReadOnly: false } active || !active.Equals(doc))
                        {
                            report("Активный проект сменился. Откройте настройки заново.");
                            return;
                        }
                        try
                        {
                            var current = EomStore.Settings(active) with
                            {
                                SwitchHeightMm = heights.SwitchHeightMm,
                                ElectricalFixtureHeightMm = heights.ElectricalFixtureHeightMm,
                                ElectricalEquipmentHeightMm = heights.ElectricalEquipmentHeightMm,
                                LightingFixtureHeightMm = heights.LightingFixtureHeightMm
                            };
                            using var transaction = new Transaction(active, "ЭОМ: высоты вставки из Базы УГО");
                            transaction.Start();
                            EomStore.SaveSettings(active, current);
                            report(transaction.Commit() == TransactionStatus.Committed
                                ? "Высоты сохранены в проекте." : "Revit не сохранил высоты.");
                        }
                        catch (Exception error) { report(error.Message); }
                    });
                });
            settingsWindow = window;
            window.Closed += (_, _) => settingsWindow = null;
            window.Show();
        });
    }

    Dictionary<BuiltInCategory, string> AvailableCategories()
    {
        var result = new Dictionary<BuiltInCategory, string>(projectCategories);
        foreach (var entry in library.Entries)
            result.TryAdd((BuiltInCategory)entry.Category, entry.CategoryName);
        foreach (var category in shownCategories)
            result.TryAdd(category, category.ToString());
        return result;
    }

    internal void Close()
    {
        settingsWindow?.Close();
        pagePreviews?.Cancel();
        pagePreviews?.Dispose();
        indexer.Dispose();
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
        string key = doc.PathName + "|" + doc.Title;
        if (key != document) list.SelectedItem = null;
        document = key;
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category is { BuiltInCategory: not BuiltInCategory.INVALID }).ToList();
        projectCategories = symbols.Select(s => s.Category).DistinctBy(c => c.BuiltInCategory)
            .ToDictionary(c => c.BuiltInCategory, c => c.Name);
        foreach (var category in Network.DeviceCategories)
            projectCategories.TryAdd(category, LabelUtils.GetLabelFor(category));
        loadedItems = symbols
            .Select(s => new Item(s.Id, s.Category.BuiltInCategory, s.Category.Name, s.FamilyName, s.Name)
                .SetPreview(shownCategories.Contains(s.Category.BuiltInCategory) ? Preview(s) : null))
            .OrderBy(i => i.Category).ThenBy(i => i.Family).ThenBy(i => i.Type).ToList();
        RefreshSources();
    }

    List<Item> CreateFolderItems() =>
        library.Entries.Where(entry => shownCategories.Contains((BuiltInCategory)entry.Category))
            .Select(entry => new Item(ElementId.InvalidElementId, (BuiltInCategory)entry.Category,
                projectCategories.GetValueOrDefault((BuiltInCategory)entry.Category, entry.CategoryName), entry.Name, LibraryLabel)
            {
                LibraryPath = Path.Combine(library.Folder, entry.File),
                FileName = Path.GetFileNameWithoutExtension(entry.File),
                LibraryEntry = entry
            }).ToList();

    void RefreshSources()
    {
        visibleItems = loadedItems.Where(item => shownCategories.Contains(item.CategoryId)).ToList();
        if (loadedOnly.IsChecked != true)
        {
            folderItems ??= CreateFolderItems();
            var loadedNames = loadedItems.GroupBy(i => i.CategoryId)
                .ToDictionary(group => group.Key, group => group.Select(i => i.Family).ToHashSet(StringComparer.OrdinalIgnoreCase));
            visibleItems.AddRange(folderItems.Where(item =>
                !loadedNames.TryGetValue(item.CategoryId, out var names) ||
                (!names.Contains(item.Family) && !names.Contains(item.FileName!))));
        }
        visibleItems = visibleItems.OrderBy(item => item.Category).ThenBy(item => item.Family).ThenBy(item => item.Type).ToList();
        if (!visibleItems.Any(i => i.CategoryId == selectedCategory)) selectedCategory = null;
        BuildCategoryButtons();
        Filter();
    }

    /// <summary>Обновляет панель, если активным стал другой проект. Вызывается из ViewActivated (контекст API).</summary>
    internal void Follow(Document doc) { if (doc.PathName + "|" + doc.Title != document && !doc.IsFamilyDocument) Refresh(doc); }

    void BuildCategoryButtons()
    {
        categories.Children.Clear();
        AddCategory(null, "Все категории");
        foreach (var item in visibleItems.DistinctBy(i => i.CategoryId))
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
        bool includesSeparator = q.Contains(':') || q.Contains('—');
        var selected = list.SelectedItem as Item;
        matches = visibleItems.Where(i => (selectedCategory == null || i.CategoryId == selectedCategory)
            && (q.Length == 0 || i.Family.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.FileName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true ||
                (i.LibraryPath == null ? i.Type : i.Category).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                includesSeparator && i.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        int selectedIndex = selected == null ? -1 : matches.FindIndex(i => i.Id == selected.Id && i.LibraryPath == selected.LibraryPath);
        ShowPage(Math.Max(0, selectedIndex) / PageSize, selectedIndex);
        if (matches.Count == 0)
            caption.Text = shownCategories.Count == 0 ? "Выберите категории в настройках Базы УГО."
                : q.Length > 0 ? "По запросу ничего не найдено." : "Список пуст. Загрузите .rfa или укажите папку в настройках.";
    }

    void ShowPage(int target, int selectedIndex = -1)
    {
        int lastPage = Math.Max(0, (matches.Count - 1) / PageSize);
        page = Math.Clamp(target, 0, lastPage);
        int start = page * PageSize;
        var entries = matches.Skip(start).Take(PageSize).ToArray();
        foreach (var old in list.Items.OfType<Item>())
            if (old.LibraryEntry != null && old.Preview != null) old.SetPreview(null);
        list.ItemsSource = entries;
        list.SelectedIndex = selectedIndex >= start && selectedIndex < start + entries.Length ? selectedIndex - start
            : entries.Length == 0 ? -1 : 0;
        if (list.SelectedItem is Item item) list.ScrollIntoView(item);
        pagination.Visibility = lastPage > 0 ? Visibility.Visible : Visibility.Collapsed;
        previousPage.IsEnabled = page > 0;
        nextPage.IsEnabled = page < lastPage;
        pageRange.Text = $"{start + 1}–{start + entries.Length} из {matches.Count}";
        LoadPagePreviews(entries);
    }

    async void LoadPagePreviews(Item[] entries)
    {
        pagePreviews?.Cancel();
        pagePreviews?.Dispose();
        pagePreviews = null;
        var pending = entries.Where(item => item.LibraryEntry?.HasPreview == true).ToArray();
        if (pending.Length == 0) return;
        var index = library.Index;
        pagePreviews = new CancellationTokenSource();
        var token = pagePreviews.Token;
        try
        {
            var previews = await Task.Run(() =>
            {
                var result = new List<(Item Item, BitmapSource? Preview)>(pending.Length);
                using var stream = index?.OpenPreviews();
                foreach (var item in pending)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = item.LibraryEntry!;
                    byte[]? bytes = entry.Preview ?? (stream != null ? index!.ReadPreview(stream, entry) : null);
                    result.Add((item, DecodePreview(bytes)));
                }
                return result;
            }, token).ConfigureAwait(false);
            await root.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                foreach (var (item, preview) in previews) item.SetPreview(preview);
                if (list.SelectedItem is Item { LibraryEntry: not null } selected)
                {
                    image.Source = selected.Preview;
                    previewIcon.Data = selected.Preview == null ? selected.Icon : null;
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
    }

    void Describe()
    {
        var libraryPath = (list.SelectedItem as Item)?.LibraryPath;
        string action = libraryPath == null ? "Разместить" : "Загрузить в проект";
        place.Content = new HeaderIcon(action, libraryPath == null ? PlaceIcon : LoadIcon, compactHeader);
        place.ToolTip = libraryPath == null ? "Разместить выбранный тип (или перетащите его на вид)" : action;
        AutomationProperties.SetName(place, action);
        place.IsEnabled = list.SelectedItem != null;
        previewIcon.Data = null;
        if (list.SelectedItem is not Item i) { image.Source = null; caption.Text = ""; return; }
        image.Source = i.Preview;
        if (libraryPath != null)
        {
            if (i.Preview == null) previewIcon.Data = i.Icon;
            caption.Text = $"{i.Family}\n{i.Category}\n{libraryPath}\nЗагрузите семейство, чтобы выбрать тип для размещения.";
            return;
        }
        caption.Text = string.Join("\n", i.Category, "Семейство: " + i.Family, "Тип: " + i.Type) + (image.Source == null ? "\nУ семейства нет сохранённого эскиза." : "");
        string key = document;
        App.Queue?.Post(app =>
        {
            if (app.ActiveUIDocument?.Document is not { IsFamilyDocument: false } doc ||
                doc.PathName + "|" + doc.Title != key || doc.GetElement(i.Id) is not FamilySymbol symbol) return;
            int count = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new FamilyInstanceFilter(doc, i.Id)).GetElementCount();
            string text = $"\nРазмещение: {Placement(symbol.Family.FamilyPlacementType)}\nВ проекте: {count} шт.";
            root.Dispatcher.Invoke(() => { if (Equals(list.SelectedItem, i)) caption.Text += text; });
        });
    }

    void Place()
    {
        if (list.SelectedItem is not Item i) return;
        if (i.LibraryPath is { } file)
        {
            string key = document;
            App.Queue?.Post(app => LoadIndexedFamily(app, key, file));
            return;
        }
        var target = new DropData(document, i.Id);
        App.Queue?.Post(app => { if (app.ActiveUIDocument is { } ui) PlaceInProject(ui, target, false); });
    }

    void LoadIndexedFamily(UIApplication app, string key, string file)
    {
        if (app.ActiveUIDocument?.Document is not { IsFamilyDocument: false } doc || doc.PathName + "|" + doc.Title != key)
        {
            TaskDialog.Show("BotPlus ЭОМ", "Откройте проект и выберите семейство в Базе УГО заново.");
            return;
        }
        if (!File.Exists(file))
        {
            TaskDialog.Show("BotPlus ЭОМ", $"Файл недоступен. Проверьте папку и повторите индексацию.\n{file}");
            return;
        }
        ElementId familyId;
        using (var transaction = new Transaction(doc, "ЭОМ: загрузка семейства из Базы УГО"))
        {
            transaction.Start();
            if (!doc.LoadFamily(file, out var family) || family == null)
            {
                transaction.RollBack();
                TaskDialog.Show("BotPlus ЭОМ", "Семейство уже есть в проекте или Revit не смог его загрузить.");
                return;
            }
            if (family.FamilyCategory is not { } category || !shownCategories.Contains(category.BuiltInCategory))
            {
                transaction.RollBack();
                TaskDialog.Show("BotPlus ЭОМ", "Включите категорию семейства в настройках Базы УГО.");
                return;
            }
            familyId = family.Id;
            if (transaction.Commit() != TransactionStatus.Committed) return;
        }
        var symbols = ((Family)doc.GetElement(familyId)).GetFamilySymbolIds();
        selectedCategory = null;
        Refresh(doc);
        int index = matches.FindIndex(i => symbols.Contains(i.Id));
        if (index >= 0) ShowPage(index / PageSize, index);
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
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || list.SelectedItem is not Item { LibraryPath: null } i) return;
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

    internal static BitmapSource? Preview(ElementType type, int size = 200)
    {
        try
        {
            using var bitmap = type.GetPreviewImage(new System.Drawing.Size(size, size));
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

    static BitmapSource? DecodePreview(byte[]? png)
    {
        if (png == null) return null;
        try
        {
            using var stream = new MemoryStream(png);
            var preview = new BitmapImage();
            preview.BeginInit();
            preview.CacheOption = BitmapCacheOption.OnLoad;
            preview.DecodePixelWidth = 160;
            preview.StreamSource = stream;
            preview.EndInit();
            preview.Freeze();
            return preview;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or FormatException) { return null; }
    }

    const string ViewResources = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="SourceToggle" TargetType="ToggleButton">
            <Setter Property="Margin" Value="0,0,4,3"/>
            <Setter Property="Padding" Value="7,3"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ToggleButton">
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
          <Style x:Key="OptionButton" TargetType="RadioButton" BasedOn="{StaticResource SourceToggle}"/>
          <DataTemplate x:Key="EquipmentTile">
            <Border x:Name="Tile" Margin="2" Padding="5" BorderThickness="1" CornerRadius="2"
                    BorderBrush="{DynamicResource BP.Border}" ToolTip="{Binding}">
              <StackPanel>
                <Border x:Name="PreviewFrame" Height="64" Background="White" Padding="3">
                  <Grid>
                    <Image Source="{Binding Preview}" Stretch="Uniform"/>
                    <Path x:Name="CategoryMark" Data="{Binding Icon}" Visibility="Collapsed" Width="28" Height="28"
                          Stretch="Uniform" HorizontalAlignment="Center" VerticalAlignment="Center"
                          Stroke="{DynamicResource BP.Text}" StrokeThickness="1.5"/>
                    <TextBlock x:Name="PreviewPlaceholder" Text="Нет эскиза" Visibility="Collapsed"
                               HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{DynamicResource BP.Muted}"/>
                  </Grid>
                </Border>
                <TextBlock x:Name="TileTitle" Text="{Binding Type}" TextTrimming="CharacterEllipsis" FontWeight="SemiBold" Margin="0,3,0,0"/>
                <TextBlock x:Name="TileSubtitle" Text="{Binding Family}" TextTrimming="CharacterEllipsis" Foreground="{DynamicResource BP.Muted}"/>
              </StackPanel>
            </Border>
            <DataTemplate.Triggers>
              <DataTrigger Binding="{Binding Preview}" Value="{x:Null}">
                <Setter TargetName="PreviewFrame" Property="Background" Value="{DynamicResource BP.Panel}"/>
                <Setter TargetName="PreviewPlaceholder" Property="Visibility" Value="Visible"/>
              </DataTrigger>
              <DataTrigger Binding="{Binding Type}" Value="Из папки">
                <Setter TargetName="Tile" Property="ToolTip" Value="{Binding LibraryPath}"/>
                <Setter TargetName="TileTitle" Property="Text" Value="{Binding Family}"/>
                <Setter TargetName="TileSubtitle" Property="Text" Value="{Binding Category}"/>
              </DataTrigger>
              <MultiDataTrigger>
                <MultiDataTrigger.Conditions>
                  <Condition Binding="{Binding Type}" Value="Из папки"/>
                  <Condition Binding="{Binding Preview}" Value="{x:Null}"/>
                </MultiDataTrigger.Conditions>
                <Setter TargetName="PreviewPlaceholder" Property="Visibility" Value="Collapsed"/>
                <Setter TargetName="CategoryMark" Property="Visibility" Value="Visible"/>
              </MultiDataTrigger>
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

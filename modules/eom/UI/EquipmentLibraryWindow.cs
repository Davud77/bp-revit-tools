using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Microsoft.Win32;

namespace BotPlus.Eom;

internal sealed class EquipmentLibraryWindow : Window
{
    readonly StackPanel categoryChoices = new();
    readonly Dictionary<BuiltInCategory, CheckBox> checks = [];

    internal EquipmentLibraryWindow(IntPtr owner, EquipmentLibrary library, EquipmentLibraryIndexer indexer,
        Action<string> startIndex, Action<string> openIndex,
        IReadOnlyDictionary<BuiltInCategory, string> available, HashSet<BuiltInCategory> selected, Action<HashSet<BuiltInCategory>> saveCategories,
        EomSettings? heights, string? projectTitle, Action<EomSettings, Action<string>> saveHeights)
    {
        Title = "Настройки Базы УГО";
        Width = 580;
        Height = 430;
        MinWidth = 480;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = owner;
        BotPlusTheme.Apply(this);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Библиотека", Content = BuildLibraryTab(library, indexer, startIndex, openIndex) });
        tabs.Items.Add(new TabItem { Header = "Категории", Content = BuildCategoriesTab(saveCategories) });
        tabs.Items.Add(new TabItem { Header = "Высоты вставки", Content = BuildHeightsTab(heights, projectTitle, saveHeights) });
        AddCategories(available, selected);
        EomUi.Layout(this, tabs, null, EomUi.Buttons(EomUi.Button("Закрыть", Close)));
    }

    FrameworkElement BuildLibraryTab(EquipmentLibrary library, EquipmentLibraryIndexer indexer, Action<string> startIndex, Action<string> openIndex)
    {
        var content = new StackPanel { Margin = new Thickness(12) };
        content.Children.Add(new TextBlock { Text = "Папка с семействами Revit (.rfa)" });
        var folderRow = new DockPanel { Margin = new Thickness(0, 5, 0, 10) };
        content.Children.Add(folderRow);
        var folder = new TextBox { Text = (indexer.IsRunning ? indexer.Folder : null) ?? library.Folder, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(folder, "Папка с семействами");
        var browse = new Button { Content = "Выбрать…", Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Выберите папку с семействами" };
            if (Directory.Exists(folder.Text)) dialog.InitialDirectory = folder.Text;
            if (dialog.ShowDialog(this) == true) folder.Text = dialog.FolderName;
        };

        var result = EomUi.Hint(library.Folder.Length == 0 ? "Выберите папку с семействами."
            : $"Семейств в индексе: {library.Entries.Length}.");
        content.Children.Add(result);
        var index = EomUi.Button("Индексация", () => startIndex(folder.Text.Trim()));
        var open = EomUi.Button("Открыть готовый индекс", () => openIndex(folder.Text.Trim()));
        content.Children.Add(EomUi.Buttons(open, index));
        content.Children.Add(EomUi.Hint("Для переноса на другой ПК скопируйте BotPlus.UGO.index в корень такой же библиотеки."));
        void Refresh()
        {
            folderRow.IsEnabled = !indexer.IsRunning;
            open.IsEnabled = index.IsEnabled = !indexer.IsRunning && !string.IsNullOrWhiteSpace(folder.Text);
            if (indexer.Status.Length > 0) result.Text = indexer.Status;
        }
        folder.TextChanged += (_, _) =>
        {
            Refresh();
            result.Text = "Откройте готовый индекс или создайте его для выбранной папки.";
        };
        indexer.Changed += Refresh;
        void SetFolder(EquipmentLibrary updated) => folder.Text = updated.Folder;
        indexer.Completed += SetFolder;
        Closed += (_, _) =>
        {
            indexer.Changed -= Refresh;
            indexer.Completed -= SetFolder;
        };
        Refresh();
        return content;
    }

    FrameworkElement BuildCategoriesTab(Action<HashSet<BuiltInCategory>> save)
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var hint = EomUi.Hint("Показывать только отмеченные категории в проекте и библиотеке.");
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        var result = EomUi.Hint("");
        var controls = new StackPanel();
        controls.Children.Add(result);
        controls.Children.Add(EomUi.Buttons(
            EomUi.Button("Выбрать все", () => SetAll(true)),
            EomUi.Button("Снять все", () => SetAll(false)),
            EomUi.Button("Сохранить категории", () =>
            {
                save(checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToHashSet());
                result.Text = "Категории сохранены.";
            })));
        DockPanel.SetDock(controls, Dock.Bottom);
        panel.Children.Add(controls);
        panel.Children.Add(new ScrollViewer { Content = categoryChoices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return panel;

        void SetAll(bool value)
        {
            foreach (var check in checks.Values) check.IsChecked = value;
        }
    }

    internal void SetCategories(IReadOnlyDictionary<BuiltInCategory, string> categories) =>
        AddCategories(categories, checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToHashSet());

    void AddCategories(IReadOnlyDictionary<BuiltInCategory, string> categories, HashSet<BuiltInCategory> selected)
    {
        checks.Clear();
        categoryChoices.Children.Clear();
        foreach (var pair in categories.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var check = new CheckBox
            {
                Content = new EquipmentPane.HeaderIcon(pair.Value, EquipmentCategoryIcons.Get(pair.Key, pair.Value), false),
                IsChecked = selected.Contains(pair.Key), Margin = new Thickness(2, 2, 0, 2)
            };
            AutomationProperties.SetName(check, pair.Value);
            checks.Add(pair.Key, check);
            categoryChoices.Children.Add(check);
        }
    }

    static FrameworkElement BuildHeightsTab(EomSettings? current, string? projectTitle, Action<EomSettings, Action<string>> save)
    {
        var content = new StackPanel { Margin = new Thickness(12) };
        if (current == null)
        {
            content.Children.Add(EomUi.Hint("Откройте доступный для записи проект Revit, чтобы задать высоты вставки."));
            return content;
        }

        content.Children.Add(EomUi.Heading(projectTitle ?? "Активный проект"));
        content.Children.Add(EomUi.Hint("Высоты по умолчанию от уровня активного вида до точки вставки."));
        var switches = EomUi.Number(current.SwitchHeightMm);
        var fixtures = EomUi.Number(current.ElectricalFixtureHeightMm);
        var equipment = EomUi.Number(current.ElectricalEquipmentHeightMm);
        var lights = EomUi.Number(current.LightingFixtureHeightMm);
        content.Children.Add(EomUi.Row("Выключатели", switches, "мм"));
        content.Children.Add(EomUi.Row("Электрические приборы", fixtures, "мм"));
        content.Children.Add(EomUi.Row("Электрооборудование", equipment, "мм"));
        content.Children.Add(EomUi.Row("Осветительные приборы", lights, "мм"));
        var result = EomUi.Hint("");
        var saveButton = EomUi.Button("Сохранить высоты", () =>
        {
            var settings = current with
            {
                SwitchHeightMm = EomSettings.ValidateHeight(EomUi.Parse(switches, "Высота выключателей")),
                ElectricalFixtureHeightMm = EomSettings.ValidateHeight(EomUi.Parse(fixtures, "Высота электрических приборов")),
                ElectricalEquipmentHeightMm = EomSettings.ValidateHeight(EomUi.Parse(equipment, "Высота электрооборудования")),
                LightingFixtureHeightMm = EomSettings.ValidateHeight(EomUi.Parse(lights, "Высота осветительных приборов"))
            };
            save(settings, message => result.Text = message);
        });
        content.Children.Add(EomUi.Buttons(EomUi.Button("По умолчанию", () =>
        {
            var defaults = new EomSettings();
            switches.Text = defaults.SwitchHeightMm.ToString();
            fixtures.Text = defaults.ElectricalFixtureHeightMm.ToString();
            equipment.Text = defaults.ElectricalEquipmentHeightMm.ToString();
            lights.Text = defaults.LightingFixtureHeightMm.ToString();
        }), saveButton));
        content.Children.Add(result);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BotPlus.Shared;
using NativeRibbon = Autodesk.Windows;

namespace BotPlus.RT;

internal sealed class RibbonNavigation : IExternalEventHandler, IDisposable
{
    internal const string Tab = "BotPlus RT";
    const string Favorites = "Избранное";
    const string Main = "Основная";
    const string FavoritesButton = "BotPlusRTFavorites";
    const int ActionWidth = 34;
    const int RowWidth = 4 * ActionWidth;
    static readonly string[] Sections = [Favorites, Main, "ВК", "ОВ", "ЭОМ", "СС"];
    // RibbonItem.Name is the add-in's local name; PostCommand needs the ID assigned by Revit.
    static readonly MethodInfo? GetNativeItem = typeof(RibbonItem).GetMethod("getRibbonItem", BindingFlags.Instance | BindingFlags.NonPublic);
    static string FavoritesPath => Path.Combine(Path.GetDirectoryName(BotPlusSettings.FilePath)!, "favorites.json");

    readonly RibbonPanel servicePanel;
    readonly RibbonPanel favoritesPanel;
    readonly RibbonItem[] serviceControls;
    readonly ComboBox section;
    readonly TextBox search;
    readonly ExternalEvent searchEvent;
    readonly HashSet<string> captured = ["BotPlus", Favorites];
    readonly List<(RibbonPanel Panel, string Section, string Module)> panels = [];
    readonly List<RibbonTool> tools = [];
    readonly Dictionary<string, PushButton> favoriteButtons = [];
    HashSet<string> favorites = [];

    internal static RibbonNavigation? Current { get; private set; }

    internal RibbonNavigation(UIControlledApplication app)
    {
        servicePanel = app.CreateRibbonPanel(Tab, "BotPlus");
        favoritesPanel = app.CreateRibbonPanel(Tab, Favorites);
        var actions = servicePanel.AddStackedItems(
            HostButton<SettingsCommand>("BotPlusRTSettings", "Настройки", "Тема окон, модули и сведения о плагине."),
            HostButton<UpdateCommand>("BotPlusRTUpdates", "Обновления", "Проверить наличие новой версии BotPlus RT."),
            HostButton<FeedbackCommand>("BotPlusRTFeedback", "Обратная связь", "Открыть беседу BotPlus в Telegram."));
        var controls = servicePanel.AddStackedItems(
            HostButton<FavoritesCommand>(FavoritesButton, Favorites, "Открыть раздел и выбрать инструменты для избранного."),
            new ComboBoxData("BotPlusRTSection"),
            new TextBoxData("BotPlusRTSearch"));
        serviceControls = [.. actions, .. controls];
        section = (ComboBox)controls[1];
        search = (TextBox)controls[2];
        section.ToolTip = "Раздел инструментов BotPlus RT";
        foreach (var name in Sections)
            section.AddItem(new ComboBoxMemberData(name, name));
        section.Current = section.GetItems().Single(item => item.Name == Main);
        section.CurrentChanged += OnSectionChanged;
        search.Width = RowWidth;
        search.PromptText = "Поиск команд";
        search.ToolTip = "Введите название или описание инструмента и нажмите Enter.";
        search.Image = RibbonStyle.Icon(search.Name, 16);
        search.ShowImageAsButton = true;
        search.SelectTextOnFocus = true;
        searchEvent = ExternalEvent.Create(this);
        search.EnterPressed += OnSearch;
        Current = this;
    }

    static PushButtonData HostButton<T>(string id, string text, string tip) =>
        new(id, text, typeof(T).Assembly.Location, typeof(T).FullName!)
        {
            ToolTip = tip,
            AvailabilityClassName = typeof(HostAvailable).FullName!
        };

    internal void CapturePanels(UIControlledApplication app, string? module = null)
    {
        foreach (var panel in app.GetRibbonPanels(Tab).Where(panel => captured.Add(panel.Name)))
        {
            string destination = module == "ЭОМ" ? "ЭОМ" : panel.Name == "ОВ" ? "ОВ" : Main;
            panels.Add((panel, destination, module ?? panel.Name));
        }
    }

    internal void Complete()
    {
        RibbonStyle.Apply(panels.Select(entry => entry.Panel).Prepend(servicePanel));
        foreach (var button in Buttons(servicePanel).Where(button => button.Name != FavoritesButton))
            tools.Add(RibbonTool.FromButton(button, servicePanel.Name));
        foreach (var (panel, destination, module) in panels)
            foreach (var button in Buttons(panel))
                tools.Add(RibbonTool.FromButton(button, panel.Name, destination, module));

        // «Открыть вид» также доступна в поиске и избранном, а её штатная кнопка контекстная.
        if (panels.Any(entry => entry.Module == "Виды"))
        {
            const string name = ViewportContext.ButtonName;
            tools.Add(new RibbonTool(name, "Открыть вид", "Виды", Main, "Виды",
                "Открыть вид выбранного видового экрана, разреза, фасада или выноски.",
                typeof(OpenViewCommand).Assembly.Location, typeof(OpenViewCommand).FullName!,
                typeof(OpenViewAvailable).FullName!, RibbonStyle.Icon(name, 16), RibbonStyle.Icon(name, 32), null));
        }

        foreach (var tool in tools)
        {
            var button = (PushButton)favoritesPanel.AddItem(tool.FavoriteButton());
            button.Visible = false;
            favoriteButtons.Add(tool.Id, button);
        }
        favorites = LoadFavorites();
        ArrangeServicePanel();
        RefreshVisibility();
    }

    void ArrangeServicePanel()
    {
        // Revit's public API creates stacks of up to three controls. Regroup the
        // registered controls on the native panel to keep their Revit commands and events.
        var panel = NativeRibbon.ComponentManager.Ribbon?.Tabs.FirstOrDefault(tab => tab.Title == Tab)?
            .Panels.FirstOrDefault(candidate => candidate.Source?.Title == servicePanel.Name);
        var items = serviceControls.Select(item => GetNativeItem?.Invoke(item, null) as NativeRibbon.RibbonItem).ToArray();
        if (panel?.Source is not { } source || items.Any(item => item == null))
        {
            Trace.WriteLine("BotPlus RT: не удалось перестроить панель BotPlus.");
            return;
        }

        var stacks = source.Items.OfType<NativeRibbon.RibbonRowPanel>().ToArray();
        if (stacks.Length != 2)
        {
            Trace.WriteLine("BotPlus RT: неожиданная структура панели BotPlus.");
            return;
        }

        foreach (var stack in stacks) stack.Items.Clear();
        source.Items.Remove(stacks[1]);
        var rows = stacks[0];
        rows.SubPanelResizeStyle = NativeRibbon.RibbonRowPanelResizeStyle.NoResize;
        panel.ResizeStyle |= NativeRibbon.RibbonResizeStyles.NeverResizeItemWidth |
            NativeRibbon.RibbonResizeStyles.NeverCollapsePanel;

        foreach (var action in items.Take(4).OfType<NativeRibbon.RibbonButton>())
        {
            action.ShowText = false;
            action.ShowImage = true;
            action.Width = action.MinWidth = ActionWidth;
            rows.Items.Add(action);
        }
        foreach (var field in items.Skip(4))
        {
            rows.Items.Add(new NativeRibbon.RibbonRowBreak());
            field!.Width = field.MinWidth = RowWidth;
            rows.Items.Add(field);
        }
    }

    static IEnumerable<PushButton> Buttons(RibbonPanel panel)
    {
        foreach (var item in panel.GetItems())
        {
            if (item is PushButton button) yield return button;
            if (item is PulldownButton menu)
                foreach (var child in menu.GetItems()) yield return child;
        }
    }

    static bool EnabledModule(string? module) => module == null || !BotPlusSettings.HiddenPanels.Contains(module);

    internal void RefreshVisibility()
    {
        string active = section.Current?.Name ?? Main;
        foreach (var (panel, destination, module) in panels)
        {
            bool visible = destination == active && EnabledModule(module);
            if (panel.Visible != visible) panel.Visible = visible;
        }
        bool hasFavorites = false;
        foreach (var tool in tools)
        {
            if (!favoriteButtons.TryGetValue(tool.Id, out var button)) continue;
            bool visible = favorites.Contains(tool.Id) && EnabledModule(tool.Module);
            if (button.Visible != visible) button.Visible = visible;
            hasFavorites |= visible;
        }
        bool showFavorites = active == Favorites && hasFavorites;
        if (favoritesPanel.Visible != showFavorites) favoritesPanel.Visible = showFavorites;
    }

    void OnSectionChanged(object? sender, ComboBoxCurrentChangedEventArgs e) => RefreshVisibility();
    void OnSearch(object? sender, TextBoxEnterPressedEventArgs e) => searchEvent.Raise();

    public void Execute(UIApplication app)
    {
        try { ShowTools(app, search.Value?.ToString() ?? ""); }
        catch (Exception error) { TaskDialog.Show(Tab, error.GetBaseException().Message); }
    }

    public string GetName() => "BotPlus RT: поиск инструментов";

    internal void ShowTools(UIApplication app, string query, bool manageFavorites = false)
    {
        favorites = LoadFavorites();
        if (manageFavorites) SelectSection(Favorites);
        else RefreshVisibility();
        var window = new RibbonToolsWindow(tools.Where(tool => EnabledModule(tool.Module)), query, manageFavorites,
            id => favorites.Contains(id), ToggleFavorite);
        new WindowInteropHelper(window) { Owner = app.MainWindowHandle };
        if (window.ShowDialog() == true && window.Selected is { } tool)
            RunTool(app, tool);
    }

    void SelectSection(string name)
    {
        if (section.Current?.Name != name)
            section.Current = section.GetItems().Single(item => item.Name == name);
        RefreshVisibility();
    }

    void RunTool(UIApplication app, RibbonTool tool)
    {
        if (tool.Button == null)
        {
            if (app.ActiveUIDocument is { } document) OpenViews.Run(document);
            else TaskDialog.Show(tool.Title, "Откройте проект Revit.");
            return;
        }

        if (tool.Section != null) SelectSection(tool.Section);
        var native = GetNativeItem?.Invoke(tool.Button, null) as NativeRibbon.RibbonItem;
        var id = native is { IsEnabled: true } ? RevitCommandId.LookupCommandId(native.Id) : null;
        if (id == null || !app.CanPostCommand(id))
        {
            TaskDialog.Show(tool.Title, "Команда недоступна в текущем состоянии Revit.");
            return;
        }
        app.PostCommand(id);
    }

    static HashSet<string> LoadFavorites()
    {
        try
        {
            return File.Exists(FavoritesPath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FavoritesPath)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine(error);
            return [];
        }
    }

    void ToggleFavorite(string id)
    {
        bool add = !favorites.Contains(id);
        var updated = LoadFavorites();
        if (add) updated.Add(id);
        else updated.Remove(id);
        string path = FavoritesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(updated.Order(StringComparer.Ordinal)));
            File.Move(pending, path, overwrite: true);
        }
        finally { File.Delete(pending); }
        favorites = updated;
        RefreshVisibility();
    }

    public void Dispose()
    {
        section.CurrentChanged -= OnSectionChanged;
        search.EnterPressed -= OnSearch;
        searchEvent.Dispose();
        if (Current == this) Current = null;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class FavoritesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try { RibbonNavigation.Current?.ShowTools(data.Application, "", manageFavorites: true); }
        catch (Exception error) { message = error.GetBaseException().Message; return Result.Failed; }
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class FeedbackCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://t.me/botplusru") { UseShellExecute = true });
            return Result.Succeeded;
        }
        catch (Exception error) { message = "Не удалось открыть страницу обратной связи. " + error.Message; return Result.Failed; }
    }
}

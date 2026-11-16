using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BotPlus.Eom;

/// <summary>
/// Тип семейства категории «Осветительные приборы» в проекте. Component — лампа или вложенный компонент, а не светильник.
/// Размеры, мм — из параметров типа (0 — параметра нет).
/// </summary>
internal sealed record FixtureType(Autodesk.Revit.DB.ElementId Id, string Family, string Type, bool Component, int Instances, double LengthMm, double WidthMm, double HeightMm)
{
    public string Name => $"{Family}: {Type}";
    public string Label => Name + (Component ? "  — лампа или вложенный компонент" : "") + (Instances > 0 ? $"  · в проекте {Instances}" : "");
}

/// <summary>Общие данные окон светотехники: каталог светильников, библиотека прототипов, семейства светильников проекта.</summary>
internal sealed class LightingContext(EomCatalog catalog, PrototypeLibrary prototypes, List<FixtureType> fixtures)
{
    internal EomCatalog Catalog { get; } = catalog;
    internal PrototypeLibrary Prototypes { get; } = prototypes;
    internal List<FixtureType> Fixtures { get; } = fixtures;
    internal LuminaireItem? Find(string name) => Catalog.Luminaires.FirstOrDefault(l => l.Name == name);
    /// <summary>Семейство, связанное со светильником БД (по «Семейство: Тип»), если оно есть в проекте.</summary>
    internal FixtureType? Linked(LuminaireItem? lum) => lum == null || lum.RevitType.Length == 0 ? null : Fixtures.FirstOrDefault(f => f.Name == lum.RevitType);

    /// <summary>
    /// Связывает светильник БД с семейством проекта. Пустые габариты БД заполняются из семейства;
    /// при расстановке данные БД записываются в тип семейства с именем светильника.
    /// </summary>
    internal bool Link(Window? owner, LuminaireItem lum)
    {
        var picked = TreePicker.Pick(owner, $"Семейство Revit для «{lum.Name}»", Fixtures,
            s => [s.Component ? "Лампы и вложенные компоненты" : "Светильники", s.Family], s => s.Type,
            s => [("Семейство", "Семейство", s.Family), ("Семейство", "Тип", s.Type),
                  ("Семейство", "Назначение", s.Component ? "лампа или вложенный компонент — для расстановки не подходит" : "светильник"),
                  ("Семейство", "Экземпляров в проекте", s.Instances.ToString()),
                  ("Габариты типа", "Длина, мм", s.LengthMm > 0 ? s.LengthMm.ToString("0") : "нет параметра"),
                  ("Габариты типа", "Ширина, мм", s.WidthMm > 0 ? s.WidthMm.ToString("0") : "нет параметра"),
                  ("Габариты типа", "Высота, мм", s.HeightMm > 0 ? s.HeightMm.ToString("0") : "нет параметра")],
            Linked(lum),
            "Выберите семейство светильника (не лампу). При расстановке плагин создаст в этом семействе тип с именем светильника БД и запишет в него " +
            "габариты, мощность, поток, cos φ, наименование, марку, код и производителя — данные семейства и БД будут совпадать. Пустые габариты БД заполнятся из семейства.");
        if (picked == null) return false;
        if (picked.Component && MessageBox.Show($"«{picked.Name}» похоже на лампу или вложенный компонент, а не на светильник. Всё равно связать?", "BotPlus ЭОМ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        lum.RevitType = picked.Name;
        if (lum.LengthMm <= 0 && picked.LengthMm > 0) lum.LengthMm = Math.Round(picked.LengthMm);
        if (lum.WidthMm <= 0 && picked.WidthMm > 0) lum.WidthMm = Math.Round(picked.WidthMm);
        if (lum.HeightMm <= 0 && picked.HeightMm > 0) lum.HeightMm = Math.Round(picked.HeightMm);
        Catalog.Save();
        return true;
    }

    /// <summary>Свойства помещения по умолчанию: прототип по названию, первый светильник БД, установка под потолком.</summary>
    internal RoomLight Auto(RoomRef room)
    {
        var light = new RoomLight { MountM = Math.Round(room.HeightM, 2), Luminaire = Catalog.Luminaires.FirstOrDefault()?.Name ?? "" };
        var p = Prototypes.Match(room.Name) ?? Prototypes.Items.FirstOrDefault(i => i.Group == PrototypeLibrary.UserGroup && i.Name == "Прочие помещения");
        return p == null ? light : light.From(p);
    }
}

/// <summary>Помещение в окне светотехнического расчёта: свойства, результат расчёта методом Ки.</summary>
internal sealed class LightRow : INotifyPropertyChanged
{
    internal RoomRef Room { get; }
    internal RoomGeometry Geometry { get; }
    internal RoomLight Light { get; set; }
    /// <summary>Свойства уже записаны в модель (иначе — подобраны автоматически по названию).</summary>
    internal bool Stored { get; set; }
    internal KiResult? Result { get; private set; }

    internal LightRow(RoomRef room, RoomLight light, bool stored) { Room = room; Geometry = room.Geometry(); Light = light; Stored = stored; }
    /// <summary>Контекст и уведомление окна для правки прямо в ячейках таблицы.</summary>
    internal LightingContext? Ctx { get; set; }
    internal Action? Edited { get; set; }

    /// <summary>Изменяет свойства с проверкой и пересчётом. Недопустимое значение не применяется (ячейка подсвечивается ошибкой).</summary>
    internal void Update(Func<RoomLight, RoomLight> change)
    {
        var updated = change(Light);
        updated.Validate();
        Light = updated;
        if (Ctx != null) Recalc(Ctx);
        Edited?.Invoke();
    }

    static double Parse(string text, string name) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
        : throw new InvalidOperationException($"«{name}»: введите число.");

    public string Source => Room.Source;
    public string Number => Room.Number;
    public string Name => Room.Name;
    public string Area => Geometry.AreaM2.ToString("0.00");
    public string Prototype => (Light.Prototype.Length > 0 ? Light.Prototype : "задано вручную") + (Stored ? "" : " · авто");
    public string Lux { get => Light.Lux.ToString("0"); set => Update(r => r with { Lux = Parse(value, "Eн") }); }
    public string WorkPlane { get => Light.WorkPlaneM.ToString("0.##"); set => Update(r => r with { WorkPlaneM = Parse(value, "Рабочая поверхность") }); }
    public string Maintenance { get => Light.Maintenance.ToString("0.##"); set => Update(r => r with { Maintenance = Parse(value, "Kз") }); }
    public string Mount { get => (Light.MountM > 0 ? Light.MountM : Geometry.HeightM).ToString("0.##"); set => Update(r => r with { MountM = Parse(value, "Высота установки") }); }
    public string Luminaire => Light.Luminaire.Length > 0 ? Light.Luminaire : "—";
    public string Index => Result is { Index: > 0 } r ? r.Index.ToString("0.00") : "";
    public string Ki => Result is { Ki: > 0 } r ? r.Ki.ToString("0.000") : "";
    public int PlaceCount => Result?.Count(Light.UseRecommended) ?? 0;
    public (int Rows, int Cols) PlaceGrid => Result?.Grid(Light.UseRecommended, Geometry) ?? (0, 0);
    public string Count => Result == null || PlaceCount == 0 ? "" : $"{PlaceCount} ({PlaceGrid.Rows}×{PlaceGrid.Cols})";
    public double ActualLux => Result?.Illuminance(Light.UseRecommended) ?? 0;
    public string Actual => Result == null ? "" : ActualLux.ToString("0");
    public string Specific => Result?.Luminaire is { } l && Geometry.AreaM2 > 0 ? (PlaceCount * l.Power / Geometry.AreaM2).ToString("0.0") : "";
    public string Status =>
        Result == null ? (Light.Luminaire.Length == 0 ? "не выбран светильник" : "светильника нет в БД")
        : !Result.Ok ? Result.Problem
        : ActualLux + 1e-6 < Light.Lux ? "ниже нормы"
        : Light.Placed.Count > 0 ? $"расставлено {Light.Placed.Count}" : "";

    internal void Recalc(LightingContext ctx)
    {
        Result = ctx.Find(Light.Luminaire) is { } lum ? Lighting.Calculate(Geometry, Light, lum) : null;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Панель «Светотехнические свойства» выделенных помещений. Изменённое поле применяется ко всем выделенным,
/// остальные свойства каждого помещения сохраняются; разные значения показываются пустым полем.
/// </summary>
internal sealed class LightPanel
{
    internal FrameworkElement View { get; }
    internal event Action? Changed;
    readonly Func<IReadOnlyList<LightRow>> selection;
    readonly LightingContext ctx;
    readonly List<Action<IReadOnlyList<LightRow>>> refreshers = [];
    bool loading;

    internal LightPanel(Func<IReadOnlyList<LightRow>> selection, LightingContext ctx)
    {
        this.selection = selection; this.ctx = ctx;
        var grid = new PropGrid();

        grid.Header("Прототип помещения");
        var proto = grid.Value("Прототип");
        refreshers.Add(rows => proto.Text = Same(rows, r => r.Light.Prototype) is { } p ? (p.Length > 0 ? p : "задано вручную") : "разные");

        grid.Header("Светотехнические характеристики");
        Text(grid, "Разряд зрительных работ", r => r.VisualCategory, (r, v) => r with { VisualCategory = v.Trim().Length == 0 ? "-" : v.Trim() });
        var lux = grid.Row("Нормируемая освещённость, лк", new ComboBox { IsEditable = true, ItemsSource = Lighting.LuxScale.Select(v => v.ToString("0")).ToList() });
        void CommitLux() { if (!loading && lux.Text.Trim().Length > 0) Apply(r => r with { Lux = Parse(lux.Text, "Нормируемая освещённость") }); }
        lux.SelectionChanged += (_, _) => { if (!loading && lux.SelectedItem is string s) { lux.Text = s; CommitLux(); } };
        lux.LostKeyboardFocus += (_, _) => { if (Same(selection(), r => r.Light.Lux.ToString("0")) != lux.Text.Trim()) CommitLux(); };
        lux.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitLux(); };
        refreshers.Add(rows => lux.Text = Same(rows, r => r.Light.Lux.ToString("0")) ?? "");
        Number(grid, "Высота рабочей поверхности, м", r => r.WorkPlaneM, (r, v) => r with { WorkPlaneM = v });

        grid.Header("Характеристики среды");
        Text(grid, "Категория помещения по СП 12.13130", r => r.RoomCategory, (r, v) => r with { RoomCategory = v.Trim() });
        Text(grid, "Класс взрывоопасной зоны (ПУЭ)", r => r.ExplosionZone, (r, v) => r with { ExplosionZone = v.Trim() });
        Text(grid, "Класс пожароопасной зоны (ПУЭ)", r => r.FireZone, (r, v) => r with { FireZone = v.Trim() });
        Text(grid, "Среда", r => r.Environment, (r, v) => r with { Environment = v.Trim() });
        Number(grid, "Температура, °C", r => r.Temperature, (r, v) => r with { Temperature = v });

        grid.Header("Светотехнические коэффициенты");
        Number(grid, "Коэффициент отражения потолка ρп, о.е.", r => r.Ceiling, (r, v) => r with { Ceiling = v });
        Number(grid, "Коэффициент отражения стен ρс, о.е.", r => r.Walls, (r, v) => r with { Walls = v });
        Number(grid, "Коэффициент отражения пола ρр, о.е.", r => r.Floor, (r, v) => r with { Floor = v });
        Number(grid, "Коэффициент запаса Kз, о.е.", r => r.Maintenance, (r, v) => r with { Maintenance = v });

        grid.Header("Расчёт освещённости. Метод Ки");
        Number(grid, "Высота установки светильников, м", r => r.MountM, (r, v) => r with { MountM = v }, "От пола помещения. 0 — по высоте помещения (под потолком).");
        var lumBox = new TextBox { IsReadOnly = true };
        grid.Row("Светильник", PropGrid.WithButton(lumBox, "…", PickLuminaire, "Выбрать светильник из базы данных оборудования"));
        refreshers.Add(rows => lumBox.Text = Same(rows, r => r.Light.Luminaire) ?? "разные");
        var familyBox = new TextBox { IsReadOnly = true };
        grid.Row("Семейство Revit светильника", PropGrid.WithButton(familyBox, "…", LinkFamily, "Связать светильник БД с семейством проекта"),
            "Каждый светильник БД связан со своим семейством. При расстановке в семействе создаётся тип с именем светильника и данными из БД.");
        refreshers.Add(rows =>
        {
            var lum = Same(rows, r => r.Light.Luminaire) is { } name ? ctx.Find(name) : null;
            familyBox.Text = lum == null ? "" : ctx.Linked(lum)?.Name ?? (lum.RevitType.Length > 0 ? $"нет в проекте: {lum.RevitType}" : "не связано — нажмите «…»");
        });
        var mode = grid.Row("Расчёт по", PropGrid.Choice(["требуемой освещённости", "заданному количеству"]));
        mode.SelectionChanged += (_, _) =>
        {
            if (loading || mode.SelectedIndex < 0) return;
            Apply(r => mode.SelectedIndex == 0 ? r with { FixedCount = 0 } : r.FixedCount > 0 ? r : r with { FixedCount = Math.Max(1, selection().FirstOrDefault()?.PlaceCount ?? 1) });
        };
        refreshers.Add(rows => mode.SelectedIndex = Same(rows, r => r.Light.FixedCount > 0 ? "1" : "0") is { } m ? int.Parse(m) : -1);
        Number(grid, "Заданное количество светильников", r => r.FixedCount, (r, v) => r with { FixedCount = (int)Math.Round(v) }, "Используется при расчёте по заданному количеству. 0 — по требуемой освещённости.");
        var layout = grid.Row("Расставлять", PropGrid.Choice(["рекомендуемое (ровной сеткой)", "необходимое"]));
        layout.SelectionChanged += (_, _) => { if (!loading && layout.SelectedIndex >= 0) Apply(r => r with { UseRecommended = layout.SelectedIndex == 0 }); };
        refreshers.Add(rows => layout.SelectedIndex = Same(rows, r => r.Light.UseRecommended ? "0" : "1") is { } m ? int.Parse(m) : -1);

        grid.Header("Результат расчёта");
        var index = grid.Value("Индекс помещения i");
        var ki = grid.Value("Коэффициент использования Ки");
        var required = grid.Value("Необходимое количество светильников");
        var recommended = grid.Value("Рекомендуемое количество (рядов × в ряду)");
        var actual = grid.Value("Расчётная освещённость Eср, лк");
        var specific = grid.Value("Удельная установленная мощность, Вт/м²");
        var status = grid.Value("Замечания");
        refreshers.Add(rows =>
        {
            var r = rows.Count == 1 ? rows[0] : null; var k = r?.Result;
            index.Text = k == null ? "" : $"{k.Index:0.00} (h = {k.HeightM:0.00} м)";
            ki.Text = k == null ? "" : k.Ki.ToString("0.000") + (k.Luminaire.Utilization is { IsValid: true } ? " — по таблице светильника" : $" — типовая КСС Д × КПД {k.Luminaire.Efficiency:0.##}");
            required.Text = k == null ? "" : $"{k.Required} → {k.ERequired:0} лк";
            recommended.Text = k == null ? "" : $"{k.Recommended} ({k.Rows} × {k.Cols}) → {k.ERecommended:0} лк";
            actual.Text = r?.Actual ?? (rows.Count > 1 ? "выделите одно помещение" : "");
            specific.Text = r?.Specific ?? "";
            status.Text = r?.Status ?? "";
        });

        var toolbar = PropGrid.Toolbar(
            EomUi.Button("Из прототипа…", PickPrototype),
            EomUi.Button("Сохранить как прототип…", SavePrototype));
        ((Button)toolbar.Children[0]).ToolTip = "Заполнить свойства выделенных помещений из библиотеки прототипов (СП 52.13330).";
        ((Button)toolbar.Children[1]).ToolTip = "Добавить свойства помещения в библиотеку прототипов пользователя.";
        var stack = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top); stack.Children.Add(toolbar);
        stack.Children.Add(new ScrollViewer { Content = grid.View, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        View = stack;
    }

    static string? Same(IReadOnlyList<LightRow> rows, Func<LightRow, string> get)
    {
        if (rows.Count == 0) return null;
        string first = get(rows[0]);
        return rows.All(r => get(r) == first) ? first : null;
    }

    static double Parse(string text, string name)
    {
        if (double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        throw new InvalidOperationException($"«{name}»: введите число.");
    }

    TextBox Number(PropGrid grid, string label, Func<RoomLight, double> get, Func<RoomLight, double, RoomLight> set, string? tip = null)
    {
        var box = grid.Row(label, PropGrid.Edit(text => { if (!loading && text.Trim().Length > 0) Apply(r => set(r, Parse(text, label))); }), tip);
        refreshers.Add(rows => box.Text = Same(rows, r => get(r.Light).ToString("0.###")) ?? "");
        return box;
    }

    void Text(PropGrid grid, string label, Func<RoomLight, string> get, Func<RoomLight, string, RoomLight> set)
    {
        var box = grid.Row(label, PropGrid.Edit(text => { if (!loading) Apply(r => set(r, text)); }));
        refreshers.Add(rows => box.Text = Same(rows, r => get(r.Light)) ?? "");
    }

    /// <summary>Применяет изменение к выделенным помещениям. Недопустимое значение не применяется ни к одному.</summary>
    internal void Apply(Func<RoomLight, RoomLight> change)
    {
        var rows = selection();
        if (rows.Count == 0) return;
        try
        {
            var updated = rows.Select(r => change(r.Light)).ToList();
            foreach (var u in updated) u.Validate();
            for (int i = 0; i < rows.Count; i++) { rows[i].Light = updated[i]; rows[i].Recalc(ctx); }
            Changed?.Invoke();
        }
        finally { Refresh(); }
    }

    internal void Refresh()
    {
        loading = true;
        try { var rows = selection(); foreach (var r in refreshers) r(rows); View.IsEnabled = rows.Count > 0; }
        finally { loading = false; }
    }

    void PickPrototype()
    {
        var rows = selection();
        if (rows.Count == 0) return;
        if (PickPrototype(Window.GetWindow(View), ctx, rows[0].Light.Prototype) is { } picked) Apply(r => r.From(picked));
    }

    /// <summary>Дерево «Прототипы помещений»; текущий прототип ищется по подписи.</summary>
    internal static RoomPrototype? PickPrototype(Window? owner, LightingContext ctx, string current)
    {
        var lib = ctx.Prototypes;
        string hint = lib.Source.Length > 0
            ? $"Библиотека импортирована из nanoCAD BIM Электро ({lib.Source}): СП 52.13330.2016 и другие нормы. Свои прототипы — кнопкой «Сохранить как прототип». Файл: {PrototypeLibrary.DefaultPath}"
            : $"Сокращённая встроенная библиотека по СП 52.13330.2016 — nanoCAD BIM Электро на компьютере не найден. Полную библиотеку можно импортировать в «Менеджере БД». Файл: {PrototypeLibrary.DefaultPath}";
        return TreePicker.Pick(owner, "Прототипы помещений", lib.Items, p => p.Group.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), p => p.Name,
            PrototypeProps, lib.Items.FirstOrDefault(p => p.Label == current || p.Name == current), hint);
    }

    void SavePrototype()
    {
        var rows = selection();
        if (rows.Count != 1) throw new InvalidOperationException("Выделите одно помещение.");
        var l = rows[0].Light;
        if (InputWindow.Ask(Window.GetWindow(View), "Сохранить как прототип", "Наименование прототипа", rows[0].Name, "Ключевые слова названия помещения через «;» (для автоподбора)", rows[0].Name.ToLowerInvariant()) is not { } answer) return;
        var p = new RoomPrototype
        {
            Group = PrototypeLibrary.UserGroup, Name = answer.First, Keywords = answer.Second, VisualCategory = l.VisualCategory, Lux = l.Lux, WorkPlaneM = l.WorkPlaneM,
            Maintenance = l.Maintenance, Ceiling = l.Ceiling, Walls = l.Walls, Floor = l.Floor, Environment = l.Environment,
            Temperature = l.Temperature, RoomCategory = l.RoomCategory, ExplosionZone = l.ExplosionZone, FireZone = l.FireZone
        };
        ctx.Prototypes.Items.RemoveAll(i => i.Group == p.Group && i.Name == p.Name);
        ctx.Prototypes.Items.Add(p);
        ctx.Prototypes.Save();
        Apply(r => r with { Prototype = p.Label });
    }

    void LinkFamily()
    {
        var rows = selection();
        if (rows.Count == 0) return;
        if (Same(rows, r => r.Light.Luminaire) is not { } name || ctx.Find(name) is not { } lum) throw new InvalidOperationException("Выделите помещения с одним и тем же светильником из БД.");
        if (ctx.Link(Window.GetWindow(View), lum)) { foreach (var r in rows) r.Recalc(ctx); Changed?.Invoke(); Refresh(); }
    }

    void PickLuminaire()
    {
        var rows = selection();
        if (rows.Count == 0) return;
        var picked = LuminairePicker.Pick(Window.GetWindow(View), ctx, ctx.Find(rows[0].Light.Luminaire));
        if (picked != null) Apply(r => r with { Luminaire = picked.Name });
    }

    internal static IEnumerable<(string, string, string)> PrototypeProps(RoomPrototype p)
    {
        const string A = "Основные характеристики", E = "Характеристики среды", B = "Светотехнические характеристики", C = "Коэффициенты отражения";
        yield return (A, "Имя прототипа", p.Name); yield return (A, "Подпись в таблице", p.Label); yield return (A, "Каталог", p.Group);
        yield return (A, "Ключевые слова (автоподбор)", p.Keywords.Length > 0 ? p.Keywords : "—");
        yield return (E, "Категория помещения по СП 12.13130", p.RoomCategory); yield return (E, "Класс взрывоопасной зоны (ПУЭ)", p.ExplosionZone);
        yield return (E, "Класс пожароопасной зоны (ПУЭ)", p.FireZone); yield return (E, "Среда", p.Environment); yield return (E, "Температура, °C", p.Temperature.ToString("0.#"));
        yield return (B, "Разряд зрительных работ", p.VisualCategory); yield return (B, "Нормируемая освещённость, лк", p.Lux.ToString("0"));
        yield return (B, "Высота рабочей поверхности, м", p.WorkPlaneM.ToString("0.##")); yield return (B, "Коэффициент запаса Kз", p.Maintenance.ToString("0.##"));
        yield return (C, "Потолка", p.Ceiling.ToString("0.##")); yield return (C, "Стен", p.Walls.ToString("0.##")); yield return (C, "Пола", p.Floor.ToString("0.##"));
    }
}

/// <summary>
/// Светотехнический расчёт по схеме nanoCAD BIM Электро: свойства помещения (прототип или вручную) → светильник из БД →
/// расчёт методом Ки → «Поместить» сеткой в помещении. Свойства помещений хранятся в модели.
/// </summary>
internal static class LightingWindow
{
    internal sealed record Outcome(List<LightRow> Rows, List<LightRow> Place, bool Replace);

    /// <summary>Столбец таблицы помещений: правка в ячейке, протягивание за угол, выбор по двойному щелчку.</summary>
    sealed record Column(string Header, string Path, bool Edit = false, Func<RoomLight, RoomLight, RoomLight>? Fill = null, Action<LightRow>? Pick = null, bool Star = false);

    internal static Outcome? Show(List<LightRow> rows, LightingContext ctx, HashSet<string> preselected)
    {
        var window = EomUi.Window("Светотехнический расчёт", 1400, 780);
        bool dirty = false;
        var filter = new TextBox { Width = 240, ToolTip = "Номер, название или источник помещения" };
        var view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = o => filter.Text.Trim().Length == 0 || o is LightRow r && $"{r.Number} {r.Name} {r.Source}".Contains(filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        filter.TextChanged += (_, _) => view.Refresh();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, ItemsSource = view, IsReadOnly = false, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow
        };
        List<LightRow> Selected() => grid.SelectedItems.Cast<LightRow>().ToList();
        var panel = new LightPanel(Selected, ctx);
        var total = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        void Totals()
        {
            var sel = Selected(); var scope = sel.Count > 0 ? sel : rows;
            int n = scope.Sum(r => r.PlaceCount);
            double kw = scope.Sum(r => r.PlaceCount * (r.Result?.Luminaire.Power ?? 0)) / 1000;
            total.Text = $"{(sel.Count > 0 ? $"Выделено помещений: {sel.Count}" : $"Всех помещений: {rows.Count}")} · светильников {n} · {kw:0.00} кВт · ниже нормы: {scope.Count(r => r.Result != null && r.ActualLux + 1e-6 < r.Light.Lux)}";
        }
        void Edited() { dirty = true; panel.Refresh(); Totals(); }
        foreach (var r in rows) { r.Ctx = ctx; r.Edited = Edited; r.Recalc(ctx); }
        panel.Changed += () => { dirty = true; Totals(); };

        static RoomLight PrototypeFields(RoomLight s, RoomLight d) => d with
        {
            Prototype = s.Prototype, VisualCategory = s.VisualCategory, Lux = s.Lux, WorkPlaneM = s.WorkPlaneM, Maintenance = s.Maintenance,
            Ceiling = s.Ceiling, Walls = s.Walls, Floor = s.Floor, Environment = s.Environment, Temperature = s.Temperature,
            RoomCategory = s.RoomCategory, ExplosionZone = s.ExplosionZone, FireZone = s.FireZone
        };
        Column[] columns =
        [
            new("Источник", nameof(LightRow.Source)), new("№", nameof(LightRow.Number)), new("Помещение", nameof(LightRow.Name), Star: true), new("S, м²", nameof(LightRow.Area)),
            new("Прототип", nameof(LightRow.Prototype), Fill: PrototypeFields,
                Pick: row => { if (LightPanel.PickPrototype(window, ctx, row.Light.Prototype) is { } p) row.Update(l => l.From(p)); }),
            new("Eн, лк", nameof(LightRow.Lux), true, (s, d) => d with { Lux = s.Lux }),
            new("Hраб, м", nameof(LightRow.WorkPlane), true, (s, d) => d with { WorkPlaneM = s.WorkPlaneM }),
            new("Kз", nameof(LightRow.Maintenance), true, (s, d) => d with { Maintenance = s.Maintenance }),
            new("Hуст, м", nameof(LightRow.Mount), true, (s, d) => d with { MountM = s.MountM }),
            new("Светильник", nameof(LightRow.Luminaire), Fill: (s, d) => d with { Luminaire = s.Luminaire },
                Pick: row => { if (LuminairePicker.Pick(window, ctx, ctx.Find(row.Light.Luminaire)) is { } l) row.Update(x => x with { Luminaire = l.Name }); }),
            new("i", nameof(LightRow.Index)), new("Ки", nameof(LightRow.Ki)), new("N (сетка)", nameof(LightRow.Count)), new("Eср, лк", nameof(LightRow.Actual)),
            new("Руд, Вт/м²", nameof(LightRow.Specific)), new("Замечания", nameof(LightRow.Status)),
        ];
        var specs = new Dictionary<DataGridColumn, Column>();
        foreach (var c in columns)
        {
            var binding = new Binding(c.Path) { Mode = c.Edit ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true };
            var column = new DataGridTextColumn
            {
                Header = c.Header + (c.Fill != null ? " ⇕" : ""), Binding = binding, IsReadOnly = !c.Edit,
                Width = c.Star ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto
            };
            if (c.Pick != null) column.CellStyle = new Style(typeof(DataGridCell)) { Setters = { new Setter(FrameworkElement.ToolTipProperty, "Двойной щелчок — выбрать; потяните за уголок ячейки, чтобы скопировать вниз или вверх") } };
            else if (c.Fill != null) column.CellStyle = new Style(typeof(DataGridCell)) { Setters = { new Setter(FrameworkElement.ToolTipProperty, "Щёлкните и введите значение; потяните за уголок ячейки, чтобы скопировать вниз или вверх") } };
            grid.Columns.Add(column); specs[column] = c;
        }
        grid.Columns[2].MinWidth = 140;
        grid.Columns[4].MaxWidth = 380;

        // Протягивание за угол текущей ячейки, как в Excel: значение источника копируется в строки диапазона.
        new FillHandle(grid, column => specs.TryGetValue(column, out var c) && c.Fill != null, (column, source, targets) =>
        {
            var c = specs[column]; var src = (LightRow)source;
            foreach (var t in targets.Cast<LightRow>()) t.Update(d => c.Fill!(src.Light, d));
            Edited();
        }).Attach();

        grid.SelectionChanged += (_, _) => { panel.Refresh(); Totals(); };

        Outcome? result = null;
        void Place(List<LightRow> place)
        {
            place = place.Where(r => r.PlaceCount > 0).ToList();
            if (place.Count == 0) throw new InvalidOperationException("Выделите помещения с рассчитанным количеством светильников.");
            if (ctx.Fixtures.Count == 0) throw new InvalidOperationException("В проекте нет семейств осветительных приборов. Загрузите семейство через «База УГО».");
            // У каждого светильника БД — своё семейство. Несвязанные связываются здесь же.
            foreach (var lum in place.Select(r => r.Result!.Luminaire).Distinct().ToList())
                if (ctx.Linked(lum) == null && !ctx.Link(window, lum)) return;
            var lines = place.GroupBy(r => r.Result!.Luminaire).Select(g => $"• {g.Key.Name}: {g.Sum(r => r.PlaceCount)} шт. → семейство «{ctx.Linked(g.Key)!.Family}», тип «{FixtureSync.TypeName(g.Key)}»");
            string text = $"Будет расставлено светильников: {place.Sum(r => r.PlaceCount)} в помещениях: {place.Count}.\n\n{string.Join("\n", lines)}\n\nДанные светильника из БД (габариты, мощность, поток, наименование) будут записаны в тип семейства.";
            bool replace = true;
            if (place.Any(r => r.Light.Placed.Count > 0))
            {
                var answer = MessageBox.Show(window, text + "\n\nВ части помещений уже есть светильники, расставленные BotPlus. Удалить их перед новой расстановкой?\n\nДа — заменить, Нет — добавить к существующим, Отмена — не расставлять.",
                    "BotPlus ЭОМ", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                replace = answer == MessageBoxResult.Yes;
            }
            else if (MessageBox.Show(window, text + "\n\nРасставить?", "BotPlus ЭОМ", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            result = new(rows, place, replace);
            window.DialogResult = true;
        }
        void OpenKi()
        {
            if (Selected().FirstOrDefault() is not { } row) throw new InvalidOperationException("Выделите помещение.");
            var outcome = KiWindow.Show(window, row, ctx);
            dirty |= outcome != KiWindow.Outcome.None;
            grid.Items.Refresh(); panel.Refresh(); Totals();
            if (outcome == KiWindow.Outcome.Place) Place([row]);
        }
        grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var cell = FillHandle.Ancestor<DataGridCell>(source);
            if (cell?.DataContext is not LightRow row) return;
            try
            {
                if (specs.TryGetValue(cell.Column, out var c) && c.Pick != null) { c.Pick(row); e.Handled = true; }
                else if (!cell.Column.IsReadOnly) return;
                else { OpenKi(); e.Handled = true; }
            }
            catch (Exception error) { MessageBox.Show(window, error.Message, "BotPlus ЭОМ"); }
        };

        var top = new StackPanel();
        top.Children.Add(EomUi.Hint("Выделите помещения (Ctrl/Shift) и заполните свойства справа или прямо в таблице: Eн, Hраб, Kз и Hуст вводятся в ячейке, «Прототип» и «Светильник» выбираются двойным щелчком. " +
            "Столбцы со значком ⇕ протягиваются за уголок ячейки вниз или вверх, как в Excel. Двойной щелчок по остальным ячейкам — «Расчёт методом Ки». N = Eн·S·Kз / (Φ·Ки), i = 2S / (h·P)."));
        var search = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        search.Children.Add(new TextBlock { Text = "Поиск помещения ", VerticalAlignment = VerticalAlignment.Center }); search.Children.Add(filter);
        top.Children.Add(search);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
        body.Children.Add(grid);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var right = new GroupBox { Header = "Светотехнические свойства", Content = panel.View, Padding = new Thickness(4) };
        Grid.SetColumn(right, 2); body.Children.Add(right);

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = EomUi.Buttons(
            EomUi.Button("Расчёт Ки…", OpenKi),
            EomUi.Button("Поместить в выделенные", () => Place(Selected()), true),
            EomUi.Button("Сохранить", () => { result = new(rows, [], false); window.DialogResult = true; }),
            EomUi.Button("Закрыть", () => window.Close()));
        DockPanel.SetDock(actions, Dock.Right);
        bottom.Children.Add(actions); bottom.Children.Add(total);
        ((Button)actions.Children[1]).ToolTip = "Расставить светильники сеткой в выделенных помещениях. Каждый светильник БД ставится своим семейством; свойства всех помещений сохраняются в модели.";
        ((Button)actions.Children[2]).ToolTip = "Записать светотехнические свойства помещений в модель без расстановки.";

        window.Closing += (_, e) =>
        {
            if (window.DialogResult == true || !dirty) return;
            var answer = MessageBox.Show(window, "Сохранить светотехнические свойства помещений в модели?", "BotPlus ЭОМ", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes) result = new(rows, [], false);
        };
        EomUi.Layout(window, body, top, bottom);
        window.Loaded += (_, _) =>
        {
            foreach (var r in rows.Where(r => preselected.Contains(r.Room.Key))) grid.SelectedItems.Add(r);
            if (grid.SelectedItems.Count == 0 && rows.Count > 0) grid.SelectedIndex = 0;
            if (grid.SelectedItem != null) grid.ScrollIntoView(grid.SelectedItem);
            panel.Refresh(); Totals();
        };
        window.ShowDialog();
        return result;
    }
}

/// <summary>Небольшой диалог на два поля.</summary>
internal static class InputWindow
{
    internal static (string First, string Second)? Ask(Window? owner, string title, string label1, string value1, string label2, string value2)
    {
        var window = EomUi.Window(title, 560, 230); window.Owner = owner;
        TextBox a = new() { Text = value1, Width = 520 }, b = new() { Text = value2, Width = 520 };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = label1 }); body.Children.Add(a);
        body.Children.Add(new TextBlock { Text = label2, Margin = new Thickness(0, 8, 0, 0) }); body.Children.Add(b);
        (string, string)? result = null;
        EomUi.Layout(window, body, null, EomUi.Buttons(
            EomUi.Button("OK", () => { if (a.Text.Trim().Length == 0) throw new InvalidOperationException("Введите наименование."); result = (a.Text.Trim(), b.Text.Trim()); window.DialogResult = true; }, true),
            EomUi.Button("Отмена", () => window.DialogResult = false)));
        window.Loaded += (_, _) => { a.Focus(); a.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BotPlus.Eom;

/// <summary>Окно выбора из дерева с поиском и таблицей свойств справа («Прототипы комнат», «База данных оборудования» nanoCAD).</summary>
internal static class TreePicker
{
    internal static T? Pick<T>(Window? owner, string title, IReadOnlyList<T> items, Func<T, string[]> path, Func<T, string> name,
        Func<T, IEnumerable<(string Group, string Label, string Value)>> props, T? current, string hint, (string Text, Action<Window, T> Run)? extra = null) where T : class
    {
        var window = EomUi.Window(title, 1180, 700); window.Owner = owner;
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Поиск по наименованию" };
        var tree = new TreeView();
        var details = new ContentControl();
        T? selected = current;

        void Show(T? item)
        {
            selected = item;
            if (item == null) { details.Content = new TextBlock { Text = "Выберите элемент в дереве.", Opacity = 0.7, Margin = new Thickness(6) }; return; }
            var grid = new PropGrid();
            foreach (var group in props(item).GroupBy(p => p.Group))
            {
                grid.Header(group.Key);
                foreach (var p in group) grid.Value(p.Label, p.Value).ToolTip = p.Value;
            }
            details.Content = new ScrollViewer { Content = grid.View, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        void Build()
        {
            tree.Items.Clear();
            string q = search.Text.Trim();
            bool filtered = q.Length > 0;
            var nodes = new Dictionary<string, TreeViewItem>();
            TreeViewItem? select = null;
            foreach (var item in items.Where(i => !filtered || name(i).Contains(q, StringComparison.CurrentCultureIgnoreCase) || string.Join(" ", path(i)).Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            {
                ItemCollection parent = tree.Items; string key = "";
                foreach (var part in path(item).DefaultIfEmpty("Без раздела"))
                {
                    key += "\\" + part;
                    if (!nodes.TryGetValue(key, out var node))
                    {
                        node = new TreeViewItem { Header = "📁 " + part, IsExpanded = filtered };
                        nodes[key] = node; parent.Add(node);
                    }
                    parent = node.Items;
                }
                var leaf = new TreeViewItem { Header = name(item), Tag = item };
                leaf.MouseDoubleClick += (_, e) => { if (leaf.IsSelected) { selected = item; window.DialogResult = true; e.Handled = true; } };
                parent.Add(leaf);
                if (ReferenceEquals(item, current)) select = leaf;
            }
            if (select != null)
            {
                for (var p = select.Parent as TreeViewItem; p != null; p = p.Parent as TreeViewItem) p.IsExpanded = true;
                select.IsSelected = true; select.BringIntoView();
            }
            else if (!filtered && tree.Items.Count > 0 && tree.Items[0] is TreeViewItem first) first.IsExpanded = true;
        }
        tree.SelectedItemChanged += (_, _) => Show((tree.SelectedItem as TreeViewItem)?.Tag as T);
        search.TextChanged += (_, _) => Build();

        var left = new DockPanel();
        DockPanel.SetDock(search, Dock.Top); left.Children.Add(search); left.Children.Add(tree);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        body.Children.Add(left);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        Grid.SetColumn(details, 2); body.Children.Add(details);

        var buttons = new List<Button>();
        if (extra is { } x) buttons.Add(EomUi.Button(x.Text, () => { if (selected == null) throw new InvalidOperationException("Выберите элемент в дереве."); x.Run(window, selected); Show(selected); }));
        EomUi.Layout(window, body, EomUi.Hint(hint), EomUi.Buttons([.. buttons,
            EomUi.Button("Выбрать", () => { if (selected == null) throw new InvalidOperationException("Выберите элемент в дереве."); window.DialogResult = true; }, true),
            EomUi.Button("Закрыть", () => window.DialogResult = false)]));
        Build(); Show(current);
        return window.ShowDialog() == true ? selected : null;
    }
}

/// <summary>«База данных оборудования»: светильники по разделам и сериям с техническими данными.</summary>
internal static class LuminairePicker
{
    internal static LuminaireItem? Pick(Window? owner, LightingContext ctx, LuminaireItem? current) =>
        TreePicker.Pick(owner, "База данных оборудования — светильники", ctx.Catalog.Luminaires,
            l => [l.Section.Length > 0 ? l.Section : "Без раздела", l.Series.Length > 0 ? l.Series : "Без серии"], l => l.Name, Props, current,
            $"Светильники из каталога «Менеджер БД» (добавление и правка — там). Без собственной таблицы Ки расчёт идёт по типовой таблице КСС Д × КПД. Файл: {EomCatalog.DefaultPath}",
            ("Связать с семейством…", (w, l) => ctx.Link(w, l)));

    internal static IEnumerable<(string, string, string)> Props(LuminaireItem l)
    {
        const string A = "Общие параметры", B = "Технические данные", C = "Механические данные", D = "Параметры ламп";
        yield return (A, "Наименование (тип)", l.Name); yield return (A, "Раздел", l.Section); yield return (A, "Серия", l.Series);
        yield return (A, "Описание в спецификации", l.Description); yield return (A, "Нормативный документ", l.Document);
        yield return (A, "Код оборудования", l.Code); yield return (A, "Производитель", l.Manufacturer);
        yield return (A, "Семейство Revit", l.RevitType.Length > 0 ? l.RevitType : "не связано");
        yield return (B, "Номинальное напряжение, В", l.Voltage.ToString("0")); yield return (B, "Номинальная мощность, Вт", l.Power.ToString("0.#")); yield return (B, "Световой поток ламп, лм", l.Flux.ToString("0"));
        yield return (B, "Световой КПД, о.е.", l.Efficiency.ToString("0.##")); yield return (B, "Коэффициент мощности, о.е.", l.PowerFactor.ToString("0.##"));
        yield return (B, "Световая отдача, лм/Вт", l.Power > 0 ? (l.Flux * l.Efficiency / l.Power).ToString("0") : "");
        yield return (B, "КСС (файл фотометрии)", l.Photometry.Length > 0 ? l.Photometry : "—");
        yield return (B, "Таблица Ки", l.Utilization is { IsValid: true } t ? $"собственная, отражения {string.Join(", ", Enumerable.Range(0, t.Reflections.Count).Select(t.ReflectionText))}" : "типовая КСС Д × КПД");
        yield return (C, "Способ установки", l.Mounting); yield return (C, "Степень защиты IP", l.IP);
        yield return (C, "Масса, кг", l.MassKg > 0 ? l.MassKg.ToString("0.##") : "—");
        yield return (C, "Длина, мм", l.LengthMm > 0 ? l.LengthMm.ToString("0") : "—"); yield return (C, "Ширина, мм", l.WidthMm > 0 ? l.WidthMm.ToString("0") : "—");
        yield return (C, "Высота, мм", l.HeightMm > 0 ? l.HeightMm.ToString("0") : "—");
        yield return (D, "Тип ламп", l.LampType); yield return (D, "Количество ламп", l.LampCount.ToString());
    }
}

/// <summary>
/// «Расчёт освещённости методом Ки»: свойства помещения слева, справа — сравнение всех светильников БД
/// (необходимое и рекомендуемое количество, освещённость, i, Ки). «Поместить» расставляет выбранный светильник.
/// </summary>
internal static class KiWindow
{
    internal enum Outcome { None, Applied, Place }

    internal sealed class Line(KiResult r, double area, bool current)
    {
        public KiResult Result { get; } = r;
        public string Name => (current ? "● " : "") + Result.Luminaire.Name;
        public string Lamps => Result.Luminaire.LampType;
        public int LampCount => Result.Luminaire.LampCount;
        public string Flux => Result.Luminaire.Flux.ToString("0");
        public string Required => Result.Ok || Result.Required > 0 ? Result.Required.ToString() : "";
        public string ERequired => Result.Required > 0 ? Result.ERequired.ToString("0.0") : "";
        public string Recommended => Result.Recommended > 0 ? $"{Result.Recommended} ({Result.Rows}×{Result.Cols})" : "";
        public string ERecommended => Result.Recommended > 0 ? Result.ERecommended.ToString("0.0") : "";
        public string Index => Result.Index.ToString("0.00");
        public string Ki => (Result.Ki * 100).ToString("0.00");
        public string Specific => area > 0 ? (Result.Recommended * Result.Luminaire.Power / area).ToString("0.0") : "";
        public string Note => Result.Problem;
    }

    internal static Outcome Show(Window owner, LightRow row, LightingContext ctx)
    {
        var window = EomUi.Window($"Расчёт освещённости методом Ки — {row.Number} {row.Name}", 1420, 760); window.Owner = owner;
        var outcome = Outcome.None;
        var panel = new LightPanel(() => [row], ctx);

        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
        void Col(string header, string path, bool star = false)
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 120 },
                Binding = new Binding(path), Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto, ElementStyle = style
            });
        }
        Col("Светильник", nameof(Line.Name), true); Col("Тип ламп", nameof(Line.Lamps)); Col("Количество ламп", nameof(Line.LampCount)); Col("Поток ламп, лм", nameof(Line.Flux));
        Col("Необходимое количество светильников", nameof(Line.Required)); Col("Освещённость от необходимого количества, лк", nameof(Line.ERequired));
        Col("Рекомендуемое количество (рядов×в ряду)", nameof(Line.Recommended)); Col("Освещённость от рекомендуемого количества, лк", nameof(Line.ERecommended));
        Col("Индекс помещения", nameof(Line.Index)); Col("Ки, %", nameof(Line.Ki)); Col("Руд, Вт/м²", nameof(Line.Specific)); Col("Замечания", nameof(Line.Note));
        grid.Columns[0].MinWidth = 220;

        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 6) };
        void Recalc()
        {
            string keep = (grid.SelectedItem as Line)?.Result.Luminaire.Name ?? row.Light.Luminaire;
            var lines = ctx.Catalog.Luminaires.Select(l => new Line(Lighting.Calculate(row.Geometry, row.Light, l), row.Geometry.AreaM2, l.Name == row.Light.Luminaire)).ToList();
            grid.ItemsSource = lines;
            grid.SelectedItem = lines.FirstOrDefault(l => l.Result.Luminaire.Name == keep) ?? lines.FirstOrDefault();
            var g = row.Geometry;
            info.Text = $"Помещение {row.Number} «{row.Name}»: S = {g.AreaM2:0.00} м², P = {g.PerimeterM:0.00} м, габарит {g.SizeXM:0.00} × {g.SizeYM:0.00} м, высота {g.HeightM:0.00} м. " +
                $"Eн = {row.Light.Lux:0} лк, Kз = {row.Light.Maintenance:0.##}, ρп/ρс/ρр = {row.Light.Ceiling * 100:0}/{row.Light.Walls * 100:0}/{row.Light.Floor * 100:0}. " +
                (row.Light.FixedCount > 0 ? $"Расчёт освещённости при заданном количестве {row.Light.FixedCount} шт." : "Расчёт по требуемой освещённости.");
        }
        panel.Changed += () => { outcome = outcome == Outcome.None ? Outcome.Applied : outcome; Recalc(); };

        void Choose()
        {
            if (grid.SelectedItem is not Line line) throw new InvalidOperationException("Выберите светильник в таблице.");
            if (!line.Result.Ok && line.Result.Required == 0) throw new InvalidOperationException(line.Result.Problem);
            row.Light = row.Light with { Luminaire = line.Result.Luminaire.Name };
            row.Recalc(ctx);
        }

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new GroupBox { Header = "Свойства помещения", Content = panel.View, Padding = new Thickness(4) };
        body.Children.Add(left);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var right = new DockPanel();
        DockPanel.SetDock(info, Dock.Top); right.Children.Add(info); right.Children.Add(grid);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        grid.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: Line }) { Choose(); outcome = Outcome.Applied; window.DialogResult = true; } };

        EomUi.Layout(window, body,
            EomUi.Hint("Ки = ηп·КПД по типовой таблице КСС Д (или по таблице Ки светильника) для индекса i = 2S/(h·P) и ближайшего сочетания ρп/ρс/ρр. " +
                "N = Eн·S·Kз/(Φ·Ки); рекомендуемое — наименьшее количество, которое ложится ровной сеткой (шаги по осям отличаются не более чем вдвое)."),
            EomUi.Buttons(
                EomUi.Button("Пересчитать", Recalc),
                EomUi.Button("Применить светильник", () => { Choose(); outcome = Outcome.Applied; window.DialogResult = true; }),
                EomUi.Button("Поместить", () => { Choose(); if (row.PlaceCount == 0) throw new InvalidOperationException("Количество светильников равно нулю."); outcome = Outcome.Place; window.DialogResult = true; }, true),
                EomUi.Button("Закрыть", () => window.DialogResult = false)));
        window.Loaded += (_, _) => panel.Refresh();
        Recalc();
        window.ShowDialog();
        return outcome;
    }
}

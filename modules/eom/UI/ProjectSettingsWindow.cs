using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.Eom;

/// <summary>Настройки проекта ЭОМ: нормы, запасы, маски маркировки и проверка обозначений проводок ГОСТ 21.210.</summary>
internal static class ProjectSettingsWindow
{
    internal sealed record Result(EomSettings Settings, bool CreateLines);

    internal static Result? Edit(EomSettings current)
    {
        var window = EomUi.Window("Настройки проекта", 640, 640);
        var loads = new ComboBox { Width = 220, ItemsSource = EomSettings.LoadNorms, SelectedItem = current.LoadNorm };
        var lighting = new ComboBox { Width = 220, ItemsSource = EomSettings.LightingNorms, SelectedItem = current.LightingNorm };
        TextBox reserve = EomUi.Number(current.CableReserve), drop = EomUi.Number(current.VoltageDropLimit),
            phase = EomUi.Number(current.PhaseVoltage), line = EomUi.Number(current.LineVoltage);
        var group = new TextBox { Width = 220, Text = current.GroupMask };
        var cable = new TextBox { Width = 220, Text = current.CableMask };
        var create = new CheckBox { Content = "Проверить семейство обозначений проводок и подгрузить из открытого файла АДСК", IsChecked = !current.LinesCreated, Margin = new Thickness(0, 4, 0, 2) };
        var preview = EomUi.Hint("");
        void Preview() => preview.Text = $"Пример: группа «{EomSettings.Mark(group.Text, "ЩР1", "3")}», кабель «{EomSettings.Mark(cable.Text, "ЩР1", "3")}».";
        group.TextChanged += (_, _) => Preview(); cable.TextChanged += (_, _) => Preview(); Preview();

        var panel = new StackPanel();
        panel.Children.Add(EomUi.Heading("Нормы расчёта"));
        panel.Children.Add(EomUi.Row("Расчёт электрических нагрузок", loads));
        panel.Children.Add(EomUi.Row("Нормы освещённости", lighting));
        panel.Children.Add(EomUi.Heading("Сеть"));
        panel.Children.Add(EomUi.Row("Фазное напряжение", phase, "В"));
        panel.Children.Add(EomUi.Row("Линейное напряжение", line, "В"));
        panel.Children.Add(EomUi.Row("Допустимая потеря напряжения в группе", drop, "%"));
        panel.Children.Add(EomUi.Row("Запас длины кабеля на монтаж", reserve, "%"));
        panel.Children.Add(EomUi.Heading("Маркировка"));
        panel.Children.Add(EomUi.Hint("{Щит} — имя щита, {N} — номер группы."));
        panel.Children.Add(EomUi.Row("Маска группы", group));
        panel.Children.Add(EomUi.Row("Маска кабеля", cable));
        panel.Children.Add(preview);
        panel.Children.Add(EomUi.Heading("Обозначения проводок (ГОСТ 21.210)"));
        panel.Children.Add(EomUi.Hint("Вдоль лотков и коробов на плане ставится линейный элемент узла «ADSK_Обозначение проводок» из шаблона АДСК ЭОМ: на лотке, в коробе, в трубе, открыто, под плинтусом и т.д. Если в проекте его нет, он копируется из другого открытого файла АДСК."));
        panel.Children.Add(create);

        Result? result = null;
        EomUi.Layout(window, new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, null, EomUi.Buttons(
            EomUi.Button("По умолчанию", () => { var d = new EomSettings(); loads.SelectedItem = d.LoadNorm; lighting.SelectedItem = d.LightingNorm; reserve.Text = d.CableReserve.ToString(); drop.Text = d.VoltageDropLimit.ToString(); phase.Text = d.PhaseVoltage.ToString(); line.Text = d.LineVoltage.ToString(); group.Text = d.GroupMask; cable.Text = d.CableMask; }),
            EomUi.Button("Сохранить", () =>
            {
                var settings = current with
                {
                    LoadNorm = (string)loads.SelectedItem, LightingNorm = (string)lighting.SelectedItem,
                    CableReserve = EomUi.Parse(reserve, "Запас длины"), VoltageDropLimit = EomUi.Parse(drop, "Потеря напряжения"),
                    PhaseVoltage = EomUi.Parse(phase, "Фазное напряжение"), LineVoltage = EomUi.Parse(line, "Линейное напряжение"),
                    GroupMask = group.Text.Trim(), CableMask = cable.Text.Trim()
                };
                settings.Validate();
                result = new(settings, create.IsChecked == true);
                window.DialogResult = true;
            }, true),
            EomUi.Button("Отмена", () => window.DialogResult = false)));
        return window.ShowDialog() == true ? result : null;
    }
}

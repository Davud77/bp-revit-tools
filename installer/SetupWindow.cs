using System.Diagnostics;
namespace BotPlus.RT.Setup;

/// <summary>Пошаговый мастер в стиле стандартных установщиков Windows: приветствие, лицензия, сведения, Revit, Tapir, установка, завершение.</summary>
sealed class SetupWindow : Form
{
    sealed record Page(string Title, string Subtitle, Control Body, bool Welcome = false, Action? Enter = null, Func<bool>? Leave = null);

    static readonly Color Footer = Color.FromArgb(240, 240, 240), Line = Color.FromArgb(223, 223, 223), Side = Color.FromArgb(233, 241, 249);
    readonly bool uninstall;
    readonly List<Page> pages = [];
    readonly Panel header = new() { Dock = DockStyle.Top, Height = 64, BackColor = Color.White };
    readonly Label headerTitle = new() { AutoSize = false, Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(24, 8), Size = new Size(490, 20), BackColor = Color.Transparent };
    readonly Label headerText = new() { AutoSize = false, Location = new Point(44, 28), Size = new Size(480, 32), BackColor = Color.Transparent };
    readonly Panel body = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    readonly Panel footer = new() { Dock = DockStyle.Bottom, Height = 56, BackColor = Footer };
    readonly Button back = new() { Text = "Назад", Size = new Size(96, 28) };
    readonly Button next = new() { Text = "Далее", Size = new Size(96, 28) };
    readonly Button cancel = new() { Text = "Отмена", Size = new Size(96, 28) };
    int index;
    bool busy, finished, failed;
    string failure = "";

    // Страница «Revit».
    readonly TextBox revit = new();
    // Страница «Tapir».
    readonly RadioButton tapirInstall = new() { Text = "Установить Tapir для Archicad:", AutoSize = true };
    readonly RadioButton tapirSkip = new() { Text = "Не устанавливать Tapir", AutoSize = true };
    readonly ComboBox archicadVersion = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    readonly TextBox archicadPath = new();
    readonly Button archicadBrowse = new() { Text = "Обзор…", Size = new Size(88, 25) };
    readonly Label tapirState = new() { AutoSize = false };
    IReadOnlyList<ArchicadInstall> detected = [];
    // Остальные страницы.
    readonly Label summary = new() { AutoSize = false };
    readonly Label stepText = new() { AutoSize = false };
    readonly Label fileText = new() { AutoSize = false, AutoEllipsis = true };
    readonly ProgressBar progressBar = new() { Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
    readonly Label finishTitle = new() { AutoSize = false, Font = new Font("Segoe UI", 15, FontStyle.Bold) };
    readonly Label finishText = new() { AutoSize = false };
    readonly LinkLabel logLink = new() { Text = "Открыть журнал установки", AutoSize = true };

    public SetupWindow(bool uninstall)
    {
        this.uninstall = uninstall;
        Text = (uninstall ? "Удаление" : "Установка") + " — BotPlus RT";
        AutoScaleMode = AutoScaleMode.Font; Font = new Font("Segoe UI", 9);
        ClientSize = new Size(600, 440); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BackColor = Color.White;

        using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        header.Controls.Add(new PictureBox { Image = new Icon(icon, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(540, 10), BackColor = Color.Transparent });
        header.Controls.Add(headerTitle); header.Controls.Add(headerText);
        header.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Line), 0, header.Height - 1, header.Width, header.Height - 1);
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Line), 0, 0, footer.Width, 0);
        cancel.Location = new Point(492, 14); next.Location = new Point(388, 14); back.Location = new Point(284, 14);
        footer.Controls.Add(back); footer.Controls.Add(next); footer.Controls.Add(cancel);
        Controls.Add(body); Controls.Add(header); Controls.Add(footer);
        AcceptButton = next;

        BuildPages();
        back.Click += (_, _) => Go(index - 1);
        next.Click += (_, _) => Advance();
        cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (busy) { e.Cancel = true; return; }
            if (!finished && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, uninstall ? "Прервать удаление BotPlus RT?" : "Прервать установку BotPlus RT?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
        };
        Deployment.Logged += AppendLog; FormClosed += (_, _) => Deployment.Logged -= AppendLog;
        Shown += (_, _) => Deployment.Log(uninstall ? "Открыт мастер удаления BotPlus RT" : "Открыт мастер установки BotPlus RT " + Deployment.Version);
        Go(0);
    }

    static Label Text_(string text, int x, int y, int w, int h) => new() { Text = text, Location = new Point(x, y), Size = new Size(w, h), AutoSize = false };

    void BuildPages()
    {
        // 1. Приветствие.
        var welcome = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var sidePanel = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Side };
        using (var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!)
            sidePanel.Controls.Add(new PictureBox { Image = new Icon(icon, 96, 96).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(96, 96), Location = new Point(52, 140), BackColor = Color.Transparent });
        welcome.Controls.Add(sidePanel);
        welcome.Controls.Add(new Label { Text = uninstall ? "Удаление BotPlus RT" : "Вас приветствует Мастер установки BotPlus RT", Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(224, 30), Size = new Size(350, 90), AutoSize = false });
        welcome.Controls.Add(Text_(uninstall
            ? "Мастер удалит надстройку BotPlus RT из Revit 2026. Ваши модели, снимки и настройки сохранятся.\r\n\r\nЗакройте Revit и AI-клиент, затем нажмите «Удалить»."
            : $"Программа установит BotPlus RT, версия {Deployment.Version}, на ваш компьютер.\r\n\r\nВкладка BotPlus RT в Revit 2026: MCP, Ac2Revit, ОВ, Отверстия и ЭОМ. Node.js, Python и библиотеки IFC включены.\r\n\r\nРекомендуется закрыть Revit и прочие приложения перед тем, как продолжить.\r\n\r\nНажмите «Далее», чтобы продолжить, или «Отмена», чтобы выйти из программы установки.", 224, 130, 350, 230));
        pages.Add(new Page("", "", welcome, true));
        if (uninstall) { BuildProgressAndFinish(); return; }

        // 2. Лицензия.
        var license = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 10, 24, 0) };
        license.Controls.Add(Text_("Прочтите условия лицензионного соглашения. Вы должны принять их, чтобы продолжить.", 24, 8, 550, 32));
        string licenseText; try { licenseText = Deployment.ReadLicense().Replace("\r\n", "\n").Replace("\n", "\r\n"); } catch (Exception) { licenseText = "GNU Affero General Public License v3.0\r\nhttps://www.gnu.org/licenses/agpl-3.0.html"; }
        var licenseBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Location = new Point(24, 40), Size = new Size(552, 206), Text = licenseText, Font = new Font("Segoe UI", 9) };
        var accept = new RadioButton { Text = "Я принимаю условия соглашения", Location = new Point(24, 256), AutoSize = true };
        var decline = new RadioButton { Text = "Я не принимаю условия соглашения", Location = new Point(24, 282), AutoSize = true, Checked = true };
        accept.CheckedChanged += (_, _) => next.Enabled = accept.Checked;
        license.Controls.Add(licenseBox); license.Controls.Add(accept); license.Controls.Add(decline);
        pages.Add(new Page("Лицензионное соглашение", "Пожалуйста, прочтите следующую важную информацию перед тем, как продолжить.", license, Enter: () => next.Enabled = accept.Checked));

        // 3. Информация.
        var info = new Panel { Dock = DockStyle.Fill };
        info.Controls.Add(Text_("Когда вы будете готовы продолжить установку, нажмите «Далее».", 24, 8, 550, 24));
        info.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, BackColor = Color.White, ScrollBars = ScrollBars.Vertical, Location = new Point(24, 40), Size = new Size(552, 250),
            Text = $"BotPlus RT {Deployment.Version} для Autodesk Revit 2026.\r\n\r\n" +
                   "Установка выполняется для текущего пользователя, права администратора нужны только для записи Tapir в папку Archicad.\r\n\r\n" +
                   "Перед установкой сохраните проекты и закройте Revit и AI-клиент (Codex, Claude), использующий MCP.\r\n\r\n" +
                   "Единая вкладка BotPlus RT: MCP, Ac2Revit, ОВ, Отверстия, Марки и ЭОМ. Подключение AI-клиентов и автоподтверждение настраиваются на ленте Revit. Тема окон и состав панелей — в «Настройках».\r\n\r\n" +
                   "Ваши модели, снимки и настройки при удалении сохраняются.\r\n\r\n" +
                   "Исходный код и AGPLv3: https://github.com/Davud77/bp-revit-tools"
        });
        pages.Add(new Page("Информация", "Пожалуйста, прочтите следующую важную информацию перед тем, как продолжить.", info));

        // 4. Revit.
        var revitPage = new Panel { Dock = DockStyle.Fill };
        revit.Text = Deployment.FindRevit() ?? @"C:\Program Files\Autodesk\Revit 2026";
        revit.Location = new Point(24, 78); revit.Size = new Size(460, 25);
        var revitBrowse = new Button { Text = "Обзор…", Location = new Point(490, 77), Size = new Size(88, 25) };
        revitBrowse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = revit.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) revit.Text = dialog.SelectedPath; };
        revitPage.Controls.Add(Text_("Надстройка будет установлена для Autodesk Revit 2026. Укажите папку Revit, если она не определена автоматически, затем нажмите «Далее».", 24, 8, 550, 48));
        revitPage.Controls.Add(Text_("Папка Revit 2026:", 24, 58, 300, 18));
        revitPage.Controls.Add(revit); revitPage.Controls.Add(revitBrowse);
        revitPage.Controls.Add(Text_("Приложение BotPlus RT копируется в %LOCALAPPDATA%\\BotPlus.RT, в папку Revit файлы не записываются.", 24, 120, 550, 40));
        pages.Add(new Page("Выбор папки Revit", "В какой папке установлен Autodesk Revit 2026?", revitPage, Leave: ValidateRevit));

        // 5. Tapir.
        BuildTapirPage();

        // 6. Готовность.
        var ready = new Panel { Dock = DockStyle.Fill };
        ready.Controls.Add(Text_("Нажмите «Установить», чтобы начать установку, или «Назад», чтобы изменить параметры.", 24, 8, 550, 40));
        summary.Location = new Point(24, 56); summary.Size = new Size(550, 200); ready.Controls.Add(summary);
        pages.Add(new Page("Всё готово к установке", "Программа установки готова начать установку BotPlus RT на ваш компьютер.", ready, Enter: () => { next.Text = "Установить"; summary.Text = Summary(); }));

        BuildProgressAndFinish();
    }

    void BuildTapirPage()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        page.Controls.Add(Text_("Tapir — дополнение Archicad, которое нужно для переноса модели Archicad → Revit (Ac2Revit). Если перенос из Archicad не нужен, установку Tapir можно пропустить.", 24, 4, 550, 44));
        tapirState.Location = new Point(24, 52); tapirState.Size = new Size(550, 36); tapirState.Font = new Font("Segoe UI", 9, FontStyle.Bold); page.Controls.Add(tapirState);
        tapirInstall.Location = new Point(24, 98); page.Controls.Add(tapirInstall);
        archicadVersion.Location = new Point(44, 124); page.Controls.Add(archicadVersion);
        page.Controls.Add(Text_("Папка Archicad:", 44, 158, 300, 18));
        archicadPath.Location = new Point(44, 178); archicadPath.Size = new Size(440, 25); page.Controls.Add(archicadPath);
        archicadBrowse.Location = new Point(490, 177); page.Controls.Add(archicadBrowse);
        tapirSkip.Location = new Point(24, 222); page.Controls.Add(tapirSkip);
        page.Controls.Add(Text_("Перед установкой Tapir закройте Archicad. Для записи в Program Files Windows может запросить права администратора. Tapir остаётся в Archicad при удалении BotPlus RT.", 24, 252, 550, 56));

        detected = Deployment.DetectArchicad();
        var shown = detected.Select(a => a.Version).Concat(Deployment.TapirVersions).Distinct().ToList();
        foreach (int version in shown)
        {
            var known = detected.FirstOrDefault(a => a.Version == version);
            archicadVersion.Items.Add(new VersionItem(version, known is null ? "Archicad " + version : "Archicad " + version + (known.Running ? " — найден, запущен" : " — найден")));
        }
        void Pick()
        {
            if (archicadVersion.SelectedItem is not VersionItem item) return;
            var known = detected.FirstOrDefault(a => a.Version == item.Version);
            archicadPath.Text = known?.Directory ?? Deployment.FindArchicad(item.Version) ?? "";
        }
        archicadVersion.SelectedIndexChanged += (_, _) => Pick();
        archicadBrowse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = archicadPath.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) archicadPath.Text = dialog.SelectedPath; };
        void Sync() { bool on = tapirInstall.Checked; archicadVersion.Enabled = archicadPath.Enabled = archicadBrowse.Enabled = on; }
        tapirInstall.CheckedChanged += (_, _) => Sync();
        if (detected.Count > 0)
        {
            var best = detected[0];
            archicadVersion.SelectedIndex = shown.IndexOf(best.Version);
            tapirState.Text = detected.Count == 1
                ? $"Найден Archicad {best.Version}{(best.Running ? " (запущен)" : "")}: {best.Directory}"
                : "Найдено версий Archicad: " + detected.Count + ". Выбрана " + (best.Running ? "запущенная " : "новейшая ") + best.Version + ".";
            tapirInstall.Checked = true;
        }
        else
        {
            archicadVersion.SelectedIndex = shown.IndexOf(28);
            tapirState.Text = "Установленный Archicad не найден. Можно указать его папку вручную или пропустить Tapir.";
            tapirSkip.Checked = true;
        }
        Sync();
        pages.Add(new Page("Установка Tapir", "Настройка переноса Archicad → Revit.", page, Enter: () => { }, Leave: ValidateTapir));
    }

    sealed record VersionItem(int Version, string Label) { public override string ToString() => Label; }

    void BuildProgressAndFinish()
    {
        var progress = new Panel { Dock = DockStyle.Fill };
        progress.Controls.Add(Text_(uninstall ? "Пожалуйста, подождите, пока BotPlus RT удаляется с вашего компьютера." : "Пожалуйста, подождите, пока BotPlus RT установится на ваш компьютер.", 24, 8, 550, 24));
        stepText.Location = new Point(24, 52); stepText.Size = new Size(550, 20); progress.Controls.Add(stepText);
        fileText.Location = new Point(24, 74); fileText.Size = new Size(550, 20); progress.Controls.Add(fileText);
        progressBar.Location = new Point(24, 104); progressBar.Size = new Size(552, 22); progress.Controls.Add(progressBar);
        pages.Add(new Page(uninstall ? "Удаление" : "Установка", uninstall ? "Идёт удаление BotPlus RT." : "Идёт установка BotPlus RT.", progress, Enter: Run));

        var finish = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var side = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Side };
        using (var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!)
            side.Controls.Add(new PictureBox { Image = new Icon(icon, 96, 96).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(96, 96), Location = new Point(52, 140), BackColor = Color.Transparent });
        finish.Controls.Add(side);
        finishTitle.Location = new Point(224, 30); finishTitle.Size = new Size(350, 90); finishText.Location = new Point(224, 130); finishText.Size = new Size(350, 190);
        logLink.Location = new Point(224, 330);
        logLink.LinkClicked += (_, _) =>
        {
            if (!File.Exists(Deployment.LogPath)) return;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(Deployment.LogPath); Process.Start(start);
        };
        finish.Controls.Add(finishTitle); finish.Controls.Add(finishText); finish.Controls.Add(logLink);
        pages.Add(new Page("", "", finish, true, Enter: ShowResult));
    }

    string Summary()
    {
        string tapir = tapirInstall.Checked && archicadVersion.SelectedItem is VersionItem item ? $"будет установлен для Archicad {item.Version}\r\n    {archicadPath.Text}" : "не устанавливается";
        return $"Папка Revit 2026:\r\n    {revit.Text}\r\n\r\nПриложение:\r\n    %LOCALAPPDATA%\\BotPlus.RT\r\n\r\nTapir:\r\n    {tapir}";
    }

    bool ValidateRevit()
    {
        string path = revit.Text.Trim();
        if (File.Exists(Path.Combine(path, "Revit.exe")) && File.Exists(Path.Combine(path, "RevitAPI.dll"))) return true;
        MessageBox.Show(this, "В указанной папке не найден Revit 2026 (Revit.exe и RevitAPI.dll). Выберите папку установки Revit 2026.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    bool ValidateTapir()
    {
        if (!tapirInstall.Checked) return true;
        try { if (archicadVersion.SelectedItem is VersionItem item) Deployment.TapirTarget(item.Version, archicadPath.Text.Trim().Length == 0 ? null : archicadPath.Text.Trim()); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
    }

    void Go(int target)
    {
        index = Math.Clamp(target, 0, pages.Count - 1);
        var page = pages[index];
        body.Controls.Clear(); body.Controls.Add(page.Body);
        header.Visible = !page.Welcome; headerTitle.Text = page.Title; headerText.Text = page.Subtitle;
        bool last = index == pages.Count - 1, running = page.Enter == (Action)Run;
        back.Visible = index > 0 && !last && !running; back.Enabled = !busy;
        next.Text = last ? "Завершить" : index == 0 && uninstall ? "Удалить" : "Далее"; next.Enabled = !running; cancel.Visible = !last; cancel.Enabled = !running;
        page.Enter?.Invoke();
        AcceptButton = next;
    }

    void Advance()
    {
        if (index == pages.Count - 1) { finished = true; Close(); return; }
        if (pages[index].Leave is { } leave && !leave()) return;
        Go(index + 1);
    }

    async void Run()
    {
        busy = true; failed = false; progressBar.Value = 0; stepText.Text = "Подготовка…"; fileText.Text = "";
        string revitPath = revit.Text.Trim();
        int? version = !uninstall && tapirInstall.Checked && archicadVersion.SelectedItem is VersionItem item ? item.Version : null;
        string? archicad = version is null || archicadPath.Text.Trim().Length == 0 ? null : archicadPath.Text.Trim();
        var progress = new Progress<SetupProgress>(step =>
        {
            progressBar.Value = Math.Clamp(step.Percent, 0, 100);
            int split = step.Message.IndexOf(" — ", StringComparison.Ordinal);
            if (step.Message.StartsWith("Файлы:") && split > 0) { stepText.Text = step.Message[..split]; fileText.Text = step.Message[(split + 3)..]; }
            else { stepText.Text = step.Message; fileText.Text = ""; }
        });
        try { await Task.Run(() => { if (uninstall) Deployment.Uninstall(progress); else Deployment.Install(revitPath, progress, version, archicad); }); progressBar.Value = 100; }
        catch (Exception ex) { failed = true; failure = ex.Message; Deployment.Log("ОШИБКА: " + ex.Message); }
        finally { busy = false; }
        Go(pages.Count - 1);
    }

    void ShowResult()
    {
        finishTitle.Text = failed ? (uninstall ? "Удаление не завершено" : "Установка не завершена") : (uninstall ? "Удаление BotPlus RT завершено" : "Завершение работы Мастера установки BotPlus RT");
        finishTitle.ForeColor = failed ? Color.DarkRed : Color.Black;
        finishText.Text = failed ? failure + "\r\n\r\nИсправьте причину и запустите установщик ещё раз. Подробности — в журнале."
            : uninstall ? "Надстройка удалена. Ваши модели, снимки и настройки сохранены."
            : "BotPlus RT установлен. Запустите Revit 2026 — вкладка BotPlus RT появится на ленте.\r\n\r\nНажмите «Завершить», чтобы выйти из программы установки.";
        logLink.Visible = true; cancel.Visible = false; next.Text = "Завершить"; next.Enabled = true; back.Visible = false;
    }

    void AppendLog(string line) { }
}

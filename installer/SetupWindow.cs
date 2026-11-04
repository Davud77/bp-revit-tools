using System.Diagnostics;
namespace BotPlus.RT.Setup;
sealed class SetupWindow : Form
{
    readonly TextBox revit = new() { Dock = DockStyle.Fill };
    readonly ComboBox archicadVersion = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly TextBox archicad = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(650, 0), Text = "Готов к установке" };
    readonly Button action = new() { Text = "Установить", AutoSize = true };
    readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    readonly TextBox journal = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
    bool busy;
    public SetupWindow(bool uninstall)
    {
        Text = "BotPlus RT — " + (uninstall ? "удаление" : "установка");
        ClientSize = new Size(730, 730); MinimumSize = new Size(660, 670); StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 12 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 12; i++) layout.RowStyles.Add(new RowStyle(i == 9 ? SizeType.Percent : SizeType.AutoSize, i == 9 ? 100 : 0));
        layout.Controls.Add(new Label { Text = "BotPlus RT " + Deployment.Version, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = uninstall
            ? "Удаление надстройки. Ваши модели, снимки и настройки сохранятся. Закройте Revit и AI-клиент."
            : "Единая вкладка BotPlus RT: MCP, Ac2Revit, ОВ и Отверстия. Node.js, Python и библиотеки IFC включены. Подключение AI-клиентов и автоподтверждение настраиваются на ленте Revit.", Margin = new Padding(0, 8, 0, 12) }, 0, 1);
        layout.Controls.Add(new Label { Text = "Каталог Revit 2026:", AutoSize = true }, 0, 2);
        revit.Text = Deployment.FindRevit() ?? @"C:\Program Files\Autodesk\Revit 2026";
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        var browse = new Button { Text = "Обзор…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = revit.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) revit.Text = dialog.SelectedPath; };
        pathRow.Controls.Add(revit, 0, 0); pathRow.Controls.Add(browse, 1, 0); layout.Controls.Add(pathRow, 0, 3);
        var acChoice = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        acChoice.Controls.Add(new Label { Text = "Установить Tapir для:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        archicadVersion.Items.Add("Не устанавливать Tapir");
        foreach(int version in Deployment.TapirVersions) archicadVersion.Items.Add("Archicad " + version);
        acChoice.Controls.Add(archicadVersion); layout.Controls.Add(acChoice, 0, 4);
        var acPath = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        acPath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); acPath.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        var acBrowse = new Button { Text = "Обзор…", Dock = DockStyle.Fill };
        acBrowse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = archicad.Text }; if(dialog.ShowDialog(this)==DialogResult.OK) archicad.Text=dialog.SelectedPath; };
        acPath.Controls.Add(archicad,0,0);acPath.Controls.Add(acBrowse,1,0);layout.Controls.Add(acPath,0,5);
        archicadVersion.SelectedIndexChanged += (_, _) => { int index=archicadVersion.SelectedIndex; archicad.Enabled=acBrowse.Enabled=index>0; archicad.Text=index>0 ? Deployment.FindArchicad(Deployment.TapirVersions[index-1]) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Graphisoft","Archicad "+Deployment.TapirVersions[index-1]) : ""; };
        archicadVersion.SelectedIndex=Array.IndexOf(Deployment.TapirVersions,28)+1;
        layout.Controls.Add(status, 0, 6); progressBar.Margin = new Padding(0, 8, 0, 8); layout.Controls.Add(progressBar, 0, 7);
        layout.Controls.Add(new Label { Text = "Журнал установки", AutoSize = true }, 0, 8); layout.Controls.Add(journal, 0, 9);
        layout.Controls.Add(new Label { Text = "Лог: " + Deployment.LogPath, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 8) }, 0, 10);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Закрыть", AutoSize = true }; close.Click += (_, _) => Close();
        var license = new Button { Text = "Лицензия", AutoSize = true };
        license.Click += (_, _) => { using var reader = new Form { Text = "GNU AGPL v3", Size = new Size(700, 600) }; reader.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = Deployment.ReadLicense() }); reader.ShowDialog(this); };
        var openLog = new Button { Text = "Открыть лог", AutoSize = true };
        openLog.Click += (_, _) =>
        {
            if (!File.Exists(Deployment.LogPath)) return;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(Deployment.LogPath); Process.Start(start);
        };
        buttons.Controls.Add(close); buttons.Controls.Add(action); buttons.Controls.Add(openLog); buttons.Controls.Add(license); layout.Controls.Add(buttons, 0, 11);
        if (uninstall) { archicadVersion.Enabled=archicad.Enabled=acBrowse.Enabled=revit.Enabled=browse.Enabled=false; action.Text = "Удалить"; status.Text = "Готов к удалению"; }
        FormClosing += (_, e) => { if (busy) e.Cancel = true; }; Deployment.Logged += AppendLog;
        FormClosed += (_, _) => Deployment.Logged -= AppendLog;
        Shown += (_, _) => Deployment.Log(uninstall ? "Открыто окно удаления BotPlus RT" : "Открыто окно установки BotPlus RT " + Deployment.Version);
        action.Click += async (_, _) =>
        {
            busy = true; action.Enabled = close.Enabled = browse.Enabled = revit.Enabled = archicadVersion.Enabled = archicad.Enabled = acBrowse.Enabled = false; progressBar.Value = 0;
            status.ForeColor = SystemColors.ControlText; status.Text = "Выполняется…"; string revitPath = revit.Text;
            int? selectedVersion=archicadVersion.SelectedIndex>0 ? Deployment.TapirVersions[archicadVersion.SelectedIndex-1] : null; string acDirectory=archicad.Text;
            var progress = new Progress<SetupProgress>(step => { progressBar.Value = Math.Clamp(step.Percent, 0, 100); status.Text = step.Message; });
            try
            {
                await Task.Run(() => { if (uninstall) Deployment.Uninstall(progress); else Deployment.Install(revitPath, progress, selectedVersion, acDirectory); });
                progressBar.Value = 100; status.ForeColor = Color.DarkGreen;
                status.Text = uninstall ? "Надстройка удалена. Данные сохранены." : "Установка завершена. Запустите Revit 2026.";
            }
            catch (Exception ex) { status.ForeColor = Color.DarkRed; status.Text = ex.Message; action.Enabled = true; Deployment.Log("ОШИБКА: " + ex.Message); }
            finally { busy = false; close.Enabled = true; if (!uninstall) { browse.Enabled = revit.Enabled = archicadVersion.Enabled = true; archicad.Enabled=acBrowse.Enabled=archicadVersion.SelectedIndex>0; } }
        };
        Controls.Add(layout);
    }
    void AppendLog(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(line))); return; }
        journal.AppendText(line + Environment.NewLine); journal.SelectionStart = journal.TextLength; journal.ScrollToCaret();
    }
}

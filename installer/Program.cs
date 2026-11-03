using System.Diagnostics;

namespace BotPlus.RT.Setup;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool silent = args.Contains("--silent");
        bool uninstall = args.Contains("--uninstall") || args.Contains("--uninstall-worker");
        try
        {
            var tapirWorker = args.FirstOrDefault(a => a.StartsWith("--tapir-worker="));
            if (tapirWorker is not null)
            {
                Deployment.InstallTapir(int.Parse(tapirWorker[15..]), args.FirstOrDefault(a => a.StartsWith("--archicad="))?[11..], false);
                return 0;
            }
            var verify = args.FirstOrDefault(a => a.StartsWith("--verify-payload="));
            if (verify is not null)
            {
                Deployment.VerifyPayload(verify[17..]);
                return 0;
            }
            if (args.Contains("--uninstall"))
            {
                // Windows cannot remove a running executable. Run outside the owned directory.
                var temp = Path.Combine(Path.GetTempPath(), "BotPlusRT-Uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                var runner = Path.Combine(temp, "Uninstall.exe");
                File.Copy(Environment.ProcessPath!, runner);
                var start = new ProcessStartInfo(runner) { UseShellExecute = false };
                start.ArgumentList.Add("--uninstall-worker");
                start.ArgumentList.Add("--parent=" + Environment.ProcessId);
                if (silent) start.ArgumentList.Add("--silent");
                Process.Start(start);
                return 0;
            }
            var parentArg = args.FirstOrDefault(a => a.StartsWith("--parent="));
            if (parentArg is not null && int.TryParse(parentArg[9..], out var parent))
            {
                try { Process.GetProcessById(parent).WaitForExit(15000); }
                catch (ArgumentException) { }
            }
            if (silent)
            {
                if (uninstall) Deployment.Uninstall();
                else Deployment.Install(args.FirstOrDefault(a => a.StartsWith("--revit="))?[8..], null,
                    args.Contains("--no-tapir") ? null : int.Parse(args.FirstOrDefault(a => a.StartsWith("--tapir="))?[8..] ?? "28"),
                    args.FirstOrDefault(a => a.StartsWith("--archicad="))?[11..]);
            }
            else
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new SetupWindow(uninstall));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Deployment.Log("ERROR: " + ex.Message);
            if (!silent) MessageBox.Show(ex.Message, "BotPlus RT", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (args.Contains("--uninstall-worker")) ScheduleRunnerCleanup();
        }
    }

    static void ScheduleRunnerCleanup()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(dir).StartsWith("BotPlusRT-Uninstall-")) return;
        var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{dir.Replace("'", "''")}' -Recurse -Force";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        Process.Start(start);
    }
}

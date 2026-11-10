using Autodesk.Revit.UI;

var assembly = typeof(BotPlus.RT.Application).Assembly.Location;
foreach (var file in new[] { "modules/codex-mcp/plugin/BP.Revit.AI.dll", "modules/ac2revit/dist/Revit2026/Ac2Revit.dll", "modules/ov/HeatLossViewer.dll", "modules/openings/BotPlus.Openings.dll" })
{
    var target = Path.Combine(Path.GetDirectoryName(assembly)!, file);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(assembly, target, true);
}
var app = new UIControlledApplication();
var host = new BotPlus.RT.Application();
if (host.OnStartup(app) != Result.Succeeded) throw new Exception("Startup failed");
host.OnShutdown(app);
if (!State.Calls.SequenceEqual(new[] { "start:mcp", "start:ac2revit", "start:ov", "start:openings", "stop:openings", "stop:ov", "stop:ac2revit", "stop:mcp" }))
    throw new Exception("Incorrect module order");
Console.WriteLine("PASS host loads modules in ribbon order and shuts down in reverse order");
State.Calls.Clear(); State.FailAc2Revit = true;
if (host.OnStartup(app) != Result.Failed) throw new Exception("Failure not propagated");
host.OnShutdown(app); // Cleanup must be idempotent after a failed startup.
if (!State.Calls.SequenceEqual(new[] { "start:mcp", "start:ac2revit", "stop:ac2revit", "stop:mcp" }) || State.Errors.Count != 1)
    throw new Exception("Partial startup did not clean up correctly");
Console.WriteLine("PASS partial startup failure cleans up handlers and reports the failure once");

State.Calls.Clear();
var configuredApp = new UIControlledApplication();
BotPlus.Shared.BotPlusSettings.HiddenPanels.UnionWith(["MCP", "Ac2Revit", "Отверстия", "Марки"]);
if (host.OnStartup(configuredApp) != Result.Succeeded) throw new Exception("Disabled module started");
host.OnShutdown(configuredApp);
if (!State.Calls.SequenceEqual(new[] { "start:ov", "stop:ov" }) || !configuredApp.Panels.SequenceEqual(new[] { "Настройки" }))
    throw new Exception("Disabled modules were started or the settings panel is missing");
Console.WriteLine("PASS host skips disabled modules and keeps the settings panel");

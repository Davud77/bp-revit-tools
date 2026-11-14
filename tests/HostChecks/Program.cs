using Autodesk.Revit.UI;

var assembly = typeof(BotPlus.RT.Application).Assembly.Location;
foreach (var file in new[] { "modules/codex-mcp/plugin/BP.Revit.AI.dll", "modules/ac2revit/dist/Revit2026/Ac2Revit.dll", "modules/ov/HeatLossViewer.dll", "modules/openings/BotPlus.Openings.dll", "modules/eom/BotPlus.Eom.dll" })
{
    var target = Path.Combine(Path.GetDirectoryName(assembly)!, file);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(assembly, target, true);
}
var app = new UIControlledApplication();
var host = new BotPlus.RT.Application();
if (host.OnStartup(app) != Result.Succeeded) throw new Exception("Startup failed");
host.OnShutdown(app);
if (!State.Calls.SequenceEqual(new[] { "start:mcp", "start:ac2revit", "start:ov", "start:openings", "start:eom", "stop:eom", "stop:openings", "stop:ov", "stop:ac2revit", "stop:mcp" }))
    throw new Exception("Incorrect module order");
Console.WriteLine("PASS host loads modules in ribbon order and shuts down in reverse order");
if(BotPlus.RT.TagCopyOptions.Current!=BotPlus.RT.TagCopyScope.Family)throw new Exception("Tag copying must default to family");
if(BotPlus.RT.TagCopyOptions.Labels.Count!=3||BotPlus.RT.TagCopyOptions.Current!=BotPlus.RT.TagCopyScope.Family)throw new Exception("Incorrect tag scope selector");
BotPlus.RT.TagCopyOptions.SelectedIndex=1;
if(BotPlus.RT.TagCopyOptions.Current!=BotPlus.RT.TagCopyScope.Category)throw new Exception("Category scope not selected");
BotPlus.RT.TagCopyOptions.SelectedIndex=2;
if(BotPlus.RT.TagCopyOptions.Current!=BotPlus.RT.TagCopyScope.All)throw new Exception("All scope not selected");
BotPlus.RT.TagCopyOptions.SelectedIndex=-1;
if(BotPlus.RT.TagCopyOptions.Current!=BotPlus.RT.TagCopyScope.Family)throw new Exception("Invalid scope must fall back to family");
BotPlus.RT.TagCopyOptions.SelectedIndex=2;BotPlus.RT.TagCopyOptions.Reset();
if(BotPlus.RT.TagCopyOptions.Current!=BotPlus.RT.TagCopyScope.Family)throw new Exception("Tag scope did not reset to family");
Console.WriteLine("PASS tag copy selector defaults to family, switches scopes and resets on startup");
var normal=(X:1/Math.Sqrt(3),Y:1/Math.Sqrt(3),Z:1/Math.Sqrt(3));
var right=(X:1/Math.Sqrt(2),Y:-1/Math.Sqrt(2),Z:0.0);
var up=(X:1/Math.Sqrt(6),Y:1/Math.Sqrt(6),Z:-2/Math.Sqrt(6));
var depthOnly=(normal.X*1000,normal.Y*1000,normal.Z*1000);
if(BotPlus.RT.TagViewGeometry.Distance(depthOnly,right,up)>1e-9)throw new Exception("Annotation depth treated as visible movement");
var moved=(depthOnly.Item1+right.X*3+up.X*4,depthOnly.Item2+right.Y*3+up.Y*4,depthOnly.Item3+right.Z*3+up.Z*4);
if(Math.Abs(BotPlus.RT.TagViewGeometry.Distance(moved,right,up)-5)>1e-9)throw new Exception("Axonometric visible movement not detected");
if(BotPlus.RT.TagViewGeometry.Distance((0,0,1000),(1,0,0),(0,1,0))!=0)throw new Exception("Plan depth not ignored");
Console.WriteLine("PASS tag projection ignores depth and detects visible movement on axonometric views and plans");
State.Calls.Clear(); State.FailAc2Revit = true;
if (host.OnStartup(app) != Result.Failed) throw new Exception("Failure not propagated");
host.OnShutdown(app); // Cleanup must be idempotent after a failed startup.
if (!State.Calls.SequenceEqual(new[] { "start:mcp", "start:ac2revit", "stop:ac2revit", "stop:mcp" }) || State.Errors.Count != 1)
    throw new Exception("Partial startup did not clean up correctly");
Console.WriteLine("PASS partial startup failure cleans up handlers and reports the failure once");

State.Calls.Clear();
var configuredApp = new UIControlledApplication();
BotPlus.Shared.BotPlusSettings.HiddenPanels.UnionWith(["MCP", "Ac2Revit", "Отверстия", "ВК", "ЭОМ", "Марки", "Спецификации"]);
if (host.OnStartup(configuredApp) != Result.Succeeded) throw new Exception("Disabled module started");
host.OnShutdown(configuredApp);
if (!State.Calls.SequenceEqual(new[] { "start:ov", "stop:ov" }) || !configuredApp.Panels.SequenceEqual(new[] { "Настройки" }))
    throw new Exception("Disabled modules were started or the settings panel is missing");
Console.WriteLine("PASS host skips disabled modules and keeps the settings panel");

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using HeatLossViewer.Mep;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var reports = new List<string>(); EngineeringStartupRefresh.Report = reports.Add;
var app = new UIControlledApplication(); var ui = new UIApplication();
var alreadyOpen = new Document { Title = "Already open" }; ui.Application.Documents.Add(alreadyOpen);
EngineeringStartupRefresh.Attach(app);
app.Idle(ui);
Check(alreadyOpen.Calls == 1 && alreadyOpen.Commits == 1 && alreadyOpen.Settings.ConditionalGroups.Count == 19,
    "Already-open project gets defaults and one initial refresh");
Check(alreadyOpen.Settings.ConditionalGroups.All(g => g.IsActive) && alreadyOpen.Settings.SystemNames.Enabled, "Every default rule enabled at startup");
app.Idle(ui); Check(alreadyOpen.Calls == 1, "No repeat refresh on every idle tick");
var opened = new Document { Title = "Opened" }; app.Open(opened); app.Open(opened);
app.Idle(ui); Check(opened.Calls == 1, "Repeated open notifications deduplicated");
var busy = new Document { Title = "Busy", IsModifiable = true }; app.Create(busy); app.Idle(ui);
Check(busy.Calls == 0, "Busy document waits until ready");
busy.IsModifiable = false; app.Idle(ui); Check(busy.Calls == 1, "Created project refreshed after transaction ends");
var unchanged = new Document { Title = "Unchanged", ChangedElements = 0 }; unchanged.Settings.PrepareDefaults(); app.Open(unchanged); app.Idle(ui);
Check(unchanged.Calls == 1 && unchanged.Rollbacks == 1 && unchanged.Commits == 0 && unchanged.Writes == 0, "Unchanged project produces no settings write or empty commit");
var disabled = new Document { Title = "User disabled" }; disabled.Settings.PrepareDefaults();
disabled.Settings.ConditionalGroups[0].Otherwise.NameEnabled = false;
var deleted = disabled.Settings.ConditionalGroups[0]; disabled.Settings.ConditionalGroups.RemoveAt(0);
app.Open(disabled); app.Idle(ui);
Check(!disabled.Settings.ConditionalGroups.Contains(deleted) && disabled.Settings.ConditionalGroups.Count == 18, "Startup preserves later deletion");
foreach (var excluded in new[] { new Document { IsReadOnly = true }, new Document { IsFamilyDocument = true }, new Document { IsLinked = true }, new Document { IsValidObject = false } })
{ app.Open(excluded); app.Idle(ui); Check(excluded.Calls == 0 && excluded.Commits == 0, "Read-only, family, linked and closed docs skipped"); }
var failed = new Document { Title = "Commit rejected", RejectCommit = true };
app.Open(failed); app.Idle(ui);
Check(reports.Any(r => r.Contains("Ошибка автоматического обновления")) && !EngineeringUpdater.Suspended && failed.Commits == 0, "Commit failure reported and updater suspension restored");
var pending = new Document(); app.Open(pending); EngineeringStartupRefresh.Detach(app); app.Idle(ui);
Check(pending.Calls == 0, "Shutdown detaches handlers and clears pending work");
EngineeringStartupRefresh.Attach(app); app.Idle(ui); EngineeringStartupRefresh.Detach(app);
Check(alreadyOpen.Calls == 2, "Next startup refreshes open projects again");
Console.WriteLine("PASS engineering startup: open/create, already-open, dedup, deferred writes, unchanged rollback, excluded documents, failure and shutdown");

namespace Autodesk.Revit.DB
{
    public sealed class Document
    {
        public bool IsValidObject { get; set; } = true;
        public bool IsFamilyDocument { get; set; }
        public bool IsLinked { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsModifiable { get; set; }
        public string Title { get; set; } = "Project";
        public EngineeringRuleSet Settings { get; set; } = new();
        public int Calls, Commits, Rollbacks, Writes, ChangedElements = 2;
        public bool RejectCommit;
    }
    public enum TransactionStatus { Started, Committed, RolledBack }
    public sealed class Transaction(Document doc, string name) : IDisposable
    {
        EngineeringRuleSet? previous;
        public TransactionStatus Start() { ArgumentException.ThrowIfNullOrWhiteSpace(name); previous = EngineeringRuleSet.FromJson(doc.Settings.ToJson()); return TransactionStatus.Started; }
        public TransactionStatus Commit() { if (doc.RejectCommit) { RollBack(); return TransactionStatus.RolledBack; } doc.Commits++; previous = null; return TransactionStatus.Committed; }
        public void RollBack() { if (previous != null) doc.Settings = previous; previous = null; doc.Rollbacks++; }
        public void Dispose() { if (previous != null) RollBack(); }
    }
}
namespace Autodesk.Revit.DB.Events
{
    public sealed class DocumentOpenedEventArgs(Document doc) : EventArgs { public Document Document => doc; }
    public sealed class DocumentCreatedEventArgs(Document doc) : EventArgs { public Document Document => doc; }
}
namespace Autodesk.Revit.UI.Events { public sealed class IdlingEventArgs : EventArgs; }
namespace Autodesk.Revit.UI
{
    public sealed class ControlledApplication
    {
        public event EventHandler<Autodesk.Revit.DB.Events.DocumentOpenedEventArgs>? DocumentOpened;
        public event EventHandler<Autodesk.Revit.DB.Events.DocumentCreatedEventArgs>? DocumentCreated;
        public void Open(Document doc) => DocumentOpened?.Invoke(this, new(doc));
        public void Create(Document doc) => DocumentCreated?.Invoke(this, new(doc));
    }
    public sealed class UIControlledApplication
    {
        public ControlledApplication ControlledApplication { get; } = new();
        public event EventHandler<Events.IdlingEventArgs>? Idling;
        public void Idle(UIApplication ui) => Idling?.Invoke(ui, new());
        public void Open(Document doc) => ControlledApplication.Open(doc);
        public void Create(Document doc) => ControlledApplication.Create(doc);
    }
    public sealed class UIApplication { public Application Application { get; } = new(); }
    public sealed class Application { public List<Document> Documents { get; } = []; }
}
namespace HeatLossViewer.Mep
{
    internal static class EngineeringRulesStore
    {
        internal static EngineeringRuleSet Read(Document doc) => doc.Settings;
        internal static void Write(Document doc, EngineeringRuleSet set) { set.ValidateConditionalGroups(); doc.Settings = set; doc.Writes++; }
    }
    internal sealed class EngineeringOutcome { public int Updated; public string Summary() => $"Updated: {Updated}"; }
    internal static class EngineeringEngine
    {
        internal static EngineeringOutcome ApplyAll(Document doc, EngineeringRuleSet set) { doc.Calls++; return new() { Updated = doc.ChangedElements }; }
    }
    internal static class EngineeringUpdater { internal static bool Suspended; }
}

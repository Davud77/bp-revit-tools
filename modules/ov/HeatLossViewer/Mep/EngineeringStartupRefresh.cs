using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace HeatLossViewer.Mep;

/// <summary>Queue project opens; write only from Idling, after Revit finishes opening the document.</summary>
internal static class EngineeringStartupRefresh
{
    static readonly HashSet<Document> pending = [];
    static bool inspectOpenDocuments;
    internal static Action<string> Report { get; set; } = Log;

    internal static void Attach(UIControlledApplication app)
    {
        inspectOpenDocuments = true;
        app.ControlledApplication.DocumentOpened += OnOpened;
        app.ControlledApplication.DocumentCreated += OnCreated;
        app.Idling += OnIdling;
    }

    internal static void Detach(UIControlledApplication app)
    {
        app.ControlledApplication.DocumentOpened -= OnOpened;
        app.ControlledApplication.DocumentCreated -= OnCreated;
        app.Idling -= OnIdling;
        pending.Clear(); inspectOpenDocuments = false;
    }

    static void OnOpened(object? sender, DocumentOpenedEventArgs e) => pending.Add(e.Document);
    static void OnCreated(object? sender, DocumentCreatedEventArgs e) => pending.Add(e.Document);

    static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication ui) return;
        if (inspectOpenDocuments)
        {
            inspectOpenDocuments = false;
            foreach (Document doc in ui.Application.Documents) pending.Add(doc);
        }
        foreach (var doc in pending.ToList())
        {
            if (!doc.IsValidObject || doc.IsFamilyDocument || doc.IsLinked || doc.IsReadOnly) { pending.Remove(doc); continue; }
            if (doc.IsModifiable) continue;
            pending.Remove(doc);
            try
            {
                var stored = EngineeringRulesStore.Read(doc);
                var rules = EngineeringRuleSet.FromJson(stored.ToJson());
                rules.PrepareDefaults();
                bool settingsChanged = rules.ToJson() != stored.ToJson();
                using var transaction = new Transaction(doc, "BotPlus: автоматическое обновление инженерных параметров");
                if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Не удалось начать транзакцию.");
                if (settingsChanged) EngineeringRulesStore.Write(doc, rules);
                var outcome = EngineeringEngine.ApplyAll(doc, rules);
                if (!settingsChanged && outcome.Updated == 0) transaction.RollBack();
                else
                {
                    bool suspended = EngineeringUpdater.Suspended;
                    EngineeringUpdater.Suspended = true;
                    try
                    {
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил автоматическое обновление.");
                    }
                    finally { EngineeringUpdater.Suspended = suspended; }
                }
                Report(doc.Title + ": " + outcome.Summary());
            }
            catch (Exception error) { Report("Ошибка автоматического обновления: " + error); }
        }
    }

    static void Log(string message)
    {
        Trace.WriteLine("BotPlus updater: " + message);
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus.RT");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "updater-startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception error) { Trace.WriteLine(error); }
    }
}

using Autodesk.Revit.UI;

namespace BotPlus.Eom;

/// <summary>Выполняет действия немодальных окон (панель ЭТМ) в контексте Revit API через ExternalEvent.</summary>
internal sealed class RevitQueue : IExternalEventHandler, IDisposable
{
    readonly Queue<Action<UIApplication>> actions = new();
    readonly ExternalEvent external;
    internal RevitQueue() => external = ExternalEvent.Create(this);

    internal void Post(Action<UIApplication> action) { lock (actions) actions.Enqueue(action); external.Raise(); }

    public void Execute(UIApplication app)
    {
        while (true)
        {
            Action<UIApplication> action;
            lock (actions) { if (actions.Count == 0) return; action = actions.Dequeue(); }
            try { action(app); }
            catch (Exception error) { TaskDialog.Show("BotPlus ЭОМ", error.Message); }
        }
    }
    public string GetName() => "BotPlus ЭОМ";
    public void Dispose() => external.Dispose();
}

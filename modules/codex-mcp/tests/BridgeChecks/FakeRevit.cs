using BP.Revit.AI;
using System.Text.Json;

namespace Autodesk.Revit.UI.Events { public sealed class IdlingEventArgs : EventArgs; }
namespace Autodesk.Revit.UI
{
    public sealed class UIApplication;
    public enum ExternalEventRequest { Accepted, Pending, Denied }
    public interface IExternalEventHandler { void Execute(UIApplication app); string GetName(); }
    public sealed class ExternalEvent : IDisposable
    {
        public static int Raises;
        public static ExternalEventRequest NextResult = ExternalEventRequest.Accepted;
        public static ExternalEvent Create(IExternalEventHandler handler) => new();
        public ExternalEventRequest Raise() { Interlocked.Increment(ref Raises); return NextResult; }
        public void Dispose() { }
    }
}
namespace BP.Revit.AI
{
    internal static class JsonTools
    {
        public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);
    }
    internal static class RevitTools
    {
        public static readonly List<string> Executed = [];
        public static BridgeResponse Execute(Autodesk.Revit.UI.UIApplication app, BridgeRequest request, Func<bool> cancelled)
        {
            if (cancelled()) throw new Exception("Cancelled request reached fake executor.");
            Executed.Add(request.Id);
            if (request.Tool == "slow") Thread.Sleep(110);
            if (request.Tool == "throw") throw new InvalidOperationException("Injected executor failure");
            return BridgeResponse.Success(request.Id, new { name = "Проект" });
        }
    }
}

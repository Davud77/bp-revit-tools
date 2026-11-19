using BP.Revit.AI;
using Autodesk.Revit.UI;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
async Task Reject<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new Exception(message);
}
PendingRequest Request(string id, TimeSpan? timeout = null) => new(new BridgeRequest { Id = id }, timeout ?? TimeSpan.FromMinutes(1));

// Fragmenting one byte at a time splits every Cyrillic UTF-8 sequence.
var utf8 = Encoding.UTF8.GetBytes("{\"name\":\"Стена\"}\n");
using (var stream = new FragmentStream(utf8))
    Check(await BridgeFrame.ReadAsync(stream, utf8.Length, default) == "{\"name\":\"Стена\"}", "UTF-8 fragments must round trip at the exact byte limit");
await Reject<InvalidDataException>(() => BridgeFrame.ReadAsync(new MemoryStream(utf8), utf8.Length - 1, default), "Enforce UTF-8 bytes including delimiter");
await Reject<IOException>(() => BridgeFrame.ReadAsync(new MemoryStream(utf8[..^1]), utf8.Length, default), "Reject incomplete frames");
await Reject<DecoderFallbackException>(() => BridgeFrame.ReadAsync(new MemoryStream([0xff, 10]), 10, default), "Reject malformed UTF-8");
using (var stream = new EndlessStream())
{
    await Reject<InvalidDataException>(() => BridgeFrame.ReadAsync(stream, 4096, default), "Never retain an unbounded newline-free input");
    Check(stream.BytesRead <= 8192, "Oversize input must stop after a bounded read");
}
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    await Reject<OperationCanceledException>(() => BridgeFrame.ReadAsync(new FragmentStream(utf8), 100, cts.Token), "Respect read cancellation");
}

var queue = new BridgeRequestQueue(2);
var a = Request("a"); var b = Request("b"); var c = Request("c");
Check(queue.TryEnqueue(a) && queue.TryEnqueue(b) && !queue.TryEnqueue(c), "Bound admission and reject overflow");
a.Cancel("disconnected");
Check(queue.TryEnqueue(c), "Reclaim cancelled slots without waiting for Revit");
Check(queue.TryDequeue(out var next) && next == b, "Preserve FIFO after reclaiming slots");
Check(queue.TryDequeue(out next) && next == c && !queue.TryDequeue(out _), "Never execute cancelled work");
Check(!(await a.Completion).Ok, "Cancellation must wake waiters");
var expired = Request("expired", TimeSpan.FromMilliseconds(-1));
Check(!queue.TryEnqueue(expired), "Expired requests cannot enter queue");
var stopped = Request("stopped"); queue.TryEnqueue(stopped); queue.Stop();
Check(!(await stopped.Completion).Ok && !queue.TryEnqueue(Request("late")), "Stop drains waiters and rejects admission");
var completed = Request("done"); completed.Complete(BridgeResponse.Success("done", 1)); completed.Cancel("too late");
Check((await completed.Completion).Ok, "Disconnect after completion must not overwrite a committed result");
var parallelQueue = new BridgeRequestQueue(32);
var admitted = 0;
Parallel.For(0, 1000, i => { if (parallelQueue.TryEnqueue(Request(i.ToString()))) Interlocked.Increment(ref admitted); });
Check(admitted == 32, "Concurrent producers must not exceed queue capacity"); parallelQueue.Stop();

// Exercise the real TCP service, authentication and scheduler against a fake API
// boundary. An ephemeral port and injected synthetic token never touch live Revit.
const string token = "synthetic-bridge-checks-token-not-a-user-secret";
async Task WaitFor(Func<bool> condition)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!condition()) await Task.Delay(5, cts.Token);
}
async Task<(TcpClient Client, StreamReader Reader)> Send(BridgeService bridge, string id, string suppliedToken = token, string tool = "read")
{
    var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", bridge.ListeningPort);
    var stream = client.GetStream();
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, token = suppliedToken, tool, arguments = new { } }) + "\n");
    await stream.WriteAsync(bytes);
    return (client, new StreamReader(stream, Encoding.UTF8));
}
async Task<JsonElement> Response(StreamReader reader)
{
    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    using var json = JsonDocument.Parse(line!); return json.RootElement.Clone();
}
var bridge = new BridgeService(0, token);
var clients = new List<TcpClient>();
try
{
    var bad = await Send(bridge, "bad", "wrong"); clients.Add(bad.Client);
    Check(!(await Response(bad.Reader)).GetProperty("ok").GetBoolean() && RevitTools.Executed.Count == 0, "Authentication failure cannot reach executor");
    var initialRaises = Volatile.Read(ref ExternalEvent.Raises);
    var readers = new List<StreamReader>();
    for (var i = 0; i < 32; i++)
    {
        var sent = await Send(bridge, "queued-" + i); clients.Add(sent.Client); readers.Add(sent.Reader);
        await WaitFor(() => Volatile.Read(ref ExternalEvent.Raises) >= initialRaises + i + 1);
    }
    var overflow = await Send(bridge, "overflow"); clients.Add(overflow.Client);
    var overflowResult = await Response(overflow.Reader);
    Check(!overflowResult.GetProperty("ok").GetBoolean() && overflowResult.GetProperty("error").GetString()!.Contains("queue is full"), "TCP overflow is a correlated failure before execution");
    bridge.ProcessPending(new UIApplication());
    Check(RevitTools.Executed.Count == 4, "One API event must process at most four operations");
    var afterPortion = Volatile.Read(ref ExternalEvent.Raises);
    bridge.SchedulePending();
    Check(Volatile.Read(ref ExternalEvent.Raises) == afterPortion + 1, "Idle must schedule the remaining portion");
    for (var i = 0; i < 4; i++)
    {
        var result = await Response(readers[i]);
        Check(result.GetProperty("id").GetString() == "queued-" + i && result.GetProperty("ok").GetBoolean(), "Preserve correlation and FIFO over TCP");
    }
    while (RevitTools.Executed.Count < 32) bridge.ProcessPending(new UIApplication());
    for (var i = 4; i < 32; i++) Check((await Response(readers[i])).GetProperty("ok").GetBoolean(), "All admitted work must complete");
    var raises = Volatile.Read(ref ExternalEvent.Raises);
    var disconnected = await Send(bridge, "disconnected"); disconnected.Client.Dispose();
    await WaitFor(() => Volatile.Read(ref ExternalEvent.Raises) > raises);
    await Task.Delay(250); bridge.ProcessPending(new UIApplication());
    Check(!RevitTools.Executed.Contains("disconnected"), "Disconnected queued work must not execute");
    ExternalEvent.NextResult = ExternalEventRequest.Denied;
    var denied = await Send(bridge, "denied"); clients.Add(denied.Client);
    Check(!(await Response(denied.Reader)).GetProperty("ok").GetBoolean(), "Denied API events must wake callers");
    ExternalEvent.NextResult = ExternalEventRequest.Accepted;
    raises = Volatile.Read(ref ExternalEvent.Raises);
    var failure = await Send(bridge, "failure", tool: "throw"); clients.Add(failure.Client);
    await WaitFor(() => Volatile.Read(ref ExternalEvent.Raises) > raises);
    bridge.ProcessPending(new UIApplication());
    Check(!(await Response(failure.Reader)).GetProperty("ok").GetBoolean(), "Executor exceptions must wake callers with failure");
    Check(!RevitTools.Executed.Contains("denied"), "Denied work stays cancelled after a later event");
    raises = Volatile.Read(ref ExternalEvent.Raises);
    var slow = await Send(bridge, "slow", tool: "slow"); clients.Add(slow.Client);
    await WaitFor(() => Volatile.Read(ref ExternalEvent.Raises) > raises);
    raises = Volatile.Read(ref ExternalEvent.Raises);
    var afterSlow = await Send(bridge, "after-slow"); clients.Add(afterSlow.Client);
    await WaitFor(() => Volatile.Read(ref ExternalEvent.Raises) > raises);
    bridge.ProcessPending(new UIApplication());
    Check((await Response(slow.Reader)).GetProperty("ok").GetBoolean() && !RevitTools.Executed.Contains("after-slow"), "Long atomic API operation must yield before starting another one");
    bridge.SchedulePending(); bridge.ProcessPending(new UIApplication());
    Check((await Response(afterSlow.Reader)).GetProperty("ok").GetBoolean(), "The next portion must resume after a slow operation");
}
finally
{
    bridge.Shutdown(); foreach (var client in clients) client.Dispose();
}
// Stop must release sockets still reading a frame and waiting for API execution;
// a subsequent service instance owns a fresh queue.
var stopping = new BridgeService(0, token);
var beforeStop = Volatile.Read(ref ExternalEvent.Raises);
var queuedAtStop = await Send(stopping, "queued-at-stop");
await WaitFor(() => Volatile.Read(ref ExternalEvent.Raises) > beforeStop);
using var unframed = new TcpClient(); await unframed.ConnectAsync("127.0.0.1", stopping.ListeningPort);
stopping.Shutdown();
try { Check(await queuedAtStop.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null, "Stop closes queued connection"); }
catch (IOException) { checks++; }
queuedAtStop.Client.Dispose();
var restarted = new BridgeService(0, token);
try
{
    restarted.ProcessPending(new UIApplication());
    Check(!RevitTools.Executed.Contains("queued-at-stop"), "Restart must not replay the previous instance's pending work");
}
finally { restarted.Shutdown(); }
Console.WriteLine($"PASS: {checks} MCP framing, admission, cancellation and TCP service checks (fake Revit boundary)");

sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}
sealed class EndlessStream : Stream
{
    public int BytesRead;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { buffer.Span.Fill((byte)'x'); BytesRead += buffer.Length; return ValueTask.FromResult(buffer.Length); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

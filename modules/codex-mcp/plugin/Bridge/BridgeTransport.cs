using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BP.Revit.AI;

// This layer deliberately has no dependency on Revit: framing, deadlines and queue
// admission are tested with real streams and concurrent producers outside the host.
internal static class BridgeFrame
{
    public static async Task<string> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var frame = new MemoryStream();
        var buffer = new byte[Math.Min(4096, maxBytes)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new IOException("Bridge request ended before the newline delimiter.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = newline >= 0 ? newline : count;
            // Include the delimiter, just as the Node client does. Enforce bytes,
            // not UTF-16 characters, before retaining the next block.
            if (frame.Length + length + 1 > maxBytes)
                throw new InvalidDataException("Request exceeds the 1 MB limit.");
            frame.Write(buffer, 0, length);
            if (newline < 0) continue;
            return new UTF8Encoding(false, true).GetString(frame.GetBuffer(), 0, (int)frame.Length).TrimEnd('\r');
        }
    }
}

internal sealed class BridgeRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; init; }
}

internal sealed class BridgeResponse
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    public static BridgeResponse Success(string id, object result) => new() { Id = id, Ok = true, Result = result };
    public static BridgeResponse Failure(string id, string error) => new() { Id = id, Ok = false, Error = error };
}

internal sealed class PendingRequest(BridgeRequest request, TimeSpan timeout)
{
    private readonly TaskCompletionSource<BridgeResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    private int _cancelled;
    public BridgeRequest Request { get; } = request;
    public Task<BridgeResponse> Completion => _completion.Task;
    public bool Cancelled => Volatile.Read(ref _cancelled) != 0 || Environment.TickCount64 >= _deadline;
    public void Complete(BridgeResponse response) => _completion.TrySetResult(response);
    public void Cancel(string reason)
    {
        Interlocked.Exchange(ref _cancelled, 1);
        Complete(BridgeResponse.Failure(Request.Id, reason));
    }
}

internal sealed class BridgeRequestQueue(int capacity)
{
    private readonly object _sync = new();
    private readonly Queue<PendingRequest> _requests = new();
    private bool _stopped;
    public bool HasPending { get { lock (_sync) return _requests.Count != 0; } }
    public bool TryEnqueue(PendingRequest request)
    {
        lock (_sync)
        {
            if (_stopped || request.Cancelled) return false;
            // Disconnected clients must not occupy admission slots until Revit
            // returns from a modal dialog. Keep the remaining requests in FIFO order.
            if (_requests.Count >= capacity)
            {
                var count = _requests.Count;
                while (count-- > 0)
                {
                    var item = _requests.Dequeue();
                    if (item.Cancelled) item.Cancel("Request cancelled before execution.");
                    else _requests.Enqueue(item);
                }
            }
            if (_requests.Count >= capacity) return false;
            _requests.Enqueue(request);
            return true;
        }
    }
    public bool TryDequeue(out PendingRequest? request)
    {
        lock (_sync)
        {
            while (_requests.TryDequeue(out var next))
            {
                if (next.Cancelled) { next.Cancel("Request cancelled before execution."); continue; }
                request = next;
                return true;
            }
            request = null;
            return false;
        }
    }
    public void Stop()
    {
        lock (_sync)
        {
            _stopped = true;
            while (_requests.TryDequeue(out var request)) request.Cancel("Revit bridge stopped before execution.");
        }
    }
}

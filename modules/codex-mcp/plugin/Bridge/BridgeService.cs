using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BP.Revit.AI;

internal sealed class BridgeService
{
    public const int Port = 37651;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxClients = 64;
    private readonly BridgeRequestQueue _queue = new(32);
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private static BridgeService? _instance;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _stopToken;
    private readonly ExternalEvent _externalEvent;
    private readonly string _token;

    internal BridgeService(int port = Port, string? token = null)
    {
        _stopToken = _cancellation.Token;
        _token = token ?? LoadOrCreateToken();
        _externalEvent = ExternalEvent.Create(new RevitRequestHandler(this));
        _listener = new TcpListener(IPAddress.Loopback, port);
        try { _listener.Start(8); }
        catch
        {
            _listener.Stop();
            _externalEvent.Dispose();
            _cancellation.Dispose();
            throw;
        }
        _ = Task.Run(AcceptLoopAsync);
    }

    public static void Start()
    {
        if (_instance is not null) return;
        _instance = new BridgeService();
    }

    public static void Stop()
    {
        var instance = _instance;
        _instance = null;
        if (instance is null) return;
        instance.Shutdown();
    }

    internal int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal void Shutdown()
    {
        _cancellation.Cancel();
        _queue.Stop();
        _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        _externalEvent.Dispose();
        _cancellation.Dispose();
    }

    private static string LoadOrCreateToken()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BP.Revit.AI");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bridge.token");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, token, new UTF8Encoding(false));
        return token;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stopToken);
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (endpoint is null || !IPAddress.IsLoopback(endpoint.Address) || _clients.Count >= MaxClients)
                {
                    client.Dispose();
                    continue;
                }
                _clients.TryAdd(client, 0);
                _ = HandleClientAsync(client, _stopToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_stopToken.IsCancellationRequested) { break; }
            catch { await Task.Delay(250); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        // Stop may dispose a client between Accept and GetStream. Own cleanup
        // outside stream construction as well as inside the normal exchange.
        try { await ExchangeClientAsync(client, cancellationToken); }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private async Task ExchangeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (var stream = client.GetStream())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            PendingRequest? pending = null;
            var requestId = "";
            async Task SendAsync(BridgeResponse response)
            {
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonTools.SerializerOptions).AsMemory(), sendTimeout.Token);
            }
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await BridgeFrame.ReadAsync(stream, MaxRequestBytes, readTimeout.Token);
                if (string.IsNullOrWhiteSpace(line))
                {
                    await SendAsync(BridgeResponse.Failure("", "Request is empty."));
                    return;
                }

                BridgeRequest? request;
                try { request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonTools.SerializerOptions); }
                catch (JsonException exception)
                {
                    await SendAsync(BridgeResponse.Failure("", $"Invalid JSON request: {exception.Message}"));
                    return;
                }

                if (request is null || !TokensMatch(request.Token, _token))
                {
                    await SendAsync(BridgeResponse.Failure(request?.Id ?? "", "Authentication failed."));
                    return;
                }
                requestId = request.Id;
                if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128
                    || string.IsNullOrWhiteSpace(request.Tool) || request.Tool.Length > 128
                    || request.Arguments.ValueKind != JsonValueKind.Object)
                {
                    await SendAsync(BridgeResponse.Failure(request.Id, "Invalid request id, tool or arguments object."));
                    return;
                }
                pending = new PendingRequest(request, RequestTimeout);
                if (!_queue.TryEnqueue(pending))
                {
                    await SendAsync(BridgeResponse.Failure(request.Id, "Revit request queue is full or stopped. This request was not executed."));
                    return;
                }
                var raised = _externalEvent.Raise();
                if (raised == ExternalEventRequest.Denied)
                {
                    pending.Cancel("Revit is not accepting external API events.");
                    await SendAsync(await pending.Completion);
                    return;
                }

                using var poller = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (!pending.Completion.IsCompleted)
                {
                    if (pending.Cancelled)
                    {
                        pending.Cancel("Revit did not respond within 5 minutes. Check the model before repeating a write; its outcome may be unknown.");
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0)
                    {
                        pending.Cancel("MCP client disconnected. Check the model before repeating a write.");
                        return;
                    }
                    // Completion wakes immediately; the timer only checks disconnects.
                    // Keep one outstanding timer wait, rather than blocking a worker.
                    var tick = poller.WaitForNextTickAsync(cancellationToken).AsTask();
                    await Task.WhenAny(pending.Completion, tick);
                    if (!pending.Completion.IsCompleted) await tick;
                }
                await SendAsync(await pending.Completion);
            }
            catch (OperationCanceledException) { client.Dispose(); }
            catch (IOException) { }
            catch (Exception exception)
            {
                pending?.Cancel("Bridge failed before returning a result. Check the model before repeating a write.");
                try { await SendAsync(BridgeResponse.Failure(requestId, exception.Message)); }
                catch { }
            }
            finally
            {
                pending?.Cancel("Bridge connection ended. Check the model before repeating a write.");
            }
        }
    }

    private static bool TokensMatch(string supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }

    public static void OnIdling(object? sender, IdlingEventArgs args)
    {
        _instance?.SchedulePending();
    }

    internal void SchedulePending()
    {
        if (_queue.HasPending && !_stopToken.IsCancellationRequested) _externalEvent.Raise();
    }

    public void ProcessPending(UIApplication application)
    {
        var elapsed = Stopwatch.StartNew();
        var processed = 0;
        // Yield between operations. An individual Revit API call remains atomic and
        // cannot be preempted; Idling schedules the next bounded portion.
        while (!_stopToken.IsCancellationRequested && processed < 4 && elapsed.ElapsedMilliseconds < 100
            && _queue.TryDequeue(out var pending))
        {
            processed++;
            try
            {
                if (pending!.Cancelled)
                {
                    pending.Cancel("The MCP client cancelled the request before Revit started it.");
                    continue;
                }
                pending.Complete(RevitTools.Execute(application, pending.Request, () => pending.Cancelled || _stopToken.IsCancellationRequested));
            }
            catch (Exception exception)
            {
                pending!.Complete(BridgeResponse.Failure(pending.Request.Id, exception.Message));
            }
        }
    }
}

internal sealed class RevitRequestHandler(BridgeService service) : IExternalEventHandler
{
    public void Execute(UIApplication application) => service.ProcessPending(application);
    public string GetName() => "BP.Revit.AI local MCP bridge";
}

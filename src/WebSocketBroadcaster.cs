using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StepSolve;

/// <summary>
/// Manages WebSocket connections and broadcasts solve/status/log messages to all clients.
/// Singleton — registered in DI, injected into StepSolveService to push events.
/// </summary>
public sealed class WebSocketBroadcaster
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Startup timestamp for uptime calculation
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    public int ClientCount => _clients.Count;

    /// <summary>
    /// Accept and hold a WebSocket connection, reading messages until disconnect.
    /// </summary>
    public async Task HandleAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        _clients.TryAdd(id, ws);
        try
        {
            // Keep connection alive — read to detect close
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var recv = await ws.ReceiveAsync(buffer, ct);
                if (recv.MessageType == WebSocketMessageType.Close)
                    break;
                // Client messages ignored — server-push only
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Broadcast a solve result to all connected clients.
    /// </summary>
    public Task BroadcastSolve(SolveResult result, bool hasImage = false) =>
        Broadcast(new
        {
            type = "solve",
            ra = result.RaDeg,
            dec = result.DecDeg,
            confidence = result.Confidence,
            solver = result.SolverName,
            solveTimeMs = result.SolveTime.TotalMilliseconds,
            timestamp = DateTimeOffset.UtcNow,
            imageUrl = hasImage ? "/solve/image" : (string?)null,
        });

    /// <summary>
    /// Broadcast current status to all connected clients.
    /// </summary>
    public Task BroadcastStatus(string mode, string state, OnStepClient? onstep = null) =>
        Broadcast(new
        {
            type = "status",
            mode,
            state,
            uptime = (int)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
            onstep = onstep != null ? new
            {
                lastSyncTimestamp = onstep.LastSyncTime != default ? onstep.LastSyncTime : (DateTimeOffset?)null,
                lastSyncResult = onstep.LastSyncResult,
            } : null,
        });

    /// <summary>
    /// Broadcast a newly captured calibrate frame so the dashboard refreshes the image preview.
    /// </summary>
    public Task BroadcastImage(string imageUrl) =>
        Broadcast(new
        {
            type = "image",
            imageUrl,
        });

    /// <summary>
    /// Broadcast a log entry to all connected clients.
    /// </summary>
    public Task BroadcastLog(string level, string message) =>
        Broadcast(new
        {
            type = "log",
            level,
            message,
            timestamp = DateTimeOffset.UtcNow,
        });

    private async Task Broadcast(object message)
    {
        if (_clients.IsEmpty) return;

        var json = JsonSerializer.Serialize(message, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var deadIds = new List<string>();

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                deadIds.Add(id);
                continue;
            }

            try
            {
                await ws.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch
            {
                deadIds.Add(id);
            }
        }

        foreach (var id in deadIds)
            _clients.TryRemove(id, out _);
    }
}

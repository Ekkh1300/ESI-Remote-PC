using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Esi.Platform.Capture;
using Esi.Platform.Input;
using Esi.Platform.Machine;
using Esi.Platform.Session;

namespace Esi.Service;

/// <summary>
/// Remote control status and input routing.
/// <para>
/// The service itself never injects input or captures the screen. It validates the request,
/// checks scope and rate limits, and forwards to the per-user session agent, which is the
/// only process that can touch the interactive desktop. When no agent is connected the
/// API says so explicitly rather than accepting the event and silently dropping it.
/// </para>
/// </summary>
public sealed class RemoteService
{
    private readonly ILogger<RemoteService> _log;
    private readonly ISessionAgentBridge _sessionAgent;
    private readonly GdiScreenCaptureProvider _capture;
    private readonly AuditLog _audit;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _activeStreams = new(StringComparer.Ordinal);

    public RemoteService(
        ILogger<RemoteService> log,
        ISessionAgentBridge sessionAgent,
        SessionAgentBridge concreteBridge,
        GdiScreenCaptureProvider capture,
        AuditLog audit)
    {
        _log = log;
        _sessionAgent = sessionAgent;
        _capture = capture;
        _audit = audit;

        // The concrete bridge is needed rather than the interface because the frame relay is
        // an event, and events are not part of the abstraction the endpoints depend on.
        concreteBridge.FrameReceived += (_, frame) => FrameReceived?.Invoke(this, frame);
    }

    private ISessionAgentBridge sessionAgent => _sessionAgent;
    private GdiScreenCaptureProvider capture => _capture;
    private ILogger<RemoteService> log => _log;
    private AuditLog audit => _audit;

    /// <summary>
    /// Raised for every captured frame, on the session agent's pump thread. Subscribers must
    /// not block: the pipe is newline-delimited JSON, so a slow consumer stalls the whole
    /// capture pipeline.
    /// </summary>
    public event EventHandler<SessionAgentFrame>? FrameReceived;

    public RemoteSessionResponse GetStatus()
    {
        var displays = capture.EnumerateDisplays();

        return new RemoteSessionResponse
        {
            Available = sessionAgent.IsConnected,
            SessionAgentState = sessionAgent.State.ToString(),
            Active = _activeStreams.Count > 0,
            Streaming = _activeStreams.Count > 0,
            ConnectedDeviceId = _activeStreams.Keys.FirstOrDefault(),
            CaptureSource = capture.Name,
            MaxFps = 30,
            InputEnabled = sessionAgent.IsConnected,
            Displays = displays.Select(d => new DisplayInfo
            {
                Id = d.Id,
                Name = d.Name,
                X = d.X,
                Y = d.Y,
                Width = d.Width,
                Height = d.Height,
                IsPrimary = d.IsPrimary,
                Rotation = d.Rotation,
                RefreshRateHz = d.RefreshRateHz,
                ScalePercent = d.ScalePercent,
            }).ToList(),
            Capabilities =
            [
                "mouseMove", "mouseLeftClick", "mouseRightClick", "mouseMiddleClick",
                "mouseScroll", "keyboard", "unicodeText",
            ],
        };
    }

    public RemoteInputResponse SendInput(RemoteInputRequest input, string? deviceId)
    {
        if (input.Kind.Equals("sessionStart", StringComparison.OrdinalIgnoreCase) ||
            input.Kind.Equals("sessionStop", StringComparison.OrdinalIgnoreCase))
        {
            // Handled by the WebSocket hub; a plain HTTP request cannot hold a stream open.
            return new RemoteInputResponse
            {
                Accepted = false,
                SessionAgentState = sessionAgent.State.ToString(),
                Detail = "Start a screen stream with remote.start over the WebSocket, not a plain request.",
            };
        }

        if (!sessionAgent.IsConnected)
        {
            log.LogInformation("Rejected a remote input event: the session agent is not connected");
            return new RemoteInputResponse
            {
                Accepted = false,
                Delivered = 0,
                SessionAgentState = sessionAgent.State.ToString(),
                Detail = "Remote input needs the per-user session agent, which is not running. " +
                         "Log in to the PC (or start 'ESI Remote PC Session') and try again.",
            };
        }

        var delivered = sessionAgent.QueueInput(input) ? 1 : 0;

        if (delivered == 1 && input.Kind.StartsWith("mouse", StringComparison.OrdinalIgnoreCase))
        {
            // Remote sessions are audited at the start, not per event: a mouse-move stream
            // would otherwise produce thousands of log rows a second.
            _activeStreams.TryAdd(deviceId ?? "unknown", DateTimeOffset.UtcNow);
        }

        return new RemoteInputResponse
        {
            Accepted = delivered == 1,
            Delivered = delivered,
            SessionAgentState = sessionAgent.State.ToString(),
        };
    }

    public bool StartStream(string deviceId, int displayId, int fps, int quality, int gridCols, int gridRows)
    {
        if (!sessionAgent.IsConnected)
            return false;

        _activeStreams[deviceId] = DateTimeOffset.UtcNow;
        audit.Record(AuditLog.ActionRemoteStarted, "ok", deviceId,
            detail: $"display {displayId} @ {fps}fps q{quality} grid {gridCols}x{gridRows}");

        return sessionAgent.RequestCapture(deviceId, displayId, Math.Clamp(fps, 1, 30),
            Math.Clamp(quality, 10, 100), Math.Clamp(gridCols, 1, 16), Math.Clamp(gridRows, 1, 16));
    }

    public void StopStream(string deviceId)
    {
        _activeStreams.TryRemove(deviceId, out _);
        sessionAgent.StopCapture(deviceId);
        log.LogInformation("Remote stream for {DeviceId} stopped", deviceId);
    }
}

/// <summary>One connected phone.</summary>
public sealed class HubConnection
{
    public required WebSocket Socket { get; init; }
    public required PairedDevice Device { get; init; }
    public string DeviceId => Device.DeviceId;
    public string DeviceName => Device.Name;
    public HashSet<string> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public long LastSeenTicks { get; set; } = DateTimeOffset.UtcNow.Ticks;
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Serialises writes; a WebSocket allows only one send at a time.</summary>
    public SemaphoreSlim SendGate { get; } = new(1, 1);

    public bool IsAlive(DateTimeOffset now) => (now - new DateTimeOffset(LastSeenTicks, TimeSpan.Zero)).TotalSeconds < 45;
}

/// <summary>
/// Fan-out hub for the WebSocket surface: metrics streaming, notification push, live
/// events, and the remote-control screen/input channel.
/// <para>
/// Only one screen stream is served at a time, because the session agent can only capture
/// on the interactive thread and multiplexing several displays through it would starve
/// input latency, which is the thing users actually notice.
/// </para>
/// </summary>
public sealed class WebSocketHub
{
    private readonly ILogger<WebSocketHub> _log;
    private readonly MetricsService _metrics;
    private readonly RemoteService _remote;
    private readonly ConcurrentDictionary<Guid, HubConnection> _connections = new();
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private string? _activeStreamDevice;

    public WebSocketHub(ILogger<WebSocketHub> log, MetricsService metrics, RemoteService remote)
    {
        _log = log;
        _metrics = metrics;
        _remote = remote;
        remote.FrameReceived += OnFrameReceived;
    }

    private ILogger<WebSocketHub> log => _log;
    private MetricsService metrics => _metrics;
    private RemoteService remote => _remote;

    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Forwards captured frames to the phone that asked for them.
    /// <para>
    /// Only the streaming device receives pixels, even though the agent knows which device
    /// each frame was captured for. Forwarding a frame to a phone that did not request a
    /// stream would push megabytes of JPEG at a device that has no decoder running.
    /// </para>
    /// </summary>
    private void OnFrameReceived(object? sender, SessionAgentFrame frame)
    {
        var deviceId = frame.DeviceId;
        if (deviceId.Length == 0 || _activeStreamDevice != deviceId)
            return;

        foreach (var connection in _connections.Values)
        {
            if (!string.Equals(connection.DeviceId, deviceId, StringComparison.Ordinal))
                continue;

            if (connection.Socket.State != WebSocketState.Open)
                continue;

            // Fire-and-forget: the pipe pump must not wait on a slow phone, and SendBinaryAsync
            // already swallows the failure that a dropped socket produces.
            _ = SendBinaryAsync(connection, frame.Data, connection.Cancellation.Token);
        }
    }

    public async Task HandleAsync(HttpContext context, WebSocket socket, PairedDevice device)
    {
        // The device, not just its id: the hub needs the scope set to authorise each frame,
        // and a name and an id are not enough to make that decision.
        var connection = new HubConnection { Socket = socket, Device = device };
        var key = Guid.NewGuid();
        _connections[key] = connection;
        log.LogInformation("WebSocket connected: {Device} ({Name}). {Count} active.",
            device.DeviceId, device.Name, _connections.Count);

        try
        {
            await SendAsync(connection, WsServerFrame.Of("hello", data: new
            {
                serverTime = AgentDatabase.NowUnix(),
                deviceId = device.DeviceId,
                deviceName = device.Name,
            })).ConfigureAwait(false);

            await PumpAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "The WebSocket loop for {Device} ended with an error", device.DeviceId);
        }
        finally
        {
            _connections.TryRemove(key, out _);
            remote.StopStream(device.DeviceId);

            await _streamGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_activeStreamDevice == device.DeviceId)
                    _activeStreamDevice = null;
            }
            finally
            {
                _streamGate.Release();
            }

            connection.Cancellation.Cancel();
            connection.SendGate.Dispose();

            log.LogInformation("WebSocket disconnected: {Device}. {Count} remaining.", device.DeviceId, _connections.Count);
        }
    }

    private async Task PumpAsync(HubConnection connection)
    {
        var socket = connection.Socket;
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(connection.Cancellation.Token);
        var buffer = new byte[64 * 1024];

        // Per-connection background work: metrics streaming and keepalive.
        using var channels = CancellationTokenSource.CreateLinkedTokenSource(ct.Token);
        var workers = new List<Task>
        {
            Task.Run(() => StreamMetricsAsync(connection, channels.Token), CancellationToken.None),
            Task.Run(() => KeepAliveAsync(connection, channels.Token), CancellationToken.None),
        };

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var receive = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), ct.Token)
                    .ConfigureAwait(false);

                if (receive.MessageType == WebSocketMessageType.Close)
                    break;

                connection.LastSeenTicks = DateTimeOffset.UtcNow.Ticks;

                if (receive.Count == 0)
                    continue;

                var text = Encoding.UTF8.GetString(buffer, 0, receive.Count);

                WsClientFrame? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<WsClientFrame>(text, ProtocolJson.Options);
                }
                catch (JsonException)
                {
                    await SendAsync(connection, new WsServerFrame
                    {
                        T = "error",
                        Code = ErrorCodes.BadRequest,
                        Message = "The frame was not valid JSON.",
                    }).ConfigureAwait(false);
                    continue;
                }

                if (frame is not null)
                    await DispatchAsync(connection, frame).ConfigureAwait(false);
            }
        }
        finally
        {
            channels.Cancel();

            try
            {
                await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Workers end on cancellation; nothing to recover.
            }
        }
    }

    private async Task DispatchAsync(HubConnection connection, WsClientFrame frame)
    {
        // Every frame is scope-checked here even though the upgrade only required `read`.
        // The socket multiplexes subscriptions and streams next to commands that drive the
        // mouse and the keyboard, so checking once at the handshake would let a read-only
        // device inject input simply by opening the connection.
        var required = FrameScope(frame.T);
        if (required != DeviceScope.None && !connection.Device.Has(required))
        {
            log.LogWarning("Rejected a {Type} frame from {Device}: the {Scope} scope is required",
                frame.T, connection.DeviceId, required);

            await SendAsync(connection, new WsServerFrame
            {
                T = "error",
                Id = frame.Id,
                Code = ErrorCodes.ForbiddenScope,
                Message = $"This device is not permitted to send '{frame.T}' frames (needs the {required} scope).",
            }).ConfigureAwait(false);

            return;
        }

        switch (frame.T)
        {
            case "ping":
                await SendAsync(connection, WsServerFrame.Of("pong", frame.Id, new { ts = AgentDatabase.NowUnix() })).ConfigureAwait(false);
                break;

            case "subscribe":
            {
                if (frame.Channels is { Count: > 0 })
                {
                    foreach (var channel in frame.Channels)
                        connection.Channels.Add(channel);
                }
                else
                {
                    connection.Channels.UnionWith(["metrics", "notifications", "events"]);
                }

                await SendAsync(connection, WsServerFrame.Of("subscribed", frame.Id,
                    new { channels = connection.Channels.ToArray() })).ConfigureAwait(false);
                break;
            }

            case "unsubscribe":
                if (frame.Channels is { Count: > 0 })
                {
                    foreach (var channel in frame.Channels)
                        connection.Channels.Remove(channel);
                }

                await SendAsync(connection, WsServerFrame.Of("unsubscribed", frame.Id,
                    new { channels = connection.Channels.ToArray() })).ConfigureAwait(false);
                break;

            case "remote.start":
                await HandleRemoteStartAsync(connection, frame).ConfigureAwait(false);
                break;

            case "remote.stop":
                remote.StopStream(connection.DeviceId);
                await SendAsync(connection, WsServerFrame.Of("remote.state", frame.Id,
                    new { active = false, connectedDeviceId = connection.DeviceId })).ConfigureAwait(false);
                break;

            case "remote.input" or "remote.key":
                await HandleRemoteInputAsync(connection, frame).ConfigureAwait(false);
                break;

            case "gaming.watch":
                await SendAsync(connection, WsServerFrame.Of("gaming", frame.Id)).ConfigureAwait(false);
                break;

            default:
                await SendAsync(connection, new WsServerFrame
                {
                    T = "error",
                    Id = frame.Id,
                    Code = ErrorCodes.Unsupported,
                    Message = $"Unknown frame type '{frame.T}'.",
                }).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// The scope a frame type requires, or <see cref="DeviceScope.None"/> when it is
    /// unrestricted.
    /// <para>
    /// A table rather than scattered checks, for the same reason the HTTP routes use one:
    /// the policy is then readable in a single place and testable without a socket. Anything
    /// that changes machine state - launching, terminating, input injection, gaming mode -
    /// is <c>control</c>. Everything that only observes is <c>read</c>.
    /// </para>
    /// </summary>
    public static DeviceScope FrameScope(string frameType) => frameType switch
    {
        "remote.start" or "remote.stop" or "remote.input" or "remote.key" => DeviceScope.Control,
        "gaming.watch" or "gaming.set" => DeviceScope.Control,
        "apps.launch" or "apps.terminate" => DeviceScope.Control,
        "media.command" => DeviceScope.Control,
        "power.command" => DeviceScope.Control,
        "files.write" => DeviceScope.Files,
        _ => DeviceScope.Read,
    };

    private async Task HandleRemoteStartAsync(HubConnection connection, WsClientFrame frame)
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeStreamDevice is not null && _activeStreamDevice != connection.DeviceId)
            {
                await SendAsync(connection, new WsServerFrame
                {
                    T = "error",
                    Id = frame.Id,
                    Code = ErrorCodes.Conflict,
                    Message = "Another device is already streaming this screen.",
                }).ConfigureAwait(false);
                return;
            }

            _activeStreamDevice = connection.DeviceId;
        }
        finally
        {
            _streamGate.Release();
        }

        var started = remote.StartStream(
            connection.DeviceId,
            frame.DisplayId ?? 0,
            frame.Fps ?? 15,
            frame.Quality ?? 70,
            (frame.Grid is { Length: 2 } ? frame.Grid[0] : 4),
            (frame.Grid is { Length: 2 } ? frame.Grid[1] : 4));

        await SendAsync(connection, WsServerFrame.Of("remote.state", frame.Id, new
        {
            active = started,
            displayId = frame.DisplayId ?? 0,
            requestedFps = frame.Fps ?? 15,
            quality = frame.Quality ?? 70,
            detail = started ? null : "The session agent is not connected, so the screen cannot be captured.",
        })).ConfigureAwait(false);
    }

    private async Task HandleRemoteInputAsync(HubConnection connection, WsClientFrame frame)
    {
        var input = frame.T == "remote.key"
            ? new RemoteInputRequest
            {
                Kind = "key",
                Code = frame.Code,
                Text = frame.Text,
                Down = frame.Down ?? true,
                Mods = frame.Mods,
            }
            : frame.Input;

        if (input is null)
        {
            await SendAsync(connection, new WsServerFrame
            {
                T = "error",
                Id = frame.Id,
                Code = ErrorCodes.BadRequest,
                Message = "No input payload was supplied.",
            }).ConfigureAwait(false);
            return;
        }

        var result = remote.SendInput(input, connection.DeviceId);

        if (!result.Accepted)
        {
            await SendAsync(connection, new WsServerFrame
            {
                T = "error",
                Id = frame.Id,
                Code = ErrorCodes.SessionAgentUnavailable,
                Message = result.Detail ?? "The input could not be delivered.",
            }).ConfigureAwait(false);
        }
    }

    private async Task StreamMetricsAsync(HubConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (connection.Channels.Contains("metrics") && connection.Socket.State == WebSocketState.Open)
                {
                    var snapshot = await metrics.GetAsync(1000, ct).ConfigureAwait(false);
                    await SendAsync(connection, WsServerFrame.Of("metrics", data: snapshot), ct).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "The metrics stream for {Device} stopped", connection.DeviceId);
        }
    }

    private async Task KeepAliveAsync(HubConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

                if (connection.Socket.State != WebSocketState.Open)
                    return;

                await SendAsync(connection, WsServerFrame.Of("ping", data: new { ts = AgentDatabase.NowUnix() }), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Pushes a notification to every subscribed client.</summary>
    public Task BroadcastNotificationAsync(NotificationDto notification, CancellationToken ct = default)
        => BroadcastAsync("notification", notification, c => c.Channels.Contains("notifications"), ct);

    /// <summary>Pushes a state-change event to every subscribed client.</summary>
    public Task BroadcastEventAsync(string kind, object data, CancellationToken ct = default)
        => BroadcastAsync("event", new { kind, data }, c => c.Channels.Contains("events"), ct);

    private async Task BroadcastAsync<T>(string type, T payload, Func<HubConnection, bool> filter, CancellationToken ct)
    {
        foreach (var connection in _connections.Values.ToList())
        {
            if (connection.Socket.State != WebSocketState.Open || !filter(connection))
                continue;

            try
            {
                await SendAsync(connection, WsServerFrame.Of(type, data: payload), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not push a {Type} to {Device}", type, connection.DeviceId);
            }
        }
    }

    public static async Task SendAsync(HubConnection connection, WsServerFrame frame, CancellationToken ct = default)
    {
        if (connection.Socket.State != WebSocketState.Open)
            return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, ProtocolJson.Options);

        await connection.SendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is gone; the pump will notice and clean up.
        }
        finally
        {
            connection.SendGate.Release();
        }
    }

    /// <summary>Sends a raw binary frame (the screen stream).</summary>
    public static async Task SendBinaryAsync(HubConnection connection, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (connection.Socket.State != WebSocketState.Open)
            return;

        await connection.SendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await connection.Socket.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // As above.
        }
        finally
        {
            connection.SendGate.Release();
        }
    }

    public void CloseAll()
    {
        foreach (var connection in _connections.Values.ToList())
        {
            try
            {
                connection.Socket.Abort();
            }
            catch
            {
                // Nothing to do during shutdown.
            }
        }

        _connections.Clear();
    }
}

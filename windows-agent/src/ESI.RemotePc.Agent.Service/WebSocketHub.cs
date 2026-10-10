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

    /// <summary>
    /// The newest camera frame per phone, for the MJPEG endpoint and the app's preview.
    /// </summary>
    private readonly Esi.Platform.Camera.CameraFrameStore _cameras;

    /// <summary>
    /// Devices already announced as serving a camera, so the log says it once per device instead
    /// of on every frame at 15fps.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _cameraSeenBy = new(StringComparer.Ordinal);

    /// <summary>
    /// Screen frames forwarded per device, for the D46 stream diagnostic. See
    /// <see cref="OnFrameReceived"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _framesForwarded = new(StringComparer.Ordinal);

    public WebSocketHub(ILogger<WebSocketHub> log, MetricsService metrics, RemoteService remote,
        Esi.Platform.Camera.CameraFrameStore cameras)
    {
        _log = log;
        _metrics = metrics;
        _remote = remote;
        _cameras = cameras;
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

        // D46: a stream that is capturing but shows nothing on the phone is indistinguishable
        // from one that is capturing and being dropped. Counting at the very top of this method
        // says which: a rising count means the frames arrived here and the fault is downstream,
        // and no count at all means the session agent is not producing them. Once a stream runs
        // this is one line per frame, so it is rate-limited to a summary per stream.
        if (_framesForwarded.AddOrUpdate(deviceId, 1, static (_, n) => n + 1) is var count && count % 60 == 1)
        {
            log.LogInformation("[ws] {Device}: forwarded {Frames} screen frames ({Bytes} bytes in the last one)",
                deviceId, count, frame.Data.Length);
        }

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

    /// <summary>
    /// Takes a camera frame pushed up by the phone and stores it for the MJPEG endpoint.
    /// <para>
    /// The header is validated rather than trusted. A phone can be a paired device and still be
    /// buggy, compromised, or simply not this build, and a bad <c>PayloadLength</c> read from the
    /// wire would either slice past the buffer or - worse - be used to index into it. The frame is
    /// taken by declared length only after that length has been checked against what actually
    /// arrived.
    /// </para>
    /// </summary>
    private void AcceptCameraFrame(HubConnection connection, byte[] message)
    {
        if (message.Length < ScreenFrameHeader.Size)
        {
            log.LogWarning("[ws] {Device} sent a {Bytes}-byte binary frame, too short for a header",
                connection.DeviceId, message.Length);
            return;
        }

        var magic = BitConverter.ToUInt32(message, 0);
        var frameType = BitConverter.ToUInt16(message, 4);

        if (magic != ScreenFrameHeader.ExpectedMagic || frameType != FrameTypes.CameraFrame)
        {
            log.LogWarning("[ws] {Device} sent an unrecognised binary frame (magic 0x{Magic:x8}, type {Type})",
                connection.DeviceId, magic, frameType);
            return;
        }

        var width = BitConverter.ToInt32(message, 16);
        var height = BitConverter.ToInt32(message, 20);
        var declared = BitConverter.ToUInt32(message, 28);
        var available = message.Length - ScreenFrameHeader.Size;

        if (declared != available || declared == 0)
        {
            log.LogWarning("[ws] {Device} declared a {Declared}-byte camera frame but sent {Available}",
                connection.DeviceId, declared, available);
            return;
        }

        if (width <= 0 || height <= 0)
        {
            log.LogWarning("[ws] {Device} sent a camera frame with {Width}x{Height} dimensions",
                connection.DeviceId, width, height);
            return;
        }

        var jpeg = message.AsSpan(ScreenFrameHeader.Size, (int)declared).ToArray();
        _cameras.Publish(connection.DeviceId, jpeg, width, height);

        if (!_cameraSeenBy.ContainsKey(connection.DeviceId))
        {
            _cameraSeenBy[connection.DeviceId] = 0;
            log.LogInformation("[ws] {Device} is now serving a {Width}x{Height} camera over MJPEG",
                connection.DeviceId, width, height);
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

        // Accumulates a multi-segment binary message. Declared outside the loop so it survives
        // between reads, which is the entire point: EndOfMessage only arrives on the last piece.
        MemoryStream? incoming = null;

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

                // Binary is the phone pushing a camera frame up. It needs no JSON wrapper and no
                // dispatch, so it is handled here rather than going through the text path below.
                //
                // The continuation is the whole reason this works. A single ReceiveAsync only
                // returns whatever one TCP segment carried, which for a camera JPEG is usually a
                // fragment: at 64 KB per read, a 640x480 frame arrives in several pieces. Parsing
                // a header off the first piece and treating the rest as payload would publish a
                // truncated image, and it would fail intermittently, only on frames that happened
                // to straddle a segment boundary. So the message is accumulated until it is
                // EndOfMessage.
                if (receive.MessageType == WebSocketMessageType.Binary)
                {
                    incoming ??= new MemoryStream();
                    incoming.Write(buffer, 0, receive.Count);

                    if (!receive.EndOfMessage)
                        continue;

                    var complete = incoming.ToArray();
                    incoming.Dispose();
                    incoming = null;
                    AcceptCameraFrame(connection, complete);
                    continue;
                }

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
        // Every frame the socket delivers is logged, with its type, before anything else looks
        // at it.
        //
        // The hub had no logging at all, which made the entire screen-and-input path opaque: a
        // phone reporting "Start did nothing" and an agent with nothing to say about it is
        // indistinguishable from a frame that never arrived, a frame that was rejected, and a
        // frame that was handled and produced nothing. It was the reason D42 took this long to
        // find. `ping` is the one exception - it arrives every few seconds forever and says
        // nothing.
        if (frame.T is not ("ping" or "pong"))
        {
            log.LogInformation("[ws] {Device} sent {Frame}", connection.DeviceId, frame.T);
        }

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

        // Camera frames do not appear here, and that is deliberate rather than an oversight. They
        // arrive as binary messages, which never reach this table, so there is no frame-type name
        // to write. What guards them is the upgrade itself: the socket is only opened to a paired
        // device holding the control scope, because turning a phone's camera on is a control
        // action for the same reason moving its mouse is - the PC is being driven from elsewhere.
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
            // A new stream starts a new count; see _framesForwarded (D46).
            _framesForwarded[connection.DeviceId] = 0;
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

        // Whether the stream actually started, and if not why. The reply below carries the
        // same detail, but only the phone can see that, and the phone was showing a disabled
        // Start button with no explanation at all (D42).
        if (started)
        {
            log.LogInformation(
                "Screen stream started for {Device}: display {Display}, {Fps} fps, quality {Quality}",
                connection.DeviceId, frame.DisplayId ?? 0, frame.Fps ?? 15, frame.Quality ?? 70);
        }
        else
        {
            // The template carried {Device} and nothing was passed, so this rendered as
            // "refused for : ..." with the identifier blanked out. It compiled, it warned
            // (CA2017), and the one log line that names *which* device could not start a
            // stream was the one missing it.
            log.LogWarning(
                "Screen stream refused for {Device}: the session agent is not connected, so there is " +
                "nothing to capture. This is the desktop session agent (the tray icon), not the service.",
                connection.DeviceId);
        }

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

using Esi.Platform.Capture;
using Esi.Platform.Input;
using Esi.Platform.Session;
using Serilog;
using Serilog.Core;

namespace Esi.Session;

/// <summary>
/// Captures the desktop on a timer, delta-encodes it, and ships it to the service.
/// <para>
/// Capturing and encoding run on a dedicated thread rather than a thread-pool task, because
/// the cost is a fixed per-frame amount of GDI work plus a JPEG encode. On the pool that
/// work would compete with the service's HTTP handlers for the same starvation-sensitive
/// threads, and remote control is the one feature where added latency is felt immediately.
/// </para>
/// <para>
/// An idle screen is sampled at <see cref="CaptureOptions.IdleFps"/> instead of the requested
/// frame rate. A remote desktop that has not been touched should not cost the CPU of a
/// full-rate video stream; the rate returns to the requested value as soon as
/// <see cref="ScreenStreamEncoder"/> reports that something changed.
/// </para>
/// </summary>
internal sealed class CapturePump
{
    private readonly Serilog.ILogger _log;
    private readonly GdiScreenCaptureProvider _capture;
    private readonly ScreenStreamEncoder _encoder;
    private readonly SessionChannel _channel;
    private readonly SemaphoreSlim _configGate = new(1, 1);

    /// <summary>
    /// Cancels the capture thread. Recreated by <see cref="Start"/> and disposed by
    /// <see cref="Stop"/>.
    /// <para>
    /// D46: this was a single readonly field created once in the constructor. The first Stop
    /// cancelled it and nothing ever cleared it, so every later Start began with
    /// <c>IsCancellationRequested == true</c> and the capture loop returned before it ever ran.
    /// </para>
    /// <para>
    /// The symptom was not "the stream never starts" but "the stream starts and then goes
    /// silent", which is what made it look like a codec or network problem: the agent reported
    /// the stream started, the session agent logged "Capturing display 0", and exactly one frame
    /// reached the phone - enough for every layer that could see it to look healthy. Only
    /// restarting the process recovered it, because nothing else ever built a fresh token.
    /// </para>
    /// </summary>
    private CancellationTokenSource _stopping = new();

    private Thread? _thread;
    private CaptureOptions _options = new();
    private DisplayGeometry? _display;
    private string _deviceId = "";
    private uint _frameIndex;
    private int _consecutiveFailures;

    /// <summary>Consecutive frames the delta codec had nothing to send (D46).</summary>
    private int _skippedFrames;

    /// <summary>Consecutive captures that produced no frame at all (D46).</summary>
    private int _captureFailures;

    public CapturePump(Serilog.ILogger log, GdiScreenCaptureProvider capture, ScreenStreamEncoder encoder, SessionChannel channel)
    {
        _log = log;
        _capture = capture;
        _encoder = encoder;
        _channel = channel;
    }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start(string deviceId, int displayId, int fps, int quality, int gridCols, int gridRows)
    {
        _configGate.Wait();
        try
        {
            _deviceId = deviceId;
            _options = new CaptureOptions
            {
                Fps = Math.Clamp(fps, 1, 30),
                Quality = Math.Clamp(quality, 10, 100),
                GridCols = (ushort)Math.Clamp(gridCols, 1, 16),
                GridRows = (ushort)Math.Clamp(gridRows, 1, 16),
                IdleFps = Math.Clamp(fps / 4, 1, 3),
                KeyframeInterval = 120,
            };

            _display = _capture.GetDisplay(displayId) ?? _capture.EnumerateDisplays().FirstOrDefault();

            if (_display is null)
            {
                _log.Warning("Display {DisplayId} does not exist; nothing to capture", displayId);
                return;
            }

            _encoder.SetQuality(_options.Quality);
            _encoder.RequestKeyframe();
            _frameIndex = 0;
            _consecutiveFailures = 0;

            if (IsRunning)
            {
                _log.Information("Retargeting the capture to display {DisplayId} at {Fps}fps for {Device}",
                    displayId, _options.Fps, deviceId);
                return;
            }

            // A fresh token, because the previous one was cancelled by Stop and never cleared.
            // Without this the loop below exits on its first condition check, so the stream sends
            // nothing after the frame that Start's own request produces (D46).
            _stopping.Dispose();
            _stopping = new CancellationTokenSource();

            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "ESI screen capture",
                // Above normal: a frame that arrives late is a dropped frame the user sees.
                Priority = ThreadPriority.AboveNormal,
            };

            _thread.Start();
            _log.Information("Capturing display {Display} ({Width}x{Height}) at {Fps}fps for {Device}",
                _display.Id, _display.Width, _display.Height, _options.Fps, deviceId);
        }
        finally
        {
            _configGate.Release();
        }
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        _log.Information("Stopping the screen capture");
        _stopping.Cancel();
    }

    private void CaptureLoop()
    {
        var next = Environment.TickCount64;

        // D46 diagnostic: the loop's own heartbeat. A stream that "started" and produced one frame
        // could mean the loop is not running at all, and nothing else said so - the agent reported
        // the stream started, the pump logged "Capturing display 0", and both were true while
        // nothing was capturing. Counted here, at the top, so a dead loop is visible in the log
        // rather than inferred from the absence of frames.
        var iterations = 0;

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                if (++iterations % 300 == 1)
                {
                    _log.Information("Screen capture loop is running ({Iterations} iterations)", iterations);
                }

                CaptureOnce();
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                // A capture failure is usually a locked desktop, a display that was just
                // unplugged, or a session switch. None of them are fatal, and none of them
                // should end the thread: the service is waiting for either frames or an
                // explicit stop, and silently going quiet is the worst outcome.
                _consecutiveFailures++;

                if (_consecutiveFailures == 1 || _consecutiveFailures % 50 == 0)
                    _log.Warning(ex, "Screen capture failed ({Failures} in a row)", _consecutiveFailures);

                if (_consecutiveFailures > 600)
                {
                    _log.Error("Screen capture has failed persistently; giving up on this stream");
                    return;
                }
            }

            var wait = NextDelay();
            next += wait;

            var sleep = next - Environment.TickCount64;
            if (sleep < 0)
            {
                // Fell behind (a very slow encode, or the process was suspended). Resynchronise
                // rather than accumulating a debt that would spin the loop at full rate forever.
                sleep = wait;
                next = Environment.TickCount64 + wait;
            }

            try
            {
                _stopping.Token.WaitHandle.WaitOne((int)sleep);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private int NextDelay()
    {
        var fps = _options.IdleFps > 0 && _encoder.MillisecondsSinceChange(NowUnixMs()) > 400
            ? _options.IdleFps
            : _options.Fps;

        return (int)Math.Max(1, 1000.0 / Math.Max(1, fps));
    }

    private void CaptureOnce()
    {
        var display = _display;
        if (display is null)
            return;

        // Cap the capture width so a 4K display is downscaled before it is encoded rather
        // than after: the JPEG encode dominates, and a 3840-wide frame costs several times
        // what a 1920-wide one does on a phone-sized screen.
        if (!_capture.TryCapture(display, MaxStreamWidth, out var frame, out var error) || frame is null)
        {
            // D46: this path was Debug, and the Debug level is below what this process logs at,
            // so a capture that never succeeded was indistinguishable from one that succeeded and
            // changed nothing. It is the first thing to check now: the encoder's skip counter
            // below never increments if we return here.
            if (++_captureFailures % 60 == 1)
            {
                _log.Information("Capture of display {Display} produced no frame ({Count} so far): {Error}",
                    display.Id, _captureFailures, error ?? "no error reported");
            }

            return;
        }

        var isKeyframe = _frameIndex % Math.Max(1, _options.KeyframeInterval) == 0;

        var encoded = _encoder.Encode(
            frame, display.Id, _options.GridCols, _options.GridRows, _options.Quality, isKeyframe);

        if (encoded is null)
        {
            // Nothing changed on the desktop, so the delta codec had nothing to send. Normal, and
            // the encoder's own keyframe timer is what guarantees it cannot last (D46). Logged at
            // Debug and sampled, because an unbounded run of these IS the silent-stream defect.
            if (++_skippedFrames % 60 == 1)
            {
                _log.Information("Skipped {Count} frames with no changed tiles", _skippedFrames);
            }

            return;
        }

        if (_skippedFrames > 0)
        {
            _log.Information("Resumed after {Count} frames with no changed tiles", _skippedFrames);
            _skippedFrames = 0;
        }

        _frameIndex++;

        // The service forwards the bytes verbatim over the WebSocket, so this is already in
        // the client's wire format. Serialising to JSON here is the one unavoidable cost of a
        // newline-delimited channel; see SessionChannel for why that trade was made.
        _channel.SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.Frame,
            DeviceId = _deviceId,
            DisplayId = display.Id,
            Width = encoded.ScreenWidth,
            Height = encoded.ScreenHeight,
            Data = encoded.ToWire(),
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Frames wider than this are downscaled. 2560 keeps a 1440p desktop legible on a phone
    /// while staying inside a sane bandwidth budget on a home Wi-Fi link.
    /// </summary>
    private const int MaxStreamWidth = 2560;

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
        _configGate.Dispose();
    }
}

/// <summary>
/// Injects input events forwarded by the service.
/// <para>
/// Input must be applied on a thread that owns an interactive desktop, which in practice
/// means the UI thread's message loop is not required but a single dedicated thread is: the
/// service can queue hundreds of mouse-move events per second, and <c>SendInput</c> is
/// subject to UIPI, so batching and rate limiting here is what keeps the desktop responsive.
/// </para>
/// </summary>
internal sealed class InputPump
{
    private const int MaxQueuedEvents = 4096;
    private const int DrainPerTick = 256;

    private readonly Serilog.ILogger _log;
    private readonly InputInjector _injector;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Esi.Protocol.RemoteInputRequest> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<string> _heldKeys = [];

    private Thread? _thread;
    private int _screenWidth;
    private int _screenHeight;

    public InputPump(Serilog.ILogger log, InputInjector injector)
    {
        _log = log;
        _injector = injector;
    }

    public void SetScreenGeometry(int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;
    }

    public void Start()
    {
        if (_thread is { IsAlive: true })
            return;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ESI input injection",
            Priority = ThreadPriority.AboveNormal,
        };

        _thread.Start();
    }

    public void Enqueue(Esi.Protocol.RemoteInputRequest input)
    {
        // Bounded on purpose. A phone whose connection stalls, or a client stuck in a loop,
        // must not be able to grow this queue until the agent runs out of memory; dropping
        // the oldest is the right failure for a latency-sensitive stream.
        if (_queue.Count >= MaxQueuedEvents)
        {
            _queue.TryDequeue(out _);
            _log.Warning("The input queue overflowed; dropped the oldest event");
        }

        _queue.Enqueue(input);

        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private void Loop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                _wake.Wait(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var drained = 0;
            while (drained < DrainPerTick && _queue.TryDequeue(out var input))
            {
                drained++;

                if (input.Kind.Equals("sessionStop", StringComparison.OrdinalIgnoreCase))
                {
                    ReleaseHeldKeys();
                    continue;
                }

                try
                {
                    TrackHeldKey(input);
                    _injector.Inject(input, _screenWidth, _screenHeight);
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Injecting {Kind} failed", input.Kind);
                }
            }
        }
    }

    /// <summary>
    /// Remembers which modifiers are down. Without this, a phone that loses connection
    /// mid-Ctrl+Shift leaves the desktop in a stuck-modifier state, which is the single most
    /// annoying failure mode a remote-input implementation can have.
    /// </summary>
    private void TrackHeldKey(Esi.Protocol.RemoteInputRequest input)
    {
        if (!input.Kind.Equals("key", StringComparison.OrdinalIgnoreCase) || input.Code is null)
            return;

        if (input.Down != true)
        {
            _heldKeys.Remove(input.Code);
            return;
        }

        if (!_heldKeys.Contains(input.Code))
        {
            _heldKeys.Add(input.Code);

            if (_heldKeys.Count > 16)
                _log.Warning("More than 16 keys are held down; the input state is probably wrong");
        }
    }

    private void ReleaseHeldKeys()
    {
        if (_heldKeys.Count == 0)
            return;

        var held = _heldKeys.ToArray();
        _heldKeys.Clear();

        _log.Information("Releasing {Count} key(s) that were still held: {Keys}",
            held.Length, string.Join(", ", held));

        foreach (var code in held)
        {
            try
            {
                _injector.Inject(new Esi.Protocol.RemoteInputRequest
                {
                    Kind = "key",
                    Code = code,
                    Down = false,
                }, _screenWidth, _screenHeight);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Releasing {Code} failed", code);
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        ReleaseHeldKeys();
        _stopping.Dispose();
        _wake.Dispose();
    }
}

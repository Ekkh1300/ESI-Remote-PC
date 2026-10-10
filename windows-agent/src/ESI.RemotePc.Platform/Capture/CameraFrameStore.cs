using System.Collections.Concurrent;
using Esi.Platform.Capture;

namespace Esi.Platform.Camera;

/// <summary>
/// Holds the latest camera frame pushed up by a phone, and hands it out to whoever wants to
/// watch that camera.
/// <para>
/// This is deliberately a one-frame store rather than a queue. A camera produces frames far
/// faster than anyone consumes them, so a queue would either grow without bound or need a
/// drop policy; and for a live view the only frame that has value is the newest one. A slow
/// viewer sees a slightly stuttering live picture rather than a growing delay, which is the
/// right failure for something being watched rather than recorded.
/// </para>
/// <para>
/// There is no virtual webcam device behind this, and there cannot be one without a signed
/// kernel driver - a WHQL certificate costs more than this project has, and iVCam's
/// <c>iVCam.sys</c> is signed by the Microsoft Windows Hardware Compatibility Publisher for
/// exactly that reason. So the camera is exposed as an MJPEG stream, which OBS, VLC, ffmpeg and
/// anything else that speaks multipart/x-mixed-replace can open as an ordinary network source.
/// </para>
/// </summary>
public sealed class CameraFrameStore
{
    /// <summary>How long a frame stays fresh before a viewer is told the camera has gone away.</summary>
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, CameraSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CameraFrameStore> _log;

    /// <summary>
    /// The clock, behind a seam. Staleness is the one behaviour here that is purely a function of
    /// elapsed time, and a test for it would otherwise have to either sleep past
    /// <see cref="FrameTimeout"/> or reach into private state by reflection - and a reflection
    /// test fails loudly the next time a field is renamed, which is noise rather than signal.
    /// </summary>
    private readonly Func<DateTimeOffset> _clock;

    public CameraFrameStore(ILogger<CameraFrameStore> log)
        : this(log, () => DateTimeOffset.UtcNow) { }

    internal CameraFrameStore(ILogger<CameraFrameStore> log, Func<DateTimeOffset> clock)
    {
        _log = log;
        _clock = clock;
    }

    /// <summary>
    /// Records the newest frame from a device. Replaces whatever was there: for a live camera the
    /// previous frame is already stale the moment a newer one arrives.
    /// </summary>
    public void Publish(string deviceId, ReadOnlyMemory<byte> jpeg, int width, int height)
    {
        if (jpeg.IsEmpty)
            return;

        var now = _clock();
        var slot = _slots.GetOrAdd(deviceId, id => new CameraSlot(id));

        var wasFirst = Interlocked.CompareExchange(ref slot.HasFrame, 1, 0) == 0;
        slot.Frame = new CameraFrame(jpeg.ToArray(), width, height, now);
        slot.LastSeen = now;

        if (wasFirst)
            _log.LogInformation("Device {Device} started sending camera frames ({Width}x{Height})",
                deviceId, width, height);
    }

    /// <summary>
    /// The newest frame for a device, or <c>null</c> when there is none or the camera has stopped.
    /// Staleness matters: a phone that stops streaming leaves its last frame behind, and serving
    /// that indefinitely would freeze a viewer on a picture that stopped moving with no
    /// indication that anything had gone wrong.
    /// </summary>
    public CameraFrame? Latest(string deviceId)
    {
        if (!_slots.TryGetValue(deviceId, out var slot))
            return null;

        var frame = slot.Frame;
        if (frame is null)
            return null;

        return _clock() - frame.CapturedAt > FrameTimeout ? null : frame;
    }

    /// <summary>
    /// True while the device is still sending. Used to tell a viewer "waiting for the first
    /// frame" apart from "the camera stopped".
    /// </summary>
    public bool IsLive(string deviceId) =>
        _slots.TryGetValue(deviceId, out var slot) &&
        slot.HasFrame == 1 &&
        _clock() - slot.LastSeen <= FrameTimeout;

    /// <summary>Forgets a device, so its frames are not served after it disconnects.</summary>
    public void Forget(string deviceId) => _slots.TryRemove(deviceId, out _);

    private sealed class CameraSlot(string deviceId)
    {
        public string DeviceId { get; } = deviceId;
        public int HasFrame;
        public volatile CameraFrame? Frame;
        public DateTimeOffset LastSeen;
    }
}

/// <summary>One camera picture: the encoded JPEG plus the dimensions it was taken at.</summary>
public sealed record CameraFrame(byte[] Jpeg, int Width, int Height, DateTimeOffset CapturedAt);
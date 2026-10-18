using NAudio.CoreAudioApi;
using NAudio.Wave;

using Esi.Platform.Audio;
using Esi.Platform.Session;

namespace Esi.Session;

// WasapiLoopbackCapture is marked obsolete in NAudio 2.2 with a message pointing at
// WasapiRecorderBuilder.WithProcessLoopback() for process-scoped capture, or a WasapiRecorder
// configured with a render device for system-wide. Neither type exists in 2.2.1 - the replacement
// is for a newer major version than the one this project pins. System-wide loopback is what
// "play the PC's audio on the phone" means, because the sound is not this process's, and the type
// exposes no Read: the buffers arrive only through DataAvailable, which is why this file has no
// managed resampling fallback to fall back to.
#pragma warning disable CS0618

/// <summary>
/// Captures what the PC is playing and pushes it to the phone, so the phone can be the speaker.
///
/// <para>
/// Everything here needs hardware: a loopback device, an audio session, a socket. Every decision
/// lives in <see cref="AudioPacketiser"/> and <see cref="MuLaw"/>, which need none of those, which
/// is why the codec and the framing are tested without a sound card and this class stays thin.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The device is asked to deliver exactly 24 kHz stereo 16-bit, which makes WASAPI resample with
/// the audio engine's own converter and sync filter. Resampling again in managed code on top
/// would be a second filter over the same signal. The stream format is then whatever the packets
/// claim, so the phone never has to guess or renegotiate mid-stream.
/// </para>
/// <para>
/// The loopback buffer arrives on the device's own thread and only ever appends to a list. All
/// the work - companding, framing, the socket - happens on this class's thread, because a
/// companding curve on the capture thread would underrun the device, and a WASAPI underrun does
/// not drop a buffer: it plays a gap of silence.
/// </para>
/// </remarks>
internal sealed class AudioPump : IDisposable
{
    private readonly Serilog.ILogger _log;
    private readonly SessionChannel _channel;
    private readonly object _gate = new();
    private readonly List<short> _carried = new(AudioPacketiser.SamplesPerPacket * 4);
    private readonly SemaphoreSlim _wake = new(0);

    private WasapiLoopbackCapture? _capture;
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile int _wakePending;
    private string _deviceId = "";
    private uint _sequence;
    private long _dropped;
    private long _sent;
    private long _oddBytes;
    private LocalOutputSilencer? _silencer;

    public AudioPump(Serilog.ILogger log, SessionChannel channel)
    {
        _log = log;
        _channel = channel;
    }

    private LocalOutputSilencer Silencer
    {
        get
        {
            _silencer ??= new LocalOutputSilencer(
                new MMDeviceEndpointMute(),
                new RegistrySilencerLedger(),
                new SerilogLoggerAdapter<LocalOutputSilencer>(_log));
            return _silencer;
        }
    }

    /// <summary>
    /// Whether the device is capturing right now.
    /// </summary>
    /// <remarks>
    /// The device's state, not this class's intentions. WASAPI opens asynchronously and can fail
    /// several hundred milliseconds after <see cref="Start"/> returns, so a caller that reported
    /// "started" from the return value would tell the user audio is on while the phone receives
    /// nothing at all.
    /// </remarks>
    public bool IsRunning => _capture is { CaptureState: CaptureState.Capturing };

    public long SentPackets => Interlocked.Read(ref _sent);

    /// <summary>
    /// Packets discarded because the socket could not keep up.
    /// </summary>
    /// <remarks>
    /// Non-zero means the phone is hearing gaps. It is worth surfacing rather than hiding: the
    /// alternative is a stream that sounds intermittently broken with nothing anywhere saying why.
    /// </remarks>
    public long DroppedPackets => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Bytes discarded because the device handed over half a sample.
    /// </summary>
    /// <remarks>
    /// Always zero in practice, and always zero is the point. WASAPI delivers whole samples, so
    /// a lone trailing byte would mean the buffer was torn at a byte boundary, and throwing one
    /// away keeps the stereo interleave aligned for every packet after it rather than sliding
    /// the two channels a sample apart.
    /// </remarks>
    public long DiscardedOddBytes => Interlocked.Read(ref _oddBytes);

    /// <summary>
    /// Opens the default output device for loopback capture. Safe to call twice.
    /// </summary>
    /// <param name="deviceId">Accepted for compatibility with the wire contract; the capture
    /// always taps the default output device, so the value is recorded but not used.</param>
    /// <param name="silenceLocal">True when the phone asked for the PC's own speakers to be
    /// silenced while the phone carries the sound (D87).</param>
    public void Start(string deviceId, bool silenceLocal = false)
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                _deviceId = deviceId;
                return;
            }

            _stopping = false;
            Interlocked.Exchange(ref _wakePending, 0);
            while (_wake.CurrentCount > 0)
            {
                _wake.Wait(0);
            }
            lock (_carried)
            {
                _carried.Clear();
            }

            // Before the capture opens, not after: muted-before-first-buffer means no unmuted
            // moment escapes to the room at all, however short the gap between the two would
            // have been the other way round.
            if (silenceLocal)
            {
                Silencer.Engage();
            }

            WasapiLoopbackCapture capture;
            WaveFormat deviceFormat;
            try
            {
                capture = new WasapiLoopbackCapture();
                deviceFormat = capture.WaveFormat;

                // Assigning WaveFormat is what switches WASAPI's automatic format conversion on.
                // Without it the stream arrives at the mix format - whatever the hardware picked -
                // and there is no way to resample it here, because this type exposes no Read.
                capture.WaveFormat = new WaveFormat(
                    AudioPacketiser.SampleRate, 16, AudioPacketiser.Channels);
            }
            catch (Exception ex)
            {
                _log.Error(ex,
                    "Could not open the default output device for loopback capture, or it would " +
                    "not convert to {Rate} Hz stereo. Audio is off; screen, input and power are " +
                    "unaffected.", AudioPacketiser.SampleRate);
                return;
            }

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                _log.Error(ex, "The output device refused to start capturing. Audio is off.");
                return;
            }

            _capture = capture;
            _deviceId = deviceId;

            _thread = new Thread(PumpLoop) { IsBackground = true, Name = "esi-audio" };
            _thread.Start();

            _log.Information(
                "Audio capture started: loopback on the default output device at {DeviceRate} Hz and " +
                "{DeviceChannels} channel(s), delivered as {Rate} Hz {Channels}-channel 16-bit, " +
                "packets are mu-law",
                deviceFormat.SampleRate, deviceFormat.Channels,
                AudioPacketiser.SampleRate, AudioPacketiser.Channels);
        }
    }

    /// <summary>Stops capturing and lets the pump thread finish. Safe to call when not started.</summary>
    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        Thread? thread;

        lock (_gate)
        {
            capture = _capture;
            thread = _thread;
            _capture = null;
            _thread = null;
            _stopping = true;
            _wake.Release();
        }

        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                // A device that has already gone away throws here routinely - a USB DAC unplugged,
                // Bluetooth headphones closed. The interesting event already fired and was already
                // logged; this one is noise.
                _log.Debug(ex, "The loopback device was already gone when audio was stopped.");
            }
        }

        // The pump thread waits on _wake with a timeout, so it cannot outlive the device by
        // however long the next buffer takes to arrive - which, if the PC is playing silence, is
        // never.
        thread?.Join(TimeSpan.FromSeconds(2));

        try
        {
            capture?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "The loopback device did not dispose cleanly.");
        }

        // After the device is gone, not before: while the capture still runs, releasing the
        // mute would put the PC's sound back into the room while the phone was still legally
        // the one carrying it.
        _silencer?.Release();

        _log.Information("Audio capture stopped after {Sent} packets ({Dropped} dropped)",
            Interlocked.Read(ref _sent), Interlocked.Read(ref _dropped));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0)
        {
            return;
        }

        // The stream is 16-bit stereo because that is the format Start asked for. The width is
        // checked rather than assumed: a device that quietly ignored the request would otherwise
        // be read as if it were stereo and come out as a chirping mess that looks exactly like a
        // codec bug.
        var format = _capture?.WaveFormat;
        if (format is null || format.BitsPerSample != 16 || format.Channels != AudioPacketiser.Channels)
        {
            _log.Warning(
                "The output device delivered {Encoding} {Bits}-bit {Channels}-channel audio, not the " +
                "16-bit stereo this stream encodes for. Dropping it; audio will sound wrong if this " +
                "is ignored.",
                format?.Encoding, format?.BitsPerSample, format?.Channels);
            return;
        }

        var shorts = args.BytesRecorded / 2;
        lock (_carried)
        {
            // Bounded. If the socket stalls, an unbounded queue grows until the process dies and
            // then holds minutes of audio that is stale by the time it could be sent. Dropping
            // the oldest is the right side to drop: what is already in front of the phone is at
            // most twenty milliseconds old, and the alternative is playing audio from half a
            // minute ago.
            const int Cap = AudioPacketiser.SamplesPerPacket * 40;
            if (_carried.Count + shorts > Cap)
            {
                var excess = _carried.Count + shorts - Cap;
                _carried.RemoveRange(0, Math.Min(excess, _carried.Count));
                Interlocked.Add(ref _dropped, excess / AudioPacketiser.SamplesPerPacket);
            }

            if ((args.BytesRecorded & 1) != 0)
            {
                Interlocked.Increment(ref _oddBytes);
                shorts--;
            }

            for (var i = 0; i < shorts; i++)
            {
                _carried.Add(BitConverter.ToInt16(args.Buffer, i * 2));
            }
        }

        // Raise the pending flag *before* the wake. PumpLoop reads it with an exchange that
        // clears it, so a flag raised after the release could be consumed by a wake that saw the
        // old value and then left the samples stranded until the next buffer arrived.
        Interlocked.Exchange(ref _wakePending, 1);

        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another buffer beat this one to the wake. One wake drains everything queued, so
                // a second one would only make the pump spin.
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is { } ex)
        {
            _log.Warning(ex,
                "The loopback device stopped unexpectedly. Audio is off until it is started again.");
        }
    }

    private void PumpLoop()
    {
        var block = new short[AudioPacketiser.SamplesPerPacket];
        var packet = new byte[AudioPacketiser.PacketBytes];

        while (!_stopping)
        {
            if (Interlocked.Exchange(ref _wakePending, 0) == 0)
            {
                _wake.Wait(TimeSpan.FromMilliseconds(50));
                continue;
            }

            while (true)
            {
                lock (_carried)
                {
                    if (_carried.Count < AudioPacketiser.SamplesPerPacket)
                    {
                        break;
                    }
                    _carried.CopyTo(0, block, 0, AudioPacketiser.SamplesPerPacket);
                    _carried.RemoveRange(0, AudioPacketiser.SamplesPerPacket);
                }

                Send(block, packet);
            }
        }
    }

    private void Send(short[] block, byte[] packet)
    {
        try
        {
            AudioPacketiser.WritePacket(block, _sequence++, packet);
            _channel.SendAsync(new SessionAgentMessage
            {
                T = SessionChannelProtocol.Events.Frame,
                DeviceId = _deviceId,
                Width = AudioPacketiser.SampleRate,
                Height = AudioPacketiser.Channels,
                Data = packet,
            }).GetAwaiter().GetResult();

            Interlocked.Increment(ref _sent);
        }
        catch (Exception ex)
        {
            // The channel going away is normal when the session ends. Throwing out of the loop
            // would leave a thread spinning on a device that is still open.
            Interlocked.Increment(ref _dropped);
            _log.Debug(ex, "An audio packet could not be sent.");
        }
    }

    public void Dispose() => Stop();
}

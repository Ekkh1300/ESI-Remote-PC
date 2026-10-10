using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Esi.Platform.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Esi.Session;
using Serilog;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D46: the screen stream sent one frame and then nothing, and the phone kept saying
/// "no frames arriving".
///
/// <para>
/// The agent log eventually held the signature, on one session agent with one phone:
/// </para>
/// <code>
/// 06:37:43  remote.start -&gt; "Screen stream started" -&gt; forwarded 1 screen frames (42928 bytes)
/// 07:40:23  remote.start -&gt; "Screen stream started" -&gt; nothing at all
/// </code>
///
/// <para>
/// The same request, accepted the same way, ten minutes apart, producing one frame the first
/// time and none the second. That is a pump that ran once and never again - and the reason is
/// <c>_stopping</c>: a <see cref="CancellationTokenSource"/> built once in the constructor. The
/// first <c>Stop</c> cancelled it and nothing ever cleared it, so the second <c>Start</c>
/// launched a thread whose very first line saw a cancelled token and returned. The agent said
/// the stream started, the session agent logged "Capturing display 0", and no layer anywhere
/// reported that the loop had ended before it began.
/// </para>
///
/// <para>
/// This is a behavioural test on purpose. The version it replaced only read the source file
/// and asserted the shape of the fix - a field that is not readonly, an assignment before a
/// thread start. That kind of test is exactly what let D46 through: a heartbeat fix earlier
/// this session passed its own tests and was still broken, because the tests fed the encoder a
/// timestamp that never advanced. A test that asserts the fix looks right is worth less than
/// none if the bug is about what the code does at runtime.
/// </para>
/// </summary>
public sealed class CapturePumpRestartBehaviourTests : IDisposable
{
    private readonly List<NamedPipeServerStream> _servers = new();
    private readonly List<CancellationTokenSource> _stops = new();
    private readonly ConcurrentQueue<int> _received = new();

    private readonly System.Text.StringBuilder _log = new();

    /// <summary>
    /// Captures the pump's own output rather than discarding it.
    ///
    /// The decision under test is invisible by construction - the pump skips a frame and says
    /// so, or fails to capture and says so, and both used to be silent or below the log floor.
    /// A test that cannot read which one happened is a test that can only report "no frames".
    /// </summary>
    private Serilog.ILogger Null => new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.Sink(new StringWriterSink(_log))
        .CreateLogger();

    private sealed class StringWriterSink(System.Text.StringBuilder into) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            lock (into)
            {
                into.Append(logEvent.Level).Append(": ")
                    .Append(logEvent.RenderMessage()).Append('\n');
            }
        }
    }

    /// <summary>
    /// Stands in for the agent service: a pipe server on the same name the session agent's
    /// channel dials, reading whatever the pump writes.
    ///
    /// <para>
    /// The ordering matters and cost an afternoon to learn. <c>ConnectOnceAsync</c> gives the
    /// client ten seconds and does not retry within that window, so the server has to be
    /// listening <em>before</em> the channel starts - and the client then writes a <c>ready</c>
    /// greeting before anything else, so a server that only reads would deadlock.
    /// </para>
    /// </summary>
    private SessionChannel StartChannel(out Task pump)
    {
        var name = $"ESIRemotePc-Test-{Guid.NewGuid():N}";

        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _servers.Add(server);

        var accepted = server.WaitForConnectionAsync();

        var channel = new SessionChannel(name, Null);
        var cts = new CancellationTokenSource();
        _stops.Add(cts);
        pump = channel.RunAsync(cts.Token);

        // Accepted completes once the client is attached; anything the channel writes after
        // that is what this test is about.
        accepted.Wait(TimeSpan.FromSeconds(20));

        // Drain in the background: a pipe's buffer is finite, and a pump that writes frames
        // faster than a test reads them will block on the write instead of failing a
        // different, more confusing assertion.
        _ = Task.Run(async () =>
        {
            var reader = new StreamReader(server, Encoding.UTF8, false, 256 * 1024, leaveOpen: true);
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    return;
                }

                if (line is null) return;
                if (line.Contains("\"t\":\"frame\"", StringComparison.Ordinal)) _received.Enqueue(line.Length);
            }
        });

        return channel;
    }

    private CapturePump NewPump(SessionChannel channel)
    {
        // The real provider and the real encoder: this test is about the pump's threading,
        // so substituting either would test something nobody ships.
        var capture = new GdiScreenCaptureProvider(NullLogger<GdiScreenCaptureProvider>.Instance);
        var encoder = new ScreenStreamEncoder(NullLogger<ScreenStreamEncoder>.Instance);
        return new CapturePump(Null, capture, encoder, channel);
    }

    /// <summary>
    /// Runs the pump and waits for frames.
    ///
    /// The desktop will not change on its own, so the encoder's keyframe heartbeat is the only
    /// thing producing frames here - which is also why that heartbeat has to be measured in
    /// wall-clock time. Counting frames to decide whether to send a frame cannot recover on a
    /// still desktop, because the counter only moves inside the code that runs when a frame is
    /// sent. That was the bug: the counter could never reach its own threshold.
    /// </summary>
    private async Task<int> WaitForFramesAsync(int minimum, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_received.Count >= minimum) return _received.Count;
            await Task.Delay(100);
        }

        return _received.Count;
    }

    private void DiscardFrames()
    {
        while (_received.TryDequeue(out _)) { }
    }

    [Fact]
    public async Task A_second_start_after_a_stop_still_produces_frames()
    {
        var channel = StartChannel(out var pump);
        var capturePump = NewPump(channel);

        try
        {
            // First run: the one that always worked.
            capturePump.Start("device-under-test", 0, 15, 60, 2, 2);
            var firstRun = await WaitForFramesAsync(1, 25_000);

            Assert.True(capturePump.IsRunning, "the capture loop was not running on the first Start");
            Assert.True(firstRun >= 1,
                "the first Start produced no frames, so this test cannot tell a restart bug " +
                "from a broken environment");

            capturePump.Stop();

            // Wait for the thread to actually be gone. Stop only signals; if the loop is still
            // winding down, the second Start races it and the test would pass or fail for a
            // reason that has nothing to do with the bug.
            for (var i = 0; i < 100 && capturePump.IsRunning; i++) await Task.Delay(50);

            DiscardFrames();

            // Second run: the one D46 lost. With the token created once in the constructor and
            // cancelled by the first Stop, this thread returned before it ran and the stream
            // went silent for the rest of the session.
            capturePump.Start("device-under-test", 0, 15, 60, 2, 2);

            var secondRun = await WaitForFramesAsync(1, 25_000);

            Assert.True(capturePump.IsRunning,
                "after Stop then Start the capture loop was not running - the token the first " +
                "Stop cancelled was never cleared");

            lock (_log) { Console.WriteLine(_log.ToString()); }
            Assert.True(secondRun >= 1,
                $"the second Start produced {secondRun} frames. It produced none on the live " +
                "system too, and the agent reported the stream as started the whole time.");
        }
        finally
        {
            capturePump.Stop();
            // The channel's loop is cancelled in Dispose, which runs after this method
            // returns, so awaiting it here would always time out. A teardown that waits on
            // something still running reports its own shutdown instead of the bug the test
            // exists for - and from the outside the two failures look identical.
            _ = pump;
        }
    }

    public void Dispose()
    {
        foreach (var s in _stops) { try { s.Cancel(); } catch { /* already disposed */ } }
        foreach (var s in _servers) { try { s.Dispose(); } catch { /* already closed */ } }
        _received.Clear();
    }
}
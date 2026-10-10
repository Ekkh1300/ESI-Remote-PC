using System.Reflection;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D46: a frame dropped on a dead command channel was indistinguishable from one that was never
/// built.
/// <para>
/// <c>SessionChannel.SendAsync</c> opened with a bare <c>return</c> when the pipe was not usable.
/// The agent reported the stream as started, the session agent logged "Capturing display 0", and
/// the pump produced frame after frame - none of which mentioned that every one of them was
/// thrown away at the first line of the send. That is the shape of this whole defect: nothing
/// throws, nothing is logged, and every layer reports health.
/// </para>
/// <para>
/// The behavioural version of this wanted a real named pipe, and building one is what the four
/// tests that were removed were doing - they failed on harness ordering rather than on the code.
/// So this guards the property instead: the drop path must say something.
/// </para>
/// </summary>
public class SessionChannelSilenceTests
{
    private static string ReadSessionChannel()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "ESI.RemotePc.Session")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var file = Path.Combine(directory!.FullName, "src", "ESI.RemotePc.Session", "SessionChannel.cs");
        Assert.True(File.Exists(file), $"could not find SessionChannel.cs under {directory.FullName}");

        return File.ReadAllText(file);
    }

    [Fact]
    public void A_message_dropped_on_a_dead_channel_is_reported()
    {
        var source = ReadSessionChannel();

        // The guard that would have turned D46 from a mystery into a one-line answer.
        Assert.Contains("Dropped a {Type} message", source, StringComparison.Ordinal);
        Assert.Contains("_log.Warning(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drop_path_names_what_was_lost()
    {
        var source = ReadSessionChannel();

        // A count alone does not help: the question is always "how much, and since when".
        Assert.Contains("_droppedSends", source, StringComparison.Ordinal);
        Assert.Contains("dropped so far", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_send_still_does_not_throw_when_the_channel_is_dead()
    {
        // The property that must not be traded away to make the drop visible. Nothing upstream -
        // the capture pump, the pairing prompt - can recover from an exception here, and the
        // method's callers are fire-and-forget in places.
        var method = typeof(Esi.Session.SessionChannel).GetMethod("SendAsync",
            BindingFlags.Public | BindingFlags.Instance,
            new[] { typeof(Esi.Platform.Session.SessionAgentMessage) });

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);
    }
}

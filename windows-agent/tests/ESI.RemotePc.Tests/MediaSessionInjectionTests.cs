using Esi.Platform.Media;
using Esi.Protocol;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D43: volume and mute were injected by the Windows service, which runs in session 0.
/// <para>
/// <c>SendInput</c> there reaches a desktop nobody is looking at, and Core Audio - a per-session
/// API - hands session 0 no endpoint at all, so <c>ReadVolume</c> returned null and
/// <c>SetVolume</c> refused every request. The phone's volume slider sat at 0 and its mute button
/// did nothing, and the agent answered with a bare <c>409</c> that carried no reason: the app
/// could not tell a refusal from a bug and kept resending, 43 times in a couple of seconds.
/// </para>
/// <para>
/// The transport keys were documented as being routed through the session agent but never were,
/// so this also guards the note that described an intent the code did not carry out.
/// </para>
/// </summary>
public class MediaSessionInjectionTests
{
    [Theory]
    [InlineData(MediaAction.VolumeUp, "0xaf")]
    [InlineData(MediaAction.VolumeDown, "0xae")]
    [InlineData(MediaAction.Mute, "0xad")]
    [InlineData(MediaAction.Unmute, "0xad")]
    [InlineData(MediaAction.ToggleMute, "0xad")]
    public void Every_volume_and_mute_action_maps_to_a_key_the_session_agent_can_press(
        MediaAction action, string expectedHex)
    {
        // If this returns null the endpoint silently falls through to injecting from session 0,
        // which is the whole defect. A new volume action added without a key would reintroduce
        // it with nothing to catch it.
        var vk = MediaController.VolumeVirtualKey(action);
        Assert.True(vk is not null,
            $"{action} has no virtual key, so it would be pressed from session 0.");

        Assert.Equal(expectedHex, $"0x{vk.Value:x2}");
    }

    [Theory]
    [InlineData(MediaAction.Play)]
    [InlineData(MediaAction.Pause)]
    [InlineData(MediaAction.PlayPause)]
    [InlineData(MediaAction.Stop)]
    [InlineData(MediaAction.Next)]
    [InlineData(MediaAction.Previous)]
    public void Transport_actions_keep_their_key(MediaAction action)
    {
        Assert.NotNull(MediaController.TransportVirtualKey(action));
    }

    [Fact]
    public void A_non_volume_action_has_no_volume_key()
    {
        // Guards the other direction: if MediaController.SomethingStartedPlaying were mapped to a
        // volume key by accident, it would be pressed as one.
        Assert.Null(MediaController.VolumeVirtualKey(MediaAction.PlayPause));
    }

    [Fact]
    public void The_endpoint_routes_volume_actions_to_the_session_agent()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Agent.Service", "Endpoints.cs"));

        // The check that decides whether a request goes to the session agent. Without it every
        // volume control is injected by the service and goes nowhere.
        Assert.Contains("if (IsVolumeControl(mediaAction))", source, StringComparison.Ordinal);
        Assert.Contains("session.SendMediaKey(", source, StringComparison.Ordinal);
        Assert.Contains("session.SetMediaVolume(", source, StringComparison.Ordinal);
        Assert.Contains("session.SendMediaTransport(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_never_injects_a_transport_key_itself()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Agent.Service", "Endpoints.cs"));

        // The quiet half of the defect: media.SendTransport on the service injects from session 0,
        // where SendInput succeeds and reaches nothing. The phone was answered 200, so play and
        // pause looked like they worked and simply never did.
        Assert.DoesNotContain("media.SendTransport(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_never_injects_a_volume_key_itself()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Agent.Service", "Endpoints.cs"));

        // If a future edit re-adds media.VolumeUp(...) / media.Mute(...) to the service-side
        // switch, the keys land in session 0 again and the phone gets a silent 409 with no
        // explanation. The switch may keep its unreachable default arm; it may not name a
        // volume action.
        foreach (var action in new[]
                 {
                     "media.VolumeUp", "media.VolumeDown", "media.SetVolume",
                     "media.Mute", "media.Unmute", "media.ToggleMute",
                 })
        {
            Assert.DoesNotContain(action, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_session_agent_actually_handles_the_media_key_command()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Session", "SessionAgentRuntime.cs"));

        // The wire protocol on its own proves nothing: a command nobody answers is the same
        // silence the phone saw before, so both the dispatch and the work are checked.
        Assert.Contains("case SessionChannelProtocol.Commands.MediaKey:", source,
            StringComparison.Ordinal);
        Assert.Contains("case SessionChannelProtocol.Commands.MediaVolumeSet:", source,
            StringComparison.Ordinal);
        Assert.Contains("SendVolumeKeyFromSession(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_agent_owns_a_media_controller_of_its_own()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Session", "SessionAgentRuntime.cs"));

        // volumeSet has to be computed where Core Audio can be read. If this controller is ever
        // removed and replaced by a static call, the walk silently starts guessing from a volume
        // it cannot read.
        Assert.Contains("new Esi.Platform.Media.MediaController(", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        // The test binary runs out of artifacts\, so the source is found by walking up to the
        // directory that actually holds the src\ tree rather than by guessing a depth.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "ESI.RemotePc.Agent.Service")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var file = Path.Combine(directory!.FullName, relativePath);
        Assert.True(File.Exists(file), $"could not find {relativePath} under {directory.FullName}");

        return File.ReadAllText(file);
    }
}
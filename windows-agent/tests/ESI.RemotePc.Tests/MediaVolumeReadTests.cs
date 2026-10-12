using System.Text.RegularExpressions;
using Esi.Platform.Media;
using Esi.Platform.Session;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// The volume slider did not work, and the reason was three separate things that each looked like
/// a smaller problem.
/// <para>
/// <b>The volume could never be read.</b> <c>MediaController</c> built its Core Audio objects
/// with <c>Activator.CreateInstance</c> and then called them with
/// <c>GetType().GetMethod("GetDefaultAudioEndpoint").Invoke(...)</c>. That works for automation
/// servers; Core Audio is not one - every interface derives from IUnknown only, so
/// <c>GetMethod</c> returned null and <c>InvokeMember</c> failed with "COM target does not
/// implement IDispatch". Both were measured on a live machine. Every read therefore returned
/// null, which <c>GetState()</c> turned into a confident 0.
/// </para>
/// <para>
/// <b>That 0 poisoned the walk.</b> The session agent computed the key presses as
/// <c>target - current</c>, and current was always 0, so asking for 30 on a PC sitting at 100
/// pressed volume-up thirty times and the PC never moved. Asking for 60 pressed sixty. Every
/// reply said the volume had been delivered.
/// </para>
/// <para>
/// <b>And the walk was truncated anyway.</b> The press count was clamped to 25 for the whole
/// walk rather than per call, so even a correct base could not have gone more than 25 steps.
/// </para>
/// <para>
/// Measured on this PC while fixing it: <c>Speakers (Realtek(R) Audio)</c> at scalar 1.0 - that
/// is 100%, reported to every phone poll as 0.
/// </para>
/// </summary>
public class MediaVolumeReadTests
{
    [Fact]
    public void A_walk_longer_than_one_batch_is_sent_in_full()
    {
        // The 25 that used to be a ceiling on the *walk* is a ceiling on one call. A request to
        // move 60 steps is three calls, not one truncated one.
        var batches = MediaController.VolumeKeyBatches(60);

        Assert.Equal(60, batches.Sum());
        Assert.All(batches, size => Assert.InRange(size, 1, 25));
    }

    [Fact]
    public void A_full_scale_walk_is_four_batches_of_twenty_five()
    {
        // 0 to 100, the largest walk the slider can ask for.
        Assert.Equal(new[] { 25, 25, 25, 25 }, MediaController.VolumeKeyBatches(100));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(100)]
    public void Every_walk_presses_exactly_the_number_of_times_asked(int presses)
    {
        Assert.Equal(presses, MediaController.VolumeKeyBatches(presses).Sum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_walk_of_nothing_still_presses_once(int presses)
    {
        // A zero-length walk would return without sending anything and report success, which is
        // the same "it said it worked and it did not" shape as the rest of this file.
        Assert.NotEmpty(MediaController.VolumeKeyBatches(presses));
        Assert.Equal(1, MediaController.VolumeKeyBatches(presses).Sum());
    }

    [Fact]
    public void A_reply_that_could_not_read_the_volume_is_not_reported_as_a_silent_pc()
    {
        // The single most important assertion in this file. `volume=0` means two different things
        // - "the PC is silent" and "I cannot see the volume" - and only the flags say which. The
        // old code collapsed them, which is how a PC at 100% reached every phone as 0.
        var silent = SessionAgentBridge.ReadMediaState(new SessionAgentMessage
        {
            Volume = 0,
            VolumeKnown = true,
            IsMuted = false,
            IsMuteKnown = true,
        });

        Assert.NotNull(silent);
        Assert.Equal(0, silent!.Volume);
        Assert.True(silent.VolumeKnown);

        var unreadable = SessionAgentBridge.ReadMediaState(new SessionAgentMessage
        {
            Volume = 0,
            VolumeKnown = false,
            IsMuted = false,
            IsMuteKnown = false,
        });

        Assert.NotNull(unreadable);
        Assert.False(unreadable!.VolumeKnown,
            "an unreadable volume came back looking like a definite zero");
        Assert.False(unreadable.IsMuteKnown);
    }

    [Fact]
    public void A_reply_that_carried_no_media_at_all_is_not_read_as_one_that_did()
    {
        // The other direction: a launch result or a toast confirmation must not be mistaken for
        // a media answer, or a query would be answered by whichever command finished first.
        Assert.Null(SessionAgentBridge.ReadMediaState(new SessionAgentMessage { Succeeded = true }));
        Assert.Null(SessionAgentBridge.ReadMediaState(new SessionAgentMessage { T = "command.result" }));
    }

    [Fact]
    public void A_read_volume_is_clamped_to_the_range_a_phone_can_show()
    {
        // A scalar outside 0..1 would otherwise appear on the slider as a number it cannot hold.
        var state = SessionAgentBridge.ReadMediaState(new SessionAgentMessage
        {
            Volume = 140,
            VolumeKnown = true,
            IsMuteKnown = true,
        });

        Assert.Equal(100, state!.Volume);
    }

    [Fact]
    public void GetState_distinguishes_an_unreadable_volume_from_a_zero_one()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Platform", "Media", "MediaController.cs"));

        // The exact expression that made the two indistinguishable. If it comes back, so does
        // the bug: a session 0 service would report 0 for every poll, forever, with no way for
        // the phone to tell that anything was wrong.
        //
        // Comments are stripped first. They are allowed to quote the bug they describe, and the
        // first version of this assertion failed against its own explanation - a source check
        // that cannot tell code from prose is a check that decides on the wrong thing.
        Assert.DoesNotContain("ReadVolume() ?? 0", StripComments(source), StringComparison.Ordinal);
        Assert.Contains("VolumeKnown = volume is not null", source, StringComparison.Ordinal);
        Assert.Contains("IsMuteKnown = muted is not null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_contract_carries_the_knowable_flags()
    {
        // The flags have to cross the HTTP boundary too, or the phone has no way to grey out a
        // slider it cannot read and would show a confident 0 instead.
        var json = System.Text.Json.JsonSerializer.Serialize(new Esi.Protocol.MediaStateResponse
        {
            Volume = 0,
            VolumeKnown = false,
            IsMuted = false,
            IsMuteKnown = false,
        });

        Assert.Contains("\"volumeKnown\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"isMuteKnown\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_asks_the_session_agent_for_the_volume_rather_than_reading_itself()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Agent.Service", "Endpoints.cs"));

        // The service is in session 0 with no audio endpoint, so its own read is always
        // unknown. Answering the phone's poll from it is what put a 0 next to a slider the user
        // had just moved, both looking equally certain.
        Assert.Contains("MediaStateAsync(session, media, context.RequestAborted)", source,
            StringComparison.Ordinal);
        Assert.Contains("QueryMediaStateAsync(TimeSpan.FromMilliseconds(750)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Results.Ok(media.GetState())", StripComments(source),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_agent_answers_the_state_query()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Session", "SessionAgentRuntime.cs"));

        // The command constant exists on its own and proves nothing: a query nobody answers is a
        // timeout on every poll, which the service would paper over with its own unknown read.
        Assert.Contains("case SessionChannelProtocol.Commands.MediaStateQuery:", source,
            StringComparison.Ordinal);
        Assert.Contains("CommandResultWithMediaAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_refuses_to_walk_from_a_volume_it_could_not_read()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Session", "SessionAgentRuntime.cs"));

        // Pressing `target` presses from an assumed base of zero is precisely how a PC at 100%
        // failed to move when told to go to 30. Refusing is the honest answer; guessing is worse
        // than saying no, because it moves the volume somewhere the user did not ask for.
        Assert.Contains("if (!before.VolumeKnown)", source, StringComparison.Ordinal);
        Assert.Contains("use volumeUp/volumeDown instead.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_media_query_is_answered_by_correlation_id_and_is_not_leaked_on_timeout()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Platform", "Session", "SessionAgentBridge.cs"));

        // Without the finally, a timed-out query leaves an entry that would later resolve a
        // question nobody is asking - and the answer would be handed to the next caller.
        Assert.Contains("_pendingMediaQueries.TryRemove(correlation, out _);", source,
            StringComparison.Ordinal);
        Assert.Contains("Task.WhenAny(", source, StringComparison.Ordinal);
        Assert.Contains("MediaStateQuery", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_interop_declares_the_volume_interface_as_a_vtable_not_as_automation()
    {
        var source = ReadRepositoryFile(
            Path.Combine("src", "ESI.RemotePc.Platform", "Native", "CoreAudio.cs"));

        // The defect was reaching for IDispatch against an interface that has none. This pins the
        // declaration that replaces it, including the two members whose absence would shift the
        // vtable and return a plausible wrong number instead of an error.
        Assert.Contains("InterfaceType(ComInterfaceType.InterfaceIsIUnknown)", source,
            StringComparison.Ordinal);
        Assert.Contains("GetMasterVolumeLevelScalar", source, StringComparison.Ordinal);
        Assert.Contains("GetMute", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes comments before a source check.
    /// <para>
    /// A comment is allowed to quote the bug it describes, and this file's comments do exactly
    /// that. The first version of the assertion on <c>ReadVolume() ?? 0</c> failed against its
    /// own explanation, which is a compact way to learn that a source check has to know what it
    /// is looking at before it can say anything about it.
    /// </para>
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", " ");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
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

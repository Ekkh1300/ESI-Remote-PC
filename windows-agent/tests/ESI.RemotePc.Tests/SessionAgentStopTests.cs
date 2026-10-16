using Esi.Platform.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D74: Exit in the tray menu did nothing, because the thing that would have made it work was
/// on the other side of a process boundary.
/// <para>
/// The session agent is supervised. The service polls every three seconds and, whenever a live
/// session has no agent, it starts one. Both a crash and a person choosing Exit arrive at the
/// service as the same thing - a named pipe that stopped working - so the supervisor answered an
/// Exit by starting a new agent within three seconds. The icon vanished and came straight back,
/// which from a tray menu is indistinguishable from a menu item wired to nothing.
/// </para>
/// <para>
/// These tests hold the decision itself: whether the supervisor should still be trying. It is
/// deliberately one pure answer, because everything the supervisor does is downstream of it and
/// the only way to observe the old behaviour was to wait three seconds for a process to come
/// back.
/// </para>
/// </summary>
public class SessionAgentStopTests
{
    private const uint ThisSession = 7;

    private static SessionAgentBridge Bridge(bool autoLaunch = true)
        => new(
            Options.Create(new SessionOptions { AutoLaunch = autoLaunch }),
            NullLogger<SessionAgentBridge>.Instance);

    [Fact]
    public void An_agent_is_supervised_while_nobody_has_asked_it_to_leave()
    {
        var bridge = Bridge();

        Assert.True(bridge.ShouldSupervise(ThisSession));
    }

    [Fact]
    public void A_tray_exit_stops_the_supervisor_wanting_that_sessions_agent()
    {
        var bridge = Bridge();

        bridge.NoteAgentStoppedByUser(ThisSession);

        Assert.False(bridge.ShouldSupervise(ThisSession));
    }

    [Fact]
    public void A_tray_exit_in_one_session_does_not_stop_another()
    {
        // Leaving is a decision about one desktop, not about the machine. Fast user switching
        // leaves the other person's agent exactly as supervised as it was.
        var bridge = Bridge();

        bridge.NoteAgentStoppedByUser(ThisSession);

        Assert.True(bridge.ShouldSupervise(ThisSession + 1));
    }

    [Fact]
    public void Signing_back_in_brings_the_agent_back()
    {
        // The supervisor's own loop clears the flag when the live session changes, and this is
        // that rule stated where it can be read: a new session id is a new start, so somebody
        // who exits and signs back in is not left with a machine that cannot be reached.
        var bridge = Bridge();

        bridge.NoteAgentStoppedByUser(ThisSession);
        var nextSession = ThisSession + 1;

        Assert.NotEqual(ThisSession, nextSession);
        Assert.True(bridge.ShouldSupervise(nextSession));
    }

    [Fact]
    public void No_live_session_is_never_supervised()
    {
        var bridge = Bridge();

        bridge.NoteAgentStoppedByUser(ThisSession);

        Assert.False(bridge.ShouldSupervise(null));
    }

    [Fact]
    public void AutoLaunch_off_is_never_supervised()
    {
        // The exit rule is one condition among three, and this guards that it did not quietly
        // become the only one: a machine configured not to launch an agent must not have one
        // started, whether or not somebody has asked one to leave.
        var bridge = Bridge(autoLaunch: false);

        Assert.False(bridge.ShouldSupervise(ThisSession));

        bridge.NoteAgentStoppedByUser(ThisSession);

        Assert.False(bridge.ShouldSupervise(ThisSession));
    }

    [Fact]
    public void The_stop_message_records_which_session_left()
    {
        // The end of the wire, not just the middle: an agent's own report has to land in the
        // same place the direct call does, or the flag is set by a code path nothing reaches.
        var bridge = Bridge();

        bridge.HandleMessage(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.AgentStop,
            SessionId = ThisSession,
            ProcessName = "ESI.RemotePc.Session",
            Detail = "exit requested from the tray menu",
        });

        Assert.False(bridge.ShouldSupervise(ThisSession));
        Assert.True(bridge.ShouldSupervise(ThisSession + 1));
    }

    // -----------------------------------------------------------------------------
    // The other half of D74: the tray showed a mark that was not this product's
    // -----------------------------------------------------------------------------

    // -----------------------------------------------------------------------------
    // The icon must not outlive the choice to remove it
    // -----------------------------------------------------------------------------

    [Fact]
    public void The_tray_icon_is_hidden_before_the_message_loop_is_asked_to_end()
    {
        // A NotifyIcon whose owner dies while it is still visible leaves a ghost icon behind:
        // Windows keeps drawing it until the mouse passes over it. So an exit that worked every
        // other way left the mark sitting in the tray, which is the exact picture of an Exit
        // that did nothing - on top of the supervisor starting a new agent (D74).
        //
        // Order is the whole assertion. Both lines can be present and the defect still there if
        // the icon is hidden after the loop has already begun shutting down.
        var shutdown = FunctionBody(ReadSessionAgentRuntime(), "Shutdown");
        var hidden = shutdown.IndexOf("_tray.Visible = false", StringComparison.Ordinal);
        var askedToExit = shutdown.IndexOf("RequestExit()", StringComparison.Ordinal);

        Assert.True(hidden >= 0, "Shutdown no longer takes the tray icon off the screen");
        Assert.True(askedToExit >= 0, "Shutdown no longer ends the message loop");
        Assert.True(hidden < askedToExit,
            "the icon is hidden only after the loop starts ending, so it can outlive the click");
    }

    [Fact]
    public void The_runtime_is_disposed_when_main_finishes()
    {
        // The runtime's own Dispose is what hides and disposes the tray icon. Main used to run
        // it and return, so nothing released the icon on any path the other tests do not cover.
        var program = ReadFile("windows-agent", "src", "ESI.RemotePc.Session", "Program.cs");

        Assert.Contains("using var runtime", program, StringComparison.Ordinal);
    }

    private static string ReadSessionAgentRuntime()
        => ReadFile("windows-agent", "src", "ESI.RemotePc.Session", "SessionAgentRuntime.cs");

    /// <summary>Extracts the body of a named private method, so an order can be asserted.</summary>
    private static string FunctionBody(string source, string name)
    {
        var head = System.Text.RegularExpressions.Regex.Match(
            source,
            $@"\b(?:private|internal|public)[^\{{;]*\b{System.Text.RegularExpressions.Regex.Escape(name)}\s*\([^)]*\)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.True(head.Success, $"could not find a method named {name}");

        var open = source.IndexOf('{', head.Index + head.Length);
        Assert.True(open >= 0, $"{name} has no body");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open);
        }

        Assert.Fail($"{name} has an unbalanced body");
        return string.Empty;
    }

    private static string ReadFile(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"could not find {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_session_agent_and_the_installer_use_one_icon_file()
    {
        // Two copies of an icon are two icons, and the defect being fixed was precisely two
        // places disagreeing about what the user was looking at: the tray said "some other
        // program" while the shortcuts three menus away carried the product's mark. The agent
        // now reads its tray icon back out of its own executable, which is embedded from the
        // csproj - so the csproj and the installer have to name the same file, or the tray and
        // the Start menu drift apart again and no test would notice.
        var root = FindRepoRoot();

        var csproj = Path.Combine(root, "windows-agent", "src", "ESI.RemotePc.Session", "ESI.RemotePc.Session.csproj");
        var installer = Path.Combine(root, "windows-agent", "installer", "ESI-RemotePC.iss");

        var embedded = SingleMatch(File.ReadAllText(csproj), @"<ApplicationIcon>([^<]+)</ApplicationIcon>", csproj);
        var setupIcon = SingleMatch(File.ReadAllText(installer), @"^SetupIconFile=(.+)$", installer);

        var csprojDir = Path.GetDirectoryName(csproj)!;
        var installerDir = Path.GetDirectoryName(installer)!;

        var fromCsproj = Path.GetFullPath(Path.Combine(csprojDir, embedded));
        var fromInstaller = Path.GetFullPath(Path.Combine(installerDir, setupIcon));

        Assert.Equal(fromInstaller, fromCsproj);
        Assert.True(File.Exists(fromCsproj), $"the shared icon is missing: {fromCsproj}");
    }

    private static string SingleMatch(string text, string pattern, string file)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, pattern, System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.True(match.Success, $"no match for {pattern} in {file}");
        return match.Groups[1].Value.Trim();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "windows-agent", "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}

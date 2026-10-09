using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Esi.Platform.Session;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The pairing prompt's geometry, asserted rather than eyeballed.
/// <para>
/// D40 was a one-character mistake with no symptom anyone could act on. D39 made the form
/// shorter when there was no QR - correctly, because there was nothing to make room for - but
/// left the buttons at y=330. 300 pixels tall put them outside the client rectangle, where
/// WinForms clips them: the prompt appeared complete, the code matched the phone, and
/// <c>Allow this device</c> could not be clicked by anyone. The pairing could not be completed
/// and nothing in the build, the tests or the log said why.
/// </para>
/// <para>
/// Nothing caught it because nothing looked at the geometry. These tests build the real form
/// and check every control against the real client size, in both shapes, so a control that
/// falls outside is a failing test rather than a button that is not there.
/// </para>
/// </remarks>
public sealed class PairingPromptFormTests
{
    private static PairingPromptRequest Request(string? pollingUrl) => new(
        PairingId: "0123456789abcdef",
        DeviceName: "Android phone",
        Platform: "android",
        OsVersion: "16",
        AppVersion: "1.0.1",
        RemoteAddress: "192.168.1.102",
        UserCode: "473374",
        ExpiresInSeconds: 120,
        PollingUrl: pollingUrl);

    /// <summary>A polling URL of the shape the agent builds, for the form that draws a QR.</summary>
    private static string PollingUrl() =>
        "https://192.168.1.101:48220/api/v1/pair/session?id=0123456789abcdef" +
        "&secret=Kx7VQm2pLZ8sT4wR9bYcH3nJdF6gN1aP5rU0vX8yZ2eQ";

    public static TheoryData<bool> Shapes => new() { true, false };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_control_is_inside_the_form(bool withQr)
    {
        using var form = withQr
            ? new Esi.Session.PairingPromptForm(Request(PollingUrl()))
            : new Esi.Session.PairingPromptForm(Request(null));

        // The shape the form actually chose, so the two are not asserted separately and allowed
        // to drift: the point is that the controls fit the height that was chosen.
        Assert.Equal(
            withQr ? 400 : 340,
            form.ClientSize.Height);

        foreach (Control control in form.Controls)
        {
            // A PictureBox left at the origin because RenderQr failed is a placeholder, and the
            // form has already degraded to typing the code. It must not fail this test.
            if (withQr && control is PictureBox && control.Size.Width == 0) continue;

            Assert.True(
                control.Bottom <= form.ClientSize.Height,
                $"'{Describe(control)}' ends at y={control.Bottom} but the form is only " +
                $"{form.ClientSize.Height} tall, so it is clipped and cannot be clicked");

            Assert.True(
                control.Right <= form.ClientSize.Width,
                $"'{Describe(control)}' ends at x={control.Right} but the form is only " +
                $"{form.ClientSize.Width} wide");

            Assert.True(
                control.Top >= 0 && control.Left >= 0,
                $"'{Describe(control)}' starts outside the form at ({control.Left},{control.Top})");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_allow_button_is_present_and_reachable(bool withQr)
    {
        using var form = withQr
            ? new Esi.Session.PairingPromptForm(Request(PollingUrl()))
            : new Esi.Session.PairingPromptForm(Request(null));

        var button = form.Controls.OfType<Button>().Single(b => b.Text == "Allow this device");

        // Not asserted on Visible: a control's Visible is false until its parent form has been
        // shown, so on a form that was only constructed it says nothing about whether the user
        // can see the button. What decides that is whether it is inside the client rectangle,
        // which is asserted here and in full above.
        Assert.True(
            button.Bottom <= form.ClientSize.Height,
            $"'Allow this device' ends at y={button.Bottom} in a form of height " +
            $"{form.ClientSize.Height}: it is clipped, and D40 is exactly that");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_deny_button_is_the_default(bool withQr)
    {
        // Every other way out of this form is a denial, and that has to survive a layout change
        // too: if Deny ever stops being CancelButton, a stray Enter would grant access.
        using var form = withQr
            ? new Esi.Session.PairingPromptForm(Request(PollingUrl()))
            : new Esi.Session.PairingPromptForm(Request(null));

        var button = form.Controls.OfType<Button>().Single(b => b.Text == "Deny");
        Assert.Same(button, form.CancelButton);
    }

    [Fact]
    public void No_QR_is_drawn_when_there_is_nothing_to_transfer()
    {
        // The whole point of D39: a requester that brought its own secret has nothing to hand
        // over, so the prompt must not draw a code for a camera that then has to read it.
        using var form = new Esi.Session.PairingPromptForm(Request(null));

        Assert.Empty(form.Controls.OfType<PictureBox>());

        Assert.DoesNotContain(
            "Scan", form.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_says_to_compare_the_code_when_there_is_no_QR()
    {
        using var form = new Esi.Session.PairingPromptForm(Request(null));

        var hint = form.Controls.OfType<Label>().Where(l => l.Text.Contains("matches", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(hint);
    }

    private static string Describe(Control control)
        => control is Button b ? $"button '{b.Text}'"
            : control is Label l ? $"label '{l.Text}'"
            : control.GetType().Name;

    // ---------------------------------------------------------------------------------
    // D41 - the decision has to be reported when it is made, not when the window closes
    // ---------------------------------------------------------------------------------

    [Fact]
    public void The_decision_is_reported_before_the_window_closes()
    {
        // This is the bug a person hits, and it is invisible from the outside: the prompt stays
        // open after Allow (D36, correctly, because the QR is the only copy of the secret), so a
        // caller that reads Decision after ShowDialog returns reads it ~120 seconds late. The
        // agent has expired the attempt by then and discards the approval, and the phone - which
        // was polling the whole time - never gets its keys. Clicking Allow does nothing.
        //
        // So: approve, and assert the listener has already heard about it while the form is
        // demonstrably still open.
        var reported = new List<Esi.Session.PairingDecision>();

        using var form = new Esi.Session.PairingPromptForm(
            Request(null),
            onDecided: reported.Add);

        form.Approve();

        Assert.Equal([Esi.Session.PairingDecision.Approved], reported);
        Assert.Equal(Esi.Session.PairingDecision.Approved, form.Decision);

        // Still open, which is the whole point: it is holding the window for the phone.
        Assert.True(form.WaitingForPhone);
        Assert.False(form.IsDisposed);
    }

    [Fact]
    public void A_denial_is_reported_as_soon_as_it_is_made_too()
    {
        var reported = new List<Esi.Session.PairingDecision>();

        using var form = new Esi.Session.PairingPromptForm(Request(null), onDecided: reported.Add);
        form.Decide(Esi.Session.PairingDecision.Denied);

        Assert.Equal([Esi.Session.PairingDecision.Denied], reported);
    }

    [Fact]
    public void The_decision_is_reported_exactly_once()
    {
        // Escape, the countdown and a click can all land on Deny at about the same moment. A
        // second report would be a second attempt to apply the same answer to the attempt, which
        // the service answers by logging a warning about an already-approved pairing.
        var reported = new List<Esi.Session.PairingDecision>();

        using var form = new Esi.Session.PairingPromptForm(Request(null), onDecided: reported.Add);
        form.Decide(Esi.Session.PairingDecision.Denied);
        form.Decide(Esi.Session.PairingDecision.Denied);
        form.Decide(Esi.Session.PairingDecision.TimedOut);

        Assert.Equal([Esi.Session.PairingDecision.Denied], reported);
    }

    [Fact]
    public void The_session_agent_actually_wires_the_callback_up()
    {
        // The tests above cover the form's contract: report the decision through onDecided. They
        // cannot cover the other half - that the session agent supplies one - because
        // ShowPairingPromptAsync is private and needs a UI anchor, a command channel and a
        // desktop to run at all, and building that much to assert one argument is not worth it.
        //
        // So this reads the source instead. That is the weakest kind of test and it is here
        // deliberately and visibly, because the alternative is what D41 was: every unit test
        // green, and a product where clicking Allow did nothing. When ShowPairingPromptAsync
        // becomes testable this should be replaced by a real test, and this comment is the note
        // to whoever does that.
        var source = ReadRepositoryFile("SessionAgentRuntime.cs");

        Assert.Contains("onDecided:", source, StringComparison.Ordinal);
        Assert.Contains("onDecided: decision => _ = result.TrySetResult(decision)", source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string name)
    {
        // The test binary runs out of artifacts\, so the source is found by walking up to the
        // directory that actually holds the Session project rather than by guessing a depth.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "ESI.RemotePc.Session")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var file = Path.Combine(
            directory!.FullName, "src", "ESI.RemotePc.Session", name);

        Assert.True(File.Exists(file), $"could not find {name} under {directory.FullName}");

        return File.ReadAllText(file);
    }

    [Fact]
    public void A_listener_that_throws_does_not_cost_the_window_its_decision()
    {
        // This runs on the UI thread inside a button handler. An exception here would become an
        // unhandled exception that closes the prompt and loses the decision - the exact outcome
        // the callback exists to prevent.
        var recorded = new List<Esi.Session.PairingDecision>();

        using var form = new Esi.Session.PairingPromptForm(
            Request(null),
            onDecided: _ => throw new InvalidOperationException("the channel is gone"));

        form.Approve();

        Assert.Equal(Esi.Session.PairingDecision.Approved, form.Decision);

        // And a second form wired the other way still hears it, proving the throw was contained.
        using var second = new Esi.Session.PairingPromptForm(
            Request(null),
            onDecided: recorded.Add);

        second.Approve();
        Assert.Equal([Esi.Session.PairingDecision.Approved], recorded);
    }
}
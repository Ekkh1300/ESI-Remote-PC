using System.Drawing;
using System.Windows.Forms;
using Esi.Platform.Session;
using Esi.Session;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D53: the phone asks the user to type a certificate pin, and the screen it tells them to read
/// it from does not show one.
///
/// <para>
/// The instruction on the phone reads, in full: *"Type the full pin from the PC's pairing
/// screen, or scan its QR code."* The pairing screen showed a six-digit approval code and a QR.
/// It did not show the pin. The only copy anywhere was a line in the agent log at startup -
/// <c>C:\ProgramData\ESI-RemotePC\logs\agent-*.log</c>, labelled <c>spki pin</c> - which is not
/// something anyone opens while holding a phone that is asking them for a value.
/// </para>
///
/// <para>
/// So the path was: notice the field is empty, open a text editor, find a log directory, find
/// today's file, search for a string. Every step of that is a place to stop, and none of them
/// explains itself. A user who does not finish it has no pin, and an app that will not take a
/// pin from discovery has no other route in.
/// </para>
/// <para>
/// The pin belongs on this window because this is where someone has already decided to trust
/// the machine. What the pin protects is that the value typed is one the user read rather than
/// one the offering machine supplied, and that comparison only means anything when both values
/// are in front of them at the same time.
/// </para>
/// </summary>
public sealed class PairingPromptShowsPinTests
{
    private const string Pin = "f9Rl9PkFy20h7EmRGjInZguDr0eBYIEqSaO1yODVzDE=";

    private static PairingPromptRequest Request(string? pollingUrl, string? pin) => new(
        PairingId: "0123456789abcdef",
        DeviceName: "Android phone",
        Platform: "android",
        OsVersion: "16",
        AppVersion: "1.0.1",
        RemoteAddress: "192.168.1.102",
        UserCode: "473374",
        ExpiresInSeconds: 120,
        PollingUrl: pollingUrl,
        SpkiPin: pin);

    private static string PollingUrl() =>
        "https://192.168.1.101:48220/api/v1/pair/session?id=0123456789abcdef&pin=" + Pin;

    [Fact]
    public void The_pin_is_on_the_prompt_in_the_shape_that_draws_a_qr()
    {
        using var form = new PairingPromptForm(Request(PollingUrl(), Pin));

        var shown = form.Controls.OfType<Label>().Select(l => l.Text).ToList();

        Assert.Contains(Pin, shown);
        Assert.Contains(shown, t => t.Contains("Certificate pin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_pin_is_on_the_prompt_in_the_shape_that_does_not_draw_a_qr()
    {
        // The two shapes build different control lists. Checking only the QR one would leave
        // the common case - a current phone brings its own secret, so there is nothing to
        // transfer - as the untested one.
        using var form = new PairingPromptForm(Request(null, Pin));

        var shown = form.Controls.OfType<Label>().Select(l => l.Text).ToList();

        Assert.Contains(Pin, shown);
    }

    [Fact]
    public void A_missing_pin_says_where_to_look_rather_than_showing_nothing()
    {
        // The agent is older than this field, or TLS is off. Blank space would read as "there
        // is no pin" and send the user looking for something that is not shown.
        using var form = new PairingPromptForm(Request(null, null));

        var shown = form.Controls.OfType<Label>().Select(l => l.Text).ToList();

        Assert.Contains(shown, t =>
            t.Contains("not reported", StringComparison.OrdinalIgnoreCase)
            && t.Contains("logs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_pin_is_readable_and_does_not_overlap_the_buttons()
    {
        // The form's whole layout is positional, and this one has already shipped a button that
        // sat outside the client rectangle and could not be clicked (D34). New controls are
        // checked against the real geometry in both shapes rather than assumed to fit.
        foreach (var url in new[] { PollingUrl(), null })
        {
            using var form = new PairingPromptForm(Request(url, Pin));

            var pin = form.Controls.OfType<Label>()
                .FirstOrDefault(l => l.Text == Pin);
            Assert.NotNull(pin);

            var approve = form.Controls.OfType<Control>()
                .FirstOrDefault(c => c.Text is "Allow this device");
            Assert.NotNull(approve);

            // Fully inside the client area, and above the buttons rather than under them.
            Assert.True(pin!.Bottom <= form.ClientSize.Height,
                $"the pin label runs past the bottom of the form ({pin.Bottom} > {form.ClientSize.Height})");
            Assert.True(pin.Top >= 0 && pin.Left >= 0 && pin.Right <= form.ClientSize.Width,
                "the pin label sits outside the form's client area, where WinForms clips it");
            Assert.True(pin.Bottom <= approve!.Top,
                $"the pin label overlaps the buttons (pin ends at {pin.Bottom}, buttons start at {approve.Top})");

            // The font has to be small enough that the value is not clipped inside its own box.
            Assert.True(pin.Height >= pin.Font.Height * 2,
                "the pin label is too short for its own text, so the value would be cut off");
        }
    }

    [Fact]
    public void The_prompt_says_where_to_type_it()
    {
        using var form = new PairingPromptForm(Request(PollingUrl(), Pin));

        // The pin on screen is only useful if the user is told it goes in the phone's field,
        // rather than being another string on a window.
        var text = string.Join('\n', form.Controls.OfType<Label>().Select(l => l.Text));
        Assert.Contains("phone", text, StringComparison.OrdinalIgnoreCase);
    }
}
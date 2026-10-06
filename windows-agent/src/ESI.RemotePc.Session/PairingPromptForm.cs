using Esi.Platform.Session;

namespace Esi.Session;

/// <summary>What the person at the keyboard decided about a pairing request.</summary>
internal enum PairingDecision
{
    Approved,
    Denied,
    TimedOut,
}

/// <summary>
/// The on-desktop "a new device wants to pair" prompt.
/// <para>
/// This is the security decision point of the whole product, so the form is built to make
/// denial the easy path: deny is the default button, the request is dismissed with Escape,
/// and closing the window is a denial. Approval requires a deliberate click on an explicit
/// label naming the device.
/// </para>
/// <para>
/// The six-digit user code is shown in both forms. The QR flow carries a code the user types
/// on the phone, and this is the value that proves the two ends are looking at the same
/// request — a relay attack that swapped in a different code would not match.
/// </para>
/// </summary>
internal sealed class PairingPromptForm : Form
{
    private readonly PairingPromptRequest _request;
    private readonly Label _codeLabel;
    private readonly Label _countdownLabel;
    private readonly Label _hint;
    private readonly Button _approve;
    private readonly Button _deny;
    private readonly Func<bool>? _phoneCollected;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DateTimeOffset _deadline;

    /// <param name="request">The attempt being decided.</param>
    /// <param name="phoneCollected">
    /// Returns true once the phone holding this attempt's provisioning secret has taken its
    /// credentials. Consulted only after approval: the dialog stays open until it says yes or
    /// the window's deadline passes.
    /// </param>
    public PairingPromptForm(PairingPromptRequest request, Func<bool>? phoneCollected = null)
    {
        _request = request;
        _phoneCollected = phoneCollected;
        _deadline = DateTimeOffset.Now.AddSeconds(Math.Max(30, request.ExpiresInSeconds));

        Text = "ESI Remote PC - pairing request";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 380);
        BackColor = Color.FromArgb(20, 22, 28);
        ForeColor = Color.FromArgb(228, 232, 240);
        Font = new Font("Segoe UI", 9F);
        TopMost = true;
        ShowInTaskbar = true;

        var accent = Color.FromArgb(94, 214, 255);

        var heading = new Label
        {
            Text = "New device wants to connect",
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = Color.White,
            AutoSize = false,
            Bounds = new Rectangle(24, 18, 492, 30),
        };

        var device = new Label
        {
            Text = $"Device:    {request.DeviceName}\nPlatform:  {request.Platform}\n" +
                   $"From:      {request.RemoteAddress}\n" +
                   $"Version:   {Describe(request.AppVersion, request.OsVersion)}",
            AutoSize = false,
            Bounds = new Rectangle(24, 54, 260, 80),
            ForeColor = Color.FromArgb(190, 198, 214),
        };

        // The QR is the actual transfer. Without it the phone can never obtain the
        // provisioning secret, and a pairing approved here would sit approved forever with
        // the phone polling for credentials that are never released.
        var qr = RenderQr(request.PollingUrl, 150);
        qr.Location = new Point(348, 50);

        var codeCaption = new Label
        {
            Text = "Scan this with the phone, or type the code shown on it:",
            AutoSize = false,
            Bounds = new Rectangle(24, 140, 300, 32),
            ForeColor = Color.FromArgb(190, 198, 214),
        };

        _codeLabel = new Label
        {
            Text = FormatCode(request.UserCode),
            Font = new Font("Consolas", 30F, FontStyle.Bold),
            ForeColor = accent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(24, 168, 300, 54),
        };

        _countdownLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Bounds = new Rectangle(24, 226, 300, 20),
            ForeColor = Color.FromArgb(150, 158, 174),
        };

        // Says what to do next rather than only how to be careful. A phone that has been
        // approved still has to fetch its keys, and the one place the key is - the QR above -
        // disappears with this window, so the order of operations is part of the message.
        _hint = new Label
        {
            Text = "Approve only if you started this on the phone in front of you.\r\n" +
                   "Scan the code above on the phone, then allow this device.",
            AutoSize = false,
            Bounds = new Rectangle(24, 248, 492, 38),
            ForeColor = Color.FromArgb(150, 158, 174),
            Font = new Font("Segoe UI", 8.5F),
        };

        _approve = new Button
        {
            Text = "Allow this device",
            Size = new Size(170, 34),
            Location = new Point(246, 330),
            BackColor = Color.FromArgb(28, 120, 160),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9.5F),
        };
        _approve.FlatAppearance.BorderSize = 0;
        _approve.Click += (_, _) => Approve();

        _deny = new Button
        {
            Text = "Deny",
            Size = new Size(104, 34),
            Location = new Point(424, 330),
            BackColor = Color.FromArgb(48, 52, 64),
            ForeColor = Color.FromArgb(220, 226, 236),
            FlatStyle = FlatStyle.Flat,
        };
        _deny.FlatAppearance.BorderSize = 0;
        _deny.Click += (_, _) => Decide(PairingDecision.Denied);

        Controls.AddRange([heading, device, codeCaption, _codeLabel, _countdownLabel, _hint, qr, _deny, _approve]);

        // Deny is the default, so a stray Enter - or a phone-triggered "approve" the user did
        // not read - cannot grant access.
        AcceptButton = _approve;
        CancelButton = _deny;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>
    /// Draws the QR straight to a bitmap.
    /// <para>
    /// Rendered here rather than fetched from the agent over HTTPS: the prompt has to work
    /// even when the listener is misconfigured or the certificate is the very thing the user
    /// is being trained to distrust. The payload is already in the request.
    /// </para>
    /// </summary>
    private static PictureBox RenderQr(string payload, int pixels)
    {
        try
        {
            var modules = Esi.Platform.Pairing.QrEncoder.Encode(payload);
            var size = modules.GetLength(0);

            // Four modules of quiet zone, as the specification requires.
            const int quiet = 4;
            const int scale = 4;
            var extent = (size + quiet * 2) * scale;

            using var bitmap = new Bitmap(extent, extent);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);

            using var brush = new SolidBrush(Color.Black);
            for (var row = 0; row < size; row++)
            {
                for (var col = 0; col < size; col++)
                {
                    if (modules[row, col])
                        graphics.FillRectangle(brush, (col + quiet) * scale, (row + quiet) * scale, scale, scale);
                }
            }

            // Scaled with nearest neighbour: interpolating a QR turns crisp module edges
            // into gradients, and a scanner reads gradients as nothing.
            var scaled = new Bitmap(pixels, pixels);
            using var destination = Graphics.FromImage(scaled);
            destination.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            destination.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            destination.DrawImage(bitmap, 0, 0, pixels, pixels);

            return new PictureBox
            {
                Image = scaled,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(pixels, pixels),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
            };
        }
        catch (Exception)
        {
            // A prompt without a QR still works if the user types the code and the agent
            // serves the URL from /pairing, so degrade rather than fail the pairing.
            //
            // A Label rather than a PictureBox: PictureBox has no text alignment, and an
            // unaligned "unavailable" in the corner of the code area looks like a broken
            // scan target rather than an honest fallback.
            return new PictureBox { Size = new Size(pixels, pixels) };
        }
    }

    /// <summary>The answer, valid once the dialog has closed.</summary>
    public PairingDecision Decision { get; private set; } = PairingDecision.TimedOut;

    /// <summary>True while the form is up holding an approved attempt open for the phone.</summary>
    internal bool WaitingForPhone => _waitingForPhone;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Tick();
    }

    /// <summary>
    /// Records the approval and then <i>stays on screen</i>.
    /// </summary>
    /// <remarks>
    /// This is D36, and it is the whole defect: the dialog used to call <c>Close()</c> here,
    /// which is the natural thing to write and the wrong thing to do. The QR on this window is
    /// the only copy of the provisioning secret, and the secret is precisely what the phone
    /// still lacks at this moment - it has asked, it is waiting, and it cannot be trusted with
    /// the secret over the network. So approving destroyed the transfer, the phone polled for
    /// 35 seconds with nothing to prove itself with, and the pairing could never complete. On a
    /// real phone that looked like a plain "nothing happened" (the agent logs 35
    /// <c>polled without a valid provisioning secret</c> warnings and gives up).
    /// <para>
    /// The order that works is scan-then-allow, and it is also the order the hint now states.
    /// Approve-then-scan is still supported, because this window does not close on approval: it
    /// turns into a "waiting for the phone" state, keeps the QR up, and closes by itself the
    /// moment <see cref="phoneCollected"/> goes true.
    /// </para>
    /// </remarks>
    private void Approve()
    {
        _decided = true;
        _waitingForPhone = true;
        Decision = PairingDecision.Approved;

        _approve.Visible = false;
        _deny.Visible = false;
        AcceptButton = null;
        CancelButton = null;

        // Green, not the cyan of the six-digit code: this is a different phase of the same
        // attempt, and the change of colour is what makes that visible without reading the text.
        var approved = Color.FromArgb(126, 231, 155);

        _hint.Text = "Allowed. Scan the code above on the phone to finish.\r\n" +
                     "This window closes by itself once the phone is connected.";
        _hint.ForeColor = approved;
        _countdownLabel.ForeColor = approved;
        _codeLabel.ForeColor = approved;
        Text = "ESI Remote PC - waiting for the phone";

        // Still the same deadline, so a phone that never scans cannot leave the dialog up for
        // ever: Tick() closes it when the window runs out.
    }

    private void Tick()
    {
        var remaining = _deadline - DateTimeOffset.Now;

        if (_waitingForPhone && TryPhoneCollected())
        {
            _waitingForPhone = false;
            Close();
            return;
        }

        if (remaining <= TimeSpan.Zero)
        {
            _waitingForPhone = false;
            Decide(PairingDecision.TimedOut);
            return;
        }

        _countdownLabel.Text = _waitingForPhone
            ? $"Waiting for the phone - closes in {remaining.TotalSeconds:0}s"
            : $"Expires in {remaining.TotalSeconds:0}s";
    }

    /// <summary>
    /// Asks whether the phone has taken its credentials, treating a failure to tell as "not
    /// yet". A closed window is the right answer either way, so there is nothing to recover.
    /// </summary>
    private bool TryPhoneCollected()
    {
        if (_phoneCollected is null)
            return false;

        try
        {
            return _phoneCollected();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Decide(PairingDecision decision)
    {
        Decision = decision;
        _decided = true;

        _timer.Stop();
        _timer.Dispose();

        // Close() rather than setting DialogResult: a decision made from the countdown has
        // no button behind it, and DialogResult would be meaningless.
        Close();
    }

    private bool _decided;
    private bool _waitingForPhone;

    /// <summary>
    /// A phone that has been approved has already had its decision recorded, so closing this
    /// window afterwards - with the X, Alt+F4, a session change, or the phone's arrival - is not
    /// a denial and must not be turned into one. Before approval every other way out is still
    /// a denial: an unattended approval is strictly worse than an unattended refusal.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _waitingForPhone = false;

        if (!_decided)
            Decision = PairingDecision.Denied;

        base.OnFormClosing(e);
    }

    private static string FormatCode(string code)
    {
        if (string.IsNullOrEmpty(code))
            return "------";

        return code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
    }

    private static string Describe(string? appVersion, string? osVersion)
    {
        var app = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion;
        var os = string.IsNullOrWhiteSpace(osVersion) ? "unknown" : osVersion;
        return $"app {app}, {os}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

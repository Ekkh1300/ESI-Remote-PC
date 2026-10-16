using System.Collections.Concurrent;
using Esi.Platform.Session;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Esi.Session;

/// <summary>
/// Composition root for the session agent: the tray icon, the command channel, the desktop
/// state monitor, and the command dispatcher that ties them together.
/// <para>
/// Everything here runs inside a Windows Forms message loop, because two of the pieces
/// (the tray icon and the pairing prompt) are genuine UI. The long-running work — the pipe
/// pump, the capture loop, the heartbeat — is on its own threads, and the dispatcher hops
/// back onto the UI thread with <see cref="Control.BeginInvoke"/> for anything that shows a
/// window.
/// </para>
/// </summary>
internal sealed class SessionAgentRuntime : ApplicationContext
{
    private readonly SessionAgentOptions _options;
    private readonly Serilog.ILogger _log;
    private readonly SessionChannel _channel;
    private readonly DesktopStateMonitor _desktop;
    private readonly Esi.Platform.Capture.GdiScreenCaptureProvider _capture;
    private readonly Esi.Platform.Capture.ScreenStreamEncoder _encoder;
    private readonly Esi.Platform.Input.InputInjector _injector;
    private readonly CapturePump _capturePump;
    private readonly AudioPump _audioPump;
    private readonly InputPump _inputPump;

    /// <summary>
    /// Built here, in the interactive session, so that Core Audio can be opened. The service
    /// cannot do this: Core Audio is a per-session API, and its session 0 has no endpoint, which
    /// is why every volume control it served came back refused (D43).
    /// </summary>
    private readonly Esi.Platform.Media.MediaController _media;

    /// <summary>
    /// Pairing attempts whose phone has taken its credentials, keyed by attempt id.
    /// <para>
    /// A prompt outliving its approval is what made this necessary, and it is also what makes a
    /// plain flag wrong: the service can learn that a phone has collected before the prompt for
    /// that attempt has finished being constructed. Additions, so the news survives that race.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _collectedPairings = new();
    private readonly CancellationTokenSource _stopping = new();

    private NotifyIcon? _tray;
    private MessageLoopAnchor? _anchor;
    private bool _shuttingDown;

    public SessionAgentRuntime(SessionAgentOptions options)
    {
        _options = options;
        _log = Log.Logger;

        _channel = new SessionChannel(options.ChannelName, _log.ForContext("SourceContext", "Esi.Session.Channel"));
        _desktop = new DesktopStateMonitor(_log);
        _capture = new Esi.Platform.Capture.GdiScreenCaptureProvider(
            new SerilogLoggerAdapter<Esi.Platform.Capture.GdiScreenCaptureProvider>(_log));
        _encoder = new Esi.Platform.Capture.ScreenStreamEncoder(
            new SerilogLoggerAdapter<Esi.Platform.Capture.ScreenStreamEncoder>(_log));
        _injector = new Esi.Platform.Input.InputInjector(
            new SerilogLoggerAdapter<Esi.Platform.Input.InputInjector>(_log));
        _media = new Esi.Platform.Media.MediaController(
            new SerilogLoggerAdapter<Esi.Platform.Media.MediaController>(_log));

        _capturePump = new CapturePump(_log, _capture, _encoder, _channel);
        _audioPump = new AudioPump(_log, _channel);
        _inputPump = new InputPump(_log, _injector);

        _channel.CommandReceived += OnCommandAsync;
    }

    public void Run()
    {
        _log.Information("ESI Remote PC session agent starting (session {Session}, user {User})",
            _options.SessionId, Environment.UserName);

        _desktop.Start();
        _desktop.StateChanged += OnDesktopStateChanged;

        _inputPump.Start();
        ApplyScreenGeometry();

        // The anchor has to exist, and have a window handle, before the command channel is
        // started. Post() is a BeginInvoke, and BeginInvoke throws "Invoke or BeginInvoke
        // cannot be called on a control until the window handle has been created" if the
        // handle is not there yet - so a pairing prompt delivered during startup produced an
        // error on the PC and no dialog at all, while the phone sat waiting for an approval
        // that could never be clicked. Eight of those were sitting in the log.
        //
        // Creating the handle before Application.Run starts pumping is not premature: the
        // messages simply queue on a thread that has not started reading them yet, and are
        // waiting when it does.
        _anchor = new MessageLoopAnchor(() => Shutdown("the message loop ended"));
        _ = _anchor.Handle;

        _channel.RunAsync(_stopping.Token).ContinueWith(
            task => Log.Fatal(task.Exception, "The command channel terminated"),
            TaskContinuationOptions.OnlyOnFaulted);

        _ = HeartbeatAsync(_stopping.Token);

        // A hidden form supplies the message loop without putting anything on screen. The
        // tray icon is the only thing the user should ever see from this process.
        _tray = BuildTrayIcon();
        _tray.Visible = true;

        UpdateTrayText();
        _log.Information("Session agent is ready");

        Application.Run(_anchor);
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("ESI Remote PC", null, (_, _) => { });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open pairing page", null, (_, _) => OpenPairingPage());
        menu.Items.Add("Remote control status", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());

        // Named for what it does rather than for the click. Exiting is not for the session -
        // the service supervises this process and would otherwise start another one within
        // three seconds (D74) - so the label says how long the exit lasts.
        menu.Items.Add("Exit until the next sign-in", null, (_, _) => Shutdown("the tray menu"));

        var tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "ESI Remote PC",
            ContextMenuStrip = menu,
            Visible = false,
        };

        tray.DoubleClick += (_, _) => ShowStatus();
        return tray;
    }

    /// <summary>
    /// The mark this executable and the installer's shortcuts already carry, falling back to
    /// the generic Windows one if it cannot be read.
    /// <para>
    /// It used to be <see cref="SystemIcons.Application"/> unconditionally - the blank page
    /// Windows draws for a program with no icon of its own. So the tray said "some other
    /// program" while a shortcut to the very same binary, three menus away on the same
    /// desktop, carried the product's mark. Two places disagreed about what the user was
    /// looking at, and the tray was the wrong one (D74).
    /// </para>
    /// <para>
    /// Read from the executable rather than from a second copy of the file. The icon is
    /// embedded at build time by <c>ApplicationIcon</c> in the project, from the same
    /// <c>release\esi.ico</c> the installer uses for its shortcuts - one image, so the tray,
    /// Explorer and the Start menu cannot drift apart.
    /// </para>
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            using var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted is null)
                return SystemIcons.Application;

            // ExtractAssociatedIcon hands back a 32-pixel image and the tray draws at 16.
            // Scaling here rather than letting the shell guess keeps the mark from becoming
            // a blurry suggestion of itself.
            using var sixteen = new Icon(extracted, new Size(16, 16));
            return (Icon)sixteen.Clone();
        }
        catch (Exception ex)
        {
            // A tray icon is not worth a session agent that will not start.
            Log.Warning(ex, "Could not read this executable's icon; using the generic one");
            return SystemIcons.Application;
        }
    }

    private void UpdateTrayText()
    {
        if (_tray is null)
            return;

        // The tray tooltip is capped at 63 characters by the shell; anything longer is
        // silently truncated, which looks like a bug.
        var state = !_channel.IsConnected
            ? "waiting for the agent service"
            : _desktop.IsLocked
                ? "locked"
                : "ready";

        _tray.Text = $"ESI Remote PC - {state}";
    }

    private void ShowStatus()
    {
        var connected = _channel.IsConnected;
        var capturing = _capturePump.IsRunning;

        var message =
            $"Service channel: {(connected ? "connected" : "waiting")}\r\n" +
            $"Windows session: {_options.SessionId}\r\n" +
            $"Signed in as: {Environment.UserName}\r\n" +
            $"Workstation: {(_desktop.IsLocked ? "locked" : "unlocked")}\r\n" +
            $"Screen capture: {(capturing ? "running" : "idle")}";

        MessageBox.Show(
            message,
            "ESI Remote PC",
            MessageBoxButtons.OK,
            connected ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void OpenPairingPage()
    {
        // The landing page lives in the service; there is nothing to show without it.
        var url = $"https://localhost:{Esi.Protocol.ProtocolConstants.DefaultHttpsPort}/pairing";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not open the pairing page");
            MessageBox.Show($"Could not open a browser.\r\n\r\nOpen this address manually:\r\n{url}",
                "ESI Remote PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // -----------------------------------------------------------------------------
    // Command dispatch
    // -----------------------------------------------------------------------------

    private async Task OnCommandAsync(SessionCommandEnvelope command)
    {
        switch (command.T)
        {
            case SessionChannelProtocol.Commands.Ping:
            case SessionChannelProtocol.Commands.RefreshState:
            case SessionChannelProtocol.Commands.SyncNow:
                await SendStateAsync().ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.Lock:
                await LockAsync().ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.SignOut:
                await SignOutAsync(command.Flag == true).ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.Suspend:
                await CommandResultAsync(command, "suspend",
                    // Suspension is refused outright: a suspended PC cannot answer the wake
                    // packet, and the phone would see a timeout instead of an honest error.
                    succeeded: false,
                    "Suspend is handled by the Windows service, not the session agent.")
                    .ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.Input:
                if (command.Input is { } pending)
                {
                    // The sequence number exists only to order events on the wire; the
                    // injector does not use it, and the wire format keeps it so a future
                    // client can ask for a resend.
                    _inputPump.Enqueue(new Esi.Protocol.RemoteInputRequest
                    {
                        Kind = pending.Kind,
                        X = pending.X,
                        Y = pending.Y,
                        Button = pending.Button,
                        Down = pending.Down,
                        DeltaX = pending.DeltaX,
                        DeltaY = pending.DeltaY,
                        Code = pending.Code,
                        Text = pending.Text,
                        Mods = pending.Mods,
                        DisplayId = pending.DisplayId,
                    });
                }
                break;

            case SessionChannelProtocol.Commands.CaptureStart:
                StartCapture(command);
                break;

            case SessionChannelProtocol.Commands.CaptureStop:
                _capturePump.Stop();
                _encoder.Reset();
                await CommandResultAsync(command, "capture.stop", true, "The screen stream was stopped.")
                    .ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.AudioStart:
                // The reply says the request was accepted, not that sound is coming. The device
                // opens asynchronously and can refuse afterwards; the honest confirmation is the
                // phone's meter moving, and the failure is in this agent's log. Claiming success
                // here would put the two on different stories and the user in the middle.
                _audioPump.Start(command.DeviceId ?? "");
                await CommandResultAsync(
                    command,
                    "audio.start",
                    _audioPump.IsRunning,
                    _audioPump.IsRunning
                        ? "The output device is open for loopback capture."
                        : "The output device could not be opened for loopback capture. See the agent log.")
                    .ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.AudioStop:
                _audioPump.Stop();
                await CommandResultAsync(command, "audio.stop", true, "Audio capture was stopped.")
                    .ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.PairingPrompt:
                await ShowPairingPromptAsync(command).ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.PairingCompleted:
                // Not answered with a CommandResult: the service already knows, having released
                // the secrets itself, and a reply would only be a second thing to correlate.
                NotePairingCollected(command.CorrelationId);
                break;

            case SessionChannelProtocol.Commands.LaunchApp:
                await LaunchAsync(command).ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.TerminateApp:
                await TerminateAsync(command).ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.MediaKey:
                InjectMediaKey(command);
                break;

            case SessionChannelProtocol.Commands.MediaTransport:
                InjectMediaTransport(command);
                break;

            case SessionChannelProtocol.Commands.MediaVolumeSet:
                SetMediaVolume(command);
                break;

            case SessionChannelProtocol.Commands.MediaStateQuery:
                ReplyMediaState(command);
                break;

            case SessionChannelProtocol.Commands.ShowToast:
                await CommandResultAsync(command, "toast.show", true,
                    "Toasts are raised by the Windows service, not the session agent.").ConfigureAwait(false);
                break;

            default:
                _log.Debug("Ignoring unknown command {Command}", command.T);
                break;
        }
    }

    private async Task SendStateAsync()
    {
        var displays = _capture.EnumerateDisplays()
            .Select(d => new SessionAgentDisplay
            {
                Id = d.Id,
                Name = d.Name,
                X = d.X,
                Y = d.Y,
                Width = d.Width,
                Height = d.Height,
                IsPrimary = d.IsPrimary,
                Rotation = d.Rotation,
                RefreshRateHz = d.RefreshRateHz,
                ScalePercent = d.ScalePercent,
            })
            .ToList();

        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        await _channel.SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.StateChanged,
            IsLocked = _desktop.IsLocked,
            UserName = $"{Environment.UserDomainName}\\{Environment.UserName}",
            SessionId = _options.SessionId,
            ProcessId = Environment.ProcessId,
            ProcessName = "ESI.RemotePc.Session",
            Detail = _desktop.IsSuspended ? "sleeping" : "active",
            Displays = displays,
        }).ConfigureAwait(false);

        if (primary is not null)
            ApplyScreenGeometry(primary.Width, primary.Height);
    }

    private Task LockAsync() => ExecuteNativeAsync(
        "lock",
        () => Esi.Platform.Native.USER32.LockWorkStation(),
        "The workstation was locked.");

    private Task SignOutAsync(bool force) => ExecuteNativeAsync(
        "signout",
        () => Esi.Platform.Native.USER32.ExitWindowsEx(
            force ? Esi.Platform.Native.WinConst.EWX_LOGOFF | Esi.Platform.Native.WinConst.EWX_FORCE
                  : Esi.Platform.Native.WinConst.EWX_LOGOFF,
            Esi.Platform.Native.WinConst.SHTDN_REASON_FLAG_USER_DEFINED),
        "The user was signed out.");

    private async Task ExecuteNativeAsync(string command, Func<bool> action, string successDetail)
    {
        bool ok;
        string? error = null;

        try
        {
            ok = action();

            if (!ok)
            {
                var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                error = $"{command} failed with Win32 error {code}";

                // A locked or disconnected session is the expected outcome of signing out, not
                // a fault, and the notification that follows will say so.
                if (code is 0x5 or 0x400 or 0x4C7 or 0x4C8)
                {
                    error = $"{command} was acknowledged by Windows (error {code}); the session is changing state";
                    ok = true;
                }
            }
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        await _channel.SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.CommandResult,
            CorrelationId = command,
            Succeeded = ok,
            Detail = ok ? successDetail : null,
            Error = error,
        }).ConfigureAwait(false);
    }

    private void StartCapture(SessionCommandEnvelope command)
    {
        if (string.IsNullOrWhiteSpace(command.DeviceId))
        {
            _ = CommandResultAsync(command, "capture.start", false, "No device id was supplied.");
            return;
        }

        try
        {
            _capturePump.Start(
                command.DeviceId,
                command.DisplayId ?? 0,
                command.Fps ?? 15,
                command.Quality ?? 70,
                command.GridCols ?? 4,
                command.GridRows ?? 4);

            _ = CommandResultAsync(command, "capture.start", true, "The screen stream started.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Starting the capture failed");
            _ = CommandResultAsync(command, "capture.start", false, ex.Message);
        }
    }

    private Task ShowPairingPromptAsync(SessionCommandEnvelope command)
    {
        var request = command.Prompt;
        if (request is null)
            return CommandResultAsync(command, "pairing.prompt", false, "No prompt payload was supplied.");

        // A lock screen, a UAC prompt or a full-screen app can make a modal dialog appear on a
        // desktop the user is not looking at. Showing it anyway is the right call — the pairing
        // must not time out because somebody was playing a game — but it is logged so the
        // situation is not a mystery afterwards.
        if (_desktop.IsLocked)
            _log.Information("Showing the pairing prompt while the workstation is locked");

        var result = new TaskCompletionSource<PairingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The phone's arrival is announced on this channel (D36). Registered before the dialog is
        // built so a phone that is quick enough to collect while the form is still being shown is
        // not lost: NotifyPairingCompleted sets the flag and the form's next tick closes it.
        _collectedPairings.AddOrUpdate(request.PairingId, 0, (_, was) => was + 1);

        void Present()
        {
            try
            {
                // The decision is taken from `onDecided`, which the form calls the moment the
                // person decides - not from `form.Decision` after ShowDialog returns.
                //
                // Both halves of that used to be true in different builds and together they are
                // this bug (D41). Reading after the dialog closed was correct while the dialog
                // closed on the decision (D34). Then D36 made the dialog survive its own approval,
                // because the QR on it is the only copy of the provisioning secret. The two
                // assumptions cannot both hold: a form that stays open and a caller that waits
                // for it to close means the click was reported a whole pairing window late, by
                // which time the agent had expired the attempt and thrown the approval away. The
                // button worked; the report did not arrive.
                using var form = new PairingPromptForm(
                    request,
                    phoneCollected: () => WasCollected(request.PairingId),
                    onDecided: decision => _ = result.TrySetResult(decision));

                form.ShowDialog();

                // Belt and braces for the paths that end the window without going through
                // Decide: a form torn down mid-flight, or a Close that raced the callback.
                _ = result.TrySetResult(form.Decision);
                form.Close();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The pairing prompt could not be shown");
                _ = result.TrySetResult(PairingDecision.TimedOut);
            }
        }

        var anchor = _anchor;
        if (anchor is null)
            return CommandResultAsync(command, "pairing.prompt", false, "There is no UI thread to show the prompt on.");

        try
        {
            anchor.Post(Present);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not post the pairing prompt to the UI thread");
            return CommandResultAsync(command, "pairing.prompt", false, ex.Message);
        }

        return CompletePromptAsync(command, request, result.Task);
    }

    /// <summary>
    /// Records that the phone holding an attempt's provisioning secret has taken its credentials.
    /// </summary>
    /// <remarks>
    /// Counted rather than flagged, because a pairing can be approved twice over its life — once
    /// through this channel and once by a console — and a message that arrived before the dialog
    /// was raised must not be forgotten. The count is dropped when the attempt is finally
    /// resolved, so the table cannot grow without bound.
    /// </remarks>
    private void NotePairingCollected(string? pairingId)
    {
        if (string.IsNullOrWhiteSpace(pairingId))
        {
            _log.Warning("The service reported a completed pairing with no attempt id; ignoring it");
            return;
        }

        _collectedPairings.AddOrUpdate(pairingId, 1, (_, was) => was + 1);
        _log.Information("The phone collected the credentials for pairing {PairingId}", pairingId);
    }

    /// <summary>Whether the phone for this attempt has taken its credentials.</summary>
    private bool WasCollected(string pairingId)
        => _collectedPairings.TryGetValue(pairingId, out var count) && count > 0;

    private void ForgetPairing(string pairingId) => _collectedPairings.TryRemove(pairingId, out _);

    private async Task CompletePromptAsync(SessionCommandEnvelope command, PairingPromptRequest request,
        Task<PairingDecision> decision)
    {
        // The service owns the expiry; this is a backstop so a dialog that is never answered
        // (machine left unattended for an hour) does not leave the task pending forever.
        var completed = await Task.WhenAny(decision, Task.Delay(TimeSpan.FromSeconds(request.ExpiresInSeconds + 30)))
            .ConfigureAwait(false);

        var answer = completed == decision ? await decision.ConfigureAwait(false) : PairingDecision.TimedOut;

        try
        {
            // Dropped here, after the answer is on the wire. A phone whose credentials were
            // released has nothing more to say, and the attempt is over either way.
            ForgetPairing(request.PairingId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not clear the collected state for pairing {PairingId}", request.PairingId);
        }

        await _channel.SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.PairingResult,
            ResponseTo = request.PairingId,
            Name = request.DeviceName,
            Detail = answer switch
            {
                PairingDecision.Approved => "approved",
                PairingDecision.Denied => "denied",
                _ => "expired",
            },
            CorrelationId = request.UserCode,
            Succeeded = answer == PairingDecision.Approved,
        }).ConfigureAwait(false);
    }

    private async Task LaunchAsync(SessionCommandEnvelope command)
    {
        if (string.IsNullOrWhiteSpace(command.Command))
        {
            await CommandResultAsync(command, "app.launch", false, "No executable was supplied.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command.Command,
                Arguments = command.Arguments ?? string.Empty,
                WorkingDirectory = command.WorkingDirectory
                                     ?? Path.GetDirectoryName(command.Command)
                                     ?? Environment.CurrentDirectory,
                UseShellExecute = true,
            };

            using var process = Process.Start(startInfo);

            await _channel.SendAsync(new SessionAgentMessage
            {
                T = SessionChannelProtocol.Events.LaunchResult,
                CorrelationId = command.Id,
                Ok = process is not null,
                ProcessId = process?.Id,
                ProcessName = process?.ProcessName,
                Succeeded = process is not null,
                Error = process is null ? "The process did not start." : null,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _channel.SendAsync(new SessionAgentMessage
            {
                T = SessionChannelProtocol.Events.LaunchResult,
                CorrelationId = command.Id,
                Ok = false,
                Succeeded = false,
                Error = ex.Message,
            }).ConfigureAwait(false);
        }
    }

    private async Task TerminateAsync(SessionCommandEnvelope command)
    {
        if (string.IsNullOrWhiteSpace(command.Id) || !int.TryParse(command.Id, out var pid))
        {
            await CommandResultAsync(command, "app.terminate", false, "No process id was supplied.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);

            await CommandResultAsync(command, "app.terminate", true, $"Process {pid} was terminated.")
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Already gone. That is the requested end state, so it is not a failure.
            await CommandResultAsync(command, "app.terminate", true, $"Process {pid} had already exited.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CommandResultAsync(command, "app.terminate", false, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Injects a volume or mute key from here, in the interactive session.
    /// <para>
    /// The service cannot do this. It runs in session 0, where the volume keys are injected into
    /// a desktop nobody is watching and Core Audio cannot be opened to read the volume back, so
    /// every volume control the phone has silently came back as <c>409</c> with no reason. Here,
    /// in the signed-in user's session, the same keys work (D43).
    /// </para>
    /// </summary>
    private void InjectMediaKey(SessionCommandEnvelope command)
    {
        if (command.VirtualKey is not { } vk || vk > ushort.MaxValue)
        {
            _ = CommandResultAsync(command, "media.key", false, "No virtual key was supplied.");
            return;
        }

        var action = command.Action ?? "media.key";
        var result = Esi.Platform.Media.MediaController.SendVolumeKeyFromSession(
            (ushort)vk, command.Repeats ?? 5, action);

        Log.Information("Media key {Action} (VK 0x{Vk:x2}) {Outcome} for {Device}",
            action, vk, result.Accepted ? "delivered" : "was not delivered", command.DeviceId ?? "the phone");

        _ = CommandResultAsync(command, "media.key", result.Accepted, result.Detail ?? $"{action} was sent.");
    }

    /// <summary>
    /// Injects a transport key from here, in the interactive session.
    /// <para>
    /// These were the quiet half of D43. The volume keys were refused loudly with a 409; the
    /// transport keys went through <c>SendInput</c> from session 0 as well, but from there the
    /// call <i>succeeds</i> - the events are queued into a desktop nobody is watching - so the
    /// service answered 200 and nothing happened. Play and pause looked like they worked, and
    /// only failed in the one way that has no error to notice.
    /// </para>
    /// </summary>
    private void InjectMediaTransport(SessionCommandEnvelope command)
    {
        if (!Enum.TryParse<Esi.Protocol.MediaAction>(command.Action, ignoreCase: true, out var action) ||
            Esi.Platform.Media.MediaController.TransportVirtualKey(action) is not { } vk)
        {
            _ = CommandResultAsync(command, "media.transport", false,
                $"'{command.Action}' is not a transport action.");
            return;
        }

        var result = Esi.Platform.Media.MediaController.SendVolumeKeyFromSession(vk, 1, action.ToString());

        Log.Information("Media transport {Action} (VK 0x{Vk:x2}) {Outcome} for {Device}",
            action, vk, result.Accepted ? "delivered" : "was not delivered",
            command.DeviceId ?? "the phone");

        _ = CommandResultAsync(command, "media.transport", result.Accepted,
            result.Detail ?? $"{action} was sent.");
    }

    /// <summary>
    /// Moves the volume to an absolute target from here.
    /// <para>
    /// This is the one place the walk can be computed, because it is the one place the current
    /// volume can be read: Core Audio is a per-session API, so the service's session 0 gets
    /// nothing back and its <c>SetVolume</c> refused outright. Here a real
    /// <see cref="Esi.Platform.Media.MediaController"/> does the read, presses the keys, and reads
    /// again (D43).
    /// </para>
    /// <para>
    /// The read is also what makes the walk *correct*, which for a long time it was not. The old
    /// code took <c>GetState().Volume</c>, which reports 0 when the volume cannot be read, so the
    /// walk was always computed from a base of zero. Asking for 30 on a PC already at 100 pressed
    /// volume-up thirty times; asking for 60 pressed sixty. Every one of those replies said the
    /// volume had been delivered, and the PC never moved.
    /// </para>
    /// </summary>
    private void SetMediaVolume(SessionCommandEnvelope command)
    {
        var target = Math.Clamp(command.Repeats ?? 50, 0, 100);
        var action = command.Action ?? "volumeSet";

        var before = _media.GetState();
        Esi.Protocol.MediaActionResponse result;

        if (!before.VolumeKnown)
        {
            // A walk is computed from where the volume is, so a volume we cannot read leaves no
            // honest walk to send. Pressing `target` presses from an assumed zero is the bug
            // above, and it is far worse than saying no.
            result = new Esi.Protocol.MediaActionResponse
            {
                Accepted = false,
                Action = action,
                Detail = "This PC's volume could not be read, so it cannot be moved to an exact " +
                         "level. The volume keys still work; use volumeUp/volumeDown instead.",
            };
        }
        else
        {
            // Same code the service would have run, not a private copy of it. This used to
            // re-derive the walk here, which is how two walk implementations ended up disagreeing
            // about how far a press moves: the endpoint reports 51 steps, so a press is ~1.96
            // points and pressing |delta| times travels twice as far as asked.
            result = new Esi.Protocol.MediaActionResponse
            {
                Accepted = _media.SetVolume(target).Accepted,
                Action = action,
                Volume = target,
                IsMuted = before.IsMuted,
            };
        }

        // Re-read after the presses land. The reply is the phone's only sight of the PC, and
        // reporting the volume as it was *before* the walk is how a control ends up arguing
        // with the number the user just set.
        var after = _media.GetState();

        Log.Information("Volume {Action} {From} -> {To} for {Device}: {Outcome}",
            action, before.VolumeKnown ? before.Volume.ToString() : "unknown", target,
            command.DeviceId ?? "the phone",
            result.Accepted ? "delivered" : "was not delivered");

        _ = CommandResultWithMediaAsync(command, "media.volumeSet", result.Accepted,
            result.Detail ?? $"{action} was sent.", after);
    }

    /// <summary>
    /// Answers a question about the PC's media state.
    /// <para>
    /// The service cannot answer this itself: it is in session 0, gets no audio endpoint, and
    /// used to report a confident 0 for a PC sitting at 100 (D43).
    /// </para>
    /// </summary>
    private void ReplyMediaState(SessionCommandEnvelope command)
    {
        var state = _media.GetState();

        Log.Debug("Media state asked for {Device}: volume {Volume} (known={Known})",
            command.DeviceId ?? "the phone", state.Volume, state.VolumeKnown);

        _ = CommandResultWithMediaAsync(command, "media.stateQuery", succeeded: true,
            state.VolumeKnown
                ? "The volume was read from the PC."
                : "The PC's volume could not be read.", state);
    }

    /// <summary>
    /// A command result that also carries the PC's media state.
    /// <para>
    /// Both parts matter and neither replaces the other: <c>Succeeded</c> says whether the
    /// command did what was asked, and the media fields say what the PC is doing now. A caller
    /// that read only the first would still be guessing at the second, which is what the phone
    /// was doing before this existed.
    /// </para>
    /// </summary>
    private Task CommandResultWithMediaAsync(
        SessionCommandEnvelope command,
        string name,
        bool succeeded,
        string? detail,
        Esi.Protocol.MediaStateResponse state)
        => _channel.SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.CommandResult,
            CorrelationId = command.CorrelationId,
            Succeeded = succeeded,
            Detail = succeeded ? detail : null,
            Error = succeeded ? null : detail,
            Volume = state.Volume,
            VolumeKnown = state.VolumeKnown,
            IsMuted = state.IsMuted,
            IsMuteKnown = state.IsMuteKnown,
            DefaultDevice = state.DefaultDevice,
        });

    private Task CommandResultAsync(SessionCommandEnvelope command, string name, bool succeeded, string? detail)
        => _channel.SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.CommandResult,
            CorrelationId = command.Id,
            Succeeded = succeeded,
            Detail = succeeded ? detail : null,
            Error = succeeded ? null : detail,
        });

    // -----------------------------------------------------------------------------
    // Background work
    // -----------------------------------------------------------------------------

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _channel.SendAsync(new SessionAgentMessage
                {
                    T = SessionChannelProtocol.Events.Heartbeat,
                    IsLocked = _desktop.IsLocked,
                    SessionId = _options.SessionId,
                    UserName = $"{Environment.UserDomainName}\\{Environment.UserName}",
                    ProcessId = Environment.ProcessId,
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void OnDesktopStateChanged()
    {
        // A locked desktop cannot be captured and must not be injected into, so the stream is
        // stopped rather than left producing black frames. It is restarted on unlock only if
        // the phone is still asking, which the service decides by re-issuing capture.start.
        if (_desktop.IsLocked && _capturePump.IsRunning)
        {
            _log.Information("The workstation locked; stopping the screen stream");
            _capturePump.Stop();
            _encoder.Reset();
            _ = _channel.SendAsync(new SessionAgentMessage
            {
                T = SessionChannelProtocol.Events.LockChanged,
                IsLocked = true,
            });
        }
        else if (!_desktop.IsLocked)
        {
            _ = _channel.SendAsync(new SessionAgentMessage
            {
                T = SessionChannelProtocol.Events.LockChanged,
                IsLocked = false,
            });
        }

        UpdateTrayText();
    }

    private void ApplyScreenGeometry(int? width = null, int? height = null)
    {
        var primary = _capture.EnumerateDisplays().FirstOrDefault(d => d.IsPrimary)
                      ?? _capture.EnumerateDisplays().FirstOrDefault();

        var w = width ?? primary?.Width ?? Esi.Platform.Native.USER32.GetSystemMetrics(0);
        var h = height ?? primary?.Height ?? Esi.Platform.Native.USER32.GetSystemMetrics(1);

        if (w > 0 && h > 0)
            _inputPump.SetScreenGeometry(w, h);
    }

    private void Shutdown(string reason)
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        _log.Information("The session agent is shutting down ({Reason})", reason);

        _stopping.Cancel();
        _capturePump.Stop();

        // Stopped here too. Shutdown is the only path out of this process and this pump was
        // the one thing on it nothing cancelled: its thread is a background thread, so it
        // could not hold the process open, but leaving a loop running against a cancelled
        // token while the rest of the agent tears down is a race nobody needs.
        _audioPump.Stop();

        // Say this was a decision before the pipe goes away. The service supervises this
        // process and starts a new one whenever a live session has none, so an exit that
        // arrives only as a broken pipe is a crash as far as it can tell - and within three
        // seconds the tray icon is back, with the menu item looking broken. That is exactly
        // what Exit did (D74).
        //
        // Bounded, and blocked on purpose: the write has to reach the pipe before the process
        // does, but waiting without a limit would turn an exit into a process that never
        // leaves, which is a worse version of the same bug.
        AnnounceUserStop();

        // Take the icon off the screen now rather than when the process finally unwinds.
        // Windows keeps drawing a NotifyIcon whose owner has gone until the mouse passes
        // over it, so an exit that worked left the mark sitting there - and an icon that
        // stays put after choosing Exit is the very picture of one that did not (D74).
        if (_tray is not null)
            _tray.Visible = false;

        _anchor?.RequestExit();
    }

    /// <summary>
    /// Tells the service that this agent is being closed by a person, so the supervisor does
    /// not immediately start another one.
    /// </summary>
    private void AnnounceUserStop()
    {
        try
        {
            var delivered = _channel.SendAsync(new SessionAgentMessage
            {
                T = SessionChannelProtocol.Events.AgentStop,
                UserName = $"{Environment.UserDomainName}\\{Environment.UserName}",
                SessionId = _options.SessionId,
                ProcessId = Environment.ProcessId,
                ProcessName = "ESI.RemotePc.Session",
                Detail = "exit requested from the tray menu",
            }).Wait(TimeSpan.FromSeconds(2));

            if (!delivered)
                _log.Warning(
                    "The stop request did not reach the service; it may start this agent again");
        }
        catch (Exception ex)
        {
            // Nothing to be done about it here - the process is leaving either way.
            _log.Warning(ex, "Could not tell the service this agent was stopped on purpose");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Cancel();

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            _anchor?.Dispose();
            _desktop.Dispose();
            _capturePump.Dispose();
            _inputPump.Dispose();
            _channel.DisposeAsync().GetAwaiter().GetResult();
            _capture.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// A zero-size, off-screen form whose only job is to own the message loop and to marshal
/// work onto the UI thread.
/// <para>
/// <see cref="ApplicationContext"/> alone cannot do this: it has no window handle, so
/// <c>Control.BeginInvoke</c> has nothing to post to. The form is created with
/// <see cref="FormWindowStyle.Hidden"/> and never shown, so it contributes nothing to the
/// desktop the user sees.
/// </para>
/// </summary>
internal sealed class MessageLoopAnchor : Form
{
    private readonly Action _onExit;

    public MessageLoopAnchor(Action onExit)
    {
        _onExit = onExit;

        Text = "ESI Remote PC session agent";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        // Positioned far off-screen rather than merely hidden: a form created with
        // ShowInTaskbar = false can still flash a sliver at (0,0) on the first layout pass,
        // which is a visible glitch on someone's desktop.
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
    }

    // Never let WinForms make this window visible. Application.Run(anchor) calls Show()
    // on the context's MainForm, and a 1x1 window parked at (-32000,-32000) would still be
    // a window the user could Alt-Tab to.
    protected override void SetVisibleCore(bool value)
    {
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0010 /* WM_CLOSE */)
        {
            // A session change or a logoff posts WM_CLOSE; treat it as a normal exit rather
            // than letting Windows tear the process down mid-capture.
            _onExit();
            return;
        }

        base.WndProc(ref m);
    }

    public void Post(Action action) => BeginInvoke(action);

    public void RequestExit()
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(Close);
    }
}

/// <summary>
/// Bridges Serilog to the <c>Microsoft.Extensions.Logging.ILogger&lt;T&gt;</c> the platform
/// layer was written against.
/// <para>
/// The platform layer takes the abstraction interface deliberately, so it can be tested with
/// a stub. Rather than restate those types, the session agent adapts its own logger once —
/// one adapter, one translation table, no per-call-site branching.
/// </para>
/// </summary>
internal sealed class SerilogLoggerAdapter<T>(Serilog.ILogger logger)
    : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logger.IsEnabled(Translate(logLevel));

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        logger.Write(Translate(logLevel), exception, formatter(state, exception));
    }

    private static LogEventLevel Translate(Microsoft.Extensions.Logging.LogLevel level) => level switch
    {
        Microsoft.Extensions.Logging.LogLevel.Trace => LogEventLevel.Verbose,
        Microsoft.Extensions.Logging.LogLevel.Debug => LogEventLevel.Debug,
        Microsoft.Extensions.Logging.LogLevel.Information => LogEventLevel.Information,
        Microsoft.Extensions.Logging.LogLevel.Warning => LogEventLevel.Warning,
        Microsoft.Extensions.Logging.LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}

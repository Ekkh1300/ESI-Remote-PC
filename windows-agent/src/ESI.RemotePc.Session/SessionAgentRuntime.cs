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
    private readonly InputPump _inputPump;
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

        _capturePump = new CapturePump(_log, _capture, _encoder, _channel);
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

        _channel.RunAsync(_stopping.Token).ContinueWith(
            task => Log.Fatal(task.Exception, "The command channel terminated"),
            TaskContinuationOptions.OnlyOnFaulted);

        _ = HeartbeatAsync(_stopping.Token);

        // A hidden form supplies the message loop without putting anything on screen. The
        // tray icon is the only thing the user should ever see from this process.
        _anchor = new MessageLoopAnchor(() => Shutdown("the message loop ended"));

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
        menu.Items.Add("Exit", null, (_, _) => Shutdown("the tray menu"));

        var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ESI Remote PC",
            ContextMenuStrip = menu,
            Visible = false,
        };

        tray.DoubleClick += (_, _) => ShowStatus();
        return tray;
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

            case SessionChannelProtocol.Commands.PairingPrompt:
                await ShowPairingPromptAsync(command).ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.LaunchApp:
                await LaunchAsync(command).ConfigureAwait(false);
                break;

            case SessionChannelProtocol.Commands.TerminateApp:
                await TerminateAsync(command).ConfigureAwait(false);
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

        void Present()
        {
            try
            {
                using var form = new PairingPromptForm(request);
                _ = result.TrySetResult(form.Decision);
                form.ShowDialog();
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

    private async Task CompletePromptAsync(SessionCommandEnvelope command, PairingPromptRequest request,
        Task<PairingDecision> decision)
    {
        // The service owns the expiry; this is a backstop so a dialog that is never answered
        // (machine left unattended for an hour) does not leave the task pending forever.
        var completed = await Task.WhenAny(decision, Task.Delay(TimeSpan.FromSeconds(request.ExpiresInSeconds + 30)))
            .ConfigureAwait(false);

        var answer = completed == decision ? await decision.ConfigureAwait(false) : PairingDecision.TimedOut;

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

        _anchor?.RequestExit();
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

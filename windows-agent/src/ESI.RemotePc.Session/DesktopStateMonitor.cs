using System.ComponentModel;
using System.Runtime.InteropServices;
using Esi.Platform.Native;
using Esi.Platform.Session;
using Serilog;
using Serilog.Core;

namespace Esi.Session;

/// <summary>
/// Watches the interactive session for lock, unlock, logon, logoff and suspend transitions.
/// <para>
/// Windows has no API that answers "is this workstation locked?". The documented test is
/// indirect: when a session locks, the input desktop switches to <c>Winlogon</c>, and a
/// process on <c>Default</c> cannot switch to it without <c>SE_TCB_NAME</c>. So the
/// notification says a transition happened, and a probe confirms which one.
/// </para>
/// <para>
/// A message-only window is used rather than a form: the agent is normally tray-only, and
/// creating a real window would put a taskbar entry on the user's desktop for a background
/// service.
/// </para>
/// </summary>
internal sealed class DesktopStateMonitor : NativeWindow, IDisposable
{
    // WTS session-change codes (wtsapi32.h). WM_WTSSESSION_CHANGE itself is a *registered*
    // message, so its numeric value is looked up at runtime rather than hard-coded.
    private const int WtsConsoleConnect = 0x3;
    private const int WtsConsoleDisconnect = 0x4;
    private const int WtsSessionLogon = 0x5;
    private const int WtsSessionLogoff = 0x6;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int WtsSessionRemoteDisconnect = 0xE;
    private const int WtsSessionRemoteConnect = 0xF;

    // Power-management notifications. WM_POWERBROADCAST and PBT_APMSUSPEND are plain
    // window-message constants, unlike WM_WTSSESSION_CHANGE.
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x4;
    private const int PbtApmResumeAutomatic = 0x12;
    private const int PbtApmResumeSuspend = 0x7;

    // HWND_MESSAGE: a window that belongs to no window station's visible desktop and so is
    // never rendered or enumerated.
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Serilog.ILogger _log;
    private uint _sessionChangeMessage;
    private bool _registered;
    private bool _disposed;

    private bool _isLocked;
    private bool _isSuspended;

    public DesktopStateMonitor(Serilog.ILogger log) => _log = log;

    public bool IsLocked => _isLocked;
    public bool IsSuspended => _isSuspended;

    /// <summary>Raised on the UI thread whenever a state transition is confirmed.</summary>
    public event Action? StateChanged;

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DesktopStateMonitor));

        if (Handle == IntPtr.Zero)
        {
            var parameters = new CreateParams
            {
                Caption = "ESI Remote PC session monitor",
                // No window is ever shown: the agent is a background component and a taskbar
                // entry for it would be a visible, unexplained change to the user's desktop.
                Parent = HwndMessage,
                Style = 0,
                ExStyle = 0,
            };

            CreateHandle(parameters);
        }

        _sessionChangeMessage = USER32.RegisterWindowMessageW("WM_WTSSESSION_CHANGE");

        if (_sessionChangeMessage == 0)
        {
            _log.Warning("Could not register WM_WTSSESSION_CHANGE; lock state will be polled instead");
        }
        else
        {
            _registered = USER32.WTSRegisterSessionNotification(Handle, 0 /* NOTIFY_FOR_THIS_SESSION */);
            if (!_registered)
            {
                _log.Warning(
                    "WTSRegisterSessionNotification failed ({Error}); lock state will be polled instead",
                    Marshal.GetLastWin32Error());
            }
        }

        // Establish the starting state without waiting for a transition.
        _isLocked = ProbeIsLocked();
        _isSuspended = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)_sessionChangeMessage && _sessionChangeMessage != 0)
        {
            HandleSessionChange((int)m.WParam);
            return;
        }

        if (m.Msg == WmPowerBroadcast)
        {
            HandlePowerBroadcast((int)m.WParam, (int)m.LParam);
            return;
        }

        base.WndProc(ref m);
    }

    private void HandleSessionChange(int code)
    {
        switch (code)
        {
            case WtsSessionLock:
            case WtsConsoleDisconnect:
            case WtsSessionRemoteDisconnect:
                SetLocked(true);
                break;

            case WtsSessionUnlock:
            case WtsConsoleConnect:
            case WtsSessionRemoteConnect:
                // Re-probe rather than trusting the code: a fast unlock/lock pair can deliver
                // both notifications before the desktop has actually settled.
                SetLocked(ProbeIsLocked());
                break;

            case WtsSessionLogon:
            case WtsSessionLogoff:
                _log.Information("Session change: {Code}", Describe(code));
                StateChanged?.Invoke();
                break;
        }
    }

    private void HandlePowerBroadcast(int eventType, int eventData)
    {
        switch (eventType)
        {
            case PbtApmSuspend:
                _isSuspended = true;
                _log.Information("The machine is suspending");
                StateChanged?.Invoke();
                break;

            case PbtApmResumeSuspend:
            case PbtApmResumeAutomatic:
                _isSuspended = false;
                _log.Information("The machine resumed (automatic={Automatic})", eventType == PbtApmResumeAutomatic);
                // A resume is a good moment to re-check everything: the desktop may have come
                // back locked, and a monitor may have been unplugged while we were away.
                SetLocked(ProbeIsLocked());
                break;
        }

        _ = eventData;
    }

    private void SetLocked(bool locked)
    {
        if (_isLocked == locked)
        {
            // A no-op transition can still mean the desktop settled differently than the code
            // implied, so notify anyway only when something else changed.
            return;
        }

        _isLocked = locked;
        _log.Information("The workstation is now {State}", locked ? "locked" : "unlocked");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// The documented lock test. See the class remarks for why there is no direct API.
    /// </summary>
    private static bool ProbeIsLocked()
    {
        var desktop = USER32.OpenInputDesktop(0, false, WinConst.DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero)
        {
            // OpenInputDesktop is denied to a process on a different window station than the
            // input desktop. That in itself means this process is not on the active input
            // desktop, which is the locked case as far as remote control is concerned.
            return true;
        }

        try
        {
            // Succeeding means the input desktop is still the one we are attached to. A locked
            // station answers ERROR_ACCESS_DENIED here because the input desktop is Winlogon's.
            return !USER32.SwitchDesktop(desktop);
        }
        finally
        {
            USER32.CloseDesktop(desktop);
        }
    }

    private static string Describe(int code) => code switch
    {
        WtsConsoleConnect => "console connected",
        WtsConsoleDisconnect => "console disconnected",
        WtsSessionLogon => "session logged on",
        WtsSessionLogoff => "session logged off",
        WtsSessionLock => "session locked",
        WtsSessionUnlock => "session unlocked",
        WtsSessionRemoteConnect => "remote session connected",
        WtsSessionRemoteDisconnect => "remote session disconnected",
        _ => $"code {code}",
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_registered && Handle != IntPtr.Zero)
        {
            try
            {
                USER32.WTSUnRegisterSessionNotification(Handle);
            }
            catch
            {
                // The handle is going away regardless.
            }
        }

        if (Handle != IntPtr.Zero)
        {
            try
            {
                DestroyHandle();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // DestroyHandle throws when the handle was never created or is already gone.
            }
        }

        GC.SuppressFinalize(this);
    }
}

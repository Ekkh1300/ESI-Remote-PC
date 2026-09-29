using System.Runtime.InteropServices;
using Esi.Protocol;
using Esi.Platform.Native;
using Esi.Platform.Session;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Power;

public enum PowerExecutionRoute
{
    /// <summary>Executed directly by the session-0 service.</summary>
    Service,

    /// <summary>Delegated to the per-user session agent (required for interactive-session-only APIs).</summary>
    SessionAgent,
}

public sealed record PowerOutcome(
    bool Accepted,
    string? EffectiveMode = null,
    long? ScheduledAt = null,
    string? Warning = null,
    bool RequiresInteractiveSession = false,
    string? Detail = null)
{
    public static PowerOutcome Fail(string detail, string? warning = null) => new(false, Warning: warning, Detail: detail);
    public static PowerOutcome Ok(string? detail = null, string? mode = null) => new(true, EffectiveMode: mode, Detail: detail);
}

/// <summary>
/// Performs power state changes.
/// <para>
/// The critical design point: <b>a service cannot do all of this</b>.
/// </para>
/// <list type="bullet">
///   <item><description><c>InitiateShutdown</c> (<i>not</i> <c>InitiateSystemShutdownEx</c>)
///   works from session 0 and is the current documented API. <c>SHUTDOWN_HYBRID</c> is
///   mandatory on Windows 8+, and pairing it with <c>SHUTDOWN_RESTART</c> avoids Fast
///   Startup — which would otherwise leave the NIC unable to receive a magic packet after
///   a restart.</description></item>
///   <item><description><c>SetSuspendState</c> needs <c>SE_SHUTDOWN_NAME</c> and works from
///   session 0, but suspending from session 0 does not cleanly suspend the interactive
///   session. When a user is logged in the action is routed to the session agent.</description></item>
///   <item><description><c>LockWorkStation</c> and <c>ExitWindowsEx(EWX_LOGOFF)</c> are
///   documented as usable <em>only</em> by processes in an interactive logon session, so
///   they are always delegated.</description></item>
/// </list>
/// </summary>
public sealed class PowerService(
    ILogger<PowerService> log,
    PrivilegeHelper privileges,
    ISessionAgentBridge sessionAgent)
{
    private long _lastStateChangeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private string? _pendingAction;

    public string? PendingAction => _pendingAction;

    public void MarkStateChanged() => Interlocked.Exchange(ref _lastStateChangeUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public long LastStateChange => Interlocked.Read(ref _lastStateChangeUnix);

    // -----------------------------------------------------------------------------
    // Shutdown / restart
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Shuts the machine down or restarts it. Uses <c>InitiateShutdownW</c> with
    /// <c>SHUTDOWN_HYBRID</c> (mandatory on Windows 8+) so a restart is a real
    /// power-cycle, keeping Wake-on-LAN working afterwards.
    /// </summary>
    public PowerOutcome Shutdown(bool restart, bool force, int delaySeconds, string? message)
    {
        privileges.Enable(WinConst.SE_SHUTDOWN_NAME);

        var flags = WinConst.SHUTDOWN_HYBRID;

        if (restart)
            flags |= WinConst.SHUTDOWN_RESTART;
        else
            flags |= WinConst.SHUTDOWN_POWEROFF;

        if (force)
        {
            // SHUTDOWN_FORCE_OTHERS: close applications without prompting. The user asked
            // for it explicitly, but the API still lets them cancel during the grace period.
            flags |= WinConst.SHUTDOWN_FORCE_OTHERS;
        }

        // Reason code: this is a user-initiated remote power action. The individual flags
        // are int-typed constants, so the OR has to be widened explicitly.
        const uint Reason = (uint)(WinConst.MAJOR_APPLICATION | WinConst.MINOR_MAINTENANCE |
                            WinConst.SHTDN_REASON_FLAG_USER_DEFINED | WinConst.SHTDN_REASON_FLAG_PLANNED);

        var grace = (uint)Math.Clamp(delaySeconds, 0, (int)WinConst.MAX_SHUTDOWN_TIMEOUT);
        var text = string.IsNullOrWhiteSpace(message) ? "ESI Remote PC" : message;

        var result = ADVAPI32.InitiateShutdownW(null, text, grace, flags, Reason);

        if (result != WinConst.ERROR_SUCCESS)
        {
            // InitiateShutdown returns the error code directly rather than setting
            // GetLastError, so the switch is on `result`, not on Marshal.GetLastWin32Error().
            return result switch
            {
                WinConst.ERROR_SHUTDOWN_IN_PROGRESS => PowerOutcome.Fail(
                    "A shutdown or restart is already in progress.",
                    warning: "Another power action is pending."),
                WinConst.ERROR_ACCESS_DENIED => PowerOutcome.Fail(
                    $"InitiateShutdown was denied (error {result}). The agent needs SeShutdownPrivilege and must run as LocalSystem.",
                    warning: "Agent is not running as LocalSystem."),
                WinConst.ERROR_NOT_READY => PowerOutcome.Fail(
                    "Fast user switching is enabled but no user is logged on.",
                    warning: "No interactive user is logged on."),
                _ => PowerOutcome.Fail($"InitiateShutdown failed with error {result}."),
            };
        }

        Interlocked.Exchange(ref _pendingAction, restart ? "restart" : "shutdown");
        MarkStateChanged();

        var effective = force ? "forced" : grace > 0 ? $"graceful ({grace}s)" : "immediate";

        return new PowerOutcome(
            Accepted: true,
            EffectiveMode: effective,
            ScheduledAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)grace,
            Warning: null,
            Detail: restart ? "Restart scheduled." : "Shutdown scheduled.");
    }

    // -----------------------------------------------------------------------------
    // Sleep / hibernate
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Suspends to RAM, or hibernates. When a user is logged in the action is delegated to
    /// the session agent so the interactive session is suspended cleanly rather than the
    /// service suspending the machine out from under it.
    /// </summary>
    public PowerOutcome Suspend(bool hibernate, bool force)
    {
        if (hibernate && !PowerStateProbe.SupportsHibernate())
        {
            // Fall back to sleep rather than silently failing, and say so.
            log.LogInformation("Hibernate requested but unavailable on this machine; falling back to sleep");
            hibernate = false;
        }

        if (sessionAgent.TrySend(PowerSessionCommand.Suspend, hibernate, out var detail))
        {
            MarkStateChanged();
            return new PowerOutcome(true, hibernate ? "hibernate" : "sleep", Detail: "Delegated to the user session agent.");
        }

        // No user session (or the session agent is not running): suspend from session 0.
        if (!privileges.Enable(WinConst.SE_SHUTDOWN_NAME))
        {
            return PowerOutcome.Fail(
                "SeShutdownPrivilege could not be enabled, so suspend is not available.",
                warning: "Agent is not running as LocalSystem.");
        }

        // Note: the bForce parameter is documented as having no effect. It is passed
        // through for signature fidelity, not because it changes behaviour.
        var ok = POWRPROF.SetSuspendState(hibernate, force, false);
        if (!ok)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return PowerOutcome.Fail(
                $"SetSuspendState failed with error {error}.",
                warning: hibernate ? "Hibernate may be disabled (powercfg /h off)." : null);
        }

        MarkStateChanged();
        return new PowerOutcome(true, hibernate ? "hibernate" : "sleep", Detail: "Suspended from the service session.");
    }

    // -----------------------------------------------------------------------------
    // Lock / sign out
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Locks the workstation. <c>LockWorkStation</c> is documented as callable only from a
    /// process on the interactive desktop with a logged-on user, so this always delegates.
    /// </summary>
    public PowerOutcome Lock()
    {
        if (sessionAgent.TrySend(PowerSessionCommand.Lock, false, out var detail))
        {
            MarkStateChanged();
            return new PowerOutcome(true, "lock", Detail: "Delegated to the user session agent.");
        }

        return new PowerOutcome(
            Accepted: false,
            Warning: "No user is logged in, or the session agent is not running.",
            RequiresInteractiveSession: true,
            Detail: detail ?? "Locking the workstation requires an interactive user session.");
    }

    /// <summary>
    /// Signs the current user out. <c>ExitWindowsEx(EWX_LOGOFF)</c> has the same
    /// interactive-session restriction, so this always delegates too.
    /// </summary>
    public PowerOutcome SignOut(bool force)
    {
        if (sessionAgent.TrySend(PowerSessionCommand.SignOut, force, out var detail))
        {
            MarkStateChanged();
            return new PowerOutcome(true, force ? "forced-signout" : "signout", Detail: "Delegated to the user session agent.");
        }

        return new PowerOutcome(
            Accepted: false,
            Warning: "No user is logged in, or the session agent is not running.",
            RequiresInteractiveSession: true,
            Detail: detail ?? "Signing out requires an interactive user session.");
    }

    // -----------------------------------------------------------------------------

    public PowerStatusResponse GetStatus()
    {
        var session = sessionAgent.CurrentSession();
        var loggedIn = session.HasValue;

        return new PowerStatusResponse
        {
            State = ResolveState(loggedIn),
            IsLocked = sessionAgent.IsWorkstationLocked,
            IsSleeping = sessionAgent.IsSuspended,
            IsHibernating = false,
            ActiveSessionId = session ?? 0xFFFFFFFF,
            ActiveUser = sessionAgent.ActiveUser,
            LoggedIn = loggedIn,
            SupportsSleep = PowerStateProbe.SupportsSleep(),
            SupportsHibernate = PowerStateProbe.SupportsHibernate(),
            LastBoot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() -
                       (long)TimeSpan.FromMilliseconds(Native.KERNEL32.GetTickCount64()).TotalSeconds,
            LastStateChange = LastStateChange,
            PendingPowerAction = _pendingAction,
        };
    }

    private string ResolveState(bool loggedIn)
    {
        if (sessionAgent.IsSuspended)
            return "sleeping";
        if (sessionAgent.IsWorkstationLocked)
            return "locked";
        if (loggedIn)
            return "online";
        return _pendingAction is null ? "online" : _pendingAction;
    }
}

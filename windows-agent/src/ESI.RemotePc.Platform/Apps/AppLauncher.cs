using System.Diagnostics;
using System.Runtime.InteropServices;
using Esi.Platform.Native;
using Esi.Platform.Session;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Apps;

/// <summary>Outcome of a launch or terminate request.</summary>
public sealed record LaunchOutcome(bool Launched, int? ProcessId, string RunAs, string? Detail = null);

/// <summary>
/// Starts and stops user applications.
/// <para>
/// The important constraint: a service runs in <b>session 0</b>, which has no visible
/// desktop. A process started there is invisible to the user, so "launch Chrome" from a
/// service would appear to do nothing. The documented way to start a process in the
/// interactive session is <c>CreateProcessAsUserW</c> with the session's user token and
/// <c>STARTUPINFO.lpDesktop = "winsta0\\default"</c>.
/// </para>
/// <para>
/// Rather than doing that in-process, the default route is to hand the request to the
/// per-user session agent, which already runs on the interactive desktop as the user. The
/// service falls back to <c>CreateProcessAsUserW</c> directly when no agent is running,
/// and only ever uses <c>CreateProcessW</c> in session 0 for explicitly headless entries.
/// </para>
/// </summary>
public sealed class AppLauncher(ILogger<AppLauncher> log, ISessionAgentBridge sessionAgent)
{
    /// <summary>Launches an application according to its configured run context.</summary>
    public LaunchOutcome Launch(AppLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Path))
            return new LaunchOutcome(false, null, spec.RunAs, "No executable path is configured.");

        if (!File.Exists(spec.Path))
            return new LaunchOutcome(false, null, spec.RunAs, $"The executable does not exist: {spec.Path}");

        var commandLine = BuildCommandLine(spec);

        return spec.RunAs switch
        {
            "service" => LaunchInServiceSession(spec, commandLine),
            "elevatedUser" => LaunchElevatedInUserSession(spec, commandLine),
            _ => LaunchInUserSession(spec, commandLine),
        };
    }

    /// <summary>Builds a properly quoted Windows command line from the executable and arguments.</summary>
    public static string BuildCommandLine(AppLaunchSpec spec)
    {
        var commandLine = new System.Text.StringBuilder();
        commandLine.Append('"').Append(spec.Path).Append('"');

        foreach (var argument in spec.Arguments)
        {
            commandLine.Append(' ');
            commandLine.Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    /// <summary>
    /// Quotes one argument per the documented <c>CommandLineToArgvW</c> rules: wrap in
    /// quotes and double any embedded backslashes that precede a quote, plus the quote
    /// itself.
    /// </summary>
    public static string QuoteArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument))
            return "\"\"";

        if (argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return argument;

        var builder = new System.Text.StringBuilder();
        builder.Append('"');

        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Escape the run of backslashes, then the quote, then re-emit the
                // backslashes so the following text is not re-escaped.
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        // Trailing backslashes must be doubled so they do not escape the closing quote.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    // ---------------------------------------------------------------------------
    // Routes
    // ---------------------------------------------------------------------------

    private LaunchOutcome LaunchInUserSession(AppLaunchSpec spec, string commandLine)
    {
        if (sessionAgent.IsConnected)
        {
            var ok = sessionAgent.TrySendLaunch(spec.Id, spec.Path, commandLine, spec.WorkingDirectory, elevated: false);
            if (ok)
                return new LaunchOutcome(true, null, "interactiveUser", "Sent to the user session agent.");
        }

        return LaunchViaCreateProcessAsUser(spec, commandLine, "interactiveUser");
    }

    private LaunchOutcome LaunchElevatedInUserSession(AppLaunchSpec spec, string commandLine)
    {
        if (sessionAgent.IsConnected)
        {
            var ok = sessionAgent.TrySendLaunch(spec.Id, spec.Path, commandLine, spec.WorkingDirectory, elevated: true);
            if (ok)
                return new LaunchOutcome(true, null, "elevatedUser", "Sent to the user session agent with elevation requested.");
        }

        // Without a session agent there is no way to present a UAC prompt on the
        // interactive desktop from session 0, so this is reported rather than attempted.
        return new LaunchOutcome(
            false,
            null,
            "elevatedUser",
            "Elevated launch needs the user session agent, which is not running. " +
            "Start 'ESI Remote PC Session' by logging in, or change this app's run context to 'Interactive user'.");
    }

    private LaunchOutcome LaunchInServiceSession(AppLaunchSpec spec, string commandLine)
    {
        try
        {
            var startup = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                dwFlags = 0,
            };

            if (ADVAPI32.CreateProcessW(
                    spec.Path,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    WinConst.CREATE_NO_WINDOW | WinConst.DETACHED_PROCESS,
                    IntPtr.Zero,
                    spec.WorkingDirectory,
                    ref startup,
                    out var pi))
            {
                if (pi.hProcess != IntPtr.Zero)
                    Native.KERNEL32.CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero)
                    Native.KERNEL32.CloseHandle(pi.hThread);

                log.LogInformation("Launched {App} in the service session as pid {Pid}", spec.Id, pi.dwProcessId);
                return new LaunchOutcome(true, (int)pi.dwProcessId, "service");
            }

            return new LaunchOutcome(false, null, "service",
                $"CreateProcessW failed with error {Marshal.GetLastWin32Error()}.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Launching {App} in the service session failed", spec.Id);
            return new LaunchOutcome(false, null, "service", ex.Message);
        }
    }

    private LaunchOutcome LaunchViaCreateProcessAsUser(AppLaunchSpec spec, string commandLine, string runAs)
    {
        var sessionId = Native.KERNEL32.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            return new LaunchOutcome(false, null, runAs,
                "No interactive user is logged in, so there is no desktop to launch onto.");
        }

        if (!TokenBootstrap.TryGetUserToken(sessionId, out var token, out var tokenError))
        {
            return new LaunchOutcome(false, null, runAs, tokenError);
        }

        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!USERENV.CreateEnvironmentBlock(out environment, token, false))
            {
                return new LaunchOutcome(false, null, runAs,
                    $"CreateEnvironmentBlock failed with error {Marshal.GetLastWin32Error()}.");
            }

            var startup = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                // This is what puts the process on the interactive desktop.
                lpDesktop = "winsta0\\default",
            };

            if (ADVAPI32.CreateProcessAsUserW(
                    token,
                    spec.Path,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    WinConst.CREATE_UNICODE_ENVIRONMENT,
                    environment,
                    spec.WorkingDirectory,
                    ref startup,
                    out var pi))
            {
                if (pi.hProcess != IntPtr.Zero)
                    Native.KERNEL32.CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero)
                    Native.KERNEL32.CloseHandle(pi.hThread);

                log.LogInformation("Launched {App} in session {Session} as pid {Pid}", spec.Id, sessionId, pi.dwProcessId);
                return new LaunchOutcome(true, (int)pi.dwProcessId, runAs);
            }

            return new LaunchOutcome(false, null, runAs,
                $"CreateProcessAsUserW failed with error {Marshal.GetLastWin32Error()}.");
        }
        finally
        {
            if (environment != IntPtr.Zero)
                USERENV.DestroyEnvironmentBlock(environment);
            if (token != IntPtr.Zero)
                Native.KERNEL32.CloseHandle(token);
        }
    }

    // ---------------------------------------------------------------------------
    // Termination
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Terminates processes whose main-module name matches. A graceful close is attempted
    /// first (WM_CLOSE) so the app can save state, and only then is the process killed.
    /// </summary>
    public (int Terminated, bool Forced) TerminateByProcessName(string processName, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return (0, false);

        var terminated = 0;
        var anyForced = false;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not enumerate processes named {Name}", processName);
            return (0, false);
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (force)
                    {
                        process.Kill(entireProcessTree: true);
                        anyForced = true;
                    }
                    else if (!process.CloseMainWindow())
                    {
                        process.Kill(entireProcessTree: false);
                        anyForced = true;
                    }

                    terminated++;
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Could not terminate pid {Pid} ({Name})", process.Id, processName);
                }
            }
        }

        return (terminated, anyForced);
    }

    /// <summary>Lists running processes, optionally filtered to those a launcher entry matches.</summary>
    public List<(int Pid, string Name, long WorkingSet, DateTime? StartedAt)> ListRunning(string? onlyProcessName = null)
    {
        var result = new List<(int, string, long, DateTime?)>();

        Process[] processes;
        try
        {
            processes = onlyProcessName is null
                ? Process.GetProcesses()
                : Process.GetProcessesByName(onlyProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not enumerate processes");
            return result;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (onlyProcessName is not null &&
                        !string.Equals(name, onlyProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add((process.Id, name, process.WorkingSet64, SafeStartTime(process)));
                }
                catch
                {
                    // Processes come and go; skip whatever raced us.
                }
            }
        }

        return result;
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            // Access denied for protected processes; not worth reporting.
            return null;
        }
    }
}

/// <summary>Everything needed to start one application.</summary>
public sealed class AppLaunchSpec
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }

    /// <summary>One of <c>interactiveUser</c>, <c>service</c> or <c>elevatedUser</c>.</summary>
    public string RunAs { get; init; } = "interactiveUser";
}

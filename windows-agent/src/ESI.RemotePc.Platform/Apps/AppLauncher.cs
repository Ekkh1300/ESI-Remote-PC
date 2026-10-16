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
    /// <summary>
    /// How long the last sample of a process that has gone away is kept, in milliseconds.
    /// </summary>
    private const long SampleRetentionMs = 5 * 60 * 1000;

    /// <summary>
    /// The last total processor time seen for each process id, and the monotonic reading it
    /// was taken at.
    /// </summary>
    /// <para>
    /// CPU per process is a difference between two observations, so this table is the first
    /// half of that difference. Keyed by pid, which Windows recycles - see
    /// <see cref="SampleCpuPercent"/> for how a recycled id is told apart from a real one.
    /// </para>
    private readonly Dictionary<int, (long Ticks, long At)> _cpuSeen = new();

    private readonly object _cpuGate = new();

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
    public List<(int Pid, string Name, long WorkingSet, DateTime? StartedAt, double? CpuPercent)> ListRunning(string? onlyProcessName = null)
    {
        var result = new List<(int, string, long, DateTime?, double?)>();

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

                    result.Add((process.Id, name, process.WorkingSet64, SafeStartTime(process), SampleCpuPercent(process)));
                }
                catch
                {
                    // Processes come and go; skip whatever raced us.
                }
            }
        }

        PruneCpuSamples();

        return result;
    }

    /// <summary>
    /// The process's share of the machine as a percentage of every core, 0-100, or null when
    /// it has only been seen once.
    /// </summary>
    /// <para>
    /// CPU per process is a <i>rate</i>, not a reading. It is the change in that process's own
    /// processor time divided by the change in wall time, so a single observation cannot
    /// produce it at all - there is nothing yet to divide. The first listing of a process
    /// therefore reports null and the second reports a real number, which is why the screen's
    /// CPU column fills in on a refresh rather than arriving complete.
    /// </para>
    /// <para>
    /// Reporting null rather than 0 matters: 0 means "idle", and an idle process that is
    /// actually saturating a core is exactly the thing this column exists to find. The DTO has
    /// no room for an absent number, so the wire carries 0 and the app's own "no reading yet"
    /// state is what decides between the two - a first sample is not evidence of idleness.
    /// </para>
    private double? SampleCpuPercent(Process process)
    {
        var pid = process.Id;

        // The reading and the comparison against the previous one belong to one instant, which
        // means inside the lock.
        //
        // They used to be taken before it. `/system/processes` and `/system/apps/running` both
        // walk the same singleton, so two requests *can* overlap, and whichever walk reaches a
        // pid first stores the fresher Ticks and At for it. The walk behind then compares its
        // own, older, numbers against those: `now - previous.At` comes out negative and
        // `used < previous.Ticks` comes out true - so a pid the other walk had just recorded
        // reports no reading, and the sample it had just written was being replaced with the
        // older one. The second half is the part worth stopping: it destroys good data rather
        // than merely declining to add to it.
        //
        // Read here instead, both values can only be ahead of what is stored: the clock is
        // monotonic and a process's own processor time only rises. So the two guards mean what
        // their comments say - a recycled pid, or an interval too short to divide by - and
        // nothing else.
        lock (_cpuGate)
        {
            long used;
            try
            {
                used = process.TotalProcessorTime.Ticks;
            }
            catch
            {
                // A process we cannot open the token for. It is listed without a CPU figure,
                // which is what the same call has always done for processes that exit mid-walk.
                return null;
            }

            var now = Environment.TickCount64;

            if (!_cpuSeen.TryGetValue(pid, out var previous))
            {
                _cpuSeen[pid] = (used, now);
                return null;
            }

            var elapsedMs = now - previous.At;

            // Two reasons, and only the second is about process ids.
            //
            // An interval of zero is not a rate - it is a division by nothing. Windows' tick
            // is coarse, about 15.6 ms by default, so two listings fired back to back can
            // sample the same process inside a single tick, and "no reading yet" is the honest
            // answer for that one listing. The sample is still recorded, so the *next* listing
            // has a longer interval to divide by. This is what a test that asked for a rate
            // after firing two requests without pausing was tripping over: every process on
            // the machine came back unmeasured, because no time had passed yet.
            //
            // A total that went backwards is a recycled process id: Windows hands the same
            // number to a different process, which is not one slowing down.
            if (elapsedMs <= 0 || used < previous.Ticks)
            {
                _cpuSeen[pid] = (used, now);
                return null;
            }

            _cpuSeen[pid] = (used, now);

            var cpuSeconds = (used - previous.Ticks) / (double)TimeSpan.TicksPerSecond;
            var wallSeconds = elapsedMs / 1000.0;
            var percent = cpuSeconds / wallSeconds / (double)Environment.ProcessorCount * 100.0;

            return Math.Clamp(percent, 0, 100);
        }
    }

    /// <summary>
    /// Drops the last sample of processes that have gone away, so a long-lived agent does not
    /// accumulate an entry for every pid it has ever seen.
    /// </summary>
    /// <para>
    /// Only walked once the table is large, because this runs once per listing and a machine
    /// with a few hundred processes would otherwise pay for a cleanup on every request.
    /// </para>
    private void PruneCpuSamples()
    {
        lock (_cpuGate)
        {
            if (_cpuSeen.Count <= 256)
            {
                return;
            }

            var cutoff = Environment.TickCount64 - SampleRetentionMs;
            var stale = new List<int>();

            foreach (var pair in _cpuSeen)
            {
                if (pair.Value.At < cutoff)
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (var pid in stale)
            {
                _cpuSeen.Remove(pid);
            }
        }
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

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Esi.Protocol;

namespace Esi.Cli;

/// <summary>
/// Registers, removes and inspects the Windows pieces that make up ESI Remote PC.
/// <para>
/// Two of them, and not both services, because of where they run:
/// </para>
/// <list type="bullet">
///   <item><description><b>ESIRemotePcAgent</b> — session 0, LocalSystem, owns the network
///   listener, the database and the power controls. Installed as a Windows service, and the
///   only one.</description></item>
///   <item><description><b>The session helper</b> — per-user, owns the interactive desktop:
///   screen capture, input injection, the lock and sign-out actions, the pairing prompt. It
///   has to be in the user's session to reach <c>winsta0\default</c>, and it is installed as a
///   <b>logon task</b> rather than as a service: a per-user service needs an empty
///   <c>obj=</c>, which the SCM rejects — silently, as far as the installer was concerned. See
///   <see cref="TaskCommands"/>.</description></item>
/// </list>
/// <para>
/// The service named <c>ESIRemotePcSession</c> still appears below, for a machine installed by
/// <c>esirc install</c>, which creates one. It is not how the helper is shipped.
/// </para>
/// <para>
/// The recovery actions and the <c>requiredPrivileges</c> registry value are set through
/// <c>sc.exe</c> and direct registry writes because <see cref="ServiceController"/> does not
/// model them. Everything else goes through the SCM API so a typo produces a real error
/// instead of a silently missing setting.
/// </para>
/// </summary>
internal static class ServiceManager
{
    private const string ServiceExe = "ESI.RemotePc.Agent.Service.exe";
    private const string SessionExe = "ESI.RemotePc.Session.exe";

    /// <summary>Result of an SCM or sc.exe operation, with the output kept for diagnosis.</summary>
    public readonly record struct CommandResult(int ExitCode, string Output)
    {
        public bool Succeeded => ExitCode == 0;
    }

    // -----------------------------------------------------------------------------
    // Elevation
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Whether the current process can actually create services.
    /// <para>
    /// Read from the token rather than by trying the operation: attempting service creation
    /// as a standard user produces a confusing <c>Access is denied</c> halfway through a
    /// multi-step install, whereas a clean up-front message tells the user to right-click
    /// and Run as administrator.
    /// </para>
    /// </summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Re-launches this executable elevated through UAC and returns true when it did.
    /// <para>
    /// Standard for a command-line tool: the alternative is to make the user open an
    /// elevated console by hand and retype the whole command, including the arguments.
    /// </para>
    /// </summary>
    public static bool RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        var quotedArgs = string.Join(' ', args.Select(Quote));

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = quotedArgs,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            });

            return true;
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user declined the UAC prompt. Not an error to report
            // loudly; they said no.
            return false;
        }
    }

    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;

    // -----------------------------------------------------------------------------
    // Install
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Installs the session-0 service, configured for automatic start with recovery, and -
    /// with <c>--include-session-agent</c> - the per-user session helper's logon task.
    /// <para>
    /// The task, not a second service: see the comment on the helper below for why the
    /// per-user service form was a warning followed by no helper.
    /// </para>
    /// </summary>
    public static int Install(InstallOptions options)
    {
        var serviceExe = options.ServicePath ?? ResolveSibling(ServiceExe);
        var sessionExe = options.SessionPath ?? ResolveSibling(SessionExe);

        if (!File.Exists(serviceExe))
        {
            Console.Error.WriteLine($"The agent executable was not found: {serviceExe}");
            return 2;
        }

        if (options.IncludeSessionAgent && !File.Exists(sessionExe))
        {
            Console.Error.WriteLine($"The session agent executable was not found: {sessionExe}");
            return 2;
        }

        Console.WriteLine("Installing ESI Remote PC...");
        Console.WriteLine($"  agent        {serviceExe}");
        if (options.IncludeSessionAgent)
            Console.WriteLine($"  session      {sessionExe}");

        // ---- The session-0 service ------------------------------------------------
        //
        // LocalSystem, not LocalService: the service needs SeTcbPrivilege to obtain a user
        // token for the console session (CreateProcessAsUser into it), and SeShutdownPrivilege
        // to shut the machine down. Both are unavailable to a virtual account.
        if (!CreateService(
                ProtocolConstants.ServiceName,
                "ESI Remote PC Agent",
                serviceExe,
                account: "LocalSystem",
                start: ServiceStartMode.Automatic,
                description: "LAN remote control agent for ESI Remote PC. Network API, telemetry, and power control.",
                out var createAgent))
        {
            Console.Error.WriteLine(createAgent);
            return 1;
        }

        Console.WriteLine($"  created {ProtocolConstants.ServiceName}");

        if (options.Autostart)
        {
            // The service opens its listener before the SCM is told the start succeeded; a
            // TLS failure at that point surfaces in the log, not here.
            var start = RunSc("start", ProtocolConstants.ServiceName);
            if (start.Succeeded)
                Console.WriteLine("  started the service");
            else
                Console.WriteLine($"  warning: the service did not start ({start.Output.Trim()})");
        }

        // ---- The interactive-session helper ----------------------------------------
        //
        // This used to be a second Windows service, created with obj= "." - the per-user
        // form the TaskCommands help text describes as "an empty obj=, which the SCM
        // rejects". CreateService got a warning printed after it and no helper afterwards:
        // an `esirc install --all` machine had screen capture, input injection and the
        // pairing prompt on none of its sessions, and the only hint was one console line.
        //
        // So the CLI now does what the Inno Setup installer has always done: register the
        // logon task, by calling into the exact code `esirc task register` runs. The helper
        // belongs to whoever is at the keyboard (winsta0\default, SendInput, the pairing
        // prompt), a task is the supported way to run an .exe there, and RegisterCore
        // starts it immediately too, so signing in was not required for it to exist.
        if (options.IncludeSessionAgent)
        {
            var registered = TaskCommands.RegisterSessionAgentTask();
            if (registered != 0)
            {
                Console.Error.WriteLine(
                    "  warning: the session agent's logon task was not registered. Screen " +
                    "capture, input injection and the pairing prompt will be unavailable on " +
                    "this machine until 'esirc task register' succeeds.");
            }
        }

        ConfigureRecovery(ProtocolConstants.ServiceName, options.RestartCount, options.RestartDelaySeconds);
        SetRequiredPrivileges(ProtocolConstants.ServiceName, ["SeTcbPrivilege", "SeShutdownPrivilege", "SeAssignPrimaryTokenPrivilege"]);

        Console.WriteLine();
        Console.WriteLine("Installed. The agent is listening on https://localhost:" +
                          options.Port + ".");
        Console.WriteLine("Run 'esirc pair show' on this machine, or open the pairing page, to pair a phone.");

        return 0;
    }

    private static bool CreateService(string name, string displayName, string binaryPath,
        string account, ServiceStartMode start, string description, out string error)
    {
        error = string.Empty;

        if (Exists(name))
        {
            error = $"{name} is already installed. Run 'uninstall' first, or 'install --force' to replace it.";
            return false;
        }

        var quoted = binaryPath.Contains(' ') ? $"\"{binaryPath}\"" : binaryPath;

        // sc.exe rather than ServiceController.Create: the managed API was deprecated for
        // creation in .NET 9 and offers no way to set the description or the account.
        //
        // `start=` takes a keyword, not the numeric ServiceStartMode. Passing the number makes
        // sc.exe print its usage and return "Invalid start= field", so the service was never
        // created at all and the CLI reported it as a failure to create.
        var args =
            $"create {name} binPath= {quoted} start= {StartModeKeyword(start)} DisplayName= \"{displayName}\"";

        if (account == ".")
        {
            // obj= is what makes this a per-user service. Without it Windows would look for
            // an account literally named ".", fail, and leave a broken service behind.
            args += " obj= ";
        }
        else
        {
            args += $" obj= {account}";
        }

        var result = RunScRaw(args);

        if (!result.Succeeded)
        {
            error = $"could not create {name}: {result.Output.Trim()}";
            return false;
        }

        // The description is a separate call; sc.exe has no combined form.
        RunScRaw($"description {name} \"{description}\"");

        if (account == ".")
        {
            // Required even after obj=, and the single most common reason a per-user service
            // silently never starts: the SCM needs to be told the service belongs to a user
            // session rather than to a service session.
            RunScRaw($"failure {name} reset= 86400 actions= restart/5000/restart/15000/restart/60000");
        }

        return true;
    }

    /// <summary>
    /// The word <c>sc.exe</c> expects for a start mode.
    /// <para>
    /// It accepts boot, system, auto, demand, disabled or delayed-auto, and rejects the
    /// numeric value that <see cref="ServiceStartMode"/> is built from with "Invalid start=
    /// field" - which is what made <c>esirc install</c> fail to create the service at all.
    /// </para>
    /// </summary>
    private static string StartModeKeyword(ServiceStartMode start) => start switch
    {
        ServiceStartMode.Boot => "boot",
        ServiceStartMode.System => "system",
        ServiceStartMode.Automatic => "auto",
        ServiceStartMode.Disabled => "disabled",
        _ => "demand",
    };

    /// <summary>
    /// Sets the failure-recovery actions, which the managed API does not expose.
    /// <para>
    /// Without recovery, a crash of the HTTPS listener takes remote control down until
    /// somebody physically visits the machine. The first restart is quick because most
    /// startup failures are a transient port conflict; the third is slow because if it is
    /// still failing, waiting longer is better than spinning.
    /// </para>
    /// </summary>
    private static void ConfigureRecovery(string serviceName, int restartCount, int delaySeconds)
    {
        if (restartCount <= 0)
            return;

        // sc.exe accepts at most three actions.
        var count = Math.Clamp(restartCount, 1, 3);
        var delays = new[] { delaySeconds, delaySeconds * 3, delaySeconds * 12 };

        var actions = string.Join('/', Enumerable.Range(0, count).Select(i => $"restart/{delays[i]}"));

        var result = RunScRaw($"failure {serviceName} reset= 86400 actions= {actions}");
        if (result.Succeeded)
            Console.WriteLine($"  recovery: {count} restart(s) after failure");
    }

    /// <summary>
    /// Writes the <c>requiredPrivileges</c> value the service host reads when it starts.
    /// <para>
    /// This is undocumented-but-load-bearing: without it the service account has the
    /// privilege <em>disabled</em>, so the first call to <c>CreateProcessAsUser</c> fails and
    /// the session agent can only be started by the logon scheduled task.
    /// </para>
    /// </summary>
    private static void SetRequiredPrivileges(string serviceName, string[] privileges)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);

        if (key is null)
        {
            Console.WriteLine($"  warning: could not open the registry key for {serviceName}");
            return;
        }

        key.SetValue("requiredPrivileges", string.Join(' ', privileges), Microsoft.Win32.RegistryValueKind.String);
    }

    // -----------------------------------------------------------------------------
    // Uninstall
    // -----------------------------------------------------------------------------

    public static int Uninstall(bool keepData)
    {
        Console.WriteLine("Removing ESI Remote PC...");

        // The legacy per-user service row, if an old CLI install managed to leave one; then
        // the session-0 service. The session helper itself is neither of them - it is a
        // scheduled task - which is why the two calls below are part of the same uninstall.
        // They used to be missing, and an uninstall that ended in "Uninstalled." while the
        // helper ran on and re-registered itself at the next sign-in was one lie in three
        // ways: the row, the status text, and the task.
        StopAndDelete(ProtocolConstants.SessionServiceName);
        StopAndDelete(ProtocolConstants.ServiceName);

        TaskCommands.StopSessionTask();
        TaskCommands.RemoveSessionTask();

        if (keepData)
        {
            Console.WriteLine("  the database, certificates and logs were left in place");
        }
        else
        {
            var dataDirectory = ResolveDataDirectory();
            try
            {
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                    Console.WriteLine($"  removed {dataDirectory}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The service is stopped, so this is usually a file still held by an antivirus
                // scanner. Leaving the data behind is better than failing the uninstall.
                Console.WriteLine($"  warning: could not remove {dataDirectory} ({ex.Message})");
            }
        }

        Console.WriteLine("Uninstalled.");
        return 0;
    }

    private static void StopAndDelete(string name)
    {
        if (!Exists(name))
        {
            Console.WriteLine($"  {name} was not installed");
            return;
        }

        try
        {
            using (var sc = new ServiceController(name))
            {
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    // A pending reboot leaves a service in StopPending indefinitely, so the
                    // wait is bounded and a timeout is not fatal: the mark for deletion still
                    // takes effect, just at the next restart.
                    sc.Stop();

                    try
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                    catch (System.ServiceProcess.TimeoutException)
                    {
                        Console.WriteLine($"  warning: {name} did not stop in time; deleting anyway");
                    }
                }
            }

            // sc.exe, not the managed API: .NET 10 removed ServiceController.Delete entirely.
            // The ServiceController handle is already disposed above, which matters because a
            // service marked for deletion is not actually removed until every handle to it is
            // closed - leaving one open would strand the entry in the SCM forever.
            var delete = RunScRaw($"delete {name}");

            if (delete.Succeeded)
                Console.WriteLine($"  removed {name}");
            else
                Console.WriteLine($"  warning: could not remove {name} ({delete.Output.Trim()})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.TimeoutException or Win32Exception)
        {
            Console.WriteLine($"  warning: could not remove {name} ({ex.Message})");
        }
    }

    // -----------------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------------

    public static int Status()
    {
        var pairingPage = Exists(ProtocolConstants.ServiceName)
            ? "https://localhost:" + ReadConfiguredPort().ToString(CultureInfo.InvariantCulture) + "/pairing"
            : "(service not installed)";

        Console.WriteLine("ESI Remote PC");
        Console.WriteLine($"  data directory  {ResolveDataDirectory()}");
        Console.WriteLine($"  pairing page    {pairingPage}");
        Console.WriteLine();

        ReportService(ProtocolConstants.ServiceName);
        ReportSessionAgent();

        var logDirectory = Path.Combine(ResolveDataDirectory(), "logs");
        if (Directory.Exists(logDirectory))
        {
            var newest = Directory.GetFiles(logDirectory, "agent-*.log")
                .Select(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)))
                .OrderByDescending(entry => entry.Written)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(newest.Path))
            {
                Console.WriteLine();
                Console.WriteLine($"  latest log      {newest.Path} ({FormatAge(DateTime.UtcNow - newest.Written)})");
            }
        }

        return 0;
    }

    /// <summary>One row of a service, or a note that it is not there.</summary>
    private static void ReportService(string name)
    {
        if (!Exists(name))
        {
            Console.WriteLine($"  {name,-24} not installed");
            return;
        }

        try
        {
            using var sc = new ServiceController(name);
            sc.Refresh();

            Console.WriteLine($"  {name,-24} {sc.Status,-12} {sc.StartType,-10}");

            if (sc.Status == ServiceControllerStatus.Stopped && sc.StartType == ServiceStartMode.Disabled)
                Console.WriteLine($"  {name,-24} -> enable it with: sc config {name} start= auto");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Console.WriteLine($"  {name,-24} error: {ex.Message}");
        }
    }

    /// <summary>
    /// One row for the per-user half of the product: the session helper that owns screen
    /// capture, input injection and the pairing prompt.
    /// </summary>
    private static void ReportSessionAgent()
    {
        const string label = ProtocolConstants.SessionServiceName;

        var helper = Process.GetProcessesByName(TaskCommands.SessionAgentProcessName).FirstOrDefault();
        using (helper)
        {
            var text = DescribeSessionAgent(
                serviceRegistered: Exists(label),
                taskRegistered: TaskCommands.IsRegistered(),
                helperRunning: helper is not null,
                helperPid: helper?.Id ?? 0);

            // Null means "this machine has the legacy per-user service", which is reported as a
            // service row rather than as a line of its own.
            if (text is null)
            {
                ReportService(label);
                return;
            }

            Console.WriteLine($"  {label,-24} {text}");
        }
    }

    /// <summary>
    /// What the status report says about the session helper, given what this machine has.
    /// <para>
    /// Internal and free of the SCM, <c>schtasks</c> and the process list so the answer can be
    /// tested on its own: this one line is the whole of the defect, and everything around it is
    /// I/O.
    /// </para>
    /// <para>
    /// The helper used to be a per-user service called <c>ESIRemotePcSession</c>, and this
    /// report asked the SCM about it - which is what it still did long after the SCM stopped
    /// being where it lived. A per-user service is registered with an empty <c>obj=</c>, which
    /// the SCM rejects, so the helper became a logon task; the status line was never told, and
    /// answered "not installed" for the one component whose entire job is to report whether the
    /// others are installed. It is printed by the Start Menu's "Agent status (console)"
    /// shortcut, which exists for exactly that question - while the helper was running.
    /// </para>
    /// <para>
    /// A machine installed by <c>esirc install</c> still gets the service row: that command
    /// still creates one, and reporting it as absent would be the same defect facing the other
    /// way.
    /// </para>
    /// </summary>
    /// <returns>
    /// Null when the machine has the legacy service and it should be reported as a service row;
    /// otherwise the text to print after the name.
    /// </returns>
    internal static string? DescribeSessionAgent(
        bool serviceRegistered,
        bool taskRegistered,
        bool helperRunning,
        int helperPid)
    {
        if (serviceRegistered)
            return null;

        if (!taskRegistered)
            return "not installed";

        return helperRunning
            ? string.Create(CultureInfo.InvariantCulture, $"running (pid {helperPid})")
            : "registered, not running";
    }

    private static string FormatAge(TimeSpan age) => age.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours}h ago")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)age.TotalMinutes)}m ago");

    private static int ReadConfiguredPort()
    {
        try
        {
            var path = Path.Combine(ResolveDataDirectory(), "settings.json");
            if (!File.Exists(path))
                return ProtocolConstants.DefaultHttpsPort;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("port", out var port) && port.TryGetInt32(out var value)
                ? value
                : ProtocolConstants.DefaultHttpsPort;
        }
        catch (Exception)
        {
            return ProtocolConstants.DefaultHttpsPort;
        }
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Whether a service is registered.
    /// <para>
    /// Deliberately broad. A missing service surfaces as <c>InvalidOperationException</c> from
    /// <c>Refresh</c>, as <c>Win32Exception</c> (ERROR_SERVICE_DOES_NOT_EXIST, 1060) from the
    /// SCM handle, and as <c>NullReferenceException</c> from a null <c>ServiceHandle</c>
    /// depending on which property is touched first. Pinning one of them means the answer
    /// depends on the Windows build, which is not a useful thing for a status report.
    /// </para>
    /// </summary>
    public static bool Exists(string name)
    {
        try
        {
            using var sc = new ServiceController(name);

            // Refresh() alone is not enough. For a service that does not exist the
            // constructor and Refresh() both succeed; the failure only surfaces when a
            // property that needs the SCM handle is read. Touching Status is what makes this
            // an actual existence check rather than a construction check.
            sc.Refresh();
            _ = sc.Status;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                                       or NullReferenceException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds an executable next to this one.
    /// <para>
    /// Layout assumption: the installer drops all three binaries into the same directory,
    /// which is what both the Inno Setup script and a manual copy produce. Looking next to
    /// the running executable is what makes the CLI work from a copied folder without a
    /// registry lookup.
    /// </para>
    /// </summary>
    public static string ResolveSibling(string fileName)
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, fileName);
    }

    public static string ResolveDataDirectory()
        => Environment.GetEnvironmentVariable("PROGRAMDATA") is { Length: > 0 } programData
            ? Path.Combine(programData, ProtocolConstants.DataDirectoryName)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ProtocolConstants.DataDirectoryName);

    /// <summary>
    /// Resolves the data directory, honouring an explicit <c>--data-dir</c>.
    /// <para>
    /// The agent takes <c>--data-dir</c> and the CLI did not, so the only way to point the
    /// CLI at the same database as a console agent was to override the <c>PROGRAMDATA</c>
    /// environment variable. That works but it is a trap: the agent's own resolver reads the
    /// registry through <c>GetFolderPath</c> and ignores <c>PROGRAMDATA</c>, so a process
    /// launched with that variable pointed the two at different databases. An explicit flag
    /// is unambiguous, and it is what the smoke test and the live-agent check both need.
    /// </para>
    /// </summary>
    public static string ResolveDataDirectory(string? explicitPath)
        => string.IsNullOrWhiteSpace(explicitPath) ? ResolveDataDirectory() : explicitPath.Trim();

    private static CommandResult RunSc(params string[] arguments) => RunScRaw(string.Join(' ', arguments));

    private static CommandResult RunScRaw(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return new CommandResult(-1, "sc.exe could not be started");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            var output = (stdout + stderr).Trim();
            return new CommandResult(process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, ex.Message);
        }
    }
}

/// <summary>Options for <c>install</c>.</summary>
internal sealed record InstallOptions
{
    public string? ServicePath { get; init; }
    public string? SessionPath { get; init; }
    public int Port { get; init; } = ProtocolConstants.DefaultHttpsPort;
    public bool Autostart { get; init; } = true;
    public bool IncludeSessionAgent { get; init; } = true;
    public int RestartCount { get; init; } = 3;
    public int RestartDelaySeconds { get; init; } = 5;
    public bool Force { get; init; }
}

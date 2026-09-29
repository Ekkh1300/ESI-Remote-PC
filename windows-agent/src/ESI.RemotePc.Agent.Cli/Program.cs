using System.Globalization;

namespace Esi.Cli;

/// <summary>
/// Entry point for <c>esirc</c>, the administration tool.
/// <para>
/// One binary with subcommands rather than several: install/uninstall/status/pair all
/// operate on the same installation, and a user who has to find three different .exe files
/// to manage one product has been set up to get it wrong.
/// </para>
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "-?" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? ExitUsage : ExitSuccess;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        return command switch
        {
            "install" => WithElevation(rest, static a => Install(a)),
            "uninstall" => WithElevation(rest, static a => Uninstall(a)),
            "status" => ServiceManager.Status(),
            "pair" => Pair(rest),
            "version" => PrintVersion(),

            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.WriteLine();
        PrintUsage();
        return ExitUsage;
    }

    // -----------------------------------------------------------------------------
    // install / uninstall
    // -----------------------------------------------------------------------------

    private static int WithElevation(string[] args, Func<string[], int> action)
    {
        if (ServiceManager.IsElevated())
            return action(args);

        Console.WriteLine("This command needs administrator rights.");

        if (!ServiceManager.RelaunchElevated(args))
        {
            Console.Error.WriteLine("Right-click a Command Prompt and choose 'Run as administrator', then retry.");
            return ExitFailure;
        }

        // The elevated instance is a different process; this one is done.
        return ExitSuccess;
    }

    private static int Install(string[] args)
    {
        var options = new InstallOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--service-path" when i + 1 < args.Length:
                    options = options with { ServicePath = args[++i] };
                    break;

                case "--session-path" when i + 1 < args.Length:
                    options = options with { SessionPath = args[++i] };
                    break;

                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options = options with { Port = port };
                    i++;
                    break;

                case "--no-autostart":
                    options = options with { Autostart = false };
                    break;

                case "--no-session-agent":
                    options = options with { IncludeSessionAgent = false };
                    break;

                case "--no-recovery":
                    options = options with { RestartCount = 0 };
                    break;

                case "--force":
                    options = options with { Force = true };
                    break;

                case "--help" or "-h" or "-?":
                    Console.WriteLine("""
                        esirc install [options]

                          --service-path <path>   Agent executable (default: next to esirc)
                          --session-path <path>   Session agent executable
                          --port <n>              HTTPS port to advertise (default 48220)
                          --no-autostart          Install but do not start the service
                          --no-session-agent      Skip the per-user session helper
                          --no-recovery           Do not configure restart-on-failure
                          --force                 Reinstall over an existing installation
                        """);
                    return ExitSuccess;
            }
        }

        if (options.Force)
        {
            // Replacing is a delete followed by a create. Anything already paired survives,
            // because the database lives outside the service registration.
            ServiceManager.Uninstall(keepData: true);
        }

        return ServiceManager.Install(options);
    }

    private static int Uninstall(string[] args)
    {
        var keepData = !args.Contains("--purge", StringComparer.OrdinalIgnoreCase);

        if (!keepData && ServiceManager.IsElevated())
        {
            var confirm = AskYesNo("This deletes the pairing database, TLS certificate and logs. Continue?");
            if (!confirm)
            {
                Console.WriteLine("Cancelled.");
                return ExitSuccess;
            }
        }

        return ServiceManager.Uninstall(keepData);
    }

    private static bool AskYesNo(string question)
    {
        Console.Write($"{question} [y/N] ");
        var answer = Console.ReadLine();
        return answer is not null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                      answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------------
    // pair
    // -----------------------------------------------------------------------------

    private static int Pair(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "-?")
        {
            Console.WriteLine("""
                esirc pair <command>

                  show                 List paired devices
                  pending              List pairing requests awaiting approval
                  url <code>           Print the pairing URL for a pending request (6 digits)
                  approve <code>       Approve a pending request (6 digits)
                  deny <code>          Deny a pending request
                  revoke <deviceId>    Remove a paired device

                Options:
                  --data-dir <path>    Operate on this agent data directory instead of the
                                       default one under ProgramData. Use it to match a
                                       console agent started with its own --data-dir.
                  --port <n>           The port the agent is listening on. `url` needs it
                                       when the agent was started with a --port of its own,
                                       because nothing persists that choice.

                show, pending and url only read, and work while the service is running. The
                three that write must be run with the ESI Remote PC service stopped, so the
                two processes cannot disagree about whether a pairing was approved. A
                --data-dir that is not the installed one has no service holding it, so those
                commands need no stop at all.

                url prints the one-time secret a phone needs. The QR is the usual way to
                transfer it, but that needs a display; this is the headless equivalent. Treat
                the output as a credential: it is valid until the attempt is approved or
                expires.
                """);

            return args.Length == 0 ? ExitUsage : ExitSuccess;
        }

        // Pull the option out first: it changes which database every subcommand touches, and
        // leaving it in the positional list would make `approve 123456` pick up the wrong
        // argument depending on where the flag was placed.
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                PairCommands.DataDir = args[++i];
                continue;
            }
            if (args[i] == "--port" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out var port) || port is <= 0 or > 65535)
                    return Fail($"--port needs a number between 1 and 65535, not '{args[i]}'.");
                PairCommands.Port = port;
                continue;
            }
            rest.Add(args[i]);
        }
        args = rest.ToArray();

        if (args.Length == 0)
            return Fail("a pair subcommand is required. See 'esirc pair --help'.");

        // `show` and `pending` only read, and reading live pairing state is exactly what an
        // administrator needs while the agent is running - that is the normal case, not the
        // exceptional one. Refusing them turned a diagnostic into an outage. The writes
        // still require the service to be stopped, because they mint device credentials and
        // rewrite rows the running service also holds.
        var writesState = args[0].ToLowerInvariant() is "approve" or "deny" or "revoke";

        // The stop requirement is about the *installed* service, which holds the default
        // data directory. An explicit --data-dir is somebody's throwaway or debug agent,
        // which no service is holding, so requiring a stop there would be pure ceremony.
        if (writesState && string.IsNullOrWhiteSpace(PairCommands.DataDir)
            && ServiceManager.Exists(Esi.Protocol.ProtocolConstants.ServiceName))

        if (writesState && ServiceManager.Exists(Esi.Protocol.ProtocolConstants.ServiceName))
        {
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(Esi.Protocol.ProtocolConstants.ServiceName);
                sc.Refresh();

                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    // The whole sequence, because stopping the service and then failing to
                    // give it back is how a machine ends up unreachable with no agent and
                    // no obvious reason why.
                    Console.Error.WriteLine(
                        $"The service is {sc.Status}, and '{args[0]}' rewrites pairing state " +
                        "the running service also holds. Do all three, in order:");
                    Console.Error.WriteLine($"  sc stop {Esi.Protocol.ProtocolConstants.ServiceName}");
                    Console.Error.WriteLine($"  esirc pair {args[0]} {args[1]}");
                    Console.Error.WriteLine($"  sc start {Esi.Protocol.ProtocolConstants.ServiceName}");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        "The phone cannot reach the agent while it is stopped, so it will " +
                        "show a connection error for a few seconds and then recover.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        "There is deliberately no way to approve from a browser. The pairing " +
                        "page is reachable by anything on the LAN and the code is six digits, " +
                        "so a page that could approve would let anyone approve a pairing " +
                        "they started themselves. Approval needs either a signed-in user at " +
                        "the desktop prompt, or this console.");
                    return ExitFailure;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The service could not be queried; proceed rather than block the user.
            }
        }

        return args[0].ToLowerInvariant() switch
        {
            "show" or "list" => PairCommands.Show(),
            "pending" => PairCommands.Pending(),
            "url" when args.Length >= 2 => PairCommands.Url(args[1]),
            "approve" when args.Length >= 2 => PairCommands.Approve(args[1]),
            "deny" when args.Length >= 2 => PairCommands.Deny(args[1]),
            "revoke" when args.Length >= 2 => PairCommands.Revoke(args[1]),

            "url" or "approve" or "deny" or "revoke" =>
                Fail($"{args[0]} needs an argument. See 'esirc pair --help'."),

            _ => Fail($"Unknown pair command '{args[0]}'. See 'esirc pair --help'."),
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return ExitUsage;
    }

    private static int PrintVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine(version is null
            ? "esirc (unknown version)"
            : $"esirc {version.Major}.{version.Minor}.{version.Build} (protocol v{Esi.Protocol.ProtocolConstants.Version})");

        return ExitSuccess;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ESI Remote PC - administration

            USAGE
              esirc <command> [options]

            COMMANDS
              install      Install the Windows services
              uninstall    Remove the Windows services (--purge also deletes the data)
              status       Show what is installed and whether it is running
              pair         List, approve, deny and revoke devices
              version      Print the version

            Run 'esirc <command> --help' for the options of a specific command.

            install and uninstall need administrator rights; the tool asks for them through
            UAC by itself.
            """);
    }
}

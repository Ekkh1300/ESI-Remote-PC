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
                  approve <code>       Approve a pending request (6 digits)
                  deny <code>          Deny a pending request
                  revoke <deviceId>    Remove a paired device

                These read and write the agent's database directly and must be run while the
                ESI Remote PC service is stopped, so the two processes cannot disagree about
                whether a pairing was approved.
                """);

            return args.Length == 0 ? ExitUsage : ExitSuccess;
        }

        // Every pair subcommand mutates or reads live security state, so refusing while the
        // service runs is the difference between a consistent database and a confusing one.
        if (ServiceManager.Exists(Esi.Protocol.ProtocolConstants.ServiceName))
        {
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(Esi.Protocol.ProtocolConstants.ServiceName);
                sc.Refresh();

                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    Console.Error.WriteLine($"The service is {sc.Status}. Stop it first:");
                    Console.Error.WriteLine($"  sc stop {Esi.Protocol.ProtocolConstants.ServiceName}");
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
            "approve" when args.Length >= 2 => PairCommands.Approve(args[1]),
            "deny" when args.Length >= 2 => PairCommands.Deny(args[1]),
            "revoke" when args.Length >= 2 => PairCommands.Revoke(args[1]),

            "approve" or "deny" or "revoke" =>
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

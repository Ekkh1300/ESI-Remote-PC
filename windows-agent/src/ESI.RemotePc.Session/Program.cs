using System.Globalization;
using Esi.Platform.Session;
using Esi.Protocol;
using Serilog;
using Serilog.Events;

namespace Esi.Session;

/// <summary>
/// The per-user half of ESI Remote PC.
/// <para>
/// This process is the only part of the agent that can touch the interactive desktop. It
/// runs as the logged-on user on <c>winsta0\default</c>, which is the only place
/// <c>SendInput</c>, <c>BitBlt</c> and <c>LockWorkStation</c> actually work: a service in
/// session 0 has no visible desktop to draw on, and input injection is subject to UIPI, so it
/// is bound to the caller's own session regardless.
/// </para>
/// <para>
/// The service starts it and talks to it over a named pipe. Nothing here is reachable from
/// the network; the service is the only thing that can make this process do anything.
/// </para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SessionAgentOptions.Parse(args);

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        ConfigureLogging(options);

        try
        {
            // STA + the default visual styles: the pairing approval prompt is a real modal
            // dialog, and a thread-pool MTA apartment would either fail to show it or show it
            // without a working message pump.
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (_, e) =>
                Log.Fatal(e.Exception, "The session agent's UI thread faulted");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "The session agent faulted");

            var runtime = new SessionAgentRuntime(options);
            runtime.Run();

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The session agent could not start");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureLogging(SessionAgentOptions options)
    {
        Directory.CreateDirectory(options.LogDirectory);

        var level = options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;

        // A per-user rolling file, not the service's. Two processes writing one file is how
        // you get interleaved, unreadable logs when something goes wrong at 3am.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(options.LogDirectory, "session-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            ESI Remote PC - session agent

              --channel <name>   Command pipe to connect to (default {SessionChannelProtocol.PipeName})
              --session <id>     The Windows session id this process was launched into
              --log-dir <path>   Override the per-user log directory
              --verbose          Debug-level logging
              --visible          Show a console window (useful when debugging by hand)
              --help             This text

            The service launches this automatically. Running it by hand is only useful for
            diagnosing a connection problem: without a service holding the other end of the
            pipe it will sit in 'waiting for the agent service' until stopped.
            """);
    }
}

/// <summary>Command-line configuration for the session agent.</summary>
internal sealed class SessionAgentOptions
{
    public string ChannelName { get; init; } = SessionChannelProtocol.PipeName;
    public uint SessionId { get; init; }
    public string LogDirectory { get; init; } = DefaultLogDirectory();
    public bool Verbose { get; init; }
    public bool Visible { get; init; }
    public bool ShowHelp { get; init; }

    /// <summary>
    /// Per-user rather than per-machine, because this file contains the details of what the
    /// person at this keyboard is doing. A %ProgramData% copy would be world-readable.
    /// </summary>
    public static string DefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProtocolConstants.DataDirectoryName,
        "logs");

    public static SessionAgentOptions Parse(string[] args)
    {
        var channel = SessionChannelProtocol.PipeName;
        uint session = 0xFFFFFFFF;
        var logDir = DefaultLogDirectory();
        var verbose = false;
        var visible = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--channel" when i + 1 < args.Length:
                    channel = args[++i];
                    break;

                case "--session" when i + 1 < args.Length &&
                                    uint.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    session = parsed;
                    i++;
                    break;

                case "--log-dir" when i + 1 < args.Length:
                    logDir = args[++i];
                    break;

                case "--verbose":
                    verbose = true;
                    break;

                case "--visible":
                    visible = true;
                    break;

                case "--help" or "-h" or "-?":
                    help = true;
                    break;
            }
        }

        return new SessionAgentOptions
        {
            ChannelName = channel,
            SessionId = session,
            LogDirectory = logDir,
            Verbose = verbose,
            Visible = visible,
            ShowHelp = help,
        };
    }
}

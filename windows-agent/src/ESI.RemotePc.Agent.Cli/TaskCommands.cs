using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Esi.Cli;

/// <summary>
/// Registers the per-user session helper as a logon-triggered scheduled task.
/// <para>
/// This lives in the CLI rather than in the installer's script for one reason, and it is a
/// blunt one: <c>schtasks /create /tr "C:\Program Files\...\something.exe"</c> cannot be made
/// to work. Task Scheduler parses the action out of the raw command line itself rather than
/// taking it as an argv element, so it stores the quoted path perfectly and then fails to
/// run it with 0x80070002, "file not found". The forms that are supposed to work around that -
/// a second pair of quotes, or routing the launch through <c>cmd /c</c> - are rejected by the
/// same parser with "Invalid argument/option - 'Files\ESI'".
/// </para>
/// <para>
/// Passing the task as an XML document sidesteps it completely: the command is element
/// content, where a space is just a character. That is what the packaging tools which support
/// spaces in an install directory do, and it is why this is C# and not Inno Pascal Script:
/// Inno has one string-to-file routine and no way to state which encoding it uses, while a
/// task document that declares an encoding it is not written in is rejected.
/// </para>
/// <para>
/// The task is registered for any interactive user, not for the account running the
/// installer. The helper belongs to whoever is at the keyboard; naming the installing
/// administrator would leave every second user with no tray icon and no approval prompt.
/// </para>
/// </summary>
public static class TaskCommands
{
    /// <summary>Name of the logon task, matching what the installer and the docs call it.</summary>
    public const string SessionAgentTaskName = "ESI Remote PC Session Agent";

    /// <summary>File name of the session helper, beside the CLI when installed.</summary>
    private const string SessionAgentFileName = "ESI.RemotePc.Session.exe";

    public static int Register(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "-?")
        {
            Console.WriteLine($"""
                esirc task <command>

                  register     Register the session helper's logon task and start it now
                  remove       Delete the logon task
                  show         Print the registered task

                The session helper runs per signed-in user: screen capture and input injection
                need an interactive desktop, and SendInput is bound to the caller's session.
                It is a logon task rather than a second Windows service because a per-user
                service is registered with an empty obj=, which the SCM rejects - silently, as
                far as the installer is concerned.

                register also starts the task immediately. An ONLOGON trigger fires when
                somebody signs in, so installing while already signed in - the normal case -
                would otherwise leave the helper unstarted until the next logon.
                """);

            return args.Length == 0 ? ExitUsage : ExitSuccess;
        }

        return args[0].ToLowerInvariant() switch
        {
            "register" => RegisterCore(),
            "remove" or "delete" or "unregister" => Remove(),
            "show" or "query" => Show(),
            _ => Usage($"Unknown task command '{args[0]}'. See 'esirc task --help'."),
        };
    }

    private static int RegisterCore()
    {
        var executable = ResolveSessionAgent();
        if (executable is null) return ExitFailure;

        var xmlPath = Path.Combine(Path.GetTempPath(), "esi-session-agent-task.xml");

        try
        {
            // Encoding.Unicode, and the document declares UTF-16 to match. schtasks hands the
            // file to a real XML parser, so a declaration the bytes disagree with is fatal.
            File.WriteAllText(xmlPath, BuildTaskXml(executable), Encoding.Unicode);

            var created = RunSchtasks("/create", "/f", "/tn", SessionAgentTaskName, "/xml", xmlPath);
            if (created.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"schtasks refused to register '{SessionAgentTaskName}' (exit {created.ExitCode}).");
                Console.Error.WriteLine(created.Combined.Trim());
                return ExitFailure;
            }

            Console.WriteLine($"Registered the logon task '{SessionAgentTaskName}'.");
            Console.WriteLine($"  action: {executable}");

            // Start it now. See the class remarks: the trigger has already been and gone on a
            // machine that was signed in when the installer ran.
            var started = RunSchtasks("/run", "/tn", SessionAgentTaskName);
            if (started.ExitCode != 0)
            {
                // Not fatal: it will start at the next sign-in, which is the correct
                // behaviour for a headless machine anyway.
                Console.WriteLine(
                    "The task could not be started now, so the helper starts at the next " +
                    "sign-in instead. That is expected on a machine with nobody signed in.");
            }
            else
            {
                Console.WriteLine("Started the session helper.");
            }

            return ExitSuccess;
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    private static int Remove()
    {
        var result = RunSchtasks("/delete", "/f", "/tn", SessionAgentTaskName);

        // 1 is "no such task", which is the state the caller asked for.
        if (result.ExitCode == 0)
        {
            Console.WriteLine($"Deleted the logon task '{SessionAgentTaskName}'.");
            return ExitSuccess;
        }

        Console.WriteLine($"No logon task named '{SessionAgentTaskName}'.");
        return ExitSuccess;
    }

    private static int Show()
    {
        var result = RunSchtasks("/query", "/tn", SessionAgentTaskName, "/fo", "LIST", "/v");

        if (result.ExitCode != 0)
        {
            Console.WriteLine($"No logon task named '{SessionAgentTaskName}'.");
            return ExitFailure;
        }

        Console.WriteLine(result.Combined.Trim());
        return ExitSuccess;
    }

    /// <summary>
    /// The session helper, found beside this executable.
    /// <para>
    /// Beside rather than configured, because the CLI and the helper are installed into the
    /// same directory and the helper is not something an administrator moves. The agent
    /// service has its own <c>SessionAgentPath</c> setting for the CreateProcessAsUser route;
    /// this is the task route, and it is a different mechanism with a different lifetime.
    /// </para>
    /// </summary>
    internal static string? ResolveSessionAgent()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, SessionAgentFileName);
        if (File.Exists(beside)) return beside;

        Console.Error.WriteLine($"The session helper is not beside this command: {beside}");
        Console.Error.WriteLine("It should be installed into the same directory as esirc.");
        return null;
    }

    /// <summary>
    /// The task document. Internal so a test can assert the command really is element
    /// content and that a path with spaces survives intact.
    /// </summary>
    internal static string BuildTaskXml(string executable)
    {
        // Interpolated into element content, never into an attribute or a command line, so
        // the ampersand is the only character that needs attention - and a path containing
        // one would otherwise produce a document that does not parse.
        var command = System.Security.SecurityElement.Escape(executable) ?? executable;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Per-user helper: screen capture, input injection, and the pairing prompt.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Combined) RunSchtasks(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // ArgumentList rather than a joined string: this is the whole reason the command is
        // written in C#. A path with a space in it becomes one element and stays one
        // element, instead of depending on how many layers of quoting survive.
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, string.Join(
            Environment.NewLine,
            stdout.Trim(),
            stderr.Trim()).Trim());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover file in %TEMP% is not worth failing an install over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return ExitUsage;
    }

    private const int ExitSuccess = 0;
    private const int ExitUsage = 1;
    private const int ExitFailure = 2;
}

using System.Xml.Linq;
using Esi.Cli;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The session helper's logon task definition.
/// <para>
/// This is here because of a bug that no other check could see.
/// <c>schtasks /create /tr "C:\Program Files\...\something.exe"</c> stores the path correctly
/// and then cannot run it: Task Scheduler reports 0x80070002, "file not found", because it
/// parses the action out of the raw command line itself instead of taking an argv element.
/// The forms that are supposed to cover that - a second pair of quotes, or
/// <c>cmd /c</c> - are rejected by the same parser. Every install produced a registered task
/// that never started, so there was no tray icon, no approval prompt, and no way to complete
/// a pairing.
/// </para>
/// <para>
/// The fix is to pass the task as an XML document, where the command is element content and
/// a space is just a character. That only works if the document really is the encoding it
/// declares and really parses, so both are asserted here rather than trusted.
/// </para>
/// </summary>
public sealed class TaskDefinitionTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XElement CommandElement(string executable) =>
        XDocument.Parse(TaskCommands.BuildTaskXml(executable))
            .Descendants(Ns + "Command")
            .Single();

    [Fact]
    public void A_path_with_spaces_survives_intact()
    {
        // The whole reason this is an XML document rather than a command line.
        const string path = @"C:\Program Files\ESI Remote PC\ESI.RemotePc.Session.exe";

        Assert.Equal(path, CommandElement(path).Value);
    }

    [Fact]
    public void The_document_parses_and_keeps_the_command_as_element_content()
    {
        // Parsed, not string-matched: a task document that does not parse is rejected by
        // schtasks, and the failure mode is an installer that reports success.
        var document = XDocument.Parse(TaskCommands.BuildTaskXml(@"C:\a b\x.exe"));

        Assert.Equal("Task", document.Root!.Name.LocalName);
        Assert.Equal(Ns + "Task", document.Root.Name);
    }

    [Fact]
    public void The_document_declares_the_encoding_it_is_written_in()
    {
        // The caller writes the file as UTF-16 with Encoding.Unicode. A document declaring
        // something else is handed to a real XML parser and rejected.
        var declaration = XDocument.Parse(TaskCommands.BuildTaskXml(@"C:\a b\x.exe"))
            .Declaration!
            .ToString();

        Assert.Contains("UTF-16", declaration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_action_is_the_session_helper()
    {
        var xml = TaskCommands.BuildTaskXml(@"C:\Program Files\ESI Remote PC\ESI.RemotePc.Session.exe");

        Assert.Contains(@"<Command>C:\Program Files\ESI Remote PC\ESI.RemotePc.Session.exe</Command>", xml);
    }

    [Fact]
    public void The_task_runs_at_logon_and_only_for_an_interactive_user()
    {
        // Interactive-only is load-bearing: the helper needs a desktop, and SendInput is
        // bound to the caller's session. A task that also ran as SYSTEM would be a headless
        // process that can neither capture the screen nor inject input.
        var document = XDocument.Parse(TaskCommands.BuildTaskXml(@"C:\a b\x.exe"));

        var logon = document.Descendants(Ns + "LogonTrigger").Single();
        Assert.Equal("true", logon.Element(Ns + "Enabled")!.Value);

        var principal = document.Descendants(Ns + "Principal").Single();
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
    }

    [Fact]
    public void The_task_is_not_bound_to_the_account_doing_the_installing()
    {
        // No UserId at all, deliberately. The helper belongs to whoever is at the keyboard;
        // naming the installing administrator would leave every other user with no tray icon
        // and no approval prompt.
        var document = XDocument.Parse(TaskCommands.BuildTaskXml(@"C:\a b\x.exe"));

        Assert.Empty(document.Descendants(Ns + "UserId"));
    }

    [Fact]
    public void The_helper_is_allowed_to_start_on_demand()
    {
        // Without this the installer could not start it immediately, which is the whole
        // point of running it after registering: the logon trigger has already been and gone
        // on a machine that was signed in when the installer ran.
        //
        // Scoped to Settings, because there are two Enabled elements in this document - the
        // trigger's and the task's - and asking for a single one across the whole tree
        // throws rather than asserts.
        var settings = XDocument.Parse(TaskCommands.BuildTaskXml(@"C:\a b\x.exe"))
            .Descendants(Ns + "Settings")
            .Single();

        Assert.Equal("true", settings.Element(Ns + "AllowStartOnDemand")!.Value);
        Assert.Equal("true", settings.Element(Ns + "Enabled")!.Value);
    }

    [Fact]
    public void Two_instances_of_the_helper_are_not_needed()
    {
        // The helper is a tray icon and a command channel. A second copy would fight the
        // first over the pipe and show two icons.
        var document = XDocument.Parse(TaskCommands.BuildTaskXml(@"C:\a b\x.exe"));

        Assert.Equal("IgnoreNew", document.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
    }

    [Fact]
    public void A_path_containing_an_ampersand_still_produces_a_parseable_document()
    {
        // The one character that matters in element content. A user who installs to
        // "C:\Program Files\A&B\..." would otherwise get a task document that does not parse,
        // and the install would report success with no helper.
        const string path = @"C:\A&B\ESI Remote PC\ESI.RemotePc.Session.exe";

        var document = XDocument.Parse(TaskCommands.BuildTaskXml(path));

        Assert.Equal(path, document.Descendants(Ns + "Command").Single().Value);
    }
}

using Esi.Cli;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// What <c>esirc status</c> says about the session helper.
/// <para>
/// This is here because of a bug that no other check could see, and that the user could not
/// avoid seeing: <c>status</c> reported the session helper as <c>not installed</c> on every
/// machine that had it installed and running.
/// </para>
/// <para>
/// The helper used to be a per-user Windows service named <c>ESIRemotePcSession</c>. A
/// per-user service needs an empty <c>obj=</c>, which the SCM rejects, so it became a logon
/// task instead - but the status line was never told, and kept asking the SCM for a service
/// that no longer existed. Every answer to that question is "no such service", and so the one
/// component whose entire job is to report whether the other components are installed said
/// "not installed" about itself. It is printed by the Start Menu's "Agent status (console)"
/// shortcut, which exists for exactly this question - and it printed it while the helper was
/// running, with a tray icon, in the taskbar.
/// </para>
/// <para>
/// The rule the old code got wrong is the one these tests pin: the absence of that service says
/// nothing at all about whether the helper is installed, so it cannot be the answer.
/// </para>
/// </summary>
public sealed class SessionAgentStatusTests
{
    [Fact]
    public void A_task_with_its_helper_running_is_not_reported_as_not_installed()
    {
        // The defect, stated as its own test. The old line asked only about the service, so it
        // reached "not installed" from these two facts alone.
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: false,
            taskRegistered: true,
            helperRunning: true,
            helperPid: 14328);

        Assert.NotEqual("not installed", said);
    }

    [Fact]
    public void A_running_helper_reports_the_process_it_found()
    {
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: false,
            taskRegistered: true,
            helperRunning: true,
            helperPid: 14328);

        Assert.Equal("running (pid 14328)", said);
    }

    [Fact]
    public void A_registered_helper_that_is_not_running_says_so_rather_than_saying_no()
    {
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: false,
            taskRegistered: true,
            helperRunning: false,
            helperPid: 0);

        Assert.Equal("registered, not running", said);
    }

    [Fact]
    public void With_neither_the_service_nor_the_task_it_really_is_not_installed()
    {
        // The other half of the rule: a machine that genuinely has nothing must still be told
        // so. Reporting "running" from the task's registration alone would be the same defect
        // facing the other way - a report that contradicts the machine.
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: false,
            taskRegistered: false,
            helperRunning: false,
            helperPid: 0);

        Assert.Equal("not installed", said);
    }

    [Fact]
    public void A_task_that_is_registered_but_not_running_is_not_reported_as_running()
    {
        // A stale pid would be worse than the original defect: the helper is either running or
        // it is not, and the caller passes a process it looked up.
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: false,
            taskRegistered: true,
            helperRunning: false,
            helperPid: 0);

        Assert.NotEqual("running (pid 0)", said);
    }

    [Fact]
    public void A_machine_still_holding_the_legacy_per_user_service_is_reported_as_a_service()
    {
        // `esirc install` still creates one. Returning null is the signal to print a service
        // row instead of a line of its own - reporting it absent would be the same defect the
        // other way around.
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: true,
            taskRegistered: false,
            helperRunning: false,
            helperPid: 0);

        Assert.Null(said);
    }

    [Fact]
    public void The_legacy_service_wins_even_when_a_task_is_registered_too()
    {
        // A machine could carry both. The service row already reports its state fully, so a
        // second line about the task would be a duplicate rather than extra information.
        var said = ServiceManager.DescribeSessionAgent(
            serviceRegistered: true,
            taskRegistered: true,
            helperRunning: true,
            helperPid: 7);

        Assert.Null(said);
    }

    [Fact]
    public void The_process_name_the_status_line_looks_up_is_the_task_action_without_its_extension()
    {
        // The status line looks the process up by name and the task runs it by path. They are
        // the same file, so they are derived from one constant rather than written twice.
        Assert.Equal("ESI.RemotePc.Session", TaskCommands.SessionAgentProcessName);
    }
}

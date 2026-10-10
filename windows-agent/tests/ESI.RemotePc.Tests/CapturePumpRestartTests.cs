using System.Reflection;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D46: the screen stream started and then went permanently silent.
/// <para>
/// <c>CapturePump._stopping</c> was a <c>readonly CancellationTokenSource</c> created once in the
/// constructor. The first <c>Stop</c> cancelled it; nothing ever cleared it, so every later
/// <c>Start</c> began with the token already cancelled and <c>CaptureLoop</c> returned on its first
/// condition check.
/// </para>
/// <para>
/// This is a source-level guard on purpose. The bug is not reachable from a public API - it needs a
/// process that has already been stopped and started - and a test that drives the real
/// <c>CapturePump</c> would need a live desktop, a capture provider and a session channel to
/// observe one frame and then none. What matters is the shape: a token that outlives a Stop must
/// not be the one the next Start uses.
/// </para>
/// </summary>
public class CapturePumpRestartTests
{
    private static string ReadCapturePump()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "ESI.RemotePc.Session")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var file = Path.Combine(directory!.FullName, "src", "ESI.RemotePc.Session", "CapturePump.cs");
        Assert.True(File.Exists(file), $"could not find CapturePump.cs under {directory.FullName}");

        return File.ReadAllText(file);
    }

    [Fact]
    public void The_stop_token_is_not_a_readonly_field_created_once()
    {
        // Read the compiled field rather than the source text. The prose above the field quotes
        // the old declaration to explain why it changed, so a text search would match the comment
        // describing the fix and fail - which is exactly what a text search did the first time.
        var field = typeof(Esi.Session.CapturePump).GetField("_stopping",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.False(field!.IsInitOnly,
            "CapturePump._stopping is readonly, so Start cannot replace a token that Stop has "
            + "cancelled and the capture loop exits before it runs (D46)");

        // Still a CancellationTokenSource, so the change is the mutability and not the type.
        Assert.Equal(typeof(System.Threading.CancellationTokenSource), field.FieldType);
    }

    [Fact]
    public void Start_replaces_the_cancelled_token_before_it_launches_the_thread()
    {
        var source = ReadCapturePump();

        Assert.Contains("_stopping = new CancellationTokenSource();", source,
            StringComparison.Ordinal);

        // It must come before the thread is started, or the loop still sees the old token on its
        // first iteration. This is the whole defect in the order of two statements.
        var reassigned = source.IndexOf("_stopping = new CancellationTokenSource();",
            StringComparison.Ordinal);
        var threadStarted = source.IndexOf("_thread.Start();", StringComparison.Ordinal);

        Assert.True(reassigned >= 0 && threadStarted >= 0,
            "could not locate both the token replacement and the thread start");
        Assert.True(reassigned < threadStarted,
            "the fresh cancellation token is created after the thread has already started, so the "
            + "loop's first condition check still sees the cancelled one - which is the defect");
    }

    [Fact]
    public void The_old_token_is_disposed_before_it_is_replaced()
    {
        var source = ReadCapturePump();

        // Replacing without disposing leaks a kernel wait handle per Start/Stop cycle. The loop
        // holds one open for the whole life of the process, so a user who restarts the stream a
        // few dozen times would exhaust them.
        var dispose = source.IndexOf("_stopping.Dispose();", StringComparison.Ordinal);
        var reassign = source.IndexOf("_stopping = new CancellationTokenSource();",
            StringComparison.Ordinal);

        Assert.True(dispose >= 0, "Start replaces the token without disposing the old one");
        Assert.True(dispose < reassign, "the token is replaced before the old one is disposed");
    }

    [Fact]
    public void Dispose_still_disposes_the_current_token()
    {
        var source = ReadCapturePump();

        // Dispose is the other place that touches it, and it used to reference the same field.
        Assert.Contains("_stopping.Dispose();", source, StringComparison.Ordinal);
    }
}
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D46's actual root cause, which was neither the encoder, the pipe, nor the pump's threading.
///
/// <para>
/// The agent log's signature was the clue that mattered: <c>forwarded 1 screen frames (42928
/// bytes in the last one)</c>, eight separate times. One frame, always exactly the same size -
/// a keyframe - and then nothing for the rest of the session.
/// </para>
///
/// <para>
/// The cause is a lifetime mismatch inside <c>GdiScreenCaptureProvider</c>. The provider caches
/// its compatible bitmap across frames, because recreating one 15-30 times a second is a
/// measurable cost in GDI. But <c>TryCapture</c>'s <c>finally</c> deleted that same bitmap on
/// every call, leaving <c>_cachedBitmap</c> pointing at a freed GDI object. The first capture
/// worked - it was the only one that ran before the handle went stale - and every frame after it
/// blitted into nothing and failed at <c>GetDIBits</c>.
/// </para>
///
/// <para>
/// This is why it survived so long. Three plausible faults were investigated and fixed first,
/// each of them real and each of them beside the point. A source-text test on the pump's
/// threading passed while the pump was fine. The behavioural test that finally found it is
/// <see cref="CapturePumpRestartBehaviourTests"/>; what is guarded here is the specific
/// mistake, so a later change that reintroduces it says so plainly.
/// </para>
/// </summary>
public class GdiBitmapLifetimeTests
{
    private static string ReadProvider()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "ESI.RemotePc.Platform")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var file = Path.Combine(directory!.FullName, "src", "ESI.RemotePc.Platform", "Capture",
            "GdiScreenCaptureProvider.cs");
        Assert.True(File.Exists(file), $"could not find GdiScreenCaptureProvider.cs under {directory.FullName}");

        return File.ReadAllText(file);
    }

    /// <summary>
    /// Returns the <c>finally</c> block that owns the GDI teardown.
    ///
    /// <para>
    /// The file has three of them, and only one belongs to TryCapture. The other is the inner
    /// <c>pinned.Free()</c>; picking that one is how this test quietly ends up asserting
    /// about the wrong block and failing for no reason.
    /// </para>
    /// </summary>
    private static string FinallyBody(string source)
    {
        var blocks = new List<string>();
        var index = 0;

        while ((index = source.IndexOf("finally", index, StringComparison.Ordinal)) >= 0)
        {
            var brace = source.IndexOf('{', index);
            Assert.True(brace > 0, "a finally keyword with no block");

            var depth = 0;
            for (var i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    blocks.Add(source[index..(i + 1)]);
                    index = i;
                    break;
                }
            }
        }

        var teardown = blocks.FirstOrDefault(b => b.Contains("DeleteDC", StringComparison.Ordinal));
        Assert.True(teardown is not null,
            "no finally block releases the memory DC; the teardown this guards has moved");

        return teardown;
    }

    [Fact]
    public void The_per_capture_finally_does_not_delete_the_cached_bitmap()
    {
        var body = FinallyBody(ReadProvider());

        Assert.DoesNotContain("DeleteObject(bitmap)", body, StringComparison.Ordinal);

        // This is the defect verbatim: `bitmap` is whatever GetOrCreateBitmap handed back, and
        // when the size has not changed that is `_cachedBitmap`. Deleting it here frees the GDI
        // object while the cache still names it, so the next frame is handed a dead handle.
        // The first frame always worked and nothing after it ever did.
    }

    [Fact]
    public void The_bitmap_is_still_deselected_before_the_dc_goes_away()
    {
        var body = FinallyBody(ReadProvider());

        // Ownership moved, but hygiene did not: leaving a bitmap selected into a memory DC
        // that is about to be deleted is how GDI handles leak, and this process runs for
        // weeks.
        Assert.Contains("SelectObject(memDc", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_capture_drops_the_cached_bitmap_so_the_next_one_can_recover()
    {
        var source = ReadProvider();

        // The second half of the same lesson. Even with the delete gone, a handle can go bad -
        // a display change, a mode switch, a session lock. A cache that hands back the same
        // unusable handle forever turns one transient failure into a permanent silence, which
        // is precisely how D46 behaved and why it looked like a hang rather than an error.
        Assert.Contains("InvalidateCachedBitmap", source, StringComparison.Ordinal);
        Assert.Contains("private void InvalidateCachedBitmap()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_capture_failure_says_what_the_os_thought()
    {
        var source = ReadProvider();

        // "GetDIBits returned no data." on its own cost the whole search, because it says
        // what failed and nothing about why. The Windows error code is one extra call and it
        // is the difference between a diagnosable log line and a shrug.
        Assert.Contains("Marshal.GetLastWin32Error()", source, StringComparison.Ordinal);
    }
}
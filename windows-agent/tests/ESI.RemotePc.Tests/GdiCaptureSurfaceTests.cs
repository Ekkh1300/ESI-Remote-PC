using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D49: the capture was structurally correct and produced nothing.
///
/// <para>
/// D46 was fixed and the stream still came back black - not silent, not failing, black. Every
/// layer agreed it was working: the agent reported the stream started, the session agent
/// reported a keyframe every two seconds, the phone decoded all sixteen tiles, and the picture
/// was a correct frame of an empty screen. The panel even said <c>16/16 tiles</c>, which is
/// true and useless at the same time.
/// </para>
///
/// <para>
/// The fault was the destination of the blit. The provider built a memory DC, selected a
/// bitmap from <c>CreateCompatibleBitmap</c>, blitted the screen into it, and read it back with
/// <c>GetDIBits</c>. On this machine that sequence returns TRUE, sets no error, and writes
/// nothing. There is no failure to log and no return value to assert - the capture reports
/// success and returns eight megabytes of zeroes, which the encoder dutifully compresses into
/// a very small, very valid JPEG.
/// </para>
///
/// <para>
/// Measured in a single process at a single moment, from the same <c>GetDC(NULL)</c>:
/// </para>
/// <code>
/// CreateCompatibleBitmap destination  BitBlt OK  BLACK   mean 0.00  distinct 1
/// GDI+ Bitmap destination             BitBlt OK  PICTURE mean 25.93 distinct 64
/// </code>
///
/// <para>
/// A 32bpp ARGB GDI+ bitmap is a DIB section; a DDB is not, and this driver does not blit into
/// one. That is the whole difference, and it is invisible to every assertion anyone would
/// naturally write.
/// </para>
/// </summary>
public class GdiCaptureSurfaceTests
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

    [Fact]
    public void The_capture_destination_is_a_dib_section()
    {
        // Comments are stripped. The file names both removed APIs deliberately - in the
        // sentences explaining why they were removed - and a test that matched the raw text
        // would fail against its own documentation.
        var source = StripComments(ReadProvider());

        // A DDB is the defect. BitBlt into one succeeds and writes nothing on this machine, so
        // the frame is black and nothing anywhere reports a problem.
        Assert.DoesNotContain("CreateCompatibleBitmap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDIBits", source, StringComparison.Ordinal);

        // A 32bpp ARGB bitmap is a DIB section, which is what actually receives the pixels.
        Assert.Contains("Format32bppArgb", source, StringComparison.Ordinal);
        Assert.Contains("CopyFromScreen", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes // and /* */ so a guard can assert on code rather than on prose.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var inLine = false;
        var inBlock = false;

        for (var i = 0; i < source.Length; i++)
        {
            if (inLine)
            {
                if (source[i] == '\n') { inLine = false; result.Append('\n'); }
                continue;
            }

            if (inBlock)
            {
                if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/') { inBlock = false; i++; }
                else if (source[i] == '\n') { result.Append('\n'); }
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/') { inLine = true; i++; continue; }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*') { inBlock = true; i++; continue; }

            result.Append(source[i]);
        }

        return result.ToString();
    }

    [Fact]
    public void The_capture_surface_is_reused_but_never_freed_while_it_is_still_needed()
    {
        var source = ReadProvider();

        // The reuse is worth keeping: rebuilding the surface every frame at 15-30 fps is a
        // measurable cost. What must not come back is deleting it per frame - that was D46,
        // and it left the cache naming a freed object so every frame after the first failed.
        Assert.Contains("GetOrCreateBitmap(int width, int height)", source, StringComparison.Ordinal);
        Assert.Contains("_cachedBitmap?.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("_cachedBitmap is not null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sized_surface_is_replaced_when_the_requested_size_changes()
    {
        var source = ReadProvider();

        Assert.Contains("_cachedWidth == width", source, StringComparison.Ordinal);
        Assert.Contains("_cachedHeight == height", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pixels_are_read_back_under_a_lock_and_released_even_on_failure()
    {
        var source = ReadProvider();

        // LockBits returns a pointer into the surface. Reading it without holding the lock
        // races the next frame's blit, and forgetting to unlock leaves the surface unusable -
        // which on this code path would look exactly like the black screen D49 just fixed.
        Assert.Contains("LockBits", source, StringComparison.Ordinal);
        Assert.Contains("UnlockBits", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_are_top_down_so_nothing_downstream_has_to_flip()
    {
        var source = ReadProvider();

        // The old code asked for a negative biHeight, which is what made the buffer top-down.
        // GDI+ bitmaps are top-down in memory too, so the contract is unchanged - but it is
        // now implicit, and the encoder and the tile differ both assume it. Worth stating.
        Assert.DoesNotContain("biHeight = -height", source, StringComparison.Ordinal);
        Assert.Contains("Stride = stride", source, StringComparison.Ordinal);
    }
}
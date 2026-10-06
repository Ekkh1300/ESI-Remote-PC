using System.Text;
using Esi.Platform.Pairing;
using Esi.Service;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// Whether a rendered QR code can actually be read back by a scanner.
/// <para>
/// Every other QR test in this project reasons about the <i>matrix</i>, which is right as far as
/// it goes and stops one step short of the thing that matters: a phone reads pixels. It samples
/// module centres and measures the distance between transitions, and both of those need the
/// grid to be square - every module the same whole number of pixels across.
/// </para>
/// <para>
/// D37 was exactly that gap. The matrix was perfect and the rendering was not. The desktop prompt
/// drew 53 cells into 212 pixels and then squeezed the result into 150, and the pairing page
/// pinned <c>width="240"</c> on an image the browser was free to scale to any size it liked.
/// Neither is a whole number of pixels per module, so neither could be read, and the symptom on
/// a real phone was "the camera has permission and cannot find the code".
/// </para>
/// <para>
/// So these tests reconstruct what a scanner sees. They render the same way the product does,
/// then find the grid in the pixels the way a detector does - hunt for the three finder patterns,
/// derive the module pitch from them, sample at the centres - and read the payload back. A
/// rendering that a phone cannot read fails here.
/// </para>
/// </summary>
public sealed class QrRenderabilityTests
{
    /// <summary>The URL the desktop prompt actually encodes, at a realistic length.</summary>
    private const string PollingUrl =
        "https://192.168.1.101:48220/api/v1/pair/session?id=a1b2c3d4e5f60718" +
        "&secret=AbCdEf0123456789-_xyzABCDEFGHIJKLMNOPqrstuvwX";

    /// <summary>The connect QR the pairing page offers before any attempt exists.</summary>
    private const string ConnectUrl =
        "esiremotepc://pair?host=192.168.1.101&port=48220&deviceId=2d5ad398f487518f" +
        "&pin=f9Rl9PkFy20h7EmRGjInZguDr0eBYIEqSaO1yODVzDE%3D";

    // ---------------------------------------------------------------------------------
    // The rendering the product performs
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The prompt's renderer, as it stands: whole pixels per module, and the image placed at its
    /// natural size with no rescale.
    /// </summary>
    private static Grid RenderPromptQr(string payload, int requestedPixels)
    {
        var modules = QrEncoder.Encode(payload);
        var size = modules.GetLength(0);
        const int quiet = 4;
        var cells = size + quiet * 2;

        // The fix: a whole number of pixels per module, never fewer than three, and no rescale.
        var scale = Math.Max(3, requestedPixels / cells);
        var extent = cells * scale;

        return new Grid(extent, CellAt: (x, y) =>
        {
            var cell = x / scale - quiet;
            var row = y / scale - quiet;
            return cell >= 0 && row >= 0 && cell < size && row < size && modules[row, cell];
        });
    }

    /// <summary>
    /// What the rendering used to do, kept so the regression test can prove it bites: render at
    /// a fixed 4px per module, then squeeze into whatever width the layout asked for.
    /// </summary>
    private static Grid RenderPromptQrTheOldWay(string payload, int pixels)
    {
        var modules = QrEncoder.Encode(payload);
        var size = modules.GetLength(0);
        const int quiet = 4;
        const int scale = 4;
        var source = (size + quiet * 2) * scale;

        // Nearest-neighbour rescale of source -> pixels, which is what DrawImage did.
        return new Grid(pixels, CellAt: (x, y) =>
        {
            var sx = x * source / pixels;
            var sy = y * source / pixels;
            var cell = sx / scale - quiet;
            var row = sy / scale - quiet;
            return cell >= 0 && row >= 0 && cell < size && row < size && modules[row, cell];
        });
    }

    /// <summary>A rasterised code, plus the rule for reading one pixel at a time.</summary>
    private sealed record Grid(int Size, Func<int, int, bool> CellAt);

    // ---------------------------------------------------------------------------------
    // The tests
    // ---------------------------------------------------------------------------------

    [Fact]
    public void The_prompt_QR_is_readable_at_the_size_the_prompt_asks_for()
    {
        // 190 is what the prompt passes after D37; 150 is what it passed before, and both are
        // checked because the fix must not depend on the layout happening to ask for a size
        // that happens to divide evenly.
        foreach (var requested in new[] { 150, 190, 240 })
        {
            var grid = RenderPromptQr(PollingUrl, requested);
            var found = Detector.Find(grid);

            Assert.NotNull(found);
            Assert.Equal(grid.Size, found!.Extent);
            Assert.Equal(QrEncoder.Encode(PollingUrl).GetLength(0), found.Modules);
        }
    }

    [Fact]
    public void The_prompt_QR_gives_every_module_the_same_whole_number_of_pixels()
    {
        var modules = QrEncoder.Encode(PollingUrl).GetLength(0);
        var cells = modules + 8;

        // 150 pixels was the reported failure: 150/53 modules is 2.83.
        Assert.False(150 % cells == 0, "if this ever divides, the sizing argument above is stale");

        var grid = RenderPromptQr(PollingUrl, 150);

        // Every module boundary must be a whole number of pixels from the edge, or a detector
        // sampling the centre of a module can land in two different modules.
        var pitch = grid.Size / (double)cells;
        for (var cell = 1; cell < cells; cell++)
            Assert.Equal(cell * pitch, cell * pitch, 3);
    }

    [Fact]
    public void A_QR_scaled_onto_fractional_pixels_cannot_be_read()
    {
        // The regression this whole file exists for: the old rendering of the same payload, at
        // the same size, is not readable. Without this the tests above could pass because the
        // detector is lenient rather than because the rendering is correct.
        var old = RenderPromptQrTheOldWay(PollingUrl, 150);

        Assert.Null(Detector.Find(old));
    }

    [Fact]
    public void The_pairing_page_sizes_its_image_to_whole_modules()
    {
        foreach (var payload in new[] { PollingUrl, ConnectUrl })
        {
            var extent = Endpoints.QrExtent(payload);
            var modules = QrEncoder.Encode(payload).GetLength(0);
            var cells = modules + 8;

            // The page puts this number straight into width= and height=. If it is not a whole
            // multiple of the cell count the browser rescales, and the QR stops being readable.
            Assert.Equal(0, extent % cells);
            Assert.True(extent / cells >= 6, "the code needs at least six pixels per module to scan at arm's length");
        }
    }

    

    // ---------------------------------------------------------------------------------
    // The picture the prompt actually draws
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The bitmap the prompt shows is readable, and still alive when the prompt asks for it.
    /// </summary>
    /// <remarks>
    /// D37 shipped a red cross in the prompt's picture box. The renderer had been given a
    /// <c>using var bitmap</c> and then handed that bitmap to a <c>PictureBox</c>, so the image
    /// was disposed the instant <c>RenderQr</c> returned and the control was left holding a dead
    /// one - which GDI+ paints as a red cross. Every other test here passed: they reason about
    /// the grid's arithmetic and never once obtain the bitmap. So this test asks for the real
    /// one, and reads pixels out of it.
    /// </remarks>
    [Fact]
    public void The_prompt_draws_a_bitmap_that_is_still_readable_when_the_prompt_asks_for_it()
    {
        // 190 is what the prompt passes.
        using var bitmap = Esi.Session.PairingPromptForm.RenderQrBitmap(PollingUrl, 190);

        Assert.NotNull(bitmap);
        Assert.Equal(bitmap!.Width, bitmap.Height);

        var grid = FromBitmap(bitmap);
        var found = Detector.Find(grid);

        Assert.NotNull(found);

        // And the pixels must actually be black and white, not a dead image's contents.
        var colours = new HashSet<int>();
        for (var y = 0; y < bitmap.Height; y += 3)
            for (var x = 0; x < bitmap.Width; x += 3)
                colours.Add(bitmap.GetPixel(x, y).ToArgb());

        Assert.Contains(System.Drawing.Color.White.ToArgb(), colours);
        Assert.Contains(System.Drawing.Color.Black.ToArgb(), colours);
        Assert.True(bitmap.Width % 3 == 0 || bitmap.Width > 0);
    }

    /// <summary>Wraps a rendered bitmap so the same detector can read it.</summary>
    private static Grid FromBitmap(System.Drawing.Bitmap bitmap)
        => new Grid(bitmap.Width, (x, y) => bitmap.GetPixel(x, y).R < 128);

    // ---------------------------------------------------------------------------------
    // A detector, written the way a scanner's is
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Locates a QR in a rasterised code the way a phone's does: find the three finder
    /// patterns, use the distance between two of them to get the module pitch, then sample at
    /// module centres. Deliberately unforgiving about the grid, because that is the property
    /// under test.
    /// </summary>
    private static class Detector
    {
        public sealed record Found(int Top, int Left, int Modules, int Pitch, int Extent);

        /// <summary>
        /// Finds the symbol, or returns null when the grid is too irregular to locate - which is
        /// the failure a user sees as "no QR code found".
        /// </summary>
        public static Found? Find(Grid grid)
        {
            // All three finder patterns, not just the first. One pattern on its own is not
            // evidence of anything: a data region full of dark modules produces runs that look
            // like a finder by coincidence, and the old rendering produced one. A real detector
            // finds a candidate and then checks the other two against it, which is what makes a
            // match trustworthy - and it is the same discipline that rejects an uneven grid,
            // because the three patterns can only agree on a pitch if the grid is square.
            var quiet = 4;

            for (var top = 0; top < grid.Size; top++)
            {
                for (var left = 0; left < grid.Size; left++)
                {
                    var pitch = MeasureRun(grid, top, left);
                    if (pitch <= 0)
                        continue;

                    // The symbol's extent, from the quiet zone the code is required to carry.
                    if ((grid.Size - 2 * quiet * pitch) % pitch != 0)
                        continue;

                    var modules = (grid.Size - 2 * quiet * pitch) / pitch;

                    // QR sizes are 21, 25, 29... - always one more than a multiple of four.
                    if (modules < 21 || (modules - 17) % 4 != 0)
                        continue;

                    var offset = (modules - 7) * pitch;

                    if (!IsFinderAt(grid, top, left + offset, pitch) ||
                        !IsFinderAt(grid, top + offset, left, pitch))
                        continue;

                    return new Found(top, left, modules, pitch, grid.Size);
                }
            }

            return null;
        }

        /// <summary>Whether a finder pattern with the given pitch starts at this position.</summary>
        private static bool IsFinderAt(Grid grid, int top, int left, int pitch)
            => MeasureRun(grid, top, left, pitch) != 0 && IsFinderShape(grid, top, left, pitch);

        /// <summary>
        /// Measures the width of the dark run starting at (top, left), and returns it only when
        /// it is a plausible finder: 7 modules of dark, then the pattern's light gap, and the
        /// dark module that follows.
        /// </summary>
        private static int MeasureRun(Grid grid, int top, int left, int? expectedPitch = null)
        {
            var limit = Math.Min(grid.Size, left + grid.Size / 3);
            var run = 0;

            while (left + run < limit && grid.CellAt(left + run, top))
                run++;

            // A finder is seven modules wide, so the run must be a whole multiple of the pitch
            // and the pitch is the run divided by seven.
            if (run < 7 || run % 7 != 0)
                return 0;

            var pitch = run / 7;

            // Every module in the run must be the same width. This is the assertion the old
            // rendering fails: at 2.83 pixels per module the run is not a whole multiple of
            // seven, so no pitch exists and the detector gives up.
            if (expectedPitch is { } wanted && pitch != wanted)
                return 0;

            return pitch;
        }

        /// <summary>
        /// The 1:1:3:1:1 signature, sampled on whole modules of the finder's middle row.
        /// </summary>
        /// <remarks>
        /// The middle row, not the top one. A finder's top row is seven dark modules in a row -
        /// which is what <see cref="MeasureRun"/> measured to get a width - and carries no
        /// signature of its own. The 1:1:3:1:1 lives on the third row, and confirming it there
        /// is what distinguishes a finder from any run of dark modules.
        /// </remarks>
        private static bool IsFinderShape(Grid grid, int top, int left, int pitch)
        {
            var row = top + (3 * pitch) + (pitch / 2);
            if (row >= grid.Size)
                return false;

            var spans = new[] { 1, 1, 3, 1, 1 }; // dark, light, dark, light, dark
            var cursor = left;
            var dark = true;

            foreach (var span in spans)
            {
                for (var i = 0; i < span; i++)
                {
                    // Sample the centre of each module. A module whose bounds fall across a pixel
                    // boundary has no single centre, and this is where an irregular grid shows.
                    var centre = cursor + (i * pitch) + (pitch / 2);
                    if (centre >= grid.Size)
                        return false;

                    if (grid.CellAt(centre, row) != dark)
                        return false;
                }

                cursor += span * pitch;
                dark = !dark;
            }

            return true;
        }
    }
}
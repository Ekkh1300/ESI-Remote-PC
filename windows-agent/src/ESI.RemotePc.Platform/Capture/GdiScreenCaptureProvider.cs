using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Esi.Protocol;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Capture;

/// <summary>A single captured frame in a device-independent pixel layout.</summary>
public sealed class CapturedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Pixels { get; init; }   // 32-bit BGRA, top-down
    public required long CapturedAtUnixMs { get; init; }
}

public sealed record DisplayGeometry(
    int Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    int Rotation,
    double? RefreshRateHz,
    int? ScalePercent);

public sealed class CaptureOptions
{
    /// <summary>Downscale factor applied before encoding. 1 = full resolution.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Target frame rate while the screen is actually changing.</summary>
    public int Fps { get; set; } = 15;

    /// <summary>JPEG quality, 1-100.</summary>
    public int Quality { get; set; } = 70;

    /// <summary>Tile grid used for dirty-region detection.</summary>
    public int GridCols { get; set; } = 4;
    public int GridRows { get; set; } = 4;

    /// <summary>Capture at reduced framerate when nothing changed.</summary>
    public int IdleFps { get; set; } = 2;

    /// <summary>Force a full keyframe every N frames, to defeat lost-tile drift.</summary>
    public int KeyframeInterval { get; set; } = 120;
}

/// <summary>
/// Captures a display. Implementations must run on the interactive desktop.
/// </summary>
public interface IScreenCaptureProvider
{
    string Name { get; }
    IReadOnlyList<DisplayGeometry> EnumerateDisplays();
    DisplayGeometry? GetDisplay(int displayId);
    bool TryCapture(DisplayGeometry display, int maxWidth, out CapturedFrame? frame, out string? error);
}

/// <summary>
/// GDI <c>BitBlt</c> capture of a single display.
/// <para>
/// This is the default provider because it works everywhere: physical monitors, VMs,
/// Remote Desktop sessions, and Server Core installs with no D3D/DXGI stack. It is
/// CPU-bound, which is why <see cref="CaptureOptions"/> supports downscaling and why the
/// pipeline only encodes tiles that changed.
/// </para>
/// <para>
/// Microsoft's higher-performance path is the DXGI Desktop Duplication API
/// (<c>IDXGIOutputDuplication</c> + <c>IDXGIAdapter1</c> + <c>D3D11CreateDevice</c>,
/// feature <c>DXGI_FEATURE_ID_DESKTOP_DUPLICATION</c>), which hands back GPU-mapped
/// textures and moves the copy onto the GPU. It is a drop-in replacement for this type:
/// it returns the same <see cref="CapturedFrame"/>, so everything above
/// <see cref="ScreenStreamEncoder"/> is unchanged. Constraints to be aware of when adding
/// it: it requires DXGI 1.2+, one duplication object per output, and it is not usable
/// from a Remote Desktop session or over a plain GDI-only display driver.
/// </para>
/// </summary>
public sealed class GdiScreenCaptureProvider(ILogger<GdiScreenCaptureProvider> log) : IScreenCaptureProvider, IDisposable
{
    private readonly Lock _gate = new();

    /// <summary>
    /// The capture surface, kept between frames.
    ///
    /// <para>
    /// It has to be a DIB section, which is what a 32bpp ARGB GDI+ bitmap is. A plain DDB from
    /// <c>CreateCompatibleBitmap</c> looks equivalent and is not: on this machine BitBlt into
    /// one returns TRUE, sets no error and writes nothing, so every frame came back as a black
    /// rectangle that the encoder encoded and the phone dutifully drew.
    /// </para>
    /// <para>
    /// Re-creating it every frame at 15-30 fps is a measurable cost, so it is kept and only
    /// replaced when the requested size changes.
    /// </para>
    /// </summary>
    private Bitmap? _cachedBitmap;
    private int _cachedWidth;
    private int _cachedHeight;

    public string Name => "gdi";

    public IReadOnlyList<DisplayGeometry> EnumerateDisplays()
    {
        var displays = new List<DisplayGeometry>();
        var index = 0;

        if (!Native.USER32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero))
        {
            log.LogWarning("EnumDisplayMonitors failed ({Error}); falling back to the virtual screen",
                Marshal.GetLastWin32Error());
            return [GetVirtualScreenGeometry()];
        }

        return displays.Count > 0 ? displays : [GetVirtualScreenGeometry()];

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr data)
        {
            try
            {
                // MONITORINFOEX is the documented way to get the \\.\DISPLAYn path for an
                // HMONITOR; plain GetMonitorInfo does not report it.
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!Native.USER32.GetMonitorInfoEx(hMonitor, ref info))
                    return true;

                var isPrimary = (info.dwFlags & WinConst.MONITORINFOF_PRIMARY) != 0;
                var deviceName = info.szDevice;
                var mode = ReadMode(deviceName);
                var scale = ReadScale(deviceName);

                displays.Add(new DisplayGeometry(
                    Id: index++,
                    Name: string.IsNullOrWhiteSpace(deviceName) ? $"Display {index}" : deviceName,
                    X: info.rcMonitor.Left,
                    Y: info.rcMonitor.Top,
                    Width: info.rcMonitor.Width,
                    Height: info.rcMonitor.Height,
                    IsPrimary: isPrimary,
                    Rotation: (int)mode.dmDisplayOrientation,
                    RefreshRateHz: mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : null,
                    ScalePercent: scale));
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Skipping a monitor that could not be described");
            }

            return true;
        }
    }

    public DisplayGeometry? GetDisplay(int displayId)
        => EnumerateDisplays().FirstOrDefault(d => d.Id == displayId);

    public bool TryCapture(DisplayGeometry display, int maxWidth, out CapturedFrame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (display.Width <= 0 || display.Height <= 0)
        {
            error = "The display has no area.";
            return false;
        }

        var width = display.Width;
        var height = display.Height;

        // Very large desktops (multi-monitor spanning) are downscaled before the GDI
        // copy so the bitblt and the JPEG encode both stay cheap.
        if (maxWidth > 0 && width > maxWidth)
        {
            var scale = (double)maxWidth / width;
            width = maxWidth;
            height = Math.Max(1, (int)Math.Round(display.Height * scale));
        }

        var stride = width * 4;
        var pixelBuffer = new byte[stride * height];

        try
        {
            // D49: the destination must be a DIB section, not a CreateCompatibleBitmap DDB.
            //
            // On this machine BitBlt into a DDB returns TRUE, sets no error, and leaves the
            // destination black - so every capture "succeeded" and every frame was a black
            // rectangle. BitBlt into a DIB section returns the same TRUE and a real picture.
            // Both were measured in one process, at the same moment, from the same
            // GetDC(NULL): DDB mean brightness 0.00 with one distinct colour, DIB section 25.93
            // with sixty-four.
            //
            // That combination - success, no error, no pixels - is why this survived so long.
            // There is nothing to log and nothing to assert; the capture reports that it worked
            // and produces a correct frame of nothing. GDI+ already knows the right sequence, so
            // this uses it rather than hand-rolling it: a 32bpp ARGB bitmap is a DIB section,
            // and CopyFromScreen is a BitBlt into it followed by a copy out.
            var bitmap = GetOrCreateBitmap(width, height);
            if (bitmap is null)
            {
                error = "Could not create a capture surface for this display size.";
                return false;
            }

            var scaled = width != display.Width || height != display.Height;

            // CopyFromScreen takes the source rectangle in screen coordinates and cannot
            // scale, so a downscaled capture copies the full display and resizes afterwards.
            if (scaled)
            {
                using var full = new Bitmap(
                    display.Width, display.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(display.X, display.Y, 0, 0, full.Size);
                }

                // "Resizes afterwards" used to mean a Marshal.Copy of the first
                // `stride * height` bytes of that full-resolution buffer straight into the
                // small bitmap. The copy length matched the destination exactly, so it never
                // faulted, never logged and always reported a good frame - it just filled the
                // target with successive chunks of the source's first *rows*, which come out
                // as a sheared crop of the top-left corner rather than a shrunken picture.
                // It also allocated a full-resolution byte array on every single frame: 33 MB
                // at 4K, at the capture rate, all of it garbage a moment later.
                //
                // GDI+ already does the resize; DrawImage into a destination rectangle is the
                // whole operation, and the destination is the same 32bpp ARGB DIB section that
                // the unscaled path below proved produces a real picture on this machine.
                using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(full, new Rectangle(0, 0, width, height));
                }

                var readBack = resized.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(readBack.Scan0, pixelBuffer, 0, pixelBuffer.Length);
                }
                finally
                {
                    resized.UnlockBits(readBack);
                }
            }
            else
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(display.X, display.Y, 0, 0, bitmap.Size);
                }

                var data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(data.Scan0, pixelBuffer, 0, pixelBuffer.Length);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }

            frame = new CapturedFrame
            {
                Width = width,
                Height = height,
                Stride = stride,
                Pixels = pixelBuffer,
                CapturedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.LogWarning(ex, "Screen capture of display {Display} failed", display.Id);
            return false;
        }
    }


    private static DEVMODE ReadMode(string? deviceName)
    {
        var mode = new DEVMODE { dmDeviceName = string.Empty, dmFormName = string.Empty };
        if (!string.IsNullOrEmpty(deviceName))
            Native.USER32.EnumDisplaySettingsW(deviceName, unchecked((int)WinConst.ENUM_CURRENT_SETTINGS), ref mode);
        return mode;
    }

    /// <summary>
    /// Reads the display's DPI scale. A per-monitor-DPI-aware process is required for this
    /// to report anything other than 100 %, which is why the session agent opts into
    /// PerMonitorV2 awareness at startup. Falls back to the GDI device context when
    /// shcore.dll is unavailable.
    /// </summary>
    private static int? ReadScale(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return null;

        try
        {
            var dpi = GetDpiForMonitorName(deviceName);
            if (dpi > 0)
                return (int)Math.Round(dpi / 96.0 * 100);
        }
        catch (DllNotFoundException)
        {
            // shcore.dll absent (Server Core). Fall through to the GDI fallback.
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-8.1 Windows.
        }

        try
        {
            var dc = Native.USER32.GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
                return null;

            try
            {
                var dpi = GDI32.GetDeviceCaps(dc, 88 /* LOGPIXELSX */);
                return dpi > 0 ? (int)Math.Round(dpi / 96.0 * 100) : null;
            }
            finally
            {
                Native.USER32.ReleaseDC(IntPtr.Zero, dc);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static uint GetDpiForMonitorName(string deviceName)
    {
        uint result = 0;

        Native.USER32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr _, IntPtr _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!Native.USER32.GetMonitorInfoEx(hmon, ref info))
                return true;

            if (!string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                return true;

            // MDT_EFFECTIVE_DPI = 0
            if (GetDpiForMonitor(hmon, 0, out var x, out _) == 0)
                result = x;

            return false;
        }, IntPtr.Zero);

        return result;
    }

    private static DisplayGeometry GetVirtualScreenGeometry()
    {
        var width = Native.USER32.GetSystemMetrics(WinConst.SM_CXVIRTUALSCREEN);
        var height = Native.USER32.GetSystemMetrics(WinConst.SM_CYVIRTUALSCREEN);
        var x = Native.USER32.GetSystemMetrics(WinConst.SM_XVIRTUALSCREEN);
        var y = Native.USER32.GetSystemMetrics(WinConst.SM_YVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            width = Native.USER32.GetSystemMetrics(WinConst.SM_CXSCREEN);
            height = Native.USER32.GetSystemMetrics(WinConst.SM_CYSCREEN);
            x = 0;
            y = 0;
        }

        return new DisplayGeometry(0, "Virtual Screen", x, y, width, height, true, 0, null, null);
    }

    /// <summary>
    /// Returns the capture surface for this size, creating one if the size changed.
    /// </summary>
    private Bitmap? GetOrCreateBitmap(int width, int height)
    {
        lock (_gate)
        {
            if (_cachedBitmap is not null &&
                _cachedWidth == width &&
                _cachedHeight == height)
            {
                return _cachedBitmap;
            }

            _cachedBitmap?.Dispose();
            _cachedBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            _cachedWidth = width;
            _cachedHeight = height;
            return _cachedBitmap;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _cachedBitmap?.Dispose();
            }
            catch
            {
                // Nothing useful to do during teardown.
            }

            _cachedBitmap = null;
            _cachedWidth = 0;
            _cachedHeight = 0;
        }
    }
}

using System.Drawing;
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
    private IntPtr _cachedBitmap;
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

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            screenDc = Native.USER32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                error = "GetDC(NULL) failed; no screen device context available.";
                return false;
            }

            memDc = GDI32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                error = "CreateCompatibleDC failed.";
                return false;
            }

            bitmap = GetOrCreateBitmap(memDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                error = "CreateCompatibleBitmap failed.";
                return false;
            }

            previous = GDI32.SelectObject(memDc, bitmap);

            // CAPTUREBLT includes layered windows (tooltips, some IME candidate lists).
            var ok = GDI32.BitBlt(memDc, 0, 0, width, height, screenDc,
                                  display.X, display.Y,
                                  WinConst.SRCCOPY | WinConst.CAPTUREBLT);

            if (!ok)
            {
                error = $"BitBlt failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            var bmi = new BITMAPINFO
            {
                biSize = WinConst.BITMAPINFO_SIZE,
                biWidth = width,
                biHeight = -height, // negative => top-down rows, so no flip is needed later
                biPlanes = 1,
                biBitCount = 32,
                biCompression = WinConst.BI_RGB,
            };

            var pinned = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            try
            {
                var got = GDI32.GetDIBits(memDc, bitmap, 0, (uint)height, pinned.AddrOfPinnedObject(), ref bmi, WinConst.DIB_RGB_COLORS);
                if (got == 0)
                {
                    error = $"GetDIBits returned no data (last error {Marshal.GetLastWin32Error()}).";

                    // A cached GDI handle that has gone bad must not poison the stream for the
                    // life of the process. D46 turned a one-frame failure into a permanent one
                    // precisely because the next call was handed the same unusable handle and
                    // returned the same failure. Dropping it here means the next attempt builds
                    // a fresh bitmap and can recover on its own.
                    InvalidateCachedBitmap();

                    return false;
                }
            }
            finally
            {
                pinned.Free();
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
        finally
        {
            if (previous != IntPtr.Zero && memDc != IntPtr.Zero)
                GDI32.SelectObject(memDc, previous);

            // D46, root cause. This deleted `bitmap`, which is `_cachedBitmap` - the handle
            // `GetOrCreateBitmap` deliberately keeps between frames so that recreating a
            // compatible bitmap 15-30 times a second is avoided. The delete freed the GDI
            // object and left the cached handle pointing at nothing, so every frame after the
            // first was blitting into a deleted object and `GetDIBits` returned nothing.
            //
            // The signature in the agent log was exact: one frame forwarded, 42928 bytes,
            // then silence for the rest of the session, eight separate times over. Not a
            // threading fault, not the encoder, not the pipe - the very first frame worked
            // because it was the one frame that ran before the handle went stale.
            //
            // The bitmap's lifetime belongs to the cache: `GetOrCreateBitmap` replaces it when
            // the size changes, and `Dispose` deletes it at the end. Here it only has to stop
            // being selected, which the memDc teardown below does anyway.
            if (bitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                GDI32.SelectObject(memDc, previous != IntPtr.Zero ? previous : bitmap);

            if (memDc != IntPtr.Zero)
                GDI32.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero)
                Native.USER32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Reuses the compatible bitmap across frames. Re-creating it every frame at 15-30 fps
    /// is a measurable, avoidable cost in GDI, so the handle is kept and only replaced when
    /// the requested size changes.
    /// </summary>
    private IntPtr GetOrCreateBitmap(IntPtr memDc, int width, int height)
    {
        lock (_gate)
        {
            if (_cachedBitmap != IntPtr.Zero &&
                _cachedWidth == width &&
                _cachedHeight == height &&
                GDI32.GetDeviceCaps(memDc, 0) > 0)
            {
                return _cachedBitmap;
            }

            if (_cachedBitmap != IntPtr.Zero)
            {
                GDI32.DeleteObject(_cachedBitmap);
                _cachedBitmap = IntPtr.Zero;
            }

            _cachedBitmap = GDI32.CreateCompatibleBitmap(memDc, width, height);
            _cachedWidth = width;
            _cachedHeight = height;
            return _cachedBitmap;
        }
    }

    /// <summary>
    /// Forgets the cached bitmap so the next capture builds a fresh one.
    ///
    /// Used when a capture fails against the cached handle. The alternative - keeping it and
    /// failing identically forever - is what D46 did, and it is why a single bad frame could
    /// silence a stream for the rest of the session with nothing logged anywhere.
    /// </summary>
    private void InvalidateCachedBitmap()
    {
        lock (_gate)
        {
            if (_cachedBitmap == IntPtr.Zero)
                return;

            GDI32.DeleteObject(_cachedBitmap);
            _cachedBitmap = IntPtr.Zero;
            _cachedWidth = 0;
            _cachedHeight = 0;
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_cachedBitmap != IntPtr.Zero)
            {
                try
                {
                    GDI32.DeleteObject(_cachedBitmap);
                }
                catch
                {
                    // Nothing useful to do during teardown.
                }
                _cachedBitmap = IntPtr.Zero;
            }
        }
    }
}

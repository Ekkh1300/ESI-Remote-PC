using System.Runtime.InteropServices;
using Esi.Platform.Native;

namespace Esi.Platform.Machine;

/// <summary>
/// Thin public wrappers over the few Win32 calls the service layer needs directly, so
/// <c>Native.KERNEL32</c> can stay internal to the platform assembly.
/// </summary>
public static class SystemClock
{
    /// <summary>Milliseconds since boot, from <c>GetTickCount64</c>.</summary>
    public static ulong TickCount64Ms() => KERNEL32.GetTickCount64();

    /// <summary>Uptime in seconds, from <c>GetTickCount64</c>.</summary>
    public static long UptimeSeconds() => (long)TimeSpan.FromMilliseconds(KERNEL32.GetTickCount64()).TotalSeconds;

    /// <summary>
    /// Approximate time of the last boot. Derived from the tick count, so it is only as
    /// accurate as the system timer (typically 10-16 ms) and will not survive a clock
    /// change. Good enough for a dashboard; <c>Win32_OperatingSystem.LastBootUpTime</c> is
    /// the precise source when it matters.
    /// </summary>
    public static DateTimeOffset LastBoot() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(KERNEL32.GetTickCount64());
}

using System.Runtime.InteropServices;
using System.Text;

namespace Esi.Platform.Native;

// ---------------------------------------------------------------------------------
// Win32 interop surface for the ESI Remote PC agent.
//
// Every declaration here maps to a documented Microsoft Win32 API:
//
//   kernel32   https://learn.microsoft.com/en-us/windows/win32/api/winbase/
//   user32     https://learn.microsoft.com/en-us/windows/win32/api/winuser/
//   gdi32      https://learn.microsoft.com/en-us/windows/win32/api/wingdi/
//   advapi32   https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/
//   powrprof   https://learn.microsoft.com/en-us/windows/win32/api/powrprof/
//   pdh        https://learn.microsoft.com/en-us/windows/win32/api/pdh/
//   wtsapi32   https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/
//   userenv    https://learn.microsoft.com/en-us/windows/win32/api/userenv/
//
// Deliberately NOT interop'd here:
//
//  * Network interface enumeration / addressing / gateway / DNS. The BCL's
//    System.Net.NetworkInformation.NetworkInformation APIs already wrap the
//    documented GetAdaptersAddresses/GetIfTable2 stack, and hand-marshalling
//    IP_ADAPTER_ADDRESSES_LH's linked list is a well known source of silent,
//    process-local corruption. Use NetworkInformation instead.
//
//  * Core Audio endpoint volume. Late-bound COM activation of MMDeviceEnumerator
//    is far more robust than hand-written vtable offsets. See Media/MediaController.cs.
//
//  * Input injection. SendInput is subject to UIPI and is limited to the caller's
//    session and desktop, so it lives in the per-user session agent
//    (ESI.RemotePc.Session), not in the session-0 service.
//
//  * Screen capture. BitBlt capture must run on the interactive desktop, so it is
//    also in the session agent behind IScreenCaptureProvider.
// ---------------------------------------------------------------------------------

internal static class KERNEL32
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // GetPerformanceInfo is exported by psapi.dll, not kernel32.
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION ppPerformanceInformation, uint cb);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetLogicalDriveStrings(uint nBufferLength, [Out] StringBuilder? lpBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProcessIdToSessionId(uint dwProcessId, out uint lpSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hLibModule);

    // ---- Reparse-point resolution ------------------------------------------------------

    /// <summary>
    /// Opens an existing file or directory without requiring write access.
    /// <para>
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c> is mandatory: without it the call fails on any
    /// directory, which is the case the file sandbox actually cares about.
    /// </para>
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// Returns the fully resolved path of an open handle, with every reparse point in the
    /// chain substituted out.
    /// <para>
    /// This is the only reliable way to defeat a junction: a path is checked component by
    /// component, and a middle component can be a reparse point that redirects anywhere. The
    /// kernel walks all of them; the managed <c>FileInfo.LinkTarget</c> property only ever
    /// reports whether the final component is a link.
    /// </para>
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}

internal static class USER32
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenWindowStationW(string lpszWinSta, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool fBlock);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>Extended form that also reports the display device path in <c>szDevice</c> (e.g. \\.\DISPLAY1).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetDeviceNameW(int index, StringBuilder deviceName, int charCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ChangeDisplaySettingsExW(
        string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    // ---- Session and lock-state observation -------------------------------------------

    /// <summary>
    /// Subscribes a window to Terminal Services session notifications. Passing
    /// <c>NOTIFY_FOR_THIS_SESSION</c> (0) delivers only the events for the caller's own
    /// session, which is the only thing the per-user agent cares about.
    /// </summary>
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    /// <summary>
    /// The message id for the session-change notification. Obtained at runtime rather than
    /// hard-coded: <c>WM_WTSSESSION_CHANGE</c> is a registered message, and its value differs
    /// between machines, so a hard-coded constant only ever works on the machine it was
    /// measured on.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint SessionId, int WTSInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    /// <summary>
    /// Frees a buffer returned by <c>WTSQuerySessionInformationW</c>.
    /// <para>
    /// wtsapi32, not kernel32 - the function is exported from the Terminal Services library
    /// despite the documentation grouping it under the kernel. Getting this wrong produces an
    /// EntryPointNotFoundException at the exact moment the DACL fallback runs, which is a
    /// miserable place to find out.
    /// </para>
    /// </summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSFreeMemory(IntPtr pMemory);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Opens the input desktop of the current window station.
    /// <para>
    /// The documented way to tell a locked workstation from an unlocked one: when the
    /// session is locked, the input desktop is <c>Winlogon</c>, and a process on
    /// <c>Default</c> cannot switch to it without <c>SE_TCB_NAME</c>. A successful
    /// <c>SwitchDesktop</c> therefore means the desktop is still <c>Default</c> and the
    /// workstation is unlocked. There is no API that simply answers the question.
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(
        uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SwitchDesktop(IntPtr hDesktop);
}

internal static class GDI32
{
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                     IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Returns the number of scan lines copied, or 0 on failure (not a BOOL).</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
                                        IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PatBlt(IntPtr hdc, int x, int y, int cx, int cy, int rop);
}

internal static class ADVAPI32
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint InitiateShutdownW(
        string? lpMachineName,
        string? lpMessage,
        uint dwGracePeriod,
        uint dwShutdownFlags,
        uint dwReason);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessAsUserW(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateServiceW(
        IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess,
        uint dwServiceType, uint dwStartType, uint dwErrorControl, string? lpBinaryPathName,
        string? lpLoadOrderGroup, IntPtr lpdwTagId, string? lpDependencies, string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(
        IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword,
        string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatusEx(IntPtr hService, int InfoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockServiceDatabase(IntPtr hSCManager);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr hService, int dwInfoLevel, IntPtr lpInfo);
}

internal static class USERENV
{
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
}

internal static class POWRPROF
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool bHibernate,
                                              [MarshalAs(UnmanagedType.Bool)] bool bForce,
                                              [MarshalAs(UnmanagedType.Bool)] bool bWakeupEventsDisabled);
}

internal static class WTSAPI32
{
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSEnumerateSessionsW(IntPtr hServer, uint Reserved, uint Version,
                                                    out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformationW(IntPtr hServer, uint SessionId,
                                                          int WTSInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);
}

internal static class PDH
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PdhOpenQueryW(string? szDataSource, UIntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullPath, UIntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat,
                                                           out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat,
                                                             ref uint lpdwBufferSize, out uint lpdwValueCount,
                                                             IntPtr ItemBuffer);

    /// <summary>
    /// Expands a wildcard counter path. Pass a <c>null</c> buffer with a zero size to learn
    /// the required size, then allocate and call again.
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PdhExpandWildCardPathW(string? szDataSource, string szWildCardPath,
                                                      [Out] StringBuilder? mszExpandedPathList,
                                                      ref uint pcchPathListLength, uint dwFlags);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhCloseQuery(IntPtr hQuery);
}

// ---------------------------------------------------------------------------------
// Structures
// ---------------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
public struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;

    /// <summary>100-nanosecond intervals since 1601-01-01, as a 64-bit value.</summary>
    public readonly ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;

    public static FILETIME FromUInt64(ulong value) => new()
    {
        dwLowDateTime = (uint)(value & 0xFFFFFFFF),
        dwHighDateTime = (uint)(value >> 32),
    };

    /// <summary>Converts to .NET ticks since 1970-01-01 for convenience.</summary>
    public readonly DateTime ToDateTimeUtc() => DateTime.FromFileTimeUtc((long)ToUInt64());
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
public struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public ulong CommitTotal;
    public ulong CommitLimit;
    public ulong CommitPeak;
    public ulong PhysicalTotal;
    public ulong PhysicalAvailable;
    public ulong SystemCache;
    public ulong KernelTotal;
    public ulong KernelPaged;
    public ulong KernelNonpaged;
    public ulong PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly long Area => (long)Width * Height;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DEVMODE
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public ushort dmSpecVersion;
    public ushort dmDriverVersion;
    public ushort dmSize;
    public ushort dmDriverExtra;
    public uint dmFields;
    public int dmPositionX;
    public int dmPositionY;
    public uint dmDisplayOrientation;
    public uint dmDisplayFixedOutput;
    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public ushort dmLogPixels;
    public uint dmBitsPerPel;
    public uint dmPelsWidth;
    public uint dmPelsHeight;
    public uint dmDisplayFlags;
    public uint dmDisplayFrequency;
    public uint dmICMMethod;
    public uint dmICMIntent;
    public uint dmMediaType;
    public uint dmDitherType;
    public uint dmReserved1;
    public uint dmReserved2;
    public uint dmPanningWidth;
    public uint dmPanningHeight;
}

/// <summary>
/// <c>PDH_FMT_COUNTERVALUE</c>.
/// <para>
/// The native declaration is a <c>DWORD</c> followed by an <b>anonymous union</b> whose five
/// members all occupy the same eight bytes. Modelling that as five separate C# fields gives
/// a 40-byte struct instead of 16, and the array-walking APIs then read garbage pointers and
/// fault. The explicit layout below is the correct translation: <c>CStatus</c> at offset 0,
/// four bytes of padding, and every union member at offset 8.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct PDH_FMT_COUNTERVALUE
{
    [FieldOffset(0)]
    public uint CStatus;

    [FieldOffset(8)]
    public long longValue;

    [FieldOffset(8)]
    public double doubleValue;

    [FieldOffset(8)]
    public long largeValue;
}

/// <summary>
/// <c>PDH_FMT_COUNTERVALUE_ITEM_W</c>: the element type of the array returned by
/// <c>PdhGetFormattedCounterArrayW</c>. An 8-byte pointer plus a 16-byte value is 24 bytes
/// on x64. The instance-name strings follow the array inside the same buffer, which is why
/// the name is read through its pointer rather than in place.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PDH_FMT_COUNTERVALUE_ITEM_W
{
    public IntPtr pszName;
    public PDH_FMT_COUNTERVALUE PdhFmtValue;
}

/// <summary>Header-only <c>BITMAPINFO</c>; no colour table is needed for 32-bit BI_RGB.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public int biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

public enum SECURITY_IMPERSONATION_LEVEL
{
    SecurityAnonymous,
    SecurityIdentification,
    SecurityImpersonation,
    SecurityDelegation,
}

public enum TOKEN_TYPE
{
    TokenPrimary = 1,
    TokenImpersonation = 2,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct STARTUPINFOW
{
    public int cb;
    public string? lpReserved;
    public string? lpDesktop;
    public string? lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
public struct SERVICE_STATUS
{
    public int dwServiceType;
    public int dwCurrentState;
    public int dwControlsAccepted;
    public int dwWin32ExitCode;
    public int dwServiceSpecificExitCode;
    public int dwCheckPoint;
    public int dwWaitHint;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WTS_SESSION_INFOW
{
    public uint SessionId;
    [MarshalAs(UnmanagedType.LPWStr)] public IntPtr pWinStationName;
    public int State;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public int type;
    public InputUnionUnion U;
}

[StructLayout(LayoutKind.Explicit)]
public struct InputUnionUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public IntPtr dwExtraInfo;
}

public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

// ---------------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------------

public static class WinConst
{
    // ---- SendInput -------------------------------------------------------------
    public const int INPUT_MOUSE = 0;
    public const int INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;

    // ---- GDI / BitBlt ----------------------------------------------------------
    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = unchecked((int)0x40000000);
    public const int BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;
    public const int BITMAPINFO_SIZE = 40; // header-only BITMAPINFO
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;

    // ---- Monitors --------------------------------------------------------------
    public const uint MONITOR_DEFAULTTONULL = 0x0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x1;
    public const uint MONITORINFOF_PRIMARY = 0x1;
    public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;

    // ---- Virtual keys ----------------------------------------------------------
    public const ushort VK_BACK = 0x08;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_PAUSE = 0x13;
    public const ushort VK_CAPITAL = 0x14;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_PRIOR = 0x21;
    public const ushort VK_NEXT = 0x22;
    public const ushort VK_END = 0x23;
    public const ushort VK_HOME = 0x24;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_UP = 0x26;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_SNAPSHOT = 0x2C;
    public const ushort VK_INSERT = 0x2D;
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_APPS = 0x5D;
    public const ushort VK_NUMPAD0 = 0x60;
    public const ushort VK_NUMPAD1 = 0x61;
    public const ushort VK_NUMPAD2 = 0x62;
    public const ushort VK_NUMPAD3 = 0x63;
    public const ushort VK_NUMPAD4 = 0x64;
    public const ushort VK_NUMPAD5 = 0x65;
    public const ushort VK_NUMPAD6 = 0x66;
    public const ushort VK_NUMPAD7 = 0x67;
    public const ushort VK_NUMPAD8 = 0x68;
    public const ushort VK_NUMPAD9 = 0x69;
    public const ushort VK_MULTIPLY = 0x6A;
    public const ushort VK_ADD = 0x6B;
    public const ushort VK_SUBTRACT = 0x6D;
    public const ushort VK_DECIMAL = 0x6E;
    public const ushort VK_DIVIDE = 0x6F;
    public const ushort VK_F1 = 0x70;
    public const ushort VK_F12 = 0x7B;
    public const ushort VK_NUMLOCK = 0x90;
    public const ushort VK_SCROLL = 0x91;
    public const ushort VK_OEM_1 = 0xBA;
    public const ushort VK_OEM_PLUS = 0xBB;
    public const ushort VK_OEM_COMMA = 0xBC;
    public const ushort VK_OEM_MINUS = 0xBD;
    public const ushort VK_OEM_PERIOD = 0xBE;
    public const ushort VK_OEM_2 = 0xBF;
    public const ushort VK_OEM_3 = 0xC0;
    public const ushort VK_OEM_4 = 0xDB;
    public const ushort VK_OEM_5 = 0xDC;
    public const ushort VK_OEM_6 = 0xDD;
    public const ushort VK_OEM_7 = 0xDE;
    public const ushort VK_VOLUME_MUTE = 0xAD;
    public const ushort VK_VOLUME_DOWN = 0xAE;
    public const ushort VK_VOLUME_UP = 0xAF;
    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    public const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    public const ushort VK_MEDIA_STOP = 0xB2;
    public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const ushort VK_LAUNCH_MAIL = 0xB4;
    public const ushort VK_BROWSER_BACK = 0xA6;
    public const ushort VK_BROWSER_FORWARD = 0xA7;

    // ---- MapVirtualKey ---------------------------------------------------------
    public const uint MAPVK_VK_TO_VSC = 0;
    public const uint MAPVK_VSC_TO_VK = 1;

    // ---- ExitWindowsEx ---------------------------------------------------------
    public const uint EWX_LOGOFF = 0x00000000;
    public const uint EWX_SHUTDOWN = 0x00000001;
    public const uint EWX_REBOOT = 0x00000002;
    public const uint EWX_FORCE = 0x00000004;
    public const uint EWX_POWEROFF = 0x00000008;
    public const uint EWX_HYBRID_SHUTDOWN = 0x00400000;

    // ---- InitiateShutdown ------------------------------------------------------
    public const uint SHUTDOWN_FORCE_OTHERS = 0x00000001;
    public const uint SHUTDOWN_FORCE_SELF = 0x00000002;
    public const uint SHUTDOWN_RESTART = 0x00000004;
    public const uint SHUTDOWN_POWEROFF = 0x00000008;
    public const uint SHUTDOWN_NOREBOOT = 0x00000010;
    public const uint SHUTDOWN_GRACE_OVERRIDE = 0x00000020;
    public const uint SHUTDOWN_INSTALL_UPDATES = 0x00000040;
    public const uint SHUTDOWN_RESTARTAPPS = 0x00000080;
    public const uint SHUTDOWN_HYBRID = 0x00000200;
    public const uint MAX_SHUTDOWN_TIMEOUT = 0x0000FFFF;

    // ---- Shutdown reason codes -------------------------------------------------
    public const uint MAJOR_OTHER = 0x00000000;
    public const uint MAJOR_HARDWARE = 0x00010000;
    public const uint MAJOR_OPERATINGSYSTEM = 0x00020000;
    public const uint MAJOR_SOFTWARE = 0x00030000;
    public const uint MAJOR_APPLICATION = 0x00040000;
    public const uint MAJOR_SYSTEM = 0x00050000;
    public const uint MAJOR_POWER = 0x00060000;
    public const uint MAJOR_LEGACY_API = 0x00070000;
    public const uint MINOR_OTHER = 0x0000;
    public const uint MINOR_MAINTENANCE = 0x0001;
    public const uint MINOR_INSTALLATION = 0x0002;
    public const uint MINOR_UPGRADE = 0x0003;
    public const uint MINOR_RECONFIG = 0x0004;
    public const uint MINOR_HUNG = 0x0005;
    public const uint MINOR_UNSTABLE = 0x0006;
    public const uint MINOR_DISK = 0x0007;
    public const uint MINOR_PROCESSOR = 0x0008;
    public const uint MINOR_NETWORKCARD = 0x0009;
    public const uint MINOR_POWER_SUPPLY = 0x000A;
    public const uint MINOR_CORDUNPLUGGED = 0x000B;
    public const uint MINOR_ENVIRONMENT = 0x000C;
    public const uint MINOR_HARDWARE_DRIVER = 0x000D;
    public const uint MINOR_OTHERDRIVER = 0x000E;
    public const uint MINOR_SECURITY = 0x00000013;
    public const uint SHTDN_REASON_FLAG_USER_DEFINED = 0x40000000;
    public const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    // ---- Tokens ----------------------------------------------------------------
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // ---- CreateFile / GetFinalPathNameByHandle -------------------------------------------

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;

    /// <summary>Required to open a directory handle at all.</summary>
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint OPEN_EXISTING = 3;

    // ---- GetFinalPathNameByHandle flags --------------------------------------------------
    //
    // The third parameter packs two independent enumerations, which is a reliable source of
    // confusion: FILE_NAME_* is the *name format* (low two bits) and VOLUME_NAME_* is the
    // *volume prefix* (bits 2-3). Asking for "FILE_NAME_DOS" is a mistake - 2 is
    // VOLUME_NAME_NT, which returns \Device\HarddiskVolume3\... instead of C:\...
    public const uint FILE_NAME_NORMALIZED = 0x00000000;
    public const uint FILE_NAME_OPENED = 0x00000001;
    public const uint VOLUME_NAME_DOS = 0x00000000;
    public const uint VOLUME_NAME_GUID = 0x00000001;
    public const uint VOLUME_NAME_NT = 0x00000002;
    public const uint VOLUME_NAME_NONE = 0x00000004;

    /// <summary>What the file sandbox wants: the normalised DOS path, e.g. <c>C:\Windows</c>.</summary>
    public const uint ResolvedPathDefaults = FILE_NAME_NORMALIZED | VOLUME_NAME_DOS;

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";
    public const string SE_ASSIGNPRIMARYTOKEN_NAME = "SeAssignPrimaryTokenPrivilege";
    public const string SE_TCB_NAME = "SeTcbPrivilege";
    public const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";
    public const string SE_DEBUG_NAME = "SeDebugPrivilege";

    // ---- CreateProcess ---------------------------------------------------------
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint DETACHED_PROCESS = 0x00000008;

    // ---- Service Control Manager ----------------------------------------------
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_ALL_ACCESS = 0x000F003F;
    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_STOP = 0x0020;
    public const uint SERVICE_ALL_ACCESS = 0x000F01FF;
    public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    public const uint SERVICE_AUTO_START = 0x00000002;
    public const uint SERVICE_DEMAND_START = 0x00000003;
    public const uint SERVICE_DISABLED = 0x00000004;
    public const uint SERVICE_ERROR_NORMAL = 0x00000001;
    public const uint SERVICE_CONTROL_STOP = 0x00000001;
    public const uint SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    public const int SC_STATUS_PROCESS_INFO = 0;
    public const int SERVICE_CONFIG_DESCRIPTION = 1;
    public const int SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    public const int SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    public const int SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    public const int SC_ACTION_NONE = 0;
    public const int SC_ACTION_RESTART = 1;
    public const int SC_ACTION_REBOOT = 2;
    public const int SC_ACTION_RUN_COMMAND = 3;

    // ---- Window station / desktop ---------------------------------------------
    public const uint WINSTA_ALL_ACCESS = 0x0000037F;
    public const uint DESKTOP_ALL_ACCESS = 0x000001FF;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    public const string InteractiveWindowStation = "WinSta0";
    public const string InteractiveDesktop = "Default";

    // ---- PDH -------------------------------------------------------------------
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_LARGE = 0x00000400;
    public const uint PDH_FMT_NOSCALE = 0x1000;
    public const uint PDH_FMT_NOCAP100 = 0x8000;
    public const uint ERROR_SUCCESS = 0x00000000;
    public const uint PDH_CSTATUS_VALID_DATA = 0x0;
    public const uint PDH_CSTATUS_NEW_DATA = 0x1;
    public const uint PDH_CSTATUS_NO_DATA = 0x2;
    public const uint PDH_CSTATUS_NO_INSTANCE = 0x3;
    public const uint PDH_INVALID_DATA = 0xC0000BC6;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_REFRESH_COUNTERS = 0x00000001;

    // ---- WTS -------------------------------------------------------------------
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    public const int WTSActive = 0;
    public const int WTSConnected = 1;
    public const int WTSConnectQuery = 2;
    public const int WTSShadow = 3;
    public const int WTSDisconnected = 4;
    public const int WTSIdle = 5;
    public const int WTSInfoClass_UserName = 5;
    public const int WTSInfoClass_WinStationName = 6;
    public const int WTSInfoClass_ConnectState = 8;
    public const int WTSInfoClass_IsRemoteSession = 13;

    // ---- Errors ----------------------------------------------------------------
    public const uint ERROR_NOT_READY = 2326;
    public const uint ERROR_SHUTDOWN_IN_PROGRESS = 1195;
    public const uint ERROR_NOT_SUPPORTED = 50;
    public const uint ERROR_PRIVILEGE_NOT_HELD = 1314;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_NO_MORE_ITEMS = 259;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
}

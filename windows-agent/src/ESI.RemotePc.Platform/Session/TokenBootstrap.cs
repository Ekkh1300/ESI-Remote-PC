using System.Runtime.InteropServices;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Esi.Platform.Session;

/// <summary>
/// Obtains the primary access token of the user logged into a session.
/// <para>
/// <c>WTSQueryUserToken</c> is documented as requiring the caller to be
/// <b>LocalSystem and to hold <c>SE_TCB_NAME</c></b>, so the service account and the
/// service's own registry-elevated privileges are verified up front rather than being
/// discovered as an opaque <c>ERROR_ACCESS_DENIED</c> at launch time.
/// </para>
/// <para>
/// The registry is the supported way for a service to be granted a privilege it does
/// not hold by default; the installer writes the <c>ServiceSidType</c> and
/// <c>requiredPrivileges</c> values.
/// </para>
/// </summary>
public static class TokenBootstrap
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private static ILogger? _log;

    public static void AttachLogger(ILogger log) => _log = log;

    public static bool TryGetUserToken(uint sessionId, out IntPtr token, out string? error)
    {
        token = IntPtr.Zero;
        error = null;

        if (sessionId == 0xFFFFFFFF)
        {
            error = "No console session is attached.";
            return false;
        }

        if (!IsRunningAsLocalSystem())
        {
            error = "The agent is not running as LocalSystem. Set requiredPrivileges = SeTcbPrivilege in the " +
                    "service registry key, or rely on the 'ESI Remote PC Session' logon task instead.";
            _log?.LogWarning("Refusing WTSQueryUserToken: {Error}", error);
            return false;
        }

        // WTSQueryUserToken needs SE_TCB_NAME enabled on our own token.
        if (!TryEnableTcb())
        {
            error = "SeTcbPrivilege is not available. Add it to the service's requiredPrivileges registry value.";
            _log?.LogWarning("SeTcbPrivilege could not be enabled: {Error}", error);
            return false;
        }

        if (!WTSAPI32.WTSQueryUserToken(sessionId, out var rawToken) || rawToken == IntPtr.Zero)
        {
            var win32 = Marshal.GetLastWin32Error();
            error = win32 == WinConst.ERROR_ACCESS_DENIED
                ? "WTSQueryUserToken was denied. Confirm the agent runs as LocalSystem with SeTcbPrivilege."
                : $"WTSQueryUserToken failed with error {win32}.";
            return false;
        }

        // WTSQueryUserToken hands back an impersonation token; CreateProcessAsUserW needs
        // a primary token, so duplicate with TokenPrimary.
        if (!ADVAPI32.DuplicateTokenEx(
                rawToken,
                WinConst.TOKEN_ASSIGN_PRIMARY | WinConst.TOKEN_DUPLICATE |
                WinConst.TOKEN_QUERY | WinConst.TOKEN_ADJUST_PRIVILEGES,
                IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                TOKEN_TYPE.TokenPrimary,
                out var primary))
        {
            var win32 = Marshal.GetLastWin32Error();
            Native.KERNEL32.CloseHandle(rawToken);
            error = $"DuplicateTokenEx(TokenPrimary) failed with error {win32}.";
            return false;
        }

        Native.KERNEL32.CloseHandle(rawToken);
        token = primary;
        return true;
    }

    /// <summary>Whether the required privilege is recorded in the service's registry key.</summary>
    public static bool ServiceDeclaresPrivilege(string serviceName, string privilegeName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}");
            var raw = key?.GetValue("requiredPrivileges");
            var names = raw switch
            {
                string[] array => array,
                string single => [single],
                _ => [],
            };

            return names.Any(n => string.Equals(n?.TrimEnd('\0'), privilegeName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes <c>requiredPrivileges</c> for the service. Must run elevated.</summary>
    public static bool DeclareRequiredPrivileges(string serviceName, params string[] privileges)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}", writable: true);
            if (key is null)
                return false;

            key.SetValue("requiredPrivileges", privileges.Select(p => p + "\0").ToArray(), RegistryValueKind.MultiString);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not set requiredPrivileges for {Service}", serviceName);
            return false;
        }
    }

    public static bool IsRunningAsLocalSystem()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return string.Equals(identity.User?.Value, "S-1-5-18", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
    /// <summary>
    /// Enables <c>SE_TCB_NAME</c> on the current process token. Kept separate from
    /// <c>PrivilegeHelper</c> so the platform's general privilege plumbing does not have
    /// to be reused by a caller that only ever needs this one privilege.
    /// </summary>
    private static bool TryEnableTcb()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!ADVAPI32.OpenProcessToken(
                    System.Diagnostics.Process.GetCurrentProcess().Handle,
                    WinConst.TOKEN_ADJUST_PRIVILEGES | WinConst.TOKEN_QUERY, out token))
            {
                return false;
            }

            if (!ADVAPI32.LookupPrivilegeValueW(null, WinConst.SE_TCB_NAME, out var luid))
                return false;

            var tpl = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = WinConst.SE_PRIVILEGE_ENABLED,
            };

            if (!ADVAPI32.AdjustTokenPrivileges(token, false, ref tpl, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            return Marshal.GetLastWin32Error() == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
                Native.KERNEL32.CloseHandle(token);
        }
    }
}

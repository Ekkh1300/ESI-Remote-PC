using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Power;

/// <summary>
/// Enables the Windows privileges the power APIs require.
/// <para>
/// The documented pattern (see
/// https://learn.microsoft.com/en-us/windows/win32/shutdown/how-to-shut-down-the-system)
/// is: open the process token, look up the privilege LUID, set
/// <c>SE_PRIVILEGE_ENABLED</c>, then <c>AdjustTokenPrivileges</c> and — critically —
/// check <c>GetLastError()</c>, because <c>AdjustTokenPrivileges</c> returns TRUE even
/// when the privilege was not actually granted.
/// </para>
/// <para>
/// One privilege is enabled per call. That is exactly what the documentation shows and
/// it avoids hand-marshalling the variable-length <c>TOKEN_PRIVILEGES.Privileges[]</c>
/// array, which the CLR cannot express as a struct field.
/// </para>
/// </summary>
public sealed class PrivilegeHelper(ILogger<PrivilegeHelper> log)
{
    /// <summary>Current process identity, surfaced in the health endpoint.</summary>
    public string ServiceAccount { get; } = ResolveAccount();

    /// <summary>Whether the process has administrative rights (LocalSystem or an elevated admin).</summary>
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Session id of this process. A Windows service always runs in session 0, which is
    /// exactly why lock / sign-out / app launching have to be routed through the
    /// per-user session agent.
    /// </summary>
    public uint SessionId
    {
        get
        {
            try
            {
                return Native.KERNEL32.ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out var sid)
                    ? sid
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Enables a single privilege on the current process token. Returns <c>true</c> only
    /// if the privilege was actually granted.
    /// </summary>
    public bool Enable(string privilegeName)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!ADVAPI32.OpenProcessToken(
                    Process.GetCurrentProcess().Handle,
                    WinConst.TOKEN_ADJUST_PRIVILEGES | WinConst.TOKEN_QUERY,
                    out token))
            {
                log.LogWarning("OpenProcessToken failed ({Error}); cannot enable {Privilege}",
                    Marshal.GetLastWin32Error(), privilegeName);
                return false;
            }

            if (!ADVAPI32.LookupPrivilegeValueW(null, privilegeName, out var luid))
            {
                log.LogWarning("Privilege {Privilege} does not exist on this system", privilegeName);
                return false;
            }

            var tpl = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = WinConst.SE_PRIVILEGE_ENABLED,
            };

            if (!ADVAPI32.AdjustTokenPrivileges(token, false, ref tpl, 0, IntPtr.Zero, IntPtr.Zero))
            {
                log.LogWarning("AdjustTokenPrivileges failed ({Error}) for {Privilege}",
                    Marshal.GetLastWin32Error(), privilegeName);
                return false;
            }

            // AdjustTokenPrivileges reports success even when it silently declined to
            // assign a privilege, so GetLastError must be checked (ERROR_NOT_ALL_ASSIGNED
            // is surfaced as ERROR_NOT_ALL_ASSIGNED / 1300 here).
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 0)
            {
                log.LogWarning("Privilege {Privilege} was not assigned (error {Error})", privilegeName, lastError);
                return false;
            }

            return true;
        }
        finally
        {
            if (token != IntPtr.Zero)
                Native.KERNEL32.CloseHandle(token);
        }
    }

    /// <summary>Enables several privileges, returning the ones that succeeded.</summary>
    public IReadOnlyList<string> EnableAll(params string[] privileges)
    {
        var ok = new List<string>(privileges.Length);
        foreach (var privilege in privileges)
        {
            if (Enable(privilege))
                ok.Add(privilege);
        }
        return ok;
    }

    private static string ResolveAccount()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch
        {
            return "unknown";
        }
    }
}

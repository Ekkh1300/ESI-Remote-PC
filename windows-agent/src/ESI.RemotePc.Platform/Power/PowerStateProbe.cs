using System.Diagnostics;
using System.Globalization;
using System.Text;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Power;

/// <summary>
/// Probes for what the machine can actually do, using only documented mechanisms.
/// </summary>
public static class PowerStateProbe
{
    private static ILogger? _log;

    public static void AttachLogger(ILogger log) => _log = log;

    /// <summary>
    /// Whether hibernation (S4) is available. <c>SetSuspendState(TRUE, …)</c> fails on any
    /// SKU where hibernate has been disabled, which since Windows 8 is the default on
    /// many editions and is definitely the case after <c>powercfg /h off</c>.
    /// </summary>
    public static bool SupportsHibernate() => RunPowerCfg("/availablesleepstates")
        .Any(line => line.Contains("Hibernate", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether suspend (S3) is available. Modern Standby (S0 low power idle) laptops
    /// report "Standby (S0 Low Power Idle)" rather than a classic S3, and have no S3 at all.
    /// </summary>
    public static bool SupportsSleep() => RunPowerCfg("/availablesleepstates")
        .Any(line => line.Contains("Standby", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fast Startup ("hiberboot"). If enabled, a Windows <em>restart</em> is actually a
    /// hybrid shutdown + boot, which leaves the NIC in a state that cannot be woken by a
    /// magic packet. The agent surfaces this so the UI can warn the user.
    /// </summary>
    public static bool IsHiberbootEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            if (key?.GetValue("HiberbootEnabled") is int enabled)
                return enabled == 1;
        }
        catch
        {
            // Non-fatal.
        }

        // powercfg /a also reports "Hybrid Boot" support; fall back to the power scheme hint.
        return false;
    }

    /// <summary>GUID of the active power scheme from <c>powercfg /getactivescheme</c>.</summary>
    public static string? GetActivePowerSchemeGuid()
    {
        var output = RunPowerCfg("/getactivescheme").FirstOrDefault();
        if (output is null)
            return null;

        // e.g. Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
        var start = output.IndexOf(':');
        if (start < 0)
            return null;
        var guid = output[(start + 1)..].Trim();
        return Guid.TryParse(guid, out var g) ? g.ToString("D") : null;
    }

    /// <summary>Human-readable name of the active power scheme.</summary>
    public static string? GetActivePowerSchemeName()
    {
        var output = RunPowerCfg("/getactivescheme").FirstOrDefault();
        if (output is null)
            return null;

        var open = output.IndexOf('(');
        var close = output.IndexOf(')');
        if (open >= 0 && close > open)
            return output[(open + 1)..close].Trim();

        return null;
    }

    /// <summary>Guids of every power scheme present on the machine.</summary>
    public static List<string> GetAvailablePowerSchemeGuids()
        => GetPowerSchemes().Select(s => s.Guid).Where(g => !string.IsNullOrEmpty(g)).ToList();

    public static List<(string Guid, string Name)> GetPowerSchemes()
    {
        var schemes = new List<(string, string)>();

        foreach (var line in RunPowerCfg("/list"))
        {
            // e.g. 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
            var open = line.IndexOf('(');
            var close = line.IndexOf(')');
            if (open < 0 || close <= open)
                continue;

            var guidPart = line[..open].Trim();
            var name = line[(open + 1)..close].Trim();

            if (Guid.TryParse(guidPart, out var guid))
                schemes.Add((guid.ToString("D"), name));
        }

        return schemes;
    }

    /// <summary>
    /// Switches the active power plan. Uses <c>powercfg /setactive</c> rather than
    /// <c>PowerSetActiveScheme</c> because it is the documented CLI surface, requires no
    /// extra P/Invoke, and works identically from a service.
    /// </summary>
    public static bool SetActivePowerScheme(string guid)
    {
        if (!Guid.TryParse(guid, out _))
            return false;

        var (exit, _) = RunPowerCfgCapture($"/setactive {guid}");
        return exit == 0;
    }

    /// <summary>Adapters that Windows reports as armed for Wake-on-LAN.</summary>
    public static List<(string Name, string Mac)> GetWakeArmedAdapters()
    {
        var result = new List<(string, string)>();
        foreach (var line in RunPowerCfg("/devicequery", "wake_armed"))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            result.Add((line.Trim(), ""));
        }
        return result;
    }

    /// <summary>Enables Wake-on-LAN for a device. <c>powercfg /deviceenablewake "&lt;name&gt;"</c>.</summary>
    public static bool EnableWakeForDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;

        var (exit, _) = RunPowerCfgCapture($"/deviceenablewake \"{deviceName}\"");
        return exit == 0;
    }

    /// <summary>The sleep states this machine exposes, e.g. "Standby (S3)" or "Standby (S0 Low Power Idle)".</summary>
    public static List<string> GetAvailableSleepStates() => RunPowerCfg("/availablesleepstates")
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => l.Trim())
        .ToList();

    /// <summary>
    /// Diagnostic description of what last woke this machine, from <c>powercfg /lastwake</c>.
    /// Returns <c>null</c> when the information is unavailable.
    /// </summary>
    public static string? GetLastBootState()
    {
        var first = RunPowerCfg("/lastwake").FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    // -----------------------------------------------------------------------------

    private static IEnumerable<string> RunPowerCfg(string prefix, params string[] args)
        => RunPowerCfgCapture(Format(prefix, args)).Output;

    private static (int ExitCode, List<string> Output) RunPowerCfgCapture(string arguments)
    {
        var output = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, output);

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            output.AddRange(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim('\r')));
            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                _log?.LogDebug("powercfg {Args} returned {Exit}: {Err}", arguments, proc.ExitCode, stderr.Trim());

            return (proc.ExitCode, output.Where(l => !string.IsNullOrWhiteSpace(l)).ToList());
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "powercfg {Args} could not be started", arguments);
            return (-1, output);
        }
    }

    private static string Format(string prefix, string[] args)
        => args.Length == 0 ? prefix : prefix + " " + string.Join(' ', args);
}

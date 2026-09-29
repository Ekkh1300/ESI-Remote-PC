using Esi.Protocol;
using Esi.Platform.Power;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Telemetry;

/// <summary>A single temperature reading, with provenance so the UI never invents a number.</summary>
public readonly record struct TemperatureReading(double? Celsius, string Source, string? Note = null);

/// <summary>
/// CPU temperature, with an honest provider chain.
/// <para>
/// <b>Why this is not simply "read the CPU temp":</b> there is no Win32 API for CPU die
/// temperature. The candidates each have a real limitation, and presenting any of them
/// without saying which would be misleading:
/// </para>
/// <list type="bullet">
///   <item><description><c>MSAcpi_ThermalZoneTemperature</c> in <c>root\wmi</c> comes from
///   the ACPI firmware, not the CPU. Many desktops expose no thermal zone at all, and on
///   laptops the value is frequently a board or ambient figure rather than the die. It also
///   reports <c>0xFFFFFFFF</c> or <c>0</c> when unsupported, so those are filtered out.</description></item>
///   <item><description>NVML gives a true GPU die temperature on NVIDIA hardware, and is
///   authoritative when present.</description></item>
///   <item><description>Intel IGCL / AMD ADLX are vendor SDKs that would need their own
///   native dependencies; the abstraction is here so they can be added without touching
///   the metrics pipeline.</description></item>
/// </list>
/// <para>
/// When nothing trustworthy is available the reading is <c>null</c> and a note explains
/// why. The API contract is explicit that a missing value is always preferable to a
/// fabricated one.
/// </para>
/// </summary>
public sealed class TemperatureProvider
{
    private readonly ILogger<TemperatureProvider> _log;
    private readonly NvmlProvider _nvml;

    public TemperatureProvider(ILogger<TemperatureProvider> log, NvmlProvider nvml)
    {
        _log = log;
        _nvml = nvml;
    }

    private DateTime _lastAcpiReadUtc = DateTime.MinValue;
    private double? _lastAcpiValue;
    private string _lastAcpiNote = "Not read yet.";

    /// <summary>Interval at which the (slow, cached-by-Windows) ACPI class is consulted.</summary>
    private static readonly TimeSpan AcpiCacheInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reads the ACPI thermal zones. <c>CurrentTemperature</c> is in tenths of a Kelvin,
    /// so degrees Celsius is <c>K / 10 - 273.15</c>.
    /// </summary>
    private (double? Value, string Note) ReadAcpiThermalZone()
    {
        if (DateTime.UtcNow - _lastAcpiReadUtc < AcpiCacheInterval)
            return (_lastAcpiValue, _lastAcpiNote);

        _lastAcpiReadUtc = DateTime.UtcNow;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"\\.\root\wmi"),
                new System.Management.ObjectQuery("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));

            using var results = searcher.Get();

            var best = double.NaN;
            var sawZone = false;
            var sawValid = false;

            foreach (System.Management.ManagementBaseObject row in results)
            {
                using (row)
                {
                    sawZone = true;
                    var raw = row["CurrentTemperature"];
                    if (raw is null)
                        continue;

                    // 0xFFFFFFFF (4294967295) is the documented "not supported" sentinel,
                    // and 0 means the firmware has no reading.
                    if (!ulong.TryParse(raw.ToString(), out var ticks))
                        continue;
                    if (ticks == 0xFFFFFFFF || ticks == 0)
                        continue;

                    var celsius = (ticks / 10.0) - 273.15;
                    if (celsius is < -50 or > 150)
                        continue;

                    sawValid = true;
                    if (double.IsNaN(best) || celsius > best)
                        best = celsius;
                }
            }

            if (sawValid)
            {
                _lastAcpiValue = best;
                _lastAcpiNote = "ACPI thermal zone (firmware-reported; this is a board or ambient figure, not the CPU die).";
            }
            else if (sawZone)
            {
                _lastAcpiValue = null;
                _lastAcpiNote = "The ACPI thermal zone exists but reports no usable value on this machine.";
            }
            else
            {
                _lastAcpiValue = null;
                _lastAcpiNote = "No ACPI thermal zone is exposed by this machine's firmware.";
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "MSAcpi_ThermalZoneTemperature is unavailable");
            _lastAcpiValue = null;
            _lastAcpiNote = "The ACPI thermal zone class could not be read (typically needs an elevated, 64-bit WMI client).";
        }

        return (_lastAcpiValue, _lastAcpiNote);
    }

    /// <summary>CPU temperature and how it was obtained.</summary>
    public TemperatureReading CpuTemperature()
    {
        var (value, note) = ReadAcpiThermalZone();
        return new TemperatureReading(value, value is null ? "unavailable" : "acpi", note);
    }

    /// <summary>GPU die temperature, authoritative when NVML is present.</summary>
    public TemperatureReading GpuTemperature()
    {
        var devices = _nvml.GetDevices();
        if (devices.Length == 0)
        {
            return new TemperatureReading(
                null,
                "unavailable",
                _nvml.UnavailableReason ?? "No NVIDIA GPU is present, so NVML reports no temperature.");
        }

        double? hottest = null;
        foreach (var device in devices)
        {
            if (device.TemperatureC is > 0 and < 200)
            {
                hottest = hottest is null ? device.TemperatureC : Math.Max(hottest.Value, device.TemperatureC);
            }
        }

        return hottest is null
            ? new TemperatureReading(null, "nvml", "NVML is present but returned no valid temperature.")
            : new TemperatureReading(hottest, "nvml", "NVML GPU die temperature.");
    }

    /// <summary>GPU fan speed as a percentage, when NVML can report it.</summary>
    public int? GpuFanPercent()
    {
        var devices = _nvml.GetDevices();
        int? hottest = null;

        foreach (var device in devices)
        {
            if (device.FanPercent is > 0)
                hottest = hottest is null ? (int)device.FanPercent : Math.Max(hottest.Value, (int)device.FanPercent);
        }

        return hottest;
    }

    /// <summary>CPU fan speed in RPM. Not exposed by any documented Windows API.</summary>
    public (int? Rpm, string? Note) CpuFanRpm()
    {
        // Windows exposes no supported CPU fan RPM interface. WMI's Win32_Fan does not
        // exist, and the undocumented MSAcpi_ThermalZoneTemperature fan classes are
        // vendor-specific and frequently absent. Reporting null with an explanation is the
        // correct behaviour rather than inventing a number.
        return (null, "Windows exposes no supported CPU fan RPM interface; install a hardware monitor driver to populate this.");
    }

    /// <summary>Whether a trustworthy GPU temperature is available at all.</summary>
    public bool GpuTemperatureAvailable => _nvml.GetDevices().Any(d => d.TemperatureC is > 0);
}

using System.Management;
using Esi.Platform.Native;
using Esi.Platform.Power;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Esi.Platform.Machine;

/// <summary>
/// Static hardware and OS inventory. Everything here is cheap or cached, so it is called
/// on demand from the <c>/system/info</c> endpoint rather than from the metrics loop.
/// </summary>
public sealed class SystemInfoProvider
{
    private readonly ILogger<SystemInfoProvider> _log;
    private readonly WmiClient _wmi;

    public SystemInfoProvider(WmiClient wmi, ILogger<SystemInfoProvider> log)
    {
        _wmi = wmi;
        _log = log;
    }

    // -----------------------------------------------------------------------------
    // Operating system
    // -----------------------------------------------------------------------------

    public OsInfo GetOsInfo()
    {
        var os = new OsInfo
        {
            LastBoot = (DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(KERNEL32.GetTickCount64())).ToUnixTimeSeconds(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        };

        // The registry is the documented source for the *real* display version.
        // ProductName is deliberately not trusted on its own: on Windows 11 it still
        // reads "Windows 10 ..." on many builds, so it is only used as a fallback.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                os.Caption = key.GetValue("ProductName") as string ?? Environment.OSVersion.VersionString;
                os.DisplayVersion = key.GetValue("DisplayVersion") as string;
                os.EditionId = key.GetValue("EditionID") as string;
                os.InstallationType = key.GetValue("InstallationType") as string;
                os.Build = key.GetValue("CurrentBuild") is int b ? b : Environment.OSVersion.Version.Build;
                os.Revision = key.GetValue("UBR") is int r ? r : Environment.OSVersion.Version.Revision;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read the Windows version registry keys");
        }

        if (string.IsNullOrWhiteSpace(os.Caption))
            os.Caption = Environment.OSVersion.VersionString;

        if (os.Build <= 0)
        {
            os.Build = Environment.OSVersion.Version.Build;
            os.Revision = Environment.OSVersion.Version.Revision;
        }

        // Registry InstallDate is a Unix timestamp (seconds since 1970) on modern Windows.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("InstallDate") is long unix)
            {
                os.InstallDate = DateTimeOffset.FromUnixTimeSeconds(unix).ToUnixTimeSeconds();
            }
        }
        catch
        {
            // Non-fatal: install date is informational only.
        }

        os.Version = $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}";
        os.UptimeSeconds = (long)TimeSpan.FromMilliseconds(KERNEL32.GetTickCount64()).TotalSeconds;
        os.Locale = System.Globalization.CultureInfo.CurrentCulture.Name;

        return os;
    }

    // -----------------------------------------------------------------------------
    // Computer
    // -----------------------------------------------------------------------------

    public (string? Manufacturer, string? Model) GetComputerIdentity()
    {
        var rows = _wmi.Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem", WmiClient.CimV2, o => (
            WmiClient.GetString(o, "Manufacturer"),
            WmiClient.GetString(o, "Model")));

        return rows.Count > 0 ? rows[0] : (null, null);
    }

    public BiosInfo GetBiosInfo()
    {
        var rows = _wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", WmiClient.CimV2, o => new BiosInfo
        {
            Manufacturer = WmiClient.GetString(o, "Manufacturer"),
            Version = WmiClient.GetString(o, "SMBIOSBIOSVersion"),
            ReleaseDate = WmiClient.GetDate(o, "ReleaseDate")?.ToUnixTimeSeconds(),
        });

        return rows.Count > 0 ? rows[0] : new BiosInfo();
    }

    public SecurityInfo GetSecurityInfo()
    {
        var info = new SecurityInfo();

        // UAC: EnableLUA = 1 means User Account Control is on.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (key?.GetValue("EnableLUA") is int enableLua)
                info.UacEnabled = enableLua == 1;
        }
        catch
        {
            // Non-fatal.
        }

        // Secure Boot state lives in UEFI variable-backed keys; absent on legacy BIOS.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int sb)
                info.SecureBoot = sb == 1;
        }
        catch
        {
            // Non-fatal: legacy BIOS or restricted.
        }

        // TPM presence: the WMI class exists in root\cimv2 on 8.0+ and only on machines with a TPM.
        try
        {
            info.TpmPresent = _wmi.Query("SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'TPM'", WmiClient.CimV2,
                _ => true).Count > 0;
        }
        catch
        {
            // Non-fatal.
        }

        try
        {
            var status = _wmi.QueryScalar("SELECT Status FROM root\\SecurityCenter2 WHERE productClass='AntiVirus'",
                @"\\.\root\SecurityCenter2", o => WmiClient.GetString(o, "productState"));
            info.DefenderEnabled = status is not null &&
                (status.StartsWith("397568", StringComparison.Ordinal) ||  // enabled + up to date
                 status.StartsWith("397584", StringComparison.Ordinal));   // enabled, may need update
        }
        catch
        {
            // Non-fatal: SecurityCenter2 absent on some SKUs.
        }

        try
        {
            var rows = _wmi.Query("SELECT ProtectionStatus FROM Win32_EncryptableVolume", WmiClient.CimV2,
                o => WmiClient.GetValue<uint>(o, "ProtectionStatus"));
            info.BitLockerStatus = rows.Count switch
            {
                0 => "Not supported or no volumes",
                1 when rows[0] == 0 => "Protection Off",
                1 => "Protection On",
                _ => "Partially protected",
            };
        }
        catch
        {
            info.BitLockerStatus = null;
        }

        return info;
    }

    // -----------------------------------------------------------------------------
    // CPU
    // -----------------------------------------------------------------------------

    public CpuInfo GetCpuInfo()
    {
        var rows = _wmi.Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, Manufacturer, Architecture FROM Win32_Processor",
            WmiClient.CimV2,
            o => new CpuInfo
            {
                Model = WmiClient.GetString(o, "Name"),
                Cores = WmiClient.GetValue<int>(o, "NumberOfCores") ?? 0,
                Threads = WmiClient.GetValue<int>(o, "NumberOfLogicalProcessors") ?? 0,
                MaxClockMhz = WmiClient.GetValue<int>(o, "MaxClockSpeed") ?? 0,
                Socket = WmiClient.GetString(o, "SocketDesignation"),
                Architecture = WmiClient.GetString(o, "Architecture"),
                L2CacheBytes = (ulong)(WmiClient.GetValue<uint>(o, "L2CacheSize") ?? 0) * 1024,
                L3CacheBytes = (ulong)(WmiClient.GetValue<uint>(o, "L3CacheSize") ?? 0) * 1024,
            });

        if (rows.Count == 0)
        {
            return new CpuInfo
            {
                Model = "Unknown processor",
                Cores = System.Environment.ProcessorCount,
                Threads = System.Environment.ProcessorCount,
                MaxClockMhz = 0,
            };
        }

        // Multi-socket machines report one row per socket; sum the cores and keep the
        // highest clock, then report the socket count.
        return new CpuInfo
        {
            Model = rows[0].Model,
            Cores = rows.Sum(r => r.Cores),
            Threads = rows.Sum(r => r.Threads),
            MaxClockMhz = rows.Max(r => r.MaxClockMhz),
            BaseClockMhz = rows.Max(r => r.MaxClockMhz),
            Socket = rows.Count > 1 ? $"{rows.Count} sockets" : rows[0].Socket,
            Architecture = rows[0].Architecture,
        };
    }

    public string? GetCpuModelShort()
    {
        var cpu = GetCpuInfo();
        return string.IsNullOrWhiteSpace(cpu.Model) ? null : cpu.Model;
    }

    // -----------------------------------------------------------------------------
    // GPU
    // -----------------------------------------------------------------------------

    public List<GpuInfo> GetGpuInfo()
    {
        var rows = _wmi.Query(
            "SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, Status, PNPDeviceID",
            WmiClient.CimV2,
            o => new
            {
                Name = WmiClient.GetString(o, "Name"),
                Vendor = WmiClient.GetString(o, "AdapterCompatibility"),
                // AdapterRAM is a uint32 and therefore caps at 4 GiB / wrong for modern cards.
                // It is only a hint; the authoritative VRAM figure comes from NVML.
                AdapterRam = WmiClient.GetUInt64(o, "AdapterRAM"),
                Driver = WmiClient.GetString(o, "DriverVersion"),
                PnpId = WmiClient.GetString(o, "PNPDeviceID"),
                Width = WmiClient.GetValue<int>(o, "CurrentHorizontalResolution") ?? 0,
                Height = WmiClient.GetValue<int>(o, "CurrentVerticalResolution") ?? 0,
                Status = WmiClient.GetString(o, "Status") ?? "",
            });

        var list = new List<GpuInfo>();
        var index = 0;
        foreach (var r in rows)
        {
            // Microsoft Basic Display Adapter is the fallback driver, not a real GPU.
            if (string.Equals(r.Name, "Microsoft Basic Display Adapter", StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add(new GpuInfo
            {
                Index = index++,
                Name = r.Name ?? "Unknown GPU",
                Vendor = GuessVendor(r.Name, r.Vendor, r.PnpId),
                VramBytes = r.AdapterRam is > 0 and < uint.MaxValue ? (long)r.AdapterRam.Value : null,
                DriverVersion = r.Driver,
                IsPrimary = index == 1 || (r.Width > 0 && r.Height > 0),
            });
        }

        return list;
    }

    public static string GuessVendor(string? name, string? adapterCompat, string? pnpId)
    {
        var haystack = $"{name} {adapterCompat} {pnpId}".ToLowerInvariant();
        if (haystack.Contains("nvidia") || haystack.Contains("geforce") || haystack.Contains("rtx") || haystack.Contains("quadro") || haystack.Contains("ven_10de"))
            return "nvidia";
        if (haystack.Contains("amd") || haystack.Contains("radeon") || haystack.Contains("ven_1002") || haystack.Contains("ven_1022"))
            return "amd";
        if (haystack.Contains("intel") || haystack.Contains("iris") || haystack.Contains("uhd graphics") || haystack.Contains("arc ") || haystack.Contains("ven_8086"))
            return "intel";
        return "unknown";
    }

    // -----------------------------------------------------------------------------
    // Memory
    // -----------------------------------------------------------------------------

    public MemoryInfo GetMemoryInfo()
    {
        var modules = _wmi.Query(
            "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, SMBIOSMemoryType, DeviceLocator, PartNumber FROM Win32_PhysicalMemory",
            WmiClient.CimV2,
            o => new MemoryModuleDto
            {
                SizeBytes = (long)(WmiClient.GetUInt64(o, "Capacity") ?? 0),
                SpeedMhz = WmiClient.GetValue<int>(o, "ConfiguredClockSpeed") ?? WmiClient.GetValue<int>(o, "Speed"),
                Type = MapMemoryType(WmiClient.GetValue<ushort>(o, "SMBIOSMemoryType")),
                Manufacturer = WmiClient.GetString(o, "Manufacturer"),
                Slot = WmiClient.GetString(o, "DeviceLocator"),
            });

        // Total physical memory is most reliable from the API, not from summing DIMMs
        // (the OS reserves some, and WMI occasionally under-reports on very large boards).
        var statusEx = MemoryStatusExFactory.Create();
        KERNEL32.GlobalMemoryStatusEx(ref statusEx);

        return new MemoryInfo
        {
            TotalBytes = (long)statusEx.ullTotalPhys,
            Slots = modules.Count > 0 ? modules.Count : null,
            Modules = modules.Where(m => m.SizeBytes > 0).ToList(),
        };
    }

    private static string? MapMemoryType(ushort? smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        35 => "LPDDR4",
        36 => "LPDDR5",
        _ => null,
    };

    // -----------------------------------------------------------------------------
    // Storage
    // -----------------------------------------------------------------------------

    public List<StorageVolume> GetStorageVolumes()
    {
        var drives = _wmi.Query(
            "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType, Description FROM Win32_LogicalDisk",
            WmiClient.CimV2,
            o => new
            {
                DeviceId = WmiClient.GetString(o, "DeviceID") ?? "",
                Label = WmiClient.GetString(o, "VolumeName"),
                FileSystem = WmiClient.GetString(o, "FileSystem"),
                Size = WmiClient.GetUInt64(o, "Size") ?? 0,
                Free = WmiClient.GetUInt64(o, "FreeSpace") ?? 0,
                DriveType = WmiClient.GetValue<ushort>(o, "DriveType") ?? 0,
            });

        var volumes = new List<StorageVolume>();
        var systemDrive = System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        foreach (var d in drives)
        {
            if (d.Size == 0)
                continue; // no media (empty card reader slot) or a disconnected volume

            // Only local fixed/removable volumes are useful for the dashboard. Network
            // mapped drives are skipped because they are frequently slow and would
            // distort the "disk usage" story.
            if (d.DriveType is not (2 or 3))
                continue;

            var used = (long)(d.Size - d.Free);
            volumes.Add(new StorageVolume
            {
                DeviceId = d.DeviceId,
                Label = d.Label,
                FileSystem = d.FileSystem,
                TotalBytes = (long)d.Size,
                FreeBytes = (long)d.Free,
                UsedPercent = d.Size > 0 ? Math.Round(used * 100.0 / d.Size, 2) : 0,
                DriveType = d.DriveType == 2 ? "fixed" : "removable",
                IsSystem = string.Equals(d.DeviceId, systemDrive, StringComparison.OrdinalIgnoreCase),
            });
        }

        return volumes;
    }

    // -----------------------------------------------------------------------------
    // Power capabilities
    // -----------------------------------------------------------------------------

    public PowerCapabilities GetPowerCapabilities()
    {
        var caps = new PowerCapabilities();

        caps.SupportsHibernate = PowerStateProbe.SupportsHibernate();
        caps.SupportsSleep = true;
        caps.HiberbootEnabled = PowerStateProbe.IsHiberbootEnabled();
        caps.ActivePowerScheme = PowerStateProbe.GetActivePowerSchemeName();
        caps.LastBootState = PowerStateProbe.GetLastBootState();

        return caps;
    }

    /// <summary>Total RAM, straight from <c>GlobalMemoryStatusEx</c>.</summary>
    public long GetTotalPhysicalMemory()
    {
        var statusEx = MemoryStatusExFactory.Create();
        return KERNEL32.GlobalMemoryStatusEx(ref statusEx) ? (long)statusEx.ullTotalPhys : 0;
    }

    /// <summary>Number of currently running user processes, from <c>GetPerformanceInfo</c>.</summary>
    public int GetProcessCount()
    {
        if (KERNEL32.GetPerformanceInfo(out var info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            return (int)info.ProcessCount;
        return 0;
    }

    public int GetThreadCount()
    {
        if (KERNEL32.GetPerformanceInfo(out var info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            return (int)info.ThreadCount;
        return 0;
    }
}

public enum PowerModeChangeMode
{
    Resume,
    Suspend,
}

public sealed class MemoryStatusExFactory
{
    public static MEMORYSTATUSEX Create() => new()
    {
        dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>(),
    };
}

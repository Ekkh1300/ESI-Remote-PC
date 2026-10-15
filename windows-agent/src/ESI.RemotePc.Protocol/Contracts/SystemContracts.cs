using System.Text.Json.Serialization;

namespace Esi.Protocol;

// ---------------------------------------------------------------------------------
// GET /api/v1/system/info
// ---------------------------------------------------------------------------------

public sealed class SystemInfoResponse
{
    [JsonPropertyName("hostName")] public string HostName { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("os")] public OsInfo Os { get; set; } = new();
    [JsonPropertyName("cpu")] public CpuInfo Cpu { get; set; } = new();
    [JsonPropertyName("gpu")] public List<GpuInfo> Gpu { get; set; } = [];
    [JsonPropertyName("memory")] public MemoryInfo Memory { get; set; } = new();
    [JsonPropertyName("storage")] public List<StorageVolume> Storage { get; set; } = [];
    [JsonPropertyName("bios")] public BiosInfo Bios { get; set; } = new();
    [JsonPropertyName("security")] public SecurityInfo Security { get; set; } = new();
    [JsonPropertyName("power")] public PowerCapabilities Power { get; set; } = new();
    [JsonPropertyName("sensors")] public SensorSummary Sensors { get; set; } = new();
    [JsonPropertyName("agent")] public AgentInfo Agent { get; set; } = new();
}

public sealed class OsInfo
{
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("displayVersion")] public string? DisplayVersion { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("build")] public int Build { get; set; }
    [JsonPropertyName("revision")] public int Revision { get; set; }
    [JsonPropertyName("architecture")] public string Architecture { get; set; } = "";
    [JsonPropertyName("installDate")] public long? InstallDate { get; set; }
    [JsonPropertyName("lastBoot")] public long LastBoot { get; set; }
    [JsonPropertyName("uptimeSeconds")] public long UptimeSeconds { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("editionId")] public string? EditionId { get; set; }
    [JsonPropertyName("installationType")] public string? InstallationType { get; set; }
}

public sealed class CpuInfo
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("cores")] public int Cores { get; set; }
    [JsonPropertyName("threads")] public int Threads { get; set; }
    [JsonPropertyName("maxClockMhz")] public int MaxClockMhz { get; set; }
    [JsonPropertyName("baseClockMhz")] public int BaseClockMhz { get; set; }
    [JsonPropertyName("socket")] public string? Socket { get; set; }
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("l2CacheBytes")] public ulong L2CacheBytes { get; set; }
    [JsonPropertyName("l3CacheBytes")] public ulong L3CacheBytes { get; set; }
}

public sealed class GpuInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = "unknown";
    [JsonPropertyName("vramBytes")] public long? VramBytes { get; set; }
    [JsonPropertyName("driverVersion")] public string? DriverVersion { get; set; }
    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; set; }
}

public sealed class MemoryModuleDto
{
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("speedMhz")] public int? SpeedMhz { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("slot")] public string? Slot { get; set; }
}

public sealed class MemoryInfo
{
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("slots")] public int? Slots { get; set; }
    [JsonPropertyName("modules")] public List<MemoryModuleDto> Modules { get; set; } = [];
}

public sealed class StorageVolume
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("fileSystem")] public string? FileSystem { get; set; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("freeBytes")] public long FreeBytes { get; set; }
    [JsonPropertyName("usedPercent")] public double UsedPercent { get; set; }
    [JsonPropertyName("driveType")] public string DriveType { get; set; } = "fixed";
    [JsonPropertyName("isSystem")] public bool IsSystem { get; set; }
}

public sealed class BiosInfo
{
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("releaseDate")] public long? ReleaseDate { get; set; }
}

public sealed class SecurityInfo
{
    [JsonPropertyName("secureBoot")] public bool? SecureBoot { get; set; }
    [JsonPropertyName("tpmPresent")] public bool? TpmPresent { get; set; }
    [JsonPropertyName("defenderEnabled")] public bool? DefenderEnabled { get; set; }
    [JsonPropertyName("uacEnabled")] public bool? UacEnabled { get; set; }
    [JsonPropertyName("bitLockerStatus")] public string? BitLockerStatus { get; set; }
    [JsonPropertyName("lanOnly")] public bool LanOnly { get; set; } = true;
}

public sealed class PowerCapabilities
{
    [JsonPropertyName("supportsSleep")] public bool SupportsSleep { get; set; } = true;
    [JsonPropertyName("supportsHibernate")] public bool SupportsHibernate { get; set; }
    [JsonPropertyName("lastBootState")] public string? LastBootState { get; set; }
    [JsonPropertyName("activePowerScheme")] public string? ActivePowerScheme { get; set; }
    [JsonPropertyName("hiberbootEnabled")] public bool HiberbootEnabled { get; set; }
}

public sealed class SensorSummary
{
    [JsonPropertyName("cpuTemperatureC")] public double? CpuTemperatureC { get; set; }
    [JsonPropertyName("gpuTemperatureC")] public double? GpuTemperatureC { get; set; }
    [JsonPropertyName("cpuFanRpm")] public int? CpuFanRpm { get; set; }
    [JsonPropertyName("gpuFanRpm")] public int? GpuFanRpm { get; set; }
    [JsonPropertyName("sensorsAvailable")] public List<string> SensorsAvailable { get; set; } = [];
    /// <summary>Human-readable note about which provider each value came from, for the UI.</summary>
    [JsonPropertyName("notes")] public Dictionary<string, string> Notes { get; set; } = new();
}

public sealed class AgentInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
    [JsonPropertyName("uptimeSeconds")] public long UptimeSeconds { get; set; }
    [JsonPropertyName("debugMode")] public bool DebugMode { get; set; }
    [JsonPropertyName("sessionAgentConnected")] public bool SessionAgentConnected { get; set; }
    [JsonPropertyName("sessionAgentState")] public string SessionAgentState { get; set; } = "NotRunning";
}

// ---------------------------------------------------------------------------------
// GET /api/v1/system/metrics
// ---------------------------------------------------------------------------------

public sealed class MetricsResponse
{
    [JsonPropertyName("capturedAt")] public long CapturedAt { get; set; }
    [JsonPropertyName("sampleWindowMs")] public int SampleWindowMs { get; set; } = 1000;
    [JsonPropertyName("cpu")] public CpuMetrics Cpu { get; set; } = new();
    [JsonPropertyName("memory")] public MemoryMetrics Memory { get; set; } = new();
    [JsonPropertyName("gpu")] public List<GpuMetrics> Gpu { get; set; } = [];
    [JsonPropertyName("gpuAggregate")] public GpuAggregate GpuAggregate { get; set; } = new();
    [JsonPropertyName("storage")] public List<StorageMetrics> Storage { get; set; } = [];
    [JsonPropertyName("network")] public NetworkMetrics Network { get; set; } = new();
    [JsonPropertyName("battery")] public BatteryMetrics? Battery { get; set; }
    [JsonPropertyName("load")] public List<LoadPoint> Load { get; set; } = [];
}

public sealed class CpuMetrics
{
    [JsonPropertyName("usagePercent")] public double UsagePercent { get; set; }
    [JsonPropertyName("perCore")] public List<double> PerCore { get; set; } = [];
    [JsonPropertyName("userPercent")] public double UserPercent { get; set; }
    [JsonPropertyName("kernelPercent")] public double KernelPercent { get; set; }
    [JsonPropertyName("temperatureC")] public double? TemperatureC { get; set; }
    [JsonPropertyName("clockMhz")] public double? ClockMhz { get; set; }
    [JsonPropertyName("maxClockMhz")] public int MaxClockMhz { get; set; }
    [JsonPropertyName("processCount")] public int ProcessCount { get; set; }
    [JsonPropertyName("threadCount")] public int ThreadCount { get; set; }
}

public sealed class MemoryMetrics
{
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("availableBytes")] public long AvailableBytes { get; set; }
    [JsonPropertyName("usedPercent")] public double UsedPercent { get; set; }
    [JsonPropertyName("cachedBytes")] public long? CachedBytes { get; set; }
    [JsonPropertyName("committedBytes")] public long? CommittedBytes { get; set; }
    [JsonPropertyName("commitLimitBytes")] public long? CommitLimitBytes { get; set; }
    [JsonPropertyName("swap")] public SwapMetrics Swap { get; set; } = new();
}

public sealed class SwapMetrics
{
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; set; }
}

public sealed class GpuMetrics
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("usagePercent")] public double UsagePercent { get; set; }
    [JsonPropertyName("memoryUsedBytes")] public long? MemoryUsedBytes { get; set; }
    [JsonPropertyName("memoryTotalBytes")] public long? MemoryTotalBytes { get; set; }
    [JsonPropertyName("temperatureC")] public double? TemperatureC { get; set; }
    [JsonPropertyName("fanRpm")] public int? FanRpm { get; set; }
    [JsonPropertyName("powerWatts")] public double? PowerWatts { get; set; }
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = "unknown";
    /// <summary>Which provider produced this row: nvml, igcl, adlx, pdh or unavailable.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "unavailable";
}

public sealed class GpuAggregate
{
    [JsonPropertyName("usagePercent")] public double UsagePercent { get; set; }
    [JsonPropertyName("maxTemperatureC")] public double? MaxTemperatureC { get; set; }
    [JsonPropertyName("dominantIndex")] public int DominantIndex { get; set; }
}

public sealed class StorageMetrics
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("usedPercent")] public double UsedPercent { get; set; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("freeBytes")] public long FreeBytes { get; set; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("readBytesPerSecond")] public long ReadBytesPerSecond { get; set; }
    [JsonPropertyName("writeBytesPerSecond")] public long WriteBytesPerSecond { get; set; }
}

public sealed class NetworkInterfaceMetrics
{
    /// <summary>Stable interface identifier, matching the agent's internal rate-counter key.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "unknown";
    [JsonPropertyName("isUp")] public bool IsUp { get; set; }
    [JsonPropertyName("macAddress")] public string? MacAddress { get; set; }
    [JsonPropertyName("ipv4")] public string? Ipv4 { get; set; }
    [JsonPropertyName("netmask")] public string? Netmask { get; set; }
    [JsonPropertyName("gateway")] public string? Gateway { get; set; }
    [JsonPropertyName("dns")] public List<string> Dns { get; set; } = [];
    [JsonPropertyName("linkSpeedBitsPerSecond")] public long LinkSpeedBitsPerSecond { get; set; }
    [JsonPropertyName("mtu")] public int Mtu { get; set; }
    [JsonPropertyName("downloadBytesPerSecond")] public long DownloadBytesPerSecond { get; set; }
    [JsonPropertyName("uploadBytesPerSecond")] public long UploadBytesPerSecond { get; set; }
    [JsonPropertyName("rxTotalBytes")] public long RxTotalBytes { get; set; }
    [JsonPropertyName("txTotalBytes")] public long TxTotalBytes { get; set; }
    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; set; }
}

public sealed class NetworkMetrics
{
    [JsonPropertyName("interfaces")] public List<NetworkInterfaceMetrics> Interfaces { get; set; } = [];
    [JsonPropertyName("primary")] public NetworkInterfaceMetrics? Primary { get; set; }
    [JsonPropertyName("pingMs")] public double? PingMs { get; set; }
}

public sealed class BatteryMetrics
{
    [JsonPropertyName("isPresent")] public bool IsPresent { get; set; }
    [JsonPropertyName("chargePercent")] public double? ChargePercent { get; set; }
    [JsonPropertyName("isCharging")] public bool? IsCharging { get; set; }
    [JsonPropertyName("remainingMinutes")] public int? RemainingMinutes { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class LoadPoint
{
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("cpu")] public double Cpu { get; set; }
    [JsonPropertyName("gpu")] public double Gpu { get; set; }
    [JsonPropertyName("ram")] public double Ram { get; set; }
}

public sealed class ProcessInfoDto
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("cpuPercent")] public double? CpuPercent { get; set; }
    [JsonPropertyName("workingSetBytes")] public long WorkingSetBytes { get; set; }
    [JsonPropertyName("isElevated")] public bool IsElevated { get; set; }
    [JsonPropertyName("startedAt")] public long? StartedAt { get; set; }
}

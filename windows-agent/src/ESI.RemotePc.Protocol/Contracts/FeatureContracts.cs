using System.Text.Json.Serialization;

namespace Esi.Protocol;

// ---------------------------------------------------------------------------------
// Media
// ---------------------------------------------------------------------------------

public sealed class MediaStateResponse
{
    [JsonPropertyName("volume")] public int Volume { get; set; }
    [JsonPropertyName("isMuted")] public bool IsMuted { get; set; }
    [JsonPropertyName("defaultDevice")] public string? DefaultDevice { get; set; }
    [JsonPropertyName("sessionPeak")] public int SessionPeak { get; set; }
    [JsonPropertyName("availableActions")] public List<string> AvailableActions { get; set; } = [];
    [JsonPropertyName("supported")] public bool Supported { get; set; } = true;
}

public sealed class MediaActionRequest
{
    /// <summary>0-100 for volumeSet; 1-25 for volumeUp/volumeDown.</summary>
    [JsonPropertyName("step")] public int? Step { get; set; }
    [JsonPropertyName("volume")] public int? Volume { get; set; }
}

public sealed class MediaActionResponse
{
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("volume")] public int? Volume { get; set; }
    [JsonPropertyName("isMuted")] public bool? IsMuted { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

// ---------------------------------------------------------------------------------
// Remote control
// ---------------------------------------------------------------------------------

public sealed class RemoteSessionResponse
{
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("sessionAgentState")] public string SessionAgentState { get; set; } = "NotRunning";
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("streaming")] public bool Streaming { get; set; }
    [JsonPropertyName("connectedDeviceId")] public string? ConnectedDeviceId { get; set; }
    [JsonPropertyName("captureSource")] public string CaptureSource { get; set; } = "gdi";
    [JsonPropertyName("maxFps")] public int MaxFps { get; set; } = 30;
    [JsonPropertyName("inputEnabled")] public bool InputEnabled { get; set; }
    [JsonPropertyName("displays")] public List<DisplayInfo> Displays { get; set; } = [];
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];
}

public sealed class DisplayInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; set; }
    [JsonPropertyName("rotation")] public int Rotation { get; set; }
    [JsonPropertyName("refreshRateHz")] public double? RefreshRateHz { get; set; }
    [JsonPropertyName("scalePercent")] public int? ScalePercent { get; set; }
}

public sealed class RemoteInputRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("down")] public bool? Down { get; set; }
    [JsonPropertyName("deltaX")] public int? DeltaX { get; set; }
    [JsonPropertyName("deltaY")] public int? DeltaY { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("mods")] public List<string>? Mods { get; set; }
    [JsonPropertyName("displayId")] public int? DisplayId { get; set; }
}

public sealed class RemoteInputResponse
{
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("delivered")] public int Delivered { get; set; }
    [JsonPropertyName("sessionAgentState")] public string? SessionAgentState { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

// ---------------------------------------------------------------------------------
// Gaming mode
// ---------------------------------------------------------------------------------

public sealed class GamingStateResponse
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("activePowerScheme")] public string? ActivePowerScheme { get; set; }
    [JsonPropertyName("availablePowerSchemes")] public List<string> AvailablePowerSchemes { get; set; } = [];
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("fpsSource")] public string? FpsSource { get; set; }
    [JsonPropertyName("cpuUsagePercent")] public double CpuUsagePercent { get; set; }
    [JsonPropertyName("gpuUsagePercent")] public double GpuUsagePercent { get; set; }
    [JsonPropertyName("ramUsedPercent")] public double RamUsedPercent { get; set; }
    [JsonPropertyName("gpuTemperatureC")] public double? GpuTemperatureC { get; set; }
    [JsonPropertyName("gpuMemoryUsedBytes")] public long? GpuMemoryUsedBytes { get; set; }
    [JsonPropertyName("gpuMemoryTotalBytes")] public long? GpuMemoryTotalBytes { get; set; }
    [JsonPropertyName("pingMs")] public double? PingMs { get; set; }
    [JsonPropertyName("networkDownBytesPerSecond")] public long NetworkDownBytesPerSecond { get; set; }
    [JsonPropertyName("networkUpBytesPerSecond")] public long NetworkUpBytesPerSecond { get; set; }
    [JsonPropertyName("suspendedApps")] public List<string> SuspendedApps { get; set; } = [];
    [JsonPropertyName("launchAppId")] public string? LaunchAppId { get; set; }
    [JsonPropertyName("history")] public List<GamingHistoryPoint> History { get; set; } = [];
    [JsonPropertyName("sessionAgentConnected")] public bool SessionAgentConnected { get; set; }
}

public sealed class GamingHistoryPoint
{
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("cpu")] public double Cpu { get; set; }
    [JsonPropertyName("gpu")] public double Gpu { get; set; }
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("ping")] public double? Ping { get; set; }
}

public sealed class GamingModeRequest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("suspendApps")] public List<string>? SuspendApps { get; set; }
    [JsonPropertyName("launchAppId")] public string? LaunchAppId { get; set; }
}

public sealed class GamingModeResponse
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("previousPowerScheme")] public string? PreviousPowerScheme { get; set; }
    [JsonPropertyName("suspendedApps")] public List<string> SuspendedApps { get; set; } = [];
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
}

public sealed class GamingProfileDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("powerScheme")] public string? PowerScheme { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("builtIn")] public bool BuiltIn { get; set; }
}

public sealed class AppStateRequest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

// ---------------------------------------------------------------------------------
// Notifications
// ---------------------------------------------------------------------------------

public sealed class NotificationDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "system";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("isRead")] public bool IsRead { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("data")] public Dictionary<string, string>? Data { get; set; }
}

public sealed class NotificationListResponse
{
    [JsonPropertyName("notifications")] public List<NotificationDto> Notifications { get; set; } = [];
    [JsonPropertyName("unreadCount")] public int UnreadCount { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
}

public sealed class NotificationRuleDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("severity")] public string Severity { get; set; } = "warning";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("threshold")] public double? Threshold { get; set; }
    [JsonPropertyName("sustainSeconds")] public int SustainSeconds { get; set; }
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; } = 300;
    [JsonPropertyName("builtIn")] public bool BuiltIn { get; set; }
    [JsonPropertyName("condition")] public string Condition { get; set; } = "";
}

public sealed class NotificationRulesResponse
{
    [JsonPropertyName("rules")] public List<NotificationRuleDto> Rules { get; set; } = [];
}

// ---------------------------------------------------------------------------------
// Settings / logs / errors
// ---------------------------------------------------------------------------------

public sealed class SettingsFileRootDto
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
}

public sealed class SecuritySettingsDto
{
    [JsonPropertyName("localNetworkOnly")] public bool LocalNetworkOnly { get; set; } = true;
    [JsonPropertyName("requireSignedRequests")] public bool RequireSignedRequests { get; set; } = true;
    [JsonPropertyName("allowedSubnets")] public List<string> AllowedSubnets { get; set; } = ["192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12"];
    [JsonPropertyName("clockSkewSeconds")] public int ClockSkewSeconds { get; set; } = ProtocolConstants.DefaultClockSkewSeconds;
    [JsonPropertyName("maxPairedDevices")] public int MaxPairedDevices { get; set; } = 8;
    [JsonPropertyName("rateLimitPerMinute")] public int RateLimitPerMinute { get; set; } = 240;

    /// <summary>
    /// Per-minute budget for the unauthenticated surface: handshake, health, pairing.
    /// <para>
    /// Separately configurable because it is the only budget an unauthenticated caller can
    /// touch, and because the right value depends on the environment - a developer running
    /// the agent on a laptop and a phone on the same wifi is a very different traffic shape
    /// from a machine being scanned.
    /// </para>
    /// </summary>
    [JsonPropertyName("unauthenticatedPerMinute")] public int UnauthenticatedPerMinute { get; set; } = 30;

    [JsonPropertyName("unauthenticatedBurst")] public int UnauthenticatedBurst { get; set; } = 10;

    /// <summary>Per-hour budget for pairing requests from one address.</summary>
    [JsonPropertyName("pairingPerHour")] public int PairingPerHour { get; set; } = 5;
    [JsonPropertyName("pairingWindowSeconds")] public int PairingWindowSeconds { get; set; } = 120;
    [JsonPropertyName("sessionAgentRequiredForLock")] public bool SessionAgentRequiredForLock { get; set; } = true;
    [JsonPropertyName("tlsEnabled")] public bool TlsEnabled { get; set; } = true;
}

public sealed class MetricsSettingsDto
{
    [JsonPropertyName("sampleIntervalMs")] public int SampleIntervalMs { get; set; } = 1000;
    [JsonPropertyName("historyLimit")] public int HistoryLimit { get; set; } = 300;
    [JsonPropertyName("collectGpu")] public bool CollectGpu { get; set; } = true;
    [JsonPropertyName("collectTemperatures")] public bool CollectTemperatures { get; set; } = true;
}

public sealed class FileSettingsDto
{
    [JsonPropertyName("allowedRoots")] public List<SettingsFileRootDto> AllowedRoots { get; set; } = [];
    [JsonPropertyName("allowDelete")] public bool AllowDelete { get; set; } = true;
    [JsonPropertyName("allowUpload")] public bool AllowUpload { get; set; } = true;
    [JsonPropertyName("maxUploadBytes")] public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

public sealed class NotificationSettingsDto
{
    [JsonPropertyName("rules")] public List<NotificationRuleDto> Rules { get; set; } = [];
    [JsonPropertyName("retentionDays")] public int RetentionDays { get; set; } = 14;
    [JsonPropertyName("toastEnabled")] public bool ToastEnabled { get; set; } = true;
}

public sealed class GamingSettingsDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "performance";
    [JsonPropertyName("suspendApps")] public List<string> SuspendApps { get; set; } = [];
    [JsonPropertyName("launchAppId")] public string? LaunchAppId { get; set; }
}

public sealed class DebugSettingsDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("verboseLogging")] public bool VerboseLogging { get; set; }
    [JsonPropertyName("allowLoopbackOnly")] public bool AllowLoopbackOnly { get; set; }
    [JsonPropertyName("logRetentionDays")] public int LogRetentionDays { get; set; } = 14;
}

public sealed class SettingsDto
{
    [JsonPropertyName("port")] public int Port { get; set; } = ProtocolConstants.DefaultHttpsPort;
    [JsonPropertyName("bindAddress")] public string? BindAddress { get; set; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
    [JsonPropertyName("discoveryEnabled")] public bool DiscoveryEnabled { get; set; } = true;
    [JsonPropertyName("discoveryPort")] public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;
    [JsonPropertyName("mdnsEnabled")] public bool MdnsEnabled { get; set; } = true;
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = true;
    [JsonPropertyName("wakeOnLanEnabled")] public bool WakeOnLanEnabled { get; set; } = true;
    [JsonPropertyName("security")] public SecuritySettingsDto Security { get; set; } = new();
    [JsonPropertyName("metrics")] public MetricsSettingsDto Metrics { get; set; } = new();
    [JsonPropertyName("files")] public FileSettingsDto Files { get; set; } = new();
    [JsonPropertyName("apps")] public List<AppEntryDto> Apps { get; set; } = [];
    [JsonPropertyName("gaming")] public GamingSettingsDto Gaming { get; set; } = new();
    [JsonPropertyName("notifications")] public NotificationSettingsDto Notifications { get; set; } = new();
    [JsonPropertyName("debug")] public DebugSettingsDto Debug { get; set; } = new();
}

public sealed class SettingsUpdateResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("restartRequired")] public bool RestartRequired { get; set; }
    [JsonPropertyName("appliedAt")] public long AppliedAt { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
}

public sealed class LogLineDto
{
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("level")] public string Level { get; set; } = "Information";
    [JsonPropertyName("logger")] public string? Logger { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("exception")] public string? Exception { get; set; }
    [JsonPropertyName("eventId")] public int? EventId { get; set; }
}

public sealed class LogTailResponse
{
    [JsonPropertyName("lines")] public List<LogLineDto> Lines { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("logDirectory")] public string? LogDirectory { get; set; }
}

public sealed class AuditEntryDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
    [JsonPropertyName("remoteAddress")] public string? RemoteAddress { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    /// <summary>Chained hash: <c>SHA256(prevChainHash | canonicalAuditLine)</c>. Tamper evident.</summary>
    [JsonPropertyName("chainHash")] public string? ChainHash { get; set; }
}

public sealed class AuditListResponse
{
    [JsonPropertyName("entries")] public List<AuditEntryDto> Entries { get; set; } = [];
    [JsonPropertyName("chainValid")] public bool ChainValid { get; set; } = true;
}

public sealed class HealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("uptimeSeconds")] public long UptimeSeconds { get; set; }
    [JsonPropertyName("serverTime")] public long ServerTime { get; set; }
    [JsonPropertyName("serviceAccount")] public string? ServiceAccount { get; set; }
    [JsonPropertyName("isElevated")] public bool IsElevated { get; set; }
    [JsonPropertyName("sessionId")] public uint SessionId { get; set; }
    [JsonPropertyName("debugMode")] public bool DebugMode { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")] public ErrorBody Error { get; set; } = new();
}

public sealed class ErrorBody
{
    [JsonPropertyName("code")] public string Code { get; set; } = "esi_internal";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("details")] public Dictionary<string, string>? Details { get; set; }
}

public static class ErrorCodes
{
    public const string BadRequest = "esi_bad_request";
    public const string Unauthorized = "esi_unauthorized";
    public const string Signature = "esi_signature";
    public const string Replay = "esi_replay";
    public const string Timestamp = "esi_timestamp";
    public const string KeyId = "esi_keyid";
    public const string ForbiddenNetwork = "esi_forbidden_network";
    public const string ForbiddenScope = "esi_forbidden_scope";
    public const string NotFound = "esi_not_found";
    public const string RateLimited = "esi_rate_limited";
    public const string PathNotAllowed = "esi_path_not_allowed";
    public const string Unsupported = "esi_unsupported";
    public const string Internal = "esi_internal";
    public const string Conflict = "esi_conflict";
    public const string UploadTooLarge = "esi_upload_too_large";
    public const string SessionAgentUnavailable = "esi_session_agent_unavailable";
}

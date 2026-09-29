using System.Text.Json.Serialization;

namespace Esi.Protocol;

// ---------------------------------------------------------------------------------
// Power
// ---------------------------------------------------------------------------------

public sealed class PowerStatusResponse
{
    [JsonPropertyName("state")] public string State { get; set; } = "unknown";
    [JsonPropertyName("isLocked")] public bool IsLocked { get; set; }
    [JsonPropertyName("isSleeping")] public bool IsSleeping { get; set; }
    [JsonPropertyName("isHibernating")] public bool IsHibernating { get; set; }
    [JsonPropertyName("activeSessionId")] public uint ActiveSessionId { get; set; }
    [JsonPropertyName("activeUser")] public string? ActiveUser { get; set; }
    [JsonPropertyName("loggedIn")] public bool LoggedIn { get; set; }
    [JsonPropertyName("supportsSleep")] public bool SupportsSleep { get; set; } = true;
    [JsonPropertyName("supportsHibernate")] public bool SupportsHibernate { get; set; }
    [JsonPropertyName("lastBoot")] public long LastBoot { get; set; }
    [JsonPropertyName("lastStateChange")] public long LastStateChange { get; set; }
    [JsonPropertyName("pendingPowerAction")] public string? PendingPowerAction { get; set; }
}

public sealed class PowerActionRequest
{
    /// <summary>Close applications without asking. Off by default: never lose the user's work silently.</summary>
    [JsonPropertyName("force")] public bool Force { get; set; }

    /// <summary>Seconds to show the shutdown dialog, giving the user a chance to cancel.</summary>
    [JsonPropertyName("delaySeconds")] public int DelaySeconds { get; set; } = 5;

    /// <summary>Optional text shown in the shutdown dialog.</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class PowerActionResponse
{
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("effectiveMode")] public string? EffectiveMode { get; set; }
    [JsonPropertyName("scheduledAt")] public long? ScheduledAt { get; set; }
    [JsonPropertyName("warning")] public string? Warning { get; set; }
    [JsonPropertyName("requiresInteractiveSession")] public bool RequiresInteractiveSession { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class WakeRequest
{
    [JsonPropertyName("macAddress")] public string MacAddress { get; set; } = "";
    [JsonPropertyName("broadcastAddresses")] public List<string>? BroadcastAddresses { get; set; }
    [JsonPropertyName("ports")] public List<int>? Ports { get; set; }
    [JsonPropertyName("repeat")] public int? Repeat { get; set; }
}

public sealed class WakeResponse
{
    [JsonPropertyName("sent")] public int Sent { get; set; }
    [JsonPropertyName("targets")] public List<string> Targets { get; set; } = [];
    [JsonPropertyName("ports")] public List<int> Ports { get; set; } = [];
}

public sealed class WolReadinessResponse
{
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("armedDevices")] public List<WolDevice> ArmedDevices { get; set; } = [];
    [JsonPropertyName("macAddresses")] public List<string> MacAddresses { get; set; } = [];
    [JsonPropertyName("broadcastAddresses")] public List<string> BroadcastAddresses { get; set; } = [];
    [JsonPropertyName("hiberbootEnabled")] public bool HiberbootEnabled { get; set; }
    [JsonPropertyName("issues")] public List<string> Issues { get; set; } = [];
}

public sealed class WolDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("macAddress")] public string MacAddress { get; set; } = "";
    [JsonPropertyName("connectionType")] public string ConnectionType { get; set; } = "";
    [JsonPropertyName("wakeMagicPacket")] public bool WakeMagicPacket { get; set; }
    [JsonPropertyName("wakePattern")] public string? WakePattern { get; set; }
}

// ---------------------------------------------------------------------------------
// App launcher
// ---------------------------------------------------------------------------------

public sealed class AppEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "other";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("iconPath")] public string? IconPath { get; set; }
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = [];
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; set; }
    [JsonPropertyName("runAs")] public string RunAs { get; set; } = "interactiveUser";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("builtIn")] public bool BuiltIn { get; set; }
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
    [JsonPropertyName("suspended")] public bool Suspended { get; set; }
    [JsonPropertyName("lastLaunched")] public long? LastLaunched { get; set; }
    [JsonPropertyName("exists")] public bool Exists { get; set; } = true;
    [JsonPropertyName("processName")] public string? ProcessName { get; set; }
}

public sealed class AppListResponse
{
    [JsonPropertyName("apps")] public List<AppEntryDto> Apps { get; set; } = [];
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
}

public sealed class LaunchAppRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public List<string>? Args { get; set; }
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; set; }
}

public sealed class LaunchAppResponse
{
    [JsonPropertyName("launched")] public bool Launched { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("processId")] public int? ProcessId { get; set; }
    [JsonPropertyName("runAs")] public string RunAs { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class RunningAppDto
{
    [JsonPropertyName("appId")] public string? AppId { get; set; }
    [JsonPropertyName("processId")] public int ProcessId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("startedAt")] public long? StartedAt { get; set; }
    [JsonPropertyName("workingSetBytes")] public long WorkingSetBytes { get; set; }
}

public sealed class RunningAppListResponse
{
    [JsonPropertyName("processes")] public List<RunningAppDto> Processes { get; set; } = [];
}

public sealed class TerminateResponse
{
    [JsonPropertyName("terminated")] public int Terminated { get; set; }
    [JsonPropertyName("forced")] public bool Forced { get; set; }
}

// ---------------------------------------------------------------------------------
// File manager
// ---------------------------------------------------------------------------------

public sealed class FileRootDto
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
    [JsonPropertyName("exists")] public bool Exists { get; set; } = true;
}

public sealed class FileRootListResponse
{
    [JsonPropertyName("roots")] public List<FileRootDto> Roots { get; set; } = [];
    [JsonPropertyName("defaultRoot")] public string? DefaultRoot { get; set; }
    [JsonPropertyName("readOnly")] public bool AllReadOnly { get; set; }
    [JsonPropertyName("allowDelete")] public bool AllowDelete { get; set; } = true;
    [JsonPropertyName("allowUpload")] public bool AllowUpload { get; set; } = true;
    [JsonPropertyName("maxUploadBytes")] public long MaxUploadBytes { get; set; }
}

public sealed class FileEntryDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("lastModified")] public long LastModified { get; set; }
    [JsonPropertyName("created")] public long? Created { get; set; }
    [JsonPropertyName("isHidden")] public bool IsHidden { get; set; }
    [JsonPropertyName("isReadOnly")] public bool IsReadOnly { get; set; }
    [JsonPropertyName("childCount")] public int? ChildCount { get; set; }
    [JsonPropertyName("extension")] public string? Extension { get; set; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("canDelete")] public bool CanDelete { get; set; } = true;
    [JsonPropertyName("canRename")] public bool CanRename { get; set; } = true;
}

public sealed class FileListResponse
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("entries")] public List<FileEntryDto> Entries { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
    [JsonPropertyName("totalEntries")] public int TotalEntries { get; set; }
}

public sealed class FileOperationRequest
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
}

public sealed class FileOperationResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class UploadTicketRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class UploadTicketResponse
{
    [JsonPropertyName("ticketId")] public string TicketId { get; set; } = "";
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("maxBytes")] public long MaxBytes { get; set; }
    [JsonPropertyName("uploadUrl")] public string UploadUrl { get; set; } = "";
}

public sealed class UploadResultResponse
{
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("bytesWritten")] public long BytesWritten { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

public sealed class FileSearchResponse
{
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("entries")] public List<FileEntryDto> Entries { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("scanned")] public int Scanned { get; set; }
}

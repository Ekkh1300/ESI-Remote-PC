namespace Esi.Protocol;

// ---------------------------------------------------------------------------------
// Shared enumerations. The string values are part of the wire contract; the
// JsonStringEnumConverter below makes them appear as those exact strings in JSON.
// ---------------------------------------------------------------------------------

/// <summary>Reachability state of the PC as reported by the phone.</summary>
public enum PcState
{
    Unknown,
    Online,
    Offline,
    Sleeping,
    Hibernating,
    Locked,
}

/// <summary>Power actions the agent can be asked to perform.</summary>
public enum PowerAction
{
    Shutdown,
    Restart,
    Sleep,
    Hibernate,
    Lock,
    SignOut,
}

/// <summary>Media transport actions.</summary>
public enum MediaAction
{
    Play,
    Pause,
    PlayPause,
    Stop,
    Next,
    Previous,
    VolumeUp,
    VolumeDown,
    VolumeSet,
    Mute,
    Unmute,
    ToggleMute,
}

/// <summary>Categories used to group launcher entries and notifications.</summary>
public enum AppCategory
{
    Game,
    Browser,
    Media,
    Communication,
    Productivity,
    Development,
    Streaming,
    System,
    Other,
}

/// <summary>How a launched application should be hosted.</summary>
public enum AppRunContext
{
    /// <summary>Launch on the interactive user's desktop (default, and what users expect).</summary>
    InteractiveUser = 0,

    /// <summary>Launch in session 0 with no visible UI. For headless helpers only.</summary>
    ServiceSession = 1,

    /// <summary>Launch on the interactive desktop with elevation, via the session agent.</summary>
    ElevatedUser = 2,
}

/// <summary>Notification classification, so the app can route/filter them.</summary>
public enum NotificationKind
{
    Power,
    System,
    Alert,
    App,
    File,
    Security,
    Gaming,
    Network,
}

/// <summary>Notification severity.</summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>Permission scopes granted to a paired device.</summary>
[Flags]
public enum DeviceScope
{
    None = 0,
    Read = 1 << 0,
    Files = 1 << 1,
    Control = 1 << 2,
    Admin = 1 << 3,
    All = Read | Files | Control | Admin,
}

/// <summary>Transport medium of a network interface.</summary>
public enum NetworkInterfaceKind
{
    Unknown,
    Ethernet,
    Wifi,
    Loopback,
    Bluetooth,
    Virtual,
    Other,
}

/// <summary>Which provider produced a telemetry value, so the UI can show provenance.</summary>
public enum TelemetrySource
{
    Unavailable = 0,
    Native = 1,
    Pdh = 2,
    Nvml = 3,
    Igcl = 4,
    Adlx = 5,
    Wmi = 6,
}

/// <summary>Status of the per-user session helper (the process that can inject input).</summary>
public enum SessionAgentState
{
    NotRunning = 0,
    Starting = 1,
    Ready = 2,
    NoUserSession = 3,
    Error = 4,
}

/// <summary>Lifecycle of a pairing attempt.</summary>
public enum PairingStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
}

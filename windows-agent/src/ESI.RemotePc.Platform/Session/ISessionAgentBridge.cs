using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Esi.Protocol;

namespace Esi.Platform.Session;

/// <summary>Commands the service can ask the per-user session agent to perform.</summary>
public enum PowerSessionCommand
{
    Lock,
    SignOut,
    Suspend,
    /// <summary>Re-evaluate desktop state (lock detection, active user) after a session change.</summary>
    RefreshState,
}

public enum SessionAgentStateValue
{
    NotRunning,
    Starting,
    Ready,
    NoUserSession,
    Error,
}

/// <summary>
/// A one-way command pipe from the session-0 service to the interactive session agent.
/// <para>
/// The service never opens windows or injects input itself. Everything that needs the
/// interactive desktop — locking, signing out, <c>SendInput</c>, <c>BitBlt</c> screen
/// capture, toast notifications, the pairing approval prompt — is performed by a helper
/// process running as the logged-on user. The two talk over a named pipe:
/// </para>
/// <list type="bullet">
///   <item><description><b>service → agent</b> (this interface): commands and input events.</description></item>
///   <item><description><b>agent → service</b> (<see cref="ISessionAgentStatus"/>): telemetry,
///   captured frames, lock state. The agent connects back to the service's HTTP API on
///   <c>localhost</c> and authenticates with a per-boot token written to ProgramData.</description></item>
/// </list>
/// <para>
/// This split is not a stylistic choice. <c>SendInput</c> is subject to UIPI and is
/// restricted to the caller's session and desktop, and a session-0 service has no
/// interactive desktop to draw on at all.
/// </para>
/// </summary>
public interface ISessionAgentBridge
{
    /// <summary>State of the helper process as last reported.</summary>
    SessionAgentStateValue State { get; }

    /// <summary>Active console session id, or <c>null</c> when nobody is logged in.</summary>
    uint? CurrentSession();

    /// <summary>User name (DOMAIN\user) of the active session, if any.</summary>
    string? ActiveUser { get; }

    /// <summary>Whether the interactive desktop is currently locked, per the session agent.</summary>
    bool IsWorkstationLocked { get; }

    /// <summary>Whether the machine has suspended to S3 (reported after a resume).</summary>
    bool IsSuspended { get; }

    /// <summary>True when the session agent is connected and responsive.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Sends a command to the session agent. Returns <c>false</c> when no agent is
    /// connected, in which case <paramref name="detail"/> explains why.
    /// </summary>
    bool TrySend(PowerSessionCommand command, bool flag, out string? detail);

    /// <summary>Queues an input event for the session agent to inject.</summary>
    bool QueueInput(RemoteInputRequest input);

    /// <summary>Asks the session agent to start a screen stream for a device and display.</summary>
    bool RequestCapture(string deviceId, int displayId, int fps, int quality, int gridCols, int gridRows);

    /// <summary>Asks the session agent to stop any active screen stream.</summary>
    bool StopCapture(string deviceId);

    /// <summary>Asks the session agent to show the pairing approval prompt.</summary>
    bool ShowPairingPrompt(PairingPromptRequest request);

    /// <summary>Asks the session agent to launch an application on the interactive desktop.</summary>
    bool TrySendLaunch(string appId, string executable, string commandLine, string? workingDirectory, bool elevated);
}

/// <summary>
/// Payload for the on-desktop "New device wants to pair" prompt.
/// <para>
/// <see cref="PollingUrl"/> is the whole reason this prompt exists. The agent mints a random
/// 32-byte provisioning secret per attempt, and the wrapped device credentials are released
/// only to a phone that can present it. That secret therefore has to reach the phone through
/// the user, not through the network: a requester on the LAN who was handed the secret over
/// HTTP would be exactly the attacker the mechanism exists to stop. A QR code on this dialog,
/// which the user points their phone at, is the transfer.
/// </para>
/// </summary>
public sealed record PairingPromptRequest(
    string PairingId,
    string DeviceName,
    string Platform,
    string? OsVersion,
    string? AppVersion,
    string RemoteAddress,
    string UserCode,
    int ExpiresInSeconds,
    string PollingUrl);

/// <summary>Incoming messages from the session agent over the reverse channel.</summary>
public sealed class SessionAgentMessage
{
    public string T { get; set; } = "";
    public string? DeviceId { get; set; }
    public int? DisplayId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? RefreshRateHz { get; set; }
    public int? ScalePercent { get; set; }
    public string? Name { get; set; }
    public int? Rotation { get; set; }
    public bool? IsPrimary { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool? IsLocked { get; set; }
    public string? UserName { get; set; }
    public uint? SessionId { get; set; }
    public int? ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public long? WorkingSetBytes { get; set; }
    public bool? Succeeded { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }
    public int? ExitCode { get; set; }
    public bool? Ok { get; set; }
    public string? ResponseTo { get; set; }
    public string? CorrelationId { get; set; }
    public byte[]? Data { get; set; }
    public List<SessionAgentDisplay>? Displays { get; set; }
    public List<SessionAgentApp>? Apps { get; set; }
    public List<SessionAgentEvent>? Events { get; set; }
}

public sealed class SessionAgentDisplay
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
    public int Rotation { get; set; }
    public double? RefreshRateHz { get; set; }
    public int? ScalePercent { get; set; }
}

public sealed class SessionAgentApp
{
    public string Id { get; set; } = "";
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
}

public sealed class SessionAgentEvent
{
    public string Kind { get; set; } = "";
    public string? State { get; set; }
    public long Ts { get; set; }
}

/// <summary>One input event queued for the session agent.</summary>
public sealed class PendingInputEvent
{
    public string Kind { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public string? Button { get; set; }
    public bool? Down { get; set; }
    public int? DeltaX { get; set; }
    public int? DeltaY { get; set; }
    public string? Code { get; set; }
    public string? Text { get; set; }
    public List<string>? Mods { get; set; }
    public int? DisplayId { get; set; }
    public long Sequence { get; set; }
}

/// <summary>Wire format for the service → agent command channel.</summary>
public sealed class SessionCommandEnvelope
{
    public string T { get; set; } = "";
    public string? Id { get; set; }
    public bool? Flag { get; set; }
    public PendingInputEvent? Input { get; set; }
    public string? DeviceId { get; set; }
    public int? DisplayId { get; set; }
    public int? Fps { get; set; }
    public int? Quality { get; set; }
    public int? GridCols { get; set; }
    public int? GridRows { get; set; }
    public PairingPromptRequest? Prompt { get; set; }
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool? Elevated { get; set; }
}

public static class SessionChannelProtocol
{
    public const string PipeName = "ESIRemotePc-SessionCommand-v1";

    /// <summary>Envelope types on the command channel.</summary>
    public static class Commands
    {
        public const string Ping = "ping";
        public const string Lock = "lock";
        public const string SignOut = "signout";
        public const string Suspend = "suspend";
        public const string RefreshState = "refreshState";
        public const string Input = "input";
        public const string CaptureStart = "capture.start";
        public const string CaptureStop = "capture.stop";
        public const string PairingPrompt = "pairing.prompt";
        public const string LaunchApp = "app.launch";
        public const string TerminateApp = "app.terminate";
        public const string ShowToast = "toast.show";
        public const string SyncNow = "sync.now";
    }

    /// <summary>Message types on the agent → service status channel.</summary>
    public static class Events
    {
        public const string Ready = "ready";
        public const string Hello = "hello";
        public const string StateChanged = "state.changed";
        public const string LockChanged = "lock.changed";
        public const string Displays = "displays";
        public const string Frame = "frame";
        public const string CommandResult = "command.result";
        public const string PairingResult = "pairing.result";
        public const string LaunchResult = "launch.result";
        public const string Heartbeat = "heartbeat";
        public const string AppStarted = "app.started";
        public const string AppExited = "app.exited";
    }
}

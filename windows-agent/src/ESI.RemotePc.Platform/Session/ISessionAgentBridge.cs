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

    /// <summary>
    /// Asks the session agent to inject a volume or mute key.
    /// <para>
    /// The service cannot do this itself. It runs in session 0, where the keys are injected into
    /// a desktop nobody is watching, so every volume control came back refused with no reason the
    /// phone could read (D43).
    /// </para>
    /// </summary>
    bool SendMediaKey(ushort virtualKey, int repeats, string action, string deviceId);

    /// <summary>
    /// Asks the session agent to move the volume to an absolute target. The walk has to happen
    /// where the current volume can be read, which is the interactive session and not session 0.
    /// </summary>
    bool SetMediaVolume(int target, string action, string deviceId);

    /// <summary>
    /// Asks the session agent for the PC's volume, mute state and output device.
    /// <para>
    /// A question rather than a command, because the answer is the point: the service runs in
    /// session 0 with no audio endpoint, so it cannot read any of this itself and every poll it
    /// answered from its own <c>MediaController</c> came back 0. Returns null when the agent did
    /// not answer in time, which the caller reports as "unknown" rather than as a silent PC.
    /// </para>
    /// </summary>
    Task<MediaStateFromAgent?> QueryMediaStateAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the session agent to inject a transport key. Injected from session 0 the call
    /// succeeds and reaches nothing, so the phone was answered 200 and nothing happened (D43).
    /// </summary>
    bool SendMediaTransport(string action, string deviceId);

    /// <summary>Asks the session agent to start a screen stream for a device and display.</summary>
    bool RequestCapture(string deviceId, int displayId, int fps, int quality, int gridCols, int gridRows);

    /// <summary>Asks the session agent to stop any active screen stream.</summary>
    bool StopCapture(string deviceId);

    /// <summary>
    /// Asks the session agent to open the output device for loopback capture.
    /// </summary>
    /// <remarks>
    /// Returns whether the request was delivered, not whether audio is flowing. The device opens
    /// asynchronously and can refuse several hundred milliseconds later, so the truth only
    /// exists in the session agent's log and on the phone's meter.
    /// </remarks>
    bool RequestAudio(string deviceId);

    /// <summary>Closes the loopback device.</summary>
    bool StopAudio(string deviceId);

    /// <summary>
    /// Asks the session agent to show the pairing approval prompt.
    /// </summary>
    /// <remarks>
    /// The prompt outlives its own approval on purpose. The QR on it is the only copy of the
    /// provisioning secret, and the secret is what the phone still needs <i>after</i> Allow is
    /// clicked - so closing the dialog there destroyed the transfer and left an approved pairing
    /// the phone could never complete (D36). The dialog stays up until this call says the phone
    /// has collected its keys, or until the window's deadline passes.
    /// </remarks>
    bool ShowPairingPrompt(PairingPromptRequest request);

    /// <summary>
    /// Tells the session agent that the phone holding <paramref name="pairingId"/>'s secret has
    /// collected its credentials, so an open prompt for that attempt has served its purpose and
    /// can close. A no-op when no prompt is open, or when the agent is not connected.
    /// </summary>
    bool NotifyPairingCompleted(string pairingId);

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
    /// <summary>
    /// The URL carrying the provisioning secret, for the prompt to draw as a QR code - or
    /// <c>null</c> when there is nothing to transfer.
    /// <para>
    /// Null means the requester supplied its own secret with the request, so it can already
    /// unlock its own credentials and the prompt only has to say yes. That is the normal case
    /// for a current phone (D39); non-null is the fallback for a client that brought nothing,
    /// which still needs the user to hand the secret over by scanning.
    /// </para>
    /// </summary>
    string? PollingUrl,
    /// <summary>
    /// This machine's SPKI pin, for the prompt to display.
    /// <para>
    /// D53: the phone asks the user to type the pin "from the PC's pairing screen", and the
    /// pairing screen did not show it. The only copy was a line in the agent log at startup,
    /// which is not something anyone opens while holding a phone. This is where the person
    /// deciding to trust the machine is already standing, so it goes here.
    /// </para>
    /// <para>
    /// It is a public key hash. Showing it tells a user nothing an attacker who can see the
    /// screen could not also see - what matters is that they compare it against the value on
    /// the phone rather than type one the phone supplied.
    /// </para>
    /// </summary>
    string? SpkiPin);

/// <summary>How the person on the desktop answered a pairing prompt.</summary>
public enum PairingOutcome
{
    /// <summary>Approved; the attempt is now waiting for the phone's one-time secret.</summary>
    Approved,

    /// <summary>Refused. The phone is told <c>denied</c> on its next poll.</summary>
    Denied,

    /// <summary>
    /// Nobody answered before the window closed — the dialog timed out, or it was closed
    /// without a decision being made by the deadline timer.
    /// </summary>
    Expired,
}

/// <summary>
/// The service's side of a pairing prompt: what the person on the desktop decided.
/// <para>
/// A distinct type from <see cref="PairingPromptRequest"/> because it answers a prompt that
/// has already been raised. Reusing the request shape invited reading a decision as if it
/// were another invitation, and nothing was listening anyway (D34).
/// </para>
/// </summary>
public sealed record PairingDecisionReport(
    string PairingId,
    string UserCode,
    string DeviceName,
    PairingOutcome Outcome);

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

    // ---- media state, carried by media replies --------------------------------------
    //
    // Filled in by the session agent, which is the only process that can read them: the
    // service sits in session 0 with no audio endpoint (D43). The *Known flags travel with the
    // values because a missing read is not a zero, and "I cannot see the volume" must not be
    // indistinguishable from "the PC is silent" once it crosses the pipe.

    /// <summary>The PC's default output volume, 0-100.</summary>
    public int? Volume { get; set; }

    /// <summary>Whether <see cref="Volume"/> was read rather than left at its default.</summary>
    public bool? VolumeKnown { get; set; }

    public bool? IsMuted { get; set; }

    /// <summary>Whether <see cref="IsMuted"/> was read rather than left at its default.</summary>
    public bool? IsMuteKnown { get; set; }

    /// <summary>The user-visible name of the default output, e.g. "Speakers (Realtek(R) Audio)".</summary>
    public string? DefaultDevice { get; set; }
}

/// <summary>
/// The media state as the session agent read it, with the "could I read it at all" answers kept
/// beside the values.
/// <para>
/// Deliberately not <see cref="Esi.Protocol.MediaStateResponse"/>: that type is what goes to the
/// phone, and the two must be able to disagree, because the service has its own fallback when
/// there is no agent to ask. Collapsing them would make "the agent said unknown" and "the
/// service guessed" look the same from the outside.
/// </para>
/// </summary>
public sealed record MediaStateFromAgent(
    int Volume,
    bool VolumeKnown,
    bool IsMuted,
    bool IsMuteKnown,
    string? DefaultDevice);

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

    /// <summary>The virtual key to inject, for <see cref="Commands.MediaKey"/>.</summary>
    public int? VirtualKey { get; set; }

    /// <summary>How many times to press it, for <see cref="Commands.MediaKey"/>.</summary>
    public int? Repeats { get; set; }

    /// <summary>The action name to echo back, so the phone's response names it.</summary>
    public string? Action { get; set; }

    /// <summary>
    /// Correlates a follow-up with the request it answers - the pairing attempt id for
    /// <see cref="SessionChannelProtocol.Commands.PairingCompleted"/>, so a prompt can tell
    /// whether the news is about the attempt it is showing.
    /// </summary>
    public string? CorrelationId { get; set; }
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

        /// <summary>
        /// Opens the default output device for loopback capture, so the phone can play what the
        /// PC is playing.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="CaptureStart"/> because the two fail independently and for
        /// entirely different reasons. A machine with no sound card can still show its screen
        /// perfectly well, and a machine whose screen capture is refused still has audio worth
        /// hearing. Folding them together would mean one broken thing took out both.
        /// </remarks>
        public const string AudioStart = "audio.start";

        /// <summary>Closes the loopback device. The counterpart to AudioStart.</summary>
        public const string AudioStop = "audio.stop";
        public const string PairingPrompt = "pairing.prompt";
        public const string PairingCompleted = "pairing.completed";
        public const string LaunchApp = "app.launch";

        /// <summary>
        /// Injects a volume or mute key from the interactive desktop. Volume keys are
        /// session-bound just like the transport keys, so the service has to route them through
        /// the session agent rather than injecting them into its own session 0 (D43).
        /// </summary>
        public const string MediaKey = "media.key";

        /// <summary>
        /// Moves the volume to an absolute target, computed where the volume can be read (D43).
        /// </summary>
        public const string MediaVolumeSet = "media.volumeSet";

        /// <summary>
        /// Asks the session agent for the volume, mute state and output device name.
        /// <para>
        /// The read has to happen there for the same reason the write does: the service runs in
        /// session 0 with no audio endpoint, so it cannot see the volume at all (D43). Without
        /// this the phone polled the service, the service answered from a process that has
        /// never been able to open Core Audio, and every poll came back 0 - next to a slider the
        /// user had just moved, with nothing on screen saying which of the two was real.
        /// </para>
        /// </summary>
        public const string MediaStateQuery = "media.stateQuery";

        /// <summary>
        /// Injects a transport key from the interactive desktop. These were the quiet half of D43:
        /// <c>SendInput</c> from session 0 succeeds and reaches nothing, so the phone was told
        /// 200 and play and pause silently did nothing.
        /// </summary>
        public const string MediaTransport = "media.transport";
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

        /// <summary>
        /// The agent is leaving because somebody asked it to from its own tray menu, rather
        /// than because something went wrong.
        /// <para>
        /// The service supervises the session agent and starts one whenever a live session has
        /// none. So an Exit that arrives only as a broken pipe is indistinguishable from a
        /// crash: within three seconds the supervisor starts a new agent, the tray icon
        /// returns, and the menu item appears to do nothing at all (D74).
        /// </para>
        /// </summary>
        public const string AgentStop = "agent.stop";
    }
}

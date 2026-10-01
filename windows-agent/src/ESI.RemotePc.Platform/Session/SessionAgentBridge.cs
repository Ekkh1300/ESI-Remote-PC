using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Esi.Platform.Session;

public sealed class SessionOptions
{
    /// <summary>Path to the per-user session agent executable.</summary>
    public string AgentPath { get; set; } = string.Empty;

    /// <summary>ProgramData directory used for the shared token and runtime files.</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>Launch the agent automatically when a user logs on.</summary>
    public bool AutoLaunch { get; set; } = true;

    /// <summary>Restart the agent automatically if it exits unexpectedly.</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Seconds to wait for the agent to answer a health probe before relaunching.</summary>
    public int StartupTimeoutSeconds { get; set; } = 15;

    /// <summary>Consider the agent dead if no heartbeat arrives within this many seconds.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 45;
}

/// <summary>
/// Owns the connection to the per-user session agent and launches it when a user
/// session appears.
/// <para>
/// Launching uses the documented service → user-session path:
/// <c>WTSEnumerateSessionsW</c> to find the active session,
/// <c>WTSQueryUserToken</c> to get its primary token (requires LocalSystem +
/// <c>SE_TCB_NAME</c>), <c>DuplicateTokenEx</c>, <c>CreateEnvironmentBlock</c>, then
/// <c>CreateProcessAsUserW</c> with <c>STARTUPINFO.lpDesktop = "winsta0\\default"</c>.
/// </para>
/// <para>
/// A deliberate non-goal: the service does <em>not</em> open a window station or desktop
/// itself. The Win32 docs are explicit that doing so exposes the service to attacks from
/// the logged-on user, so the service stays headless and all desktop work happens in the
/// agent process.
/// </para>
/// </summary>
public sealed class SessionAgentBridge : ISessionAgentBridge, IAsyncDisposable, IDisposable
{
    private readonly ILogger<SessionAgentBridge> _log;
    private readonly SessionOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private NamedPipeServerStream? _pipe;
    private Task? _pump;
    private Task? _supervisor;

    private volatile SessionAgentStateValue _state = SessionAgentStateValue.NotRunning;
    private volatile bool _connected;
    private volatile bool _locked;
    private volatile bool _suspended;
    private volatile uint _sessionId = 0xFFFFFFFF;
    private volatile string? _activeUser;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private long _inputSequence;

    // Events raised when the session agent reports something interesting.
    public event EventHandler<SessionAgentEvent>? EventReported;
    public event EventHandler<PairingPromptRequest>? PairingApprovalRequested;
    public event EventHandler<SessionAgentCommandResult>? CommandCompleted;

    /// <summary>
    /// A captured, encoded screen frame arrived from the session agent.
    /// <para>
    /// This is the only path screen pixels take. The service cannot capture anything itself
    /// (see <see cref="ISessionAgentBridge"/>), so without this event a phone that started a
    /// stream would be told the stream was running and then receive silence.
    /// </para>
    /// </summary>
    public event EventHandler<SessionAgentFrame>? FrameReceived;

    public SessionAgentBridge(IOptions<SessionOptions> options, ILogger<SessionAgentBridge> log)
    {
        _options = options.Value;
        _log = log;
    }

    public SessionAgentStateValue State => _state;
    public bool IsConnected => _connected;
    public bool IsWorkstationLocked => _locked;
    public bool IsSuspended => _suspended;
    public string? ActiveUser => _activeUser;

    public uint? CurrentSession()
    {
        var id = Native.KERNEL32.WTSGetActiveConsoleSessionId();
        if (id == 0xFFFFFFFF)
            return null;
        _sessionId = id;
        return id;
    }

    public void Start()
    {
        if (_supervisor is not null)
            return;

        _supervisor = Task.Run(() => SuperviseAsync(_cts.Token));
    }

    // -----------------------------------------------------------------------------
    // Supervisor
    // -----------------------------------------------------------------------------

    private async Task SuperviseAsync(CancellationToken ct)
    {
        // The session agent only exists while somebody is logged in, and the active
        // session can change (fast user switching, RDP, unlock). Poll for it.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var session = CurrentSession();
                var shouldRun = session.HasValue && _options.AutoLaunch;

                if (shouldRun && !_connected)
                {
                    try
                    {
                        await EnsureConnectedAsync(session!.Value, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Could not reach the session agent for session {Session}", session);
                        _state = SessionAgentStateValue.Error;
                    }
                }
                else if (!shouldRun && _connected)
                {
                    _log.LogInformation("The interactive session ended; tearing the command channel down");
                    await DisconnectAsync("no interactive session").ConfigureAwait(false);
                }
                if (_connected && _lastHeartbeatUtc != DateTime.MinValue &&
                    DateTime.UtcNow - _lastHeartbeatUtc > TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds))
                {
                    _log.LogWarning("Session agent heartbeat timed out; reconnecting");
                    await DisconnectAsync("heartbeat timeout").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The session agent supervisor stopped unexpectedly");
        }
    }

    private async Task EnsureConnectedAsync(uint sessionId, CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected && _pipe is { IsConnected: true })
                return;

            _state = SessionAgentStateValue.Starting;

            // Release whatever instance is left over before making another one. The name
            // allows a single server instance, so overwriting this reference without
            // closing it strands an open instance that nothing can reach, and every later
            // CreatePipeServer fails with "All pipe instances are busy" - after which the
            // bridge can never reconnect. The previous pipe is only ever here when the pump
            // has already finished with it, so nothing is being pulled out from under a
            // live connection.
            if (_pipe is not null)
            {
                await SafeDisposeAsync(_pipe).ConfigureAwait(false);
                _pipe = null;
            }

            // The pipe must be bidirectional. The service reads the agent's ready, heartbeat,
            // frame and result messages, and writes the agent's commands, input events and
            // pairing prompts. Opening it In-only lets the service read a stream nothing can
            // write to, and every command send fails with InvalidOperationException.
            _pipe = CreatePipeServer();

            // 1. Make sure the agent is running in that session. The pipe is created first
            //    so the agent's connect attempt is not refused while the process is starting.
            if (_options.AutoLaunch)
                EnsureAgentRunning(sessionId);

            // 2. Wait for it to connect back on the command pipe.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));

            try
            {
                await _pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await SafeDisposeAsync(_pipe).ConfigureAwait(false);
                _pipe = null;
                _state = SessionAgentStateValue.NoUserSession;
                return;
            }

            _connected = true;
            _lastHeartbeatUtc = DateTime.UtcNow;
            _state = SessionAgentStateValue.Ready;
            _pump = Task.Run(() => PumpAsync(_pipe!, ct), ct);

            _log.LogInformation("Session agent connected on {Pipe} (session {Session})",
                SessionChannelProtocol.PipeName, sessionId);

            // Ask for a fresh state snapshot now that we are connected.
            Send(new SessionCommandEnvelope { T = SessionChannelProtocol.Commands.SyncNow });
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task DisconnectAsync(string reason)
    {
        _connected = false;
        _state = CurrentSession().HasValue ? SessionAgentStateValue.Starting : SessionAgentStateValue.NoUserSession;
        _locked = false;
        _suspended = false;

        if (_pipe is not null)
        {
            await SafeDisposeAsync(_pipe).ConfigureAwait(false);
            _pipe = null;
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // The pump is already gone or wedged; nothing useful to do here.
            }
            _pump = null;
        }

        _log.LogInformation("Session agent channel closed: {Reason}", reason);
    }

    private async Task PumpAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                if (line.Length == 0)
                    continue;

                SessionAgentMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<SessionAgentMessage>(line, SessionJson.Options);
                }
                catch (JsonException ex)
                {
                    _log.LogWarning(ex, "Discarding a malformed session agent message");
                    continue;
                }

                if (message is not null)
                    HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal.
        }
        catch (IOException)
        {
            // The agent went away; the supervisor will reconnect.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The session agent command pump stopped with an error");
        }
        finally
        {
            // Closing the instance this pump was reading is what frees the single server
            // slot the pipe name allows. Handing it back here rather than leaving it for
            // the next EnsureConnectedAsync is the difference between a reconnect and a
            // permanent "All pipe instances are busy".
            await SafeDisposeAsync(pipe).ConfigureAwait(false);

            _connected = false;
            _state = SessionAgentStateValue.NotRunning;
        }
    }

    private void HandleMessage(SessionAgentMessage m)
    {
        _lastHeartbeatUtc = DateTime.UtcNow;

        switch (m.T)
        {
            case SessionChannelProtocol.Events.Ready:
            case SessionChannelProtocol.Events.Hello:
                _state = SessionAgentStateValue.Ready;
                _connected = true;
                if (m.SessionId.HasValue)
                    _sessionId = m.SessionId.Value;
                _activeUser = m.UserName;
                _log.LogInformation("Session agent ready: user={User} session={Session} pid={Pid}",
                    m.UserName, m.SessionId, m.ProcessId);
                break;

            case SessionChannelProtocol.Events.Heartbeat:
                if (m.IsLocked.HasValue)
                    _locked = m.IsLocked.Value;
                if (m.SessionId.HasValue)
                    _sessionId = m.SessionId.Value;
                _activeUser = m.UserName ?? _activeUser;
                break;

            case SessionChannelProtocol.Events.StateChanged:
                if (m.IsLocked.HasValue)
                    _locked = m.IsLocked.Value;
                _suspended = string.Equals(m.Detail, "sleeping", StringComparison.OrdinalIgnoreCase);
                if (m.UserName is not null)
                    _activeUser = m.UserName;
                if (m.SessionId.HasValue)
                    _sessionId = m.SessionId.Value;
                break;

            case SessionChannelProtocol.Events.LockChanged:
                _locked = m.IsLocked ?? false;
                break;

            case SessionChannelProtocol.Events.PairingResult:
                // The user answered a prompt the agent raised earlier, so this is a
                // notification rather than a new request: the fields carried are the outcome,
                // not a fresh invitation. There is no URL to publish, hence the empty one.
                PairingApprovalRequested?.Invoke(this, new PairingPromptRequest(
                    m.ResponseTo ?? "", m.Name ?? "Unknown device", "android", null, null,
                    m.Detail ?? "local", m.CorrelationId ?? "", 0, string.Empty));
                break;

            case SessionChannelProtocol.Events.CommandResult:
            case SessionChannelProtocol.Events.LaunchResult:
                CommandCompleted?.Invoke(this, new SessionAgentCommandResult
                {
                    Command = m.T,
                    CorrelationId = m.CorrelationId,
                    Succeeded = m.Succeeded ?? m.Ok ?? false,
                    Detail = m.Detail,
                    Error = m.Error,
                    ProcessId = m.ProcessId,
                });
                break;

            case SessionChannelProtocol.Events.Frame:
                if (m.Data is { Length: > 0 })
                    FrameReceived?.Invoke(this, new SessionAgentFrame(m.DeviceId ?? "", m.Data));
                break;

            case SessionChannelProtocol.Events.AppStarted:
            case SessionChannelProtocol.Events.AppExited:
                break;
        }

        if (m.Events is { Count: > 0 })
        {
            foreach (var e in m.Events)
            {
                if (string.Equals(e.Kind, "resume", StringComparison.OrdinalIgnoreCase))
                    _suspended = false;
                EventReported?.Invoke(this, e);
            }
        }
    }

    // -----------------------------------------------------------------------------
    // Sending
    // -----------------------------------------------------------------------------

    private bool Send(SessionCommandEnvelope envelope)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected || !_connected)
            return false;

        try
        {
            var json = JsonSerializer.Serialize(envelope, SessionJson.Options);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (IOException ex)
        {
            _log.LogDebug(ex, "Could not write to the session agent channel");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool TrySend(PowerSessionCommand command, bool flag, out string? detail)
    {
        var envelope = command switch
        {
            PowerSessionCommand.Lock => new SessionCommandEnvelope { T = SessionChannelProtocol.Commands.Lock },
            PowerSessionCommand.SignOut => new SessionCommandEnvelope { T = SessionChannelProtocol.Commands.SignOut, Flag = flag },
            PowerSessionCommand.Suspend => new SessionCommandEnvelope { T = SessionChannelProtocol.Commands.Suspend, Flag = flag },
            PowerSessionCommand.RefreshState => new SessionCommandEnvelope { T = SessionChannelProtocol.Commands.SyncNow },
            _ => null,
        };

        if (envelope is null)
        {
            detail = $"Unsupported session command {command}.";
            return false;
        }

        if (Send(envelope))
        {
            detail = null;
            return true;
        }

        detail = "The per-user session agent is not connected.";
        return false;
    }

    public bool QueueInput(RemoteInputRequest input)
    {
        var evt = new PendingInputEvent
        {
            Kind = input.Kind,
            X = input.X,
            Y = input.Y,
            Button = input.Button,
            Down = input.Down,
            DeltaX = input.DeltaX,
            DeltaY = input.DeltaY,
            Code = input.Code,
            Text = input.Text,
            Mods = input.Mods,
            DisplayId = input.DisplayId,
            Sequence = Interlocked.Increment(ref _inputSequence),
        };

        return Send(new SessionCommandEnvelope
        {
            T = SessionChannelProtocol.Commands.Input,
            Input = evt,
        });
    }

    public bool RequestCapture(string deviceId, int displayId, int fps, int quality, int gridCols, int gridRows)
        => Send(new SessionCommandEnvelope
        {
            T = SessionChannelProtocol.Commands.CaptureStart,
            DeviceId = deviceId,
            DisplayId = displayId,
            Fps = fps,
            Quality = quality,
            GridCols = gridCols,
            GridRows = gridRows,
        });

    public bool StopCapture(string deviceId)
        => Send(new SessionCommandEnvelope
        {
            T = SessionChannelProtocol.Commands.CaptureStop,
            DeviceId = deviceId,
        });

    public bool ShowPairingPrompt(PairingPromptRequest request)
        => Send(new SessionCommandEnvelope
        {
            T = SessionChannelProtocol.Commands.PairingPrompt,
            Prompt = request,
        });

    public bool TrySendLaunch(string appId, string executable, string commandLine, string? workingDirectory, bool elevated)
        => Send(new SessionCommandEnvelope
        {
            T = SessionChannelProtocol.Commands.LaunchApp,
            Id = appId,
            Command = executable,
            Arguments = commandLine,
            WorkingDirectory = workingDirectory,
            Elevated = elevated,
        });

    // -----------------------------------------------------------------------------
    // The pipe's access control
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Builds the command pipe with an explicit DACL.
    /// <para>
    /// The default is wrong in both directions. A named pipe created by LocalSystem is
    /// readable only by LocalSystem, so the per-user agent in the interactive session would
    /// be refused with <c>UnauthorizedAccessException</c> and the feature would appear to do
    /// nothing. Granting everyone would be worse: on a shared machine any local account
    /// could then ask the service to lock the desktop or inject keystrokes into another
    /// user's session.
    /// </para>
    /// <para>
    /// The DACL therefore names exactly three principals: LocalSystem and Administrators,
    /// which own the service, and the account that owns the console session this agent exists
    /// to serve. If that account cannot be resolved the pipe falls back to SYSTEM plus
    /// Administrators only, and the session agent cannot connect — a visible failure, which is
    /// the right trade against silently widening access.
    /// </para>
    /// </summary>
    private NamedPipeServerStream CreatePipeServer()
    {
        var security = new PipeSecurity();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // The owner is deliberately left as the default. Naming a different owner is
        // rejected outright ("This security ID may not be assigned as the owner of this
        // object") unless the caller is running as that account, and the agent also runs in
        // console mode under a normal user account - where SetOwner(LocalSystem) throws and
        // the session agent can then never connect. The creator is the owner by default,
        // which is the correct answer in both modes.
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (TryGetSessionUserSid(_sessionId, out var sessionUser) && sessionUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                sessionUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));
        }
        else
        {
            _log.LogWarning(
                "Could not resolve the account for session {Session}; the session agent will not be " +
                "able to connect to the command pipe. This is expected in a container or a " +
                "session with no cached profile.", _sessionId);
        }

        return NamedPipeServerStreamAcl.Create(
            SessionChannelProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    /// <summary>Resolves the account behind a session id to a SID.</summary>
    private static bool TryGetSessionUserSid(uint sessionId, out SecurityIdentifier? sid)
    {
        sid = null;

        if (sessionId == 0xFFFFFFFF)
            return false;

        // WTSUserName == 5
        if (!Native.USER32.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, 5, out var buffer, out _))
            return false;

        try
        {
            var name = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var account = new System.Security.Principal.NTAccount(name);
            sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (SystemException)
        {
            // Unknown or unresolvable account (a deleted profile, a domain that is down).
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Native.USER32.WTSFreeMemory(buffer);
        }
    }

    // -----------------------------------------------------------------------------
    // Launching the agent in the interactive session
    // -----------------------------------------------------------------------------

    private void EnsureAgentRunning(uint sessionId)
    {
        if (string.IsNullOrWhiteSpace(_options.AgentPath) || !File.Exists(_options.AgentPath))
        {
            _log.LogDebug("Session agent binary not found at {Path}; relying on the logon task", _options.AgentPath);
            return;
        }

        // Already running in that session?
        if (_connected || HasSessionAgentProcess(sessionId))
            return;

        if (!TokenBootstrap.TryGetUserToken(sessionId, out var token, out var tokenError))
        {
            _log.LogWarning("No user token for session {Session}: {Error}", sessionId, tokenError);
            _state = SessionAgentStateValue.NoUserSession;
            return;
        }

        IntPtr environment = IntPtr.Zero;
        try
        {
            if (USERENV.CreateEnvironmentBlock(out environment, token, false))
            {
                var startup = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOW>(),
                    // This is what places the process on the interactive desktop. A
                    // process started without it is invisible and cannot create windows.
                    lpDesktop = "winsta0\\default",
                    dwFlags = 0,
                };

                var commandLine = $"\"{_options.AgentPath}\" --channel \"{SessionChannelProtocol.PipeName}\" --session {sessionId}";

                if (ADVAPI32.CreateProcessAsUserW(
                        token,
                        _options.AgentPath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        WinConst.CREATE_UNICODE_ENVIRONMENT,
                        environment,
                        Path.GetDirectoryName(_options.AgentPath),
                        ref startup,
                        out var pi))
                {
                    _log.LogInformation("Launched the session agent as pid {Pid} in session {Session}",
                        pi.dwProcessId, sessionId);

                    if (pi.hProcess != IntPtr.Zero)
                        Native.KERNEL32.CloseHandle(pi.hProcess);
                    if (pi.hThread != IntPtr.Zero)
                        Native.KERNEL32.CloseHandle(pi.hThread);

                    return;
                }

                var error = Marshal.GetLastWin32Error();
                _log.LogWarning("CreateProcessAsUserW failed ({Error}) for session {Session}. " +
                                "This is expected when the agent is not running as LocalSystem " +
                                "or lacks SeTcbPrivilege / SeAssignPrimaryTokenPrivilege; " +
                                "the 'Start at logon' scheduled task will cover it instead.", error, sessionId);
            }
            else
            {
                _log.LogWarning("CreateEnvironmentBlock failed ({Error})", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
                USERENV.DestroyEnvironmentBlock(environment);
            if (token != IntPtr.Zero)
                Native.KERNEL32.CloseHandle(token);
        }
    }

    private bool HasSessionAgentProcess(uint sessionId)
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("ESI.RemotePc.Session"))
            {
                using (process)
                {
                    if (Native.KERNEL32.ProcessIdToSessionId((uint)process.Id, out var sid) && sid == sessionId)
                        return true;
                }
            }
        }
        catch
        {
            // Process enumeration can fail under tight audit policies; treat as absent.
        }

        return false;
    }

    // -----------------------------------------------------------------------------

    private static async Task SafeDisposeAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Shutting down regardless.
            }
        }

        Dispose();
    }

    public void Dispose()
    {
        Cancel();

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // Nothing to do.
        }

        _pipe = null;

        // Deliberately not disposed. The bridge is registered under both its concrete type
        // and ISessionAgentBridge, so the container disposes the same instance twice; a
        // disposed CancellationTokenSource throws on Cancel, and a background supervisor
        // holding the token must be able to observe cancellation long after teardown.
        _connectGate.Dispose();
    }

    private int _disposed;

    /// <summary>Cancels the supervisor exactly once, even across a double dispose.</summary>
    private void Cancel()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }
    }
}

public sealed class SessionAgentCommandResult
{
    public string Command { get; set; } = "";
    public string? CorrelationId { get; set; }
    public bool Succeeded { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }
    public int? ProcessId { get; set; }
}

/// <summary>
/// One encoded screen frame on its way from the session agent to a phone. The bytes are
/// already in the wire format of <c>docs/PROTOCOL.md</c> §7.5: a 32-byte
/// <see cref="ScreenFrameHeader"/> followed by the tile descriptors and JPEG payloads.
/// </summary>
public sealed class SessionAgentFrame
{
    public SessionAgentFrame(string deviceId, byte[] data)
    {
        DeviceId = deviceId;
        Data = data;
    }

    /// <summary>The device the session agent was asked to capture for.</summary>
    public string DeviceId { get; }

    /// <summary>Complete wire payload, header included.</summary>
    public byte[] Data { get; }
}

public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

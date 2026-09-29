# ESI Remote PC — Architecture

How the Windows Agent is put together and why it is split the way it is. The wire contract
is in [`PROTOCOL.md`](PROTOCOL.md); the route list is in [`API.md`](API.md); the security
rationale is in [`SECURITY.md`](SECURITY.md).

---

## 1. The four-layer split

```
┌──────────────────────────────────────────────────────────────────────┐
│  ESI.RemotePc.Agent.Service      session 0, LocalSystem              │
│  ─────────────────────────────────────────────────────────────────── │
│  AgentHost          composition root: DI, Kestrel, pipeline, routes  │
│  Endpoints          the HTTP surface, one thin adapter per handler   │
│  Middleware         request context, LAN guard, signature, scope    │
│  FeatureServices    AppService, GamingService, RemoteService,        │
│                     UploadTicketStore, NotificationEngine            │
│  WebSocketHub       streaming fan-out and remote-control routing     │
│  AgentHostedService metrics loop, discovery, mDNS, session bridge    │
└──────────────────────────────┬───────────────────────────────────────┘
                               │ project references (one direction)
┌──────────────────────────────▼───────────────────────────────────────┐
│  ESI.RemotePc.Agent.Core       no Windows UI, no HTTP                │
│  ─────────────────────────────────────────────────────────────────── │
│  AgentDatabase     SQLite schema, WAL, connection factory           │
│  SettingsStore     SettingsDto persistence, validation, live apply   │
│  DeviceStore       paired devices, DPAPI-protected master secrets    │
│  PairingManager    the pairing state machine                         │
│  RequestVerifier   HMAC verification and nonce cache                  │
│  RateLimiter       token buckets + LocalNetworkGuard                  │
│  AuditLog          append-only hash chain                             │
│  CertificateAuthority  the TLS identity                               │
│  MetricsService    sampling, caching, coalescing                      │
│  Catalogs          notification rules, app catalogue                  │
└──────────────────────────────┬───────────────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────────────┐
│  ESI.RemotePc.Platform        Windows API surface, no policy          │
│  ─────────────────────────────────────────────────────────────────── │
│  Native/Win32     the P/Invoke surface (45 KB of declarations)       │
│  Machine          WMI and PDH system information, network, clock     │
│  Telemetry        SystemSampler, PdhQuery, NvmlProvider, temps       │
│  Power            PowerService, PowerStateProbe, WakeOnLanSender     │
│  Capture          IScreenCaptureProvider, GDI provider, encoder      │
│  Input            InputInjector, KeyMap (W3C code → VK/scan)         │
│  Files            FileSandbox — the only gate to the filesystem      │
│  Session          ISessionAgentBridge, SessionAgentBridge, tokens    │
│  Discovery        UdpDiscoveryResponder, MdnsResponder               │
│  Media            MediaController (Core Audio + transport keys)      │
│  Apps             AppLauncher                                          │
└──────────────────────────────┬───────────────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────────────┐
│  ESI.RemotePc.Protocol      the wire contract. No dependencies.      │
│  ─────────────────────────────────────────────────────────────────── │
│  ProtocolConstants  ports, paths, limits, names, HKDF salt            │
│  EsiCrypto          HKDF, AES-GCM, canonical string, signing         │
│  Enums              scopes, actions, severities (JSON strings)        │
│  Contracts          every DTO, one [JsonPropertyName] each           │
└──────────────────────────────────────────────────────────────────────┘
```

The dependency arrows only ever point down. `Protocol` references nothing. `Platform`
references `Protocol`. `Core` references `Protocol` and `Platform`. `Service` references
all three. `Session` references `Protocol` and `Platform` — **not** `Core`, because the
per-user process must not be able to reach the device table. `Cli` references `Protocol`,
`Platform` and `Core`, because the administration tool composes the agent's own services
rather than reimplementing the schema.

### What each boundary buys

| Boundary | Rule enforced | Consequence of breaking it |
|---|---|---|
| `Protocol` depends on nothing | Constants have exactly one definition | Two implementations of the canonical string, which fails at runtime on a signature mismatch |
| `Platform` holds no policy | `FileSandbox` validates, `FileService` decides | A second code path reaches the filesystem without validation |
| `Core` holds no HTTP and no UI | Security state has one owner | Two credential stores, two chances to get it wrong |
| `Service` holds no Win32 | Route handlers are adapters | P/Invoke leaks into request handling and cannot be tested without a host |
| `Session` cannot see `Core` | The interactive process has no credentials | A compromised desktop session would yield the device table |

The `Session` boundary is the load-bearing one. `ESI.RemotePc.Session.csproj` references
only `ESI.RemotePc.Protocol` and `ESI.RemotePc.Platform`. It can serialise JSON, move
bytes over a pipe and inject input, and that is the whole of its authority.

---

## 2. Why the session agent is a separate process

`ESI.RemotePc.Agent.Service.exe` runs as `LocalSystem` in **session 0**. Windows services
have no interactive desktop. Three consequences follow, and all three are load-bearing:

**1. Session 0 has no visible desktop.** `BitBlt` of a monitor DC, `LockWorkStation`,
`ExitWindowsEx` aimed at the console session, and any window the agent wants to show all
require a desktop that session 0 does not have. `winsta0\default` belongs to the logged-on
user's session.

**2. `SendInput` is subject to UIPI.** User Interface Privilege Isolation prevents a
process from injecting input into a window of a *higher* integrity level — and it binds
input to the caller's own session and desktop regardless of privileges. A service in
session 0 cannot type into an elevated prompt on the user's desktop, and cannot type at
all into session 2.

**3. `SetSuspendState` from session 0 gets suspend wrong.** The classic workaround —
`rundll32 powrprof.dll,SetSuspendState` — suspends session 0, not the interactive session,
and on machines without S3 it hibernates instead. `PowerService` does not use it.

So the split is: **the service owns everything that does not need a desktop** (the network
listener, the database, telemetry, power control, TLS, signing) **and the session agent owns
everything that does** (capture, input, lock, sign-out, toasts, the pairing prompt).

### What the service deliberately does not do

`SessionAgentBridge` does not open a window station or desktop. The Win32 documentation is
explicit that a service doing so exposes it to attacks from the logged-on user, so the
service stays headless and all desktop work happens in the helper. This is a considered
non-goal, recorded in the class comment.

### How the session agent is launched

`SessionAgentBridge.EnsureAgentRunning` uses the documented service-to-user-session path:

1. `WTSGetActiveConsoleSessionId` to find the active session (polled every 3 s, so fast
   user switching and RDP sessions are followed).
2. `TokenBootstrap.TryGetUserToken` → `WTSQueryUserToken`, which requires `LocalSystem`
   **and** `SeTcbPrivilege` enabled on the caller's own token. The privilege is enabled
   explicitly through `AdjustTokenPrivileges` and the error is checked with
   `GetLastWin32Error() == 0`, because `AdjustTokenPrivileges` reports success even when it
   assigns nothing.
3. `DuplicateTokenEx` with `TokenPrimary` — `WTSQueryUserToken` returns an impersonation
   token and `CreateProcessAsUserW` needs a primary one.
4. `CreateEnvironmentBlock` for the user's environment.
5. `CreateProcessAsUserW` with `STARTUPINFO.lpDesktop = "winsta0\default"`. Without that
   the process is invisible and cannot create windows.

It is also installed as a per-user service (`sc create … obj=` with an empty account), so
Windows starts one instance in each interactive session at logon. Two independent launch
paths, because either one alone has a failure mode: the per-user service silently never
starts if the SCM is not told it belongs to a user session, and the `CreateProcessAsUserW`
path fails without the `requiredPrivileges` registry value. The installer writes that value
for both services.

`--session-agent <path>` on the service's command line, or `AgentOptions.SessionAgentPath`,
tells the bridge where the binary is. With an empty path it logs and falls back to relying
on the per-user service.

---

## 3. The named-pipe command channel

One pipe, one direction of command, and a callback for everything else.

| | |
|---|---|
| Name | `ESIRemotePc-SessionCommand-v1` (`SessionChannelProtocol.PipeName`) |
| Server | The service, `SessionAgentBridge` |
| Client | The session agent, `SessionChannel` |
| Direction | `InOut` — bidirectional |
| Instances | `maxNumberOfServerInstances: 1` |
| Framing | Newline-delimited JSON, UTF-8, 64 KiB in and out |
| Access | `PipeSecurity`, created via `NamedPipeServerStreamAcl.Create` |

### The DACL

`CreatePipeServer` sets the owner to `LocalSystem` and grants:

| Principal | Rights |
|---|---|
| `LocalSystem` (`S-1-5-18`) | `FullControl` |
| `BuiltinAdministrators` (`S-1-5-32-544`) | `FullControl` |
| The account owning the active session | `ReadWrite \| Synchronize` |

The third entry is resolved by `WTSQuerySessionInformationW(WTSUserName)` →
`NTAccount.Translate(SecurityIdentifier)`. If the account cannot be resolved — a container, a
session with no cached profile, a deleted profile, a domain that is down — the bridge logs a
warning naming the session id and grants nothing extra, so the session agent **cannot
connect**. That is the intended fail-closed behaviour, and it is why the grant is narrowed
to the one user who needs it rather than to `Everyone` or to all interactive users.

The named pipe namespace is the attack surface here: a `\\.\pipe\` name is not subject to
the LAN guard, only to this DACL.

### Message types

**Service → agent** (`SessionCommandEnvelope`):

| `t` | Payload |
|---|---|
| `ping` | — |
| `lock` | — |
| `signout` | `flag` |
| `suspend` | Always answered with a refusal |
| `refreshState` | — |
| `input` | `input: PendingInputEvent` |
| `capture.start` | `deviceId`, `displayId`, `fps`, `quality`, `gridCols`, `gridRows` |
| `capture.stop` | `deviceId` |
| `pairing.prompt` | `prompt: PairingPromptRequest` |
| `app.launch` | `command`, `arguments`, `workingDirectory`, `elevated` |
| `app.terminate` | `command` |
| `toast.show` | — |
| `sync.now` | — |

**Agent → service** (`SessionAgentMessage`): `ready`, `hello`, `state.changed`,
`lock.changed`, `displays`, `frame`, `command.result`, `pairing.result`, `launch.result`,
`heartbeat`, `app.started`, `app.exited`.

### Why newline-delimited JSON

The control traffic is low-rate and human-readable, so one line in the log diagnoses a
problem. The only high-rate item is screen frames, and those are base64 inside a JSON
message — roughly a third more bytes on the wire than a binary frame. That is a deliberate
price for a channel that cannot desynchronise and needs no framing state on either side.
The service re-wraps each frame as a proper WebSocket binary frame on the way out to the
phone, so the 33 % overhead is paid once, on the local hop, not on the Wi-Fi link.

Writes are serialised through a `SemaphoreSlim(1,1)` because `PipeStream` permits only one
outstanding write and the capture loop, the heartbeat and the command results all write
concurrently. `WriteTimeout` is 5 s — a blocking write with a timeout is easier to reason
about than an async write that can complete after the reader has already gone.

The session agent handles commands **one at a time and in order**. A pairing prompt must not
be reordered behind an input event, and a capture start must not overtake the stop that
preceded it.

### Reconnection

A `PeriodicTimer` on the service polls `WTSGetActiveConsoleSessionId` every 3 s. It creates
the pipe *before* launching the agent, so the agent's connect attempt is not refused while
the process is still starting. A 15 s `StartupTimeoutSeconds` bounds the wait; exceeding it
sets the state to `NoUserSession`. A heartbeat more than `HeartbeatTimeoutSeconds` (45 s)
old tears the channel down and retries.

The session agent's own loop reconnects on `IOException`,
`UnauthorizedAccessException` and `TimeoutException` with a 3 s delay, and never throws for
an expected disconnection — the service restarts independently of logon, and a long-lived
agent has to survive that.

---

## 4. Data flow: a screen frame

The only path screen pixels take. Seven hops, three processes' worth of trust boundaries
crossed in total.

```
Phone                   Service (session 0)              Session agent (session N)
  │                            │                                  │
  │  WS binary frame           │                                  │
  │  {t:"remote.start",        │                                  │
  │   displayId, fps, quality, │                                  │
  │   grid}                    │                                  │
  ├───────────────────────────>│                                  │
  │                            │ _streamGate: refuse if another   │
  │                            │   device holds the stream        │
  │                            │                                  │
  │                            │  remote.start ──────────────────>│
  │                            │  {deviceId, displayId, fps ≤30,  │
  │                            │   quality 10-100, grid 1-16}     │
  │                            │                                  │
  │  {t:"remote.state",         │                                  │
  │   active:true,…}           │                                  │
  │<───────────────────────────┤                                  │
  │                            │                                  │
  │                            │                    CapturePump:  │
  │                            │                    timer @ fps    │
  │                            │                     │             │
  │                            │                    GDI BitBlt of │
  │                            │                    the monitor DC │
  │                            │                     │             │
  │                            │                    tile hash grid│
  │                            │                    unchanged tiles│
  │                            │                    are dropped    │
  │                            │                     │             │
  │                            │                    JPEG encode    │
  │                            │                    per changed tile│
  │                            │                     │             │
  │                            │  {t:"frame", deviceId, data:      │
  │                            │   <base64 of the 32-byte header + │
  │                            │   descriptors + JPEG bytes>}      │
  │                            │<─────────────────────────────────┤
  │                            │                                  │
  │                            │ OnFrameReceived:                 │
  │                            │   drop unless frame.DeviceId ==  │
  │                            │     _activeStreamDevice          │
  │                            │   SendBinaryAsync (not awaited)  │
  │  WS binary frame           │                                  │
  │<───────────────────────────┤                                  │
```

### Step by step, with the code

1. **`WebSocketHub.HandleRemoteStartAsync`** takes `_streamGate`, and if a different device
   already holds the stream it replies `esi_conflict` and returns. Otherwise it sets
   `_activeStreamDevice` and calls `RemoteService.StartStream`, which clamps the parameters
   and audits the start as `remote.started`.
2. **`RemoteService.StartStream`** returns `false` immediately if the session agent is not
   connected. The reply is then
   `{ active: false, detail: "The session agent is not connected, so the screen cannot be captured." }`.
3. **`ISessionAgentBridge.RequestCapture`** writes a `capture.start` envelope to the pipe.
4. **`SessionAgentRuntime`** dispatches it to `CapturePump`, which starts a dedicated
   `Thread` at `AboveNormal` priority running the capture loop at the requested frame rate.
   `AboveNormal` because a capture thread that competes with the compositor produces a
   visibly stuttering stream.
5. **`GdiScreenCaptureProvider`** does a `BitBlt` of the monitor device context into a
   managed `Bitmap`. `Name` is `"gdi"`, which is what `/remote/session` reports as
   `captureSource`. The capture is behind `IScreenCaptureProvider`, so a DXGI Desktop
   Duplication implementation is a drop-in that returns the same
   `CapturedFrame { pixelFormat, stride, width, height, buffer }`.
6. **`ScreenStreamEncoder`** hashes each cell of the tile grid against the previous frame.
   Unchanged tiles are not encoded. Changed tiles are JPEG-encoded. `EncodedFrame.ToWire()`
   serialises a 32-byte `ScreenFrameHeader`, then `tileCount` × 14-byte `TileDescriptor`,
   then the concatenated JPEG payloads.
7. **`SessionChannel.SendAsync`** writes one base64 JSON line. `SendAsync` is called from
   the capture loop, so the `SemaphoreSlim` matters here.
8. **`SessionAgentBridge.PumpAsync`** reads the line, deserialises, and raises
   `FrameReceived` with the decoded bytes.
9. **`RemoteService.FrameReceived`** re-raises it, and `WebSocketHub.OnFrameReceived` drops
   any frame whose `DeviceId` is not the active streaming device. Frames go only to
   connections belonging to that device, so a second paired phone with a live WebSocket does
   not receive megabytes it cannot decode.
10. **`WebSocketHub.SendBinaryAsync`** is **not awaited**. The pipe pump must not block on a
    slow phone, and the trade is that frames are dropped silently when a socket's send queue
    is full. Per-connection sends are serialised by `HubConnection.SendGate`, because a
    WebSocket permits only one send at a time.

### What is not in v1

No tile resend (`frameType == 3` is defined in the header struct but no path emits it and no
handler consumes it), so a lost tile stays lost until the next change touches that cell. No
cursor shape or position updates. No multi-monitor spanning — each display has its own
`displayId`. No clipboard. No audio.

---

## 5. Data flow: an input event

The same trust boundary, in the other direction, and with a much shorter path.

```
Phone                   Service (session 0)              Session agent (session N)
  │                            │                                  │
  │  WS {t:"remote.input",     │                                  │
  │   input:{kind:"key", …}}   │                                  │
  ├───────────────────────────>│                                  │
  │                            │ remote.key is folded into the    │
  │                            │ same path as remote.input         │
  │                            │                                  │
  │                            │ RemoteService.SendInput:         │
  │                            │   reject sessionStart/sessionStop│
  │                            │   reject if !IsConnected         │
  │                            │                                  │
  │                            │  {t:"input", input:{kind, x, y,  │
  │                            │   button, down, code, text, mods,│
  │                            │   displayId, sequence}}          │
  │                            │─────────────────────────────────>│
  │                            │                                  │
  │                            │                    InputInjector  │
  │                            │                    pump dequeues  │
  │                            │                    KeyMap: W3C   │
  │                            │                    code → VK +   │
  │                            │                    scan code     │
  │                            │                    SendInput on   │
  │                            │                    the UI thread  │
  │  {t:"error",               │                                  │
  │   code:"esi_session_       │                                  │
  │   agent_unavailable"}      │                                  │
  │<───────────────────────────┤  (only when not delivered)       │
```

### Step by step

1. **Two entry points, one path.** `remote.input` carries a `RemoteInputRequest` in the
   `input` field. `remote.key` is shorthand — `code`, `text`, `down`, `mods` at the top level
   — and `HandleRemoteInputAsync` folds it into a `RemoteInputRequest` before calling
   `RemoteService.SendInput`. Both then go through `ISessionAgentBridge.QueueInput`.
2. **`RemoteService.SendInput`** rejects `sessionStart` and `sessionStop` (a plain frame
   cannot hold a stream) and returns `accepted: false` with an explanation when
   `sessionAgent.IsConnected` is `false`. The API never accepts an event it cannot deliver.
3. **`PendingInputEvent`** carries a monotonic `sequence`. The injector does not use it
   today; it is on the wire so a future client can request a resend, and so a real input
   pipeline can deduplicate and reorder without a wire change.
4. **`SessionAgentRuntime`** converts to a `RemoteInputRequest` and enqueues it on
   `InputPump`, a `ConcurrentQueue` drained by a dedicated `AboveNormal` background thread.
   Injection therefore happens off the UI thread: a `SendInput` that blocks stalls the
   message pump, and the message pump is what keeps the tray icon and the pairing dialog
   alive. The cost is that an event queued while the desktop is locked is still injected
   into whatever window is foreground when the desktop unlocks.
5. **`KeyMap`** maps W3C `UI Events` `KeyboardEvent.code` values (`KeyA`, `Digit1`,
   `ArrowUp`, `ShiftLeft`, `F5`, `Numpad*`) to a virtual-key code and a scan code. An
   unmapped code falls back to `text` injection through `KEYEVENTF_UNICODE`, so a physical
   keyboard is at least partially usable before the mapping table is complete.
6. **The reply is asymmetric.** On success, `remote.input` produces no acknowledgement frame
   at all — silence is success. On failure, the hub sends
   `{ t: "error", code: "esi_session_agent_unavailable", message: <detail> }`.
7. **Auditing is per session, not per event.** The first accepted `mouse*` event adds the
   device to `_activeStreams`, which is what makes `/remote/session` report `active: true`.
   The stream start is audited once, because a mouse-move stream would otherwise produce
   thousands of audit rows a second.

### Coordinates

`x` and `y` are normalised 0.0–1.0 within the target `displayId`, not pixels. This is what
makes the event stream resolution-independent, and it is the property that lets a real RDP
input pipeline be added without changing the wire shape.

---

## 6. Storage layout

### Per machine — `%ProgramData%\ESI-RemotePC\`

Created by the installer with `users-modify` permissions, or on first run by the service.

| Path | Contents | Protection |
|---|---|---|
| `agent.db` | SQLite. Devices, pairing attempts, notifications, audit, upload tickets, recent files, rule state, request log | Device master secrets DPAPI `LocalMachine`, entropy `ESI-RemotePC/v1/device-secret` |
| `agent.pfx` | The TLS private key and certificate, as a PFX | DPAPI, `LocalMachine` as `LocalSystem` / `CurrentUser` in console mode, entropy `ESI-RemotePC/v1/tls` |
| `settings.json` | `SettingsDto` | Plain JSON, written to a temp file and moved into place |
| `logs\agent-YYYYMMDD.log` | Serilog, rolling daily | Plain text |
| `agent.db-wal`, `agent.db-shm` | SQLite write-ahead log | — |

`AgentOptions.SessionTokenPath` declares a fourth file, `session.token`, described in
`ISessionAgentBridge` as a per-boot token the session agent would use to authenticate back
to the service over `127.0.0.1`. **No code writes or reads it.** The reverse channel in
today's build is the pipe only, and the session agent authenticates to nothing.

`settings.json` is the only user-editable state that changes behaviour at runtime, and the
path is `--data-dir`-overridable. A corrupt file is moved aside to
`settings.json.corrupt-<epoch>` and the defaults are used, because an unreadable settings
file must not stop the agent.

### Per user — `%LOCALAPPDATA%\ESI-RemotePC\logs\`

| Path | Contents |
|---|---|
| `session-YYYYMMDD.log` | The session agent's Serilog output, 14 files retained |

Per user rather than per machine because this file records what the person at the keyboard
is doing; a `%ProgramData%` copy would be world-readable. Two processes writing one file is
how you get interleaved, unreadable logs at 3 am, so the session agent has its own.

This path is **not** reachable through `GET /api/v1/logs`, which only reads the service's
`agent-*.log` files.

### Schema

`AgentDatabase.EnsureCreated` is idempotent and runs from two places: `AgentHost.Main`
before the pipeline is configured, and `AgentHostedService.Initialise`. The first is there
because the start-up banner already reads the agent identity and several services resolve
their dependencies before the host begins running.

| Table | Purpose | Notable columns |
|---|---|---|
| `schema_version` | Applied migrations. No migration runner exists yet | `version`, `applied_at` |
| `agent_identity` | Exactly one row, `id = 1` | `device_id`, `device_name` |
| `devices` | Paired devices | `master_secret` (DPAPI blob), `key_id`, `previous_key_id`, `previous_key_valid_until`, `public_pin` |
| `pairing_attempts` | In-flight and historical pairings | `user_code`, `provisioning_secret` (plain blob), `secrets_delivered` |
| `upload_tickets` | Single-use upload grants | `ticket_id`, `max_bytes`, `consumed_at` |
| `notifications` | Raised by rules and events | `kind`, `severity`, `is_read`, `data` (JSON) |
| `audit_log` | Append-only hash chain | `chain_hash` |
| `rule_state` | Sustain timers, so a condition does not re-fire every sample | `condition_since`, `last_fired_at`, `suppress_until` |
| `recent_files` | Backs `GET /files/recent` | `accessed_at` |
| `request_log` | One row per request, short-lived | `duration_ms` |

Every connection opens with `PRAGMA journal_mode=WAL; synchronous=NORMAL;
busy_timeout=5000; foreign_keys=ON`. WAL lets the metrics writer and the HTTP path read
concurrently; the busy timeout covers the window where the service and `esirc` overlap.

`pairing_attempts.provisioning_secret` is stored as a **plain blob**, not DPAPI-protected.
It is short-lived — a pending attempt expires in 30–900 s and `PurgeExpired` removes it
hourly — and `esirc pair` has to read it from the same database the service uses, which
would fail if it were machine-scope DPAPI. That trade is worth stating: for the life of a
pending pairing, a readable copy of `agent.db` yields the provisioning secret for that
attempt. Anyone who can read it during that window and observe the `pairingId` can
complete the pairing themselves.

### Phone-side storage

`flutter_secure_storage` 11.x, defaulting to AES-GCM data encryption with RSA-OAEP key
wrapping in the Android keystore. Both the machine list and the master secrets live there
(`android-app/esi_remote/lib/src/data/machine_store.dart`). See
[`SECURITY.md`](SECURITY.md) §9.

---

## 7. Threading model

### The service

`WebApplication` with a fixed middleware order that **is** the security policy:

| # | Middleware | Why here |
|---|---|---|
| 1 | `RequestContextMiddleware` | Allocates the request id and buffers the body so the signature covers the exact bytes |
| 2 | `LocalNetworkMiddleware` | Cheap, and before any database read — an off-LAN packet never reaches the device table |
| 3 | `UseWebSockets` | Before routing |
| 4 | `UseRouting` | So authentication can tell a missing route from a rejected signature |
| 5 | `SignatureAuthenticationMiddleware` | Signature, then scope, then rate limit |
| 6 | `RequestLoggingMiddleware` | Console + `request_log` row, in a `finally` so every status is recorded |
| 7 | `ErrorEnvelopeMiddleware` | Last, so it sees every produced status |
| 8 | `UseEndpoints` | — |

Registration order in `RegisterServices` matters in two places: the pipeline above, and
`KestrelTuning`, which resolves the certificate **before** Kestrel starts so a generation
failure is in the log rather than a mystery TLS error on the first request.

Background work, all in `AgentHostedService`, all `Task.Run` and all sharing one linked
`CancellationTokenSource`:

| Loop | Period | Work |
|---|---|---|
| Session bridge supervisor | 3 s | Poll the active session, launch or tear down the session agent |
| Metrics | `metrics.sampleIntervalMs`, clamped 250–10000 | Sample, evaluate notification rules, record a gaming history point |
| Housekeeping | 5 min | Purge notifications, pairing attempts and audit rows; sweep the rate limiter; rebuild the handshake so the IP set stays current for mDNS |
| Discovery | Once at start | Start the UDP responder and the mDNS responder |

The metrics loop deliberately skips rule evaluation on its first pass, because that pass
only establishes the PDH baselines.

Request handling is Kestrel's own thread pool. Everything the request path touches is
registered as a singleton and is either stateless or lock-guarded:

| Component | Guard |
|---|---|
| `RateLimiter` | Per-bucket `lock`; buckets in `ConcurrentDictionary` |
| `RequestVerifier` | `ConcurrentDictionary` of nonce sets; no lock on the hot path |
| `SettingsStore` | `SemaphoreSlim` for writes, `Lock` for the read of `Current` |
| `GamingService` | `object` for mode transitions, `Lock` for the history list |
| `SessionAgentBridge` | `SemaphoreSlim` for connect, `volatile` fields for the state the endpoints read |
| `WebSocketHub` | `ConcurrentDictionary` of connections, `SemaphoreSlim` per connection for sends, one `_streamGate` for the single stream |
| `CertificateAuthority` | `Lock` around certificate load/generate |
| `AgentDatabase` | One connection per operation, WAL, 5 s busy timeout |

`AgentHostedService` treats a failure in any loop as fatal to the host: it logs
`LogCritical` and calls `Environment.Exit(1)`, so the SCM's recovery actions restart the
service. A service that appears to be running while doing nothing is worse than one that
is visibly stopped.

### The session agent

`[STAThread]` WinForms, `ApplicationConfiguration.Initialize`, and `Application.Run` on a
hidden `MessageLoopAnchor` form that exists only to supply a message pump. A thread-pool MTA
apartment would either fail to show the pairing dialog or show it with no working pump.

| Thread | Work |
|---|---|
| UI thread | Message pump, tray icon, the pairing dialog |
| `SessionChannel` pump | Read loop over the pipe. Commands are handled **one at a time, in order**, on this task |
| `CapturePump` | A dedicated `Thread` at `AboveNormal` priority, driving the capture loop at the requested fps |
| `InputPump` | A dedicated `Thread` at `AboveNormal` priority, draining queued input events into `SendInput` |
| `DesktopStateMonitor` | A message-only window — not a form, because the agent is normally tray-only — tracking lock state and session changes |
| Heartbeat | Periodic `heartbeat` message with the current lock and session state |

`InputPump` is bounded at **4096 queued events** and drains at most **256 per tick**. On
overflow it drops the **oldest** event and logs a warning, which is the right failure for a
latency-sensitive stream: a stalled phone must not be able to grow the queue until the agent
runs out of memory, and a stale mouse position is worth less than a current one. It also
means input is never applied in strict arrival order across an overflow.

`Application.SetUnhandledExceptionMode(CatchException)` plus `Application.ThreadException`
and `AppDomain.UnhandledException` handlers log a fault and terminate rather than dying
silently. A UI-thread exception in a per-user service is otherwise invisible from the
service side.

---

## 8. Build and publish topology

### Project graph

```
ESI.RemotePc.Protocol          net10.0-windows, library
  └── ESI.RemotePc.Platform    net10.0-windows, library
        ├── ESI.RemotePc.Agent.Core     library
        │     └── ESI.RemotePc.Agent.Service   Exe, win-x64, self-contained
        ├── ESI.RemotePc.Session        WinExe, win-x64, self-contained
        └── ESI.RemotePc.Agent.Cli      Exe, win-x64, self-contained
```

`ESI.RemotePc.Session` deliberately does **not** reference `Agent.Core`. See §1.

`ESI.RemotePc.Agent.Service` is a Worker SDK project with an explicit
`<FrameworkReference Include="Microsoft.AspNetCore.App" />`. It is used purely as a
Kestrel-hosted API surface — no MVC, no Razor, nothing beyond the standard host's own DI.

### Published executables

All three are `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`,
`EnableCompressionInSingleFile`, `RuntimeIdentifier=win-x64`, `DebugType=embedded`,
`SatelliteResourceLanguages=en`. This is a product decision, not a build preference: the
agent has to install and run on a machine with no .NET runtime, and must not break when the
machine's shared runtime is upgraded or removed. The install directory can be moved or
deleted freely.

`RuntimeIdentifiers` is deliberately **not** set in `Directory.Build.props`; declaring it at
directory level makes every restore resolve a runtime pack per RID, multiplying restore time
for no benefit during normal development. The three publishing projects declare the single
RID they ship.

### Services

| Service | Account | Start | Scope |
|---|---|---|---|
| `ESIRemotePcAgent` | `LocalSystem` | Automatic | The HTTPS listener, SQLite, telemetry, power control |
| `ESIRemotePcSession` | per-user (`obj=` empty) | Automatic | Screen capture, input injection, the pairing prompt, the tray icon |

`LocalSystem` rather than `LocalService` for the agent: it needs `SeTcbPrivilege` to obtain
a user token and `SeShutdownPrivilege` to power the machine down. Neither is available to a
virtual account.

Creation and deletion go through `sc.exe` rather than `ServiceController.Create`, because
the managed API was deprecated for creation in .NET 9 and cannot set an account, a
description or the failure-recovery actions. Everything `ServiceController` *can* do — start,
stop, status, existence — uses the managed API. `.NET 10` removed
`ServiceController.Delete` entirely.

The `ServiceController` handle is explicitly disposed before `sc delete`, because a service
marked for deletion is not actually removed until every handle to it is closed; leaving one
open strands the entry in the SCM forever.

Both services get three escalating recovery actions — `restart/5000`, `restart/15000`,
`restart/60000` with `reset= 86400`. The first is fast because most startup failures are a
transient port conflict; the third is slow because if it is still failing, waiting longer is
better than spinning.

See [`BUILD.md`](BUILD.md) for the exact build and packaging commands.

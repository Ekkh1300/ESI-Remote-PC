# ESI Remote PC — Troubleshooting

Symptom → cause → fix. Every message quoted here is a literal string from the source, so
`rg` for it in `windows-agent/src/` to find the code path that produced it.

Three tools cover most of it:

```powershell
esirc status
Get-Content "C:\ProgramData\ESI-RemotePC\logs\agent-$(Get-Date -Format yyyyMMdd).log" -Tail 80
Get-Content "$env:LOCALAPPDATA\ESI-RemotePC\logs\session-$(Get-Date -Format yyyyMMdd).log" -Tail 80
```

The third one matters more often than people expect: the **session agent writes to its own
per-user log** and it is not reachable through `GET /api/v1/logs`, which only reads the
service's `agent-*.log`.

---

## Quick index

| Area | Symptom |
|---|---|
| Discovery | [The phone finds nothing](#discovery) |
| Pairing | [The prompt never appears](#pairing) · [Pairing completes but every request is `401`](#pairing-completes-but-every-request-is-401) |
| Remote control | [`session agent not connected`](#remote-control-session-agent-not-connected) |
| Transport | [TLS errors on the phone](#tls-errors-on-the-phone) |
| Authorisation | [A route returns `esi_signature`](#a-route-returns-esi_signature) · [A file operation returns `esi_path_not_allowed`](#a-file-operation-returns-esi_path_not_allowed) |
| Telemetry | [Temperature shows as "not reported"](#temperature-shows-as-not-reported) · [FPS is null](#fps-is-null) |
| Limits | [Rate limiting](#rate-limiting) |
| Service | [The service fails to start](#the-service-fails-to-start) |

---

## Discovery

<a id="discovery"></a>

### The phone finds nothing

| Cause | How to tell | Fix |
|---|---|---|
| The agent is not running | `esirc status` shows `Stopped` | See [the service fails to start](#the-service-fails-to-start) |
| Windows Firewall is blocking UDP `48211` | `Get-NetFirewallRule -DisplayName "ESI Remote PC*"` returns nothing | Re-run the installer with the *Networking* task checked, or add the rule: `netsh advfirewall firewall add rule name="ESI Remote PC discovery (LAN)" dir=in action=allow protocol=UDP localport=48211 profile=private,domain` |
| Discovery is disabled in settings | `GET /api/v1/handshake` → `features.discovery: false` | `PUT /api/v1/settings` with `discoveryEnabled: true`, then restart the service |
| The network profile is Public | `Get-NetConnectionProfile` shows `Public` | The firewall rules are scoped to `private,domain` deliberately. Switch the profile to Private on a network you trust |
| Different subnets, or a routed/VLANed network | Both addresses are in `192.168.x.x` but different thirds | WoL and discovery are layer-2. Discovery itself will work across a router that forwards broadcast, but the agent's `LocalNetworkGuard` only allows `192.168.0.0/16`, `10.0.0.0/8`, `172.16.0.0/12` — add the subnet through `security.allowedSubnets` |
| AP / client isolation | The PC is on a guest network, the phone is not | Move one of them. Client isolation drops broadcast with no error |
| The firewall blocks the phone, not discovery | Discovery finds the PC but connecting fails | The TCP `48220` rule is missing. Add it the same way with `protocol=TCP` |
| The subnet is `169.254.x.x` | — | **Not in the default `allowedSubnets`.** `LocalNetworkGuard.IsAllowed` has an IPv6 link-local special case but no IPv4 one, so a link-local IPv4 client is rejected. Add `169.254.0.0/16` explicitly |

Test discovery from the PC itself:

```powershell
$u = New-Object System.Net.Sockets.UdpClient
$u.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket,
                          [System.Net.Sockets.SocketOptionName]::Broadcast, $true)
$u.Client.ReceiveTimeout = 3000
$p = [System.Text.Encoding]::UTF8.GetBytes('ESI_DISCOVER_V1')
$null = $u.Send($p, $p.Length, '255.255.255.255', 48211)
$e = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
[System.Text.Encoding]::UTF8.GetString($u.Receive([ref]$e))
```

A JSON handshake body is a pass. `NO-REPLY` or a socket exception is a fail, and the cause is
one of the first six rows.

### Discovery works but the connection is refused

Discovery and the API are different listeners on different ports, and the firewall rules are
separate. Discovery succeeding proves nothing about `48220`:

```powershell
Test-NetConnection -ComputerName <pc-ip> -Port 48220
```

---

## Pairing

<a id="pairing"></a>

### The prompt never appears

The dialog is shown by the **per-user session agent**, not by the service. There is no
prompt at a login screen, and no prompt if the session agent is not running.

| Cause | How to tell | Fix |
|---|---|---|
| Nobody is logged in | `POST /pair/request` returns `approval: "browser-or-cli"` | Log in, then retry — the prompt is the only route that needs nothing but a user. Otherwise the three-step console sequence below |
| The phone is on a headless PC and the QR has nowhere to be shown | Same as above, and there is no display to open the pairing page on | `esirc pair url <code>` prints the URL the QR would have carried; paste it into the phone's *Paste* box on the pairing screen |
| The session agent is not running | `GET /api/v1/remote/session` → `sessionAgentState` is not `Ready` | See [remote control](#remote-control-session-agent-not-connected) |
| The dialog was denied or dismissed | Poll returns `denied` or `expired` | Retry. Deny is the default button and every other exit path is a denial, by design |
| The service is running and `esirc pair approve` refuses | `The service is Running, and 'approve' rewrites pairing state…` | All three, in order: `sc stop ESIRemotePcAgent`, `esirc pair approve <code>`, `sc start ESIRemotePcAgent`. The phone shows a connection error for a few seconds and recovers on its own |

There is deliberately no way to approve from a browser. The `/pairing` page is reachable by
anything on the LAN and the user code is six digits, so a page that could approve would let
anyone approve a pairing they started themselves. See `SECURITY.md` §12.

Both `pair url` and `pair approve` accept `--data-dir` and `--port`, for when the agent was
not started by the installer and so is not the one under `ProgramData`:

```
esirc pair url 418207 --port 48333 --data-dir D:\esi-test
``` |
| The code was mistyped | `esirc pair approve` prints `No pending pairing matches the code` | The code expires in 30–900 s. Start over |
| `maxPairedDevices` reached | `409 esi_conflict`, "The maximum number of paired devices (8) has been reached" | `esirc pair revoke <deviceId>` or `DELETE /api/v1/devices/{deviceId}` |
| The `userCode` is stale | `esirc pair pending` lists it as expired | The list only shows unexpired attempts. Retry from the phone |

### Pairing never completes — the provisioning secret

**This is the one gap that affects users directly in the current build.**

`GET /api/v1/pair/session` keeps returning

```json
{ "status": "error" }
```

when the poll is missing a valid 32-byte `provisioningSecret` as the `secret` query
parameter, or carries one of the wrong length. `PairingManager.Poll` logs:

```
Pairing <id> was polled without a valid provisioning secret; the secrets will not be released
```

`PROTOCOL.md` §5.4 says that secret is shown on the PC approval prompt and encoded in a QR
code. **The Windows Agent does not render a QR code and does not display the secret.** The
prompt shows the device name, platform, source address, versions and a six-digit code; the
tray menu has *Open pairing page*, *Remote control status* and *Exit*. The pairing page at
`/pairing` shows the device name, id, SPKI pin and API base — no secret.

The phone's parser (`lib/src/protocol/pairing_qr.dart`) is ready for a QR that nothing
produces; its own comment says so. Until an emitter exists, pairing cannot complete through
the shipped agent regardless of how carefully the user follows the flow.

If the phone and PC are otherwise paired — for example, the database was populated by a
different build — and every request then fails, see the next section.

### Pairing completes but every request is `401`

| Cause | How to tell | Fix |
|---|---|---|
| Wrong clock on the phone | `401 esi_timestamp`: "The request timestamp is 4213s away from the agent's clock" | Turn on automatic date and time on both devices. The window is ±300 s by default |
| The master secret was lost | `401 esi_unauthorized`: "The stored secret for this device is unusable. The device must be re-paired." | `agent.db` was copied from another machine, so DPAPI cannot unwrap it. `esirc pair revoke <deviceId>` and pair again |
| The device was revoked | `401 esi_unauthorized`: "The device id is not paired with this agent." | Pair again |
| The device was disabled | `403 esi_forbidden_scope`: "This device has been disabled on the PC." | Re-enable it, or pair again |
| The TLS identity was reset | Every device is simultaneously rejected, and `/handshake` shows a new `spkiSha256` | `POST /api/v1/settings/reset` deletes the certificate and invalidates everyone. Re-pair every device |
| A key rotation was never delivered | `401 esi_signature` right after a rotation | The `wrappedSecrets` envelope was never applied by the phone. See [`SECURITY.md`](SECURITY.md) §6 — there is no automated recovery path |
| The phone's clock is fine but the nonce is stale | `401 esi_replay` on a request that was retried | A retried request reuses its nonce. Generate a fresh nonce per attempt |

---

## Remote control: session agent not connected

<a id="remote-control-session-agent-not-connected"></a>

The exact message from `RemoteService.SendInput`:

> Remote input needs the per-user session agent, which is not running. Log in to the PC (or
> start 'ESI Remote PC Session') and try again.

`POST /api/v1/remote/input` returns `409` with that in `detail`. `GET /api/v1/remote/session`
gives the state:

| `sessionAgentState` | Meaning |
|---|---|
| `Ready` | Connected and heartbeating. Input will work |
| `Starting` | A session exists; the bridge is trying to connect |
| `NotRunning` | The channel pump stopped |
| `NoUserSession` | Nobody logged in, or it did not connect within 15 s |
| `Error` | The supervisor could not reach the session agent |

Diagnose in this order.

**1. Is anybody logged in?** `NoUserSession` at a login screen is the correct answer. There
is no desktop to inject into.

**2. Is the per-user service there and running?**

```powershell
sc.exe query ESIRemotePcSession
```

It is registered at install time but only **starts at logon**, so `Stopped` immediately
after a reboot is normal. `not installed` means re-run `esirc install` without
`--no-session-agent`.

**3. Is `SeTcbPrivilege` present?**

```powershell
reg query "HKLM\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent" /v requiredPrivileges
```

Expected: `SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege`.

A missing or wrong value is the single most common cause. Without it the privilege exists
in the service account's token but is **disabled**, so the first `CreateProcessAsUserW`
fails and the agent can only be started by the per-user service. The service log says:

```
SeTcbPrivilege could not be enabled: <error>
```

or

```
WTSQueryUserToken was denied. Confirm the agent runs as LocalSystem with SeTcbPrivilege.
```

**4. Is the agent running as LocalSystem?**

```powershell
curl.exe -k https://localhost:48220/api/v1/health
```

`serviceAccount` must be `NT AUTHORITY\SYSTEM` and `isElevated` `true`. `sessionId: 0`
confirms the session-0 service answered rather than the session agent.

**5. Can the pipe be reached?** The DACL grants the console session's own account
`ReadWrite | Synchronize` and nobody else. If the SID cannot be resolved:

```
Could not resolve the account for session <id>; the session agent will not be able to
connect to the command pipe. This is expected in a container or a session with no cached
profile.
```

The session agent's log repeats it as `The command channel is not available yet` and retries
every 3 s.

**6. Is the binary where the bridge expects it?**

```
Session agent binary not found at <path>; relying on the logon task
```

`SessionAgentPath` is empty unless `--session-agent <path>` was passed. Run the agent by
hand with it set:

```powershell
.\ESI.RemotePc.Agent.Service.exe --session-agent "C:\Program Files\ESI Remote PC\ESI.RemotePc.Session.exe"
```

**7. Read the session agent's log.**

```
%LOCALAPPDATA%\ESI-RemotePC\logs\session-YYYYMMDD.log
```

**8. The tray icon.** A visible "ESI Remote PC" tray icon means the process is alive.
Double-click it for a status box: service channel, Windows session id, signed-in user,
workstation lock state, and whether capture is running. A tooltip of
`ESI Remote PC - waiting for the agent service` means the process is up but the pipe is not
connected — go to step 5.

### Related messages

| Message | Cause |
|---|---|
| `Media keys must be sent from the interactive desktop. Log in to the PC, or start 'ESI Remote PC Session'.` | `POST /api/v1/media/play` and the other transport keys, `409`. Volume and mute work without the session agent |
| `The session agent is not connected, so the screen cannot be captured.` | `remote.state` with `active: false` after `remote.start` |
| `Another device is already streaming this screen.` | `esi_conflict`. `WebSocketHub` serves one screen to one device. Disconnect the other, or `DELETE /api/v1/remote/session` |
| `Start a screen stream with remote.start over the WebSocket, not a plain request.` | `POST /api/v1/remote/input` with `kind: sessionStart` or `sessionStop` |
| The screen is black, or frozen after a while | The GDI provider does a `BitBlt` of the monitor DC. It works on RDP sessions and VMs but captures the primary display's composition; a secured desktop (UAC prompt, Ctrl+Alt+Del) is not capturable by design |

### What still works without the session agent

Shutdown, restart, force restart, sleep, hibernate, telemetry, metrics, the file manager,
the app launcher, gaming mode, notifications, settings, logs and audit. Only remote control,
`lock`, `signout`, the media transport keys, desktop toasts and the pairing prompt need it.

Note that `GET /api/v1/power/status` reports `isLocked: false` whenever the session agent is
not connected. That is a known unknown, not a report that the desktop is unlocked.

---

## TLS errors on the phone

<a id="tls-errors-on-the-phone"></a>

| Symptom | Cause | Fix |
|---|---|---|
| `HandshakeException`, `CERTIFICATE_VERIFY_FAILED` | The certificate is self-signed and untrusted by the OS | **Expected.** The design is SPKI pinning, not CA validation. The client must accept the pinned SPKI and nothing else |
| The pin in the app does not match `/handshake` | Wrong data directory, or the certificate was regenerated | Compare `GET /api/v1/handshake` → `spkiSha256` with the pin on `/pairing`. They must be identical on the same machine |
| The pin changed on its own | `agent.pfx` was deleted, expired, or could not be unwrapped | The agent logs `Generated a new TLS identity` or `The stored TLS certificate has expired`. Every paired device must re-pair. That is the intended fail-safe |
| Certificate expired | 825-day validity, no auto-renewal | Delete `agent.pfx` with the service stopped, start it, read the new pin, re-pair |
| `SSL_ERROR_SYSCALL`, immediate close, on a machine that worked before | The agent started without TLS | Look for `No TLS certificate is available; listening on plaintext HTTP` in the log. TLS generation failed — see [the service fails to start](#the-service-fails-to-start) |
| Works on the PC, fails from the phone | The SAN does not cover the address used | The certificate has `IP:` SANs for the addresses present **when it was generated** and is never reissued. Connecting by IP that has since changed fails hostname validation. Connect by the pinned IP, or re-issue |
| The phone rejects a `.local` name | mDNS is not resolving | `esiremotepc-<port>.local` and `<ComputerName>.local` are in the SAN. If the resolver does not answer for `_esiremotepc._tcp.local.`, use the IPv4 address |

The pin is `base64(SHA-256(DER(SubjectPublicKeyInfo)))` and is shown on `/pairing`. To read
it without a client:

```powershell
curl.exe -k https://localhost:48220/api/v1/handshake
```

`PROTOCOL.md` §2.1 says the certificate is re-generated when the address set changes. It is
not. That is a real gap; see [`API.md`](API.md) §15.

---

## A route returns `esi_signature`

```json
{ "error": { "code": "esi_signature", "message": "The request signature is not valid.",
             "requestId": "…" } }
```

`401`. The `requestId` appears in the service log on the same line as the request. Note the
code is the same for two different failures:

| `message` | Cause |
|---|---|
| `The request signature is not valid.` | The HMAC over the canonical string does not match |
| `The request body does not match X-ESI-Content-Sha256.` | The body arrived different from the bytes that were signed |

| Cause of `The request signature is not valid.` | Fix |
|---|---|
| The query string was not sorted before signing | Segments are sorted ordinally, whole-segment, duplicates preserved. See [`PROTOCOL.md`](PROTOCOL.md) §4.2 |
| The path was URL-decoded before signing | Use the raw path. No decoding, no normalisation |
| The method was not uppercased | `BuildCanonicalString` uppercases; the client must too |
| The device id was not lowercased | Both the canonical string and the key derivation lowercase it |
| A body of `null` was signed instead of `""` | The empty body hashes to `47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuGU=` |
| Trailing whitespace in the canonical string | Exactly 8 LF-separated lines, **no trailing newline** |
| An HTTP client rewrote the query | A proxy or a library that re-encodes query parameters changes the canonical target |
| Clock skew on the phone | That is `esi_timestamp`, not `esi_signature`. Fix the clock first and see the next row |
| A key rotation happened and the phone never applied the envelope | See [`SECURITY.md`](SECURITY.md) §6 |
| The body exceeded `MaxSignedBodyBytes` (8 MiB) | The body is not buffered, so it cannot be signed. Use an upload ticket |

### `esi_timestamp` and `esi_replay`

```json
{ "error": { "code": "esi_timestamp", "message": "The request timestamp is 4213s away from the agent's clock, outside the 300s window. Check the clock on both devices." } }
```

```json
{ "error": { "code": "esi_replay", "message": "This nonce has already been used." } }
```

`esi_replay` on a request that was retried: **generate a fresh nonce per attempt.** A retry
that reuses the nonce is a replay by definition, and the verifier cannot tell the two apart.

`esi_keyid` means `keyId` is neither the current generation nor a live previous one.

All four are `401` and all four are audited as `auth.failed` with the offending header value
in `detail`.

---

## A file operation returns `esi_path_not_allowed`

```json
{ "error": { "code": "esi_path_not_allowed", "message": "The path is outside every allowed file root: C:\\Windows\\System32\\config\\systemprofile\\Desktop", "requestId": "…" } }
```

`403`. The sandbox is doing its job, or the request is malformed. `FileSandbox` rejects
these outright, before any normalisation:

| Message fragment | What was sent |
|---|---|
| `Device and extended-length paths are not permitted.` | `\\?\C:\...` or `\\.\C:\...` |
| `UNC paths are not permitted.` | `\\server\share\...`, or a `\\?\UNC\...` that the kernel resolved |
| `Alternate data streams are not permitted.` | `file.txt:hidden` |
| `The reserved device name 'NUL' is not permitted.` | `NUL`, `CON`, `PRN`, `AUX`, `COM1`–`COM9`, `LPT1`–`LPT9`, with or without an extension |
| `The path is outside every allowed file root: …` | A junction or symlink component that resolves elsewhere, or any path outside the roots |
| `No file roots are configured, so the file manager is disabled.` | `files.allowedRoots` is empty or every configured root does not exist |
| `The path does not exist: …` | `mustExist: true` and nothing is there |

### The most common cause: a relative path

`PROTOCOL.md` §6.6 says a relative path is "rooted at the default root". It is not.
`FileSandbox.NormalizeInput` roots a relative path at
`Environment.SpecialFolder.UserProfile`, and the service runs as `LocalSystem`, so that
resolves to the **system profile**:

```
C:\Windows\System32\config\systemprofile\Desktop
```

Send absolute paths. `GET /api/v1/files/roots` gives you the exact strings to use, already
canonicalised.

### Other causes

| Symptom | Cause | Fix |
|---|---|---|
| `403 esi_forbidden_scope`: `The root containing … is read-only.` | The target is in a `readOnly` root | `GET /api/v1/files/roots` → `readOnly` per root |
| `403 esi_forbidden_scope`: `Windows denied access: …` | NTFS ACLs, not the sandbox | The service runs as `LocalSystem`, so this is unusual. Check the folder's permissions |
| `403 esi_forbidden_scope`: `That folder is the root of an allowed area and cannot be renamed.` | Renaming or deleting a root, or an ancestor of one | Move the contents instead |
| `200` with `{"ok": false, "detail": "Deleting is disabled in the agent settings."}` | `files.allowDelete` is `false` | **This is not an error status.** Branch on `ok`, not on the status code |
| `200` with `{"ok": false, "detail": "Rename is disabled by configuration."}` | The same flag governs rename and move | As above |
| `404` on a path that exists | It is outside every root, and `ResolveFile`/`ResolveDirectory` threw | `GET /api/v1/files/roots` |
| A junction inside a root silently disappears | The root was reconfigured since the entry was recorded | `/files/recent` re-validates on read and drops such entries |

`Endpoints.Guard` maps `SandboxViolationException` to `403 esi_path_not_allowed`,
`UnauthorizedAccessException` to `403 esi_forbidden_scope`, `DirectoryNotFoundException` and
`FileNotFoundException` to `404 esi_not_found`, and `IOException` to `500 esi_internal`.

---

## Temperature shows as "not reported"

The agent never fabricates a sensor value. A value is either measured by something
identified, or absent with a note. `GET /api/v1/system/info` → `sensors`:

```json
{ "sensors": {
    "cpuTemperatureC": 61.5,
    "gpuTemperatureC": null,
    "cpuFanRpm": 2200,
    "gpuFanRpm": null,
    "sensorsAvailable": ["cpu.temperature"],
    "notes": {
      "cpu.temperature": "…",
      "gpu.temperature": "No GPU temperature source is available. Windows has no general-purpose GPU temperature API: this is populated through NVML on NVIDIA cards, or a hardware-monitor driver on other vendors.",
      "cpu.fan": "…",
      "gpu.fan": "No GPU fan speed is reported by NVML (cards without a fan report nothing).",
      "gpu.load": "…"
    } }
```

Read `notes` — it names the specific reason, per sensor.

| `notes` key | Empty or absent when | Fix |
|---|---|---|
| `cpu.temperature` | No trustworthy CPU thermal source on this machine | Laptop or desktop with no readable thermal sensor, or a virtual machine. Nothing to configure |
| `gpu.temperature` | Not NVIDIA with working NVML, and no hardware-monitor driver | Install the vendor's utility. Intel needs IGL, AMD needs ADLX. On a plain Windows install there is no GPU temperature API at all |
| `cpu.fan` | No fan RPM source | Common on laptops and on desktops with no fan |
| `gpu.fan` | A card without a fan, or no NVML | The value is a **percentage of maximum**, not RPM, even though the DTO field is named `gpuFanRpm` |
| `gpu.load` | Neither the `\GPU Engine(*)` PDH counters nor NVML | The counters are missing on some driver and hardware combinations. Game Bar / Task Manager can still show load, but the agent has no other route |

`sensorsAvailable` lists the sensors that produced a value, e.g. `["cpu.temperature"]`.
`gpu[].source` on `/system/metrics` says where each GPU number came from: `nvml`,
`intel-igcl`, `amd-adlx`, `pdh`, or `unavailable`.

If a screen shows a plausible-looking number that the API did not supply, the client is
inventing it. `TelemetrySource` is on the wire precisely so that a UI can tell the user which
sensor a value came from.

---

## FPS is null

`GET /api/v1/gaming/fps`:

```json
{ "fps": null, "note": "Windows exposes no supported API for the presented frame rate, so this is null rather than a guess." }
```

This is correct on every machine. `GamingService.MeasureFps` returns `null` unconditionally.
`fps` in `GET /api/v1/gaming/state` and in every `history[]` point is `null` for the same
reason. `PROTOCOL.md` §6.9 shows `"fps": 144, "fpsSource": "dwm-present-rate"`; neither
field is ever populated.

`pingMs` in the same response **is** measured — a single ICMP echo to the primary interface's
gateway with a 1 s timeout. It is `null` when there is no gateway or the ping fails.

`gpuUsagePercent`, `cpuUsagePercent`, `ramUsedPercent` and `gpuTemperatureC` are all real
measurements.

If the app shows an FPS number, it is a client-side estimate and should be labelled as one.

---

## Rate limiting

```json
{ "error": { "code": "esi_rate_limited", "message": "Rate limit reached. Retry in 3 second(s)." } }
```

`429`, with a `Retry-After` header. Audited as `rate.limited`.

| Category | Sustained | Burst |
|---|---|---|
| `api`, per device | 240 / min | 60 |
| `api`, per address | 480 / min | 120 |
| `power`, per device | 10 / min | 3 |
| `unauth` (public routes) | 30 / min | 10 |
| `pair` (pairing requests) | 5 / min | 2 |
| `upload`, per address | 480 / min | 120 |

Retry on `Retry-After`, and honour it — a dashboard that polls four times a second from
several components will exhaust the per-device bucket.

To tell which limit was hit:

| `message` | Bucket |
|---|---|
| `Rate limit reached. Retry in N second(s).` | Per device. `Retry-After` is computed from the actual bucket |
| `Too many requests from this address.` | Per address. `Retry-After` is a flat `5` |
| `Too many unauthenticated requests.` | The public-route bucket |
| `Too many upload attempts from this address.` | The upload bucket |
| `Too many pairing attempts from this address. Try again later.` | The pairing bucket. See below |

**The pairing bucket is not "5 per hour".** Both `RateLimiter.PairingPerHour`'s name and
`PROTOCOL.md` §3 say five per hour; the implementation refills at
`PairingPerHour / 60000` tokens per millisecond, so the steady state is **five per minute**
capped at a burst of two, and `Retry-After` is one or two seconds. A phone retrying a
failed pairing will not be locked out for an hour.

Raise the API budget through `PUT /api/v1/settings` → `security.rateLimitPerMinute`
(10–100000). The per-address bucket is always twice the per-device one and cannot be
configured separately.

---

## The service fails to start

<a id="the-service-fails-to-start"></a>

```powershell
esirc status
sc.exe query ESIRemotePcAgent
Get-Content "C:\ProgramData\ESI-RemotePC\logs\agent-$(Get-Date -Format yyyyMMdd).log" -Tail 80
```

| Symptom | Cause | Fix |
|---|---|---|
| `not installed` | Never registered, or deleted | `esirc install` |
| `Stopped`, start type `Disabled` | Start type changed | `sc config ESIRemotePcAgent start= auto` |
| `StartPending` forever | Port `48220` already bound | `Get-NetTCPConnection -LocalPort 48220 -State Listen` — stop the owner, or change `port` in `settings.json` and restart |
| No log file at all | The data directory is not writable by `LocalSystem` | The service creates the directory before logging, so no file means it could not. Check the ACL on `C:\ProgramData\ESI-RemotePC` |
| `Could not initialise the database at …` | The database is corrupt, or a copy from another machine | DPAPI blobs cannot be unwrapped off-machine, so a copied `agent.db` is not a valid backup. Stop the service, move `agent.db` aside, start it. Every device must re-pair |
| `No TLS certificate is available; listening on plaintext HTTP` | Certificate generation failed | See below |
| `The agent terminated unexpectedly` | An unhandled exception at start | The full stack trace is the next line in the log |
| `The agent's background loop failed. Exiting so the service can recover.` | A background loop died | `Environment.Exit(1)` is deliberate, so the SCM's recovery actions run. Read the exception above it |
| Exits immediately with no message | The binary was replaced while the service was running | `sc stop`, then `sc start` |
| `1053 The service did not respond in a timely manner` | Startup took over 30 s | Usually first-run certificate generation on a slow disk, or a very large `agent.db` |

### TLS generation failed

The log contains:

```
Could not generate a TLS certificate. The agent will start with HTTP only, which
means request signing is the only protection in place.
```

and, if TLS was requested but not obtained:

```
TLS was requested but the certificate could not be created. Traffic will be plaintext;
only request signing protects it.
```

Causes in likelihood order:

1. **Not running as LocalSystem.** `CertificateAuthority` detects `S-1-5-18`; without it the
   private key goes in the *user* key store, which the machine-scope path denies. Check
   `serviceAccount` and `isElevated` in `GET /api/v1/health`.
2. **`agent.pfx` is corrupt or was copied from another machine.** `Load()` catches the
   DPAPI failure, logs `Could not load the stored TLS certificate; generating a replacement`,
   and generates a new one. **This changes the pin and invalidates every paired device** —
   which is the correct and safe failure mode, not a bug to work around.
3. **The data directory is not writable.** `Generate()` writes the PFX with
   `File.WriteAllBytes`.

There is no in-memory fallback. Kestrel requires a certificate with a **persisted** private
key; an `EphemeralKeySet` certificate is rejected outright, which is why the key is placed
in a key store with `PersistKeySet` rather than generated per start.

### After a repair

```powershell
sc.exe failure ESIRemotePcAgent
```

Expected: `reset= 86400 actions= restart/5000/restart/15000/restart/60000`. Re-arm with:

```powershell
sc.exe failure ESIRemotePcAgent reset= 86400 actions= restart/5000/restart/15000/restart/60000
```

`esirc install --no-recovery` skips this, so a service installed with it has no automatic
recovery from a crash.

---

## Protocol mismatches

| Symptom | Cause | Fix |
|---|---|---|
| `404 esi_not_found` with "This usually means the phone and the agent disagree about the protocol version" on a route you believe exists | A route the agent does not have | Check [`API.md`](API.md) §15. `GET /api/v1/system/storage` and `POST /api/v1/auth/activate` are in `PROTOCOL.md` but **not implemented** |
| `400` with an empty body | Should not happen — `EsiProblemDetailsService` replaces the default writer | If you see one, the response was produced by a proxy. Check `X-ESI-Request-Id` is present |
| `415` on a POST with a body | No `Content-Type: application/json` | The agent requires it for body-bound routes |
| `400` with `details` naming a field | Model bind failure | The `details` map names the offending field and why |
| The phone greys out a feature | `features` in `/handshake` | `remote` is `false` when the session agent is disconnected; `fileManager` when no roots are configured; `appLauncher` when no apps are configured |
| A `404` for `/` | Expected as a service | `/` only maps in console mode. Use `/pairing` |

---

## Diagnostics reference

### Commands

| Command | Shows |
|---|---|
| `esirc status` | Data directory, pairing URL, both services' state and start type, newest log and its age |
| `esirc pair show` | Device id, name, scopes, last seen, state (`ok` / `disabled` / `rotating`), last address and key id |
| `esirc pair pending` | Pending pairing codes and time remaining |
| `esirc version` | `esirc 1.0.0 (protocol v1)` |
| `curl.exe -k https://localhost:48220/api/v1/health` | Uptime, service account, elevation, session id, debug mode |
| `curl.exe -k https://localhost:48220/api/v1/handshake` | Protocol version, pin, features, pairing state |
| `Get-Content "$env:LOCALAPPDATA\ESI-RemotePC\logs\session-*.log" -Tail 80` | The session agent's own log |
| `Get-NetTCPConnection -LocalPort 48220` | Who holds the port |
| `Get-NetFirewallRule -DisplayName "ESI Remote PC*"` | Whether the LAN rules exist |
| `powercfg /devicequery wake_armed` | Adapters Windows considers armed for WoL |

### Log locations

| Path | Contents |
|---|---|
| `C:\ProgramData\ESI-RemotePC\logs\agent-YYYYMMDD.log` | The service. Also served by `GET /api/v1/logs` (admin scope) |
| `%LOCALAPPDATA%\ESI-RemotePC\logs\session-YYYYMMDD.log` | The session agent. 14 files retained. **Not** reachable over the API |

The session agent's log is per user because it records what the person at the keyboard is
doing; a `%ProgramData%` copy would be world-readable. Two processes writing one file is
how you get interleaved, unreadable logs at 3 am, so the session agent has its own.

### Correlating a failure

Every response carries `X-ESI-Request-Id`, and the same value appears as `requestId` in an
error body. The service log line for it uses the template
`{Method} {Path} -> {Status} in {Elapsed}ms from {Remote} as {Device}`, with the level
derived from the status (4xx is `WRN`, 5xx is `ERR`):

```
2026-09-29 14:03:11.204 +02:00 [WRN] Esi.Service.Middleware: GET /api/v1/audit -> 403 in 1ms from 192.168.1.77 as 3f2a9c17d4b8e206
```

The request id is in the same line only if the message template carried it; it does not, so
use the `X-ESI-Request-Id` header and the `request_log` table, or grep on
`(ts, method, path, device_id, remote, status, duration_ms)` in `agent.db`. The audit row
carries the full detail:

```
GET /api/v1/audit: esi_forbidden_scope — This device is not permitted to use /api/v1/audit (needs the Admin scope).
```

### Getting a verbose log

```powershell
sc.exe stop ESIRemotePcAgent
.\ESI.RemotePc.Agent.Service.exe --debug --data-dir "C:\ProgramData\ESI-RemotePC"
```

`--debug` sets Debug-level logging for the process. In console mode the start-up banner is
printed, including the data directory, the log directory, the SPKI pin and the active file
roots — which is the fastest way to confirm the agent is reading the data directory you
think it is.

Note that a console instance and the service **cannot run at once on the same port**. Stop
the service first.

---

## Related

| | |
|---|---|
| Wire protocol and error codes | [`PROTOCOL.md`](PROTOCOL.md) |
| HTTP and WebSocket surface | [`API.md`](API.md) |
| Installation and first run | [`INSTALL.md`](INSTALL.md) |
| Wake-on-LAN | [`WOL_SETUP.md`](WOL_SETUP.md) |
| Security model and limitations | [`SECURITY.md`](SECURITY.md) |
| Building and testing | [`BUILD.md`](BUILD.md) |

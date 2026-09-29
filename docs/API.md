# ESI Remote PC — HTTP and WebSocket API

The complete surface the Windows Agent actually serves, generated from the route
registrations in `windows-agent/src/ESI.RemotePc.Agent.Service/Endpoints.cs` and the scope
table in `Middleware.cs`.

[`PROTOCOL.md`](PROTOCOL.md) is normative for the wire format, the signing scheme and the
key hierarchy. This document is normative for **which routes exist, what scope each
requires, and what each one returns**. Where the two disagree, §12 says so explicitly
rather than silently picking one.

---

## 1. Conventions

| | |
|---|---|
| Base path | `/api/v1` (`ProtocolConstants.ApiBase`) |
| WebSocket | `/ws` — outside the base path |
| HTML page | `/pairing` — outside the base path |
| Default HTTPS port | `48220` |
| Content type | `application/json; charset=utf-8` on every request and response except the file download and upload streams |
| Timestamps | Unix epoch seconds, UTC |
| Null handling | `null` properties are omitted from responses (`DefaultIgnoreCondition = WhenWritingNull`). A missing field and an explicit `null` are the same thing on the wire |
| Enum handling | Serialised as strings (`JsonStringEnumConverter`), case-insensitive on input |
| Lenient reading | Property names are case-insensitive, numbers may arrive as strings, comments and trailing commas are accepted |

Every response carries `X-ESI-Request-Id`, which is also the `requestId` field in error
bodies. Use it to find the line in the agent log.

### Error envelope

Every error, including framework-generated ones, is:

```json
{ "error": { "code": "esi_signature", "message": "The request signature is not valid.", "requestId": "f3Q8Zl0aW1kJ7w3K" } }
```

| Code | Status | Raised when |
|---|---|---|
| `esi_bad_request` | 400 | Body bind failure, missing required field, malformed input |
| `esi_unauthorized` | 401 | Missing/unknown device, missing headers, or an upload ticket problem |
| `esi_signature` | 401 | HMAC mismatch, or `X-ESI-Content-Sha256` does not match the body |
| `esi_replay` | 401 | Nonce already used by this device |
| `esi_timestamp` | 401 | Timestamp outside the clock-skew window |
| `esi_keyid` | 401 | `keyId` is neither the current generation nor a live previous one |
| `esi_forbidden_network` | 403 | Source address is not on an allowed subnet |
| `esi_forbidden_scope` | 403 | Device lacks the required scope, or the device is disabled, or Windows denied the file |
| `esi_path_not_allowed` | 403 | Path escaped the sandbox, or hit a rejected form |
| `esi_not_found` | 404 | No such route, no such process, no such pairing, no such device, or an unknown `{action}` |
| `esi_rate_limited` | 429 | Token bucket exhausted. `Retry-After` is set |
| `esi_conflict` | 409 | Refused action — another device is streaming, the session agent is offline, a launch failed |
| `esi_session_agent_unavailable` | (WS frame) | Input could not be delivered to the session agent |
| `esi_unsupported` | (WS frame) | Unknown frame type |
| `esi_internal` | 500 | Unhandled exception, or an `IOException` from the file service |

`EsiProblemDetailsService` replaces ASP.NET Core's automatic `ProblemDetails` writer so a
model-bind failure still produces this envelope. Minimal API's default 400 has no body and
would be indistinguishable from a transport failure on the client.

### Scope enforcement

`RequestSignatureValidator.RequiredScope(path, method)` is a prefix table:

| Path prefix | `GET`/`HEAD` | Everything else |
|---|---|---|
| `/api/v1/auth/rotate`, `/api/v1/devices`, `/api/v1/settings`, `/api/v1/logs`, `/api/v1/audit` | `admin` | `admin` |
| `/api/v1/files` | `files` | `files` |
| `/api/v1/power`, `/api/v1/apps`, `/api/v1/media`, `/api/v1/remote`, `/api/v1/gaming` | `read` | `control` |
| *everything else* | `read` | `read` |

Scopes are the flags enum `Read | Files | Control | Admin`. `esirc pair approve` grants
`Read | Control | Files` (no `admin`); a device with `admin` is normally a second paired
device.

**Two consequences of the table that are worth knowing before relying on it — see §13.**

---

## 2. Public surface

Reachable without a signature. Subject to `LocalNetworkMiddleware` and to a tight
per-address rate limit (30/min sustained, burst 10).

### `GET /api/v1/handshake`

Identity, protocol version, ports, TLS pin, pairing state. Also returned verbatim as the
body of a UDP discovery reply and embedded in the mDNS TXT record.

| Field | Type | Notes |
|---|---|---|
| `protocolVersion` | int | `1` |
| `agentVersion` | string | From the entry assembly's version |
| `deviceId` | string | The **agent's** id, 16 lowercase hex. Stable across restarts, stored in `agent_identity` |
| `deviceName` | string | From settings, else the machine name |
| `hostName` | string | `Environment.MachineName` |
| `httpsPort` | int | Configured port |
| `discoveryPort` | int | Configured UDP port |
| `spkiSha256` | string? | Base64 SHA-256 of the DER SubjectPublicKeyInfo. `null` if TLS is off |
| `tlsSubject` | string? | e.g. `CN=META-PC` |
| `notBefore`, `notAfter` | int? | Certificate validity, epoch seconds |
| `serverTime` | int | Used to correct the phone's clock |
| `pairingRequired` | bool | No enabled devices |
| `pairingOpen` | bool | Fewer devices than `maxPairedDevices` |
| `maxPairedDevices` | int | Default 8 |
| `pairedDeviceCount` | int | Count of **enabled** devices |
| `features` | object | See below |

`features`:

| Key | True when |
|---|---|
| `power`, `metrics`, `media`, `gaming`, `notifications` | Always `true` |
| `discovery` | `settings.discoveryEnabled` |
| `mdns` | `settings.mdnsEnabled` — **not listed in `PROTOCOL.md` §3.1** |
| `wakeOnLan` | `settings.wakeOnLanEnabled` |
| `fileManager` | At least one allowed file root |
| `appLauncher` | At least one configured app |
| `remote` | The session agent is connected |
| `remoteAccess` | Always `false` — deliberately not implemented in v1 |

### `GET /api/v1/health`

Liveness and the identity of the process answering.

```json
{ "status": "ok", "version": "1.0.0", "uptimeSeconds": 3600, "serverTime": 1756000000,
  "serviceAccount": "NT AUTHORITY\\SYSTEM", "isElevated": true, "sessionId": 0, "debugMode": false }
```

`sessionId: 0` confirms the request was answered by the session-0 service, not the session
agent.

### `POST /api/v1/pair/request`

The only unauthenticated write. Rate limited to 5 tokens per minute per source address,
burst 2 (see [`SECURITY.md`](SECURITY.md) §7 — this is not "5 per hour").

**Request**

```json
{ "deviceName": "Pixel 9", "platform": "android", "osVersion": "16", "appVersion": "1.0.0",
  "publicSpkiSha256": "…" }
```

`publicSpkiSha256` is the phone's own pin. It is stored on the pairing attempt and on the
device record. It is **not** verified by the agent — the agent has no client-certificate
mechanism.

**Response — `201 Created`**, `Location: /api/v1/pair/session`

```json
{ "pairingId": "b21f…", "userCode": "418207", "expiresAt": 1756000120,
  "approval": "desktop-prompt", "pollIntervalMs": 1000 }
```

| `approval` value | Meaning |
|---|---|
| `desktop-prompt` | A session agent is connected and a modal dialog has been posted |
| `tray-or-cli` | No interactive session. The attempt stays pending; approve with `esirc pair approve <code>` |

`409` with `esi_conflict` when `maxPairedDevices` is reached.

### `GET /api/v1/pair/session`

| Query | Required | Notes |
|---|---|---|
| `id` | yes | The `pairingId` |
| `secret` | for secrets | Base64 of the 32-byte provisioning secret |
| `code` | no | A six-digit `userCode`. **Only a validity check** — if it does not match any attempt the call is `404`, but the poll still uses `id`. It is not an alternative selector |

**Response — `200`**

```json
{ "status": "pending" }
{ "status": "denied" }
{ "status": "expired" }
{ "status": "consumed" }
{ "status": "unknown" }
{ "status": "error" }
{ "status": "approved",
  "deviceId": "3f2a9c17d4b8e206", "keyId": 1,
  "wrappedSecrets": { "nonce": "…", "ciphertext": "…", "tag": "…" } }
```

| Status | Cause |
|---|---|
| `pending` | Waiting for a human |
| `denied` / `expired` | The human said no, or the window closed |
| `approved` | Secrets released. **Exactly once** — the delivery is claimed with a conditional `UPDATE` before the envelope is produced |
| `consumed` | A second poll after `approved` |
| `unknown` | No such `pairingId`, or an unrecognised stored state |
| `error` | Approved but `secret` was absent or not 32 bytes, or the device record has gone |

`PairStatusResponse` also declares `spkiSha256`. `PairingManager.Poll` never populates it,
so it is always absent. The pin comes from `/handshake`.

**The provisioning secret.** Without it the agent approves the attempt but will never
release the envelope — `status: "error"`. The Windows Agent does not render a QR code that
carries it; see §12 and [`SECURITY.md`](SECURITY.md) §3.2.

### `GET /pairing`

A self-contained HTML card: device name, device id, the SPKI pin, and the API base path.
No signature, no rate limit beyond the public bucket. Useful for reading the pin onto a
second device by hand.

### `GET /`

**Console mode only.** Registered inside `AgentHost.RunInteractiveAsync`, so it exists only
when the executable is run from a console, not when it runs as a service. Returns `302` to
`/pairing`. As a service it is whitelisted as public by `IsPublic("/")` and then answered
`404` by the routing check.

---

## 3. Identity and device administration

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/auth/whoami` | `read` | — | `WhoAmIResponse` |
| `POST` | `/api/v1/auth/rotate` | `admin` | `{"confirmDeviceId":"…"}` | `RotateResponse` |
| `GET` | `/api/v1/devices` | `admin` | — | `PairedDeviceDto[]` |
| `POST` | `/api/v1/devices/{deviceId}/rotate` | `admin` | — | `RotateResponse` |
| `DELETE` | `/api/v1/devices/{deviceId}` | `admin` | — | `{"removed":true}` |

`WhoAmIResponse`:

```json
{ "deviceId": "3f2a9c17d4b8e206", "keyId": 1, "scopes": ["read", "control", "files"],
  "name": "Pixel 9", "platform": "android", "firstPaired": 1756000000,
  "lastSeen": 1756000500, "lastAddress": "192.168.1.77", "rotatedAt": null,
  "agentVersion": "1.0.0" }
```

`PairedDeviceDto` adds `enabled` and `suspended` (which is `!enabled`) and omits the
secrets. `lastSeen` is written on every authenticated request.

`RotateResponse`:

```json
{ "deviceId": "3f2a9c17d4b8e206", "previousKeyId": 1, "keyId": 2, "graceSeconds": 120,
  "wrappedSecrets": { "nonce": "…", "ciphertext": "…", "tag": "…" },
  "requiresManualDelivery": true }
```

`POST /auth/rotate` requires `confirmDeviceId` to equal the caller's own device id
(case-insensitive); a mismatch is `400` naming the expected value. It rotates **the calling
device**. `POST /devices/{deviceId}/rotate` rotates **any** device and needs no
confirmation; the envelope is only unsealable by that device's owner.

`requiresManualDelivery` is always `true` — the server never applies the new secret to
anything. See [`SECURITY.md`](SECURITY.md) §6 for why the 120 s grace period does not in
fact let the old key keep working.

---

## 4. System and metrics

| Method | Path | Scope | Query | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/system/info` | `read` | `detail=full\|summary` | `SystemInfoResponse` |
| `GET` | `/api/v1/system/metrics` | `read` | `window=250..10000` (default 1000) | `MetricsResponse` |
| `GET` | `/api/v1/system/network` | `read` | — | `NetworkMetrics` |
| `GET` | `/api/v1/system/display` | `read` | — | `DisplayInfo[]` |
| `GET` | `/api/v1/system/processes` | `read` | `sort=cpu\|memory\|name`, `limit=1..200` (default 50) | `ProcessInfoDto[]` |
| `POST` | `/api/v1/system/process/{pid}/terminate` | **`read`** | — | `{"terminated":true,"pid":1234}` |

`detail=summary` omits `gpu` (returns `[]`). It does not reduce anything else, despite the
`Cpu` being re-fetched unconditionally in both branches.

`sort` accepts `memory` and `name`; anything else — **including `cpu`**, which
`PROTOCOL.md` §6.2 documents — falls through to descending working set, the same as
`memory`. There is no CPU-usage column in the response to sort by.

`POST /system/process/{pid}/terminate` kills the process and its whole tree
(`Kill(entireProcessTree: true)`) after enabling `SeDebugPrivilege`. `404 esi_not_found`
if there is no such pid. It is **not** a graceful termination: the specification's
"Graceful then force" is not implemented.

### `GET /api/v1/system/storage`

**Does not exist.** `PROTOCOL.md` §6.2 lists it. Volume data is returned inside
`/system/info` (`storage[]`) and `/system/metrics` (`storage[]` with throughput). A call to
`/api/v1/system/storage` gets `404 esi_not_found` with the version-mismatch message.

### Sensor honesty

`/system/info` carries a `sensors` block with a `sensorsAvailable` array and a `notes` map
keyed by sensor name. A value is either measured by something identified, or absent with a
note explaining what was missing. Specifically:

* `cpu.temperature` — from `TemperatureProvider`. Absent on hardware with no readable
  thermal source.
* `gpu.temperature` — from NVML on NVIDIA, or a hardware-monitor driver elsewhere. Windows
  has no general-purpose GPU temperature API; the note says so.
* `gpu.fan` — a percentage of maximum from NVML, not RPM. `PROTOCOL.md` §6.2's
  `gpuFanRpm` field name is wrong; the DTO property is `gpuFanRpm` holding a percentage.
* `gpu.load` — from the `\GPU Engine(*)` PDH counters, else NVML, else absent.

`gpu[].source` is one of `nvml`, `intel-igcl`, `amd-adlx`, `pdh`, `unavailable`.

---

## 5. Power

| Method | Path | Scope | Body | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/power/status` | `read` | — | `PowerStatusResponse` |
| `POST` | `/api/v1/power/{action}` | `control` | `PowerActionRequest` | `PowerActionResponse` |
| `GET` | `/api/v1/power/wol/readiness` | `read` | — | `WolReadinessResponse` |
| `POST` | `/api/v1/power/wake` | `control` | `WakeRequest` | `WakeResponse` |

`{action}`, lowercased before matching:

| Action | Effect |
|---|---|
| `shutdown` | Graceful, honours `delaySeconds` |
| `restart` | Graceful restart |
| `forcerestart` | Restart with `force` implied true |
| `sleep` | Suspend |
| `hibernate` | Hibernate |
| `lock` | Lock the workstation. **Requires the session agent** |
| `signout`, `logoff` | Sign the user out. **Requires the session agent** |

`logoff` is an undocumented alias for `signout`. Anything else is
`404 esi_not_found` with `Unknown power action '<x>'.`

`PowerActionRequest`:

```json
{ "force": false, "delaySeconds": 5, "message": "ESI Remote PC" }
```

`force` defaults to `false` so applications are not closed without asking;
`delaySeconds` defaults to `5`, which is how long the shutdown dialog is shown.

`PowerActionResponse` — `200` when accepted, **`409` when refused**:

```json
{ "accepted": true, "action": "restart", "effectiveMode": "hybrid",
  "scheduledAt": 1756000005, "warning": null, "requiresInteractiveSession": false,
  "detail": "…" }
```

`lock` and `signout` return `accepted: false` with `requiresInteractiveSession: true` and a
`detail` explaining that nobody is logged in, when the session agent is offline. They are
not queued for later.

**Suspend caveat.** `sleep` and `hibernate` are handled by the service. The session agent
refuses the `suspend` command with "Suspend is handled by the Windows service, not the
session agent", so `effectiveMode` for these reflects the service's own path.

`PowerStatusResponse`:

```json
{ "state": "online", "isLocked": false, "isSleeping": false, "isHibernating": false,
  "activeSessionId": 2, "activeUser": "META-PC\\META", "loggedIn": true,
  "supportsSleep": true, "supportsHibernate": true, "lastBoot": 1755990000,
  "lastStateChange": 1755990000, "pendingPowerAction": null }
```

`state` is one of `online`, `offline`, `sleeping`, `hibernating`, `locked`, `unknown`.
`isLocked` comes from the session agent and is `false` whenever that agent is not
connected, so **`isLocked: false` with `loggedIn: true` does not prove the desktop is
unlocked.** `PROTOCOL.md` §6.3's `pendingPowerAction` is declared and never populated.

### `GET /api/v1/power/wol/readiness`

```json
{ "ready": false,
  "armedDevices": [ { "name": "Ethernet", "macAddress": "AA:BB:CC:DD:EE:FF",
                      "connectionType": "ethernet", "wakeMagicPacket": false,
                      "wakePattern": null } ],
  "macAddresses": ["AA:BB:CC:DD:EE:FF"],
  "broadcastAddresses": ["192.168.1.255"],
  "hiberbootEnabled": true,
  "issues": [ "Fast Startup is enabled, … Disable it with: powercfg /h off" ] }
```

`ready` is `issues.length == 0`. The `issues` array is generated, not measured prose — see
[`WOL_SETUP.md`](WOL_SETUP.md) for what each message means and how to clear it.

`wakeMagicPacket` is matched by a **partial, case-insensitive substring** between
`powercfg /devicequery wake_armed` output and the friendly adapter name, in both
directions. An adapter that does not match is reported as not armed rather than optimistically
assumed armed. This can produce a false negative when the two names diverge, and in
principle a false positive if one adapter's name is a substring of another's.

### `POST /api/v1/power/wake`

```json
{ "macAddress": "AA:BB:CC:DD:EE:FF",
  "broadcastAddresses": ["192.168.1.255"],
  "ports": [9, 7], "repeat": 3 }
```

| Field | Default |
|---|---|
| `macAddress` | Required. Accepts `AA:BB:…`, `aa-bb-…` and `aabb.ccdd.eeff`; anything that is not 12 hex digits is `400` |
| `broadcastAddresses` | `network.getRecommendedBroadcastAddresses()` |
| `ports` | `[9, 7]` |
| `repeat` | `3`, clamped to 1–10, 100 ms apart |

```json
{ "sent": 6, "targets": ["192.168.1.255:9,7"], "ports": [9, 7] }
```

`sent` counts datagrams handed to the socket. There is **no delivery feedback** — the
response says "sent", not "woken up". See [`WOL_SETUP.md`](WOL_SETUP.md).

Normally the phone sends the magic packet itself. This endpoint exists so a relay node can
send on the PC's behalf, which is why it is scoped `control` rather than `read`.

---

## 6. Apps

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/apps` | `read` | — | `{ "apps": AppEntryDto[], "categories": string[] }` |
| `GET` | `/api/v1/apps/running` | `read` | — | `{ "processes": RunningAppDto[] }` |
| `POST` | `/api/v1/apps/launch` | `control` | `{ "id": "steam" }` or `{ "path": "…", "args": [...], "workingDirectory": "…" }` | `LaunchAppResponse` |
| `POST` | `/api/v1/apps/{id}/terminate` | `control` | — | `{ "terminated": 2, "forced": false }` |
| `DELETE` | `/api/v1/apps/{id}/terminate` | `control` | — | `{ "terminated": 2, "forced": true }` |

`AppEntryDto` carries `id`, `name`, `category`, `path`, `iconPath`, `arguments`,
`workingDirectory`, `runAs`, `enabled`, `builtIn`, `pinned`, `suspended`, `lastLaunched`,
`exists`, `processName`. `exists` is computed live on every call, so an app installed or
removed since the settings were written shows the right state. `iconPath` is declared and
never populated by the built-in catalogue.

`runAs` ∈ `interactiveUser` (default), `service`, `elevatedUser`. The `elevatedUser` path
goes through the session agent and needs it to be running; `service` launches into session
0 with no visible UI.

`POST /apps/launch` returns **`409`** with `LaunchAppResponse` when `launched` is `false` —
unknown id, disabled app, or a launch that failed. The response is still the launch shape,
not an error envelope, so branch on `launched`, not on the status alone.

An ad-hoc `path` must exist on disk unless it ends with `:` (an App Paths registry entry).
There is no path sandbox for the launcher — any existing executable can be started, with any
arguments, as any of the three run contexts. This is a deliberate consequence of
`control` scope, not an oversight, but it is the broadest capability in the API.

`GET /apps/running` matches by `processName` only, so it lists configured apps that happen
to be running, not a general process list. Use `/system/processes` for that.

`DELETE /apps/{id}/terminate` is not in `PROTOCOL.md` §6.5. It is the force variant of the
`POST` and is what the mobile app calls when the user picks "force stop".

---

## 7. Files

Every route requires the `files` scope, for reads as well as writes. All paths are
validated by `FileSandbox` before they touch the filesystem; see
[`SECURITY.md`](SECURITY.md) §8 for the rules.

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/files/roots` | `files` | — | `FileRootListResponse` |
| `GET` | `/api/v1/files/list` | `files` | `path`, `showHidden` | `FileListResponse` |
| `GET` | `/api/v1/files/stat` | `files` | `path` (required) | `FileEntryDto` |
| `GET` | `/api/v1/files/search` | `files` | `root`, `query`, `maxResults` (default 200) | `FileSearchResponse` |
| `GET` | `/api/v1/files/recent` | `files` | `limit` (default 50) | `FileEntryDto[]` |
| `POST` | `/api/v1/files/mkdir` | `files` | `{"path":"…"}` | `FileOperationResponse` |
| `POST` | `/api/v1/files/rename` | `files` | `{"from":"…","to":"…"}` | `FileOperationResponse` |
| `POST` | `/api/v1/files/move` | `files` | `{"from":"…","to":"…"}` | `FileOperationResponse` |
| `DELETE` | `/api/v1/files` | `files` | `path` (query) | `FileOperationResponse` |
| `GET` | `/api/v1/files/download` | `files` | `path` (query) | Raw stream, `application/octet-stream`, `Content-Disposition: attachment` |
| `POST` | `/api/v1/files/upload-ticket` | `files` | `{"path":"…","size":123}` | `201 UploadTicketResponse` |
| `PUT` \| `POST` | `/api/v1/files/upload` | **ticket** | `?ticket=…`, raw body | `UploadResultResponse` |

`FileRootListResponse`:

```json
{ "roots": [ { "path": "C:\\Users\\META\\Downloads", "label": "Downloads",
               "readOnly": false, "exists": true } ],
  "defaultRoot": "C:\\Users\\META\\Downloads", "readOnly": false,
  "allowDelete": true, "allowUpload": true, "maxUploadBytes": 2147483648 }
```

`PROTOCOL.md` §6.6 documents this route as `read` scope. It requires `files`.

`FileListResponse` caps at **5000 entries** and sets `truncated: true` past that. Entries
are directories first, then case-insensitive alphabetical.

`/files/search` matches a case-insensitive **substring of the name**, recurses to depth 8,
skips system attributes and inaccessible entries, and stops after 20 000 entries examined.
`maxResults` is clamped to 1–500. `limit` on `/files/recent` is clamped to 1–200. Recent
entries are re-validated against the sandbox on read, so a root removed from the
configuration drops out of the list and a deleted file is skipped.

Both are implemented but the mobile app has no UI for them.

`/files/move` is a thin alias for `/files/rename` (`FileService.Move` calls `Rename`).

`/files/rename` and `/files/move` are refused with `{ "ok": false, "detail": "Rename is
disabled by configuration." }` when `files.allowDelete` is `false` — the same flag governs
both, which is surprising but is what the code does.

`/files` (delete) is refused with `{ "ok": false, "detail": "Deleting is disabled in the
agent settings." }` when `files.allowDelete` is `false`. These two are `200` with
`ok: false`, **not** an error status. Only a sandbox violation produces `403`.

Note the asymmetry: a *rejected* operation is `200 {ok:false}`, a *forbidden* one is `403`.
A client that only branches on status will treat a policy refusal as a success.

`FileOperationResponse` on a successful call also updates the `recent_files` table that
`/files/recent` reads.

### Upload tickets

`POST /files/upload-ticket` → `201`:

```json
{ "ticketId": "b21f8c4d90a1e273", "expiresAt": 1756000240, "maxBytes": 123456789,
  "uploadUrl": "/api/v1/files/upload?ticket=b21f8c4d90a1e273" }
```

`size` must be `>= 0` and `<= files.maxUploadBytes`, or the call throws
(`400`/`500` through the error envelope). A zero `size` issues a valid ticket that accepts
only an empty body.

Then:

```
PUT /api/v1/files/upload?ticket=b21f8c4d90a1e273
Content-Type: application/octet-stream

<raw bytes>
```

→ `200`

```json
{ "completed": true, "path": "C:\\Users\\META\\Downloads\\movie.mkv",
  "bytesWritten": 123456789, "sha256": "…" }
```

The upload route is the **only** route that is not signature-authenticated. It carries no
`X-ESI-*` headers at all; the single-use ticket is the credential. The ticket is 8 random
bytes hex-encoded, bound to `(deviceId, path, size)`, expires in 120 s, and is claimed with
a conditional `UPDATE … WHERE consumed_at IS NULL` so two concurrent uploads cannot both
win. The LAN guard still applies.

`POST` and `PUT` are both mapped. `PROTOCOL.md` §7.4 documents only `PUT`; the
`POST /api/v1/files/upload` form in §6.6 with `path` as the target file is **not**
implemented — a bare `POST` with no ticket is `401`.

Additional checks on the write itself, all `403 esi_forbidden_scope`:

* `files.allowUpload` is `false`;
* `Content-Length` exceeds `files.maxUploadBytes` (a missing `Content-Length` is treated as
  `0`, so a chunked upload is not pre-checked);
* the body grows past the limit mid-stream — the partial file is deleted before the error;
* a **directory** already exists at the target path. Overwriting an existing *file* is
  allowed; that is what "replace" means.

`sha256` is the SHA-256 of the bytes as written, **lowercase hex**, not base64.

---

## 8. Media

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/media/state` | `read` | — | `MediaStateResponse` |
| `POST` | `/api/v1/media/{action}` | `control` | `{"step":5}` or `{"volume":50}` | `MediaActionResponse` |

`GET /media/state`:

```json
{ "volume": 42, "isMuted": false, "defaultDevice": "Speakers (Realtek(R) Audio)",
  "sessionPeak": 78, "availableActions": ["…"], "supported": true }
```

`{action}`, matched case-insensitively against the `MediaAction` enum: `play`, `pause`,
`playPause`, `stop`, `next`, `previous`, `volumeUp`, `volumeDown`, `volumeSet`, `mute`,
`unmute`, `toggleMute`. Anything else is `404 esi_not_found`.

| Group | Needs the session agent | Body |
|---|---|---|
| `play`, `pause`, `playPause`, `stop`, `next`, `previous` | **Yes** | — |
| `volumeUp`, `volumeDown`, `volumeSet`, `mute`, `unmute`, `toggleMute` | No | `step` (1–25, default 5) or `volume` (0–100, default 50) |

Transport keys are `VK_MEDIA_PLAY_PAUSE` / `_STOP` / `_NEXT_TRACK` / `_PREV_TRACK`, which
only reach the focused application when injected on the interactive desktop. Injected from
session 0 they go nowhere, so the service forwards them. When the session agent is
offline the call returns **`409`** with:

```json
{ "accepted": false, "action": "play",
  "detail": "Media keys must be sent from the interactive desktop. Log in to the PC, or start 'ESI Remote PC Session'." }
```

---

## 9. Notifications

| Method | Path | Scope | Query | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/notifications` | `read` | `unreadOnly`, `limit` (default 100), `since` | `NotificationListResponse` |
| `POST` | `/api/v1/notifications/{id}/read` | `read` | — | `{"read":true}` |
| `POST` | `/api/v1/notifications/read-all` | `read` | — | `{"updated":7}` |
| `DELETE` | `/api/v1/notifications/{id}` | `read` | — | `{"deleted":true}` |
| `DELETE` | `/api/v1/notifications` | `read` | — | `{"deleted":42}` |
| `GET` | `/api/v1/notifications/rules` | `admin` | — | `{ "rules": NotificationRuleDto[] }` |
| `PUT` | `/api/v1/notifications/rules` | `admin` | `{ "rules": [...] }` | `{ "rules": [...] }` |

Mutating notifications needs only `read`. That is deliberate in `PROTOCOL.md` §6.10 and
harmless: the blast radius is one phone's own notification list.

`PUT /notifications/rules` merges with `NotificationRuleCatalog.Merge` rather than
replacing, so built-in rules that are omitted survive. It then calls
`engine.ResetState()` and `engine.Prime(...)` so thresholds take effect immediately instead
of waiting for the previous sustain timers to expire, and audits the change.

`NotificationRuleDto`: `id`, `enabled`, `severity`, `title`, `body`, `threshold`,
`sustainSeconds`, `cooldownSeconds` (default 300), `builtIn`, `condition`. `condition` is a
free-form expression string; it is not validated by the agent.

Retention is `notifications.retentionDays`, capped at `debug.logRetentionDays`, applied by
the 5-minute housekeeping pass.

---

## 10. Gaming

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/gaming/state` | `read` | — | `GamingStateResponse` |
| `GET` | `/api/v1/gaming/profiles` | `read` | — | `GamingProfileDto[]` |
| `GET` | `/api/v1/gaming/fps` | `read` | — | `{ "fps": null, "note": "…" }` |
| `POST` | `/api/v1/gaming/mode` | `control` | `GamingModeRequest` | `GamingModeResponse` |
| `POST` | `/api/v1/gaming/apps/{id}/state` | `control` | `{"enabled":false}` | `GamingStateResponse` |

`GET /gaming/fps` always returns `fps: null` with:

> Windows exposes no supported API for the presented frame rate, so this is null rather than
> a guess.

`fps` in `/gaming/state` and in every `history[]` point is `null` for the same reason.
`pingMs` is a real measurement — a single ICMP echo to the primary interface's gateway with
a 1 s timeout.

`sessionAgentConnected` in `GamingStateResponse` is hardcoded to `false` by
`GamingService.GetState`. It is not wired to the bridge and carries no information.

Enabling gaming mode:

1. Captures the current power scheme GUID.
2. Switches to the first scheme whose name contains the profile keyword
   (`performance` → `Ultimate Performance`, `balanced` → `Balanced`, otherwise the literal
   `profileId`). If nothing matches, the current scheme is kept and a warning is returned.
3. **Terminates** each app in `suspendApps` that has a `processName` and is running. There
   is no documented Windows process-freeze API, so "suspend" means close. The apps are not
   restarted, and the response says so in `warnings`.
4. Persists `gaming.enabled`, `profileId` and `launchAppId` to `settings.json`.

Disabling restores the captured power scheme exactly — it does not assume `Balanced` — and
clears the suspended-app list without restarting anything.

---

## 11. Remote control

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/remote/session` | `read` | — | `RemoteSessionResponse` |
| `POST` | `/api/v1/remote/input` | `control` | `RemoteInputRequest` | `RemoteInputResponse` |
| `DELETE` | `/api/v1/remote/session` | `control` | — | `{"stopped":true}` |

`GET /remote/session`:

```json
{ "available": false, "sessionAgentState": "NoUserSession", "active": false,
  "streaming": false, "connectedDeviceId": null, "captureSource": "gdi", "maxFps": 30,
  "inputEnabled": false, "displays": [ DisplayInfo ],
  "capabilities": ["mouseMove", "mouseLeftClick", "mouseRightClick", "mouseMiddleClick",
                   "mouseScroll", "keyboard", "unicodeText"] }
```

`sessionAgentState` ∈ `NotRunning`, `Starting`, `Ready`, `NoUserSession`, `Error`.
`available` and `inputEnabled` are both `sessionAgent.IsConnected`.
`captureSource` is `"gdi"` — `GdiScreenCaptureProvider` is the only provider compiled in.
`maxFps` is 30, and `RemoteService.StartStream` clamps `fps` to 1–30, `quality` to 10–100
and `gridCols`/`gridRows` to 1–16.

`RemoteInputRequest`:

| Field | Type | Used by |
|---|---|---|
| `kind` | string | `mouseMove`, `mouseButton`, `mouseWheel`, `key`, `text`, `sessionStart`, `sessionStop` |
| `x`, `y` | double? | Normalised 0.0–1.0 within the target display |
| `button` | string? | `left`, `right`, `middle` |
| `down` | bool? | Key and button press/release |
| `deltaX`, `deltaY` | int? | Wheel notches |
| `code` | string? | W3C `UI Events` `KeyboardEvent.code` |
| `text` | string? | Unicode text for `KEYEVENTF_UNICODE` injection |
| `mods` | string[]? | `ctrl`, `shift`, `alt`, `meta` |
| `displayId` | int? | Target display |

`sessionStart` and `sessionStop` are rejected with
`"Start a screen stream with remote.start over the WebSocket, not a plain request."` — a
plain HTTP request cannot hold a stream open.

`POST /remote/input` returns **`409`** with `RemoteInputResponse` when `accepted` is
`false`, which is what happens whenever the session agent is not connected:

```json
{ "accepted": false, "delivered": 0, "sessionAgentState": "NotRunning",
  "detail": "Remote input needs the per-user session agent, which is not running. Log in to the PC (or start 'ESI Remote PC Session') and try again." }
```

`delivered` is `0` or `1`; it is not a queue depth. `RemoteService` records a device in
`_activeStreams` on the first accepted `mouse*` event, which is what makes
`/remote/session` report `active: true` for single-shot input even without a WebSocket
stream.

The session agent's own log lives at
`%LOCALAPPDATA%\ESI-RemotePC\logs\session-*.log`, not in the service's log directory.

---

## 12. Settings, logs, audit

| Method | Path | Scope | Request | Response |
|---|---|---|---|---|
| `GET` | `/api/v1/settings` | `admin` | — | `SettingsDto` |
| `PUT` | `/api/v1/settings` | `admin` | `SettingsDto` | `SettingsUpdateResponse` |
| `POST` | `/api/v1/settings/reset` | `admin` | — | `{ "ok": true, "detail": "…", "restartRequired": true }` |
| `GET` | `/api/v1/logs` | `admin` | `tail` (1–2000, default 200), `level`, `since` | `LogTailResponse` |
| `GET` | `/api/v1/audit` | `admin` | `limit` (1–2000, default 200), `deviceId` | `AuditListResponse` |

`PUT /settings` validates, saves, and pushes the new policy into the live components
(`LocalNetworkGuard.Configure`, `RateLimiter.RequestsPerMinute`,
`SettingsStore.ApplyToSandbox`, `NotificationEngine.Prime`) without a restart, then reports
whether one is needed:

```json
{ "ok": true, "restartRequired": false, "appliedAt": 1756000000, "warnings": [] }
```

`restartRequired` is `true` only when `port`, `discoveryPort`, `discoveryEnabled` or
`mdnsEnabled` changed — those are read once at start and are not re-bound.

**Three settings cannot be turned off through this route.**
`SettingsStore.UpdateAsync` emits a warning for each and then unconditionally writes the
value back to `true`:

| Field | Forced to |
|---|---|
| `security.localNetworkOnly` | `true` |
| `security.requireSignedRequests` | `true` |
| `security.tlsEnabled` | `true` |

`security.requireSignedRequests` is additionally never read by any code path — signing is
unconditional. `security.sessionAgentRequiredForLock` and `startWithWindows` are declared in
the DTO and never read either.

`allowedSubnets` is restored to `["192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12"]` if
sent empty, with a warning. `PROTOCOL.md` §6.11's default list also includes
`169.254.0.0/16`; the code does not, and `LocalNetworkGuard.IsAllowed` has no IPv4
link-local special case, so link-local IPv4 clients are rejected.

Rejected with `400 esi_bad_request` and the offending field named:

| Rule | Range |
|---|---|
| `port` | 1–65535 |
| `discoveryPort` | 1–65535, and not equal to `port` |
| `security.clockSkewSeconds` | 10–3600 |
| `security.maxPairedDevices` | 1–64 |
| `security.rateLimitPerMinute` | 10–100000 |
| `security` section present | required |

Non-existent `files.allowedRoots[].path` values are **warned about, not rejected** — the
root is skipped at apply time and the warning tells you so.

`POST /settings/reset` restores the built-in defaults **and** deletes the TLS identity
(`CertificateAuthority.Reset()`), which invalidates every paired device. It is audited as
`certificate.reset` and the response says so in plain language. A restart is required to
finish. There is no way to reset settings without also rotating the certificate.

`GET /logs` parses the agent's own Serilog files in `agent-*.log` and returns
`LogTailResponse`:

```json
{ "lines": [ { "ts": 1756000000, "level": "Information", "logger": "Esi.Service.Endpoints",
               "message": "…", "exception": null, "eventId": null } ],
  "truncated": false, "logDirectory": "C:\\ProgramData\\ESI-RemotePC\\logs" }
```

It parses compact Serilog lines by hand (`ParseLogLine`). A multi-line exception stack
becomes several independent entries, and any line it cannot parse is returned as
`Information` with the whole line as the message. `level` filtering is applied after
parsing; `since` compares `ts >= since`.

`GET /audit`:

```json
{ "entries": [ { "id": 4211, "ts": 1756000000, "deviceId": "3f2a9c17d4b8e206",
                 "deviceName": "Pixel 9", "remoteAddress": "192.168.1.77",
                 "action": "auth.failed", "result": "denied",
                 "detail": "GET /api/v1/audit: esi_forbidden_scope — …",
                 "chainHash": "9f2c…" } ],
  "chainValid": true }
```

`entries` are newest first. `chainValid` is the result of recomputing the whole SHA-256
chain; a `false` value means a row was edited, deleted, or aged out by retention. See
[`SECURITY.md`](SECURITY.md) §10 for what this does and does not prove.

The session agent's log is **not** in this directory and is not reachable through
`/api/v1/logs`.

---

## 13. WebSocket `/ws`

Outside `/api/v1`. The upgrade must be signed; the signature covers `/ws` with an empty
body. Per [`PROTOCOL.md`](PROTOCOL.md) §7.1 the header values travel as query parameters
(`device_id`, `key_id`, `ts`, `nonce`, `content`, `sig`) so a signature is not exposed in a
URL that an intermediary might log.

The upgrade is refused with `400 esi_bad_request` if it is not a WebSocket request, and
`401 esi_unauthorized` if `ctx.Device` is null. A request that is not a WebSocket upgrade
but *is* on `/ws` never reaches the endpoint — the local-network and rate-limit middleware
run first.

### Client → server frames

| `t` | Fields | Effect |
|---|---|---|
| `ping` | `id`, `ts` | Replies `pong` |
| `subscribe` | `id`, `channels[]` | Adds channels. Empty or absent subscribes to `metrics`, `notifications`, `events` |
| `unsubscribe` | `id`, `channels[]` | Removes channels. Always replies, even with none given |
| `remote.start` | `id`, `displayId`, `fps`, `quality`, `grid[2]`, `inputEnabled` | Starts capture. Defaults: display 0, 15 fps, quality 70, grid 4×4 |
| `remote.stop` | `id` | Stops capture |
| `remote.input` | `id`, `input{…}` | One input event (§11) |
| `remote.key` | `id`, `code`, `text`, `down`, `mods[]` | Shorthand for a `key` event |
| `gaming.watch` | `id`, `intervalMs` | Replies with a `gaming` frame |

`inputEnabled` is accepted and **ignored** — input is always accepted while the session
agent is connected. `intervalMs` on `gaming.watch` is accepted and ignored: the reply is a
single `gaming` frame with the body set to the frame id, not to the current gaming state.
`gaming.watch` does not start a subscription and nothing follows it.

`remote.start` is refused with `esi_conflict` and
`"Another device is already streaming this screen."` when a different device holds the
stream. One screen, one device.

### Server → client frames

| `t` | When | `data` |
|---|---|---|
| `hello` | Immediately on connect | `{ serverTime, deviceId, deviceName }` — **not** the `/handshake` body |
| `pong` | Reply to `ping` | `{ ts }` |
| `subscribed` / `unsubscribed` | Reply to the matching frame | `{ channels: [...] }` |
| `metrics` | Every 1 s, if subscribed to `metrics` | The `/system/metrics` body |
| `notification` | New notification, if subscribed to `notifications` | `NotificationDto` |
| `event` | State change, if subscribed to `events` | `{ kind, data }` |
| `remote.state` | Reply to `remote.start` / `remote.stop` | `{ active, displayId, requestedFps, quality, detail }` |
| `error` | Any failure | `{ code, message }`, with `id` where the failing frame had one |

`hello` does not carry the handshake payload. A client that wants the TLS pin or the
feature map must also call `GET /api/v1/handshake`.

`gaming` is a frame type the server can send (`BroadcastEventAsync` is not currently wired
to it) but no code path emits one.

### Binary frames

`opcode 2`, the screen stream. Header is 32 bytes, little-endian
(`ScreenFrameHeader`, `Size = 32`):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `magic` — `0x56495345`, so the bytes on the wire are `45 53 49 56`, ASCII `ESIV` |
| 4 | 2 | `frameType` — `1` keyframe, `2` tile update, `3` resync request |
| 6 | 2 | `displayId` |
| 8 | 4 | `frameIndex`, monotonic per session |
| 12 | 2 | `tileCount` |
| 14 | 2 | `codec` — `1` JPEG. `2` (`RdpGraphicsPipeline`) is reserved and never emitted |
| 16 | 4 | `screenWidth` |
| 20 | 4 | `screenHeight` |
| 24 | 2 | `gridCols` |
| 26 | 2 | `gridRows` |
| 28 | 4 | `payloadLength` |

Then `tileCount` × 14-byte `TileDescriptor` (`col`, `row`, `tileWidth`, `tileHeight`,
`tileLength` — all little-endian, `TileLength` a `uint32`), then the concatenated JPEG
payloads in descriptor order.

`PROTOCOL.md` §7.5 writes the magic as `0x56 53 49 45 little-endian`; the byte sequence it
describes is the one the code produces, but the hex literal in the specification is written
in the wrong order.

**A frame arrives base64-encoded inside a JSON message on the named pipe**, and the service
re-wraps it as a WebSocket binary frame. A client only ever sees the binary form.

The `frameType == 3` resync request is defined in `ScreenFrameHeader.ResyncRequest` and
mentioned in the specification, but no code path emits one, and the server has no
tile-request handler. A client that loses tiles has no recovery mechanism in v1.

### Keepalive

The **server** sends `{"t":"ping"}` every 20 s. A client that wants a `pong` must reply with
its own `ping` frame — the server does not answer its own pings.

`HubConnection.IsAlive` uses a 45 s threshold but is **never called**, and the
`CloseAll()` path is only reached on shutdown. There is no idle-connection reaper. A
half-open socket therefore persists until the client or the OS notices.

The client's send is fire-and-forget (`SendBinaryAsync` is not awaited) so a slow phone
cannot stall the capture pump — at the cost of frames being dropped silently on a socket
whose send queue is full.

---

## 14. Audit actions

Recorded by `AuditLog.Record` and returned by `GET /api/v1/audit`:

| Action | Recorded when |
|---|---|
| `pair.requested` | A pairing attempt is created, with the device name and code |
| `pair.approved` | Secrets are released for a pairing |
| `pair.denied` | Denied from `esirc pair deny` |
| `auth.failed` | Any signature rejection, or a scope refusal |
| `network.rejected` | A source address was outside the allowed subnets |
| `rate.limited` | A token bucket was exhausted |
| `power.action` | A power action was accepted or failed |
| `device.revoked` | A device was removed, by API or by CLI |
| `device.rotated` | A key rotation completed |
| `settings.changed` | `PUT /settings`, `PUT /notifications/rules` |
| `file.denied` | A delete was refused because the target is a root or an ancestor of one |
| `file.deleted` | A file or directory was deleted |
| `app.launched` | A configured or ad-hoc app was launched |
| `certificate.reset` | `POST /settings/reset` wiped the TLS identity |
| `remote.started` | A screen stream started, with display, fps, quality and grid |
| `files.upload` | Upload ticket issued or consumed |
| `http.rejected` | Declared, never recorded |

`auth.succeeded` is declared in `AuditLog` and never recorded. Successful authentications
are not in the trail.

---

## 15. Discrepancies with `PROTOCOL.md`

| # | `PROTOCOL.md` | The code | Impact |
|---|---|---|---|
| 1 | §6.2 `GET /api/v1/system/storage`, scope `read` | **Route does not exist.** `404` | Client must use `system/info` or `system/metrics` |
| 2 | §3.2 `POST /api/v1/auth/activate` | **Route does not exist.** `404` | The pairing flow has no activation step; the phone starts signing immediately after `pair/session` returns `approved` |
| 3 | §3 pre-auth list includes `GET /` as the HTML pairing page | `/` is a `302` to `/pairing`, and only in console mode. The page is at `/pairing`, which is not in the specification's list | A client following §3 will look for HTML at `/` and get `404` from a service |
| 4 | §6.6 `GET /files/roots` scope `read` | `files` | A `read`-only device cannot list the roots |
| 5 | §6.2 `sort=cpu` | `cpu` is not handled; it falls through to descending working set, identical to `memory` | `cpu` does not sort by CPU |
| 6 | §6.2 `POST /system/process/{pid}/terminate` "Graceful then force" | `Kill(entireProcessTree: true)` — always force | A graceful close is not attempted |
| 7 | §6.2 `gpuFanRpm` | Field name retained; the value is a **percentage of maximum**, not RPM | Do not label it RPM |
| 8 | §6.5 only `POST /apps/{id}/terminate` | `DELETE` also exists and forces | Not a break; additive |
| 9 | §6.6 `POST /files/upload` with `path` and a raw body | Only the ticket form exists; a ticketless upload is `401` | Use `upload-ticket` then `PUT`/`POST` with `?ticket=` |
| 10 | §7.4 upload is `PUT` only | `PUT` and `POST` are both mapped | Not a break; additive |
| 11 | §6.11 `localNetworkOnly`, `requireSignedRequests`, `tlsEnabled` as settings | All three are forced back to `true` by `UpdateAsync`; `requireSignedRequests` is never read | They are not configurable through the API at all |
| 12 | §6.11 `sessionAgentRequiredForLock`, `startWithWindows` | Declared in the DTO, never read | They have no effect |
| 13 | Default LAN includes `169.254.0.0/16` | Default `allowedSubnets` is the three RFC 1918 ranges only, and `IsAllowed` has no IPv4 link-local case | Link-local IPv4 clients are rejected |
| 14 | §3.1 `features` map | Also contains `mdns` | Additive |
| 15 | §6.3 `pendingPowerAction` | Declared, never populated | Always `null` |
| 16 | §7.5 magic `0x56 53 49 45 little-endian` | `0x56495345`, bytes `45 53 49 56` = `ESIV` | Specification's hex literal is byte-reversed |
| 17 | §7.5 `frameType 3` tile request/ack | Header struct exists; no path emits it and no handler consumes it | No tile-resend recovery in v1 |
| 18 | §7.5 `hello` carries the §3.1 handshake body | `hello` carries `{ serverTime, deviceId, deviceName }` | Fetch `/handshake` separately for the pin and feature map |
| 19 | §7.6 client pings every 20 s, server closes after 45 s idle and 30 failed sends | The server pings every 20 s; the 45 s liveness check (`IsAlive`) is never called and there is no failed-send counter | Idle sockets are not reaped |
| 20 | §5.2 `auditKey` is a third derived key | Never derived or used; the chain is unkeyed | See [`SECURITY.md`](SECURITY.md) §10 |
| 21 | §5.4 / §9.3 a QR carries the provisioning secret | No QR emitter in the agent; `Poll` requires the secret to release anything | **Pairing cannot complete** without an out-of-band source for the secret |
| 22 | §6.9 `fpsSource: "dwm-present-rate"` | `fps` and `fpsSource` are never populated; `MeasureFps` returns `null` | Any client rendering an FPS number is inventing it |
| 23 | §6.9 gaming mode "suppresses non-critical notifications" | Not implemented; gaming mode switches the power scheme and closes apps | Smaller behaviour change, no security impact |
| 24 | §6.8 `DELETE /remote/session` "End the streaming session" | Correct | — |

### Two findings that are not discrepancies but are worth stating plainly

**1. `POST /api/v1/system/process/{pid}/terminate` requires only the `read` scope.**
`/api/v1/system` is not in any of the four special prefixes in
`RequestSignatureValidator.RequiredScope`, so the method is not consulted and the call
falls through to the `read` default. A device paired with `read` scope only can terminate
arbitrary processes on the machine, including ones owned by other users, because the
handler enables `SeDebugPrivilege` and kills the whole tree. Compare
`POST /api/v1/apps/{id}/terminate`, which correctly lands under `/api/v1/apps` and requires
`control`. The fix is to add `/api/v1/system/process` to the `control` branch of the scope
table.

**2. WebSocket frames are not scope-checked.** The `/ws` upgrade passes through the scope
table, where `/ws` falls through to the `read` default. Once the socket is open,
`WebSocketHub.DispatchAsync` handles `remote.input` and `remote.key` with no further scope
check, routing them to `RemoteService.SendInput` and on to the session agent. A
`read`-only device can therefore inject mouse and keyboard events into the interactive
desktop over the WebSocket even though `POST /api/v1/remote/input` correctly requires
`control`. The fix is a scope check per frame type in `DispatchAsync`.

Both are recorded here rather than fixed, because this document set describes the code as it
stands. Neither is a theoretical concern: both are reachable by a device that was paired
with strictly less privilege than the phone's UI implies.

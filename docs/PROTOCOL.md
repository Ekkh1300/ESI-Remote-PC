# ESI Remote PC — Wire Protocol v1

> Canonical, normative specification of the contract between the **Windows Agent** and the
> **Mobile App**. Both implementations are generated from / verified against this document.

| Item | Value |
|---|---|
| Protocol family | `ESIV1` |
| REST base | `/api/v1` |
| Default HTTPS port | `48220` |
| Default discovery UDP port | `48211` |
| mDNS service type | `_esiremotepc._tcp.local.` |
| WebSocket endpoint | `/ws` |
| Default LAN | `192.168.0.0/16`, `10.0.0.0/8`, `172.16.0.0/12`, `169.254.0.0/16` |

All payloads are `application/json; charset=utf-8` unless stated otherwise.
All timestamps are **Unix epoch seconds (UTC)** unless stated otherwise.
All sizes are **bytes**. All speeds are **bytes/second** unless the field says `*PerSecond` explicitly
in bits.

---

## 1. Versioning

* `PROTOCOL_VERSION` is an integer, currently `1`.
* The pre-auth handshake (`GET /api/v1/handshake`) returns `protocolVersion`.
* A client MUST refuse to operate against a major version it does not understand
  (`protocolVersion != 1`). Additive changes (new optional fields, new endpoints) stay within v1.
* Every WebSocket frame carries `p` (protocol version) so streams can be versioned independently.

---

## 2. Transport

### 2.1 TLS

The agent generates a **self-signed ECDSA P-256 certificate** on first run and persists it as a PFX.
Recommended subject: `CN=<ComputerName>`, with SANs:

* `DNS:<ComputerName>`
* `DNS:<ComputerName>.local`
* `DNS:esiremotepc-<deviceIdShort>.local`
* `IP:<each non-loopback IPv4 address>` (re-generated when the address set changes)

Clients pin the certificate by **SPKI SHA-256** fingerprint:

```
spkiSha256 = base64( SHA-256( DER( SubjectPublicKeyInfo ) ) )
```

The pin is delivered out-of-band in the pairing QR payload and can be re-read from
`GET /api/v1/handshake` (unauthenticated — a MITM could lie about the pin here, which is
precisely why the QR code is authoritative).

Clients MUST reject a connection whose presented SPKI pin does not match a pinned value.
Two pins may be held simultaneously (current + next) to allow rotation without bricking
a paired client.

### 2.2 Why TLS *and* request signing

TLS + pinning gives confidentiality and integrity of the channel. Request signing adds:

* **Authentication** of the paired device (a stolen/absent pin cannot mint requests).
* **Replay protection** even if a request body is captured from memory/logs.
* **Anti-confusion**: a captured request cannot be replayed against a different endpoint
  because the method + path are inside the MAC.

Both are required. See `SECURITY.md`.

---

## 3. Pre-authentication surface (local network only)

Only these endpoints are reachable without a paired device. All are subject to the
`LocalNetworkOnly` filter and to aggressive rate limiting.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/handshake` | Identity, protocol version, ports, TLS pin, pairing state |
| `GET` | `/api/v1/pair/session?id=<pairingId>` | Poll a pending pairing attempt |
| `POST` | `/api/v1/pair/request` | Create a pairing attempt (returns id + user code) |
| `GET` | `/api/v1/health` | Liveness, uptime of the agent process |
| `GET` | `/` | Minimal HTML pairing landing page (human readable) |

`POST /api/v1/pair/request` is rate limited to **5 per hour per source address**, with a
burst of 5. The hour is deliberate: pairing is the only unauthenticated *write* endpoint on
the agent, and it is what an attacker on the LAN reaches for. Five attempts a minute would
be a desktop prompt every twelve seconds; five an hour is a nuisance for a user who keeps
re-pairing and a meaningful bound on an attacker. The limit is configurable
(`security.pairingPerHour`).

The unauthenticated surface as a whole is budgeted separately and much more tightly
(`security.unauthenticatedPerMinute`, default 30, burst 10), so a caller cannot spend the
pairing budget on health checks.

`GET /api/v1/pair/session` is the one route that is **not** on that budget, and the
exception is load-bearing. The agent's own `pair/request` response tells a phone to poll
this route every `pollIntervalMs` (1000 ms) for the whole of `pairingWindowSeconds` (120 s),
so a client that obeys the protocol makes ~120 requests to get through one pairing. Against
a budget of 30 a minute that client is throttled about a quarter of the way through the
window, and any user who takes more than half a minute to approve a pairing sees it fail.
The poll route therefore has its own budget, derived at startup from the two protocol
constants rather than configured, so it cannot fall behind the advice the agent is giving:
`RateLimiter.RequiredPairingPollsPerMinute` returns twice the polls the protocol demands,
clamped to a floor of 60 and a ceiling of 100000. Doubling absorbs a client that polls a
little faster than asked; the clamp keeps a spinning client bounded.

This was found by pointing the real app at a running agent, not by reading the code. The
budget was 30 a minute, hard-coded, and the tests all passed.

### 3.1 `GET /api/v1/handshake` → 200

```json
{
  "protocolVersion": 1,
  "agentVersion": "1.0.0",
  "deviceId": "3f2a9c17d4b8e206",
  "deviceName": "META-PC",
  "hostName": "META-PC",
  "httpsPort": 48220,
  "discoveryPort": 48211,
  "spkiSha256": "MIIBIjANBgkq...=",
  "tlsSubject": "CN=META-PC",
  "notBefore": 1756000000,
  "notAfter": 1787536000,
  "serverTime": 1756000000,
  "pairingRequired": true,
  "pairingOpen": true,
  "maxPairedDevices": 8,
  "pairedDeviceCount": 1,
  "features": {
    "power": true, "metrics": true, "discovery": true, "wakeOnLan": true,
    "fileManager": true, "appLauncher": true, "media": true, "remote": true,
    "gaming": true, "notifications": true, "remoteAccess": false
  }
}
```

### 3.2 Pairing flow

```
Phone                                       PC
  |-- POST /pair/request {name, platform, nonce, userCode} --->|
  |                                            creates PairingAttempt and adopts the
  |                                            phone's nonce as the provisioning secret
  |<-- 201 {id, userCode, expiresAt, method} --------|
  |                                                    [ desktop approval prompt:
  |                                                      "New device wants to pair",
  |                                                      showing that same userCode ]
  |--- GET /pair/session?id=...&secret=<nonce> (1/s) -->|
  |<-- 200 {status:"pending"} --------------------------|
  |                                                    [ user compares the code, taps Approve ]
  |<-- 200 {status:"approved", deviceId, keyId, wrappedSecrets, ...} (once)
  |-- POST /api/v1/auth/activate (deliver client pubkey/nonce, mark bound) -->|
```

`POST /pair/request` body:

```json
{ "deviceName": "Pixel 9", "platform": "android", "osVersion": "16", "appVersion": "1.0.0",
  "publicSpkiSha256": "…(optional, the phone's identity pin)",
  "nonce": "…(base64url, 32 bytes, the phone's own one-time secret)",
  "userCode": "418207" }
```

`POST /pair/request` → `201`:

```json
{ "pairingId": "b21f…", "userCode": "418207", "expiresAt": 1756000120,
  "approval": "desktop-prompt", "pollIntervalMs": 1000 }
```

`approval` is one of two tokens, and a client has to be able to tell them apart because
they imply different instructions for the human:

| Token | Meaning | What the human does |
|---|---|---|
| `desktop-prompt` | A signed-in session was found and has been asked to prompt. | Compare the code with the one on the phone, then approve in the prompt on the PC. |
| `browser-or-cli` | Nobody is signed in, so there is no trusted display. | Stop the agent, run `esirc pair approve <code>`, start it. See §5.4.1. |

### 3.2.1 Why there is no QR code

`nonce` and `userCode` work in opposite directions, and it is worth being exact about which
is which:

- **`nonce`** is the credential. It must be exactly 32 bytes of base64url. It is stored as the
  attempt's provisioning secret, and the agent releases the wrapped credentials only to a poll
  that presents it. An agent that received a `nonce` of the wrong length rejects the request
  outright rather than minting a replacement: a replacement would leave the caller holding an
  attempt it can never unlock, which surfaces much later as a poll refused for a missing secret
  and reads as a networking fault rather than a rejected request.
- **`userCode`** is six digits the phone chose and the agent echoes back, so both screens show
  the same characters without either side knowing a derivation. It carries no authority. Its
  whole job is to let the human tell *their* prompt apart from one raised by something else on
  the network — the role Bluetooth's numeric comparison plays. A malformed code is replaced
  with a generated one rather than rejected, because it is a display token and not a
  credential.

Why this is not weaker than minting the secret server-side and transferring it: the guarantee
was never *where* the secret was made, it was that only the party the user meant could produce
it. The agent still releases only to a poll bearing the right value. What changed is that the
value now starts on the device already in the user's hand, so it does not have to travel
there.

That is what removed the QR code, and with it any dependence on the phone having a camera that
can read one. Two attempts at removing it by fixing the QR instead both failed on real
hardware — the prompt closed on its own approval (D36) and then drew a code on fractional
pixels (D37, D38) — which is the argument for not needing the code at all rather than for
tuning it once more.

**Compatibility.** Both fields are optional. A client that omits them gets the previous
behaviour: the agent mints the secret, puts the polling URL on the desktop prompt, and the user
transfers it by scanning. The `pairing-requested` log line records which path an attempt took
(`secret supplied by the requester` or `secret minted here, to be transferred by the prompt`),
because "which flow is this phone on" is the first thing worth knowing when a pairing will not
finish.
An earlier draft called the second token `tray-or-cli`. There is a tray icon, but it has no
pairing item, so the name described a UI that does not exist.

`GET /pair/session?id=` → `200` with one of:

```json
{ "status": "pending" }
{ "status": "denied" }
{ "status": "expired" }
{ "status": "approved",
  "deviceId": "3f2a9c17d4b8e206", "keyId": 1,
  "spkiSha256": "…",
  "wrappedSecrets": { "nonce": "…", "ciphertext": "…", "tag": "…" } }
```

`wrappedSecrets` is the base64 `masterSecret` encrypted with AES-256-GCM under the
`wrapKey` derived from the *provisioning* secret described in §5.4. The secrets are
delivered **exactly once**; subsequent polls return `{"status":"consumed"}`.

---

## 4. Request signing (HMAC-SHA256)

### 4.1 Headers

| Header | Value |
|---|---|
| `X-ESI-Device-Id` | lowercase hex, 16 chars |
| `X-ESI-Key-Id` | decimal integer, the current key generation of the device |
| `X-ESI-Timestamp` | Unix epoch seconds |
| `X-ESI-Nonce` | base64url (no padding) of 16 random bytes |
| `X-ESI-Content-Sha256` | base64 of SHA-256 over the exact request body; if there is no body use the SHA-256 of the empty byte string (`47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=`) |
| `X-ESI-Signature` | base64 of HMAC-SHA256(`sessionKey`, canonicalString) |

### 4.2 Canonical string

Exactly 8 lines, LF-separated, **no trailing newline**. Built in this order:

```
ESIV1
{METHOD}                        uppercase, e.g. GET
{canonicalTarget}               path + '?' + sorted query, see below
{timestamp}
{nonce}
{contentSha256}                 base64, as sent in the header
{deviceId}                      lowercase hex
{keyId}                         decimal
```

`canonicalTarget` rules:

* Start with the **raw** request path (no decoding, no normalisation). Empty → `/`.
* If a query string is present, drop the leading `?`, split on `&`, drop empty segments,
  then sort the segments **ordinal ascending (UTF-16 code unit order, i.e. byte order for
  ASCII)**, then rejoin with `&`. **Do not re-encode or re-decode** — both sides use the
  exact bytes the client placed on the wire.
* Duplicate keys are preserved and sorted by full segment, so `?a=2&a=1` sorts to
  `?a=1&a=2`.

```
GET  /api/v1/system/info?detail=full
ESIV1
GET
/api/v1/system/info?detail=full
1756000000
f3Q8Zl0aW1kJ7w3K
X48dCZ6lN3Zg0tDg3m2m4Q8YF9n0pQ1rS2tU3vW4xY=
3f2a9c17d4b8e206
1
```

### 4.3 Server verification order

1. Parse and length-validate headers. Reject unknown/missing `X-ESI-Device-Id` with `401`.
2. Look up device. Unknown / revoked / disabled → `401`.
3. **Timestamp window**: `|serverTime - timestamp| <= 300 s` → else `401 esi_timestamp`.
4. **Nonce replay**: `nonce` unseen for this device within the window → accept and record;
   repeat → `401 esi_replay`. Cache TTL = `2 × window` (600 s).
5. **Key generation**: only the device's *current* `keyId` and (for 120 s after a rotation)
   the *previous* one are accepted → else `401 esi_keyid`.
6. **Signature**: `HMAC-SHA256(sessionKey, canonicalString)` compared in
   **constant time** → mismatch `401 esi_signature`.
7. **Content hash**: recompute over the buffered body and compare (constant time).
8. **Scope check**: the endpoint's required scope must be granted to the device.
9. **Rate limit**: token bucket, per device and per source IP.

A body larger than `maxSignedBodyBytes` (default 8 MiB) is not signed: file upload uses a
dedicated `X-ESI-Upload-Token` short-lived ticket instead (§7.4).

### 4.4 Errors

```json
{ "error": { "code": "esi_signature", "message": "Signature mismatch", "requestId": "9c1f…" } }
```

| Code | HTTP |
|---|---|
| `esi_bad_request` | 400 |
| `esi_unauthorized` / `esi_signature` / `esi_replay` / `esi_timestamp` / `esi_keyid` | 401 |
| `esi_forbidden_network` | 403 |
| `esi_forbidden_scope` | 403 |
| `esi_not_found` | 404 |
| `esi_rate_limited` | 429 (with `Retry-After`) |
| `esi_path_not_allowed` | 403 |
| `esi_unsupported` | 501 |
| `esi_internal` | 500 |
| `esi_conflict` | 409 |

---

## 5. Key hierarchy

### 5.1 Provisioning / activation

At approval the agent creates:

* `deviceId` — 8 random bytes, lowercase hex (16 chars)
* `masterSecret` — 32 random bytes (CSPRNG)
* `keyId` — 1

`masterSecret` leaves the agent exactly once, wrapped (§5.4).

### 5.2 Derivation (HKDF-SHA256, RFC 5869)

```
salt  = "ESI-RemotePC/v1" (ASCII, 16 bytes)         -- fixed, non-secret
info  = UTF8("esi-session-v1|" + deviceId + "|" + keyId)
sessionKey = HKDF-SHA256(ikm = masterSecret, salt, info, L = 32)   -- signs requests

info  = UTF8("esi-wrap-v1|" + deviceId)
wrapKey     = HKDF-SHA256(ikm = masterSecret, salt, info, L = 32)   -- wraps rotations

info  = UTF8("esi-audit-v1|" + deviceId + "|" + keyId)
auditKey    = HKDF-SHA256(ikm = masterSecret, salt, info, L = 32)   -- log integrity
```

The **extract** step uses the salt as the HMAC *key* and `masterSecret` as the HMAC
*input* (RFC 5869 §2.1).

### 5.3 Token rotation

`POST /api/v1/auth/rotate` (requires the `admin` scope).

Request:

```json
{ "confirmDeviceId": "3f2a9c17d4b8e206" }
```

Response `200`:

```json
{ "deviceId": "3f2a9c17d4b8e206", "previousKeyId": 1, "keyId": 2,
  "graceSeconds": 120,
  "wrappedSecrets": { "nonce": "<b64,12B>", "ciphertext": "<b64,32B>", "tag": "<b64,16B>" } }
```

* `plaintext` = `masterSecret_new ‖ uint32be(keyId_new)` (36 bytes)
* `AES-256-GCM(key = wrapKey_old, nonce, aad = UTF8("esi-rotate-v1|" + deviceId), plaintext)`
* After success the agent's `masterSecret`/`keyId` are replaced. The **previous** key
  generation stays verifiable for `graceSeconds` so in-flight requests do not break.
* Rotation is the recovery path for a suspected key compromise; it is also what the PC
  triggers from the tray menu.

### 5.4 Delivery encryption

`wrappedSecrets` for the *initial* pairing uses a one-time `provisioningSecret`
(32 random bytes) that is **never sent to the requester over the network**. It is rendered
into a QR on the PC, and the phone obtains it only by scanning that code:

```
provisioningKey = HKDF-SHA256(ikm = provisioningSecret, salt = "ESI-RemotePC/v1",
                              info = UTF8("esi-provision-v1|" + pairingId), L = 32)
plaintext = masterSecret ‖ uint32be(keyId)   // 36 bytes
AAD       = UTF8("esi-pair-v1|" + pairingId + "|" + deviceId)
```

This closes the "anyone on the LAN races the approval" hole: the phone can only decrypt
the secrets if it is the device the human approved (it learned `provisioningSecret` from
the QR that was physically transferred / scanned).

**This is why the secret must not be returned by `POST /api/v1/pair/request`.** The
requester of a pairing attempt is unauthenticated and unverified; handing that requester
the provisioning secret would give an attacker exactly the capability the secret removes —
they could trigger a prompt, let a user approve it in good faith, and collect the
credentials. The transfer has to cross a channel only the user controls.

#### 5.4.1 The pairing URL

The QR encodes the polling URL itself, so a scan is all the phone has to do:

```
https://<host>:<port>/api/v1/pair/session?id=<pairingId>&secret=<base64url>&code=<userCode>
```

* `host` is the address the *requesting device* came from, because that is an address the
  phone can already reach. The agent's own idea of its LAN address would produce a code the
  phone cannot resolve when the two are on different interfaces.
* `secret` is `base64url(provisioningSecret)`, unpadded. base64url rather than standard
  base64 because the value travels in a query string, where `+` and `/` would need escaping.
  The agent accepts standard base64 as well.
* `code` is the six-digit code shown on the PC, carried so a phone that scans the code but
  types the number by hand can still confirm they are looking at the same attempt.

The URL is a single-use credential with the attempt's lifetime (default five minutes): the
attempt expires, and the secrets are released exactly once. A URL captured from a QR image
afterwards is worthless.

#### 5.4.2 Where the QR is available

| Source | Route | Notes |
|---|---|---|
| Desktop prompt | - | The session agent draws the QR into the approval dialog. |
| Browser | `GET /pairing/qr.svg?code=<6 digits>&host=<addr>` | SVG, keyed by the user code the phone displays. 404 if no pending attempt matches. |
| Landing page | `GET /pairing?code=<6 digits>` | The same QR inline, with the approval warning. |
| Console | `esirc pair pending` | Prints the polling URL when there is no session agent to show it. |

Keyed by the six-digit code rather than the internal attempt id, because the user is the
one who knows the code: they read it off the phone and type it in. It also keeps the
long-lived attempt id out of something a person retypes.

---

## 6. REST endpoints

Scope column: `—` = unauthenticated, `read`, `files`, `control`, `admin`.

### 6.1 Identity / auth

| Method | Path | Scope | Notes |
|---|---|---|---|
| `GET` | `/api/v1/auth/whoami` | `read` | Current device id, keyId, scopes, lastSeen, agent version |
| `POST` | `/api/v1/auth/rotate` | `admin` | §5.3 |
| `GET` | `/api/v1/devices` | `admin` | All paired devices |
| `DELETE` | `/api/v1/devices/{deviceId}` | `admin` | Revoke a paired device |
| `POST` | `/api/v1/devices/{deviceId}/rotate` | `admin` | Rotate another device's keys (returns `wrappedSecrets` for out-of-band delivery) |

### 6.2 System & metrics

| Method | Path | Scope | Query |
|---|---|---|---|
| `GET` | `/api/v1/system/info` | `read` | `detail=full` (default) or `detail=summary` |
| `GET` | `/api/v1/system/metrics` | `read` | `window=1000` (sample window ms, 250–10000) |
| `GET` | `/api/v1/system/storage` | `read` | `—` |
| `GET` | `/api/v1/system/network` | `read` | `—` |
| `GET` | `/api/v1/system/display` | `read` | `—` |
| `GET` | `/api/v1/system/processes` | `read` | `limit`, `sort=cpu\|memory\|name` |
| `POST` | `/api/v1/system/process/{pid}/terminate` | `control` | Graceful then force |

#### `GET /system/info` → 200

```json
{
  "hostName": "META-PC", "deviceName": "META-PC", "manufacturer": "Dell Inc.", "model": "XPS 15",
  "os": { "caption": "Windows 11 Pro", "displayVersion": "24H2", "version": "10.0.26100",
          "build": 26100, "revision": 2314, "architecture": "x64", "installDate": 1721000000,
          "lastBoot": 1755990000, "uptimeSeconds": 10000, "locale": "en-US" },
  "cpu": { "model": "13th Gen Intel(R) Core(TM) i7-13700H", "cores": 14, "threads": 20,
           "maxClockMhz": 4900, "baseClockMhz": 2600, "socket": "U3E1" },
  "gpu": [ { "name": "NVIDIA GeForce RTX 4060", "vendor": "nvidia", "vramBytes": 8589934592,
             "driverVersion": "…", "isPrimary": true } ],
  "memory": { "totalBytes": 34359738368, "slots": 2, "modules": [ { "sizeBytes": 17179869184,
             "speedMhz": 5200, "type": "DDR5", "manufacturer": "Samsung", "slot": "DIMM_A" } ] },
  "storage": [ { "deviceId": "C:", "label": "System", "fileSystem": "NTFS", "totalBytes": …,
                 "freeBytes": …, "driveType": "fixed", "isSystem": true } ],
  "bios": { "manufacturer": "Dell Inc.", "version": "1.24.0", "releaseDate": 1721000000 },
  "security": { "secureBoot": true, "tpmPresent": true, "defenderEnabled": true,
                "uacEnabled": true, "bitLockerStatus": "Protection On" },
  "power": { "supportsHibernate": true, "supportsSleep": true, "lastBootState": "S0LowPowerIdling",
             "activePowerScheme": "Balanced" },
  "sensors": { "cpuTemperatureC": 61.5, "gpuTemperatureC": 58, "cpuFanRpm": 2200,
               "gpuFanRpm": null, "cpuLoad": 22.4, "gpuLoad": 31.0, "gpuMemoryUsedBytes": …,
               "gpuMemoryTotalBytes": …, "sensorsAvailable": ["cpu","gpu"] },
  "agent": { "version": "1.0.0", "protocolVersion": 1, "uptimeSeconds": 3600,
             "debugMode": false, "sessionAgentConnected": true }
}
```

#### `GET /system/metrics` → 200

```json
{
  "capturedAt": 1756000000, "sampleWindowMs": 1000,
  "cpu": { "usagePercent": 12.4, "perCore": [3.1, 9.0], "userPercent": 7.2, "kernelPercent": 5.2,
           "temperatureC": 61.5, "clockMhz": 4200, "maxClockMhz": 4900, "processCount": 213,
           "threadCount": 1840 },
  "memory": { "totalBytes": 34359738368, "usedBytes": 16521576704, "availableBytes": …,
              "usedPercent": 48.1, "cachedBytes": …, "committedBytes": …, "commitLimitBytes": …,
              "swap": { "totalBytes": 0, "usedBytes": 0 } },
  "gpu": [ { "index": 0, "name": "NVIDIA GeForce RTX 4060", "usagePercent": 32.0,
             "memoryUsedBytes": 1207959552, "memoryTotalBytes": 8589934592,
             "temperatureC": 58, "fanRpm": 0, "powerWatts": null, "vendor": "nvidia",
             "source": "nvml" } ],
  "gpuAggregate": { "usagePercent": 32.0, "maxTemperatureC": 58, "dominantIndex": 0 },
  "storage": [ { "deviceId": "C:", "usedPercent": 78.2, "usedBytes": …, "freeBytes": …,
                 "totalBytes": …, "readBytesPerSecond": 1024, "writeBytesPerSecond": 4096 } ],
  "network": {
    "interfaces": [ { "name": "Wi-Fi", "description": "Intel(R) Wi-Fi 6E AX211", "type": "wifi",
                      "isUp": true, "macAddress": "AA:BB:CC:DD:EE:FF", "ipv4": "192.168.1.42",
                      "netmask": "255.255.255.0", "gateway": "192.168.1.1", "dns": ["192.168.1.1"],
                      "linkSpeedBitsPerSecond": 866000000, "mtu": 1500,
                      "downloadBytesPerSecond": 12500, "uploadBytesPerSecond": 3200,
                      "rxTotalBytes": …, "txTotalBytes": … } ],
    "primary": { "…same shape as an interface entry…", "gateway": "192.168.1.1",
                 "dns": ["1.1.1.1","8.8.8.8"], "publicIpHint": null }
  },
  "battery": null,
  "load": [ { "cpu": 12.4, "gpu": 32.0, "ram": 48.1, "ts": 1756000000 } ]
}
```

Field notes:

* `cpu.perCore` may be `[]` when the per-core PDH counters are unavailable.
* `gpu[].source` ∈ `nvml`, `intel-igcl`, `amd-adlx`, `pdh`, `unavailable`.
* Temperatures are `null` (never a fabricated number) when no trustworthy sensor exists.
* `network.primary` is the interface the agent considers primary (lowest metric, up, non-loopback).

### 6.3 Power

| Method | Path | Scope | Body |
|---|---|---|---|
| `GET` | `/api/v1/power/status` | `read` | — |
| `POST` | `/api/v1/power/{action}` | `control` | see below |

`action` ∈ `shutdown`, `restart`, `sleep`, `hibernate`, `lock`, `signout`, `forceRestart`.

```json
{ "force": false, "delaySeconds": 5, "message": "ESI Remote PC" }
```

`200` response:

```json
{ "accepted": true, "action": "restart", "effectiveMode": "hybrid", "scheduledAt": 1756000005,
  "warning": null, "requiresInteractiveSession": false }
```

Status:

```json
{ "state": "online", "isLocked": false, "isSleeping": false, "isHibernating": false,
  "activeSessionId": 2, "activeUser": "META-PC\\META", "loggedIn": true,
  "supportsSleep": true, "supportsHibernate": true, "lastBoot": 1755990000,
  "lastStateChange": 1755990000, "pendingPowerAction": null }
```

`state` ∈ `online`, `offline`, `sleeping`, `hibernating`, `locked`, `unknown`.

> `sleep` / `hibernate` are issued by the agent, but if a *user* session exists the action is
> routed through the session agent so the machine actually suspends the interactive session
> (this is what `rundll32 powrprof.dll,SetSuspendState` from session 0 gets wrong).
> `lock` and `signout` **always** require the interactive session and return
> `"requiresInteractiveSession": true` with a clear error if the session agent is offline.

### 6.4 Wake-on-LAN

| Method | Path | Scope | Notes |
|---|---|---|---|
| `POST` | `/api/v1/power/wake` | `control` | Body `{"macAddress":"AA:BB:CC:DD:EE:FF","broadcastAddresses":["192.168.1.255"],"ports":[9,7],"repeat":3}`. Normally the **phone** sends the magic packet directly; this endpoint exists for relaying from a gateway/relay node in the future. |
| `GET` | `/api/v1/power/wol/readiness` | `read` | Which local adapters are armed, the recommended broadcast addresses, whether the machine is hiberboot-enabled (which breaks restart-WoL). |

### 6.5 App launcher

| Method | Path | Scope |
|---|---|---|
| `GET` | `/api/v1/apps` | `read` |
| `POST` | `/api/v1/apps/launch` | `control` — body `{"id":"steam"}` or `{"path":"…","args":[…]}` |
| `POST` | `/api/v1/apps/{id}/terminate` | `control` |
| `GET` | `/api/v1/apps/running` | `read` |

```json
{ "apps": [
  { "id": "chrome", "name": "Google Chrome", "category": "browser", "path": "…\\chrome.exe",
    "iconPath": null, "arguments": ["--start-maximized"], "workingDirectory": null,
    "runAs": "interactiveUser", "enabled": true, "builtIn": true, "pinned": true,
    "lastLaunched": 1756000000, "categoryIcon": "public" }
] }
```

`runAs` ∈ `interactiveUser` (default — appears on the user's desktop), `service` (session 0,
no UI), `elevatedUser` (interactive + elevated via the session agent).

### 6.6 File manager (sandboxed)

| Method | Path | Scope | Notes |
|---|---|---|---|
| `GET` | `/api/v1/files/roots` | `read` | The configured allowed roots + a `readOnly` flag each |
| `GET` | `/api/v1/files/list` | `files` | `path` (optional → default root), `showHidden` |
| `GET` | `/api/v1/files/stat` | `files` | `path` |
| `POST` | `/api/v1/files/mkdir` | `files` | `{"path":"…"}` |
| `POST` | `/api/v1/files/rename` | `files` | `{"from":"…","to":"…"}` |
| `POST` | `/api/v1/files/move` | `files` | `{"from":"…","to":"…"}` |
| `DELETE` | `/api/v1/files` | `files` | `path` (query) |
| `GET` | `/api/v1/files/download` | `files` | `path`; streams with `Content-Disposition: attachment` |
| `POST` | `/api/v1/files/upload` | `files` | `path` (target file), raw body = content |
| `POST` | `/api/v1/files/upload-ticket` | `files` | Body `{"path":"…","size":123}` → short-lived upload ticket (§7.4) |
| `GET` | `/api/v1/files/search` | `files` | `root`, `query`, `maxResults` |
| `GET` | `/api/v1/files/recent` | `files` | Recently accessed files, from the agent's own history table |

Listing entry:

```json
{ "name": "Videos", "path": "C:\\Users\\META\\Videos", "isDirectory": true, "sizeBytes": 0,
  "lastModified": 1755999000, "isHidden": false, "isReadOnly": false, "childCount": 12,
  "extension": null, "mimeType": null, "canDelete": true, "canRename": true }
```

**Sandbox rules (agent-enforced, non-negotiable):**

1. A default allowed-root set is created on install: the interactive user's `Desktop`,
   `Documents`, `Downloads`, `Music`, `Pictures`, `Videos`.
2. Every path is canonicalised: relative → rooted at the default root; forward slashes →
   backslashes; the longest **reparse point / symlink / junction** chain is resolved; the
   final path must be compared with `OrdinalIgnoreCase` against the roots.
3. Rejected outright: paths escaping the root after canonicalisation, `\\?\`,
   `\\.\`, NT device paths, UNC paths not under a root, alternate data streams
   (`file.txt:stream`), `CON`/`NUL`/`COM1`..`LPT9` device names, and 8.3 aliases that
   resolve outside the root.
4. `readOnly` roots reject every mutating verb.
5. Deletes are refused for the root itself and for any directory that is an ancestor of a root.

### 6.7 Media control

| Method | Path | Scope | Body |
|---|---|---|---|
| `GET` | `/api/v1/media/state` | `read` | — |
| `POST` | `/api/v1/media/{action}` | `control` | optional `{"step":5}` for volume |

`action` ∈ `play`, `pause`, `playPause`, `stop`, `next`, `previous`, `volumeUp`, `volumeDown`,
`volumeSet`, `mute`, `unmute`, `toggleMute`.

`GET /media/state` → `200`:

```json
{ "volume": 42, "isMuted": false, "defaultDevice": "Speakers (Realtek(R) Audio)",
  "availableActions": ["play","pause","next","previous","volumeUp","volumeDown","mute"],
  "sessionPeak": 78 }
```

### 6.8 Remote control

| Method | Path | Scope | Notes |
|---|---|---|---|
| `GET` | `/api/v1/remote/session` | `read` | Session-helper status, display list, capabilities |
| `POST` | `/api/v1/remote/input` | `control` | Single-shot input event (fallback / accessibility) |
| `DELETE` | `/api/v1/remote/session` | `control` | End the streaming session |

The real time-stream is the WebSocket (§7.3).

### 6.9 Gaming mode

| Method | Path | Scope |
|---|---|---|
| `GET` | `/api/v1/gaming/state` | `read` |
| `POST` | `/api/v1/gaming/mode` | `control` — `{"enabled":true,"profileId":"performance"}` |
| `GET` | `/api/v1/gaming/profiles` | `read` |
| `POST` | `/api/v1/gaming/apps/{id}/state` | `control` — `{"enabled":false}` suspend/resume an app |
| `GET` | `/api/v1/gaming/fps` | `read` — best-effort presented-frame counter |

`GET /gaming/state`:

```json
{ "mode": "on", "profileId": "performance", "activePowerScheme": "Ultimate Performance",
  "fps": 144, "fpsSource": "dwm-present-rate", "cpuUsagePercent": 88, "gpuUsagePercent": 97,
  "ramUsedPercent": 62, "gpuTemperatureC": 74, "gpuMemoryUsedBytes": …, "pingMs": 3,
  "networkDownBytesPerSecond": …, "networkUpBytesPerSecond": …,
  "suspendedApps": ["discord"], "launchGameAppId": "steam-game-halo",
  "history": [ { "ts": 1756000000, "fps": 144, "cpu": 88, "gpu": 97, "temp": 74 } ] }
```

Enabling gaming mode applies the selected power scheme, suppresses non-critical
notifications, and (optionally) suspends the configured background apps. Disabling it
restores the previously captured state exactly.

### 6.10 Notifications

| Method | Path | Scope |
|---|---|---|
| `GET` | `/api/v1/notifications` | `read` — `unreadOnly`, `limit`, `since` |
| `POST` | `/api/v1/notifications/{id}/read` | `read` |
| `POST` | `/api/v1/notifications/read-all` | `read` |
| `DELETE` | `/api/v1/notifications/{id}` | `read` |
| `DELETE` | `/api/v1/notifications` | `read` — clears all |
| `GET` | `/api/v1/notifications/rules` | `admin` |
| `PUT` | `/api/v1/notifications/rules` | `admin` |

```json
{ "notifications": [
  { "id": 42, "kind": "power", "severity": "warning", "title": "PC shutdown requested",
    "body": "Restart was requested from Pixel 9", "createdAt": 1756000000, "isRead": false,
    "source": "agent", "data": { "action": "restart", "actor": "Pixel 9" } }
] }
```

`kind` ∈ `power`, `system`, `alert`, `app`, `file`, `security`, `gaming`, `network`.
`severity` ∈ `info`, `warning`, `critical`.

Built-in rules (all configurable):

| id | Condition | severity |
|---|---|---|
| `cpu.high` | `cpu.usagePercent >= 90` for 60 s | warning |
| `cpu.critical` | `cpu.usagePercent >= 98` for 30 s | critical |
| `gpu.temp.high` | `gpuTemperatureC >= 83` for 30 s | warning |
| `gpu.temp.critical` | `gpuTemperatureC >= 90` | critical |
| `ram.high` | `memory.usedPercent >= 92` for 120 s | warning |
| `disk.low` | any fixed volume `usedPercent >= 90` | warning |
| `disk.critical` | any fixed volume `usedPercent >= 97` | critical |
| `battery.low` | battery `< 20 %` | warning |
| `agent.started` | agent process start | info |
| `agent.pairing` | a new device pairing attempt | info |
| `system.boot` / `system.shutdown` | boot or shutdown event | info |
| `app.launched` | a configured app was launched | info |
| `file.transfer` | upload/download completed or failed | info |

### 6.11 Settings

| Method | Path | Scope |
|---|---|---|
| `GET` | `/api/v1/settings` | `admin` |
| `PUT` | `/api/v1/settings` | `admin` |
| `POST` | `/api/v1/settings/reset` | `admin` |
| `GET` | `/api/v1/logs` | `admin` — `tail`, `level`, `since` |
| `GET` | `/api/v1/audit` | `admin` — append-only security audit trail |

Settings shape (partial; the full schema lives in `SettingsModel.cs`):

```json
{ "port": 48220, "discoveryEnabled": true, "discoveryPort": 48211, "mdnsEnabled": true,
  "startWithWindows": true, "wakeOnLanEnabled": true,
  "security": { "localNetworkOnly": true, "requireSignedRequests": true,
                "allowedSubnets": ["192.168.0.0/16","10.0.0.0/8","172.16.0.0/12"],
                "clockSkewSeconds": 300, "maxPairedDevices": 8,
                "rateLimitPerMinute": 240, "pairingWindowSeconds": 120,
                "sessionAgentRequiredForLock": true, "tlsEnabled": true },
  "metrics": { "sampleIntervalMs": 1000, "historyLimit": 300, "collectGpu": true,
               "collectTemperatures": true },
  "files": { "allowedRoots": [ { "path": "C:\\Users\\META\\Downloads", "readOnly": false } ],
             "allowDelete": true, "allowUpload": true, "maxUploadBytes": 2147483648 },
  "apps": [ /* configured launcher entries */ ],
  "gaming": { "enabled": false, "profileId": "performance",
              "suspendApps": [], "launchAppId": null },
  "notifications": { "rules": [ /* … */ ], "retentionDays": 14, "toastEnabled": true },
  "debug": { "enabled": false, "verboseLogging": false, "allowLoopbackOnly": false } }
```

---

## 7. WebSocket `/ws`

### 7.1 Handshake

The upgrade request carries the same `X-ESI-*` **headers** as REST, except:

* the signature covers the path `/ws` with an **empty** body, and the canonical target is
  `/ws` with no query string.

Headers, not query parameters. An earlier draft of this document specified query parameters,
on the reasoning that a signature in a URL is more likely to be logged. That reasoning was
backwards: query parameters are the form that intermediaries, proxies, browser history and
server access logs record by default, whereas a header is not. Both implementations use
headers, and this section has been corrected to match. `RequestVerifier` reads only
`X-ESI-*` headers, so a client following the old text would have failed every upgrade with
`esi_signature`.

If signing fails during upgrade the server responds `401` **before** switching protocols.

### 7.1.1 Scope on the socket

The upgrade itself requires only `read`, because subscribing and streaming are read-only.
Every frame is then authorised **individually** against `WebSocketHub.FrameScope`:

| Frame types | Scope |
|---|---|
| `remote.start`, `remote.stop`, `remote.input`, `remote.key` | `control` |
| `gaming.watch`, `gaming.set` | `control` |
| `apps.launch`, `apps.terminate` | `control` |
| `media.command`, `power.command` | `control` |
| `files.write` | `files` |
| everything else (`subscribe`, `ping`, `unsubscribe`, …) | `read` |

This is not redundant with the HTTP scope table. A single socket multiplexes subscriptions
with commands that drive the mouse and the keyboard, so a device holding only `read` could
otherwise open the socket and inject input — the `control` requirement on
`POST /api/v1/remote/input` would be trivially bypassed. A frame that fails the check is
answered with `error` / `esi_forbidden_scope` and has no effect.

### 7.2 Client → server control frames

```json
{ "p":1, "t":"subscribe",   "id":"s1", "channels":["metrics","notifications","events"] }
{ "p":1, "t":"unsubscribe", "id":"s2", "channels":["metrics"] }
{ "p":1, "t":"ping",        "id":"p1", "ts": 1756000000 }
{ "p":1, "t":"remote.start","id":"r1", "displayId":0, "fps":15, "quality":65, "grid":[4,4],
                       "inputEnabled":true }
{ "p":1, "t":"remote.stop", "id":"r2" }
{ "p":1, "t":"remote.input","id":"r3", "input": { "…": "see §7.3" } }
{ "p":1, "t":"remote.key",  "id":"r4", "code":"KeyA", "text":"a", "down":true, "mods":["ctrl"] }
{ "p":1, "t":"gaming.watch","id":"g1", "intervalMs":1000 }
{ "p":1, "t":"audio.start", "id":"a1" }
{ "p":1, "t":"audio.stop",  "id":"a2" }
```

`audio.start` opens the PC's default output device for loopback capture and streams what is
playing there back down the same socket, so the phone can be the PC's speaker. It carries no
parameters: the PC captures its own default device, so there is nothing to choose. Both frames
require the `control` scope. The reply is an `audio.state` frame (§7.5) whose `active` field
reports whether the *request* was accepted - the device opens asynchronously and can still
refuse afterwards, so the honest confirmation is the phone's meter moving, and any later
failure is in the agent's log.

### 7.3 Remote input schema

```jsonc
{ "kind": "mouseMove", "x": 0.42, "y": 0.61 }              // normalised 0..1
{ "kind": "mouseButton", "button": "left|right|middle", "down": true, "x":0.5,"y":0.5 }
{ "kind": "mouseWheel", "deltaX": 0, "deltaY": -3 }        // notches, positive = away from user
{ "kind": "key", "code": "KeyA", "text": "a", "down": true, "mods": ["ctrl","shift"] }
{ "kind": "text", "text": "hello" }                        // unicode text injection
```

`code` uses the **W3C UI Events KeyboardEvent.code** values (`KeyA`, `Digit1`, `ArrowUp`,
`Enter`, `ShiftLeft`, `F5`, …) plus `Numpad*`. The agent maps them to Windows virtual-key
codes and scan codes; unknown codes fall back to `text` injection via `KEYEVENTF_UNICODE`,
so every physical keyboard is at least partially usable even before a mapping table is
complete.

### 7.4 File upload tickets

Signed request bodies are capped. For large uploads:

```
POST /api/v1/files/upload-ticket  {"path":"C:\\...\\movie.mkv","size":123456789}
  → 201 {"ticketId":"…","expiresAt":…,"maxBytes":123456789,"uploadUrl":"/api/v1/files/upload?ticket=…"}

PUT  /api/v1/files/upload?ticket=…   Content-Type: application/octet-stream
  <raw body>
  → 200 {"completed":true,"path":"…","bytesWritten":123456789,"sha256":"…"}
```

The ticket is single-use, bound to `(deviceId, path, size)`, and expires in 120 s.

### 7.5 Server → client frames

```json
{ "p":1, "t":"hello", "id":null, "data": { "…": "handshake §3.1 body" } }
{ "p":1, "t":"pong", "id":"p1", "ts": 1756000000 }
{ "p":1, "t":"subscribed", "id":"s1", "channels":["metrics"] }
{ "p":1, "t":"metrics", "id":null, "data": { "…": "/system/metrics body" } }
{ "p":1, "t":"notification", "id":null, "data": { "…": "notification object" } }
{ "p":1, "t":"event", "id":null, "data": { "kind":"power.state", "state":"sleeping" } }
{ "p":1, "t":"gaming", "id":null, "data": { "…": "/gaming/state body" } }
{ "p":1, "t":"remote.state", "id":"r1", "data": { "active":true, "displayId":0,
                                                "width":2560, "height":1440, "fps":15 } }
{ "p":1, "t":"audio.state",  "id":"a1", "data": { "active":true, "detail":null } }
{ "p":1, "t":"error", "id":"r3", "code":"esi_forbidden_scope", "message":"…" }
```

**Binary frames** (`opcode = 2`) carry the screen stream (§8) and, when `audio.start` is in
force, the loopback audio stream. The first 32 bytes are a fixed header, then the payload:

```
offset  size  field
0       4     magic "ESIV" (0x56 53 49 45 little-endian == 'E','S','I','V')
4       2     frameType (1 = keyframe, 2 = tile update, 3 = tile request/ack, 5 = audio chunk)
6       2     displayId
8       4     frameIndex (monotonic per session)
12      2     tileCount
14      2     codec (1 = JPEG)
16      4     screenWidth
20      4     screenHeight
24      2     gridCols
26      2     gridRows
28      4     payloadLength
--- descriptor table: tileCount x 14 bytes, contiguous, starting at offset 32 ---
        2     col
        2     row
        2     tileWidth
        2     tileHeight
        4     tileLength
--- payload region: starts immediately after the LAST descriptor; the JPEGs follow ---
        ...   tileLength bytes of JPEG, in the order the descriptors listed
        (so the payload region holds payloadLength bytes and nothing else)
```

For `frameType == 3` the payload is just a `frameIndex` + the list of tiles the client
wants resent (used when a tile packet is lost on a lossy Wi-Fi link).

For `frameType == 5` (audio chunk) the header is the same 32 bytes, but its picture-shaped
members are reused for the audio format rather than being left as dimensions:

```
codec (offset 14)      3 = µ-law (G.711)
screenWidth  (offset 16)   sample rate in Hz (24000)
screenHeight (offset 20)   channel count (2)
gridCols     (offset 24)   samples per channel (480)
gridRows     (offset 26)   unused
payloadLength(offset 28)   µ-law bytes (= samples per channel x channels)
tileCount               0, and no descriptor table follows
```

The payload is `payloadLength` µ-law bytes, one per sample, channels interleaved. 480 samples
per channel at 24 kHz is 20 ms per packet: fifty packets a second, small enough that a lost one
is a gap no longer than a blink, large enough that the header is not worth mentioning. The
struct was not widened to hold real audio fields because it is fixed at 32 bytes and both ends
read it by offset - moving any field would break the screen stream already deployed.

A frame is an audio chunk only when **both** the frame type and the codec agree. One without
the other is malformed, not a new codec, and is dropped.

### 7.6 Keepalive

The client sends `{"t":"ping"}` every 20 s. The server replies `{"t":"pong"}`. The server
closes the socket if no client frame arrives for 45 s, and closes after 30 consecutive
failed sends.

---

## 8. Screen streaming design (v1, upgradeable to full RDP)

v1 is deliberately **not** "RDP in a bad wrapper". It is a delta-stream designed so that
the pieces a real RDP codec needs are already in the right places:

1. **Display enumeration** is via `EnumDisplayMonitors` + `QueryDisplayConfig`, and the
   agent already tracks monitor rectangles, scale factors and which monitor is primary.
   A future RDP path replaces only the transport and adds the RDP graphics pipeline.
2. **Capture is abstracted** behind `IScreenCaptureProvider`. v1 ships
   `GdiScreenCaptureProvider` (`BitBlt` of the monitor DC) which works on every machine
   including RDP sessions and VMs. `DxgiDesktopDuplicationProvider` (Desktop Duplication
   API) is the drop-in high-performance provider — it returns the same
   `CapturedFrame { pixelFormat, stride, width, height, buffer }`, so nothing above the
   abstraction changes.
3. **Dirty detection** is a tile hash grid. Tiles that did not change are simply not sent.
   This is a genuine bandwidth optimisation, not a placeholder.
4. **Input is a normalised, resolution-independent event stream** with a monotonic
   `sequence` per session that the client echoes back, so a real input pipeline can
   deduplicate and reorder. Adding clipboard, multi-monitor coordinates and keyboard
   scancode remapping does not change the wire shape.

Explicitly **not** in v1, and why: multi-monitor spanning as one canvas, clipboard sync,
audio redirection, drive redirection, cursor shape/position updates, and true
DXGI Desktop Duplication. Each is a self-contained module behind an existing seam.

---

## 9. Discovery

### 9.1 UDP broadcast (primary, works on every Android version without extra permissions)

Port `48211`, UDP. The client sends a datagram whose payload is the ASCII string
`ESI_DISCOVER_V1` (optionally followed by a newline). The agent answers with a single
JSON datagram equal to the `GET /handshake` body, to the *source* address.

`255.255.255.255` **and** the per-interface subnet-directed broadcast address are both
tried, because many access points and mobile stacks drop the limited broadcast.

No response means "offline or blocked" — discovery is *hints*, never identity. Every
field learned here is re-confirmed cryptographically at pairing time.

### 9.2 mDNS / DNS-SD (secondary, RFC 6762 + 6763)

The agent advertises `_esiremotepc._tcp.local.` with:

| RR | Name | rdata | TTL |
|---|---|---|---|
| PTR | `_esiremotepc._tcp.local.` | `"<DeviceName>._esiremotepc._tcp.local."` | 4500 |
| SRV | `<DeviceName>._esiremotepc._tcp.local.` | `0 0 <port> <hostname>.local.` | 120 |
| TXT | `<DeviceName>._esiremotepc._tcp.local.` | `v=1`, `id=<deviceId>`, `name=<DeviceName>`, `path=<api path>`, `pin=<spkiSha256>` | 4500 |
| A | `<hostname>.local.` | LAN IPv4 | 120 |
| AAAA | `<hostname>.local.` | LAN IPv6 | 120 |

Instance names are de-duplicated with the RFC 6762 §9.4 `2`/`3` suffix rule, answers are
delayed a random 20–120 ms, a record is never multicasted more than once per second, and
a goodbye packet (TTL 0) is sent on clean shutdown. Unknown questions get an explicit
NSEC negative answer for the types the agent owns.

The Android app also runs a best-effort mDNS query; failures there are non-fatal because
UDP broadcast already covers discovery.

### 9.3 Static / manual entries

A user can always add a PC by hand: `name`, `host` (IP or `.local` name), `macAddress`,
`port`, and — when a desktop prompt is not available — the six-digit `userCode` the phone
generated, so `https://<host>:<port>/pairing?code=<code>` on the PC displays the pairing
QR (see 5.4.2). The manually entered `macAddress` is what the app uses for Wake-on-LAN.

There is no "Show pairing QR" tray item. Earlier drafts referred to one; the QR lives in the
approval prompt, on the `/pairing` page and at `/pairing/qr.svg`, and those are the only
places it exists.

### 9.4 Getting the secret without a display

The provisioning secret reaches the phone through the QR, which needs something to look at.
On a headless machine there is nothing, so the agent also accepts a console route:

```
esirc pair url <code>
```

which prints the same polling URL the QR encodes. This is a local administrative command
that already requires the ability to rewrite the pairing database, and the secret only ever
releases credentials to whoever presents it, so it widens nothing that `/pairing` does not
already expose.

What deliberately does **not** exist is a way to approve from the browser. The `/pairing`
page is reachable by anything on the LAN, and the user code is six digits, so a page that
could approve would let an attacker approve a pairing attempt they started themselves -
which is precisely what the desktop prompt exists to prevent. Approval needs a signed-in
user at the prompt, or the console. `esirc pair approve` therefore requires the service to
be stopped first, and says so in full when it will not proceed.

---

## 10. Compatibility & versioning rules

* New optional JSON fields may be added in v1 at any time. Clients MUST ignore unknown fields.
* New endpoints may be added in v1; clients MUST treat `404 esi_not_found` on a known path
  as "feature not supported by this agent version" and degrade gracefully.
* A `2` in the protocol major means the handshake/auth scheme changed; the agent will
  answer `GET /handshake` with `"protocolVersion": 2` and clients must refuse.
* The `features` map in the handshake exists so the app can grey out unavailable modules
  rather than showing a failing button.

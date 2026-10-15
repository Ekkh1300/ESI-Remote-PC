# ESI Remote PC — Security Model

Threat model and design rationale for the Windows Agent and the Mobile App. The wire
contract is normative in [`PROTOCOL.md`](PROTOCOL.md); this document explains what the
design defends against, what it deliberately does not defend against, and where the
implementation departs from the specification.

Read the *Limitations* section (§11) before deploying this anywhere that matters. Several
of the limits are structural, not bugs that a later release will close.

---

## 1. Assumptions

| Assumption | Consequence if false |
|---|---|
| The PC and the phone are on the same physical or logical LAN | The agent refuses the connection outright (`esi_forbidden_network`) |
| The Windows account running the agent has not been compromised | Nothing below applies — see §11.1 |
| The phone's screen lock and Android keystore are intact | The master secret can be extracted and the PC fully controlled |
| A human is present to approve a pairing request | An attacker on the LAN can race the approval — see §3.2 |

The agent runs as `LocalSystem` in session 0 and is granted `SeTcbPrivilege`,
`SeShutdownPrivilege` and `SeAssignPrimaryTokenPrivilege`
(`installer/ESI-RemotePC.iss`, `src/ESI.RemotePc.Agent.Cli/ServiceManager.cs`). It can do
a great deal. The security model is about restricting *remote* attackers, not about
restricting whoever controls the machine.

---

## 2. What the design defends against

| Threat | Defence |
|---|---|
| Eavesdropping on the LAN | TLS with a pinned certificate (§4) |
| Man-in-the-middle between phone and PC | SPKI pinning delivered out of band at pairing; a substituted certificate is rejected, not warned about |
| A captured request being replayed verbatim | Nonce cache plus a ±300 s timestamp window (`RequestVerifier.cs`) |
| A captured request being replayed against a *different* endpoint | Method and canonical path are inside the MAC (§5) |
| An attacker on the LAN silently pairing a rogue device | Mandatory human approval with denial as the default button (§3) |
| A rogue device winning the race for the approval popup | The secrets are wrapped under a provisioning secret the attacker never sees (§3.2) |
| A paired phone being disabled or revoked | `DELETE /api/v1/devices/{deviceId}`; the next request fails at device lookup |
| A stolen or leaked master secret | `POST /api/v1/auth/rotate` mints a new one; the old key stops verifying after the grace window (§6) |
| A compromised phone reaching beyond its grant | Scope enforcement per route, in one table (`RequestSignatureValidator.RequiredScope`) |
| Reading the PC's filesystem outside the granted roots | `FileSandbox` canonicalises and re-validates every path (§8) |
| A stolen copy of `agent.db` yielding usable credentials | Device master secrets are DPAPI-protected at rest (§9) |
| Silent rewriting of the audit history | The log is a SHA-256 hash chain; `GET /api/v1/audit` reports chain validity (§10) |
| Unauthenticated brute force | Separate, much tighter rate-limit buckets for pre-auth routes and pairing (§7) |

---

## 3. Pairing

### 3.1 Why a human must approve

`POST /api/v1/pair/request` is the only unauthenticated write endpoint in the product. Any
device that can reach the agent on the LAN can call it. Without a human decision, an
attacker on the same coffee-shop Wi-Fi pairs a device, receives a master secret, and can
then shut the machine down, read the user's Documents, or inject keystrokes into a bank.

The design therefore makes approval unavoidable rather than merely encouraged:

* `PairingManager.Create` mints a 6-digit `userCode` and a 32-byte `provisioningSecret`,
  then asks the session agent to show a modal dialog
  (`src/ESI.RemotePc.Session/PairingPromptForm.cs`).
* The dialog's **Deny** is the cancel/default path. Closing the window, pressing Escape,
  and letting the countdown expire are all denials. Approval requires a deliberate click on
  a button that names the device (`OnFormClosing` forces `PairingDecision.Denied` on any
  undecided exit).
* If no interactive session exists, no prompt is shown. The attempt stays pending and
  `esirc pair approve <code>` becomes the only route — an explicit, local, physical act.

### 3.2 Closing the approval race

Human approval alone is not sufficient. An attacker can call
`POST /api/v1/pair/session?id=...` in a loop while the human is looking at the dialog, and
be the first to collect the secrets.

`PairingManager.Poll` closes this. The envelope is sealed with AES-256-GCM under
`provisioningKey = HKDF(provisioningSecret, salt, "esi-provision-v1|" + pairingId)`, and
that `provisioningSecret` is only ever shown to the human (encoded in the QR, or typed
onto the phone). An attacker who wins the poll receives ciphertext it cannot decrypt.
Delivery is single-use, claimed with a conditional `UPDATE ... WHERE secrets_delivered = 0`
so two concurrent polls cannot both win.

**Implementation caveat.** The Windows Agent contains no code that renders a pairing QR
code. The tray menu offers "Open pairing page" and "Remote control status" only, and the
pairing page is an HTML card showing the device name, device id, SPKI pin and API base —
it does not include a `provisioningSecret`. The dialog says "Type this code on the phone",
and the six-digit `userCode` is the only human-transferred value. `PairingManager.Poll`
treats the `provisioningSecret` as mandatory (`status: "error"` when it is absent or the
wrong length), so a phone that has not obtained one through some other channel cannot
complete pairing. This is documented in `PROTOCOL.md` §5.4 and §9.3 as though the emitter
exists. See §12.

---

## 4. TLS and SPKI pinning

The agent generates a **self-signed ECDSA P-256** certificate on first run
(`src/ESI.RemotePc.Agent.Core/Security/CertificateAuthority.cs`):

* Subject `CN=<ComputerName>`; SANs for `<ComputerName>`, `<ComputerName>.local`,
  `esiremotepc-<port>.local`, and every non-loopback IPv4 address present at generation
  time.
* Validity 825 days, `notBefore` one day in the past.
* Extended key usage `serverAuth`; `basicConstraints` `CA=false`, marked critical. It is an
  end-entity certificate that happens to be self-signed.
* The private key must be **persisted**, not ephemeral — Kestrel rejects `EphemeralKeySet`
  outright — so it is placed in the machine key store as `LocalSystem` and in the user key
  store in console mode, both with `PersistKeySet | Exportable`.
* The PFX on disk is wrapped with DPAPI (entropy `ESI-RemotePC/v1/tls`), so a copy of
  `agent.pfx` is useless to another account and, in machine scope, on another machine.

The pin is `base64(SHA-256(DER(SubjectPublicKeyInfo)))`, computed from the certificate's
RSA or ECDSA public key. Pinning the SubjectPublicKeyInfo rather than the whole
certificate means a reissue with a new serial or validity window does not invalidate
paired devices.

`GET /api/v1/handshake` returns the pin **unauthenticated**. A man-in-the-middle can lie
about that value, which is exactly why the out-of-band path (QR / typed code) is the
authoritative one. A client that trusts the handshake pin alone has no protection against
an active attacker who is present before pairing completes.

**Client authentication is not mutual TLS.** `KestrelTuning` sets
`ClientCertificateMode.NoCertificate`. The phone authenticates with a signed request on
every call, which is the property that matters; a client certificate would add ceremony
without adding security here.

**No certificate revocation.** `CertificateAuthority.Reset()` deletes the PFX, which changes
the pin and invalidates every paired device. That is the recovery path, and it is
destructive by design. There is no CRL, no OCSP, and no short-lived certificate.

**Plaintext fallback.** If TLS is disabled by configuration, or certificate generation
fails, Kestrel listens on plain HTTP and the agent logs a warning
(`AgentHost.KestrelTuning`). Request signing still protects integrity and authenticity,
but not confidentiality. Start the agent with `--http true` and every byte, including file
downloads and the screen stream, is on the wire in the clear.

---

## 5. Request signing

See [`PROTOCOL.md`](PROTOCOL.md) §4 for the header list and the canonical string. The
implementation is `src/ESI.RemotePc.Agent.Core/Security/RequestVerifier.cs`, which performs
the verification in exactly the order the specification gives, cheapest and most selective
first:

1. Header shape and length bounds — no I/O.
2. Device lookup and `Enabled` check — one indexed read.
3. Timestamp window, `|now - ts| <= clockSkewSeconds` (default 300).
4. Nonce replay.
5. Key generation — current, or previous inside the rotation grace window.
6. `HMAC-SHA256(sessionKey, canonicalString)`, compared with
   `CryptographicOperations.FixedTimeEquals`.
7. `SHA-256(body)` against `X-ESI-Content-Sha256`, also constant-time.

The body is buffered before the handler runs so the signature covers the exact bytes that
arrived, and the stream is rewound so Minimal API's JSON binder sees the same bytes
(`RequestContextMiddleware.BufferBodyAsync`). Bodies above `MaxSignedBodyBytes` (8 MiB)
are not buffered at all and cannot be signed; the file upload path uses a single-use
ticket instead.

Comparison is constant-time, but note that both `SignatureEquals` and the nonce check
length-check first, and the header length bounds (nonce 16–64 chars, signature 20–128
chars) reject implausible values before any HMAC is computed. That is a shape filter, not
a timing oracle: a correct signature is never distinguishable from an incorrect one of the
same length.

### Key hierarchy

`src/ESI.RemotePc.Protocol/EsiCrypto.cs` implements HKDF-SHA256 (RFC 5869) with
`HMACSHA256(salt, ikm)` as the extract step, salt = `"ESI-RemotePC/v1"`:

| Purpose | `info` | Length |
|---|---|---|
| Request signing | `esi-session-v1\|{deviceId}\|{keyId}` | 32 |
| Rotation envelope | `esi-wrap-v1\|{deviceId}` | 32 |
| Audit chain (declared, unused — see §10) | `esi-audit-v1\|{deviceId}\|{keyId}` | 32 |
| Initial pairing envelope | `esi-provision-v1\|{pairingId}` | 32 |

Device ids are lowercased before derivation, matching the canonical string and the device
table lookup, so one identifier has exactly one spelling on the wire.

---

## 6. Replay protection and token rotation

**Nonce cache.** A `ConcurrentDictionary` per device, in memory, swept every 30 s, with
entries retained for `2 × clockSkewSeconds`. A nonce is rejected on the second sighting
for that device. The cache is in memory rather than SQLite deliberately: it only has to
outlive the replay window, and a write per authenticated request would put the metrics
path in contention for no benefit. The consequence is that **an agent restart empties the
cache**, so a request captured before a restart can be replayed afterwards if it is still
inside the 300 s window. Rotation clears the cache explicitly (`RequestVerifier.ResetNonces`)
so in-flight requests signed with the outgoing key are not spuriously rejected.

**Rotation.** `POST /api/v1/auth/rotate` replaces the device's `masterSecret` and
increments `keyId`. The new secret is sealed under the **old** wrap key, so the phone can
unseal it with what it already holds, and the previous key generation is recorded with a
120 s grace deadline.

Two honest notes on the rotation grace period:

* The grace window does not actually let an old key keep working. `RequestVerifier` accepts
  the previous `keyId` at step 5, but then derives the session key from
  `device.MasterSecret`, which `ApplyRotation` has already replaced with the new secret.
  A phone still holding the old secret therefore fails at step 6 with `esi_signature`, not
  at step 5 with `esi_keyid`. The window is a header-tolerance window, not a working
  dual-key window.
* Rotation has no out-of-band completion. `POST /api/v1/auth/rotate` returns
  `wrappedSecrets` with `requiresManualDelivery: true`; only the calling device can unseal
  it. `POST /api/v1/devices/{deviceId}/rotate` is the recovery path when a phone has lost
  its key — the caller receives the envelope and must hand it to the phone by some means
  that is not specified in the protocol. **There is no CLI or tray command that performs
  key recovery.**

---

## 7. Scopes and rate limiting

### Scopes

`DeviceScope` is a flags enum: `Read`, `Files`, `Control`, `Admin`, `All`.
`RequestSignatureValidator.RequiredScope(path, method)` maps prefixes, most specific
first:

| Path prefix | GET/HEAD | Everything else |
|---|---|---|
| `/api/v1/auth/rotate`, `/api/v1/devices`, `/api/v1/settings`, `/api/v1/logs`, `/api/v1/audit` | `admin` | `admin` |
| `/api/v1/files` | `files` | `files` |
| `/api/v1/power`, `/api/v1/apps`, `/api/v1/media`, `/api/v1/remote`, `/api/v1/gaming` | `read` | `control` |
| `/api/v1/system/process` | `read` | `control` |
| everything else | `read` | `read` |

The file manager requires `files` for reads as well as writes: enumerating a directory
tree is about as sensitive as writing to it, and `GET /api/v1/files/roots` is part of that
file manager. (`PROTOCOL.md` §6.6 used to list it as `read`; that row was wrong and has been
corrected, so this note records the history rather than a live discrepancy.)

### Rate limiting

Token buckets, refilled continuously, keyed by device and by source address
(`src/ESI.RemotePc.Agent.Core/Security/RateLimiter.cs`):

| Category | Key | Sustained | Burst |
|---|---|---|---|
| `api` | `device:{id}:api` | 240 / min | 60 |
| `api` | `addr:{ip}:api` | 480 / min | 120 |
| `power` | `device:{id}:power` | 10 / min | 3 |
| `unauth` | `addr:{ip}:unauth` | 30 / min | 10 |
| `pair` | `addr:{ip}:pair` | 5 / min | 2 |
| `upload` | `addr:{ip}:upload` | 480 / min | 120 |

The `pair` bucket is the important one and it is **not** what the code comment or
`PROTOCOL.md` §3 say. Both describe "5 per hour". The implementation calls
`TryConsume(key, perMinute: PairingPerHour, burst: 2)`, and the refill rate is
`perMinute / 60000` tokens per millisecond — so the steady-state allowance is 5 tokens per
*minute*, capped at a burst of 2. The `Retry-After` returned on `429` is computed from that
same rate, so it will be at most a second or two. In practice a phone retrying a failed
pairing will not be locked out for an hour.

A denied request is audited as `rate.limited`, and the response carries `Retry-After`.

---

## 8. Network boundary and the file sandbox

### LAN-only

`LocalNetworkMiddleware` runs **before** authentication, so an off-LAN packet never
reaches the device table. `LocalNetworkGuard.IsAllowed` returns true for:

* loopback, always (the per-user session agent talks to the agent over `127.0.0.1`, and a
  developer running the agent in a console needs it);
* IPv6 link-local and `fc00::/7`;
* any address inside a configured subnet.

The default configured subnets are `192.168.0.0/16`, `10.0.0.0/8` and `172.16.0.0/12`.
`PROTOCOL.md` also lists `169.254.0.0/16`; that range is **not** in the default set and
`IsAllowed` has no IPv4 special case for it, so a link-local IPv4 client is rejected.
IPv6-mapped IPv4 addresses are unmapped to IPv4 before the check, and `X-Forwarded-For` is
ignored — the agent supports no reverse proxy, and trusting the header would let any client
claim a permitted subnet.

Kestrel binds `ListenAnyIP`. "Local network only" is enforced in one reviewable place
rather than split between the socket and the router, which means a configuration mistake in
the socket layer cannot silently widen exposure — but it also means the port is reachable
from anywhere the firewall allows, and the only network-level control is the Windows
Firewall rule the installer optionally adds (scoped to the `private,domain` profiles).

### File sandbox

`src/ESI.RemotePc.Platform/Files/FileSandbox.cs` is the single gate between the network and
the filesystem. It is written to fail closed:

1. Roots are canonicalised at configuration time and compared with `OrdinalIgnoreCase`.
   A case-sensitive comparison would be a bypass on a case-insensitive volume.
2. Every candidate path is canonicalised, then the **final** path is re-validated. Opening
   a handle and asking the kernel for `GetFinalPathNameByHandle` walks the entire reparse
   chain, so a junction *component* pointing outside the root is caught — not only a
   final component that is a link.
3. A path that does not exist yet (an upload target, a file being created) has no handle to
   open, so the deepest existing ancestor is resolved and the missing tail re-attached.
   The tail is then guaranteed to be literal.
4. Rejected before normalisation, never normalised into safety: `\\?\`, `\\.\`, UNC paths
   (including `\\?\UNC\` arriving via the kernel), NTFS alternate data streams, the reserved
   DOS device names `CON`/`PRN`/`AUX`/`NUL`/`COM1`–`COM9`/`LPT1`–`LPT9` (with or without an
   extension), and paths over 32767 characters.
5. `readOnly` roots reject every mutating verb with `esi_forbidden_scope`.
6. A root, and any ancestor of a root, can never be deleted or renamed away.
7. With no roots configured, every path is denied.

Default roots are the logged-on user's `Desktop`, `Documents`, `Downloads`, `Music`,
`Pictures` and `Videos`, read-write. Note that **a relative path is rooted at
`Environment.SpecialFolder.UserProfile`, not at the sandbox default root** — and because
the service runs as `LocalSystem`, that is the system profile directory, so relative paths
from a phone will be rejected rather than resolved to a useful location. Send absolute
paths.

`FileService` is the only caller, and `Endpoints.Guard` maps every `SandboxViolationException`
to `403 esi_path_not_allowed` and every `UnauthorizedAccessException` to
`403 esi_forbidden_scope`. Sandbox denials and deletes are audited.

---

## 9. Secrets at rest

| Secret | Protection |
|---|---|
| Device `masterSecret` (in `agent.db`) | DPAPI, `DataProtectionScope.LocalMachine`, entropy `ESI-RemotePC/v1/device-secret` (`DeviceStore.Protect`) |
| TLS private key (`agent.pfx`) | DPAPI, machine scope as `LocalSystem` / user scope in console mode, entropy `ESI-RemotePC/v1/tls` |
| Session handshake token (`session.token`) | Written by `TokenBootstrap`-adjacent code; not covered by the DPAPI helpers above |

Machine scope is the right granularity for the database: the agent runs as `LocalSystem`
and the file lives under `%ProgramData%`, so a different Windows account cannot decrypt it
even if it can read the file. **A copy of `agent.db` taken from another machine cannot be
unwrapped** — `DeviceStore.Unprotect` catches the `CryptographicException`, logs, and
substitutes an empty secret, which makes that device fail with
"The stored secret for this device is unusable" until it is re-paired. This is the intended
fail-safe, and it also means the database is not portable as a backup.

The installer's `[Dirs]` entry sets `permissions: users-modify` on
`%ProgramData%\ESI-RemotePC`. The folder is writable by standard users, so the protection
rests entirely on DPAPI rather than on the file ACL.

**On the phone**, the master secret is held in `flutter_secure_storage` 11.x, which
defaults to AES-GCM data encryption with RSA-OAEP key wrapping in the Android keystore
(`android-app/esi_remote/lib/src/data/machine_store.dart`). The protection is exactly as
strong as the device's lock screen and as the keystore's hardware binding.

---

## 10. Audit log

`src/ESI.RemotePc.Agent.Core/Security/AuditLog.cs` maintains an append-only trail in the
`audit_log` table. Each row's `chain_hash` is:

```
chainHash[n] = SHA256( chainHash[n-1] || join(U+001F, ts, deviceId, remoteAddress, action, result, detail) )
```

where `chainHash[-1]` is 64 `0` characters. The result is lowercase hex. `U+001F` (ASCII
unit separator) is used because it cannot appear in any of the fields. `GET /api/v1/audit`
recomputes the whole chain and returns `chainValid` plus the id of the first row that fails.

Audited actions: `pair.requested`, `pair.approved`, `pair.denied`, `auth.failed`,
`network.rejected`, `rate.limited`, `power.action`, `device.revoked`, `device.rotated`,
`settings.changed`, `file.denied`, `file.deleted`, `app.launched`, `certificate.reset`,
`remote.started`, `files.upload`, `http.rejected`.

What this is and is not:

* **Tamper-evident, not tamper-proof.** An attacker with write access to `agent.db` can
  recompute the entire chain. The property is that silent history rewriting is detectable
  by anyone holding an earlier copy of the chain head, and that doing it requires
  deliberate extra work. It is not a signature, and it does not survive an attacker who
  controls the database from the first entry.
* **Retention purging breaks the chain.** `AuditLog.Purge` deletes rows older than the
  retention window and logs a warning that says so. `VerifyChain` will then report the
  first deleted row as a break. Export before purging if continuity matters.
* **The derived audit key is not used.** `EsiCrypto.DeriveAuditKey` exists and is mirrored
  on the Android side, and `PROTOCOL.md` §5.2 lists `auditKey` as one of the three derived
  keys. `AuditLog` never calls it: the chain is unkeyed, and it is per-agent rather than
  per-device. A device's audit key is derived and discarded.
* **Recording never fails the operation.** `AuditLog.Record` swallows every exception. A
  database that is locked or full produces a gap in the trail, not a failed request. That
  is the right trade for availability and the wrong one for assurance.
* Successful authentications are not audited. `AuditLog.ActionAuthSucceeded` is declared
  and never called, so the trail records failures and privileged actions only.

---

## 11. Limitations

This section is the point of the document.

### 11.1 Not hardened against a local administrator

The agent runs as `LocalSystem` with `SeTcbPrivilege`. Anyone who can obtain
administrative rights on the PC can read `agent.db` and `agent.pfx`, unprotect the device
secrets with the machine DPAPI key, mint signed requests, and read the file manager's
roots. DPAPI raises the cost of a casual file copy; it does not defend against a process
that can call `CryptUnprotectData` as `SYSTEM`. There is no integrity protection of the
agent's own binaries, no code signing enforcement, and no anti-tamper mechanism.

The same applies to the session agent: anything that can inject into the user's session as
that user can use the command pipe.

### 11.2 No multi-factor authentication

Pairing is a single factor: possession of the one-time provisioning secret, which in the
current build is the six-digit `userCode` displayed on the desktop. There is no PIN, no
biometric prompt, no per-action confirmation, and no second paired device required to
perform a destructive action. Every `control`-scoped device can shut the machine down, lock
it, sign the user out, terminate processes, launch elevated applications, and inject input
into the interactive desktop.

### 11.3 The TLS certificate is self-signed and pinned, not CA-issued

No public CA can issue for a private LAN address, so the trust anchor is out-of-band
delivery. That transfer is the weakest link:

* `GET /api/v1/handshake` returns the pin unauthenticated, and an attacker present before
  pairing can substitute both the certificate and the pin.
* The Windows Agent does not render a QR code, so the out-of-band channel is the human
  reading a six-digit code off a dialog, or manually typing host, port and pin. There is
  no machine-to-machine verification of that transfer, and the code is the only binding
  between the two ends.
* There is no revocation, no rotation schedule, and no short-lived certificate. The
  certificate lives for 825 days and is only replaced when it expires or when
  `POST /api/v1/settings/reset` deletes it.

### 11.4 A compromised paired device is fully trusted within its scopes

Compromise of the phone's master secret yields everything that device's scopes allow, with
no further prompts and no per-action confirmation. A `control` + `files` device is a
complete remote-control capability: full screen, full input injection, arbitrary file
read/write inside the roots, process termination, and power control.

Mitigations available today are revocation (`DELETE /api/v1/devices/{deviceId}`) and
rotation (`POST /api/v1/auth/rotate`), both requiring `admin` scope from *another* paired
device or the local CLI. There is no automatic revocation on suspicious behaviour, no
per-session scope reduction, and no read-only pairing option exposed at pairing time —
`esirc pair approve` grants `Read | Control | Files` unconditionally.

### 11.5 Physical access is out of scope

The design assumes the PC is in a place the attacker cannot reach. A local user at the
keyboard can approve a pairing, read the pairing code, and change the agent's settings.
BitLocker, a locked BIOS and Secure Boot are reported in `GET /api/v1/system/info` for
awareness but the agent does not enforce or depend on any of them.

### 11.6 The phone's key depends on the device's lock screen

`flutter_secure_storage` protects the master secret with the Android keystore, and the
keystore's keys are frequently hardware- or TEE-backed and gated on the lock screen. That
is a real protection against a casual attacker with physical access to an unlocked phone.
It is not protection against a rooted device, a bootloader unlock, a compromised ROM, or an
attacker who can unlock the phone. There is no server-side revocation that helps here: the
PC cannot tell a legitimate phone from a cloned one.

### 11.7 Other gaps worth stating

| Gap | Detail |
|---|---|
| No request signing bypass exists | `requireSignedRequests` is declared in the settings DTO and never read by any code path. Signing is unconditional. |
| LAN-only and TLS cannot be turned off through the API | `SettingsStore.UpdateAsync` warns about them and then overwrites the incoming values back to `true`. They can only be changed by editing `settings.json` or passing `--http true` at start. |
| Screen stream is a single stream | `WebSocketHub` serves one device at a time and refuses a second with `esi_conflict`. A paired device can watch another's session only by being that device. |
| Media transport keys need the session agent | `play`/`pause`/`stop`/`next`/`previous` are forwarded to the session agent and return `409` when it is not connected. Volume and mute work without it. |
| Gaming "suspend" is terminate | `GamingService.SetMode` closes the configured background apps. Windows has no documented process-freeze API. They are not auto-restarted, and the response says so. |
| Presented FPS is not measurable | `GamingService.MeasureFps` returns `null` and `GET /api/v1/gaming/fps` says so in its own `note` field. Any client that renders a number there is inventing it. |
| `Suspend` from the session agent is refused | `SessionAgentRuntime` answers the `suspend` command with "Suspend is handled by the Windows service, not the session agent." `POST /api/v1/power/sleep` still works through the service. |
| Audit `auth.succeeded` is never recorded | See §10. |
| Pairing bucket is 5/minute, not 5/hour | See §7. |
| Rotation grace does not preserve the old key | See §6. |
| No key recovery path | See §6. |
| Upload tickets are unauthenticated-by-signature | `PUT /api/v1/files/upload?ticket=…` carries no signature. The ticket is 16 hex characters (8 bytes), bound to `(device, path, size)`, single-use, 120 s. It is a bearer token over a channel the LAN guard has already vetted. |
| In-memory nonce cache | Cleared on restart. See §6. |

---

## 12. Discrepancies with `PROTOCOL.md`

Documented here rather than silently reconciled. The code is normative for what the agent
actually does.

| # | `PROTOCOL.md` says | The code does |
|---|---|---|
| 1 | §2.1 SAN `esiremotepc-<deviceIdShort>.local` | `esiremotepc-<port>.local` (`CertificateAuthority.Generate`) |
| 2 | §2.1 "re-generated when the address set changes" | Never reissued. A machine that changes subnet keeps a certificate whose `IP:` SANs no longer match; clients that pin rather than validate names are unaffected, clients that validate names are not |
| 3 | §3 `GET /` is a pre-auth endpoint | `/` is mapped only in console mode (`AgentHost.RunInteractiveAsync`) and 302s to `/pairing`. As a service it is whitelisted as public and then 404s |
| 4 | §3 `GET /` is a "minimal HTML pairing landing page" | The HTML lives at `/pairing`; `/` is a redirect. Not in the public list in §3 at all |
| 5 | §3 "5 pairing requests per hour per source address" | 5/minute sustained, burst 2. See §7 |
| 6 | §3.2 `POST /api/v1/auth/activate` | No such route |
| 7 | §5.2 `auditKey` is a third derived key | Derived nowhere; `AuditLog` chains unkeyed hashes |
| 8 | §5.3 rotation keeps the previous key valid for 120 s | The previous `keyId` passes header validation but the HMAC is computed from the *new* secret, so the old key fails. See §6 |
| 9 | §5.4 / §9.3 the QR carries the provisioning secret | No QR emitter exists in the agent. The dialog asks the user to type a 6-digit code. See §3.2 |
| 10 | §5.4 "the PC approval prompt" shows the provisioning secret | The prompt shows the device name, platform, address, versions and the `userCode` only |
| 11 | §6.6 `GET /files/roots` requires `read` | `files` |
| 12 | §6.6 "relative → rooted at the default root" | Rooted at `Environment.SpecialFolder.UserProfile`, which for the LocalSystem service is the system profile |
| 13 | §6.11 settings expose `localNetworkOnly`, `requireSignedRequests`, `tlsEnabled` as settings | All three are forced back to `true` by `SettingsStore.UpdateAsync` |
| 14 | §6.11 `sessionAgentRequiredForLock` | Declared in the DTO, never read |
| 15 | §6.11 `startWithWindows` | Declared in the DTO, never read; the installer registers both services with `start= auto` |
| 16 | §6.3 `effectiveMode` for sleep/hibernate | `PowerService` sets it; `SessionAgentRuntime` refuses the `suspend` command the service would use |
| 17 | §7.5 binary frame header `magic "ESIV" (0x56 53 49 45 little-endian)` | `ScreenFrameHeader.ExpectedMagic` is `0x56495345`, written with `BitConverter.TryWriteBytes`, so the bytes on the wire are `45 53 49 56` which read as ASCII `ESIV`. The specification's hex literal is in the wrong order; the byte sequence it describes is right. The rest of the 32-byte header matches the spec |
| 18 | §6.10 `POST /notifications/{id}/read` scope `read` | Correct in code. No change |
| 19 | — | **Fixed.** The C# side already said `esirc`; the Flutter app said `esi-cli`, which does not exist. Both app messages now name `esirc` and state the stop/start sequence, because `esirc pair approve` refuses while the service is running |
| 20 | §3 rate limiting | **Fixed.** `GET /api/v1/pair/session` shared the 30-per-minute unauthenticated budget while the agent's own `pair/request` response tells a phone to poll it every 1000 ms for 120 s. A correctly-behaving client was throttled a quarter of the way through pairing. The poll route now has its own budget, derived at startup from `ProtocolConstants.PairingPollIntervalMs` and `security.PairingWindowSeconds` so the two cannot drift apart again. Found by pointing the real app at a running agent; the unit tests could not see it |
| 21 | §5.4.1 approval | **Clarified, not changed.** `approval` was `tray-or-cli`; there is a tray icon but it has no pairing item, so the token now reads `browser-or-cli`. The browser route still cannot approve - the page is LAN-reachable and the code is six digits - so approval needs a signed-in user at the desktop prompt, or the console. `esirc pair url <code>` was added for the headless case, where the QR has no display to be shown on |

Items 9 and 10 are the ones that affect a user's ability to pair at all today.

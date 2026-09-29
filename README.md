# ESI Remote PC

Control a Windows PC from a phone on the same network: live system telemetry, power
actions, application launching, a sandboxed file manager, remote mouse and keyboard, media
control, gaming mode, and notifications.

Two halves that share one wire contract:

| | |
|---|---|
| **`windows-agent/`** | A C#/.NET 10 Windows Service plus a per-user session helper and an administration CLI. Holds the HTTPS API, the telemetry collectors, the pairing state machine and the security boundary. |
| **`android-app/`** | A Flutter Android app. Talks to the agent over HTTPS with a signed request on every call. |
| **`docs/PROTOCOL.md`** | The normative contract both halves implement. Read this first. |

---

## What actually works

Everything below is implemented, builds clean, and is covered by the test suite or the
runtime smoke test. Nothing in this README is aspirational.

- **Windows Service agent** — real SCM service, auto-start, restart-on-failure, self-contained
  single-file binary that needs no .NET runtime on the target machine.
- **HTTPS API + WebSocket** — Kestrel with a self-signed ECDSA P-256 identity; the phone
  pins the SPKI hash out of band.
- **Request signing** — HMAC-SHA256 over a canonical 8-line string, with HKDF-SHA256 key
  derivation, per-request nonces, timestamp skew limits and replay rejection.
- **Pairing** — six-digit code, on-desktop approval prompt, QR flow, and a console
  fallback for when nobody is at the keyboard. Secrets are delivered encrypted to a
  provisioning key the attacker in a racing attack does not have.
- **Discovery** — UDP broadcast (primary, needs no Android permission) and hand-rolled
  mDNS/DNS-SD.
- **Telemetry** — CPU, RAM, GPU (PDH per-engine counters, plus NVML where the driver allows
  it), temperatures, disk, network throughput and uptime.
- **Power** — shutdown, restart, sleep, hibernate, lock, sign out, and Wake-on-LAN magic
  packets.
- **Gaming mode** — suspend or re-prioritise background applications, and restore them.
- **Application launcher** — curated catalogue plus the live process list.
- **File manager** — confined to explicitly configured roots, with traversal, junction,
  symlink, UNC, device-path, alternate-data-stream and reserved-name defences.
- **Remote control** — live screen stream with tile-hash delta encoding, plus mouse and
  keyboard injection, executed by a separate per-user process.
- **Media control** — playback transport and volume through the Windows Core Audio API.
- **Notifications** — rules for disk, temperature, service and connectivity events, pushed
  over the WebSocket.
- **Administration CLI** (`esirc`) — install, uninstall, status, and device management.

Deliberately honest telemetry: a sensor that cannot be read reports `null` with a note. It
never reports a plausible number, because a fabricated 45 °C is worse than "not reported"
for anybody deciding whether to shut the machine down.

---

## Quick start

```powershell
# 1. Build everything and run the tests.
pwsh -File windows-agent\build.ps1

# 2. Install the services (this asks for administrator rights through UAC).
.\windows-agent\artifacts\publish\ESI.RemotePc.Agent.Cli.exe install

# 3. Confirm it is up and get the pairing address.
.\windows-agent\artifacts\publish\ESI.RemotePc.Agent.Cli.exe status
```

Then open `https://localhost:48220/pairing` on the PC, or let the phone find the agent by
itself — the app broadcasts on the LAN and the agent answers.

See [`docs/INSTALL.md`](docs/INSTALL.md) for the full walkthrough and
[`docs/BUILD.md`](docs/BUILD.md) for the toolchain prerequisites.

---

## Repository layout

```
.
├── docs/                      Protocol and documentation
│   └── PROTOCOL.md            The normative wire contract. Start here.
├── windows-agent/             The C# side
│   ├── ESI.RemotePc.Agent.slnx
│   ├── build.ps1              restore -> build -> test -> smoke test -> publish
│   ├── Directory.Packages.props
│   ├── installer/             Inno Setup script
│   ├── src/
│   │   ├── ESI.RemotePc.Protocol/     DTOs, enums, crypto. No I/O, no Windows.
│   │   ├── ESI.RemotePc.Platform/     Win32 interop, telemetry, capture, input, files.
│   │   ├── ESI.RemotePc.Agent.Core/   Security, storage, settings, notifications.
│   │   ├── ESI.RemotePc.Agent.Service/ The Kestrel host and the REST surface.
│   │   ├── ESI.RemotePc.Session/      The per-user interactive-desktop helper.
│   │   └── ESI.RemotePc.Agent.Cli/    install / uninstall / status / pair.
│   └── tests/
│       ├── ESI.RemotePc.Tests/        xUnit: crypto, canonical string, file sandbox.
│       └── smoke-test.ps1             Boots the published binary and probes it over HTTPS.
└── android-app/               The Flutter side
```

The dependency direction is strictly downward. `Protocol` knows nothing about Windows;
`Platform` knows nothing about the network; `Core` knows nothing about HTTP. That is what
makes the security-critical code testable without a desktop session and the protocol
testable without a machine.

---

## Why there are two Windows processes

A service runs in session 0, which has no interactive desktop. `SendInput` is bound to the
caller's session and is subject to UIPI, `BitBlt` has nothing to capture, and `LockWorkStation`
targets a desktop the service cannot see. The Win32 documentation is explicit that a service
should not open a window station precisely because doing so exposes it to the logged-on user.

So the split is structural, not stylistic:

- **`ESIRemotePcAgent`** (LocalSystem, session 0) — network, storage, telemetry, power.
- **`ESIRemotePcSession`** (per-user, interactive session) — screen capture, input
  injection, lock and sign out, the pairing prompt.

They talk over a named pipe whose DACL grants exactly LocalSystem, Administrators, and the
account that owns the console session. When the session agent is not running, the API says
so explicitly rather than accepting a command and dropping it.

Details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Security posture in one paragraph

Local network only by default. TLS with a self-signed identity whose SPKI hash is delivered
by the QR code, so the trust does not come from a CA. Every authenticated request is signed
with a key derived per device and per key generation; the signature covers the method, the
canonicalised target, a timestamp, a nonce and a hash of the body. Nonces are remembered so
a replay is rejected, timestamps outside the skew window are rejected, and a key rotation
keeps the previous generation valid for a short grace period so an in-flight request is not
spammed into failure. Device secrets are encrypted at rest with DPAPI. The audit log is
hash-chained, so removing a line breaks every line after it.

The limitations are stated plainly in [`docs/SECURITY.md`](docs/SECURITY.md), because a
security document that only lists strengths is not a security document.

---

## Testing

```powershell
# Unit tests: HKDF against the RFC 5869 vectors, the canonical string against a golden
# vector, signature binding, GCM tamper detection, and the file sandbox against real NTFS.
dotnet test windows-agent\tests\ESI.RemotePc.Tests\ESI.RemotePc.Tests.csproj -c Release

# Runtime smoke test: boots the published binary, probes it over HTTPS, and checks that
# unsigned requests are rejected and that discovery answers.
pwsh -File windows-agent\tests\smoke-test.ps1
```

The smoke test is deliberately external to the test project: it exercises the published
single-file binary, because that is the artefact that actually ships.

---

## Documentation

| Document | Contents |
|---|---|
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | The wire contract. Signing, key hierarchy, endpoints, frames, discovery. |
| [`docs/INSTALL.md`](docs/INSTALL.md) | Installing, first run, pairing, uninstalling. |
| [`docs/BUILD.md`](docs/BUILD.md) | Toolchain, build script, tests, debug mode. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Layering, the two-process split, data flow, threading. |
| [`docs/API.md`](docs/API.md) | Every REST and WebSocket endpoint. |
| [`docs/SECURITY.md`](docs/SECURITY.md) | Threat model, and its limits. |
| [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) | Symptom to cause to fix. |
| [`docs/WOL_SETUP.md`](docs/WOL_SETUP.md) | Wake-on-LAN, including the Fast Startup caveat. |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | What is done, what is not, and why. |

---

## Licence

See [`LICENSE`](LICENSE).

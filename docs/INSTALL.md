# ESI Remote PC — Installation and First Run

For the PC side. The mobile app is a separate Flutter project; its own README is at
[`../android-app/esi_remote/README.md`](../android-app/esi_remote/README.md).

---

## 1. Requirements

| | |
|---|---|
| OS | Windows 10 version 1809 or later, x64 |
| Disk | ~200 MB for the three self-contained executables |
| Network | A LAN. The agent is local-network-only and refuses other sources |
| Privileges | Administrator, for the installer and for `esirc install` |
| Nothing else | No .NET runtime, no Visual C++ redistributable, no database server |

The three executables are self-contained single files, so there is no dependency tree and
the install directory can be moved or deleted freely.

---

## 2. Installing

### With the installer

Run `ESI-RemotePC-Setup-1.0.0-x64.exe` from an elevated prompt, or double-click it and
accept the UAC prompt.

The wizard offers two optional tasks:

| Task | Default | What it does |
|---|---|---|
| Add a Windows Firewall rule for the agent's LAN port | **Checked** | Two inbound rules: TCP `48220` and UDP `48211`, on the **private and domain** profiles only |
| Create a desktop shortcut | Unchecked | A shortcut to `esirc status` |

The firewall task is checked by default because without it Windows blocks inbound
connections and the phone cannot find the PC. It is scoped to private and domain profiles
deliberately: on a public network — a coffee shop, an airport — a rule opening this port
would expose the agent to the internet, and the agent's own LAN guard cannot help with that
because the traffic would already have arrived.

The installer then registers both services, sets their failure-recovery actions and
`requiredPrivileges`, starts the agent, and opens a console showing `esirc status`.

### From the command line

If you already have `artifacts\publish\` populated, the CLI installs the same thing without
the wizard:

```powershell
cd "C:\Program Files\ESI Remote PC"
.\ESI.RemotePc.Agent.Cli.exe install
```

`install` needs administrator rights and asks for them through UAC by itself. It re-elevates
into a new process and returns immediately, so watch the new window for the result.

| Flag | Default | Effect |
|---|---|---|
| `--service-path <path>` | Next to `esirc` | Agent executable |
| `--session-path <path>` | Next to `esirc` | Session agent executable |
| `--port <n>` | `48220` | Port to print in the closing message |
| `--no-autostart` | off | Register the service but do not start it |
| `--no-session-agent` | off | Skip the per-user helper — no remote control, no pairing prompt |
| `--no-recovery` | off | Do not configure restart-on-failure |
| `--force` | off | Uninstall over an existing installation first, keeping the data |

`--port` only changes the text `install` prints. The port the agent actually binds comes
from `settings.json`; change it with `PUT /api/v1/settings` or by editing the file, and see
[`API.md`](API.md) §12.

### Checking it worked

```powershell
.\ESI.RemotePc.Agent.Cli.exe status
```

```
ESI Remote PC
  data directory  C:\ProgramData\ESI-RemotePC
  pairing page    https://localhost:48220/pairing

  ESIRemotePcAgent         Running    Automatic
  ESIRemotePcSession       Running    Automatic

  latest log      C:\ProgramData\ESI-RemotePC\logs\agent-20260929.log (2m ago)
```

`ESIRemotePcSession` shows `not installed` until somebody logs on — it is a per-user
service, so it has no entry to report before the first interactive logon. It is created at
install time but only starts at logon.

---

## 3. First run

The first start creates the data directory, the SQLite schema and the TLS identity, and
prints a banner into the log:

```
ESI Remote PC agent 1.0.0 ready
  device id   : 3f2a9c17d4b8e206
  https port  : 48220 (TLS on)
  discovery   : udp/48211 mDNS on
  tls subject : CN=META-PC
  spki pin    : X48dCZ6lN3Zg0tDg3m2m4Q8YF9n0pQ1rS2tU3vW4xY=
  file roots  : C:\Users\META\Desktop, C:\Users\META\Documents, …
  paired      : 0/8
```

It also writes `agent.pfx` (the DPAPI-wrapped self-signed ECDSA P-256 certificate) and
`agent.db`. If `agent.pfx` is deleted the pin changes and **every paired device must pair
again** — that is the intended fail-safe, not a bug to work around.

The agent's device id is minted once and stored in `agent_identity`. It does not change on
restart. Do not delete `agent.db` expecting the id to survive; it will not.

### Discovery

With the firewall rule in place the PC advertises itself two ways:

* **UDP broadcast** on `48211`. A client sends the ASCII string `ESI_DISCOVER_V1` to
  `255.255.255.255` and to each interface's subnet-directed broadcast; the agent replies with
  a single JSON datagram containing the same body as `GET /api/v1/handshake`, to the source
  address. This is the primary mechanism and needs no extra Android permission.
* **mDNS / DNS-SD** on `_esiremotepc._tcp.local.`, with PTR, SRV, TXT (including
  `id=<deviceId>` and `pin=<spkiSha256>`), A and AAAA records.

Neither is identity. Every field learned from discovery is a hint; the authoritative
exchange is the cryptographic one at pairing.

### The pairing page

```
https://localhost:48220/pairing
```

A self-contained HTML card showing the device name, the agent device id, the **SPKI pin**,
and the API base path. It is the easiest way to read the pin onto a second device by hand,
and it is reachable from the tray menu (*Open pairing page*) or from the Start menu
(*Pair a device*).

Note the path is `/pairing`, not `/`. `GET /` is a redirect to `/pairing` **only when the
agent runs in console mode**; as a service it is whitelisted as public and then answered
`404`. Use `/pairing`.

---

## 4. Pairing a phone

### The flow

```
Phone                                       PC
  |-- POST /api/v1/pair/request --------------->|   (unauthenticated)
  |                                            creates a PairingAttempt with a
  |                                            6-digit userCode and a 32-byte
  |                                            provisioning secret
  |<-- 201 {pairingId, userCode, approval} -----|
  |
  |   [ desktop dialog: "New device wants to
  |     connect" — device, platform, source
  |     address, versions, and the code, with
  |     a countdown ]
  |
  |-- GET /api/v1/pair/session?id=… (1/s) ----->|
  |<-- 200 {"status":"pending"} -----------------|
  |
  |   [ the human reads the code and types it
  |     into the phone, then clicks
  |     "Allow this device" ]
  |
  |-- GET /api/v1/pair/session?id=…&secret=… -->|
  |<-- 200 {"status":"approved", deviceId,
  |         keyId, wrappedSecrets} --------------|
  |
  |   [ the phone unwraps the envelope with the
  |     provisioning secret and starts signing ]
```

### On the desktop

The dialog is shown by the session agent, so **somebody has to be logged in**. If nobody is,
`approval` comes back as `browser-or-cli` and nothing is displayed — see §5.

The dialog is built to make denial the path of least resistance:

* **Deny is the cancel/default button.** Escape denies.
* Closing the window with the X, Alt+F4, or a session change is a denial. `OnFormClosing`
  forces `PairingDecision.Denied` on any undecided exit.
* Letting the countdown run out is `expired`.
* Approval requires a deliberate click on a button labelled "Allow this device", next to
  the device name, platform, source address and app/OS versions.

An unattended approval is strictly worse than an unattended refusal, so there is no
timeout that defaults to yes and no way to approve from the phone.

### The provisioning secret, and what is missing

`PairingManager.Poll` will not release the credentials envelope unless the poll carries a
valid 32-byte `provisioningSecret` as the `secret` query parameter. Without it the response
is `{"status":"error"}`, forever, and the phone can never pair.

**The Windows Agent renders the pairing QR in the desktop prompt**, and serves the same code
at `GET /pairing/qr.svg?code=<code>`. The tray menu has three items — *Open pairing page*,
*Remote control status*, *Exit* — and deliberately no QR item: the code is a credential, and
a menu that displayed one would put it in front of anyone who could reach the notification
area. The pairing page renders the QR when given `?code=`, and shows the device name, id and
pin.

`PROTOCOL.md` §5.4 and §9.3 describe a QR carrying the provisioning secret, and the mobile
app's parser (`lib/src/protocol/pairing_qr.dart`) accepts a QR, a URI, a bare query string
or a JSON object — its own comment says "no code in the agent emits it, so there is no
canonical encoding to match". The phone is prepared for a QR that the PC now does produce.

**This gap is closed.** `QrEncoder` renders the code, the desktop prompt shows it inline,
`GET /pairing/qr.svg?code=` serves it as SVG, and `esirc pair url <code>` prints the same URL
as text for a machine with no display. The round trip is verified by decoding the emitted
matrix back to the URL it was built from. See [`PROTOCOL.md`](PROTOCOL.md) §9.4.

### Pairing without a session agent

If the PC is at the login screen, or the session agent is not running, no dialog appears.
Approve from a console on the machine instead:

```powershell
sc stop ESIRemotePcAgent
.\ESI.RemotePc.Agent.Cli.exe pair pending
.\ESI.RemotePc.Agent.Cli.exe pair url 418207      # only if there is no display for the QR
.\ESI.RemotePc.Agent.Cli.exe pair approve 418207
sc start ESIRemotePcAgent
```

The service has to be stopped because `approve` mints device credentials and rewrites rows
the running agent also holds. The phone will show a connection error for the few seconds the
agent is down and then recover on its own; the attempt is in the database and the approval is
waiting for it.

`pair url` is only needed when the QR has nowhere to be displayed. It prints the polling URL,
which carries the provisioning secret, so treat the output as a credential — it is valid
until the attempt is approved or expires. There is deliberately no browser equivalent: the
pairing page is reachable by anything on the LAN and the code is six digits, so a page that
could show the secret *and* approve would let an attacker drive their own pairing to
completion. See [`PROTOCOL.md`](PROTOCOL.md) §9.4.

`pair approve` grants `read`, `control` and `files`. It does **not** grant `admin`, so a
CLI-approved device cannot rotate keys, read the audit log, change settings or manage other
devices. Getting a second `admin` device requires a device that already has it, via
`POST /api/v1/devices/{deviceId}/rotate` and
`POST /api/v1/devices` (there is no way to grant `admin` through pairing or the CLI at all).

The service must be stopped first. `esirc pair` refuses otherwise, because approving an
attempt the service is holding in memory would leave the two disagreeing about whether the
pairing succeeded.

---

## 5. `esirc`

One binary, five commands. The executable on disk is `ESI.RemotePc.Agent.Cli.exe` — this
document calls it `esirc` for brevity, and so do the agent's own help text and log
messages. The installer adds a Start-menu shortcut named **ESI Remote PC (status and
pairing)** that runs `status`. Nothing puts the install directory on `PATH`, so invoke the
executable by full path unless you add it yourself.

```powershell
& "C:\Program Files\ESI Remote PC\ESI.RemotePc.Agent.Cli.exe"
```

```
ESI Remote PC - administration

USAGE
  esirc <command> [options]

COMMANDS
  install      Install the Windows services
  uninstall    Remove the Windows services (--purge also deletes the data)
  status       Show what is installed and whether it is running
  pair         List, approve, deny and revoke devices
  version      Print the version
```

| Command | Needs admin | Notes |
|---|---|---|
| `status` | No | Data directory, pairing URL, both services' state and start type, newest log file and its age |
| `version` | No | `esirc 1.0.0 (protocol v1)` |
| `install` | Yes | UAC-relaxes itself |
| `uninstall` | Yes | `--purge` prompts before deleting data |
| `pair …` | No | **Refuses while the service is running** |

Exit codes: `0` success, `1` failure, `2` usage error.

### `esirc pair`

| Subcommand | Effect |
|---|---|
| `show` (or `list`) | Table of device id, name, scopes, last seen, state. `state` is `ok`, `disabled`, or `rotating` |
| `pending` | Pending requests with their codes and time remaining |
| `approve <code>` | Approve. Accepts `418207` or `418 207`; non-digits are stripped |
| `deny <code>` | Deny |
| `revoke <deviceId>` | Remove a device immediately |

`pair show` with no devices prints where to go next:

```
No devices are paired.
Pair one from the phone: the app discovers this PC automatically, or scan the QR
at https://localhost:48220/pairing
```

That message points at a QR that the agent does not render. The URL is correct.

`pair revoke` takes effect at the device's next request — there is no push to disconnect a
live WebSocket, though `PairingApprovalRequested` and the streaming gate mean an active
stream is the one case where the revocation is not immediate. Revoking twice is harmless.

`pair show` deliberately reports a device as `rotating` while its previous key generation
is still inside the 120 s grace window, so a phone holding the old key is not mistaken for
an attacker.

---

## 6. Uninstalling

### From Windows Settings

*Settings → Apps → ESI Remote PC → Uninstall.* This runs the installer's `[UninstallRun]`
and `[UninstallDelete]` sections, which:

1. Stop and delete `ESIRemotePcSession`.
2. Stop and delete `ESIRemotePcAgent`.
3. Delete `{commonappdata}\ESI-RemotePC` in full — certificate, key, device database,
   pairing history, notifications, audit log and logs.

It also removes both firewall rules.

Deleting the data directory is deliberate. Leaving a usable TLS identity and a list of
paired devices behind on a machine that no longer has the software is exactly the kind of
residue that gets missed during an audit. **There is no "keep my data" option in the
installer**; reinstalling means pairing again.

### From the command line

```powershell
.\ESI.RemotePc.Agent.Cli.exe uninstall
```

Keeps the data directory and says so. To delete it too:

```powershell
.\ESI.RemotePc.Agent.Cli.exe uninstall --purge
```

`--purge` prompts `This deletes the pairing database, TLS certificate and logs. Continue?`
and only when already elevated. Answer anything other than `y`/`yes` to cancel.

If a service will not stop within 30 s — a pending reboot leaves one in `StopPending`
indefinitely — the CLI warns and deletes the registration anyway. The entry is then removed
at the next restart, and the executable can still be locked until then.

---

## 7. When the service will not start

Work down this list. `esirc status` and the log are the two tools.

```powershell
esirc status
Get-Content "C:\ProgramData\ESI-RemotePC\logs\agent-$(Get-Date -Format yyyyMMdd).log" -Tail 60
```

| Symptom | Cause | Fix |
|---|---|---|
| `not installed` from `esirc status` | The service was never registered, or was deleted | `esirc install` |
| `Stopped` and `Disabled` | Start type was changed | `sc config ESIRemotePcAgent start= auto` |
| `StartPending` that never resolves | Port `48220` is already bound | `Get-NetTCPConnection -LocalPort 48220 -State Listen` to find the owner; stop it or change `port` in `settings.json` |
| Exits immediately, log shows `No TLS certificate is available` | Certificate generation failed | See below |
| Exits immediately, no log at all | The data directory is not writable by `LocalSystem` | Check the ACL on `C:\ProgramData\ESI-RemotePC`; the installer creates it `users-modify` |
| `The agent terminated unexpectedly` in the log | An unhandled exception during start | The full stack trace is the next line |
| Starts but the phone cannot see it | Firewall | `Get-NetFirewallRule -DisplayName "ESI Remote PC*"`; re-run the installer or add the rule by hand |

### Certificate generation failed

The log contains:

```
Could not generate a TLS certificate. The agent will start with HTTP only, which
means request signing is the only protection in place.
```

Causes, in the order they are likely:

1. The machine key store is unavailable. `CertificateAuthority` detects `S-1-5-18`; without
   it the key goes in the user key store and DPAPI uses `CurrentUser` scope. If the service
   is not running as LocalSystem — check `serviceAccount` and `isElevated` in
   `GET /api/v1/health` — the machine key store is denied.
2. `C:\ProgramData\ESI-RemotePC` is not writable.
3. `agent.pfx` exists but is corrupt, or was copied from another machine and cannot be
   DPAPI-unwrapped. `Load()` catches that, logs, and generates a replacement — which changes
   the pin and invalidates every paired device.

Kestrel **requires** a certificate whose private key is persisted; an `EphemeralKeySet`
certificate is rejected outright. There is no in-memory fallback.

### Recovery actions

Both services are configured with `restart/5000`, `restart/15000` and `restart/60000`,
reset after 86400 s. A crash is therefore retried three times before the service stays down.
To see the actions:

```powershell
sc.exe failure ESIRemotePcAgent
```

To re-arm them after editing:

```powershell
sc.exe failure ESIRemotePcAgent reset= 86400 actions= restart/5000/restart/15000/restart/60000
```

### Checking it is actually serving

```powershell
curl.exe -k https://localhost:48220/api/v1/health
```

```json
{"status":"ok","version":"1.0.0","uptimeSeconds":42,"serverTime":1756000000,
 "serviceAccount":"NT AUTHORITY\\SYSTEM","isElevated":true,"sessionId":0,"debugMode":false}
```

`sessionId: 0` confirms session 0. If `isElevated` is `false` or `serviceAccount` is not
`NT AUTHORITY\SYSTEM`, the service is not running as intended and
`CreateProcessAsUserW` into the console session will fail — which is the usual cause of the
session agent never connecting.

---

## 8. When remote control is unavailable

Remote control, the lock and sign-out actions, the media transport keys, the desktop toasts
and the pairing prompt **all** require the per-user session agent. Everything else —
telemetry, metrics, power status, shutdown and restart, file manager, app launcher, gaming
mode, notifications, settings, logs, audit — works without it.

### Symptoms

| What you see | Where it appears |
|---|---|
| `Remote input needs the per-user session agent, which is not running.` | `POST /api/v1/remote/input`, `409` |
| `Media keys must be sent from the interactive desktop. Log in to the PC, or start 'ESI Remote PC Session'.` | `POST /api/v1/media/play` and the other transport keys, `409` |
| `Start a screen stream with remote.start over the WebSocket, not a plain request.` | `POST /api/v1/remote/input` with `kind: sessionStart` |
| `Another device is already streaming this screen.` | `remote.start` over the WebSocket, `esi_conflict` |
| `The session agent is not connected, so the screen cannot be captured.` | `remote.state` with `active: false` |
| `"remote": false` in the handshake | `features` map |
| `sessionAgentState: "NoUserSession"` | `GET /api/v1/remote/session` |
| `Suspend is handled by the Windows service, not the session agent.` | Session agent log, if something sends the `suspend` command |

### Why it is not connected

`SessionAgentBridge` polls `WTSGetActiveConsoleSessionId` every 3 s. The state it reports
maps to:

| State | Meaning |
|---|---|
| `NotRunning` | The channel pump stopped. Nothing is connected |
| `Starting` | A session exists and the bridge is trying |
| `Ready` | Connected and heartbeating |
| `NoUserSession` | Nobody is logged in, or the agent did not connect within 15 s |
| `Error` | The supervisor could not reach the session agent |

### Fixes, in order

1. **Is anybody logged in?** `NoUserSession` is the correct answer at a login screen. Remote
   control needs an interactive desktop; there is nothing to inject into otherwise.

2. **Is the per-user service running?** It starts at logon, so check after a reboot:

   ```powershell
   sc.exe query ESIRemotePcSession
   ```

   If it does not exist, re-run `esirc install` without `--no-session-agent`. If it exists
   and is `Stopped`, start it:

   ```powershell
   sc.exe start ESIRemotePcSession
   ```

3. **Is the executable where the bridge expects it?** The service-side
   `SessionAgentPath` must resolve. Pass it explicitly when running the agent by hand:

   ```powershell
   .\ESI.RemotePc.Agent.Service.exe --session-agent "C:\Program Files\ESI Remote PC\ESI.RemotePc.Session.exe"
   ```

   With an empty path the bridge logs `Session agent binary not found at {Path}; relying on
   the logon task` and does nothing else.

4. **Is `SeTcbPrivilege` present?** `WTSQueryUserToken` needs it, and it is only usable if
   the `requiredPrivileges` registry value is set:

   ```powershell
   reg query "HKLM\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent" /v requiredPrivileges
   ```

   Expected: `SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege`. A missing
   value is the single most common cause of the bridge never connecting — the privilege is
   present in the account's token but **disabled**, so the first `CreateProcessAsUserW`
   fails. The service log says:

   ```
   SeTcbPrivilege could not be enabled: <error>
   ```

   or

   ```
   WTSQueryUserToken was denied. Confirm the agent runs as LocalSystem with SeTcbPrivilege.
   ```

5. **Can the session agent reach the pipe?** Its DACL grants the console session's own
   account `ReadWrite | Synchronize` and nobody else. If the SID cannot be resolved the
   bridge logs:

   ```
   Could not resolve the account for session <id>; the session agent will not be able to
   connect to the command pipe. This is expected in a container or a session with no
   cached profile.
   ```

   The session agent's own log repeats it as `The command channel is not available yet` and
   retries every 3 s.

> **The tray menu does not have a "Show pairing QR" item, and never did.** The menu has
> *Open pairing page*, *Remote control status* and *Exit*. Earlier drafts of the pairing
> instructions told the user to look for a QR item; they were wrong and the text has been
> corrected everywhere. The QR is on the desktop prompt and at `/pairing/qr.svg?code=`, and
> `esirc pair url <code>` prints the same URL for a machine with no display at all. See §4
> and `PROTOCOL.md` §9.4.

6. **Read the session agent's log.** It is per user and is **not** reachable through
   `GET /api/v1/logs`:

   ```
   %LOCALAPPDATA%\ESI-RemotePC\logs\session-YYYYMMDD.log
   ```

7. **Is the tray icon there?** A visible "ESI Remote PC" tray icon means the session agent
   process is alive. Double-click it for a status box showing the service channel, the
   Windows session id, the signed-in user, the workstation lock state and whether capture is
   running. A tooltip of `ESI Remote PC - waiting for the agent service` means the process
   is up but the pipe is not connected — go to step 5.

### What still works without it

Shutdown, restart, force restart, sleep and hibernate are handled by the service and do not
need the session agent. `lock` and `signout` do, and return
`accepted: false, requiresInteractiveSession: true` rather than pretending to succeed.

Note also that `GET /api/v1/power/status` reports `isLocked: false` whenever the session
agent is not connected. That is a *known unknown*, not a report that the desktop is
unlocked.

### Installing without the session agent

`esirc install --no-session-agent` produces an agent that is fully functional for telemetry,
power and administration, with no remote control, no lock, no sign-out, no media transport
keys, no desktop notifications and no pairing prompt. Pairing then requires
`esirc pair approve`, which needs the service stopped.

---

## 9. Related

| | |
|---|---|
| Wire protocol | [`PROTOCOL.md`](PROTOCOL.md) |
| Building | [`BUILD.md`](BUILD.md) |
| Troubleshooting | [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) |
| Security model | [`SECURITY.md`](SECURITY.md) |
| Architecture | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| HTTP surface | [`API.md`](API.md) |
| Wake-on-LAN | [`WOL_SETUP.md`](WOL_SETUP.md) |

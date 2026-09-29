# ESI Remote PC 1.0.0

Two files. That is the whole delivery.

| File | What it is |
|---|---|
| `ESI-RemotePC-Setup-1.0.0-x64.exe` | The Windows installer. One self-contained executable; run it, and it installs everything and starts the agent. |
| `ESI-RemotePC-1.0.0.apk` | The Android app. Sideload it, or install from the APK. |

SHA-256 (first 32 hex characters):

```
ESI-RemotePC-Setup-1.0.0-x64.exe  B269612F954A889072E9C5DEED28DC3A
ESI-RemotePC-1.0.0.apk             D7B34D8AE49A120F21BFFD9B40588F43
```

---

## Windows

1. Run `ESI-RemotePC-Setup-1.0.0-x64.exe`. It asks for administrator rights through UAC.
2. Accept the firewall rule, or add it yourself.
3. Open `https://localhost:48220/pairing` on the PC.
4. Pair a phone, then point the app at the phone.

**What the installer does.** Writes three self-contained executables to
`%ProgramFiles%\ESI Remote PC`, registers the `ESIRemotePcAgent` service as LocalSystem with
automatic start and restart-on-failure, sets the `requiredPrivileges` registry value the
service host needs to start the per-user helper, registers a logon-triggered scheduled task
for that helper, and adds a firewall rule scoped to the private and domain profiles only.

**Why three executables inside one installer.** They are three processes, not three
deliverables. A Windows service runs in session 0 with no interactive desktop, so it cannot
inject input, capture the screen, or lock the workstation — those require a process inside
the signed-in user's session. The third executable is the command-line tool
(`esirc install`, `esirc status`, `esirc pair approve`). The packaging is one file; the
architecture is not one file, and it cannot be.

**To uninstall.** Start menu → *Uninstall ESI Remote PC*. That also deletes the pairing
database, the TLS identity and the logs; there is no "keep my data" option, and leaving a
usable identity behind on a machine that no longer has the software is the kind of thing that
gets missed in an audit.

---

## Android

1. Copy `ESI-RemotePC-1.0.0.apk` to the phone and open it, or install with `adb install`.
2. The app broadcasts on the LAN and finds the PC by itself. If it does not, enter the
   address by hand.
3. Scan the pairing QR shown on the PC.

This is a **debug** build, unsigned for release. Android will ask you to allow installation
from an unknown source. It is a test build, not a distributable one.

**Why the QR matters.** Pairing starts with an unauthenticated request the phone makes to
the PC. If the PC sent the resulting secret back over the network, anyone on the LAN could
trigger a prompt, wait for a user to approve it in good faith, and collect the credentials.
The QR is how that secret moves — from the screen to the camera, which is the only channel
an attacker is not on.

---

## Known limitations, stated plainly

- **The app has never been run against a live agent.** It was built and tested in isolation:
  129 Dart tests, including golden vectors that are duplicated in the C# test suite so the two
  implementations cannot drift apart silently. The first real pairing is still the first real
  test.
- The remote-control screen stream keeps running in the foreground and reconnects on its own,
  but the background service that holds it alive across an app switch is wired up and not yet
  started from the remote screen.
- Three Gradle workarounds were needed because this build machine's network returns 404 for
  every `dl.google.com` path. All three are commented in place; a normal network is unaffected.
- The debug APK is 183 MB because it carries every CPU architecture. A release build with
  splits would be a fraction of that.

See `../docs/` for the full documentation, and `../docs/SECURITY.md` for the threat model
and its limits.

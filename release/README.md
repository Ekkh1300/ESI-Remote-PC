# ESI Remote PC 1.0.0

Two files. That is the whole delivery.

| File | Size | What it is |
|---|---|---|
| `ESI-RemotePC-Setup-1.0.0-x64.exe` | 43.8 MB | The Windows installer. One self-contained executable; run it and it installs everything and starts the agent. |
| `ESI-RemotePC-1.0.0.apk` | 69.7 MB | The Android app. |

SHA-256, first 32 hex characters:

```
ESI-RemotePC-Setup-1.0.0-x64.exe  962D712F6B97A261BC37D99DA3F97B95
ESI-RemotePC-1.0.0.apk             02253FA591E2EC1037B83FADCDC824E9
```

Both were cut down from what they were. The installer went from 129.7 MB and the APK from
183.3 MB; the reasons are in `docs/BUILD.md` and below, because "it is smaller" is only
interesting if you know what was given up.

---

## Windows

1. Run `ESI-RemotePC-Setup-1.0.0-x64.exe`. It asks for administrator rights through UAC.
2. Accept the firewall rule, or add it yourself.
3. Open `https://localhost:48220/pairing` on the PC.
4. Pair a phone, then point the app at the phone.

**What the installer does.** Lays a self-contained .NET application into
`%ProgramFiles%\ESI Remote PC`, registers the `ESIRemotePcAgent` service as LocalSystem with
automatic start and restart-on-failure, sets the `requiredPrivileges` registry value the
service host needs to start the per-user helper, registers a logon-triggered scheduled task
for that helper, and adds a firewall rule scoped to the private and domain profiles only.
It then queries the SCM and Task Scheduler and logs anything that did not land, so a partial
install cannot look like a working one.

**Why the install directory is a folder and not three tidy binaries.** Three processes have
to run, and one of them cannot be a service. A Windows service runs in session 0 with no
interactive desktop, so it cannot inject input, capture the screen, or lock the workstation —
those need a process inside the signed-in user's session. The third entry point is the
command-line tool (`esirc install`, `esirc status`, `esirc pair approve`).

The three were originally shipped as self-contained *single-file* bundles, which is the wrong
shape for shipping several of them in one installer: each bundle embeds its own compressed
copy of the .NET runtime, so the installer cannot compress it further and all three between
them carry the runtime three times. Publishing them as one loose layout puts the runtime in
the directory once, where the installer compresses it. 129.7 MB became 43.8 MB. The trade is
that the three entry-point executables are now 160 KB launchers that need the DLLs beside
them, so the directory has to be moved and deleted as a unit.

**To uninstall.** Start menu → *Uninstall ESI Remote PC*. That also deletes the pairing
database, the TLS identity and the logs; there is no "keep my data" option, and leaving a
usable identity behind on a machine that no longer has the software is the kind of thing that
gets missed in an audit.

---

## Android

1. Copy `ESI-RemotePC-1.0.0.apk` to the phone and open it, or install with `adb install`.
2. The app broadcasts on the LAN and finds the PC by itself. If it does not, enter the
   address by hand.
3. Approve on the PC, then scan the QR.

The app is a **release** build. It is signed with the debug key, so Android will ask you to
allow installation from an unknown source. Per-ABI builds are 22–28 MB if you would rather
have the one that matches your phone.

**Why the QR matters.** Pairing starts with an unauthenticated request the phone makes to
the PC. If the PC sent the resulting secret back over the network, anyone on the LAN could
trigger a prompt, wait for a user to approve it in good faith, and collect the credentials.
The QR is how that secret moves — from the screen to the camera, which is the only channel an
attacker is not on. There is deliberately no way to approve from a browser: the pairing page
is reachable by anything on the LAN and the code is six digits, so a page that could approve
would let an attacker finish a pairing they started themselves.

---

## What was verified, and what was not

Worth reading before you trust this on a real machine.

**Verified by running it.** The Windows build is green end to end: 162 unit and integration
tests, 19 live probes against a booted agent, and a real install → serve → uninstall cycle
that was repeated from the delivered installer file. The Android side is 138 Dart tests, and
— the part that matters — a live check that starts a throwaway agent and drives the app's
own networking, TLS pinning, signing and AES-GCM code against it. Eleven assertions, all
passing:

| | |
|---|---|
| A pinned TLS handshake completes, **and a wrong pin is refused** | the second half is the one that matters |
| The handshake's advertised pin equals the fingerprint of the certificate actually served | PROTOCOL.md §A2.1 and the agent agree |
| `esirc pair approve` works, from the real CLI | not a stub |
| An approved attempt releases a wrapped envelope | |
| **Dart opens an envelope C# produced** | AES-GCM, HKDF and the key generation, across a real key |
| **A request signed by Dart is accepted by the agent** | and a replayed nonce is refused with `esi_replay` |

**Not verified by me.** I could not run this installer start to finish — the UAC prompt was
declined in the environment it was built in. It has been run by a person, and what that
surfaced is now fixed. Three real defects, all of which made "installed but nothing happens":

- **`stop` / `delete` / `create` with no waits between them.** `sc stop` is asynchronous, and
  `sc delete` on a service that is still running only *marks* the key for deletion. So
  `create` failed with 1073, silently, because nothing was checking it; `start` then failed
  with 87, because a delete-pending service cannot be started. The outcome was an install
  that reported success, a service visible in `services.msc`, and an agent that never ran.
  The sequence now waits for the SCM at each step and checks every exit code.
- **The logon task never fired.** An `ONLOGON` trigger fires when somebody *signs in*, and
  installing while already signed in — the normal case — means it has already been and gone.
  So there was no tray icon and no approval prompt, and pairing could not be completed
  either. The installer now starts the task as well as registering it.
- **The post-install check only asked whether the service *existed*.** It now also connects
  to the agent's port, because a service can be registered and permanently stopped, and that
  is indistinguishable from a working install if all you check is registration.

**The other half of "the program does not open" is by design, and is now said out loud.**
This product has no window. The agent is a Windows service; the tray icon and the pairing
approval prompt exist only while somebody is signed in to the PC. The installer now ends with
a message saying so and giving the pairing page address, because installing a service and
then looking for a window to click is a reasonable thing to expect and this product cannot
meet it.

**The app has never been on a phone.** It has been driven against a real agent from the
harness, and the emulator install and launch were checked, but no human has held it.

**Known gaps**, all recorded rather than papered over: the remote-control foreground service
is wired but not started from the remote screen; three Gradle workarounds were needed
because this build machine's network returns 404 for every `dl.google.com` path (all three
are commented in place and are no-ops on a normal network). `docs/API.md` §15 and
`docs/SECURITY.md` §12 list the rest.

See `../docs/` for the full documentation, and `../docs/SECURITY.md` for the threat model
and its limits.

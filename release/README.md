# ESI Remote PC 1.0.0

Two files. That is the whole delivery.

| File | Size | What it is |
|---|---|---|
| `ESI-RemotePC-Setup-1.0.0-x64.exe` | 43.8 MB | The Windows installer. One self-contained executable; run it and it installs everything and starts the agent. |
| `ESI-RemotePC-1.0.0.apk` | 51.1 MB | The Android app. |

SHA-256, first 32 hex characters:

```
ESI-RemotePC-Setup-1.0.0-x64.exe  5EBF393B4F9EC909E92BBA318365943C
ESI-RemotePC-1.0.0.apk             DC252F8FE4D74E502EAACA82E158D80D
```

Both were cut down from what they were. The installer went from 129.7 MB and the APK from
183.3 MB; the reasons are in `docs/BUILD.md` and below, because "it is smaller" is only
interesting if you know what was given up.

---

## Windows

1. Run `ESI-RemotePC-Setup-1.0.0-x64.exe`. It asks for administrator rights through UAC.
2. Accept the firewall rule, or add it yourself.
3. Open `https://localhost:48220/pairing` on the PC.
4. Pair a phone, then point the app at the PC.

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
   address by hand. The certificate pin comes from the PC's pairing page; it is never filled
   in from discovery, because a value from the wire is not evidence.
3. Press **Pair**. The phone asks the PC for a code and shows six digits.
4. Approve it **on the PC**, in the prompt that appears there.
5. The prompt shows a QR code. Scan it on the phone, or paste its contents. That QR carries
   the one-time secret, and the phone cannot collect the credentials without it.

Step 5 is not an extra step. The secret cannot exist before step 3, because the PC mints it
while handling the request that step 3 makes — which is the whole reason the QR exists.

**Signed, and how to install it.** The APK is a Flutter release build — AOT compiled,
tree-shaken, minified — signed with a 4096-bit RSA key held for this delivery, certificate
`CN=ESI Remote PC, OU=Release, O=ESI Remote PC, C=US`, SHA-256
`de2e20eb3795c5d25baaf191d8c4f44a886c7268f399e9f16592acb75b156f68`. **Earlier builds of this
app were signed with the Android debug key and are not installable over this one** — see the
troubleshooting below, because the error message does not say that.

Because it is not from the Play Store, Android asks you to allow installation from that source.
With a file manager: open the APK, and if it refuses, go to **Settings → Apps → Special app
access → Install unknown apps → pick your file manager or browser → Allow**. Then open the APK
again. Over ADB it needs no such permission:

```
adb install ESI-RemotePC-1.0.0.apk
```

It carries `arm64-v8a` and `x86_64`, and needs Android 7.0 (API 24) or newer.

**Why 51 MB and not 69.9.** A universal APK ships Flutter's engine and the compiled Dart
twice or three times — once per CPU architecture — and that was almost the entire package:
**66.5 MB of the 69.9 MB was `lib/`.** The 32-bit ARM ABI was dropped, taking 18.8 MB with
it. `x86_64` was kept deliberately even though it only serves emulators and Chromebooks,
because without it the app cannot be installed on an emulator at all, and an artefact nobody
can install is not one whose behaviour anyone can check.

Removing it took a second step that is worth knowing about if you ever touch this again.
`ndk.abiFilters` does not reach prebuilt `.so` files inside third-party AARs, so ML Kit's copy
of the barcode model survived the filter while Flutter's own libraries for that ABI did not.
`aapt` then advertised `armeabi-v7a` support from the leftover file — so a 32-bit phone would
have installed the package and died on launch with an `UnsatisfiedLinkError`. The exclusion
is a packaging rule over the directory, which is what makes the advertised list true. The
build is invoked with
`flutter build apk --release --target-platform android-arm64,android-x64`.

**If it refuses to install**, it is one of exactly two things:

| What you see | What it means | What to do |
|---|---|---|
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE`, or *App not installed* when an older copy is already on the phone | An earlier build signed with the **debug** key is installed. Android refuses to replace an app with one signed by a different key, and the message never mentions signatures. | Uninstall the old one first, then install this. You will have to pair again: the master secret lives in app-private storage that an uninstall removes |
| *For your security, your phone is not allowed to install unknown apps from this source* | The per-source install permission has not been granted | **Settings → Apps → Special app access → Install unknown apps**, as above |

Both were reproduced here: installing the release-signed APK over the debug-signed one fails
with `Existing package signatures do not match`, and the same APK installs and launches on a
clean device.

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

**Verified by running it.** The Windows build is green end to end: 171 unit and integration
tests, 19 live probes against a booted agent, and a real install → serve → uninstall cycle
that was repeated from the delivered installer file. The Android side is 152 Dart tests, and
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

**The installer is now tested by running it.** `installer/install-cycle-test.ps1` drives the
real thing through five states — clean, install, **install again over its own output**,
uninstall, install again — and asserts 26 things, all passing. It asks the question a user
would ask ("is it working?") by connecting to the agent's port, not by reading the SCM's
opinion, because a service can be registered and permanently stopped and that is
indistinguishable from a working install if all you check is registration.

Running it found five defects that every other check in the repository was blind to, and
every one of them produced "installed, and nothing happens":

- **`stop` / `delete` / `create` with no waits between them.** `sc stop` is asynchronous, and
  `sc delete` on a service that is still running only *marks* the key for deletion. So
  `create` failed with 1073, silently, because nothing was checking it; `start` then failed
  with 87, because a delete-pending service cannot be started. The outcome was an install
  that reported success, a service visible in `services.msc`, and an agent that never ran.
  The sequence now waits for the SCM at each step and checks every exit code.
- **The logon task could never fire.** Two separate faults. An `ONLOGON` trigger fires when
  somebody *signs in*, and installing while already signed in — the normal case — means it
  has already been and gone, so the installer now starts the task too. And
  `schtasks /create /tr "C:\Program Files\…"` cannot be made to work at all: Task Scheduler
  parses the action out of the raw command line instead of taking an argv element, so it
  stored the quoted path perfectly and then reported 0x80070002, "file not found", every time
  it tried to run it. The task is now registered from an XML document, where the command is
  element content and a space is just a character.
- **Reinstalling over a working install failed with exit code 5**, because the running agent
  and the running helper hold the very files being overwritten.
- **Uninstalling left the entire 139 MB application directory behind**, for the same reason:
  the uninstaller's own `sc stop` lives in `[UninstallRun]`, which runs *after* the files are
  removed. Both are now stopped before anything is written or deleted.
- **The post-install check only asked whether the service *existed*.** It now also connects to
  the agent's port.

Three more, all found once the cycle was pointed at the right binary:

- **The cycle could not be run from the entry point that ships it.**
  `install-cycle-test.ps1` joined even an absolute `-Setup` to its own directory, so
  `build.ps1` handed it `E:\…\installer\E:\…\setup.exe` and every install step threw —
  while the steps that only assert the machine is clean reported ok. It now refuses to start
  without an installer rather than failing three steps in with green above it.
- **It was testing a day-old installer.** The build checked only that the setup file
  *existed*, so an install cycle cheerfully passed 26 checks against a binary compiled the
  day before, installed from code that was not in the tree. The step now compiles the
  installer itself and fails if it cannot.
- **The test suite could go red for no reason at all.** Port allocation seeded from the
  Windows dynamic range, where Hyper-V and WinNAT reserve whole blocks; binding one of those
  fails with "access forbidden" rather than "in use", so 22 integration tests died saying
  nothing about the code under test. Every candidate is now probed on `IPAddress.Any`, the
  address Kestrel actually binds.

**A defect in the product itself, found by reading its own log.** The agent's supervisor
orphaned an open named-pipe server instance on each reconnect: the pump stopped without
closing the pipe it had been reading, and the next attempt created a replacement over the
top of it. The pipe name allows one server instance, so from the second reconnect onward
`CreatePipeServer` failed with `All pipe instances are busy` and the session helper could
never attach again — meaning remote input and screen capture would quietly stop working the
first time somebody signed out and back in. The agent's log already contained that exact
exception, which is the only reason it was findable. Five forced restarts of the helper on
the old build produced 168 lines of the error and not one successful reconnect; on the
build in this archive the same five restarts produce five `Session agent connected` lines
and no error.

**The other half of "the program does not open" is by design, and is now said out loud.**
This product has no window. The agent is a Windows service; the tray icon and the pairing
approval prompt exist only while somebody is signed in to the PC. The installer now ends with
a message saying so and giving the pairing page address, because installing a service and
then looking for a window to click is a reasonable thing to expect and this product cannot
meet it.

**The app has now been on a device, and it was broken in five separate ways.** Every one of
these was invisible to 138 passing Dart tests, because not one of them drove a navigation,
opened a connection, or read the stored state back.

The APK was installed on an Android 16 emulator and pointed at a real agent on this machine.
Discovery found the PC on its own (`10.0.2.2:48220`, subnet sweep and UDP broadcast; mDNS
correctly reported no answer rather than pretending otherwise). Pairing then failed, and kept
failing, for five unrelated reasons — any one of which would have been enough on its own:

1. **Pairing could not be started.** The app navigated with `pushNamed('/pairing')`, and
   go_router resolves a name through `RouteMatchList.namedLocation`, which walks the match
   list and dereferences each route's `name`. No route declared one, so every attempt threw
   `Null check operator used on a null value` before the request was ever sent. The exception
   escaped ahead of the code that clears the button's busy flag, so the Pair button stayed
   greyed out afterwards and the app looked merely busy rather than broken.
2. **The pairing secret was demanded before it could exist.** The form required a pairing
   secret, but the PC mints that secret while handling the pairing request, and only ever
   hands it out on a QR shown *afterwards* — on the approval prompt, or at
   `/pairing?code=…`. The two requirements deadlocked, so pairing was impossible from the
   phone by every route. Worse, pasting a secret from an earlier attempt made the PC consume
   the new one before its unwrap failed, because §5.4 claims delivery before it tries. An
   attempt now starts with no secret; the waiting screen offers Scan and Paste for the PC's QR
   while the attempt is live; the poll omits `secret=` until one has been adopted; and a poll
   that returns `error` *because the secret is still missing* is treated as the cue to scan
   rather than as a failure.
3. **The client was closed before it was used.** `PairingService` closed its HTTP client in a
   `finally` wrapped around the pairing *request*, then used that same closed client for every
   poll of the attempt. Each poll failed with `Client is already closed`, so the credentials
   could never be collected however correct the secret was — and the only symptom was a retry
   loop that reads like the PC being flaky.
4. **A successful pairing did not survive being saved.** `MachineStore.loadAll` rebuilt each
   machine from the index, and the index is only a pointer list (`deviceId`, `host`, `port`);
   the master secret lives in the record that pointer names. Every machine came back with no
   secret, so the app reported *"Pairing did not finish"* on every screen, forever, with the
   correct secret sitting on disk next to it. There were no round-trip tests for the store at
   all; there are eight now.
5. **A paired phone landed on a blank screen.** `ConnectionNotifier.build` called
   `_attachSocket` and `_refresh` inline, and both read and assign `state` — which Riverpod
   does not initialise until `build` has returned — then ended with `return state`. Opening any
   paired PC threw `ProviderException: Tried to use a provider that is in error state` and the
   shell rendered as blank grey. The widget tests built the shell with *nothing* paired, which
   is exactly the state that does not trigger it.

On the rebuilt APK the same script, unchanged, completes: discovery → six-digit code →
approval on the PC → secret read from the QR → credentials decrypted → machine stored →
dashboard **Live**, streaming signed telemetry over the WebSocket (CPU, memory, GPU,
290 processes, 5067 threads, 12 logical processors). The device id the phone reports,
`181d76c2797b6128`, is the one the agent has on file.

**The CLI install path was broken too, and nothing caught it.** `esirc install` could not
create the service at all: it passed `start= 2`, the numeric `ServiceStartMode`, where `sc.exe`
wants a keyword and answers `ERROR: Invalid start= field`. The shipped installer's own `[Code]`
had it right, which is why the `.exe` worked and only the CLI was broken. Verified after the
fix: `esirc install` reports *created* and *started*, and the service reaches `Running`.

**Still open, and it matters for a machine you are not standing at.** After this machine
rebooted, the installed service never came back, and `sc start` answered 87 with no
delete-pending key — on a registration the installer had itself produced and that had run for
two and a half hours beforehand. The cause was not established. What was *ruled out*, by
building probe services rather than by argument: an unquoted `ImagePath` over a path with
spaces (a probe with the identical key starts and runs), the `WOW64 = 0x14c` 32-bit flag the
installer's 32-bit `sc.exe` does set on this x64 binary, `requiredPrivileges`, and
`FailureActions`. Running the service executable by hand works perfectly, so the binary is not
at fault, and re-running the installer restored the service. Recorded as D27.

**A fourth, found the same way - from the log, not from a test. The channel between the
service and the session helper had only ever worked in one direction.** `SessionChannel`
set `pipe.WriteTimeout` before every write, and .NET refuses that on a handle opened
asynchronously: the assignment throws `Timeouts are not supported on this stream`, the catch
around it swallowed the exception, marked the channel dead and logged it at *Debug* - below
the level this process logs at. So no heartbeat, no `ready` and no reply ever left the helper.
The service saw silence, decided the helper had died 45 seconds later, tore the pipe down, and
the helper reconnected 3 seconds after that. The log contained **1675 "Session agent
connected" against zero "Session agent ready"**, and 1650 heartbeat timeouts, continuously
since 30 September - while the phone saw a PC that answered HTTPS perfectly and did nothing
else, because screen frames, remote input, lock state and any approval clicked on the desktop
all travel down that pipe.

The timeout is a cancellation around the async write now, and the failure is logged at
*Warning*, because a whole channel going dark is not a debug detail. A second, smaller fault
sat behind it: the approval prompt was posted with `BeginInvoke` to a window handle that did
not exist yet, so a prompt delivered during start-up threw and never appeared. The anchor's
handle is forced before the channel is started.

Verified on the installed build: one `connected`, one `ready`, and zero timeouts across
three minutes of monitoring - where the previous build produced a timeout every 45 seconds.

**A fifth, found by launching Notepad from the phone: every command that carries a body was
failing, and only GETs ever looked right.** `EsiApi._perform` wrote `Content-Type` inside an
`else if` on the *unsigned* branch - so it was attached to pairing (which is unsigned, §3) and
to nothing else. Every signed `POST` or `PUT` went out with a body and no media type, and
ASP.NET cannot bind a body it cannot identify: it answers **415** from the body binder, before
the endpoint's code runs at all.

The blast radius was every write in the product: launching and terminating apps, **all six
power actions**, Wake-on-LAN relay, file create/move/rename, uploads, gaming mode, the media
keys, notification rules, saving settings and rotating the key. All of them returned 415. They
were invisible because the reads - handshake, system info, metrics, power status, the app list,
the live WebSocket stream - are all `GET`, and every one of them worked perfectly. The phone
showed a healthy PC that could not be told to do anything.

It surfaced as `POST /api/v1/apps/launch -> 415` in the agent's log the moment the Apps tab
was actually used. The header now goes out whenever a body does, signed or not; it is not part
of what is signed, so the signatures are unchanged, and one of the new tests asserts exactly
that. Six tests were added to `signed_request_test.dart`, the file that already exists for
"wiring that no formula test can see". Run against the previous code, three of them fail
(`POST`, `PUT` and the raw `application/octet-stream` upload); with the fix, all 152 pass.

**Re-verified end to end on the delivered APK**, against the installed service:

| | |
|---|---|
| Fresh pairing, app data cleared: discovery → six-digit code → `pair approve` → `POST /pair/request → 201` → credentials → **dashboard Live** | device `7793a787a914d2f9`, key 1 |
| `POST /apps/launch → 200` | Notepad went from 0 to 2 processes on the PC |
| `POST /apps/notepad/terminate → 200` | back to 0, after the app's own confirmation dialog |
| `POST /power/wake → 200` | a power endpoint with a signed body - the case that was 415 |
| `GET /power/status → 200`, machine `online`, `E-K\META PC`, 290 processes, 4069 threads | live telemetry, not cached |
| 415s after the fix | **zero** (the single one in the log is the pre-fix run that found it) |

**Known gaps**, all recorded rather than papered over: the remote-control foreground service
is wired but not started from the remote screen; three Gradle workarounds were needed
because this build machine's network returns 404 for every `dl.google.com` path (all three
are commented in place and are no-ops on a normal network). `docs/API.md` §15 and
`docs/SECURITY.md` §12 list the rest.

See `../docs/` for the full documentation, and `../docs/SECURITY.md` for the threat model
and its limits.

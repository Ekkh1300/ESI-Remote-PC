# ESI Remote PC 1.0.0

Two files. That is the whole delivery.

| File | Size | What it is |
|---|---|---|
| `ESI-RemotePC-Setup-1.0.0-x64.exe` | 43.8 MB | The Windows installer. One self-contained executable; run it and it installs everything and starts the agent. |
| `ESI-RemotePC-1.0.0.apk` | 51.1 MB | The Android app. |

SHA-256, first 32 hex characters:

```
ESI-RemotePC-Setup-1.0.0-x64.exe  1E4DBC5734BEC16D36F09E753EB9C324E0A68D554E58A84B4BC4A66994D99BE1
ESI-RemotePC-1.0.0.apk             044547BC54D2A9BAF6B5498B76DD21D8
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
interactive desktop, so it cannot inject input, capture the screen, or lock the workstation â€”
those need a process inside the signed-in user's session. The third entry point is the
command-line tool (`esirc install`, `esirc status`, `esirc pair approve`).

The three were originally shipped as self-contained *single-file* bundles, which is the wrong
shape for shipping several of them in one installer: each bundle embeds its own compressed
copy of the .NET runtime, so the installer cannot compress it further and all three between
them carry the runtime three times. Publishing them as one loose layout puts the runtime in
the directory once, where the installer compresses it. 129.7 MB became 43.8 MB. The trade is
that the three entry-point executables are now 160 KB launchers that need the DLLs beside
them, so the directory has to be moved and deleted as a unit.

**To uninstall.** Start menu â†’ *Uninstall ESI Remote PC*. That also deletes the pairing
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
while handling the request that step 3 makes â€” which is the whole reason the QR exists.

**Signed, and how to install it.** The APK is a Flutter release build â€” AOT compiled,
tree-shaken, minified â€” signed with a 4096-bit RSA key held for this delivery, certificate
`CN=ESI Remote PC, OU=Release, O=ESI Remote PC, C=US`, SHA-256
`de2e20eb3795c5d25baaf191d8c4f44a886c7268f399e9f16592acb75b156f68`. **Earlier builds of this
app were signed with the Android debug key and are not installable over this one** â€” see the
troubleshooting below, because the error message does not say that.

Because it is not from the Play Store, Android asks you to allow installation from that source.
With a file manager: open the APK, and if it refuses, go to **Settings â†’ Apps â†’ Special app
access â†’ Install unknown apps â†’ pick your file manager or browser â†’ Allow**. Then open the APK
again. Over ADB it needs no such permission:

```
adb install ESI-RemotePC-1.0.0.apk
```

It carries `arm64-v8a` and `x86_64`, and needs Android 7.0 (API 24) or newer.

**Why 51 MB and not 69.9.** A universal APK ships Flutter's engine and the compiled Dart
twice or three times â€” once per CPU architecture â€” and that was almost the entire package:
**66.5 MB of the 69.9 MB was `lib/`.** The 32-bit ARM ABI was dropped, taking 18.8 MB with
it. `x86_64` was kept deliberately even though it only serves emulators and Chromebooks,
because without it the app cannot be installed on an emulator at all, and an artefact nobody
can install is not one whose behaviour anyone can check.

Removing it took a second step that is worth knowing about if you ever touch this again.
`ndk.abiFilters` does not reach prebuilt `.so` files inside third-party AARs, so ML Kit's copy
of the barcode model survived the filter while Flutter's own libraries for that ABI did not.
`aapt` then advertised `armeabi-v7a` support from the leftover file â€” so a 32-bit phone would
have installed the package and died on launch with an `UnsatisfiedLinkError`. The exclusion
is a packaging rule over the directory, which is what makes the advertised list true. The
build is invoked with
`flutter build apk --release --target-platform android-arm64,android-x64`.

**If it refuses to install**, it is one of exactly two things:

| What you see | What it means | What to do |
|---|---|---|
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE`, or *App not installed* when an older copy is already on the phone | An earlier build signed with the **debug** key is installed. Android refuses to replace an app with one signed by a different key, and the message never mentions signatures. | Uninstall the old one first, then install this. You will have to pair again: the master secret lives in app-private storage that an uninstall removes |
| *For your security, your phone is not allowed to install unknown apps from this source* | The per-source install permission has not been granted | **Settings â†’ Apps â†’ Special app access â†’ Install unknown apps**, as above |

Both were reproduced here: installing the release-signed APK over the debug-signed one fails
with `Existing package signatures do not match`, and the same APK installs and launches on a
clean device.

**Why the QR matters.** Pairing starts with an unauthenticated request the phone makes to
the PC. If the PC sent the resulting secret back over the network, anyone on the LAN could
trigger a prompt, wait for a user to approve it in good faith, and collect the credentials.
The QR is how that secret moves â€” from the screen to the camera, which is the only channel an
attacker is not on. There is deliberately no way to approve from a browser: the pairing page
is reachable by anything on the LAN and the code is six digits, so a page that could approve
would let an attacker finish a pairing they started themselves.

---

## What was verified, and what was not

Worth reading before you trust this on a real machine.

**Verified by running it.** The Windows build is green end to end: 179 unit and integration
tests, 19 live probes against a booted agent, and a real install â†’ serve â†’ uninstall cycle
that was repeated from the delivered installer file. The Android side is 163 Dart tests, and
â€” the part that matters â€” a live check that starts a throwaway agent and drives the app's
own networking, TLS pinning, signing and AES-GCM code against it. Eleven assertions, all
passing:

| | |
|---|---|
| A pinned TLS handshake completes, **and a wrong pin is refused** | the second half is the one that matters |
| The handshake's advertised pin equals the fingerprint of the certificate actually served | PROTOCOL.md Â§A2.1 and the agent agree |
| `esirc pair approve` works, from the real CLI | not a stub |
| An approved attempt releases a wrapped envelope | |
| **Dart opens an envelope C# produced** | AES-GCM, HKDF and the key generation, across a real key |
| **A request signed by Dart is accepted by the agent** | and a replayed nonce is refused with `esi_replay` |

**The installer is now tested by running it.** `installer/install-cycle-test.ps1` drives the
real thing through five states â€” clean, install, **install again over its own output**,
uninstall, install again â€” and asserts 29 things, all passing. It asks the question a user
would ask ("is it working?") by connecting to the agent's port, not by reading the SCM's
opinion, because a service can be registered and permanently stopped and that is
indistinguishable from a working install if all you check is registration.

Three of those 29 are the `requiredPrivileges` check, and they are worth reading twice. The
old one was `$value -like '*SeTcbPrivilege*'`, which is a boolean for a string and an *array*
for a `string[]` â€” so it passed while the value was the broken REG_SZ that stopped the agent
starting for months, and then **threw** the moment the value was corrected. A test that could
tell neither version from the other. It now reads the value through the registry API, asserts
that the kind is `REG_MULTI_SZ`, and asserts each privilege is its own element.

Running it found five defects that every other check in the repository was blind to, and
every one of them produced "installed, and nothing happens":

- **`stop` / `delete` / `create` with no waits between them.** `sc stop` is asynchronous, and
  `sc delete` on a service that is still running only *marks* the key for deletion. So
  `create` failed with 1073, silently, because nothing was checking it; `start` then failed
  with 87, because a delete-pending service cannot be started. The outcome was an install
  that reported success, a service visible in `services.msc`, and an agent that never ran.
  The sequence now waits for the SCM at each step and checks every exit code.
- **The logon task could never fire.** Two separate faults. An `ONLOGON` trigger fires when
  somebody *signs in*, and installing while already signed in â€” the normal case â€” means it
  has already been and gone, so the installer now starts the task too. And
  `schtasks /create /tr "C:\Program Files\â€¦"` cannot be made to work at all: Task Scheduler
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
  `build.ps1` handed it `E:\â€¦\installer\E:\â€¦\setup.exe` and every install step threw â€”
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
never attach again â€” meaning remote input and screen capture would quietly stop working the
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
failing, for five unrelated reasons â€” any one of which would have been enough on its own:

1. **Pairing could not be started.** The app navigated with `pushNamed('/pairing')`, and
   go_router resolves a name through `RouteMatchList.namedLocation`, which walks the match
   list and dereferences each route's `name`. No route declared one, so every attempt threw
   `Null check operator used on a null value` before the request was ever sent. The exception
   escaped ahead of the code that clears the button's busy flag, so the Pair button stayed
   greyed out afterwards and the app looked merely busy rather than broken.
2. **The pairing secret was demanded before it could exist.** The form required a pairing
   secret, but the PC mints that secret while handling the pairing request, and only ever
   hands it out on a QR shown *afterwards* â€” on the approval prompt, or at
   `/pairing?code=â€¦`. The two requirements deadlocked, so pairing was impossible from the
   phone by every route. Worse, pasting a secret from an earlier attempt made the PC consume
   the new one before its unwrap failed, because Â§5.4 claims delivery before it tries. An
   attempt now starts with no secret; the waiting screen offers Scan and Paste for the PC's QR
   while the attempt is live; the poll omits `secret=` until one has been adopted; and a poll
   that returns `error` *because the secret is still missing* is treated as the cue to scan
   rather than as a failure.
3. **The client was closed before it was used.** `PairingService` closed its HTTP client in a
   `finally` wrapped around the pairing *request*, then used that same closed client for every
   poll of the attempt. Each poll failed with `Client is already closed`, so the credentials
   could never be collected however correct the secret was â€” and the only symptom was a retry
   loop that reads like the PC being flaky.
4. **A successful pairing did not survive being saved.** `MachineStore.loadAll` rebuilt each
   machine from the index, and the index is only a pointer list (`deviceId`, `host`, `port`);
   the master secret lives in the record that pointer names. Every machine came back with no
   secret, so the app reported *"Pairing did not finish"* on every screen, forever, with the
   correct secret sitting on disk next to it. There were no round-trip tests for the store at
   all; there are eight now.
5. **A paired phone landed on a blank screen.** `ConnectionNotifier.build` called
   `_attachSocket` and `_refresh` inline, and both read and assign `state` â€” which Riverpod
   does not initialise until `build` has returned â€” then ended with `return state`. Opening any
   paired PC threw `ProviderException: Tried to use a provider that is in error state` and the
   shell rendered as blank grey. The widget tests built the shell with *nothing* paired, which
   is exactly the state that does not trigger it.

On the rebuilt APK the same script, unchanged, completes: discovery â†’ six-digit code â†’
approval on the PC â†’ secret read from the QR â†’ credentials decrypted â†’ machine stored â†’
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
delete-pending key â€” on a registration the installer had itself produced and that had run for
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
`else if` on the *unsigned* branch - so it was attached to pairing (which is unsigned, Â§3) and
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
(`POST`, `PUT` and the raw `application/octet-stream` upload); with the fix, all 163 pass.

**A sixth, found by cancelling a pairing instead of completing one: the form never came back
to life.** `_start` awaits the future `context.push('/pairing')` returns, and that future's
`finally` is the only thing that clears the `_busy` which greys out `Check address` and
`Pair`. The pairing screen left its route with `context.go('/onboarding')` â€” from *Start
again* and from the empty state's *Back* â€” and `go` replaces the whole stack instead of
popping it. go_router completes a pushed route's future only from `_completeRouteMatch`,
which runs on a pop, so the `go` abandoned the await: `finally` never ran, and because
`/onboarding` is the base route its `State` is *reused* rather than recreated, so the form
came back still `_busy` with both buttons dead until the app was restarted. Both exits pop
now; `go` survives only for arriving at the pairing screen without having been pushed, where
there is nothing to pop. The new widget test drives the real path â€” fill the form, *Pair*,
let the attempt fail, *Start again* â€” and asserts both buttons are live again; run against
the previous code it fails with `Expected: not null / Actual: <null>` for `Pair`. On the
device, Cancel â†’ Stop â†’ Back hands the form back with both buttons enabled and the address
and 44-character pin untouched.

**A seventh, found on a real phone against a real PC: the pairing window was ten seconds
long.** The phone created an attempt, the PC showed its prompt with the code, and the phone
then failed with *"The pairing window closed before it was approved"* â€” four times, always
after 8.2 seconds, always nine polls. Nothing in the log looked wrong: `POST /pair/request â†’
201`, then a poll a second, then silence, then a fresh attempt thirty seconds later.

The window is the agent's `pairingWindowSeconds` counted from the moment it created the
attempt, and the attempt is stamped with `expiresAt` in the **agent's** clock. The phone read
that stamp against **its own** clock. The PC that prompted this had a Windows Time service
that had never started, and NTP (UDP 123) is blocked on its network, so its clock ran 110 s
behind real time. That put the end of the stamp only ~10 s into the phone's future, and
`.clamp(10, 900)` handed the whole 120 second window to its 10 second floor: the phone gave
up while the agent was still holding the attempt open, waiting for somebody to click
Approve. The Android emulator paired happily throughout, because an emulator's clock is
slaved to the host's and so was wrong by the same 110 s â€” which is why nothing in the test
suite had ever caught it.

Both halves are fixed. The PC's clock is now correct, and `w32tm` cannot put it back the way
it found it, because NTP is blocked on that network. And the phone no longer subtracts one
machine's clock from another's: it reads `serverTime` from the agent's handshake and maps
`expiresAt` through that offset onto its own clock, which turns the window into a *duration*
instead of a difference between two clocks, so no offset in either can shorten it. The
countdown on the pairing screen reads from the same number, so it stops lying too. Five tests
assert the property for lags of 0 to 600 s in both directions, and record what the old rule
computed on that PC: `clamp(10, 900)` â†’ `10`.

**An eighth, and the reason the seventh was so hard to find: the Approve button on the PC did
nothing at all.** The seventh produced a symptom â€” *"The pairing window closed before it was
approved"* â€” that pointed squarely at the clock, and the clock *was* wrong, and fixing it
changed nothing. This is what was underneath. The phone asked, the PC showed its prompt with
the code, somebody clicked **Allow this device**, the dialog closed â€” and the attempt stayed
pending until it expired, so the phone was told the window had closed. Four attempts, four
`POST /pair/request â†’ 201`, four runs of a one-second poll, and not one line in the log between
them: an approval that was never recorded because it was never applied.

Two separate breaks, either of which alone is fatal. **One:** `ShowPairingPromptAsync`
completed its task with `form.Decision` read *before* `form.ShowDialog()`, and
`PairingPromptForm.Decision` starts life as `TimedOut` â€” so the outcome was `expired` before the
user had been offered a choice, whatever they then pressed. **Two:** the service learned the
outcome through `SessionAgentBridge.PairingApprovalRequested`, an event that had a declaration
and exactly one `?.Invoke` and **no subscriber anywhere in the solution**, so a correct report
would have been discarded even if one had been produced. The event was also shaped like a fresh
invitation rather than an answer, which is how a routing mistake looked like a naming one.

It survived because no test ever used the path. Every end-to-end run in this project approved
with `esirc pair approve`, which needs the service stopped and so cannot share a window with a
live attempt; and the integration fixture drives `PairingManager` directly, under a comment
asserting that was "the same object the prompt's answer is routed to". That was the assumption
nothing had checked. It is now a test.

The decision is read after the dialog closes, arrives as a `PairingDecisionReport` carrying a
`PairingOutcome`, and is applied by `AgentHostedService` â€” approve, deny, or expire, each with
its audit entry â€” with the reach a pairing grants defined once on `PairingManager` so the
desktop and the CLI cannot drift apart. Three integration tests feed in the exact
`pairing.result` message the session agent sends and assert the attempt's state and the sealed
credentials that follow; with the subscription removed, two of the three fail. On the installed
agent: `pending` â†’ click **Allow this device** â†’ `approved`, `deviceId ef9ad8efc29100d3`,
`keyId 1`, envelope present, and `Approved Desktop e2e from the desktop prompt` in the log.

**A ninth, and the one that decided whether the product was finished: the agent never
started after a reboot.** The service was registered, `Start` was `Automatic`, and every boot
produced SCM 7000 *"The parameter is incorrect"* â€” while the same executable, launched by hand,
ran perfectly and bound 48220. That mismatch is the whole reason it was hard: the agent's own
log had no line after the boot timestamp, so the process never reached its first one and there
was nothing to read. Everything visible said the registration was fine.

It was not. `requiredPrivileges` was written as **REG_SZ**, holding all three privileges in one
space-separated string. The SCM reads that value as a REG_MULTI_SZ and looks each element up as
a privilege name, so it searched for a privilege actually called *"SeTcbPrivilege
SeShutdownPrivilege SeAssignPrimaryTokenPrivilege"*, found nothing, and refused to launch the
image at all â€” `ERROR_INVALID_PARAMETER`, which `sc start` reports as 87. Written as a real
multi-string, the identical registration starts on the first attempt.

Two things had to change. The value is written as REG_MULTI_SZ â€” and via `reg.exe`, because
`RegWriteMultiStringValue` takes `RootKey` as an `HKEY` and its payload as a plain string, so it
cannot express three separate privileges at all; the terminating `\0` on the last element
matters too, since `reg.exe` rejects the `\0\0` the documentation shows. And the installer's
verification no longer asks only whether the service is *registered*: it now also asks whether
it is *running*, because "the key exists" was the one check that had been passing this by for
months. On the reinstall: `requiredPrivileges` reads back as `MultiString` with three elements,
and the setup log says *"Verified: the agent service is running and the logon task is
registered."*

Two earlier theories were wrong and are now ruled out with evidence rather than argument. The
unquoted `ImagePath` over a path with spaces is real â€” `sc create` consumes the outer quotes â€”
and it changes nothing: the SCM started the service with the path exactly as the installer
leaves it. And the 32-bit `sc.exe` stamping `WOW64` is gone from the key, and the service still
failed.

**A tenth, and the one that made the product unpairable by a human: the phone could not
obtain the certificate pin at all.** Every other defect on this list broke something. This one
removed the only way in. The app disables its `Pair` button until the pin field is non-empty â€”
correctly, because the pin is what makes the connection worth trusting â€” and its own help text
says to read it "from the PC's pairing screen, or scan its QR code". The pin *was* on the
pairing page, as text. Scanning it was impossible: every QR the agent emitted was built from a
pairing **attempt**, whose URL carries the address and the one-time secret but never the pin,
and an attempt only exists after a phone has already asked to pair. So the app's `Scan` button
could fill the address and never the pin, the `Pair` button stayed disabled, and the only route
left was reading 44 characters of base64 off a monitor and typing them on a soft keyboard â€” where
`l`, `I`, `1`, `0` and `O` are the same glyph to a human and autocorrect is one keystroke away.
The way in required the thing you could only get by being already inside.

The idle pairing page now shows a second QR, carrying `esiremotepc://pair?host=â€¦&port=â€¦&
deviceId=â€¦&pin=â€¦` â€” no attempt, therefore no secret, just what the phone needs before it is
able to ask. One scan fills address, port and pin, and the rest of the flow already worked. It
discloses nothing an attacker on the LAN did not already have: the pin is in the
unauthenticated `GET /handshake` by design, which is exactly why the app will not take it from
there. What the QR adds is the out-of-band hop â€” nothing on the network path can alter the
pixels on that screen, so the phone ends up comparing itself against what the user is actually
looking at. And because the page is usually opened *on the PC*, where the host is `localhost`
and the QR would be useless, it now says so and names the addresses that would work instead.

The chain is pinned at every link by a literal both sides share: `PairingManager.BuildConnectUrl`
emits that exact string, `QrEncoderTests` round-trips it through an independent decoder to prove
it survives encoding, and `pairing_qr_test.dart` feeds the same string to `PairingQr.parse` and
reads address, port, device id and pin back. Writing the test found a smaller bug in the same
area: `PairingQr`'s own doc comment promised a second accepted shape,
`esiremotepc://pair/192.168.1.42?â€¦`, which put the address in the **path** while the parser
read `uri.host` â€” so it silently did not work.

**Re-verified end to end on the delivered APK**, against the installed service:

| | |
|---|---|
| Fresh pairing from a cleared app: discovery â†’ six-digit code â†’ QR secret pasted â†’ `esirc pair approve` â†’ `POST /pair/request â†’ 201` â†’ credentials â†’ **dashboard Live** | device `fe6343821655eac1`, key 1 |
| **The same run with the phone's clock set 109 s ahead of the PC's** â€” the failure above, reproduced on purpose | countdown `85s` â†’ `67s`, a full window; **Paired** as `bca92023c70adb41`, dashboard Live at 264 processes |
| Cold start after a force-stop: the stored PC comes back as `10.0.2.2:48220 â€” paired â€” key 1`, `Open` â†’ `GET /handshake 200` â†’ `whoami 200` â†’ **dashboard Live** | sampled 20:53:48, 263 processes |
| The live stream actually moved: sampled 18:42:16 â†’ 18:42:29, CPU 75 % â†’ 78 %, 271â€“273 processes, 4754â€“4814 threads | a frame a second over the WebSocket, not a cached reading |
| `GET /power/status â†’ 200`, machine `online`, `E-K\META PC`, signed in `yes`, last boot 7 h ago | the Power tab |
| `GET /apps/list` renders the catalog with launch and close actions | the Apps tab |
| `POST /apps/launch â†’ 200` and `POST /apps/edge/terminate â†’ 200` (15 processes) | signed requests **with a body** â€” the case that used to be 415 |
| Cancel â†’ Stop â†’ Back after a live attempt: `Check address` and `Pair` both re-enabled, address and 44-character pin intact | D32 above |
| **The on-desktop prompt, clicked for real**: attempt â†’ prompt window â†’ click *Allow this device* â†’ poll `pending` â†’ poll `approved` with `deviceId ef9ad8efc29100d3`, `keyId 1` and a sealed envelope, and `Released the pairing secrets` in the log | D34 above â€” the path the whole product depends on and which no test had ever exercised |
| **The service starts, from a registration that would not start before**: after the reinstall `requiredPrivileges` is a `MultiString` with three elements, `sc start` succeeds, 48220 is listening and the session agent connects | D27 above â€” `REG_SZ` to `REG_MULTI_SZ`, and the installer's verification now checks *running*, not just *registered* |
| **Verified against a real boot, twice**: rebooted at 16:29 on 4 October and the agent did not come up; rebooted at 16:28:24 on 5 October and it logged its first line **ten seconds later, unprompted**, with no SCM event, then the session agent connected 30 s after that | the log line that would have answered this in one look â€” the first line of start-up â€” never existed before, because the process was never created |
| **`installer/install-cycle-test.ps1` against the delivered installer: 29 checks, 0 failed** across clean install â†’ install over its own output â†’ uninstall â†’ install again, each step asking whether the port answers | and the `requiredPrivileges` assertions among them are the ones D27 needed |
| **The idle pairing page now serves a QR that carries the pin**: `GET /pairing` contains `/pairing/qr.svg?connect=1`, `?connect=1` returns `image/svg+xml`, and the connect URL round-trips through the QR encoder's independent decoder | D35 below â€” the loop that made the app unpairable by hand, and the **APK is unchanged**, so a phone that already has it can now scan the QR |
| The SPKI pin is unchanged by the rebuild, so the value already on the pairing form is still correct | the certificate lives in `agent.pfx`, not in the binary |
| 415 responses since `Content-Type` was fixed | **zero** - the single 415 in today's log is the 12:31 run that found D31 |

Also on this same agent binary, from the previous APK rebuild: `POST /power/wake â†’ 200` and
`POST /apps/notepad/terminate â†’ 200` (Notepad 0 â†’ 2 â†’ 0). The installer is unchanged from the
hash at the top of this file. The agent is **not** the same binary it was before D34 â€” the
desktop prompt's answer is applied by the service, which it previously never was â€” so this
installer carries a real agent change. Before that it was byte-identical, and the three fixes
before it were in Dart: two navigation, one the pairing window described above.

**Known gaps**, all recorded rather than papered over: the remote-control foreground service
is wired but not started from the remote screen; three Gradle workarounds were needed
because this build machine's network returns 404 for every `dl.google.com` path (all three
are commented in place and are no-ops on a normal network). `docs/API.md` Â§15 and
`docs/SECURITY.md` Â§12 list the rest.

See `../docs/` for the full documentation, and `../docs/SECURITY.md` for the threat model
and its limits.
## D36 - the prompt closed on its own approval (found on the real phone, 19:19)

The phone's first attempt was approved from the desktop at 19:19:55 and then nothing happened.
The agent log says why, in 35 identical lines:

    Approved Android phone from the desktop prompt as d47f1861a1451a35, key 1
    Pairing bb673d2c2cf70a4e was polled without a valid provisioning secret

PairingPromptForm closed the dialog on every decision, approval included. The QR on that dialog
is the only copy of the provisioning secret, and the secret is exactly what the phone still lacks
when Allow is clicked - it cannot be handed the secret over the network, because that is what
the mechanism exists to prevent. So approving destroyed the transfer and the pairing could never
finish. The comment above RenderQr already described this outcome and the code did it anyway.

Approving no longer closes the window. It turns into a waiting state with the QR still up, and
closes by itself when the phone collects (PairingManager.PairingCollected, raised at the poll
that releases the secrets) or when the pairing window expires. Scan-then-allow and
approve-then-scan both work.

The attempt also left a paired device that could never authenticate, holding one of the eight
slots. esirc pair list showed it as LAST SEEN never. Revoked.
## D37 - the QR was drawn on fractional pixels, so no camera could read it

The phone reported that it had camera permission and could not find the code. The matrix was
correct throughout; the rendering was not.

A scanner samples module centres and measures the distance between transitions, so both need a
square grid - every module the same whole number of pixels across. Neither rendering provided
one:

- the desktop prompt drew 53 cells (45 modules plus the quiet zone, which is what a ~120 character
  polling URL needs) at 4 pixels each into 212 px, then squeezed the result into the 150 px the
  layout asked for. 150/53 is 2.83, so half the module boundaries fell on a fractional pixel
- the pairing page pinned width="240", leaving the browser to fit 53 cells into 240 px - 4.528
  each

Both now draw at 6 pixels per module and are placed at that size with no rescale. QrRenderabilityTests
reconstructs what a scanner sees - all three finder patterns, the pitch derived from them, the
1:1:3:1:1 signature sampled at module centres - and asserts the old rendering is unreadable, so
the suite cannot pass by being lenient.

The APK is unchanged. The camera was never the problem and the phone needs no new build.
## D38 - the prompt's picture box showed a red cross

D37 changed the prompt's QR to whole-pixel modules and wrapped the bitmap in a using, then handed that
bitmap to the PictureBox. The image was disposed the instant the renderer returned, so the control held a
dead one and GDI+ drew a red cross. The pre-D37 code had been correct by accident: its bitmap was declared
outside any using precisely because it escaped into the control, and the rewrite tidied that away.

All five D37 tests passed throughout. They check the grid's arithmetic and never obtain the bitmap the prompt
draws. The_prompt_draws_a_bitmap_that_is_still_readable_when_the_prompt_asks_for_it now runs the same
detector over the real pixels, and asserts the bitmap still holds black and white. Verified to bite.
## D39 - the QR code is gone from pairing

Asked why there was a barcode at all: the honest answer is that it only ever existed to move one 32-byte
provisioning secret from the PC to the phone. Two attempts at removing it by fixing the QR both failed on
real hardware - the prompt closed on its own approval (D36), then drew a code on fractional pixels (D37),
then a red cross (D38) - which is the argument for not needing it rather than for tuning it again.

**The phone now mints the secret itself** and sends it as 
once with POST /pair/request. It chooses its
own six-digit userCode, which the agent echoes back so both screens show the same digits.

This is not a weaker guarantee. It was never about *where* the secret was made, it was that only the party
the user meant could produce it - and the agent still releases the credentials only to a poll bearing the
right value. What changed is that the value now starts on the device already in the user's hand, so it does
not have to travel there.

The whole sequence is now: **Pair -> check the code matches -> Allow this device.** One click.

Both fields are optional, so an older app still pairs against this agent the old way, with the QR on the
prompt. The agent log records which path each attempt took.

The prompt drops the QR entirely when there is nothing to transfer, and says so: "Approve only if this code
matches the one on your phone."

**The APK has changed.** Install it once; after that no QR code is involved in pairing at all.
# ESI Remote PC — Android app

The phone half of **ESI Remote PC**, a LAN remote-control app for a Windows machine. The
Windows agent lives in `../windows-agent` and the wire contract in `../docs/PROTOCOL.md`;
this app implements that contract exactly, and where it could not, it says so on screen.

App id `com.esi.remotepc` · label **ESI Remote PC** · `minSdk 24` · `compileSdk 36` ·
`targetSdk 36` · Flutter 3.47.5 / Dart 3.13.4.

---

## What it does

| Screen | What it is |
|---|---|
| **Onboarding** | Find agents on the LAN, type an address, or scan the PC's pairing QR. Ends at a confirmed endpoint plus a verified pin. |
| **Pairing** | Runs the §3.2 handshake with a "waiting for approval on the PC" state, a countdown, and distinct error states for denied, expired, consumed and decryption failure. |
| **Dashboard** | CPU, memory, GPU, temperatures, storage, network, uptime and hardware inventory. Live over the WebSocket, with a polling fallback. |
| **Power** | Shut down, restart, sleep, hibernate, lock, sign out, force restart — each behind a confirmation that says what will happen *on the PC*. Plus Wake-on-LAN with the agent's readiness report. |
| **Gaming mode** | Toggle, profile selection, frame-rate history, and per-app suspend/resume. |
| **Apps** | The launcher catalog and the running-process list, with graceful-then-forced termination. |
| **Files** | Browse the sandbox with the roots shown, create/rename/move/delete, upload (with the §7.4 ticket flow for large files) and download. |
| **Screen** | Live tile-streamed screen with touch, drag, right-click and an on-screen key pad. |
| **Control** | Media transport and volume, plus links to notifications and settings. |
| **Notifications** | The agent's log with read/dismiss/clear, and the rule set behind it. |
| **Settings** | Agent settings, this phone's identity, key rotation, paired-device revocation, the security audit trail, sign-out and about. |

---

## Build

Prerequisites: Flutter 3.47.5 on `PATH`, Android SDK 36, JDK 17.

```bash
cd esi_remote
flutter pub get
flutter analyze          # expected: "No issues found!"
flutter test             # expected: 129 tests, all passing
flutter build apk --debug
```

The APK lands at `esi_remote/build/app/outputs/flutter-apk/app-debug.apk`.

Verified on this machine:

```
flutter analyze   No issues found!            (0 errors, 0 warnings, 0 lints)
flutter test      129 tests, All tests passed!
flutter build apk --debug   Built build\app\outputs\flutter-apk\app-debug.apk
```

APK: **183.28 MB** (192,186,219 bytes), SHA-256
`D7B34D8AE49A120F21BFFD9B40588F4310927AA6ADA1921D32E57DF379383697`. Debug builds are
multi-ABI and carry ML Kit's native barcode library, which is most of the size; a release
build with a single ABI is a fraction of this.

---

## How to pair

1. **Install the agent on the PC** and let it start. The tray icon appears; the device id
   and TLS pin are on the `https://<pc>:48220/pairing` page.
2. **Open the app.** It searches the local subnets and broadcasts a discovery probe
   automatically. Pick the machine, or enter the address by hand, or start a pairing attempt
   and scan the QR from the `/pairing` page on the PC.
3. **Confirm on the phone.** The app checks the address, then shows exactly what it is
   about to connect to.
4. **Approve on the PC.** A six-digit code appears in the desktop prompt — or, if nobody is
   signed in, on the `/pairing` page. The phone waits, showing the same code and a
   countdown. Nothing is paired until the PC says yes.
5. The phone unwraps its credentials and stores them in the Android keystore. Done.

Pairing needs a **pairing secret** in addition to the certificate pin. It exists only on
the PC, on its pairing screen or in its QR code — see *Deviations* below for why.

---

## Security, and what the UI is careful about

* **TLS with SPKI pinning.** The agent's certificate is self-signed, so the pin delivered
  out of band in the pairing QR is the entire identity check. Pinning happens in
  `HttpClient.badCertificateCallback` by computing `base64(SHA-256(DER(SubjectPublicKeyInfo)))`
  from the presented certificate. `dart:io` exposes no public-key accessor, so the app walks
  the DER itself (`lib/src/net/spki_pinning.dart`); the walk is tested against a real
  self-signed P-256 certificate.
* **Signed requests.** Every call past the pre-auth surface carries the six `X-ESI-*`
  headers of §4.1. The canonical target is built from the *raw* path and the *raw* query
  string the client itself generated, so the bytes signed are byte-for-byte the bytes sent.
* **Honest about unverified data.** A pin read from `GET /handshake` could have been
  written by anything on the wire (§2.1), so the UI labels it *pin unverified* and never
  fills the pin field from it.
* **Honest about missing data.** A `null` temperature is not a zero-degree reading. Every
  nullable reading formats as a dim em dash through one accessor
  (`EsiReadout(value: null)`), and `test/models_test.dart` and `test/ui_test.dart` assert
  both that the null survives parsing and that it renders as a dash. Severity has a
  distinct `unknown` state so a panel with no data never looks like a healthy panel.
* **Confirmations say what happens.** A dialog that says "Are you sure?" tells the user
  nothing they did not already suspect. The power actions spell out what Windows will do,
  and shutdown/restart/force-restart require typing a word.

---

## Design

Dark, glassmorphic, and built around the idea that **numbers are the content**:

* **Two typefaces with distinct jobs.** Space Grotesk carries all language; JetBrains Mono
  carries every number, path, id and pin, because those are read in columns and compared
  character by character. Both are bundled as static instances (instanced from the
  upstream variable fonts by `tool/instance_font_weight.py`) and the OFL licences ship
  alongside them.
* **A four-temperature palette** rather than one accent, so "nominal", "busy", "wrong" and
  "this is key material" are all distinguishable: aqua, amber, rose, violet, on a cool
  blue-black.
* **Structure carries state.** Every panel has a 2×22 px signal tick whose colour is that
  subsystem's severity, so the left edge of a column of panels is a legible status display.
* **One memorable element.** The load trace at the top of the dashboard is a real
  multi-channel oscilloscope on a shared time axis, because the question about a machine
  you are not sitting at is about the *shape* of the last few minutes, not the current
  value. Gaps in the data are drawn as gaps.
* **Motion is rationed.** The trace and the status spine's live pulse are the only
  non-user-initiated animations in the app.

---

## Layout

```
android-app/
├── README.md                  ← this file
└── esi_remote/
    ├── pubspec.yaml
    ├── analysis_options.yaml
    ├── assets/fonts/          Space Grotesk + JetBrains Mono (static, OFL)
    ├── tool/
    │   ├── generate_golden_vectors.py   Transliterates EsiCrypto.cs → test vectors
    │   └── instance_font_weight.py      Variable font → static instances
    ├── android/                          Gradle, manifest, launcher resources
    ├── lib/
    │   ├── main.dart
    │   └── src/
    │       ├── protocol/                Constants, DTOs, crypto, QR parser
    │       ├── net/                     Pinned TLS, signed REST, signed WS,
    │       │                             discovery, mDNS, Wake-on-LAN
    │       ├── data/                     Machine store, pairing service
    │       ├── state/                    Riverpod notifiers per feature
    │       └── ui/                       Theme, widgets, router, screens
    └── test/
        ├── protocol_golden_test.dart     38 tests: the crypto contract
        ├── protocol_shapes_test.dart     41 tests: QR, query building, frames
        ├── models_test.dart              30 tests: DTO parsing, null honesty
        ├── spki_pinning_test.dart        10 tests: the DER walk
        └── ui_test.dart                  26 tests: formatting and widget behaviour
```

### The crypto tests are golden vectors, not self-consistency checks

`tool/generate_golden_vectors.py` is a line-for-line transliteration of the agent's
`EsiCrypto.cs` and `ProtocolConstants.cs`: the same HMAC key/message order, the same HKDF
extract/expand loop, the same `string.Join('\n', …)` canonical string, the same base64. It
asserts the published **RFC 5869 A.1/A.2/A.3** vectors before emitting anything, so a
divergence from the RFC would fail the generator rather than quietly agree with the app.

`test/protocol_golden_test.dart` additionally asserts the canonical string against the
worked example printed in `docs/PROTOCOL.md` §4.2, line for line.

There is no .NET SDK on this machine, so the C# could not be executed to produce the
vectors; the transliteration is the derivation, and the RFC vectors plus the normative
document are the independent checks on it.

---

## Deviations from `PROTOCOL.md`, and why

1. **The WebSocket upgrade is signed with headers, not query parameters.** §7.1 says the
   `X-ESI-*` values go in the URL query so they never land in a log. The agent does not
   implement that: `SignatureAuthenticationMiddleware` reads only `X-ESI-*` headers
   (`RequestVerifier.cs`), and `/ws` is not on the public list, so the upgrade is verified
   as an ordinary signed request over path `/ws` with an empty body. The app sends them as
   headers, which still keeps the signature out of the URL. *The agent would need a change
   to match the document; the app matches the agent.*

2. **Discovery is a three-channel search, not broadcast alone.** PROTOCOL.md §9.1 is
   implemented exactly — `ESI_DISCOVER_V1` to `255.255.255.255` and every subnet-directed
   broadcast address. But **Dart's `RawDatagramSocket.receive()` returns the socket's own
   address, not the sender's**, so a broadcast reply can identify an agent but not say
   where it is. The app therefore also runs a directed TCP connect sweep of the local /24s
   plus an unauthenticated `GET /handshake` on whatever answers, and a best-effort mDNS
   query, which *does* carry A records. Results merge by device id. The broadcast channel is
   what identifies an agent; the sweep is what locates it.

3. **The pairing QR payload format is invented.** §5.4 and §9.3 say the code carries the
   address, the device id, the pin and the provisioning secret, but **no code in the agent
   emits a QR** — there is no CLI source, and the tray item referenced in the pairing page
   does not exist in the repository. The parser (`lib/src/protocol/pairing_qr.dart`) is
   therefore deliberately liberal: a URI, a bare query string, a JSON object or a bare
   address, with every field looked up under every plausible name. Anything missing is
   reported **by name**, so the app says "the QR code did not include the pairing secret"
   rather than "invalid QR". Both the QR and the manual form reach the same code path from
   there.

4. **Local-network access is not declared as a permission.** `ACCESS_LOCAL_NETWORK` does
   not exist until API 37 and `compileSdk` is 36, so it cannot be declared.
   `targetSdk 36` gets the implicit grant on Android 17; bumping to 37 would require the
   manifest entry and, on some devices, a runtime prompt.

5. **Upload ticket path is parsed before use.** §7.4's `uploadUrl` already contains its
   own `?ticket=…` query. It is split into path and query and the query is passed through
   verbatim, because the signed path would otherwise be the whole string.

6. **The pairing secret is required, not optional.** The agent approves a pairing without
   it but then never releases the wrapped secrets (`PairingManager.Poll` returns
   `status: "error"`). A flow that appeared to work and then stalled would be worse than one
   that says up front what it needs.

7. **`up, uploads, the "now playing" metadata, and clipboard sync` are not in the agent.**
   The UI says so rather than showing a control that cannot work: the media screen states
   that the protocol reports transport state, not track titles.

---

## Known limitations

**In the build**

* **Google Maven is unreachable from the network this was built on** — every path under
  `dl.google.com` returns 404, including for AGP versions that certainly exist. Two
  mirrors of Google Maven are therefore listed *after* `google()` in
  `android/settings.gradle.kts` and `android/build.gradle.kts`, scoped by `content { … }` to
  the groups that actually live there. On a network that can reach `dl.google.com` the
  mirrors are never consulted and the build is unchanged; remove them if you prefer.
* **`android_file_picker` 2.0.0 pins Kotlin Gradle plugin 1.8.22 in its own `buildscript`
  block** (with a comment in its own source acknowledging it is a workaround). That daemon
  predates Gradle 9 and crashes while closing its incremental caches, even though
  compilation succeeds. `kotlin.compiler.execution.strategy=in-process` in
  `android/gradle.properties` removes the daemon.
* **`path_provider_android` is pinned to 2.2.23.** 2.3.x pulls in `jni`, which needs NDK and
  CMake 3.22.1 — neither of which can be installed here, because the SDK component
  downloads come from `dl.google.com`. Nothing in this app uses path_provider on Android
  (`flutter_secure_storage` depends on it only for Windows), so the pin costs nothing. It
  is in `dependency_overrides` with that explanation.
* `android.newDsl=false` and `android.builtInKotlin=false` are Flutter's own template
  opt-outs for AGP 9; Flutter's Gradle plugin does not read the new DSL yet.

**In the product**

* **No notification permission is requested at runtime.** On API 33+ this only gates the
  *display* of the foreground-service notification; the service itself starts either way,
  so a screen stream survives with notifications switched off. `POST_NOTIFICATIONS` is
  declared but never asked for, which is why `permission_handler` is not a dependency — and
  why no 12.x/13.x conflict exists.
* **`flutter_foreground_task` is a dependency but not yet wired to the screen stream.**
  The remote screen runs and reconnects while the app is in the foreground; keeping it
  alive in the background needs the service started from the remote screen, which is the
  next piece of work. The permission and the plugin are in place.
* **mDNS is best effort by design.** The query is hand-rolled over `RawDatagramSocket`
  rather than `NsdManager`, which would need `NEARBY_WIFI_DEVICES` from API 33 and
  `ACCESS_LOCAL_NETWORK` from Android 17. The DNS parser is deliberately partial — it reads
  A, SRV and TXT rdata and drops a malformed packet rather than throwing — because full DNS
  parsing is not worth the surface area for a fallback path. A network that filters
  multicast loses mDNS and keeps the other two channels; the screen says which channels saw
  each agent.
* **Discovery sweeps /24s only**, capped at 254 addresses per interface, 6 seconds. A
  genuinely larger subnet will be missed and the app says the scan reached its time limit
  rather than reporting a short search as complete.
* **Sensors are the agent's, not the phone's.** A machine with no readable CPU temperature
  reports `null` and this app shows a dash. The app does not estimate, interpolate or carry
  a value forward.
* **Frame rate is the agent's best-effort counter** (§6.9 says so) and is labelled with its
  source. The chart's dashed reference line is the primary display's refresh rate, because
  the game's own frame cap is not exposed by the protocol.
* **Screenshots and release signing** are not set up. `android/key.properties` is read when
  present and the release build falls back to the debug keys without it, which is only ever
  useful locally.
* **Not runtime-verified against a live agent.** The Windows agent is, per the brief, and its
  contract is normative, but this app has not been run against it on a LAN. Everything
  crypto is verified against transliterated golden vectors; everything else is verified
  against the documented JSON shapes and the code. Treat the first pairing as the real
  test.

---

## Licence

The bundled fonts are under the SIL Open Font License; the licence texts are in
`assets/fonts/`.

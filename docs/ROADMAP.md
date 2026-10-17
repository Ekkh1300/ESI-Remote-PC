# ESI Remote PC — Roadmap

What is built, what is deliberately not built, and what would come next with reasons. This
is a statement of intent, not a schedule. Items are ordered by the damage they do if left
alone, not by convenience.

Where a claim here is checkable, the file and the symbol are named so it can be verified.

---

## 1. Done and verified

The following is implemented, covered by tests, and exercised by the smoke test.

### Transport and identity

| Item | Where |
|---|---|
| Self-signed ECDSA P-256 TLS identity, DPAPI-protected at rest, machine or user scope by account | `CertificateAuthority.cs` |
| SPKI SHA-256 pinning | `CertificateAuthority.ComputePin` |
| HMAC-SHA256 request signing over the 8-line canonical string, constant-time comparison | `EsiCrypto.cs`, `RequestVerifier.cs` |
| HKDF-SHA256 key hierarchy (`session`, `wrap`, `provision`) | `EsiCrypto.DeriveSessionKey` / `DeriveWrapKey` / `DeriveProvisioningKey` |
| Nonce replay cache, per device, swept | `RequestVerifier.TryRecordNonce` |
| AES-256-GCM secret wrapping with AAD | `EsiCrypto.Seal` / `Open` |
| Key rotation with a 120 s previous-key window | `PairingManager.Rotate` |
| RFC 5869 HKDF conformance and a golden canonical-string vector | `EsiCryptoTests.cs` |

### Security controls

| Item | Where |
|---|---|
| LAN-only enforcement before authentication | `LocalNetworkMiddleware`, `LocalNetworkGuard` |
| Scope table, one reviewable mapping | `RequestSignatureValidator.RequiredScope` |
| Token-bucket rate limiting per device and per address, six categories | `RateLimiter.cs` |
| Device master secrets DPAPI-protected at rest | `DeviceStore.Protect` / `Unprotect` |
| File sandbox: canonicalisation, reparse-point resolution, device/UNC/ADS/reserved-name rejection | `FileSandbox.cs`, `FileSandboxTests.cs` |
| Append-only hash-chained audit log with `chainValid` reporting | `AuditLog.cs` |
| Human approval mandatory, denial as the default path | `PairingPromptForm.cs` |
| Provisioning-secret binding so an approval race yields nothing usable | `PairingManager.Poll` |
| Single-use upload tickets bound to `(device, path, size)` | `UploadTicketStore` |
| Uniform error envelope, including framework-generated 400s | `ErrorEnvelopeMiddleware`, `EsiProblemDetailsService` |

### Product surface

| Item | Where |
|---|---|
| 58 route registrations under `/api/v1` (59 method/path pairs — `PUT` and `POST` on `/files/upload` are one `MapMethods` call), plus `/pairing` and `/ws` | `Endpoints.cs` |
| WebSocket hub: subscriptions, metrics push, notification push, screen stream, input | `WebSocketHub.cs` |
| Session-0 service and per-user session agent, split on the interactive-desktop boundary | `ARCHITECTURE.md` §2 |
| Named-pipe command channel with a DACL narrowed to the console session's own user | `SessionAgentBridge.CreatePipeServer` |
| GDI screen capture with a tile hash grid and JPEG delta encoding | `ScreenStreamEncoder.cs` |
| `SendInput` injection with W3C `KeyboardEvent.code` mapping and Unicode fallback | `KeyMap.cs`, `InputInjector.cs` |
| UDP broadcast discovery and an RFC 6762/6763 mDNS responder | `UdpDiscoveryResponder.cs`, `MdnsResponder.cs` |
| Telemetry: PDH counters, NVML, WMI, temperatures — never a fabricated value | `SystemSampler`, `NvmlProvider`, `TemperatureProvider` |
| Power control, WoL sending and readiness reporting | `PowerService`, `WakeOnLanSender`, `PowerStateProbe` |
| Gaming mode with exact previous-state restore | `GamingService` |
| Notification rules with sustain timers and cooldowns | `NotificationEngine`, `NotificationRuleCatalog` |
| Two Windows services with failure recovery and `requiredPrivileges` | `ServiceManager.cs`, `ESI-RemotePC.iss` |
| PC audio captured on the loopback endpoint and played on the phone over the WebSocket | `AudioPump.cs`, hub 0x04 audio frames, phone `PhoneSpeaker` |
| `esirc` administration CLI | `ESI.RemotePc.Agent.Cli` |
| Helper install and uninstall through the logon task, one code path shared with the installer | `TaskCommands.RegisterSessionAgentTask`, `ServiceManager.Install` / `Uninstall` |
| 447 test methods — 38 crypto, 32 sandbox, 21 HTTP integration including a full middleware-pipeline harness | `ESI.RemotePc.Tests` |
| A smoke test that boots the published binary and runs 13 checks | `tests/smoke-test.ps1` |

---

## 2. Known defects in shipped code

Not "limitations of the design" — defects. Each is a specific, locatable behaviour that
contradicts the code's own stated intent or the protocol. All are documented in
[`SECURITY.md`](SECURITY.md) and [`API.md`](API.md); they are listed here because they are
what should be fixed first.

### Blocking

| # | Defect | Location | Effect |
|---|---|---|---|
| ~~D1~~ | **RESOLVED.** The QR emitter exists: `QrEncoder` renders byte-mode ECC-L versions 1-10, `GET /pairing/qr.svg?code=` serves it as SVG, and the desktop prompt shows it inline. The round trip is verified by decoding the emitted matrix back to the URL. A headless machine additionally has `esirc pair url <code>`, which prints the same URL the QR encodes | `QrEncoder.cs`, `Endpoints.RenderQrSvg`, `PairCommands.Url` | Was: **pairing could not complete on a stock build.** No longer applies |
| **D2** | `POST /api/v1/system/process/{pid}/terminate` requires only the `read` scope. `/api/v1/system` matches no special prefix, so `RequiredScope` returns the `read` default without consulting the method | `RequestSignatureValidator.RequiredScope` | A `read`-only device can terminate arbitrary processes, including other users', with `SeDebugPrivilege` |
| **D3** | WebSocket frames are not scope-checked. The `/ws` upgrade passes through the scope table as `read`; `DispatchAsync` then handles `remote.input` and `remote.key` with no further check | `WebSocketHub.DispatchAsync` | A `read`-only device can inject mouse and keyboard into the interactive desktop, bypassing the `control` requirement on `POST /remote/input` |

D2 and D3 are the same class of bug: the scope table is a prefix table with a permissive
default, and the WebSocket has no equivalent table at all. The fix for both is small and
local.

### Correctness

| # | Defect | Location | Effect |
|---|---|---|---|
| **D4** | The rotation grace period does not work. `ApplyRotation` replaces `master_secret` with the new value; `Verify` then accepts the previous `keyId` but derives the session key from the *new* secret, so a client holding the old one fails with `esi_signature` rather than succeeding | `DeviceStore.ApplyRotation` + `RequestVerifier.Verify` step 5 | The documented 120 s dual-key window is a header-tolerance window only |
| **D5** | The pairing rate limit is 5 tokens per **minute**, not per hour. `TryConsume(key, perMinute: PairingPerHour, burst: 2)` refills at `perMinute/60000` per millisecond | `RateLimiter.TryConsumeForAddress` | The comment, the field name and `PROTOCOL.md` §3 all say "per hour". A phone retrying a failed pairing is not locked out |
| **D6** | The nonce cache is in memory, so a restart empties it and a request captured before the restart can be replayed inside the 300 s window | `RequestVerifier._nonces` | Accepted trade for the metrics path, but it should be stated in the threat model rather than left implicit |
| **D7** | `IsHiberbootEnabled` reads only `HiberbootEnabled` from the registry. The `powercfg /a` fallback in the comment ("Hybrid Boot" detection) is not implemented — the method returns `false` when the value is absent | `PowerStateProbe.IsHiberbootEnabled` | A machine with Fast Startup managed by other means reports `hiberbootEnabled: false` and no issue |
| **D8** | `PairStatusResponse.spkiSha256` is declared and never populated | `PairingManager.Poll` | A client that expects the pin here gets nothing. The DTO invites the mistake |
| **D9** | `GamingStateResponse.sessionAgentConnected` is hardcoded to `false` | `GamingService.GetState` | Carries no information and will mislead a client |
| **D10** | `AppEntryDto.iconPath` is declared and never populated by the catalogue | `Catalogs.cs` | An icon-less launcher list |
| **D11** | `HubConnection.IsAlive` is defined with a 45 s threshold and never called. There is no idle-socket reaper and no failed-send counter | `WebSocketHub` | Half-open sockets persist until the client or the OS notices |
| **D12** | `NetworkProvider.GetWakeCapableAdapters` carries a doc comment saying "adapters that are wired" and applies no filter, so Wi-Fi and virtual adapters appear in `armedDevices` | `NetworkProvider.GetWakeCapableAdapters` | Noise, not a security issue. The endpoint only uses the list to raise the "no wired adapter" issue |
| **D13** | `SettingsStore.UpdateAsync` warns about `localNetworkOnly`, `requireSignedRequests` and `tlsEnabled` and then overwrites all three back to `true` | `SettingsStore.UpdateAsync` | The warnings are misleading: they describe a change that is then silently reverted |
| **D14** | `EsiCrypto.DeriveAuditKey` is implemented, mirrored on the Android side, and documented in `PROTOCOL.md` §5.2 — and never used. `AuditLog` chains unkeyed hashes | `EsiCrypto`, `AuditLog` | The audit chain is tamper-evident but not keyed, and the specification overstates it |
| **D15** | `AuditLog.ActionAuthSucceeded` is declared and never recorded | `AuditLog` | Successful authentications are absent from the trail |
| **D16** | `schema_version` table exists with no migration runner | `AgentDatabase` | The first schema change after release will need one written from scratch |
| **D17** | `AgentOptions.SessionTokenPath` declares `session.token`, described in `ISessionAgentBridge` as the session agent's per-boot credential for a reverse HTTP channel — and nothing writes or reads it | `SettingsStore.AgentOptions` | The documented agent→service reverse channel does not exist. The pipe is the only reverse path |
| **D18** | `PowerStatusResponse.pendingPowerAction` is declared and never populated | `PowerService` | Always `null` |
| ~~D19~~ | **RESOLVED.** `SessionAgentBridge` orphaned an open named-pipe server instance. `PumpAsync` cleared `_connected` in its `finally` but never closed the pipe it had been reading, and `EnsureConnectedAsync` then ran `_pipe = CreatePipeServer()` straight over the old reference. The pipe name allows one server instance, so the orphaned one made every later `CreatePipeServer` fail with `All pipe instances are busy` - the exact exception the agent log was already printing - and after that the bridge could never reconnect, leaving the session agent at `NotRunning` for the life of the process. The pump now closes the instance it owns, and `EnsureConnectedAsync` releases any leftover before creating another | `SessionAgentBridge.PumpAsync` finally, `SessionAgentBridge.EnsureConnectedAsync` | Was: remote input and screen frames stop working after the first reconnect, with no error a user can act on. No longer applies |
| **D20** | An install once timed out waiting for the service to disappear from the SCM and warned that a reboot was needed, and `sc start` then answered 87 with the key in delete-pending. **The cause was not established.** The obvious suspect - the session helper holding the service handle, because killing the helper made a pending delete complete at once - does not survive measurement: with the helper running, a clean stop takes the service process down in 0.6 s and the key is removed in 0 s, and the full install cycle passes 26/26 with no delay. Something held the key on that one occasion and has not held it since | `WaitForServiceGone`, `StopAgentService` | Twice, a reinstall looked like it had done nothing, and once it reported a false "reboot, then run this installer again". The installer says what it saw, which is what made it findable at all |
| ~~D21~~ | **RESOLVED.** `esirc install` could not create the service at all. `ServiceManager.CreateService` passed `start= {(int)start}`, and `sc.exe` wants a keyword (`auto`, `demand`, ...) - the numeric value drew `ERROR: Invalid start= field` and the CLI reported a failure to create. The installer's own `[Code]` had it right all along, which is why the shipped `.exe` worked and only the CLI was broken. `StartModeKeyword` maps the enum; `esirc install` now creates and starts the service | `ServiceManager.CreateService`, `StartModeKeyword` | Was: the documented CLI install path fails outright, and the only reason it was not caught is that nothing in the repository runs it. Verified on this machine: `esirc install` now reports "created ESIRemotePcAgent / started the service" and the service reaches `Running` |
| ~~D22~~ | **RESOLVED.** Pairing could not be started from the app at all. `_start` used `context.pushNamed('/pairing', ...)`, and go_router resolves a name through `RouteMatchList.namedLocation`, which walks the current match list and dereferences each route's `name` - no route in the app declared one, so every attempt threw `Null check operator used on a null value`. The exception escaped before `_busy` was cleared, so the Pair button stayed greyed out for the rest of the session and the phone looked merely "busy" rather than broken. Now pushed by path, with the busy flag cleared in a `finally` | `onboarding_screen.dart` `_start`, `router.dart` | Was: no phone could ever reach the pairing screen, so the app's whole purpose was unreachable. The 138 Dart tests all passed while this was true, because none of them drove a navigation |
| ~~D23~~ | **RESOLVED.** The app demanded the one-time provisioning secret *before* it would start a pairing attempt. The secret cannot exist at that point: the agent mints it while handling `POST /pair/request`, and the only things that carry it off the PC are the approval prompt's QR and `/pairing?code=...` - both of which need a live attempt. The two requirements deadlocked, and worse, pasting a stale secret made the agent consume the attempt before its unwrap failed. An attempt now starts with no secret, the waiting screen offers Scan/Paste for the PC's QR while the attempt is live, the poll omits `secret=` until one is adopted, and a poll that comes back `error` because the secret is still missing is treated as the cue to scan rather than as a failure | `PairingRequest`, `PairingFlowNotifier.adoptSecret`, `PairingService._poll`, `_Waiting` | Was: pairing is impossible from the phone by every route. The onboarding form's secret field is gone, since any value typed or pasted there belongs to a different attempt and would burn the code just issued |
| ~~D24~~ | **RESOLVED.** `PairingService.pair` closed its HTTP client in a `finally` wrapped around the *request*, then used that same client for every poll of the attempt. Every poll failed with `HTTP request failed. Client is already closed`, so the credentials could never be collected however correct the secret was. The client now lives for the whole handshake and is closed once, when the flow returns | `PairingService.pair` / `_run` | Was: the handshake completes and the phone still cannot obtain its credentials, and the only symptom is a retry loop that looks like the PC is flaky |
| ~~D25~~ | **RESOLVED.** `MachineStore.loadAll` rebuilt each machine from the index, and the index is only a pointer list - `{deviceId, host, port}` - while the master secret lives in the record the pointer names. Every machine therefore came back with `masterSecret == null`, `isPaired` false, and every screen reported "Pairing did not finish" after a successful pairing. `loadAll` now reads the record and falls back to the index entry if it has gone | `MachineStore.loadAll` | Was: a paired phone is unusable after any restart, permanently, and the stored secret is right there on disk. Eight regression tests now cover the round trip; there were none before |
| ~~D26~~ | **RESOLVED.** `ConnectionNotifier.build` called `_attachSocket` and `_refresh` inline, and both read and assign `state` - which Riverpod does not initialise until `build` has returned - then ended with `return state`. Opening any paired PC threw `ProviderException: Tried to use a provider that is in error state` (`Bad state: Tried to read the state of an uninitialized provider`) and the shell rendered as a blank grey screen. `build` now returns a fresh status and defers both side effects by one microtask | `ConnectionNotifier.build`, `_attachSocket`, `_refresh` | Was: every paired phone lands on a blank screen on the dashboard. The widget tests built the shell with nothing paired, which is exactly the state that does not trigger it |
| ~~D27~~ | **RESOLVED.** `requiredPrivileges` was written as **REG_SZ**, holding all three privileges in one space-separated string. The SCM reads that value as a REG_MULTI_SZ and looks up each element as a privilege name, so it searched for a privilege literally called "SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege", found none, and refused to create the process image at all - `ERROR_INVALID_PARAMETER`, which `sc start` reports as 87 and the event log as SCM 7000 "The parameter is incorrect". That is the whole reason this was so hard to see: the agent's own log had no line after the boot timestamp, because the process never reached its first line, so the one piece of evidence that would have answered it in a glance never existed. Two changes. The value is written as REG_MULTI_SZ through `reg.exe`, because Inno's `RegWriteMultiStringValue` takes `RootKey` as an `HKEY` and its payload as a single `Data: String` and so cannot express three separate privileges at all, and because `reg.exe` rejects the `\0\0` terminator the documentation shows - the last element needs `\0`. And the installer's verification no longer asks only whether the service is registered but whether it is **running**: "the key exists" was the single check that had been passing this by for months, and it is exactly the check that cannot distinguish a working agent from a dead one | `RegisterEverything`, the new `ServiceIsRunning`, the post-install check in `CurStepChanged` | Was: every reboot left the agent down, so the phone could not reach the machine at all and the Start Menu entry appeared to do nothing - the one open item that decided whether the product was finished. Verified against a real boot at last: the machine rebooted at 16:29 on 4 October, the agent did not come up, and the SCM event said "The parameter is incorrect" with nothing in the agent's own log. After the reinstall `requiredPrivileges` reads back as a `MultiString` with three elements, `sc start` succeeds on the first attempt, 48220 is listening, the session agent connects, and the setup log now says "Verified: the agent service is running and the logon task is registered." Two earlier theories are disproved rather than assumed away: the unquoted `ImagePath` over a path with spaces is real, because `sc create` consumes the outer quotes, and it changes nothing - the SCM starts the service with the path exactly as the installer writes it; and the 32-bit `sc.exe` stamping `WOW64 = 0x14c` is no longer present on the key, and the service still failed. **Verified on the boot itself**, which is the only test that counts and the one `shutdown /r` refused to let me run: the machine rebooted at 16:28:24 on 5 October, the agent logged its first line at 16:28:34 - ten seconds later, unprompted, with no SCM event - reached `ready`, and the session agent connected 30 seconds after that. `Start = automatic` now means it |
| ~~D28~~ | **RESOLVED.** The channel between the service and the session agent had only ever worked in one direction. `SessionChannel.SendAsync` set `pipe.WriteTimeout = 5_000` before writing, but the pipe is opened with `PipeOptions.Asynchronous` and .NET refuses a write timeout on that handle: the assignment throws `InvalidOperationException("Timeouts are not supported on this stream")`, the surrounding catch swallows it, sets `_connected = false` and logs it at **Debug** - below the `Information` floor the process logs at, so it was invisible. Every send after connecting became a no-op. The service therefore never received a heartbeat, declared the agent dead 45 s later, tore the pipe down, and the agent reconnected 3 s after that: `Session agent connected` **1675** times against `Session agent ready` **0** times, and **1650** heartbeat timeouts, continuously since 30 September. The bound now comes from a `CancellationTokenSource` around `WriteAsync`, and the failure is logged at `Warning` because a channel going dark is not a debug detail | `SessionChannel.SendAsync`, `SessionChannel.WriteTimeout` | Was: screen streaming, remote input, lock state, telemetry deltas from the session and any desktop approval of a pairing prompt could never reach the service, while the phone saw a PC that answered HTTPS perfectly and did nothing else. Found by running the session agent with `--verbose`, which is the only reason the `Debug` line was ever visible |
| ~~D29~~ | **RESOLVED.** `ShowPairingPromptAsync` posts the dialog to `MessageLoopAnchor` with `BeginInvoke`, and the anchor's window handle did not exist yet: `Run` created the anchor *after* starting the command channel and only got a handle when `Application.Run` began pumping. A `pairing.prompt` delivered in that window threw `Invoke or BeginInvoke cannot be called on a control until the window handle has been created`, the prompt was reported as a failure and never appeared, while the phone waited for an approval nobody could give. The anchor is now created and its handle forced before the channel is started; messages queue harmlessly until the loop starts | `SessionAgentRuntime.Run`, `MessageLoopAnchor.Post` | Was: pairing approved from the PC desktop could fail silently depending on timing. Eight occurrences were in the log. Was masked by D28 - the channel never delivered a prompt successfully anyway |
| ~~D30~~ | **RESOLVED.** Two installer faults, both found from a phone that timed out against a PC which answered its own address from the command line. **One:** the network was set to `Public`, and the firewall rule is scoped to `private,domain` by design, so for anything off the machine the port was closed while the agent logged itself as listening. The installer now asks `netsh` for the active profile after adding the rule and says exactly what to change if it is Public. **Two:** `netsh advfirewall firewall add rule` has no notion of replace, so every install over an existing copy appended another rule with the same name - this machine had three of each, six entries for two ports. Delete now precedes add | `WarnIfNetworkIsPublic`, the `addfirewall` task | Was: the product looks broken - a phone that sits timing out - when it is a network classification, and nobody would suspect a setting from a timeout alone |
| ~~D31~~ | **RESOLVED.** `EsiApi._perform` set `Content-Type` in an `else if` on the unsigned branch only, so every *signed* request carrying a body went out with no media type. ASP.NET's body binder answers **415** before the endpoint runs, because it cannot bind a body it cannot identify. The signed branch computes the signature over method, target, timestamp, nonce, content hash and identity - never `Content-Type` - so nothing about the signature was wrong, and nothing about the request looked wrong either. The header is now attached whenever a body is, signed or not | `EsiApi._perform` in `lib/src/net/esi_api.dart` | Was: launching and terminating apps, **all six power actions**, Wake-on-LAN relay, file create/move/rename, uploads, gaming mode, media keys, notification rules, saving settings and key rotation all failed with 415 - while handshake, system info, metrics, power status, the app list and the live stream are all `GET` and worked perfectly, so the phone showed a healthy PC that could not be told to do anything. Pairing was unaffected because it is unsigned, which is why the one write path that was exercised kept passing. Found by launching Notepad from the phone and reading `POST /api/v1/apps/launch -> 415` out of the agent's log; six regression tests added, three of which fail against the previous code |
| ~~D32~~ | **RESOLVED.** `_start` awaits the future `context.push('/pairing')` returned, and that future's `finally` is the only thing that clears the `_busy` which greys out `Check address` and `Pair`. The pairing screen left its route with `context.go('/onboarding')` from *Start again* and from the empty state's *Back*, and `go` replaces the whole stack instead of popping it. go_router completes a pushed route's future only from `GoRouterDelegate._completeRouteMatch`, which runs on a pop, so `go` dropped the `ImperativeRouteMatch` without ever completing its completer: the `await` never resumed, `finally` never ran, and because `/onboarding` is a base route its `State` is reused rather than recreated, so the form came back still `_busy`. Both exits now pop, guarded by `context.canPop()`, with `go` kept only for arriving at `/pairing` without having been pushed | `_leavePairing` in `lib/src/ui/screens/onboarding_screen.dart` | Was: after any cancelled or failed attempt the form came back with `Check address` and `Pair` greyed out for ever, and only an app restart recovered it - the most visible dead end in the app, because cancelling is the normal thing to do after mistyping an address. Found by cancelling a pairing instead of completing one, and reproduced twice. A widget test drives fill -> Pair -> fail -> Start again and asserts both buttons are live again; against the previous code it fails with `Expected: not null / Actual: <null>` for `Pair`. Verified on the device: Cancel -> Stop -> Back hands the form back with both buttons enabled and the address and 44-character pin untouched |
| ~~D33~~ | **RESOLVED.** The pairing window was computed by subtracting one machine's clock from another. `PairingService._run` built its give-up deadline as `now + (created.expiresAt - _nowSeconds()).clamp(10, 900)`, but `expiresAt` is stamped by the **agent** and `_nowSeconds()` is this phone's, so the quantity being clamped was the offset between their clocks rather than the window. That clamp's 10 second floor turns any disagreement larger than `window - 10` into a 10 second pairing window. On the PC that prompted this, the Windows Time service had never started and NTP (UDP 123) is blocked on its network, so its clock ran 110 s behind real time: the stamp landed only about 10 s into the phone's future, and the phone reported "The pairing window closed before it was approved" after exactly nine polls - four attempts running, each 8.2 s long - while the agent was still holding the attempt open and waiting for somebody to click Approve. The phone now reads `serverTime` from the agent's handshake and maps `expiresAt` through that offset onto its own clock, which turns the window into a duration instead of a difference between two clocks, so no offset in either can shorten it; the countdown on the pairing screen is read from the same number and stops lying too. `expiresAt` is not an input to the rule any more. The handshake is read best effort, and if it cannot be read the stamp is taken at face value, which is what the app did before and is correct exactly when the clocks already agree | `PairingService.closesAt`, `PairingService.deadlineEpoch`, `PairingService._run` | Was: on any PC whose clock is more than about 110 s out, no phone can ever pair, and the message names a window that is still open on the agent. Found from a real phone against a real PC, and invisible to every test to date because an Android emulator's clock is slaved to the host's and so was out by the same 110 s - the whole suite passed while pairing was impossible from any device with a correct clock. Five tests added in `pairing_service_test.dart` cover lags of 0 to 600 s in both directions and record what the old rule computed on that PC (`clamp(10, 900)` gave 10). Verified on the device by setting the phone's clock 109 s ahead of the PC's, which reproduces the failure deliberately: the countdown reads a full 120 s and pairing completes as `bca92023c70adb41`; before the fix that same setup fails in 10 s |
| ~~D34~~ | **RESOLVED.** The Allow button on the PC never did anything, and two independent breaks had to be fixed for it to. **One:** `SessionAgentRuntime.ShowPairingPromptAsync` completed its task with `form.Decision` read *before* `form.ShowDialog()`, and `PairingPromptForm.Decision` is initialised to `PairingDecision.TimedOut` - so the prompt reported itself expired before the user had been offered a choice, whatever they then pressed. The decision is now read after the dialog closes. **Two:** the service learned the outcome from `SessionAgentBridge.PairingApprovalRequested`, an event with a declaration and exactly one `?.Invoke` and no subscriber anywhere in the solution, so even a correct report would have been discarded. The event was also shaped like a fresh invitation rather than an answer, which made a routing mistake look like a naming one; it is now `PairingDecided` carrying a `PairingDecisionReport` with a `PairingOutcome`, subscribed in `AgentHostedService`, which approves, denies or expires the attempt and writes the matching audit entry. The reach a pairing grants moved onto `PairingManager.DefaultScopes` so the desktop and the CLI cannot drift apart | `SessionAgentRuntime.ShowPairingPromptAsync`, `SessionAgentBridge.PairingDecided`, `AgentHostedService.OnPairingDecided`, `PairingManager.DefaultScopes` | Was: pairing approved from the PC desktop could never complete. The phone asked, the PC showed the prompt with the code, the human clicked Allow, the dialog closed, and the attempt stayed pending until it expired - so the phone reported "The pairing window closed before it was approved" and the log held four `POST /pair/request -> 201` and four runs of a one-second poll with nothing between them. Invisible to every test because every end-to-end run approved with `esirc pair approve`, which needs the service stopped and so cannot share a window with a live attempt, and the integration fixture drives `PairingManager` directly under a comment asserting that was "the same object the prompt's answer is routed to" - an assumption nothing checked. Found from a real phone against a real PC, after D33's clock was fixed and the symptom survived. Three integration tests now feed in the exact `pairing.result` message the session agent sends and assert the attempt state and the sealed credentials that follow; with the subscription removed two of them fail. Verified on the installed agent by clicking the real prompt: `pending`, click, then `approved` with device id, key id and an envelope, and `Approved ... from the desktop prompt` in the log |
| ~~D35~~ | **RESOLVED.** A phone could not obtain the certificate pin at all, which made the product unpairable by a human being. The app disables its `Pair` button until the pin field is non-empty - correctly, since the pin is what makes the connection worth trusting - and its help text says to read it "from the PC's pairing screen, or scan its QR code". The pin was on the pairing page as text; scanning it was impossible. Every QR the agent emitted was built by `PairingManager.BuildPollingUrl` from a live attempt, and that URL carries the address, the id and the one-time secret but never the pin - while an attempt only exists after a phone has already asked to pair. So the form's `Scan` button could fill the address and never the pin, `Pair` stayed disabled, and the only remaining route was reading 44 characters of base64 off a monitor and typing them on a soft keyboard where `l`, `I`, `1`, `0` and `O` are one glyph to a human and autocorrect is one keystroke away. The way in required the thing you could only get by being already inside. `PairingManager.BuildConnectUrl` now emits `esiremotepc://pair?host=..&port=..&deviceId=..&pin=..` with no attempt and therefore no secret, the idle pairing page renders it as a second QR, and a single scan fills address, port and pin. It discloses nothing an attacker on the LAN did not already have: the pin is in the unauthenticated `GET /handshake` by design, which is precisely why the app will not take it from there - what the QR adds is the out-of-band hop, since nothing on the network path can alter the pixels on the PC's screen. Because the page is normally opened *on the PC*, where the host is `localhost` and the QR would send the phone nowhere, it now says so and names the machine's routable addresses instead | `PairingManager.BuildConnectUrl`, `GET /pairing`, `GET /pairing/qr.svg?connect=1`, `IsLoopback`, `LanAddresses`, `PairingQr._fieldsFrom` | Was: a real phone could not be paired, and nothing said why - the `Pair` button was simply inert. Every other defect in this list broke something; this one removed the only way in, and it was invisible to every test because the emulator harness approves with `esirc pair approve` and pastes the secret rather than typing a pin. Verified at every link by one literal both sides share: `BuildConnectUrl` emits it, `QrEncoderTests` round-trips it through the encoder's independent decoder to prove it survives encoding, and a new `pairing_qr_test.dart` feeds the same string to `PairingQr.parse` and reads address, port, device id and pin back. Writing that test found a smaller bug beside it: `PairingQr`'s own doc comment promised a second accepted shape, `esiremotepc://pair/192.168.1.42?..`, which puts the address in the **path** while the parser read `uri.host`, so the documented shape silently did not work. The APK is unchanged by this - it is the agent that was missing the QR - so a phone that already has 1.0.0 can scan it |

### Protocol drift

`PROTOCOL.md` describes routes and behaviour the agent does not have. Full table in
[`API.md`](API.md) §15. The two that change what a client can do:

| Documented | Reality |
|---|---|
| `GET /api/v1/system/storage` | Does not exist. Volume data is in `/system/info` and `/system/metrics` |
| `POST /api/v1/auth/activate` | Does not exist. The phone starts signing immediately after `pair/session` returns `approved` |

---

## 3. Deliberately not done

Each of these is a decision, not an omission. The reason is given so it can be revisited
with the trade-off in mind rather than rediscovered.

### Security

| Not done | Why |
|---|---|
| **No MFA on pairing** | A second factor has to come from somewhere. The obvious answer — a PIN on the phone — defends against a stolen phone, which is already what the keystore and the lock screen defend against. A better answer is a confirmation on a *second* already-trusted device, which is a real protocol change. Doing neither is more honest than shipping a checkbox |
| **No client certificates / mutual TLS** | The phone authenticates with a signed request on every call. A client certificate adds ceremony without adding a property the signature does not already provide. `ClientCertificateMode.NoCertificate` is a considered choice |
| **No certificate revocation list** | The only credential is the SPKI pin, held by the paired device. `CertificateAuthority.Reset()` is the revocation mechanism: delete the identity, invalidate every pairing. A CRL would need a distribution channel a LAN product does not have |
| **No per-action confirmation on the PC** | Approving an action is more secure and unusable for a remote-control product. The control boundary is the pairing decision, not the click |
| **No read-only pairing mode exposed** | `PairingManager.Approve` takes a `DeviceScope` argument, so the capability exists. Only the CLI calls it, and it hardcodes `Read \| Control \| Files`. Surfacing the choice means deciding who chooses, and the current answer — the human at the PC during pairing — is right |
| **No encryption of the audit log at rest** | The log contains device ids, addresses and action descriptions, not secrets. DPAPI would make it unreadable to the local operator, which is the person most likely to need it |
| **No rate limiting on `LocalNetworkGuard` failures** | Rejections happen before authentication and are already the cheapest possible path: one dictionary lookup, no I/O. The per-address bucket that follows authentication covers the expensive case |

### Platform

| Not done | Why |
|---|---|
| **No DXGI Desktop Duplication capture** | GDI `BitBlt` works on every machine including RDP sessions and VMs, and `IScreenCaptureProvider` already abstracts it. DXGI is a real performance win on a high-refresh display and a real exclusion on some VMs. The seam is in place; the provider is not |
| **No multi-monitor spanning as one canvas** | Each display has its own `displayId` and its own geometry. A spanning canvas is a coordinate-translation problem that belongs with the RDP path, not before it |
| **No clipboard sync** | Self-contained, and a well-known remote-support attack surface. It would need its own design, not a slot in the existing frame format |
| **No audio redirection** | Self-contained, and a large amount of work for a feature the protocol does not currently have a frame type for |
| **No drive redirection** | Already covered by the file manager, inside the sandbox |
| **No cursor shape or position updates** | The client draws its own cursor from the normalised input events. A separate cursor frame is needed for RDP-class fidelity |
| **No tile resend** | `ScreenFrameHeader.ResyncRequest` exists and nothing emits or consumes it. Until there is a real loss detector there is nothing to resend on request of. See "next" below |
| **No true presented-FPS measurement** | Windows exposes no supported API for it. Reading the display refresh rate is a lower bound, not the same number. The endpoint returns `null` with a note rather than dressing the refresh rate up as an FPS reading |
| **No process suspend/resume in gaming mode** | Windows has no documented process-freeze API. `GamingService` terminates the configured apps and says in the response that they are not restarted. Inventing a "suspend" that is really a kill would be worse than the honest behaviour |
| **No `rundll32 powrprof.dll,SetSuspendState`** | It suspends session 0, not the interactive session, and hibernates on machines without S3. `PowerService` does not use it |
| **The service does not open a window station or desktop** | The Win32 documentation is explicit that doing so exposes a service to attack from the logged-on user. The service stays headless |

### Product

| Not done | Why |
|---|---|
| **No `remoteAccess` feature** | The handshake advertises `"remoteAccess": false` and always has. Reaching the PC from outside the LAN needs a relay, and a relay is a different product with a different threat model — an internet-facing node holding a master secret. Advertising it as `false` is the honest thing to do until it exists |
| **No cloud relay** | See above. It would be the single largest change to the security model in this document |
| **No file manager UI in the app** | The agent serves `/files/*` fully and the app has no `state/files.dart`. The endpoints were built to a documented contract; the UI was not started. The API is ready |
| **No `gaming.watch` streaming** | The frame type exists in the hub's vocabulary and no code path emits it. A pushed gaming-state channel is a small change once there is a reason for it |
| **No in-app certificate viewer** | `/pairing` is the desktop equivalent and is deliberately minimal |

---

## 4. Next

Ordered by the damage of leaving them undone.

### Immediate — correctness and access control

1. ~~**Ship a pairing QR emitter.**~~ Done. `QrEncoder` renders it, `/pairing/qr.svg` serves
   it, the desktop prompt shows it, and `esirc pair url` covers the headless case. Closes
   **D1**, which was the item that stopped a user exercising anything else in the product.
2. **Fix the scope table for process termination.** Add `/api/v1/system/process` to the
   `control` branch. Closes **D2**. One line, and add an integration test alongside it —
   `AgentIntegrationTests` already has `The_files_scope_is_required_for_file_routes` and
   `A_device_with_no_admin_scope_cannot_read_the_audit_log` to copy.
3. **Add a per-frame scope check to `DispatchAsync`.** A second prefix table for WebSocket
   frame types: `remote.*` → `control`, `gaming.mode` if it is ever added → `control`,
   `subscribe` → the scope each channel requires. Closes **D3**.
4. **Make the rotation grace period real.** Retain the previous `masterSecret` alongside
   the current one — a `previous_master_secret` column, DPAPI-protected the same way — and
   select on `keyId` rather than always using `device.MasterSecret`. Closes **D4**, and makes
   `PROTOCOL.md` §5.3 true. Rotation becomes safe to trigger without a synchronisation
   window, which is what makes it usable as a compromise-recovery path.
5. **Give key rotation a delivery path.** Either a tray-menu command that shows the
   `wrappedSecrets` envelope for out-of-band transfer, or a recovery code the phone can
   present. Today `POST /devices/{deviceId}/rotate` returns an envelope that only the device
   owner can unseal, and there is no way to get it to them. This is what makes **D4**
   matter in practice.

### Short term

6. **Fix the pairing rate limit.** Either implement a genuine hourly bucket — a separate
   refill calculation, not the shared per-minute one — or rename the field and correct the
   comment and `PROTOCOL.md` §3. Closes **D5**. The honest answer is probably to keep the
   current behaviour and correct the documentation, because an hour-long lockout after five
   failed attempts is a denial-of-service vector on a shared LAN.
7. **Persist the nonce cache**, or state D6 in `SECURITY.md` as an accepted risk. Persisting
   costs a write per authenticated request; a middle option is a small ring buffer
   serialised periodically. Closing this removes a replay window that a restart reopens.
8. **Reconcile `PROTOCOL.md` with `Endpoints.cs`.** Add the two missing routes to the
   specification or remove them from it; correct the SAN name, the certificate-regeneration
   claim, the `/` vs `/pairing` path, the `files/roots` scope, the `sort=cpu` parameter, the
   binary magic's byte order, and the WebSocket keepalive description. A spec that is
   authoritative and wrong is worse than one that is merely incomplete.
9. **Populate or delete the dead DTO fields.** `PairStatusResponse.spkiSha256`,
   `GamingStateResponse.sessionAgentConnected`, `PowerStatusResponse.pendingPowerAction`,
   `AppEntryDto.iconPath`, `EsiCrypto.DeriveAuditKey`, `AuditLog.ActionAuthSucceeded`,
   `AgentOptions.SessionTokenPath`. Closes **D8**, **D9**, **D10**, **D14**, **D15**,
   **D17**, **D18**. Each is a place a client author will build an assumption on.
10. **Correct the `UpdateAsync` warnings.** Either honour the three settings, or stop
    warning about values that are then overwritten and document that the only supported way
    to change them is `settings.json` or `--http true`. Closes **D13**.
11. **Reissue the TLS certificate when the address set changes.** The 5-minute housekeeping
    pass already rebuilds the handshake and updates mDNS. Comparing the current IPv4 set
    against the certificate's `IP:` SANs, and reissuing when they differ, closes the
    "certificate SANs do not match the current address" failure in
    [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md). Note this changes the pin only if the key
    changes — a reissue with the same key and a new serial keeps the SPKI pin valid, which
    is exactly why the specification pins the SubjectPublicKeyInfo.

### Medium term

12. **An idle-socket reaper.** Call `IsAlive`, or wire the 45 s threshold into the pump.
    Closes **D11**. Ten lines.
13. **A `frameType == 3` tile-resend path.** A per-tile sequence or a periodic full-grid
    keyframe so a client can detect loss. `ScreenFrameHeader.ResyncRequest` and
    `TileDescriptor` are already defined. Until this exists, a dropped tile stays dropped
    until the cell changes — which on a static screen is never.
14. **A SQLite migration runner.** `schema_version` is already in the schema. Closes
    **D16**. Every item above that adds a column needs it.
15. **A file manager screen in the app.** The API is complete and sandboxed; `state/files.dart`
    is the missing half. The sandbox's failure messages are specific enough to surface
    verbatim to a user.
16. **DXGI Desktop Duplication as an alternative capture provider.** Implement
    `IScreenCaptureProvider` against the Desktop Duplication API and return the same
    `CapturedFrame`. Nothing above the abstraction changes. The provider registry is the
    only new decision — which one to prefer, and how to fall back when the DXGI path is
    unavailable on a VM.
17. **Push gaming state over the WebSocket.** `BroadcastEventAsync` exists;
    `gaming.watch` does nothing. Subscribe, then push on the metrics tick. Small, and the
    app already has the metrics subscription.

### Larger, and deliberately deferred

| Item | What it would require | Why it is not next |
|---|---|---|
| **RDP-class graphics** | An H.264 or RemoteFX-class codec, an RDP graphics pipeline, and the multi-monitor spanning and cursor work that comes with it. `Codecs.RdpGraphicsPipeline = 2` is already reserved in `Codecs` | The delta-stream design in `PROTOCOL.md` §8 exists so this is an additive transport change rather than a rewrite. Rushing it means abandoning the tile grid that makes the current version cheap on Wi-Fi |
| **A relay / internet access** | A relay node, an authentication scheme for it, and a completely new section in [`SECURITY.md`](SECURITY.md) §11 about a node holding master secrets on a public network | The handshake already advertises `remoteAccess: false`. Enabling it before the threat model exists would be the worst possible order |
| **Multi-factor authentication** | A second factor, and a decision about who holds it | See "not done". The right answer — a second paired device confirming — is a protocol change, not a feature flag |
| **Scheduled actions** | A durable job store, a clock, and a conflict story with power actions that are already immediate | A nice-to-have that expands the blast radius of the `control` scope |
| **Clipboard and audio** | Separate modules with separate threat models | See "not done" |

---

## 5. Things that would be regressions

Listed so a future change does not reintroduce them.

| Invariant | Where it lives | Why |
|---|---|---|
| `LocalNetworkMiddleware` runs **before** authentication | `AgentHost.ConfigurePipeline` | An off-LAN packet must never reach the device table. Reordering costs a database read per rejected packet and leaks a timing signal |
| `UseRouting` runs **before** `SignatureAuthenticationMiddleware` | same | So a missing route is `404` and not a misleading `401`. A client cannot tell a version mismatch from a rejected signature otherwise |
| The body is buffered before the handler, via `EnableBuffering`, and the stream is rewound | `RequestContextMiddleware.BufferBodyAsync` | The signature must cover the exact bytes received, and Minimal API's JSON binder reads through a pipe reader captured over the original stream. Swapping `HttpRequest.Body` for a `MemoryStream` compiles and silently produces a body-less `400` on every body-bound route |
| `MaxRequestBodySize` is `files.maxUploadBytes`, not a literal | `KestrelTuning.Configure` | A literal 8 MiB would reject a legitimate large upload that the ticket path is designed to allow |
| The nonce cache is keyed per device | `RequestVerifier` | A shared cache would let one device's request invalidate another's nonce |
| Root comparison is `OrdinalIgnoreCase` | `FileSandbox.FindOwningRoot` | A case-sensitive comparison is a bypass on a case-insensitive volume |
| Sandbox denial is `403`, policy refusal is `200 {ok:false}` | `Endpoints.Guard`, `FileService` | An established split. Changing it breaks clients that branch on status |
| `AgentOptions.SessionAgentPath` and the pipe DACL are re-derived, not cached across sessions | `SessionAgentBridge` | Fast user switching changes the active user, and therefore the DACL's third ACE |
| The three executables are published from one build | `build.ps1` | They bake in the same protocol constants. A service and a client that disagree on a wire constant fail at runtime, not at build time |
| A session close releases the mouse buttons it was holding as well as its keys | `CapturePump.InputPump.ReleaseHeldKeys` | A press without its release turns the desktop into a held drag that no phone can undo |
| `esirc install` and `uninstall` manage the helper through the logon task, the same path the installer uses | `ServiceManager.Install`, `ServiceManager.Uninstall` | An install that registers a shape the SCM rejects, or an uninstall that leaves the helper relaunching at every sign-in, is a machine nobody can repair from the CLI |
| `RuntimeIdentifiers` is not set in `Directory.Build.props` | `Directory.Build.props` | It multiplies restore time by the RID count for no benefit in normal development |
| Serial and scope checks are constant-time | `EsiCrypto.SignatureEquals` | Not a micro-optimisation concern; it is the difference between "a wrong signature fails" and "a wrong signature fails slowly" |
| The rate limiter refills continuously rather than in fixed windows | `RateLimiter.TryConsume` | A fixed window gives a client 2× the budget across the boundary |
| `AuditLog.Record` swallows exceptions | `AuditLog.Record` | An audit failure must not take down the operation being audited. This is a real trade, and it is stated in [`SECURITY.md`](SECURITY.md) §10 |

---

## 6. Related

| | |
|---|---|
| Wire protocol | [`PROTOCOL.md`](PROTOCOL.md) |
| Security model and limitations | [`SECURITY.md`](SECURITY.md) |
| HTTP and WebSocket surface | [`API.md`](API.md) |
| Architecture and data flows | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Building and testing | [`BUILD.md`](BUILD.md) |
| Installation | [`INSTALL.md`](INSTALL.md) |
| Troubleshooting | [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) |
| Wake-on-LAN | [`WOL_SETUP.md`](WOL_SETUP.md) |
| Mobile app | [`../android-app/esi_remote/README.md`](../android-app/esi_remote/README.md) |

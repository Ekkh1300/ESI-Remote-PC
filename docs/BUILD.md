# ESI Remote PC — Building the Windows Agent

Everything needed to build, test, run and package the Windows side. The mobile app has its
own toolchain; see [`../android-app/esi_remote/README.md`](../android-app/esi_remote/README.md).

---

## 1. Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | **10.0** | `net10.0-windows`. Any 10.0.x SDK works; the build script probes for one |
| PowerShell | 5.1 or PowerShell 7+ | `build.ps1` uses `pwsh`; the smoke test is launched with `powershell` for compatibility |
| Windows | 10 (1809) or later | The installer sets `MinVersion=10.0` |
| Inno Setup | **6.x** | Only needed to build the `.exe` installer |
| Architecture | x64 | `ArchitecturesAllowed=x64compatible`; all three binaries publish `win-x64` |

```powershell
dotnet --list-sdks
```

The build script deliberately does not trust whatever `dotnet` is on `PATH`. A Store alias
or a leftover shim reports itself successfully and then fails every subcommand, so
`build.ps1` probes each candidate with `dotnet --list-sdks` and uses the first one that
answers:

1. the `dotnet` found on `PATH`
2. `C:\dotnet\dotnet.exe`
3. `%ProgramFiles%\dotnet\dotnet.exe`
4. `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`

If none of them has an SDK installed, the script throws with the list it tried.

---

## 2. The build entry point

```powershell
pwsh -File build.ps1
```

`build.ps1` is the only supported build path. The three shipped executables — the service,
the session agent and the CLI — bake in the same protocol constants, and a service and a
client that disagree on a wire constant fail at runtime rather than at build time. One
script, one build.

### Flags

| Flag | Default | Effect |
|---|---|---|
| `-Configuration Debug`\|`Release` | `Release` | Passed to `dotnet build`/`test`/`publish` and to the smoke test |
| `-SkipTests` | off | Skips the xUnit run and the smoke test |
| `-SkipPublish` | off | Builds and tests but does not write `artifacts\publish` |

### What it does, in order

1. Resolves a usable `dotnet`.
2. `dotnet restore ESI.RemotePc.Agent.slnx`
3. `dotnet build -c <Configuration> --no-restore`
4. `dotnet test tests\ESI.RemotePc.Tests --no-build`
5. `powershell -File tests\smoke-test.ps1 -Configuration <Configuration>`
6. Clears `artifacts\publish` and publishes the three shipping projects into it.
7. Prints a size table and the exact `iscc` command to build the installer.

Step 5 runs *before* publishing, and it exercises the build output at
`src\ESI.RemotePc.Agent.Service\bin\<Configuration>\net10.0-windows\win-x64\`. That path
exists because `build` produces a single-file executable for the agent's own RID. If it is
missing the smoke test fails immediately with a clear message rather than timing out.

### Output

```
artifacts\publish\ESI.RemotePc.Agent.Service.exe
artifacts\publish\ESI.RemotePc.Session.exe
artifacts\publish\ESI.RemotePc.Agent.Cli.exe
```

All three are self-contained single files (`SelfContained`, `PublishSingleFile`,
`IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile`, embedded debug
symbols). There is no dependency tree to deploy, which is the point: the agent has to run
on a machine with no .NET runtime and must survive the machine's shared runtime being
upgraded or removed.

---

## 3. Building the installer

Requires Inno Setup 6 (`iscc.exe` on `PATH`, or installed from
<https://jrsoftware.org/isdl.php>).

```powershell
pwsh -File build.ps1
iscc /DAppVersion=1.0.0 installer\ESI-RemotePC.iss
```

The script reads `..\artifacts\publish\*.exe`, so `build.ps1` must run first unless the
publish directory is already populated. `OutputDir` is `..\artifacts` and the output name is
`ESI-RemotePC-Setup-<version>-x64.exe`.

`iscc.exe` is usually not on `PATH`; `build.ps1` looks for it in Inno's default install
locations as well (Setup 5, 6 and 7). `build.ps1 -WithInstallCycle` compiles the installer
itself before testing it, so that path needs no manual `iscc` step.

What the installer does, in order:

| Step | Action |
|---|---|
| 1 | Copies the three executables to `{autopf}\ESI Remote PC` |
| 2 | Creates `{commonappdata}\ESI-RemotePC` with `users-modify` permissions |
| 3 | Registers `ESIRemotePcAgent` — `start= auto`, `obj= LocalSystem` |
| 4 | Sets its description and failure recovery (`restart/5000`, `restart/15000`, `restart/60000`, `reset= 86400`) |
| 5 | Writes `requiredPrivileges = SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege` to the agent's registry key |
| 6 | Registers a **logon-triggered scheduled task** for the session agent, rather than a second Windows service |
| 7 | Deletes the same task again on uninstall |
| 8 | Adds two Windows Firewall rules, **private and domain profiles only** |
| 9 | Starts `ESIRemotePcAgent` |
| 10 | Runs `esirc status` |

`requiredPrivileges` is undocumented-but-load-bearing. Without it the service account holds
`SeTcbPrivilege` in a *disabled* state and the first `CreateProcessAsUserW` into the
console session fails, so the session agent can only be started by the logon path.

Step 6 was a Windows service once, created with an empty `obj=` to make it per-user. The SCM
rejects that form, and it rejects it *silently* from the installer's point of view: `sc.exe`
prints to stderr, Inno carries on with an entry that failed, and the result is a
"successful" install with no session agent and no error. A logon-triggered scheduled task is
the documented mechanism for running in each interactive session, needs no stored
credentials, and does not fail quietly. The installer's post-install step queries both the
SCM and Task Scheduler and logs what is missing, so a partial install cannot look like a
working one.

Uninstall stops and deletes both services, removes both firewall rules, and deletes
`{commonappdata}\ESI-RemotePC` in full — certificate, key, device database and logs. There
is no "keep my data" option in the installer; `esirc uninstall` without `--purge` does keep
it.

The installer requires elevation (`PrivilegesRequired=admin`). It does not create a desktop
shortcut by default; that is an unchecked task on the Tasks page.

---

## 4. Tests

### Unit and integration tests

```powershell
dotnet test tests\ESI.RemotePc.Tests\ESI.RemotePc.Tests.csproj -c Release
```

Or through the build script, which is what CI should use:

```powershell
pwsh -File build.ps1
```

| File | Covers |
|---|---|
| `EsiCryptoTests.cs` | HKDF against RFC 5869 test cases 1 and 3, canonical string against a golden vector, canonical target ordering, signature and content-hash comparison, AES-GCM seal/open, base64url round trips, device-id and nonce generation |
| `FileSandboxTests.cs` | Parent traversal, device and extended-length paths, UNC, alternate data streams, reserved DOS names, 8.3 aliases, junctions and symlinks pointing outside a root, read-only roots, root-deletion rules |
| `AgentIntegrationTests.cs` | Boots the real host: health and handshake without a signature, a signed metrics request, replayed nonces, stale timestamps, wrong key ids, tampered paths and bodies, scope enforcement, the 404 version-mismatch hint, and that every error body is a well-formed envelope |

`AgentIntegrationTests` exercises the middleware pipeline end to end, including the
`LocalNetworkMiddleware` → `UseRouting` → `SignatureAuthenticationMiddleware` ordering, so
a change to the pipeline order fails the tests rather than production.

### Smoke test

```powershell
pwsh -File tests\smoke-test.ps1
```

| Flag | Default | Effect |
|---|---|---|
| `-Port <n>` | `48999` | The port to boot on |
| `-Configuration Debug`\|`Release` | `Release` | Which build output to run |
| `-KeepRunning` | off | Leaves the agent up and prints its pid and data directory |
| `-SkipDiscovery` | off | Skips the UDP broadcast probe (useful where a firewall blocks broadcast) |

The smoke test creates a throwaway data directory under `%TEMP%`, starts
`ESI.RemotePc.Agent.Service.exe` with `--port` and `--data-dir`, waits up to 45 s for
`/api/v1/health`, then probes:

* the public surface — `GET /api/v1/handshake`, `GET /api/v1/health`, `GET /` (expects
  `302`), `GET /pairing`;
* pairing — `POST /api/v1/pair/request` returns `201` with a `pairingId`, and
  `GET /api/v1/pair/session?id=…` reports `pending`;
* authentication — unsigned `GET /system/metrics`, `GET /system/info` and
  `POST /power/shutdown` all return `401` with an `esi_` error code;
* the error envelope — malformed JSON returns `400`, a missing `Content-Type` returns
  `415`, and an unknown route returns `404`, all with a body;
* UDP broadcast discovery on port `48211`.

All probes use `curl -k`, because the certificate is self-signed and the point of the test
is the application surface, not the trust chain.

Unless `-KeepRunning` is passed, the agent is stopped and the data directory removed in a
`finally` block.

The smoke test runs as part of `build.ps1` unless `-SkipTests` is given. Running it on its
own requires that `dotnet build` has already produced the single-file output; otherwise it
throws `The agent has not been built`.

---

## 5. Running the agent in console mode

Running the executable directly instead of as a service. `AgentHost.Main` detects this with
`WindowsServiceHelpers.IsWindowsService()`, so the same binary does both.

```powershell
.\artifacts\publish\ESI.RemotePc.Agent.Service.exe
```

| Flag | Argument | Effect |
|---|---|---|
| `--debug` | — | `DebugMode = true`; Debug-level logging and the `debug.enabled` / `debug.verboseLogging` settings are set |
| `--port <n>` | integer | Overrides `settings.port`. Default `48220` |
| `--http <true\|false>` | boolean | `--http true` sets `security.tlsEnabled = false` and serves plain HTTP. Signed requests are still required; only confidentiality is lost |
| `--data-dir <path>` | path | Overrides the `%ProgramData%\ESI-RemotePC` location |
| `--session-agent <path>` | path | Path to `ESI.RemotePc.Session.exe`, used when the service has to launch it |
| `--help`, `-h` | — | Prints the usage text and exits |

Configuration is layered: `appsettings` → environment variables prefixed `ESI_` → the
command line above. The command line is applied last, in `ApplyCommandLineOverrides`.

### What console mode changes

* **The start-up banner is printed** to the console: device id, data directory, log
  directory, HTTPS port and TLS state, discovery ports, TLS subject, SPKI pin, and the
  active file roots.
* **`GET /` is mapped** and redirects to `/pairing`. As a service it is whitelisted as
  public and then 404s, because the redirect is only registered in
  `RunInteractiveAsync`.
* **TLS key storage switches to the user key store.** `CertificateAuthority` detects
  `S-1-5-18`; without it the private key goes in the user key store and DPAPI is called
  with `CurrentUser` scope instead of `LocalMachine`.

### Useful invocations

```powershell
# Throwaway instance on a spare port, isolated data, verbose
.\artifacts\publish\ESI.RemotePc.Agent.Service.exe `
    --port 48999 --data-dir "$env:TEMP\esi-dev" --debug

# Plaintext, for a local client that has no pinning configured
.\artifacts\publish\ESI.RemotePc.Agent.Service.exe --http true --data-dir "$env:TEMP\esi-dev"

# Check the pairing page from the same machine
Start-Process "https://localhost:48220/pairing"
```

Console mode does not install a service and does not start the session agent unless a user
session exists and `--session-agent` points at a real binary.

---

## 6. Verifying a build

In order of increasing strength.

**1. It compiled.** `build.ps1` prints a duration per step and throws on the first non-zero
exit code. A clean run with no red output is the baseline.

**2. The constants match.** The three executables must agree on the protocol. A change to
anything in `src\ESI.RemotePc.Protocol\ProtocolConstants.cs` is a breaking change; review
`git diff` on that file as a protocol review.

**3. The tests passed.** `build.ps1` runs both suites unless `-SkipTests`. The count is not
printed by the script, so run `dotnet test` directly if the number matters.

**4. The published binary actually serves.** Run the smoke test against a Release build:

```powershell
pwsh -File build.ps1
```

Expected tail:

```
{n} passed, 0 failed
```

**5. The certificate is what you think it is.** Start the agent and compare the printed pin
with the handshake, then confirm a *different* pin is produced by a different data
directory — this is the check that catches a data directory that is not the one you think
it is:

```powershell
$ProgressPreference = 'SilentlyContinue'
(Invoke-RestMethod -SkipCertificateCheck https://localhost:48220/api/v1/handshake).spkiSha256
```

`-SkipCertificateCheck` is PowerShell 7+. On Windows PowerShell 5.1 use:

```powershell
curl.exe -k https://localhost:48220/api/v1/handshake
```

**6. The installer round-trips.** This one is automated.
`installer\install-cycle-test.ps1` runs the real installer through five states — clean,
install, **install again over its own output**, uninstall, install again — and asserts 29
things about the result, asking the question a user would ask ("is it working?") by
connecting to the agent's port rather than by reading the SCM's opinion. It needs
administrator rights, so it is opt-in:

```powershell
powershell -File windows-agent\build.ps1 -SkipTests -SkipPublish -WithInstallCycle
```

The cycle step compiles the installer itself: it finds `iscc.exe` on `PATH` or in Inno's
default install locations (this machine has Inno Setup 7, which the repository predates)
and fails rather than testing a stale binary. There is no separate `iscc` line to forget —
see below for why that mattered.

It exists because the installer is the only artefact that no automated check ever ran, and
because every defect it has found was invisible everywhere else: a service that was
registered but could not be started, a logon task that could never fire, an upgrade that
failed on locked files, an uninstall that reported success while leaving 139 MB behind,
and a cycle that checked only that the installer *existed* — so it cheerfully reported 26
green checks against a `.exe` compiled the day before, from code that was not in the tree.
All five look identical to a user: something is running, and it is not what you just wrote.

It does not pass cleanly on a machine where somebody is signed in, or it has not always —
once, and only once, it timed out waiting for the service key to disappear and printed a
warning telling the user to reboot and run it again. The installer recovered anyway, and
neither a clean measurement nor a repeat of the cycle reproduces the delay: the service
process exits in under a second and the key goes with it. So the honest statement is that
the warning can appear and be wrong, not that the cycle is known to be slow. See
`ROADMAP.md` **D20** and [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) — *Installing or
reinstalling is slow*. What made it findable at all is that the installer logs what it saw
instead of hiding it.

By hand, on a throwaway VM: install, `esirc status`, open
`https://localhost:48220/pairing`, confirm the device id on the page matches the console
output of `esirc status`, then uninstall and confirm `{commonappdata}\ESI-RemotePC` is gone
and the service and logon task are both removed:

```powershell
sc.exe query ESIRemotePcAgent
schtasks.exe /query /tn "ESI Remote PC Session Agent"
```

`sc.exe query ESIRemotePcAgent` should return `ERROR_SERVICE_DOES_NOT_EXIST` (1060), the
`schtasks` query should report that it cannot find the file, and
`{autopf}\ESI Remote PC` should be gone entirely rather than left as an empty folder.

**7. The app and the agent actually talk to each other.** This is the check that is not a
unit test and not a smoke probe, and every serious defect in the pairing path was found by
it rather than by anything else. It starts a throwaway agent on a spare port with its own
data directory — so it needs no administrator rights and does not disturb an installed one —
and drives the real app code against it:

```powershell
cd android-app\esi_remote
dart run tool/live_agent_check.dart `
  --agent ..\..\windows-agent\artifacts\publish\ESI.RemotePc.Agent.Service.exe `
  --esirc ..\..\windows-agent\artifacts\publish\ESI.RemotePc.Agent.Cli.exe `
  --port 48333
```

Eleven assertions, and each one corresponds to a way the two halves can disagree:

| Step | What it proves |
|---|---|
| 1 | A pinned TLS handshake completes, **and a wrong pin is refused** — the second half is the one that matters, since a client that accepts anything is not doing pinning |
| 2 | The unsigned pre-auth surface answers, and the `spkiSha256` the handshake advertises equals `pinForCertificate` of the certificate actually served, so PROTOCOL.md A2.1 and the agent agree |
| 3 | `esirc pair approve` works, from the real CLI, not a stub |
| 4 | An approved attempt releases a wrapped envelope |
| 5 | **Dart opens an envelope C# produced** — AES-GCM, HKDF and the key generation, across a real key |
| 6 | **A request signed by Dart is accepted by the agent**, and a replayed nonce is refused with `esi_replay` |

Two of the three worst bugs in this project were invisible to every other check because both
sides were individually correct and the wiring between them was not: the app signed the
un-prefixed path while sending the prefixed one, so no signed request could ever verify; and
the agent bound the `--port` it was given while advertising the default, so a discovered
agent pointed the phone at a port nothing was listening on. Neither shows up in a unit test,
because each half is right on its own.

If you extend the protocol, extend this file. It is the only thing in the repository that
notices when the two implementations stop agreeing.

---

## 7. Repository layout

```
windows-agent\
  build.ps1                      One entry point: restore, build, test, smoke, publish
  Directory.Build.props          Shared properties: net10.0-windows, nullable warnings as errors
  Directory.Packages.props       Central package versions
  ESI.RemotePc.Agent.slnx        Solution (7 projects)
  installer\ESI-RemotePC.iss     Inno Setup script
  tests\
    ESI.RemotePc.Tests\          xUnit: crypto, sandbox, HTTP integration
    smoke-test.ps1               Boots the built binary and probes it
  src\
    ESI.RemotePc.Protocol\       Wire constants, crypto, DTOs. No dependencies
    ESI.RemotePc.Platform\       Win32 / WMI / SQLite platform code
    ESI.RemotePc.Agent.Core\     Storage, security, settings, notifications
    ESI.RemotePc.Agent.Service\  Kestrel host, routes, WebSocket hub
    ESI.RemotePc.Session\        Per-user session agent
    ESI.RemotePc.Agent.Cli\      esirc
  artifacts\publish\             Build output (not source)
```

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for why the projects are split this way.

### Dependency versions

Managed centrally in `Directory.Packages.props`:

| Package | Version |
|---|---|
| `Microsoft.Extensions.*` (hosting, config, DI, options, logging) | 10.0.12 |
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.12 |
| `System.ServiceProcess.ServiceController` | 10.0.12 |
| `Microsoft.Data.Sqlite` | 10.0.12 |
| `System.Drawing.Common` | 10.0.12 |
| `System.Management` | 10.0.12 |
| `System.Security.Cryptography.ProtectedData` | 10.0.12 |
| `Serilog.AspNetCore` | 10.0.0 |
| `Serilog.Sinks.File` | 7.0.0 |
| `Serilog.Sinks.Console` | 6.1.1 |
| `Serilog.Extensions.Logging` | 10.0.0 |
| `Microsoft.NET.Test.Sdk` | 18.10.1 |
| `xunit` | 2.9.3 |
| `xunit.runner.visualstudio` | 4.0.0 |

`CentralPackageTransitivePinningEnabled` is on, so transitive dependencies are pinned to
these versions too.

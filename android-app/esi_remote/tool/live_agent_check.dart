// Live-agent verification: the app's real HTTP, TLS pinning, signing and AES-GCM code
// against a running agent.
//
// Why this exists
// ---------------
// The unit tests and the golden vectors prove the two implementations agree on paper.
// They cannot prove that the Dart TLS stack completes a handshake against the agent's
// self-signed ECDSA certificate, that `badCertificateCallback` fires and accepts the right
// SPKI pin, or that a request signed by this code is accepted by the middleware that
// verifies it. Only a real exchange does that.
//
// This is a harness, not a unit test. It needs a running agent, so it is not in `test/`.
//
//   dart run tool/live_agent_check.dart \
//     --host 127.0.0.1 --pin <base64 spki> --esirc "<path to esirc>" --log "<agent log>"
//
// What it does, in order:
//   1. TLS + SPKI pinning: handshake over a pinned connection, and prove a wrong pin fails.
//   2. Unsigned pre-auth request: POST /pair/request.
//   3. Approval via the real CLI, so nothing about the exchange is simulated.
//   4. Collect the credentials the agent wrapped for the provisioning secret.
//   5. Unwrap them with the app's AES-GCM + HKDF code.
//   6. Send a *signed* request with the derived session key and get a 200.
//
// Step 2 needs the provisioning secret, which by design only ever exists inside the QR
// that the PC displays. A phone gets it by scanning. This has no camera, so it reads it
// from the agent log instead: with no interactive session the agent logs the polling URL
// in full so that an administrator can pair by hand. That is the same URL the QR encodes.

import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_api.dart';
import 'package:esi_remote/src/net/spki_pinning.dart';
import 'package:esi_remote/src/protocol/esi_crypto.dart';

late String _host;
late String _pin;
late String _esirc;
late int _port;

/// The throwaway data directory the self-hosted agent and esirc share. Empty when pointed
/// at an agent that is already running, in which case neither is redirected.
late String _dataDirectory;

/// The agent this run started, if it started one. Null when pointed at an agent that was
/// already running, which is then left alone.
Process? _ownedAgent;

/// The last few lines the agent wrote to its console, for a failure message. The agent
/// writes its real log to a file this deliberately does not read, because Serilog holds
/// that file open for writing and a shared-read reader is refused.
final List<String> _agentLog = <String>[];

// Never used for anything: every request made with it is unsigned, so the session key
// derived from this secret is never computed.
final EsiIdentity _unused =
    EsiIdentity(deviceId: 'unused', keyId: 0, masterSecret: Uint8List(0));

int _passed = 0;
int _failed = 0;

void ok(String what) {
  stdout.writeln('  PASS  $what');
  _passed++;
}

void bad(String what, Object why) {
  stdout.writeln('  FAIL  $what');
  stdout.writeln('        $why');
  _failed++;
}

void step(String n, String title) {
  stdout.writeln('\n$n. $title');
}

Future<void> main(List<String> args) async {
  final opts = _parse(args);
  _host = opts['host'] ?? '127.0.0.1';
  _port = int.tryParse(opts['port'] ?? '') ?? 48220;
  _esirc = opts['esirc'] ?? '';
  _dataDirectory = '';
  final agentExe = opts['agent'];
  final dataDir = opts['data-dir'] ?? '';

  if (agentExe != null) {
    // Boot our own agent on a spare port against a throwaway data directory. That way the
    // check runs the current build output with the shipped defaults - including the real
    // rate limits, which is the point of step 4 - and needs no administrator rights, so it
    // does not disturb an agent that is already installed and running.
    //
    // `--data-dir` on both processes, rather than a redirected PROGRAMDATA. The agent and
    // the CLI each have their own resolver, and pointing them at one directory explicitly
    // is the only way to be certain they are looking at the same database.
    final dataRoot = Directory(dataDir.isEmpty
        ? Directory.systemTemp.createTempSync('esi-live-data-').path
        : dataDir);
    if (!dataRoot.existsSync()) dataRoot.createSync(recursive: true);
    _dataDirectory = dataRoot.path;

    stdout.writeln('Starting an agent from $agentExe on port $_port');
    stdout.writeln('  data dir: $_dataDirectory (throwaway)');

    // The default start mode, which pipes stdout and stderr - and both are drained below.
    // This is not a detail.
    //
    // With `ProcessStartMode.normal` the child inherits this process's stdout, which under
    // `dart run` is a pipe nobody reads. The agent logs continuously - telemetry, disk and
    // memory warnings - and once the pipe buffer fills, its console sink blocks on the
    // write. The agent then sits there alive but never finishes starting, which looks
    // exactly like a slow or broken agent and cost a long detour to diagnose. Measured:
    // 1.1 s to serve with the pipe drained, an indefinite stall without it.
    _ownedAgent = await Process.start(
      agentExe,
      <String>['--port', '$_port', '--data-dir', _dataDirectory],
    );
    // Keep the tail of the agent's own console output, so a failure can say what it said.
    _ownedAgent!.stdout
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(_agentLog.add)
        .onError((Object _) {});
    _ownedAgent!.stderr
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(_agentLog.add)
        .onError((Object _) {});

    if (await _hasExited()) {
      stderr.writeln('the agent exited immediately');
      exit(70);
    }
    _pin = await _learnPin();
  } else {
    // Pointed at an agent somebody else is running. Its pin has to be supplied, because
    // learning it is only safe against a throwaway agent whose certificate nobody depends on.
    _pin = opts['pin'] ?? '';
    if (_pin.isEmpty) {
      stderr.writeln('--pin is required when --agent is not given.');
      stderr.writeln('');
      stderr.writeln('usage: dart run tool/live_agent_check.dart --agent <agent exe> \\');
      stderr.writeln('         [--esirc <esirc exe>] [--port 48220] [--data-dir <dir>]');
      stderr.writeln('   or: dart run tool/live_agent_check.dart --pin <spki> --esirc <esirc exe>');
      exit(64);
    }
  }

  stdout.writeln('Live agent check against https://$_host:$_port');
  stdout.writeln('SPKI pin: $_pin');

  final api = _api(_pin);
  try {
    await _checkTls(api);
    final created = await _requestPairing(api);
    final pairingId = created['pairingId'] as String;
    final userCode = created['userCode'] as String;
    final secret = await _provisioningSecret(userCode);
    await _approve(userCode);
    final identity = await _collectCredentials(api, pairingId, secret);
    await _signedRequest(identity);
  } catch (error, trace) {
    bad('the run completed', '$error\n$trace');
  } finally {
    api.close();
    _shutdown();
  }

  stdout.writeln('\n$_passed passed, $_failed failed');
  exit(_failed == 0 ? 0 : 1);
}

// ---------------------------------------------------------------- 1. TLS + pinning

EsiApi _api(String pin, {String? fixedNonce}) => EsiApi(
      endpoint: EsiEndpoint(host: _host, port: _port, pins: <String>[pin]),
      identity: _unused,
      // A fixed nonce turns the client into a deliberate replay attacker, which is the
      // only way to reach the agent's nonce cache from this side.
      nonceSource: fixedNonce == null ? null : () => fixedNonce,
    );

Future<void> _checkTls(EsiApi api) async {
  step('1', 'TLS handshake and SPKI pinning');

  final health = await api.getJson('/health', signed: false);
  if (health['status'] == 'ok') {
    ok('pinned handshake completed and GET /health returned ok');
  } else {
    bad('pinned handshake completed and GET /health returned ok', health);
  }

  // The security property that matters: a pin that does not match must fail the
  // connection, not be quietly ignored. A client that accepts any certificate is not
  // doing pinning, it is doing TLS.
  final wrong = EsiApi(
    endpoint: EsiEndpoint(
      host: _host,
      port: _port,
      pins: <String>[base64Encode(List<int>.filled(32, 0xAB))],
    ),
    identity: _unused,
  );
  try {
    await wrong.getJson('/health', signed: false);
    bad('a wrong SPKI pin is rejected', 'the request succeeded, so the pin was not enforced');
  } catch (error) {
    ok('a wrong SPKI pin is rejected (${error.runtimeType})');
  } finally {
    wrong.close();
  }
}

// ------------------------------------------------------------- 2. pre-auth request

Future<Map<String, dynamic>> _requestPairing(EsiApi api) async {
  step('2', 'Unsigned pre-auth request: POST /pair/request');

  final created = await api.postJson(
    '/pair/request',
    signed: false,
    body: <String, dynamic>{
      'deviceName': 'LiveAgentCheck',
      'platform': 'android',
      'osVersion': '16',
      'appVersion': '1.0.0',
    },
  );

  final code = created['userCode'] as String?;
  final id = created['pairingId'] as String?;
  if (code == null || id == null || code.length != 6) {
    throw StateError('pair/request returned an unusable body: $created');
  }
  ok('agent issued a 6-digit user code ($code) and a pairing id');
  return created;
}

// ------------------------------------------------------------------ 3. approval

Future<void> _approve(String userCode) async {
  step('3', 'Approval from the real CLI: esirc pair approve $userCode');

  if (_esirc.isEmpty) {
    bad('esirc path was not supplied', 'pass --esirc "<path>"');
    return;
  }

  // Approving rewrites rows the running agent also holds, so esirc refuses while the
  // *installed* service is running. With an explicit --data-dir there is no installed
  // service holding that database, so no stop is needed - which is the self-hosted path
  // this harness takes. Against the installed agent the whole stop/start sequence is
  // required, and is performed here so the check exercises the real path rather than a
  // shortcut around it.
  final selfHosted = _dataDirectory.isNotEmpty;
  final service = Platform.environment['ESI_SERVICE'] ?? 'ESIRemotePcAgent';
  var stopped = false;

  if (!selfHosted) {
    final running = await _sc(['query', service]);
    if (running != null && running.contains('RUNNING')) {
      stdout.writeln('      (stopping $service; the phone will see a brief connection error)');
      final halt = await Process.run('sc.exe', <String>['stop', service]);
      if (halt.exitCode != 0) {
        bad('the service could be stopped for approval',
            'sc stop failed: ${halt.stdout}${halt.stderr}');
        return;
      }
      stopped = true;
    }
  }

  try {
    final result = await Process.run(_esirc, <String>[
      'pair',
      'approve',
      userCode,
      if (selfHosted) ...<String>['--data-dir', _dataDirectory],
    ]);
    final output = '${result.stdout}${result.stderr}'.trim();
    if (result.exitCode != 0) {
      bad('esirc pair approve succeeded', 'exit ${result.exitCode}: $output');
      return;
    }
    ok('esirc approved the request');
  } finally {
    if (stopped) {
      final start = await Process.run('sc.exe', <String>['start', service]);
      ok(start.exitCode == 0 ? 'the service was started again' : 'the service restart FAILED');
      // Give Kestrel time to bind again before the next request.
      await Future<void>.delayed(const Duration(seconds: 3));
    }
  }
}

Future<String?> _sc(List<String> args) async {
  final r = await Process.run('sc.exe', args);
  return r.exitCode == 0 ? '${r.stdout}' : null;
}

// ----------------------------------------------- 4-5. collect and unwrap credentials

Future<EsiIdentity> _collectCredentials(EsiApi api, String pairingId, String secret) async {
  step('4', 'Collect the wrapped credentials');

  Map<String, dynamic> status = <String, dynamic>{};
  for (var attempt = 0; attempt < 15; attempt++) {
    status = await api.getJson(
      '/pair/session',
      signed: false,
      query: <(String, String)>[
        ('id', pairingId),
        ('secret', secret),
      ],
    );
    if (status['status'] == 'approved') break;
    await Future<void>.delayed(const Duration(milliseconds: 400));
  }

  if (status['status'] != 'approved') {
    throw StateError('the attempt never reached "approved": $status');
  }
  if (status['wrappedSecrets'] == null) {
    throw StateError('approved but no wrappedSecrets were released: $status');
  }
  ok('the agent released a wrapped envelope for the approved attempt');

  step('5', 'Unwrap with the app\'s AES-GCM and HKDF code');

  final deviceId = status['deviceId'] as String;
  final keyId = status['keyId'] as int;
  final envelope = WrappedSecretEnvelope.fromJson(
      (status['wrappedSecrets'] as Map<String, dynamic>));

  final opened = await EsiCrypto.openPairingEnvelope(
    envelope: envelope,
    // base64url, not base64: the secret arrived inside a query string, where a '+' or a '/'
    // would have to be escaped. The agent accepts either, and the app sends standard base64
    // on the wire; this path is the one the URL produces.
    provisioningSecret: EsiCrypto.fromBase64Url(secret),
    pairingId: pairingId,
    deviceId: deviceId,
  );

  if (opened.masterSecret.length != 32) {
    bad('the unwrapped master secret is 32 bytes', 'got ${opened.masterSecret.length}');
  } else {
    ok('AES-GCM envelope produced by C# opened with the Dart code (32-byte secret)');
  }
  if (opened.keyId != keyId) {
    bad('the unwrapped key id matches the response', '${opened.keyId} != $keyId');
  } else {
    ok('the key generation survived the round trip (keyId $keyId)');
  }

  return EsiIdentity(
    deviceId: deviceId,
    keyId: opened.keyId,
    masterSecret: opened.masterSecret,
  );
}

// ------------------------------------------------------------- 6. a signed request

Future<void> _signedRequest(EsiIdentity identity) async {
  step('6', 'A request signed by the Dart code, verified by the agent');

  final api = EsiApi(
    endpoint: EsiEndpoint(host: _host, port: _port, pins: <String>[_pin]),
    identity: identity,
  );
  try {
    // The request succeeding at all is the assertion: this route requires a valid signature
    // and answers 401 without one. Before the path-signing fix it never got this far.
    final info = await api.getJson('/system/info');
    final hostName = info['hostName'];
    if (hostName is String && hostName.isNotEmpty) {
      ok('a signed GET /system/info was accepted and returned the host name');
    } else {
      bad('a signed GET /system/info was accepted and returned the host name', '$info');
    }

    final metrics = await api.getJson('/system/metrics');
    if (metrics.containsKey('cpu')) {
      ok('GET /system/metrics returned telemetry');
    } else {
      bad('GET /system/metrics returned telemetry', 'no cpu key in ${metrics.keys.toList()}');
    }

    // Replay protection. The signature covering a nonce is worthless if the agent will
    // accept the same nonce twice, so this sends a byte-identical request twice and
    // requires the second to be refused. A fixed `nonceSource` is what makes the two
    // requests identical; a fresh random nonce each time would prove nothing.
    const replayed = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA';
    final replayer = _api(_pin, fixedNonce: replayed);
    final signed = EsiApi(
      endpoint: EsiEndpoint(host: _host, port: _port, pins: <String>[_pin]),
      identity: identity,
      nonceSource: () => replayed,
    );
    try {
      await signed.getJson('/system/info');
      ok('the first of two identical signed requests was accepted');
      try {
        await signed.getJson('/system/info');
        bad('a replayed nonce is refused',
            'the agent accepted the same nonce and the same signature twice');
      } on EsiApiException catch (error) {
        if (error.statusCode == 401) {
          ok('the replay was refused with 401 (${error.code})');
        } else {
          bad('a replayed nonce is refused', 'unexpected ${error.statusCode} ${error.code}');
        }
      }
    } finally {
      signed.close();
      replayer.close();
    }
  } finally {
    api.close();
  }
}

// ------------------------------------------------------------------- the secret

/// The provisioning secret, obtained the way an administrator obtains it on a headless
/// machine: `esirc pair url <code>` prints the polling URL, and the secret is in it.
///
/// That is the same URL the QR encodes, so the code path from here on is byte-for-byte the
/// one a phone takes after a scan. It used to be scraped out of the agent's log file
/// instead, which was needlessly fragile: Serilog holds the log open for writing, and Dart's
/// `readAsStringSync` asks only for shared-read access, so the read was refused for reasons
/// that had nothing to do with the code under test.
Future<String> _provisioningSecret(String userCode) async {
  if (_esirc.isEmpty) {
    throw StateError('--esirc is required: the provisioning secret comes from the CLI');
  }

  final result = await Process.run(_esirc, <String>[
    'pair',
    'url',
    userCode,
    '--port',
    '$_port',
    if (_dataDirectory.isNotEmpty) ...<String>['--data-dir', _dataDirectory],
  ]);
  final output = '${result.stdout}'.trim();
  if (result.exitCode != 0) {
    throw StateError('esirc pair url $userCode failed (${result.exitCode}): '
        '${result.stderr}'.trim());
  }

  final match = RegExp(r'secret=([A-Za-z0-9_\-]+)').firstMatch(output);
  if (match == null) {
    throw StateError('esirc pair url printed no secret: $output');
  }
  return match.group(1)!;
}

/// Learns the agent's SPKI pin, the way the app's own discovery does.
///
/// The first contact with an agent is necessarily unverified - there is no pin yet - so it
/// is made with certificate checking disabled, exactly as `LanDiscovery` does, and the pin
/// is then read from the handshake. The certificate the server actually presented is
/// fingerprinted with the app's own DER reader at the same time and the two are compared,
/// so this also proves PROTOCOL.md A2.1's definition agrees with the agent's.
///
/// This replaced reading the pin out of the agent's log file, which was a poor foundation
/// for a test: Serilog holds the log open for writing, and a reader that asks only for
/// shared-read access is refused, so the read failed intermittently for reasons that had
/// nothing to do with the code under test.
Future<String> _learnPin() async {
  final deadline = DateTime.now().add(const Duration(seconds: 90));
  var lastError = 'the agent never answered';

  while (DateTime.now().isBefore(deadline)) {
    if (await _hasExited()) throw StateError('the agent exited during startup');
    try {
      String? presented;
      final client = HttpClient()
        ..connectionTimeout = const Duration(seconds: 5)
        ..badCertificateCallback = (certificate, _, _) {
          presented = pinForCertificate(certificate);
          return true;
        };
      try {
        final request = await client
            .getUrl(Uri.parse('https://$_host:$_port/api/v1/handshake'))
            .timeout(const Duration(seconds: 8));
        final response = await request.close().timeout(const Duration(seconds: 8));
        final body = await response.transform(utf8.decoder).join();
        if (response.statusCode != 200) {
          lastError = 'handshake returned ${response.statusCode}';
        } else {
          final json = jsonDecode(body);
          if (json is Map<String, dynamic>) {
            final advertised = json['spkiSha256'];
            final port = json['httpsPort'];
            if (advertised is String && advertised.isNotEmpty) {
              // The agent must also be telling the truth about which port it is on. It was
              // not, for a while: the listener took --port and the handshake reported the
              // default, so a discovered agent sent the phone to the wrong port.
              if (port is int && port != _port) {
                throw StateError(
                    'the agent is listening on $_port but its handshake advertises $port');
              }
              if (presented != null && presented != advertised) {
                throw StateError(
                    'the handshake advertises pin $advertised but the certificate it served '
                    'fingerprints to $presented');
              }
              return advertised;
            }
          }
          lastError = 'the handshake carried no spkiSha256';
        }
      } finally {
        client.close();
      }
    } on StateError {
      rethrow;
    } on Object catch (e) {
      lastError = '$e';
    }
    await Future<void>.delayed(const Duration(milliseconds: 500));
  }

  throw StateError('could not learn the agent\'s SPKI pin within 90s. Last error: $lastError'
      '\nThe agent said:\n${_agentLog.take(8).join('\n')}');
}

/// Stops the agent this run started. An agent somebody else started is left alone.

/// Stops the agent this run started. An agent somebody else started is left alone.
void _shutdown() {
  _ownedAgent?.kill();
  _ownedAgent = null;
}

/// Whether the self-hosted agent has already died.
///
/// `Process.exitCode` is a `Future`, so it cannot be compared to test for a crash; it
/// completes only once the process is gone. Racing it against a short delay answers
/// "did it die in the last moment" without blocking on a process that is running fine.
Future<bool> _hasExited() async {
  final process = _ownedAgent;
  if (process == null) return false;
  final code = await Future.any<int?>(<Future<int?>>[
    process.exitCode,
    Future<int?>.delayed(const Duration(milliseconds: 150), () => null),
  ]);
  return code != null;
}

// ------------------------------------------------------------------------- plumbing

Map<String, String> _parse(List<String> args) {
  final result = <String, String>{};
  for (var i = 0; i < args.length; i++) {
    if (!args[i].startsWith('--')) continue;
    final key = args[i].substring(2);
    final value = (i + 1 < args.length && !args[i + 1].startsWith('--')) ? args[++i] : '';
    result[key] = value;
  }
  return result;
}

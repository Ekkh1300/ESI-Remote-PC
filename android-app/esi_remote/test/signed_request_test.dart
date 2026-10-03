// What the app signs, and what it sends.
//
// The bug these tests exist for is the reason this file exists at all. `EsiApi` builds the
// request URL with `EsiEndpoint.apiUri`, which puts `/api/v1` in front of the path it is
// handed, but it was passing the *unprefixed* path to `signHeaders`. The agent rebuilds its
// canonical target from the raw path it received, so the two were signing different strings
// and every authenticated request came back `esi_signature`.
//
// Nothing caught it. The golden vectors pin the signature *formula*, and `buildCanonicalTarget`
// is tested directly with hand-written `/api/v1/...` strings, so both were correct - the
// wiring between them was not, and no test constructed an `EsiApi` at all.
//
// So these tests do the only thing that can catch it: they send a request through a fake
// transport, then rebuild the canonical string from the request that actually went out and
// check the signature against it. The expected value is derived from the wire, not from the
// call site.

import 'dart:convert';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_api.dart';
import 'package:esi_remote/src/protocol/esi_crypto.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

/// Records what was sent and answers with a fixed JSON body.
class _RecordingClient extends http.BaseClient {
  final List<http.BaseRequest> sent = <http.BaseRequest>[];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    sent.add(request);
    return http.StreamedResponse(
      Stream<List<int>>.value(utf8.encode('{"ok":true}')),
      200,
      headers: const <String, String>{'content-type': 'application/json'},
    );
  }
}

const _deviceId = 'a1b2c3d4e5f60718';
final _secret = Uint8List.fromList(List<int>.generate(32, (int i) => i + 1));

EsiApi _api(_RecordingClient transport) => EsiApi(
      endpoint: const EsiEndpoint(host: '192.168.1.42', port: 48220, pins: <String>['pin']),
      identity: EsiIdentity(deviceId: _deviceId, keyId: 3, masterSecret: _secret),
      httpClient: transport,
      clock: () => DateTime.utc(2026, 1, 2, 3, 4, 5),
      nonceSource: () => 'fixed-nonce-for-tests',
    );

/// Recomputes the signature from the bytes that went on the wire, the way the agent does.
String _signatureOf(http.BaseRequest request, EsiIdentity identity) {
  final uri = request.url;
  final canonical = EsiCrypto.buildCanonicalString(
    method: request.method,
    canonicalTarget: EsiCrypto.buildCanonicalTarget(uri.path, uri.query),
    timestamp: int.parse(request.headers[EsiProtocol.headerTimestamp]!),
    nonce: request.headers[EsiProtocol.headerNonce]!,
    contentSha256B64: request.headers[EsiProtocol.headerContentSha256]!,
    deviceId: request.headers[EsiProtocol.headerDeviceId]!,
    keyId: int.parse(request.headers[EsiProtocol.headerKeyId]!),
  );
  return EsiCrypto.sign(identity.sessionKey, canonical);
}

void main() {
  final identity = EsiIdentity(deviceId: _deviceId, keyId: 3, masterSecret: _secret);

  group('EsiApi signs what it sends', () {
    test('a signed GET is signed over the full /api/v1 path', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      await api.getJson('/system/info');

      final request = transport.sent.single;
      expect(request.url.path, '/api/v1/system/info');
      expect(
        request.headers[EsiProtocol.headerSignature],
        _signatureOf(request, identity),
        reason: 'the signature must be computed over the path actually sent, which '
            'includes the /api/v1 prefix that apiUri adds',
      );
    });

    test('the signed path is the wire path, prefix included - the regression itself', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      // The caller says "/system/info". The wire says "/api/v1/system/info". Signing the
      // caller's string is what produced a 401 esi_signature against the real agent.
      await api.getJson('/system/info');

      final sent = transport.sent.single.url.path;
      final signedPath = EsiCrypto.buildCanonicalTarget(sent, '');

      expect(sent, '/api/v1/system/info');
      expect(signedPath, contains('/api/v1/'),
          reason: 'the canonical target must carry the api base, or the agent computes a '
              'different HMAC and rejects the request');
    });

    test('a signed query request signs the query as sent, not as asked for', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      await api.getJson(
        '/files/list',
        query: <(String, String)>[('path', r'C:\'), ('sort', 'name')],
      );

      final request = transport.sent.single;
      expect(request.url.query, contains('path='));
      expect(request.headers[EsiProtocol.headerSignature], _signatureOf(request, identity));
    });

    test('a signed body request signs the hash of the body it sends', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      await api.postJson('/gaming/mode', body: <String, dynamic>{'mode': 'performance'});

      final request = transport.sent.single;
      final expectedHash =
          EsiCrypto.hashContent(utf8.encode(jsonEncode(<String, dynamic>{'mode': 'performance'})));
      expect(request.headers[EsiProtocol.headerContentSha256], expectedHash);
      expect(request.headers[EsiProtocol.headerSignature], _signatureOf(request, identity));
    });

    test('the signature changes when the path changes', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      await api.getJson('/system/info');
      await api.getJson('/system/metrics');

      final a = transport.sent[0].headers[EsiProtocol.headerSignature];
      final b = transport.sent[1].headers[EsiProtocol.headerSignature];
      expect(a, isNot(b));
    });

    test('an unsigned request carries no signature headers at all', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      await api.getJson('/health', signed: false);

      final request = transport.sent.single;
      expect(request.headers.containsKey(EsiProtocol.headerSignature), isFalse);
      expect(request.headers.containsKey(EsiProtocol.headerDeviceId), isFalse);
    });

    // Content-Type was written only on the unsigned branch, so every signed request with a
    // body went out media-type-less and the agent answered 415 before the endpoint ran.
    // Nothing here is about the signature - the point is that the header exists at all.
    group('a signed request with a body declares its media type', () {
      test('POST /apps/launch carries application/json', () async {
        final transport = _RecordingClient();
        final api = _api(transport);

        await api.postJson('/apps/launch', body: <String, dynamic>{'id': 'notepad'});

        final request = transport.sent.single;
        expect(
          request.headers['Content-Type'],
          'application/json; charset=utf-8',
          reason: 'the agent cannot bind a body it cannot identify; without this it '
              'answers 415 and the endpoint never runs',
        );
      });

      test('and is still signed correctly with the header present', () async {
        final transport = _RecordingClient();
        final api = _api(transport);

        await api.postJson('/power/shutdown', body: <String, dynamic>{'force': false});

        final request = transport.sent.single;
        expect(request.headers[EsiProtocol.headerSignature], _signatureOf(request, identity),
            reason: 'the media type is not covered by the signature, so adding it must not '
                'change what the agent computes');
      });

      test('a PUT body is declared too', () async {
        final transport = _RecordingClient();
        final api = _api(transport);

        await api.putJson('/settings', body: <String, dynamic>{'theme': 'dark'});

        expect(
          transport.sent.single.headers['Content-Type'],
          'application/json; charset=utf-8',
        );
      });

      test('an unsigned POST with a body keeps the header pairing relies on', () async {
        final transport = _RecordingClient();
        final api = _api(transport);

        await api.postJson('/pair/request', body: <String, dynamic>{'name': 'phone'},
            signed: false);

        expect(
          transport.sent.single.headers['Content-Type'],
          'application/json; charset=utf-8',
        );
      });

      test('a raw body carries the content type it was given', () async {
        final transport = _RecordingClient();
        final api = _api(transport);

        await api.sendRaw(
          method: 'POST',
          path: '/files/upload',
          body: Uint8List.fromList(<int>[1, 2, 3]),
          contentType: 'application/octet-stream',
        );

        expect(
          transport.sent.single.headers['Content-Type'],
          'application/octet-stream',
        );
      });

      test('a bodyless request declares nothing', () async {
        final transport = _RecordingClient();
        final api = _api(transport);

        await api.getJson('/system/info');

        expect(
          transport.sent.single.headers.containsKey('Content-Type'),
          isFalse,
          reason: 'there is no body, so there is no media type to advertise',
        );
      });
    });

    test('the signed identity headers match the identity in use', () async {
      final transport = _RecordingClient();
      final api = _api(transport);

      await api.getJson('/system/info');

      final request = transport.sent.single;
      expect(request.headers[EsiProtocol.headerDeviceId], _deviceId);
      expect(request.headers[EsiProtocol.headerKeyId], '3');
      // Seconds, not milliseconds, and derived rather than hard-coded: the agent compares
      // this against its own clock in seconds, so a client in milliseconds is rejected as
      // wildly skewed.
      expect(
        request.headers[EsiProtocol.headerTimestamp],
        (DateTime.utc(2026, 1, 2, 3, 4, 5).millisecondsSinceEpoch ~/ 1000).toString(),
      );
    });
  });
}

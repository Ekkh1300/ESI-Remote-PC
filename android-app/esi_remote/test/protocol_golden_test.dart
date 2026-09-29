/// Golden vectors for the wire protocol.
///
/// Every expectation in this file was produced by
/// `tool/generate_golden_vectors.py`, which is a line-for-line transliteration of the
/// Windows agent's `ESI.RemotePc.Protocol/EsiCrypto.cs` and `ProtocolConstants.cs`:
/// the same HMAC key/message order, the same HKDF extract/expand loop, the same
/// `string.Join('\n', …)` canonical string, the same base64. The generator asserts the
/// published RFC 5869 A.1/A.2/A.3 HKDF vectors before it emits anything, so a divergence
/// from the RFC would fail the generator rather than quietly agree with the app.
///
/// The canonical string is additionally checked against the worked example printed in
/// `docs/PROTOCOL.md` §4.2, which is the normative document both implementations are
/// written from.
///
/// Regenerate with:  python tool/generate_golden_vectors.py
library;

import 'dart:convert';
import 'dart:typed_data';

import 'package:esi_remote/src/protocol/esi_crypto.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
import 'package:flutter_test/flutter_test.dart';

/// The device identity used throughout `docs/PROTOCOL.md` §3.1 and §4.2.
const String deviceId = '3f2a9c17d4b8e206';

/// `0x00..0x1f`. Fixed so the derivation can be audited by hand.
final Uint8List masterSecret = Uint8List.fromList(
  List<int>.generate(32, (i) => i),
);

/// `0x20..0x3f`.
final Uint8List provisioningSecret = Uint8List.fromList(
  List<int>.generate(32, (i) => 0x20 + i),
);

/// `0x80..0x9f`: the master secret the agent mints during a rotation.
final Uint8List rotatedSecret = Uint8List.fromList(
  List<int>.generate(32, (i) => 0x80 + i),
);

const int timestamp = 1756000000;
const String nonce = 'f3Q8Zl0aW1kJ7w3K';
const String pairingId = 'b21f0c9d8e7a6b5c';

void main() {
  group('constants mirror ProtocolConstants.cs', () {
    test('values match the agent', () {
      expect(EsiProtocol.version, 1);
      expect(EsiProtocol.signatureDomain, 'ESIV1');
      expect(EsiProtocol.hkdfSalt, 'ESI-RemotePC/v1');
      expect(EsiProtocol.defaultHttpsPort, 48220);
      expect(EsiProtocol.defaultDiscoveryPort, 48211);
      expect(EsiProtocol.discoveryProbe, 'ESI_DISCOVER_V1');
      expect(EsiProtocol.mdnsServiceType, '_esiremotepc._tcp.local.');
      expect(EsiProtocol.apiBase, '/api/v1');
      expect(EsiProtocol.webSocketPath, '/ws');
      expect(EsiProtocol.nonceLength, 16);
      expect(EsiProtocol.secretLength, 32);
      expect(EsiProtocol.maxSignedBodyBytes, 8 * 1024 * 1024);
      expect(EsiProtocol.uploadTicketSeconds, 120);
    });

    test('the empty-body hash constant is really SHA-256 of nothing', () {
      // ProtocolConstants.EmptyBodySha256Base64 is a hard-coded literal on both sides; if
      // the two ever disagree, every request without a body fails with esi_signature.
      expect(EsiCrypto.hashContent(const <int>[]), EsiProtocol.emptyBodySha256B64);
      expect(
        EsiCrypto.base64(EsiCrypto.sha256(const <int>[])),
        EsiProtocol.emptyBodySha256B64,
      );
    });
  });

  group('canonical target', () {
    test('empty path becomes /', () {
      expect(EsiCrypto.buildCanonicalTarget('', null), '/');
    });

    test('no query leaves the path alone', () {
      expect(
        EsiCrypto.buildCanonicalTarget('/api/v1/system/metrics', null),
        '/api/v1/system/metrics',
      );
      expect(EsiCrypto.buildCanonicalTarget('/api/v1/system/metrics', ''), '/api/v1/system/metrics');
      expect(EsiCrypto.buildCanonicalTarget('/api/v1/system/metrics', '?'), '/api/v1/system/metrics');
    });

    test('segments sort ordinally and empty ones are dropped', () {
      // PROTOCOL.md §4.2: duplicates are preserved and sorted by the whole segment, so
      // `?a=2&a=1` sorts to `?a=1&a=2`.
      expect(
        EsiCrypto.buildCanonicalTarget(
          '/api/v1/files/search',
          '?maxResults=50&root=C%3A%5C&query=a&a=2&a=1&',
        ),
        '/api/v1/files/search?a=1&a=2&maxResults=50&query=a&root=C%3A%5C',
      );
    });

    test('a leading question mark is stripped, not doubled', () {
      expect(
        EsiCrypto.buildCanonicalTarget('/x', '?a=1'),
        EsiCrypto.buildCanonicalTarget('/x', 'a=1'),
      );
    });

    test('segments are not decoded or re-encoded', () {
      // The agent hashes the exact bytes it received. If either side normalised, a Windows
      // path with a space or a plus would break the signature.
      expect(
        EsiCrypto.buildCanonicalTarget('/api/v1/files/list', 'path=C%3A%5CA+B%20C'),
        '/api/v1/files/list?path=C%3A%5CA+B%20C',
      );
    });
  });

  group('canonical string', () {
    test('matches the worked example in PROTOCOL.md §4.2', () {
      final canonical = EsiCrypto.buildCanonicalString(
        method: 'GET',
        canonicalTarget: '/api/v1/system/info?detail=full',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiProtocol.emptyBodySha256B64,
        deviceId: deviceId,
        keyId: 1,
      );

      // The document shows the eight lines with a placeholder content hash. Every other
      // line must be identical; only line 6 differs, because the document's value is not
      // the real hash of an empty body.
      const fromDocument = <String>[
        'ESIV1',
        'GET',
        '/api/v1/system/info?detail=full',
        '1756000000',
        'f3Q8Zl0aW1kJ7w3K',
        EsiProtocol.emptyBodySha256B64,
        '3f2a9c17d4b8e206',
        '1',
      ];
      expect(canonical.split('\n'), fromDocument);
    });

    test('is exactly 8 LF-separated lines with no trailing newline', () {
      final canonical = EsiCrypto.buildCanonicalString(
        method: 'get',
        canonicalTarget: '/api/v1/files/search?a=1&a=2&maxResults=50&query=a&root=C%3A%5C',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiProtocol.emptyBodySha256B64,
        deviceId: deviceId,
        keyId: 12,
      );
      final lines = canonical.split('\n');
      expect(lines, hasLength(8));
      expect(canonical, isNot(endsWith('\n')));
      expect(
        canonical,
        'ESIV1\n'
        'GET\n'
        '/api/v1/files/search?a=1&a=2&maxResults=50&query=a&root=C%3A%5C\n'
        '1756000000\n'
        'f3Q8Zl0aW1kJ7w3K\n'
        '47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=\n'
        '3f2a9c17d4b8e206\n'
        '12',
      );
    });

    test('uppercases the method and lowercases the device id', () {
      final canonical = EsiCrypto.buildCanonicalString(
        method: 'post',
        canonicalTarget: '/api/v1/gaming/mode',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: 'X',
        deviceId: deviceId.toUpperCase(),
        keyId: 1,
      );
      expect(canonical.split('\n')[1], 'POST');
      expect(canonical.split('\n')[6], deviceId);
    });
  });

  group('key derivation', () {
    test('matches the golden vectors', () {
      expect(
        EsiCrypto.base64(EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1)),
        '+cfGI98S26cbOjGTJtYAulmCRjSL58QQJORChRHvXB4=',
      );
      expect(
        EsiCrypto.base64(EsiCrypto.deriveWrapKey(masterSecret, deviceId)),
        'zufKdgrBzKvo0m0XXfikGlwxqGBiUpaHcMUO7e3TTMU=',
      );
      expect(
        EsiCrypto.base64(EsiCrypto.deriveAuditKey(masterSecret, deviceId, 1)),
        'a3onjZVUZ7X/wSIILDQ3KNUdS0QyZiYK0eQLvxRXSF4=',
      );
      expect(
        EsiCrypto.base64(EsiCrypto.deriveProvisioningKey(provisioningSecret, pairingId)),
        'VD3QGQ8Vy844FnaCCKHor3r4ua/IVnXa6HvHssjeKwE=',
      );
    });

    test('the session key changes with the key generation', () {
      expect(
        EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1),
        isNot(EsiCrypto.deriveSessionKey(masterSecret, deviceId, 2)),
      );
    });

    test('the session key changes with the device id', () {
      expect(
        EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1),
        isNot(EsiCrypto.deriveSessionKey(masterSecret, '0000000000000000', 1)),
      );
    });

    test('HKDF matches RFC 5869 A.1', () {
      // Guards against the transliteration and the Dart implementation both being
      // self-consistently wrong. RFC 5869 A.1: IKM = 0x0b x 22, salt = 0x00..0x0c,
      // info = 0xf0..0xf9, L = 42.
      final prk = EsiCrypto.hkdfExtract(
        _hex('000102030405060708090a0b0c'),
        List<int>.filled(22, 0x0b),
      );
      expect(
        EsiCrypto.base64(prk),
        EsiCrypto.base64(_hex('077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5')),
      );
      final okm = EsiCrypto.hkdfExpand(
        prk,
        _hex('f0f1f2f3f4f5f6f7f8f9'),
        42,
      );
      expect(
        EsiCrypto.base64(okm),
        EsiCrypto.base64(_hex(
          '3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf'
          '34007208d5b887185865',
        )),
      );
    });

    test('HKDF matches RFC 5869 A.2 across two blocks', () {
      final okm = EsiCrypto.hkdfExpand(
        EsiCrypto.hkdfExtract(
          List<int>.generate(80, (i) => 0x60 + i),
          List<int>.generate(80, (i) => i),
        ),
        List<int>.generate(80, (i) => 0xb0 + i),
        82,
      );
      expect(
        EsiCrypto.base64(okm),
        EsiCrypto.base64(_hex(
          'b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c'
          '59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71'
          'cc30c58179ec3e87c14c01d5c1f3434f1d87',
        )),
      );
    });

    test('HKDF matches RFC 5869 A.3 with an empty salt and info', () {
      final prk = EsiCrypto.hkdfExtract(
        const <int>[],
        List<int>.filled(22, 0x0b),
      );
      expect(
        EsiCrypto.base64(prk),
        EsiCrypto.base64(_hex('19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04')),
      );
      final okm = EsiCrypto.hkdfExpand(prk, const <int>[], 42);
      expect(
        EsiCrypto.base64(okm),
        EsiCrypto.base64(_hex(
          '8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d'
          '9d201395faa4b61a96c8',
        )),
      );
    });

    test('extract uses the salt as the HMAC key, not the message', () {
      // RFC 5869 §2.1, and the one place a transposition would go unnoticed because
      // HMAC is symmetric in nothing at all.
      expect(
        EsiCrypto.hkdfExtract(_hex('0a0b0c'), _hex('0102')),
        isNot(EsiCrypto.hkdfExtract(_hex('0102'), _hex('0a0b0c'))),
      );
    });

    test('a nonsense output length is rejected', () {
      expect(
        () => EsiCrypto.hkdfExpand(const <int>[1], const <int>[], 0),
        throwsArgumentError,
      );
      expect(
        () => EsiCrypto.hkdfExpand(const <int>[1], const <int>[], 255 * 32 + 1),
        throwsArgumentError,
      );
    });
  });

  group('signatures', () {
    test('match the golden vectors', () {
      final sessionKey = EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1);

      final simple = EsiCrypto.buildCanonicalString(
        method: 'GET',
        canonicalTarget: '/api/v1/system/info?detail=full',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiProtocol.emptyBodySha256B64,
        deviceId: deviceId,
        keyId: 1,
      );
      expect(
        EsiCrypto.sign(sessionKey, simple),
        '3IgBbqX92+lvNq8roQ22p4USyn8+CgMxCwb6OGrnDto=',
      );

      final messy = EsiCrypto.buildCanonicalString(
        method: 'get',
        canonicalTarget: '/api/v1/files/search?a=1&a=2&maxResults=50&query=a&root=C%3A%5C',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiProtocol.emptyBodySha256B64,
        deviceId: deviceId,
        keyId: 12,
      );
      expect(
        EsiCrypto.sign(sessionKey, messy),
        'hNBeWVEj92bLuC8zlqNphVlKlOqqK0C5o8slvCFuq/4=',
      );
    });

    test('a request with a body matches the golden vector', () {
      final sessionKey = EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1);
      final body = utf8.encode('{"enabled":true,"profileId":"performance"}');
      expect(
        EsiCrypto.hashContent(body),
        '18QKl3TUP/rmFyQfKm8JgHzfn/53Pii/gvzyRAi7QZs=',
      );
      final canonical = EsiCrypto.buildCanonicalString(
        method: 'POST',
        canonicalTarget: '/api/v1/gaming/mode',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiCrypto.hashContent(body),
        deviceId: deviceId,
        keyId: 1,
      );
      expect(
        EsiCrypto.sign(sessionKey, canonical),
        'pKec3mxgJ5BwjHRmv3vhMxKFz7P9MJIm7rnYQaC3rJ0=',
      );
    });

    test('the signature covers the query order, not just its contents', () {
      final key = EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1);
      final a = EsiCrypto.buildCanonicalString(
        method: 'GET',
        canonicalTarget: '/x?a=1&b=2',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiProtocol.emptyBodySha256B64,
        deviceId: deviceId,
        keyId: 1,
      );
      final b = EsiCrypto.buildCanonicalString(
        method: 'GET',
        canonicalTarget: '/x?b=2&a=1',
        timestamp: timestamp,
        nonce: nonce,
        contentSha256B64: EsiProtocol.emptyBodySha256B64,
        deviceId: deviceId,
        keyId: 1,
      );
      expect(EsiCrypto.sign(key, a), isNot(EsiCrypto.sign(key, b)));
    });

    test('the method is inside the MAC, so a signature cannot be moved', () {
      // §2.2 anti-confusion: a captured GET cannot be replayed as a DELETE.
      final key = EsiCrypto.deriveSessionKey(masterSecret, deviceId, 1);
      String sign(String method) => EsiCrypto.sign(
            key,
            EsiCrypto.buildCanonicalString(
              method: method,
              canonicalTarget: '/api/v1/power/shutdown',
              timestamp: timestamp,
              nonce: nonce,
              contentSha256B64: EsiProtocol.emptyBodySha256B64,
              deviceId: deviceId,
              keyId: 1,
            ),
          );
      expect(sign('GET'), isNot(sign('DELETE')));
    });

    test('constant-time comparison behaves like ==', () {
      const a = '3IgBbqX92+lvNq8roQ22p4USyn8+CgMxCwb6OGrnDto=';
      expect(EsiCrypto.signatureEquals(a, a), isTrue);
      expect(EsiCrypto.signatureEquals(a, '${a}x'), isFalse);
      expect(EsiCrypto.signatureEquals(a, ''), isFalse);
      expect(EsiCrypto.signatureEquals('not base64!', 'also not'), isFalse);
    });
  });

  group('content hashing', () {
    test('a null body and an empty body both hash to the empty constant', () {
      expect(EsiCrypto.hashContent(null), EsiProtocol.emptyBodySha256B64);
      expect(EsiCrypto.hashContent(const <int>[]), EsiProtocol.emptyBodySha256B64);
    });

    test('the body is hashed, not the JSON text', () {
      // The agent hashes the bytes on the wire, so the client has to hash the same bytes.
      final body = utf8.encode('{"enabled":true}');
      expect(EsiCrypto.hashContent(body), EsiCrypto.base64(EsiCrypto.sha256(body)));
    });
  });

  group('base64url', () {
    test('matches the golden vector', () {
      expect(
        EsiCrypto.base64Url(List<int>.filled(16, 0xff)),
        '_____________________w',
      );
      expect(
        EsiCrypto.base64Url(<int>[0xfb, 0xff, 0xbe, 0x00, 0x10]),
        '-_--ABA',
      );
    });

    test('round-trips, including the + and / characters', () {
      final bytes = List<int>.generate(48, (i) => (i * 37) % 256);
      expect(EsiCrypto.fromBase64Url(EsiCrypto.base64Url(bytes)), bytes);
    });

    test('accepts a padded value as well', () {
      expect(EsiCrypto.fromBase64Url('_-8='), <int>[0xff, 0xef]);
    });
  });

  group('nonce and device id', () {
    test('a nonce is 16 base64url characters of 16 bytes', () {
      final value = EsiCrypto.newNonce();
      expect(value, hasLength(22));
      expect(value, isNot(contains('=')));
      expect(value, isNot(contains('+')));
      expect(value, isNot(contains('/')));
      expect(EsiCrypto.fromBase64Url(value), hasLength(EsiProtocol.nonceLength));
    });

    test('nonces do not repeat', () {
      final seen = <String>{};
      for (var i = 0; i < 200; i++) {
        seen.add(EsiCrypto.newNonce());
      }
      expect(seen, hasLength(200));
    });

    test('a device id is 16 lowercase hex characters', () {
      final id = EsiCrypto.newDeviceId();
      expect(id, hasLength(16));
      expect(id, matches(RegExp(r'^[0-9a-f]{16}$')));
    });
  });

  group('AES-256-GCM unwrapping', () {
    test('opens the pairing envelope from the golden vector', () async {
      final opened = await EsiCrypto.openPairingEnvelope(
        envelope: WrappedSecretEnvelope(
          nonce: EsiCrypto.fromBase64('QEFCQ0RFRkdISUpL'),
          ciphertext: EsiCrypto.fromBase64('006FjGld21jBdnZLe+kHEov4zxcnlVuxhO5+zz821RpF0fTc'),
          tag: EsiCrypto.fromBase64('qzbhnvGwb/jGUVxC+wv8zA=='),
        ),
        provisioningSecret: provisioningSecret,
        pairingId: pairingId,
        deviceId: deviceId,
      );
      expect(opened.masterSecret, masterSecret);
      expect(opened.keyId, 1);
      // `masterSecret || uint32be(keyId)` is 36 bytes.
      expect(opened.masterSecret, hasLength(36 - 4));
    });

    test('opens the rotation envelope from the golden vector', () async {
      final opened = await EsiCrypto.openRotationEnvelope(
        envelope: WrappedSecretEnvelope(
          nonce: EsiCrypto.fromBase64('oKGio6Slpqeoqaqr'),
          ciphertext: EsiCrypto.fromBase64(
            'AV/fapKkygbGx9eC+Ed/28gFXIn09XM32IQM6wBrgR+9ItOX',
          ),
          tag: EsiCrypto.fromBase64('UxEBcrFNogkis0oYxXGQ2g=='),
        ),
        masterSecret: masterSecret,
        deviceId: deviceId,
      );
      expect(opened.masterSecret, rotatedSecret);
      expect(opened.keyId, 2);
    });

    test('the wrong provisioning secret does not open the envelope', () async {
      await expectLater(
        EsiCrypto.openPairingEnvelope(
          envelope: WrappedSecretEnvelope(
            nonce: EsiCrypto.fromBase64('QEFCQ0RFRkdISUpL'),
            ciphertext: EsiCrypto.fromBase64('006FjGld21jBdnZLe+kHEov4zxcnlVuxhO5+zz821RpF0fTc'),
            tag: EsiCrypto.fromBase64('qzbhnvGwb/jGUVxC+wv8zA=='),
          ),
          provisioningSecret: Uint8List.fromList(List<int>.filled(32, 0xaa)),
          pairingId: pairingId,
          deviceId: deviceId,
        ),
        throwsA(isA<EsiCryptoException>()),
      );
    });

    test('a tampered ciphertext does not open the envelope', () async {
      final tampered = EsiCrypto.fromBase64('006FjGld21jBdnZLe+kHEov4zxcnlVuxhO5+zz821RpF0fTd');
      tampered[0] ^= 0x01;
      await expectLater(
        EsiCrypto.openPairingEnvelope(
          envelope: WrappedSecretEnvelope(
            nonce: EsiCrypto.fromBase64('QEFCQ0RFRkdISUpL'),
            ciphertext: tampered,
            tag: EsiCrypto.fromBase64('qzbhnvGwb/jGUVxC+wv8zA=='),
          ),
          provisioningSecret: provisioningSecret,
          pairingId: pairingId,
          deviceId: deviceId,
        ),
        throwsA(isA<EsiCryptoException>()),
      );
    });

    test('the pairing id is inside the AAD, so it cannot be swapped', () async {
      await expectLater(
        EsiCrypto.openPairingEnvelope(
          envelope: WrappedSecretEnvelope(
            nonce: EsiCrypto.fromBase64('QEFCQ0RFRkdISUpL'),
            ciphertext: EsiCrypto.fromBase64('006FjGld21jBdnZLe+kHEov4zxcnlVuxhO5+zz821RpF0fTc'),
            tag: EsiCrypto.fromBase64('qzbhnvGwb/jGUVxC+wv8zA=='),
          ),
          provisioningSecret: provisioningSecret,
          pairingId: 'ffffffffffffffff',
          deviceId: deviceId,
        ),
        throwsA(isA<EsiCryptoException>()),
      );
    });

    test('the plaintext layout is 32 bytes of secret then a big-endian key id', () {
      final payload = Uint8List.fromList(<int>[...masterSecret, 0, 0, 1, 44]);
      final parsed = EsiCrypto.unwrapSecretPayload(payload);
      expect(parsed.masterSecret, masterSecret);
      expect(parsed.keyId, 300);
    });

    test('a plaintext of the wrong length is rejected outright', () {
      expect(
        () => EsiCrypto.unwrapSecretPayload(Uint8List(35)),
        throwsA(isA<EsiCryptoException>()),
      );
    });
  });
}

Uint8List _hex(String hex) {
  final out = Uint8List(hex.length ~/ 2);
  for (var i = 0; i < out.length; i++) {
    out[i] = int.parse(hex.substring(i * 2, i * 2 + 2), radix: 16);
  }
  return out;
}

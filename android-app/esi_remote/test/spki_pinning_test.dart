/// SPKI pin extraction.
///
/// The pin is `base64(SHA-256(DER(SubjectPublicKeyInfo)))` (PROTOCOL.md §2.1) and
/// `dart:io` exposes no public-key accessor on [X509Certificate], so the app walks the
/// DER itself. These tests run it against a real self-signed ECDSA P-256 certificate of
/// exactly the kind the agent generates, and against the same certificate with a
/// deliberately broken structure.
library;

import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:esi_remote/src/net/spki_pinning.dart';
import 'package:esi_remote/src/protocol/esi_crypto.dart';
import 'package:flutter_test/flutter_test.dart';

/// A self-signed P-256 certificate with `CN=META-PC`, generated for this test.
const String _certificateBase64 =
    'MIIBJDCByqADAgECAhQ8iy0VKvl19WmPyQBNQ2+JXCaY4jAKBggqhkjOPQQDAjASMRAwDgYDVQQDDAdNRVRBLVBD'
    'MB4XDTI1MDgyNDEwMDAwMFoXDTI2MDgyNDEwMDAwMFowEjEQMA4GA1UEAwwHTUVUQS1QQzBZMBMGByqGSM49AgEG'
    'CCqGSM49AwEHA0IABOqYRHQx9BO05MyTLy7MQp/+wBkL1/NhDYckxUGXJStFR6j0PyMnzFkM7bM+D5bE09HmbapgDP2jC'
    'ocUNisNMzMwCgYIKoZIzj0EAwIDSQAwRgIhAMsTtfZo8DSIL3i19aab74JsmzAEQz7saaVq+jek//0gAiEAzJW8PD'
    'VyJeiklRQscVcTrEPEg8f4tNM8qujzxIQWs6s=';

/// The same certificate's SubjectPublicKeyInfo, which is what must be hashed.
const String _spkiBase64 =
    'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6phEdDH0E7TkzJMvLsxCn/7AGQvX82ENhyTFQZclK0VHqPQ/IyfMWQz'
    'tsz4PlsTT0eZtqmAM/aMKhxQ2Kw0zMw==';

/// `base64(SHA-256(SPKI))` for the certificate above, computed independently.
const String _expectedPin = 'SU5BeCIH84IaJiDJCCzPOdeKngzhZRTMcoID7N0hRc0=';

void main() {
  group('DER reader', () {
    test('finds the SubjectPublicKeyInfo in a real certificate', () {
      final der = base64.decode(_certificateBase64);
      final spki = DerReader.subjectPublicKeyInfo(Uint8List.fromList(der));
      expect(spki, isNotNull);
      expect(base64.encode(spki!), _spkiBase64);
    });

    test('the extracted key hashes to the expected pin', () {
      final der = Uint8List.fromList(base64.decode(_certificateBase64));
      final spki = DerReader.subjectPublicKeyInfo(der)!;
      expect(
        EsiCrypto.spkiSha256FromDer(spki),
        _expectedPin,
      );
    });

    test('the pin is over the SPKI, not the whole certificate', () {
      // Pinning the certificate would break on every renewal; PROTOCOL.md §2.1 is explicit
      // that it is the public key that is pinned.
      final der = Uint8List.fromList(base64.decode(_certificateBase64));
      expect(
        EsiCrypto.spkiSha256FromDer(der),
        isNot(_expectedPin),
      );
    });

    test('a truncated buffer yields null instead of throwing', () {
      final der = Uint8List.fromList(base64.decode(_certificateBase64));
      for (final length in <int>[0, 1, 4, 16, 64, 200]) {
        expect(
          DerReader.subjectPublicKeyInfo(Uint8List.sublistView(der, 0, length)),
          isNull,
          reason: 'length $length should not parse',
        );
      }
    });

    test('garbage yields null', () {
      expect(
        DerReader.subjectPublicKeyInfo(Uint8List.fromList(List<int>.filled(64, 0x41))),
        isNull,
      );
    });

    test('an indefinite-length encoding is rejected', () {
      // 0x80 in the length byte means "the rest of the buffer", which is not valid DER and
      // would otherwise make the walk run off the end.
      final der = Uint8List.fromList(<int>[0x30, 0x80, ...List<int>.filled(20, 0)]);
      expect(DerReader.subjectPublicKeyInfo(der), isNull);
    });

    test('a long-form length is handled', () {
      // 0x82 means two length bytes follow. The certificate above is 296 bytes, so its
      // outer length is already a long-form one — this asserts the 0x82 path specifically.
      final der = Uint8List.fromList(base64.decode(_certificateBase64));
      expect(der[1] & 0x80, isNot(0));
      expect(DerReader.subjectPublicKeyInfo(der), isNotNull);
    });

    test('elementRange bounds-checks the end of the buffer', () {
      final der = Uint8List.fromList(base64.decode(_certificateBase64));
      expect(DerReader.elementRange(der, der.length - 1), isNull);
      expect(DerReader.elementRange(der, der.length), isNull);
    });
  });

  group('pin comparison', () {
    test('accepts a pin in the set', () {
      final client = PinnedHttpClient(pins: <String>[_expectedPin, 'other-pin']);
      expect(client.pins, contains(_expectedPin));
      addTearDown(client.close);
    });

    test('refuses to be built with no pins at all', () {
      // An empty pin set would make badCertificateCallback a permanent true, which is the
      // exact bug this class exists to prevent.
      expect(() => PinnedHttpClient(pins: const <String>[]), throwsA(isA<AssertionError>()));
    });
  });
}

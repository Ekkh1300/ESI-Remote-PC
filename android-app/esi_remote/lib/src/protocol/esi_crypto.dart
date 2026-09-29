import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as c;
import 'package:cryptography/cryptography.dart';

import 'protocol_constants.dart';

/// Thrown when a wrapped-secret envelope fails authentication.
class EsiCryptoException implements Exception {
  EsiCryptoException(this.message);
  final String message;
  @override
  String toString() => 'EsiCryptoException: $message';
}

/// The client half of `EsiCrypto.cs`.
///
/// Every function in here has a line-for-line counterpart in
/// `windows-agent/src/ESI.RemotePc.Protocol/EsiCrypto.cs`, and the golden vectors in
/// `test/protocol_golden_test.dart` are produced by transliterating that C# file.
class EsiCrypto {
  const EsiCrypto._();

  // ------------------------------------------------------------------ primitives

  static Uint8List sha256(List<int> data) =>
      Uint8List.fromList(c.sha256.convert(data).bytes);

  static Uint8List hmacSha256(List<int> key, List<int> message) =>
      Uint8List.fromList(c.Hmac(c.sha256, key).convert(message).bytes);

  /// RFC 5869 §2.1 extract. The salt is the HMAC *key*; `ikm` is the input. Matches
  /// `HMACSHA256.HashData(salt, ikm)`.
  static Uint8List hkdfExtract(List<int> salt, List<int> ikm) =>
      hmacSha256(salt, ikm);

  /// RFC 5869 §2.2 expand: `T(i) = HMAC(PRK, T(i-1) | info | i)`.
  static Uint8List hkdfExpand(List<int> prk, List<int> info, int length) {
    if (length < 1 || length > 255 * 32) {
      throw ArgumentError.value(length, 'length', 'HKDF output length must be 1..8160');
    }
    final out = Uint8List(length);
    var previous = const <int>[];
    var offset = 0;
    for (var counter = 1; offset < length; counter++) {
      final input = <int>[...previous, ...info, counter];
      previous = hmacSha256(prk, input);
      final take = min(previous.length, length - offset);
      out.setRange(offset, offset + take, previous);
      offset += take;
    }
    return out;
  }

  static Uint8List hkdf(
    List<int> ikm,
    List<int> salt,
    List<int> info,
    int length,
  ) =>
      hkdfExpand(hkdfExtract(salt, ikm), info, length);

  /// `DeriveKey`: HKDF over the fixed protocol salt with an application label.
  static Uint8List deriveKey(
    List<int> masterSecret,
    String label, {
    int length = EsiProtocol.secretLength,
  }) =>
      hkdf(
        masterSecret,
        utf8.encode(EsiProtocol.hkdfSalt),
        utf8.encode(label),
        length,
      );

  /// The key that authenticates this device's requests.
  static Uint8List deriveSessionKey(
    List<int> masterSecret,
    String deviceId,
    int keyId,
  ) =>
      deriveKey(masterSecret, 'esi-session-v1|$deviceId|$keyId');

  /// The key used to wrap a rotated master secret for delivery to this device.
  static Uint8List deriveWrapKey(List<int> masterSecret, String deviceId) =>
      deriveKey(masterSecret, 'esi-wrap-v1|$deviceId');

  /// The key chained into the audit log.
  static Uint8List deriveAuditKey(
    List<int> masterSecret,
    String deviceId,
    int keyId,
  ) =>
      deriveKey(masterSecret, 'esi-audit-v1|$deviceId|$keyId');

  /// The one-time key that encrypts the initial secret delivery for a pairing attempt.
  static Uint8List deriveProvisioningKey(
    List<int> provisioningSecret,
    String pairingId,
  ) =>
      deriveKey(provisioningSecret, 'esi-provision-v1|$pairingId');

  // ------------------------------------------------------------------ encodings

  static String base64(List<int> bytes) => base64Encode(bytes);

  static Uint8List fromBase64(String value) => base64Decode(value);

  /// base64url without padding. `EsiCrypto.Base64Url`.
  static String base64Url(List<int> data) =>
      base64Encode(data).replaceAll('=', '').replaceAll('+', '-').replaceAll('/', '_');

  static Uint8List fromBase64Url(String value) {
    final normalised = value.replaceAll('-', '+').replaceAll('_', '/');
    final padding = (4 - (normalised.length % 4)) % 4;
    return base64Decode('$normalised${'=' * padding}');
  }

  static Uint8List bigEndianBytes(int value) {
    final b = Uint8List(4);
    b[0] = (value >> 24) & 0xff;
    b[1] = (value >> 16) & 0xff;
    b[2] = (value >> 8) & 0xff;
    b[3] = value & 0xff;
    return b;
  }

  /// Parses the 36-byte wrapped plaintext `masterSecret || uint32be(keyId)`.
  static ({Uint8List masterSecret, int keyId}) unwrapSecretPayload(Uint8List plaintext) {
    if (plaintext.length != EsiProtocol.secretLength + 4) {
      throw EsiCryptoException(
        'Wrapped secret is ${plaintext.length} bytes; expected '
        '${EsiProtocol.secretLength + 4}.',
      );
    }
    final secret = Uint8List.sublistView(plaintext, 0, EsiProtocol.secretLength);
    final keyId = (plaintext[32] << 24) |
        (plaintext[33] << 16) |
        (plaintext[34] << 8) |
        plaintext[35];
    return (masterSecret: Uint8List.fromList(secret), keyId: keyId);
  }

  // ------------------------------------------------------------------ AES-256-GCM

  static final AesGcm _aesGcm = AesGcm.with256bits();

  /// Reverses `EsiCrypto.Seal`. Throws [EsiCryptoException] on a tag mismatch, which is
  /// what a tampered envelope or the wrong provisioning secret looks like.
  static Future<Uint8List> aesGcmOpen({
    required List<int> nonce,
    required List<int> ciphertext,
    required List<int> tag,
    required List<int> key,
    required List<int> associatedData,
  }) async {
    final box = SecretBox(
      ciphertext,
      nonce: nonce,
      mac: Mac(tag),
    );
    try {
      final clear = await _aesGcm.decrypt(
        box,
        secretKey: SecretKey(key),
        aad: associatedData,
      );
      return Uint8List.fromList(clear);
    } on SecretBoxAuthenticationError {
      throw EsiCryptoException(
        'The wrapped secret did not authenticate. Either the envelope was tampered with '
        'or the provisioning secret belongs to a different pairing attempt.',
      );
    }
  }

  /// Unwraps the `wrappedSecrets` envelope produced by the initial pairing (§5.4).
  static Future<({Uint8List masterSecret, int keyId})> openPairingEnvelope({
    required WrappedSecretEnvelope envelope,
    required Uint8List provisioningSecret,
    required String pairingId,
    required String deviceId,
  }) async {
    final key = deriveProvisioningKey(provisioningSecret, pairingId);
    final aad = utf8.encode('esi-pair-v1|$pairingId|$deviceId');
    final plaintext = await aesGcmOpen(
      nonce: envelope.nonce,
      ciphertext: envelope.ciphertext,
      tag: envelope.tag,
      key: key,
      associatedData: aad,
    );
    return unwrapSecretPayload(plaintext);
  }

  /// Unwraps the `wrappedSecrets` envelope produced by `POST /auth/rotate` (§5.3).
  static Future<({Uint8List masterSecret, int keyId})> openRotationEnvelope({
    required WrappedSecretEnvelope envelope,
    required Uint8List masterSecret,
    required String deviceId,
  }) async {
    final key = deriveWrapKey(masterSecret, deviceId);
    final aad = utf8.encode('esi-rotate-v1|$deviceId');
    final plaintext = await aesGcmOpen(
      nonce: envelope.nonce,
      ciphertext: envelope.ciphertext,
      tag: envelope.tag,
      key: key,
      associatedData: aad,
    );
    return unwrapSecretPayload(plaintext);
  }

  // ------------------------------------------------------------------ signatures

  /// Normalises a request target: raw path plus ordinally sorted raw query segments.
  ///
  /// Neither side decodes or re-encodes: the agent hashes the exact bytes it received, so
  /// the client has to sign the exact bytes it sends.
  static String buildCanonicalTarget(String rawPath, String? rawQuery) {
    final path = rawPath.isEmpty ? '/' : rawPath;
    if (rawQuery == null || rawQuery.isEmpty) return path;

    var trimmed = rawQuery;
    if (trimmed.startsWith('?')) trimmed = trimmed.substring(1);
    if (trimmed.isEmpty) return path;

    final segments = trimmed
        .split('&')
        .where((s) => s.isNotEmpty)
        .toList(growable: false)
      // Dart's String.compareTo orders by UTF-16 code unit, which is what
      // StringComparer.Ordinal does on the C# side. Every segment the app signs is ASCII.
      ..sort();
    return '$path?${segments.join('&')}';
  }

  /// The 8-line canonical string. LF-separated, no trailing newline.
  static String buildCanonicalString({
    required String method,
    required String canonicalTarget,
    required int timestamp,
    required String nonce,
    required String contentSha256B64,
    required String deviceId,
    required int keyId,
  }) =>
      <String>[
        EsiProtocol.signatureDomain,
        method.toUpperCase(),
        canonicalTarget,
        timestamp.toString(),
        nonce,
        contentSha256B64,
        deviceId.toLowerCase(),
        keyId.toString(),
      ].join('\n');

  /// base64 of HMAC-SHA256(`sessionKey`, UTF-8 of the canonical string).
  static String sign(List<int> sessionKey, String canonicalString) =>
      base64(hmacSha256(sessionKey, utf8.encode(canonicalString)));

  /// base64 SHA-256 of the body, or the empty-body constant when there is no body.
  static String hashContent(List<int>? body) {
    if (body == null || body.isEmpty) return EsiProtocol.emptyBodySha256B64;
    return base64(sha256(body));
  }

  /// Constant-time comparison of two base64 MACs. Never `==` on a signature.
  static bool signatureEquals(String expectedB64, String actualB64) {
    final a = _tryDecodeBase64(expectedB64);
    final b = _tryDecodeBase64(actualB64);
    if (a == null || b == null) return false;
    if (a.length != b.length) return false;
    var diff = 0;
    for (var i = 0; i < a.length; i++) {
      diff |= a[i] ^ b[i];
    }
    return diff == 0;
  }

  static List<int>? _tryDecodeBase64(String value) {
    try {
      return base64Decode(value);
    } on FormatException {
      return null;
    }
  }

  // ------------------------------------------------------------------ randomness

  static final Random _secure = Random.secure();

  static Uint8List randomBytes(int count) {
    final out = Uint8List(count);
    for (var i = 0; i < count; i++) {
      out[i] = _secure.nextInt(256);
    }
    return out;
  }

  /// A 16-character lowercase hex id, matching `EsiCrypto.NewDeviceId`.
  static String newDeviceId() {
    final bytes = randomBytes(8);
    final buffer = StringBuffer();
    for (final b in bytes) {
      buffer.write(b.toRadixString(16).padLeft(2, '0'));
    }
    return buffer.toString();
  }

  /// base64url (no padding) of 16 random bytes, matching `EsiCrypto.NewNonce`.
  static String newNonce() => base64Url(randomBytes(EsiProtocol.nonceLength));

  // ------------------------------------------------------------------ SPKI pinning

  /// `base64(SHA-256(DER(SubjectPublicKeyInfo)))`, per PROTOCOL.md §2.1.
  static String spkiSha256FromDer(List<int> subjectPublicKeyInfoDer) =>
      base64(sha256(subjectPublicKeyInfoDer));
}

/// The base64 AES-GCM envelope from `WrappedSecretDto`.
class WrappedSecretEnvelope {
  const WrappedSecretEnvelope({
    required this.nonce,
    required this.ciphertext,
    required this.tag,
  });

  factory WrappedSecretEnvelope.fromJson(Map<String, dynamic>? json) {
    if (json == null) {
      throw const FormatException('wrappedSecrets is missing from the pairing response');
    }
    return WrappedSecretEnvelope(
      nonce: EsiCrypto.fromBase64(_requireString(json, 'nonce')),
      ciphertext: EsiCrypto.fromBase64(_requireString(json, 'ciphertext')),
      tag: EsiCrypto.fromBase64(_requireString(json, 'tag')),
    );
  }

  final Uint8List nonce;
  final Uint8List ciphertext;
  final Uint8List tag;

  Map<String, dynamic> toJson() => <String, dynamic>{
        'nonce': EsiCrypto.base64(nonce),
        'ciphertext': EsiCrypto.base64(ciphertext),
        'tag': EsiCrypto.base64(tag),
      };
}

String _requireString(Map<String, dynamic> json, String key) {
  final value = json[key];
  if (value is! String || value.isEmpty) {
    throw FormatException('Expected a non-empty string at "$key".');
  }
  return value;
}

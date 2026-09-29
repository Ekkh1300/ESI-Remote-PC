import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' show sha256;


/// Why a TLS connection was refused. Surfaced verbatim in the UI, because "certificate
/// pinning failed" is not something the user can act on unless it says what it expected.
class CertificatePinFailure implements Exception {
  const CertificatePinFailure({
    required this.reason,
    required this.expected,
    required this.presented,
  });

  final String reason;

  /// The pins the app is willing to accept: the current one from the pairing QR and, after
  /// a rotation, the next one.
  final List<String> expected;

  /// The pin the server actually presented, so the user can compare it by eye.
  final String? presented;

  @override
  String toString() => reason;
}

/// Minimal DER reader, just enough to pull `SubjectPublicKeyInfo` out of a certificate.
///
/// `dart:io`'s [X509Certificate] exposes `der` and `pem` but no public key accessor, and
/// PROTOCOL.md §2.1 pins `SHA-256(DER(SubjectPublicKeyInfo))`. Rather than reimplement X.509
/// this walks the two SEQUENCEs it needs to walk:
///
/// ```text
/// Certificate     ::= SEQUENCE { tbsCertificate, signatureAlgorithm, signatureValue }
/// TBSCertificate  ::= SEQUENCE {
///     version              [0] EXPLICIT INTEGER,   -- skipped
///     serialNumber             INTEGER,            -- skipped
///     signature                AlgorithmIdentifier,-- skipped
///     issuer                   Name,               -- skipped
///     validity                 Validity,           -- skipped
///     subject                  Name,               -- skipped
///     subjectPublicKeyInfo     SubjectPublicKeyInfo -- this one
///     ... issuerUniqueID, subjectUniqueID, extensions
/// }
/// ```
class DerReader {
  const DerReader._();

  /// Byte range of one DER TLV: `[start, end)`.
  static ({int start, int end})? elementRange(Uint8List der, int offset) {
    if (offset + 2 > der.length) return null;
    // Tag: one byte is enough for everything X.509 uses (all tags here are < 0x1F).
    var pos = offset + 1;
    var length = der[pos++];
    if (length & 0x80 != 0) {
      final lengthBytes = length & 0x7f;
      if (lengthBytes == 0 || lengthBytes > 4 || pos + lengthBytes > der.length) {
        return null;
      }
      length = 0;
      for (var i = 0; i < lengthBytes; i++) {
        length = (length << 8) | der[pos++];
      }
    }
    if (length < 0 || pos + length > der.length) return null;
    return (start: offset, end: pos + length);
  }

  /// Extracts the DER-encoded `SubjectPublicKeyInfo` from a certificate, or null if the
  /// certificate is not shaped the way [DerReader] expects.
  static Uint8List? subjectPublicKeyInfo(Uint8List certificateDer) {
    final certificate = elementRange(certificateDer, 0);
    if (certificate == null) return null;

    // Certificate is a SEQUENCE whose first child is the TBSCertificate SEQUENCE.
    final tbsStart = certificate.start + headerLength(certificateDer, certificate.start);
    final tbs = elementRange(certificateDer, tbsStart);
    if (tbs == null) return null;

    var offset = tbs.start + headerLength(certificateDer, tbs.start);
    // Skip version, serialNumber, signature, issuer, validity, subject.
    for (var i = 0; i < 6; i++) {
      final next = elementRange(certificateDer, offset);
      if (next == null) return null;
      offset = next.end;
    }
    final spki = elementRange(certificateDer, offset);
    if (spki == null) return null;
    return Uint8List.sublistView(certificateDer, spki.start, spki.end);
  }

  /// Number of bytes the TLV at [offset] spends on tag + length.
  static int headerLength(Uint8List der, int offset) {
    var pos = offset + 1;
    final first = der[pos++];
    if (first & 0x80 == 0) return pos - offset;
    final lengthBytes = first & 0x7f;
    if (lengthBytes == 0 || lengthBytes > 4 || pos + lengthBytes > der.length) {
      return pos - offset;
    }
    return (pos - offset) + lengthBytes;
  }
}

/// A TLS client that only talks to agents whose certificate SPKI matches a known pin.
///
/// PROTOCOL.md §2.1: the agent uses a self-signed ECDSA P-256 certificate, so the usual
/// CA validation can never succeed and the pin is the whole of the identity check. The
/// agent's pin arrives out of band in the pairing QR, which is why it — not
/// `GET /handshake` — is authoritative.
class PinnedHttpClient {
  PinnedHttpClient({
    required this.pins,
    this.onCertificateSeen,
    this.connectTimeout = const Duration(seconds: 8),
  })  : assert(pins.isNotEmpty, 'refusing to build a client with no pins'),
        _http = HttpClient() {
    _http
      ..connectionTimeout = connectTimeout
      ..badCertificateCallback = _check;
    _http.userAgent = 'ESI-RemotePC/1.0 (Android)';
  }

  /// Accepted `base64(SHA-256(SPKI))` values. Two may be held at once so a key rotation
  /// does not brick a paired client.
  final List<String> pins;

  /// Called with the pin of every certificate that passed validation. Used to learn the
  /// rotated pin before it becomes the only one.
  final void Function(String spkiPin)? onCertificateSeen;

  final Duration connectTimeout;
  final HttpClient _http;

  final List<CertificatePinFailure> _failures = <CertificatePinFailure>[];
  List<CertificatePinFailure> get failures => List.unmodifiable(_failures);

  bool _check(X509Certificate certificate, String host, int port) {
    final presented = pinForCertificate(certificate);
    _failures.clear();
    if (presented != null && pins.contains(presented)) {
      onCertificateSeen?.call(presented);
      return true;
    }
    _failures.add(CertificatePinFailure(
      reason: presented == null
          ? 'The server\'s certificate could not be read on this device, so its key '
              'fingerprint cannot be checked.'
          : 'The server presented a TLS key that does not match the one this phone paired '
              'with. Refusing to connect rather than trusting an unverified key.',
      expected: pins,
      presented: presented,
    ));
    return false;
  }

  HttpClient get client => _http;

  void close({bool force = true}) => _http.close(force: force);
}

/// `base64(SHA-256(DER(SubjectPublicKeyInfo)))` for a presented certificate.
String? pinForCertificate(X509Certificate certificate) {
  final spki = DerReader.subjectPublicKeyInfo(certificate.der);
  if (spki == null) return null;
  return base64Encode(sha256.convert(spki).bytes);
}

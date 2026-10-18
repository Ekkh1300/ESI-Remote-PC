import 'dart:convert';

import '../net/wol.dart';
import 'esi_crypto.dart';
import 'json_read.dart';
import 'protocol_constants.dart';

/// The contents of the pairing QR code shown on the PC.
///
/// PROTOCOL.md §5.4 and §9.3 say the QR carries the address, the device id, the TLS pin and
/// the one-time provisioning secret, but no code in the agent emits it, so there is no
/// canonical encoding to match. The parser below is therefore deliberately liberal: it
/// accepts a URI, a bare query string or a JSON object, and looks for each field under
/// every name it could plausibly have been given. Anything it cannot find is reported by
/// name, so the UI can say which field is missing instead of "invalid QR".
///
/// The accepted shapes, tried in this order:
/// ```text
/// esiremotepc://pair?host=192.168.1.42&port=48220&deviceId=…&pin=…&secret=…
/// esiremotepc://pair/192.168.1.42?p=48220&k=…&s=…
/// {"host":"…","port":48220,"deviceId":"…","spkiSha256":"…","provisioningSecret":"…"}
/// host=192.168.1.42&port=48220&deviceId=…&pin=…&secret=…
/// 192.168.1.42
/// ```
class PairingQrPayload {
  const PairingQrPayload({
    required this.host,
    required this.port,
    required this.deviceId,
    required this.spkiPin,
    required this.provisioningSecret,
    required this.deviceName,
    required this.macAddress,
  });

  /// The address to connect to. May be empty if the QR only carried a pin.
  final String host;
  final int port;

  /// The agent's device id. A hint like any other: the id that actually matters is the one
  /// pairing returns, and that is what gets stored.
  final String deviceId;

  /// `base64(SHA-256(SPKI))`, the authoritative pin. PROTOCOL.md §2.1 is why this is what
  /// makes the QR worth scanning rather than trusting `GET /handshake`.
  final String spkiPin;

  /// The one-time 32-byte secret from §5.4, base64. Without it the agent will approve the
  /// pairing but never release the wrapped secrets.
  final String provisioningSecret;

  final String? deviceName;

  /// The MAC the agent should be woken on, per §9.3.
  final String? macAddress;

  bool get hasAddress => host.isNotEmpty;
  bool get hasPin => spkiPin.isNotEmpty;
  bool get hasSecret => provisioningSecretBytes.length == EsiProtocol.secretLength;

  bool get isComplete => hasAddress && hasPin && hasSecret;

  /// The bytes of [provisioningSecret], or an empty list when absent or malformed.
  List<int> get provisioningSecretBytes {
    if (provisioningSecret.isEmpty) return const <int>[];
    try {
      return EsiCrypto.fromBase64Url(provisioningSecret);
    } on FormatException {
      try {
        return EsiCrypto.fromBase64(provisioningSecret);
      } on FormatException {
        return const <int>[];
      }
    }
  }

  /// A short human summary for the confirmation sheet.
  String get summary {
    final parts = <String>[
      if (deviceName != null && deviceName!.isNotEmpty) deviceName!,
      if (hasAddress)
        port == EsiProtocol.defaultHttpsPort ? host : '$host:$port',
      if (!hasAddress) 'address not in the QR',
    ];
    return parts.join(' · ');
  }

  /// Human names of what still has to be typed in.
  List<String> get missingFields => <String>[
        if (!hasAddress) 'the PC address',
        if (!hasPin) 'the certificate pin',
        if (!hasSecret) 'the pairing secret',
      ];

  PairingQrPayload copyWith({
    String? host,
    int? port,
    String? spkiPin,
    String? provisioningSecret,
  }) =>
      PairingQrPayload(
        host: host ?? this.host,
        port: port ?? this.port,
        deviceId: deviceId,
        spkiPin: spkiPin ?? this.spkiPin,
        provisioningSecret: provisioningSecret ?? this.provisioningSecret,
        deviceName: deviceName,
        macAddress: macAddress,
      );
}

class QrParseResult {
  const QrParseResult({
    required this.payload,
    this.error,
  });

  /// The parsed payload, present even when some fields are missing so the UI can prefill
  /// what it does have and ask for the rest.
  final PairingQrPayload? payload;

  /// Set when the payload could not be read at all.
  final String? error;

  bool get isSuccess => payload != null;
}

/// What is decided off one camera decode (D88). The scanner used to make this call
/// silently, which is why a code that decoded but did not parse — or did not decode at
/// all — left the camera open with nothing said anywhere.
enum PairingScanDecision {
  /// A pairing payload the form can take.
  accept,

  /// Something decoded, and it is not a pairing code.
  reject,

  /// Nothing decodeable came out of the capture.
  unreadable,
}

/// Reads the pairing QR, tolerantly.
class PairingQr {
  const PairingQr._();

  /// Decides what one decoded barcode is. Pure: it is what the scanner's meter reads,
  /// and it is pinned so every branch of the camera screen says something.
  static PairingScanDecision read(String? rawValue) {
    if (rawValue == null) return PairingScanDecision.unreadable;
    if (parse(rawValue).isSuccess) return PairingScanDecision.accept;
    return PairingScanDecision.reject;
  }

  static const List<String> _hostKeys = <String>[
    'host',
    'h',
    'hostname',
    'ip',
    'address',
    'addr',
  ];
  static const List<String> _portKeys = <String>['port', 'p', 'httpsPort'];
  static const List<String> _deviceIdKeys = <String>[
    'deviceId',
    'device_id',
    'id',
    'agentDeviceId',
    'd',
  ];
  static const List<String> _pinKeys = <String>[
    'spkiSha256',
    'spki',
    'pin',
    'tlsPin',
    'fingerprint',
    'k',
  ];
  static const List<String> _secretKeys = <String>[
    'provisioningSecret',
    'provisioning_secret',
    'secret',
    'pairingSecret',
    's',
  ];
  static const List<String> _nameKeys = <String>['deviceName', 'name', 'hostName', 'n'];
  static const List<String> _macKeys = <String>['macAddress', 'mac', 'm'];

  static QrParseResult parse(String raw) {
    final text = raw.trim();
    if (text.isEmpty) {
      return const QrParseResult(payload: null, error: 'That QR code is empty.');
    }

    final Map<String, dynamic> fields;
    try {
      fields = _fieldsFrom(text);
    } on FormatException catch (e) {
      return QrParseResult(
        payload: null,
        error: 'That QR code is not in a shape this app understands — ${e.message}.',
      );
    }

    final host = _firstString(fields, _hostKeys);
    final pin = _normalisePin(_firstString(fields, _pinKeys));
    final secret = _firstString(fields, _secretKeys);
    final name = _firstString(fields, _nameKeys);

    if (host.isEmpty && pin.isEmpty && secret.isEmpty) {
      return const QrParseResult(
        payload: null,
        error: 'That QR code does not look like an ESI Remote PC pairing code. It has no '
            'address, certificate pin or pairing secret in it.',
      );
    }

    return QrParseResult(
      payload: PairingQrPayload(
        host: host,
        port: _firstInt(fields, _portKeys) ?? EsiProtocol.defaultHttpsPort,
        deviceId: _firstString(fields, _deviceIdKeys),
        spkiPin: pin,
        provisioningSecret: secret,
        deviceName: name.isEmpty ? null : name,
        macAddress: _normaliseMac(fields),
      ),
    );
  }

  static Map<String, dynamic> _fieldsFrom(String text) {
    if (text.startsWith('{')) {
      return asObject(jsonDecode(text));
    }

    if (text.contains('://')) {
      final uri = Uri.tryParse(text);
      if (uri == null) {
        throw const FormatException('the address is not a valid URI');
      }
      final fields = <String, dynamic>{...uri.queryParameters};
      // An explicit field always wins. `esiremotepc://pair?host=1.2.3.4` puts the word
      // "pair" in the authority, and reading that as the address would send the phone
      // somewhere that does not exist.
      if (_firstString(fields, _hostKeys).isEmpty && _looksLikeAuthority(uri.host)) {
        fields['host'] = uri.host;
        if (uri.port != 0) fields['port'] = uri.port;
      } else if (_firstString(fields, _hostKeys).isEmpty) {
        // `esiremotepc://pair/192.168.1.42?p=...` - the second shape in the doc comment above -
        // puts the address in the *path*, because the authority is taken by "pair". Only the
        // first segment is considered, and only when it is unmistakably an address, so this
        // cannot invent one. The comment used to promise that shape while the parser silently
        // ignored it.
        final segment = uri.pathSegments.isEmpty ? '' : uri.pathSegments.first;
        if (_looksLikeAddress(segment)) {
          fields['host'] = segment;
          if (uri.port != 0) fields['port'] = uri.port;
        }
      }
      return fields;
    }

    if (text.contains('=')) {
      return Uri.splitQueryString(text);
    }

    // A bare address, e.g. someone printed "192.168.1.42" as a QR.
    if (_looksLikeAddress(text)) return <String, dynamic>{'host': text};
    throw const FormatException('it is neither JSON, a URI, nor an address');
  }

  static bool _looksLikeAddress(String text) {
    final parts = text.split('.');
    if (parts.length != 4) return false;
    return parts.every((p) {
      final n = int.tryParse(p);
      return n != null && n >= 0 && n <= 255;
    });
  }

  /// The authority of `esiremotepc://192.168.1.42` is the address. The authority of
  /// `esiremotepc://pair` is a route name, not a host, and must not be read as one.
  static bool _looksLikeAuthority(String authority) =>
      _looksLikeAddress(authority) || authority.endsWith('.local');

  static String _firstString(Map<String, dynamic> fields, List<String> keys) {
    for (final key in keys) {
      final value = fields[key];
      if (value is String && value.trim().isNotEmpty) return value.trim();
    }
    return '';
  }

  static int? _firstInt(Map<String, dynamic> fields, List<String> keys) {
    for (final key in keys) {
      final value = asIntOrNull(fields[key]);
      if (value != null && value > 0 && value < 65536) return value;
    }
    return null;
  }

  static String? _normaliseMac(Map<String, dynamic> fields) {
    for (final key in _macKeys) {
      final value = fields[key];
      if (value is String && value.isNotEmpty) {
        return WakeOnLan.normaliseMac(value);
      }
    }
    return null;
  }

  /// Pins travel as base64, which can arrive URL-safe or unpadded. A fingerprint printed
  /// as hex is accepted too, because that is how people tend to copy them by hand.
  static String _normalisePin(String pin) {
    final trimmed = pin.trim();
    if (trimmed.isEmpty) return '';
    if (RegExp(r'^[0-9a-fA-F]+$').hasMatch(trimmed) &&
        trimmed.length.isEven &&
        trimmed.length >= 40) {
      final bytes = <int>[];
      for (var i = 0; i + 1 < trimmed.length; i += 2) {
        bytes.add(int.parse(trimmed.substring(i, i + 2), radix: 16));
      }
      return EsiCrypto.base64(bytes);
    }
    return trimmed;
  }
}

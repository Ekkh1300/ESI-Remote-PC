import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:http/http.dart' as http;
import 'package:http/io_client.dart';

import '../protocol/esi_crypto.dart';
import '../protocol/json_read.dart';
import '../protocol/models.dart';
import '../protocol/protocol_constants.dart';
import 'spki_pinning.dart';
/// The credentials the agent minted for this phone.
class EsiIdentity {
  const EsiIdentity({
    required this.deviceId,
    required this.keyId,
    required this.masterSecret,
  });

  final String deviceId;
  final int keyId;
  final Uint8List masterSecret;

  Uint8List get sessionKey => EsiCrypto.deriveSessionKey(masterSecret, deviceId, keyId);

  /// Applies a completed rotation: the new secret replaces the old one and the key
  /// generation moves forward. Nothing else has to change, because the session key is
  /// derived on demand.
  EsiIdentity rotateTo({required Uint8List masterSecret, required int keyId}) =>
      EsiIdentity(deviceId: deviceId, keyId: keyId, masterSecret: masterSecret);
}

/// A signed request that came back with an error envelope.
class EsiApiException implements Exception {
  const EsiApiException({
    required this.statusCode,
    required this.code,
    required this.message,
    this.requestId,
    this.details = const <String, String>{},
    this.cause,
  });

  final int statusCode;
  final String code;
  final String message;
  final String? requestId;
  final Map<String, String> details;
  final Object? cause;

  /// PROTOCOL.md §10: a 404 on a known path means the agent is older than the app, not
  /// that the file is missing. Callers degrade instead of showing an error.
  bool get isUnsupported =>
      code == EsiProtocol.errNotFound || code == EsiProtocol.errUnsupported;

  bool get isAuthFailure => const <String>{
        EsiProtocol.errUnauthorized,
        EsiProtocol.errSignature,
        EsiProtocol.errReplay,
        EsiProtocol.errTimestamp,
        EsiProtocol.errKeyId,
      }.contains(code);

  bool get isForbidden => const <String>{
        EsiProtocol.errForbiddenNetwork,
        EsiProtocol.errForbiddenScope,
        EsiProtocol.errPathNotAllowed,
      }.contains(code);

  bool get isRateLimited => code == EsiProtocol.errRateLimited;

  /// Short, human phrasing. The agent's message is usually already good, so it is reused
  /// rather than replaced with vaguer wording.
  String get displayMessage => message.trim().isNotEmpty
      ? message
      : 'The agent refused the request ($code).';

  @override
  String toString() => 'EsiApiException($statusCode $code): $message';
}

/// Thrown when the certificate pin did not match. Carries enough detail for the UI to
/// explain the failure instead of shrugging.
class EsiTlsException implements Exception {
  const EsiTlsException(this.failure);
  final CertificatePinFailure failure;
  @override
  String toString() => failure.reason;
}

/// A transport failure with no HTTP response: the PC is asleep, the Wi-Fi dropped, or the
/// agent died mid-request.
class EsiNetworkException implements Exception {
  const EsiNetworkException(this.message, {this.cause});
  final String message;
  final Object? cause;
  @override
  String toString() => 'EsiNetworkException: $message';
}

/// Everything needed to reach one agent: where it is and how to prove who we are.
class EsiEndpoint {
  const EsiEndpoint({
    required this.host,
    required this.port,
    required this.pins,
  });

  final String host;
  final int port;

  /// Current pin, and possibly the next one during a rotation window.
  final List<String> pins;

  /// `host:port`, always with the port spelled out. See [EsiProtocol.authority] for why
  /// the default port is not special-cased.
  String get authority => EsiProtocol.authority(host, port);

  Uri apiUri(String path, [String? rawQuery]) {
    final suffix = (rawQuery == null || rawQuery.isEmpty) ? path : '$path?$rawQuery';
    // Braces are required: `$EsiProtocol.apiBase` would interpolate the *identifier*,
    // not the value, and produce a URL like `https://hostesiprotocol.apibase/…`.
    return Uri.parse('https://$authority${EsiProtocol.apiBase}$suffix');
  }

  Uri wsUri() => Uri.parse('wss://$authority${EsiProtocol.webSocketPath}');

  /// The address as a human reads it, and as it appears in the pairing-page URL the app
  /// tells the user to open. The port is included for the same reason as in [authority]:
  /// the string is pasted into a URL, and a URL without a port means 443.
  String get displayAddress => authority;

  EsiEndpoint withPins(List<String> next) =>
      EsiEndpoint(host: host, port: port, pins: next);
}

/// The signed REST client.
///
/// Every request carries the six `X-ESI-*` headers from PROTOCOL.md §4.1. The canonical
/// target is built from the *raw* path and the *raw* query string this class itself
/// generated, so the bytes that get signed are byte-for-byte the bytes that get sent.
class EsiApi {
  EsiApi({
    required this.endpoint,
    required this.identity,
    PinnedHttpClient? pinnedClient,
    http.Client? httpClient,
    DateTime Function()? clock,
    String Function()? nonceSource,
  })  : _clock = clock ?? (() => DateTime.now().toUtc()),
        _nonceSource = nonceSource ?? EsiCrypto.newNonce,
        _ownsPinnedClient = pinnedClient == null && httpClient == null {
    if (httpClient != null) {
      _client = httpClient;
      _pinned = null;
    } else {
      _pinned = pinnedClient ??
          PinnedHttpClient(
            pins: endpoint.pins,
            onCertificateSeen: _onCertificateSeen,
          );
      // `IOClient` routes everything through the pinned `HttpClient`, which is what makes
      // the `badCertificateCallback` apply to REST requests at all. `http` 1.6 always
      // encodes a string body as UTF-8, matching the agent's charset contract.
      _client = IOClient(_pinned!.client);
    }
  }

  final EsiEndpoint endpoint;
  EsiIdentity identity;
  final DateTime Function() _clock;
  final String Function() _nonceSource;
  late final http.Client _client;
  late final PinnedHttpClient? _pinned;
  final bool _ownsPinnedClient;

  /// The pin of the certificate that actually answered, if it differed from the pinned
  /// set. Reported by the settings screen so a rotation is visible before it bites.
  String? lastSeenPin;

  void _onCertificateSeen(String pin) {
    if (!endpoint.pins.contains(pin)) lastSeenPin = pin;
  }

  void close() {
    _client.close();
    if (_ownsPinnedClient) _pinned?.close();
  }

  // ------------------------------------------------------------------ signing

  /// Builds the six signature headers for a request.
  ///
  /// [rawQuery] must be the exact query string that will be put on the wire and [body] the
  /// exact bytes. Nothing re-encodes either one, which is what the agent expects.
  Map<String, String> signHeaders({
    required String method,
    required String path,
    String rawQuery = '',
    Uint8List? body,
  }) {
    final effectiveBody = body ?? Uint8List(0);
    final timestamp = _clock().millisecondsSinceEpoch ~/ 1000;
    final nonce = _nonceSource();
    final contentSha256 = EsiCrypto.hashContent(effectiveBody);
    final canonical = EsiCrypto.buildCanonicalString(
      method: method,
      canonicalTarget: EsiCrypto.buildCanonicalTarget(path, rawQuery),
      timestamp: timestamp,
      nonce: nonce,
      contentSha256B64: contentSha256,
      deviceId: identity.deviceId,
      keyId: identity.keyId,
    );

    return <String, String>{
      EsiProtocol.headerDeviceId: identity.deviceId,
      EsiProtocol.headerKeyId: identity.keyId.toString(),
      EsiProtocol.headerTimestamp: timestamp.toString(),
      EsiProtocol.headerNonce: nonce,
      EsiProtocol.headerContentSha256: contentSha256,
      EsiProtocol.headerSignature: EsiCrypto.sign(identity.sessionKey, canonical),
    };
  }

  // ------------------------------------------------------------------ verbs

  /// [signed] is false only for the agent's pre-authentication surface (§3), which
  /// ignores signatures because no key exists yet.
  Future<Map<String, dynamic>> getJson(
    String path, {
    List<(String, String)>? query,
    bool signed = true,
  }) =>
      _send('GET', path, query: query, signed: signed);

  Future<Map<String, dynamic>> postJson(
    String path, {
    Object? body,
    bool signed = true,
  }) =>
      _send('POST', path, body: body, signed: signed);

  Future<Map<String, dynamic>> putJson(String path, {Object? body}) =>
      _send('PUT', path, body: body);

  Future<Map<String, dynamic>> deleteJson(String path, {List<(String, String)>? query}) =>
      _send('DELETE', path, query: query);

  /// Sends a request whose body is raw bytes rather than JSON. [signed] is false only for
  /// the ticket-authenticated upload, which the agent deliberately does not verify with a
  /// request signature.
  Future<Map<String, dynamic>> sendRaw({
    required String method,
    required String path,
    String rawQuery = '',
    Uint8List? body,
    String contentType = 'application/octet-stream',
    bool signed = true,
    Duration? timeout,
  }) =>
      _send(
        method,
        path,
        rawQuery: rawQuery,
        body: body,
        contentType: contentType,
        signed: signed,
        timeout: timeout,
      );

  /// The raw response bytes, for file downloads. Errors are still unwrapped into
  /// [EsiApiException].
  Future<http.Response> getBytes(
    String path, {
    List<(String, String)>? query,
    Duration? timeout,
  }) async {
    final response = await _perform(
      method: 'GET',
      path: path,
      rawQuery: buildQuery(query),
      body: null,
      contentType: null,
      signed: true,
      timeout: timeout ?? const Duration(minutes: 5),
    );
    _throwIfError(response);
    return response;
  }

  // ------------------------------------------------------------------ plumbing

  /// Builds a query string by hand rather than through [Uri], because the exact bytes
  /// signed have to be the exact bytes sent. Windows paths live in these values and must
  /// reach the agent as `C%3A%5CUsers%5C…`.
  static String buildQuery(List<(String, String)>? query) {
    if (query == null || query.isEmpty) return '';
    return query
        .map((e) =>
            '${Uri.encodeQueryComponent(e.$1)}=${Uri.encodeQueryComponent(e.$2)}')
        .join('&');
  }

  Future<Map<String, dynamic>> _send(
    String method,
    String path, {
    Object? body,
    List<(String, String)>? query,
    String rawQuery = '',
    String contentType = 'application/json; charset=utf-8',
    bool signed = true,
    Duration? timeout,
  }) async {
    final encoded = body == null
        ? Uint8List(0)
        : Uint8List.fromList(utf8.encode(jsonEncode(body)));
    final response = await _perform(
      method: method,
      path: path,
      rawQuery: query != null ? buildQuery(query) : rawQuery,
      body: encoded,
      contentType: contentType,
      signed: signed,
      timeout: timeout,
    );
    _throwIfError(response);
    return _decodeObject(response);
  }

  Future<http.Response> _perform({
    required String method,
    required String path,
    required String rawQuery,
    required Uint8List? body,
    required String? contentType,
    required bool signed,
    required Duration? timeout,
  }) async {
    final uri = endpoint.apiUri(path, rawQuery);
    final headers = <String, String>{};

    // The media type is declared whenever a body goes out, signed or not. It is not part
    // of what is signed - the agent covers method, target, timestamp, nonce, content hash
    // and identity, never Content-Type - so writing it first cannot invalidate a signature.
    //
    // It used to be written only on the unsigned branch, below the `if (signed)`. Every
    // signed request carrying a body therefore went out with no Content-Type at all, and
    // ASP.NET cannot bind a body it cannot identify: it answers 415 before the endpoint
    // runs. Launching an app, every power action, file writes, uploads, gaming mode, media
    // keys, settings saves and key rotation all failed as a media-type error while every
    // GET kept looking perfectly healthy - which is exactly why it hid.
    //
    // A bodyless request stays bare: there is no media type to declare.
    if (contentType != null && body != null && body.isNotEmpty) {
      headers['Content-Type'] = contentType;
    }

    if (signed) {
      // Signed over `uri.path` and `uri.query`, not over the `path` argument.
      //
      // `apiUri` puts EsiProtocol.apiBase (`/api/v1`) in front of the path it is given, so
      // the request that goes on the wire is `/api/v1/system/info` while the argument is
      // `/system/info`. The agent builds its canonical target from the raw path it received,
      // so signing the argument produced an HMAC over a different string and every signed
      // request came back `esi_signature`. Deriving both from the URI that is actually sent
      // is what makes the rule "sign exactly what you send" true by construction rather than
      // by remembering.
      headers.addAll(signHeaders(
        method: method,
        path: uri.path,
        rawQuery: uri.query,
        body: body,
      ));
    }

    final request = http.Request(method, uri)
      ..headers.addAll(headers)
      ..followRedirects = false;
    if (body != null && body.isNotEmpty) request.bodyBytes = body;

    try {
      final streamed =
          await _client.send(request).timeout(timeout ?? const Duration(seconds: 20));
      return await http.Response.fromStream(streamed);
    } on CertificatePinFailure catch (e) {
      throw EsiTlsException(e);
    } on HandshakeException catch (e) {
      // badCertificateCallback returned false, so dart:io reports the handshake failure
      // here. The pin we recorded on the way past is the actionable detail.
      throw EsiTlsException(_pinned?.failures.lastOrNull ??
          CertificatePinFailure(
            reason: 'The TLS handshake with ${endpoint.displayAddress} failed: ${e.message}',
            expected: endpoint.pins,
            presented: null,
          ));
    } on TimeoutException catch (e) {
      throw EsiNetworkException(
        '${endpoint.displayAddress} did not answer in time. It may be asleep or off the '
        'network.',
        cause: e,
      );
    } on SocketException catch (e) {
      throw EsiNetworkException(
        'Could not reach ${endpoint.displayAddress}: ${e.osError?.message ?? e.message}',
        cause: e,
      );
    } on http.ClientException catch (e) {
      throw EsiNetworkException(
        'The connection to ${endpoint.displayAddress} failed: ${e.message}',
        cause: e,
      );
    }
  }

  void _throwIfError(http.Response response) {
    if (response.statusCode < 400) return;
    Map<String, dynamic> decoded;
    try {
      decoded = asObject(jsonDecode(utf8.decode(response.bodyBytes, allowMalformed: true)));
    } on FormatException {
      throw EsiApiException(
        statusCode: response.statusCode,
        code: EsiProtocol.errInternal,
        message: 'The agent returned a ${response.statusCode} response that was not a '
            'protocol error envelope.',
      );
    }
    final body = EsiApiErrorBody.fromJson(asObject(decoded['error']));
    throw EsiApiException(
      statusCode: response.statusCode,
      code: body.code,
      message: body.message,
      requestId: body.requestId,
      details: body.details,
    );
  }

  static Map<String, dynamic> _decodeObject(http.Response response) {
    if (response.bodyBytes.isEmpty) return <String, dynamic>{};
    try {
      return asObject(jsonDecode(utf8.decode(response.bodyBytes)));
    } on FormatException catch (e) {
      throw EsiApiException(
        statusCode: response.statusCode,
        code: EsiProtocol.errInternal,
        message: 'The agent returned a body that is not valid JSON.',
        cause: e,
      );
    }
  }
}

extension _LastOrNull<T> on List<T> {
  T? get lastOrNull => isEmpty ? null : last;
}

/// `GET /api/v1/handshake`, the one unauthenticated call that says who the agent is.
///
/// PROTOCOL.md §2.1 is explicit that a MITM could write anything it likes into this
/// response, pin included, which is exactly why the pairing QR is the authoritative source
/// of the pin. So this function exists to *confirm an address* and to *show the user what
/// the PC says its pin is* so they can compare it with the PC's own pairing screen — the
/// pin it returns is always presented as unverified.
///
/// The TLS check here is trust-on-first-use rather than a pin check, because at the moment
/// of first contact there is nothing to check against.
Future<Handshake?> readHandshakeUnpinned(EsiEndpoint endpoint) async {
  final client = HttpClient()
    ..connectionTimeout = const Duration(seconds: 6)
    ..badCertificateCallback = (cert, host, port) => true;
  try {
    final request = await client.getUrl(endpoint.apiUri('/handshake'));
    final response = await request.close().timeout(const Duration(seconds: 6));
    if (response.statusCode != 200) return null;
    final body = await response.transform(utf8.decoder).join();
    final json = tryDecodeObject(body);
    if (json.isEmpty) return null;
    return Handshake.fromJson(json);
  } on Object {
    return null;
  } finally {
    client.close(force: true);
  }
}

import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:web_socket_channel/web_socket_channel.dart';
import 'package:web_socket_channel/io.dart';

import '../protocol/json_read.dart';
import '../protocol/models.dart';
import '../protocol/protocol_constants.dart';
import 'esi_api.dart';
import 'spki_pinning.dart';

/// Connection state of the live socket, surfaced in the app's status spine.
enum EsiSocketState { idle, connecting, live, reconnecting, failed }

/// The signed `/ws` connection (PROTOCOL.md §7).
///
/// Deviation from the document, forced by the agent's implementation: §7.1 says the
/// signature headers are passed as *query parameters* so they never land in a URL that an
/// intermediary might log. The agent does not implement that — `SignatureAuthentication-
/// Middleware` reads the `X-ESI-*` headers and nothing else (`RequestVerifier.cs`), and
/// `/ws` is not on the public list, so the upgrade is verified as an ordinary signed
/// request over path `/ws` with an empty body. `WebSocket.connect` lets us send headers on
/// the upgrade, so the signature is still never in a URL. See the README.
class EsiWebSocket implements EsiWebSocketHandle {
  EsiWebSocket({
    required this.endpoint,
    required this.identity,
    PinnedHttpClient? pinnedClient,
    this.keepAlive = const Duration(seconds: 20),
  }) : _pinned = pinnedClient ??
            PinnedHttpClient(pins: endpoint.pins);

  final EsiEndpoint endpoint;

  /// Mutable: a key rotation swaps the identity underneath the live socket.
  EsiIdentity identity;

  final PinnedHttpClient _pinned;
  final Duration keepAlive;

  WebSocketChannel? _channel;
  StreamSubscription<Object?>? _subscription;
  Timer? _pingTimer;
  Timer? _reconnectTimer;
  int _reconnectAttempt = 0;
  bool _disposed = false;
  bool _connecting = false;
  EsiIdentity? _connectedAs;

  final _frames = StreamController<EsiWsFrame>.broadcast();
  final _binary = StreamController<Uint8List>.broadcast();
  final _stateChanges = StreamController<EsiSocketState>.broadcast();

  /// Decoded control frames: hello, metrics, notification, event, gaming, error, …
  Stream<EsiWsFrame> get frames => _frames.stream;

  /// Binary screen-stream frames (§7.5). Only arrives while `remote.start` is active.
  Stream<Uint8List> get binary => _binary.stream;

  Stream<EsiSocketState> get stateChanges => _stateChanges.stream;

  EsiSocketState _state = EsiSocketState.idle;
  EsiSocketState get state => _state;

  bool get isLive => _state == EsiSocketState.live;

  /// Subscribes to the channels the dashboard and notification screens need.
  Future<void> connect({List<String> channels = const ['metrics', 'notifications', 'events']}) async {
    if (_disposed) return;
    if (_connecting || isLive) return;
    _connecting = true;
    _setState(EsiSocketState.connecting);

    // The upgrade is signed as an ordinary request for path `/ws` with an empty body.
    final headers = _signatureHeaders();
    final uri = endpoint.wsUri();

    try {
      final channel = IOWebSocketChannel.connect(
        uri,
        headers: headers,
        customClient: _pinned.client,
        connectTimeout: const Duration(seconds: 8),
      );
      // `connect` is lazy, so the handshake only happens when the channel is awaited.
      await channel.ready;
      if (_disposed) {
        await channel.sink.close();
        return;
      }
      _channel = channel;
      _connectedAs = identity;
      _reconnectAttempt = 0;
      _subscription = channel.stream.listen(
        _onMessage,
        onError: (Object error, StackTrace stack) => _onFailure(error),
        onDone: _onDone,
        cancelOnError: false,
      );
      _setState(EsiSocketState.live);
      _startPing();
      if (channels.isNotEmpty) {
        send(EsiWsFrame.subscribe('s${DateTime.now().microsecondsSinceEpoch}', channels));
      }
    } on CertificatePinFailure catch (e) {
      _connecting = false;
      _setState(EsiSocketState.failed);
      _frames.add(EsiWsFrame.error(
        id: null,
        code: EsiProtocol.errSignature,
        message: e.reason,
      ));
    } on Object catch (error) {
      _connecting = false;
      _setState(EsiSocketState.failed);
      _frames.add(EsiWsFrame.error(
        id: null,
        code: EsiProtocol.errSignature,
        message: 'Could not open the live stream: $error',
      ));
    } finally {
      _connecting = false;
    }
  }

  Map<String, String> _signatureHeaders() {
    final api = EsiApi(
      endpoint: endpoint,
      identity: identity,
      pinnedClient: _pinned,
    );
    return api.signHeaders(
      method: 'GET',
      path: EsiProtocol.webSocketPath,
    );
  }

  void _startPing() {
    _pingTimer?.cancel();
    // §7.6: the agent closes the socket after 45 s without a client frame.
    _pingTimer = Timer.periodic(keepAlive, (_) {
      if (_state == EsiSocketState.live) {
        send(EsiWsFrame.ping('p${DateTime.now().microsecondsSinceEpoch}'));
      }
    });
  }

  void _onMessage(Object? message) {
    if (message is Uint8List) {
      if (!_binary.isClosed) _binary.add(message);
      return;
    }
    if (message is!String) return;
    final frame = EsiWsFrame.parse(message);
    // A frame with no type is something this app does not understand; PROTOCOL.md §10 says
    // to ignore it rather than treat it as an error.
    if (frame.type.isEmpty) return;
    if (!_frames.isClosed) _frames.add(frame);
  }

  void _onFailure(Object error) {
    if (_disposed) return;
    _teardown();
    _setState(EsiSocketState.failed);
    if (!_frames.isClosed) {
      _frames.add(EsiWsFrame.error(
        id: null,
        code: EsiProtocol.errInternal,
        message: 'The live stream dropped: $error',
      ));
    }
    _scheduleReconnect();
  }

  void _onDone() {
    if (_disposed) return;
    _teardown();
    _scheduleReconnect();
  }

  void _scheduleReconnect() {
    if (_disposed) return;
    _reconnectAttempt++;
    _setState(EsiSocketState.reconnecting);

    // D47: this used to stop after six attempts and set `failed`. Six attempts is
    // 1+2+4+8+16+20 seconds, so any outage longer than about fifty seconds - a service restart,
    // a PC reboot, a Wi-Fi blip - left the socket dead for the rest of the session.
    //
    // What made it permanent rather than merely slow is that nothing else could revive it. The
    // dashboard polls over HTTP on its own timer, so the app kept looking healthy: metrics kept
    // arriving, the status line kept updating, and the only symptom was that every streaming
    // control stayed disabled next to the words "no live socket". The one component whose whole
    // job is to survive a PC being off was the one that gave up first, and the part of the app
    // that had a working retry loop is the part that did not need one.
    //
    // So there is no attempt limit. The delay stays capped, the loop is otherwise unbounded, and
    // `failed` now means "not connected right now" rather than "will never connect".
    final delaySeconds = (1 << (_reconnectAttempt - 1).clamp(0, 4)).clamp(1, 20);
    _reconnectTimer?.cancel();
    _reconnectTimer = Timer(Duration(seconds: delaySeconds), connect);
  }

  /// Drops the accumulated backoff and tries again immediately.
  ///
  /// The automatic loop waits up to twenty seconds, which is right for a PC that is rebooting
  /// and wrong for someone standing there watching. This is what a retry button calls.
  void retryNow() {
    if (_disposed) return;
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
    _reconnectAttempt = 0;
    if (_connecting || isLive) return;
    connect();
  }

  void _teardown() {
    _pingTimer?.cancel();
    _pingTimer = null;
    _subscription?.cancel();
    _subscription = null;
    final channel = _channel;
    _channel = null;
    _connectedAs = null;
    channel?.sink.close();
  }

  void _setState(EsiSocketState next) {
    if (_state == next) return;
    _state = next;
    if (!_stateChanges.isClosed) _stateChanges.add(next);
  }

  void send(EsiWsFrame frame) {
    final channel = _channel;
    if (channel == null || _state != EsiSocketState.live) return;
    channel.sink.add(frame.encode());
  }

  // ------------------------------------------------------------------ remote control
  // The remote screen drives the stream through this narrow surface rather than holding a
  // reference to the socket, so it cannot accidentally send metrics or control frames.

  @override
  void startRemote({
    required int displayId,
    required int fps,
    required int quality,
    List<int> grid = const <int>[4, 4],
  }) {
    send(EsiWsFrame.remoteStart(
      id: 'r${DateTime.now().microsecondsSinceEpoch}',
      displayId: displayId,
      fps: fps,
      quality: quality,
      grid: grid,
    ));
  }

  @override
  void stopRemote() {
    send(EsiWsFrame.remoteStop('r${DateTime.now().microsecondsSinceEpoch}'));
  }

  @override
  void sendInput(Map<String, dynamic> input) {
    send(EsiWsFrame.remoteInput(
      'i${DateTime.now().microsecondsSinceEpoch}',
      input,
    ));
  }

  @override
  void sendKey({
    required String code,
    required bool down,
    String? text,
    List<String> mods = const <String>[],
  }) {
    send(EsiWsFrame.remoteKey(
      id: 'k${DateTime.now().microsecondsSinceEpoch}',
      code: code,
      down: down,
      text: text,
      mods: mods,
    ));
  }

  void sendRaw(Object payload) {
    final channel = _channel;
    if (channel == null || _state != EsiSocketState.live) return;
    if (payload is String) {
      channel.sink.add(payload);
    } else if (payload is Uint8List) {
      channel.sink.add(payload);
    } else if (payload is List<int>) {
      channel.sink.add(Uint8List.fromList(payload));
    }
  }

  /// The identity in force when the socket opened. A rotation that lands mid-stream means
  /// this no longer matches [identity] and the socket has to be recycled.
  bool get isStale => _connectedAs != null && _connectedAs!.keyId != identity.keyId;

  /// Forces a fresh upgrade, which is what a key rotation needs.
  Future<void> recycle() async {
    _reconnectAttempt = 0;
    _reconnectTimer?.cancel();
    _teardown();
    _setState(EsiSocketState.idle);
    await connect();
  }

  Future<void> dispose() async {
    _disposed = true;
    _reconnectTimer?.cancel();
    _teardown();
    _pinned.close();
    await _frames.close();
    await _binary.close();
    await _stateChanges.close();
  }
}

/// Implemented by [EsiWebSocket]; declared here so the state layer can hold a narrow
/// reference without importing the socket's whole surface.
abstract class EsiWebSocketHandle {
  void startRemote({
    required int displayId,
    required int fps,
    required int quality,
    List<int> grid,
  });

  void stopRemote();
  void sendInput(Map<String, dynamic> input);
  void sendKey({
    required String code,
    required bool down,
    String? text,
    List<String> mods,
  });
}

/// Typed convenience wrappers over the raw frame JSON, so screens do not hand-build maps.
extension EsiWsStream on Stream<EsiWsFrame> {
  Stream<MetricsSnapshot> get metricsSnapshots => where((f) => f.type == 'metrics').map(
        (f) => MetricsSnapshot.fromJson(f.data ?? const <String, dynamic>{}),
      );

  Stream<EsiNotification> get notifications => where((f) => f.type == 'notification').map(
        (f) => EsiNotification.fromJson(f.data ?? const <String, dynamic>{}),
      );

  Stream<AgentEvent> get events => where((f) => f.type == 'event').map(
        (f) => AgentEvent.fromJson(f.data ?? const <String, dynamic>{}),
      );

  Stream<Handshake> get hello => where((f) => f.type == 'hello').map(
        (f) => Handshake.fromJson(f.data ?? const <String, dynamic>{}),
      );

  Stream<GamingState> get gamingStates => where((f) => f.type == 'gaming').map(
        (f) => GamingState.fromJson(f.data ?? const <String, dynamic>{}),
      );

  Stream<EsiWsError> get errors =>
      where((f) => f.type == 'error').map(EsiWsError.fromFrame);
}

/// A `{"kind": "...", "data": {...}}` agent event (§7.5).
class AgentEvent {
  const AgentEvent({required this.kind, required this.data});
  factory AgentEvent.fromJson(Map<String, dynamic> json) => AgentEvent(
        kind: json['kind']?.toString() ?? '',
        data: asObject(json['data']),
      );

  final String kind;
  final Map<String, dynamic> data;
}

/// An `{"t":"error","code":...,"message":...}` frame.
class EsiWsError {
  const EsiWsError({required this.id, required this.code, required this.message});

  factory EsiWsError.fromFrame(EsiWsFrame frame) =>
      EsiWsError(id: frame.id, code: frame.code ?? '', message: frame.message ?? '');

  final String? id;
  final String code;
  final String message;
}

/// Encodes/decodes the JSON control frames of §7.2 and §7.5.
class EsiWsFrame {
  const EsiWsFrame({
    required this.p,
    required this.type,
    this.id,
    this.data,
    this.code,
    this.message,
  });

  factory EsiWsFrame.parse(String raw) {
    try {
      final decoded = jsonDecode(raw);
      if (decoded is! Map) return const EsiWsFrame(p: EsiProtocol.version, type: '');
      final map = decoded.cast<String, Object?>();
      return EsiWsFrame(
        p: map['p'] is int ? map['p']! as int : EsiProtocol.version,
        type: map['t']?.toString() ?? '',
        id: map['id']?.toString(),
        data: map['data'] is Map
            ? (map['data']! as Map).cast<String, dynamic>()
            : (map['data'] == null ? null : <String, dynamic>{'value': map['data']}),
        code: map['code']?.toString(),
        message: map['message']?.toString(),
      );
    } on FormatException {
      return const EsiWsFrame(p: EsiProtocol.version, type: '');
    }
  }

  final int p;
  final String type;
  final String? id;
  final Map<String, dynamic>? data;
  final String? code;
  final String? message;

  String encode() => jsonEncode(<String, dynamic>{
        'p': p,
        't': type,
        if (id != null) 'id': id,
        if (data != null) 'data': data,
      });

  static EsiWsFrame subscribe(String id, List<String> channels) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'subscribe', id: id)
          ._withChannels(channels);

  static EsiWsFrame unsubscribe(String id, List<String> channels) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'unsubscribe', id: id)
          ._withChannels(channels);

  static EsiWsFrame ping(String id) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'ping', id: id);

  static EsiWsFrame error({required String? id, required String code, required String message}) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'error', id: id, code: code, message: message);

  static EsiWsFrame remoteStart({
    required String id,
    int displayId = 0,
    int fps = 15,
    int quality = 65,
    List<int> grid = const [4, 4],
    bool inputEnabled = true,
  }) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'remote.start', id: id)
          ._withRemote(displayId, fps, quality, grid, inputEnabled);

  static EsiWsFrame remoteStop(String id) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'remote.stop', id: id);

  /// Asks the agent to push `gaming/state` on the gaming channel at [intervalMs].
  static EsiWsFrame gamingWatch(String id, {int intervalMs = 1000}) => EsiWsFrame(
        p: EsiProtocol.version,
        type: 'gaming.watch',
        id: id,
        data: <String, dynamic>{'intervalMs': intervalMs},
      );

  static EsiWsFrame remoteInput(String id, Map<String, dynamic> input) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'remote.input', id: id, data: input);

  static EsiWsFrame remoteKey({
    required String id,
    required String code,
    required bool down,
    List<String> mods = const <String>[],
    String? text,
  }) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'remote.key', id: id)
          ._withKey(code, down, mods, text);

  EsiWsFrame _withChannels(List<String> channels) => EsiWsFrame(
        p: p,
        type: type,
        id: id,
        data: <String, dynamic>{'channels': channels},
      );

  EsiWsFrame _withRemote(
    int displayId,
    int fps,
    int quality,
    List<int> grid,
    bool inputEnabled,
  ) =>
      EsiWsFrame(
        p: p,
        type: type,
        id: id,
        data: <String, dynamic>{
          'displayId': displayId,
          'fps': fps,
          'quality': quality,
          'grid': grid,
          'inputEnabled': inputEnabled,
        },
      );

  EsiWsFrame _withKey(
    String code,
    bool down,
    List<String> mods,
    String? text,
  ) =>
      EsiWsFrame(
        p: p,
        type: type,
        id: id,
        data: <String, dynamic>{
          'code': code,
          'down': down,
          'mods': mods,
          'text': ?text,
        },
      );
}

/// Kept for readability at call sites: the envelope of §5.4 is a plain base64 AES-GCM
/// triple, identical in shape on the pairing and rotation paths.
typedef EsiWrappedEnvelope = ({List<int> nonce, List<int> ciphertext, List<int> tag});

/// The fixed 32-byte header that prefixes every binary screen frame (PROTOCOL.md §7.5).
class ScreenFrameHeader {
  const ScreenFrameHeader({
    required this.frameType,
    required this.displayId,
    required this.frameIndex,
    required this.tileCount,
    required this.codec,
    required this.screenWidth,
    required this.screenHeight,
    required this.gridCols,
    required this.gridRows,
    required this.payloadLength,
  });

  static const int size = 32;
  static const int expectedMagic = 0x56495345; // 'E','S','I','V' little-endian

  static const int typeKeyframe = 1;
  static const int typeTileUpdate = 2;
  static const int typeResyncRequest = 3;

  static const int codecJpeg = 1;

  factory ScreenFrameHeader.parse(Uint8List bytes) {
    if (bytes.length < size) {
      throw const FormatException('Screen frame header is shorter than 32 bytes.');
    }
    final view = ByteData.sublistView(bytes);
    final magic = view.getUint32(0, Endian.little);
    if (magic != expectedMagic) {
      throw FormatException('Screen frame has magic 0x${magic.toRadixString(16)}.');
    }
    return ScreenFrameHeader(
      frameType: view.getUint16(4, Endian.little),
      displayId: view.getUint16(6, Endian.little),
      frameIndex: view.getUint32(8, Endian.little),
      tileCount: view.getUint16(12, Endian.little),
      codec: view.getUint16(14, Endian.little),
      screenWidth: view.getInt32(16, Endian.little),
      screenHeight: view.getInt32(20, Endian.little),
      gridCols: view.getUint16(24, Endian.little),
      gridRows: view.getUint16(26, Endian.little),
      payloadLength: view.getUint32(28, Endian.little),
    );
  }

  final int frameType;
  final int displayId;
  final int frameIndex;
  final int tileCount;
  final int codec;
  final int screenWidth;
  final int screenHeight;
  final int gridCols;
  final int gridRows;
  final int payloadLength;
}

/// One tile descriptor: `col, row, tileWidth, tileHeight, tileLength`.
class ScreenTile {
  const ScreenTile({
    required this.col,
    required this.row,
    required this.width,
    required this.height,
    required this.jpeg,
  });

  final int col;
  final int row;
  final int width;
  final int height;
  final Uint8List jpeg;
}

/// Splits a binary screen frame into its tiles. Returns empty for a resync request, which
/// carries an index rather than image data.
List<ScreenTile> parseScreenTiles(Uint8List frame) {
  final header = ScreenFrameHeader.parse(frame);
  if (header.frameType == ScreenFrameHeader.typeResyncRequest) {
    return const <ScreenTile>[];
  }
  final tiles = <ScreenTile>[];
  final view = ByteData.sublistView(frame);

  // Two passes over one descriptor table (D46).
  //
  // The frame is laid out as header, then ALL the tile descriptors, then ALL the tile payloads
  // back to back. The old loop advanced a single `offset` through both, so after the first tile
  // it resumed at 32 + 14 + len - which is inside the second tile's JPEG rather than at its
  // descriptor. Every subsequent read landed at the wrong place, and the slice handed to
  // android.graphics.ImageDecoder started mid-picture, which it reports as
  // "Input contained an error".
  //
  // So: read every descriptor first, then walk the payload region once. The descriptor count is
  // in the header, so the payload region starts at a known offset and the two cannot drift.
  final descriptors = <({int col, int row, int width, int height, int length})>[];
  var descriptorOffset = ScreenFrameHeader.size;

  for (var i = 0; i < header.tileCount; i++) {
    if (descriptorOffset + 14 > frame.length) break;
    descriptors.add((
      col: view.getUint16(descriptorOffset, Endian.little),
      row: view.getUint16(descriptorOffset + 2, Endian.little),
      width: view.getUint16(descriptorOffset + 4, Endian.little),
      height: view.getUint16(descriptorOffset + 6, Endian.little),
      length: view.getUint32(descriptorOffset + 8, Endian.little),
    ));
    descriptorOffset += 14;
  }

  var payloadOffset = descriptorOffset;
  for (final d in descriptors) {
    if (payloadOffset + d.length > frame.length) break;
    tiles.add(ScreenTile(
      col: d.col,
      row: d.row,
      width: d.width,
      height: d.height,
      jpeg: Uint8List.sublistView(frame, payloadOffset, payloadOffset + d.length),
    ));
    payloadOffset += d.length;
  }
  return tiles;
}

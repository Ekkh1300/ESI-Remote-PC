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
  void startAudio(String requestId, {bool silenceLocal = false}) {
    send(EsiWsFrame.audioStart(requestId, silenceLocal: silenceLocal));
  }

  @override
  void stopAudio() {
    send(EsiWsFrame.audioStop(EsiWsFrame.newAudioRequestId()));
  }

  @override
  Stream<AudioState> audioStateFor(String requestId) =>
      _frames.stream.audioStateFor(requestId);

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

  /// Asks the PC to start streaming its own audio to this phone.
  ///
  /// Takes the id so the answer to *this* request can be listened for before it is written
  /// (the reply can arrive on the next turn of the event loop) and picked out from the one
  /// still in flight for a `stop` the user just made - see [audioStateFor].
  ///
  /// [silenceLocal] is the D87 request: the PC's own speakers are silenced while the phone
  /// carries the sound, and restored when the stream ends. Sent only when the user asked for
  /// it, so an agent too old to know it simply captures without silencing - the flag rides
  /// the frame at the top level, exactly where the PC reads it from.
  void startAudio(String requestId, {bool silenceLocal = false});

  /// Stops the PC streaming its audio.
  ///
  /// Carries no id because nobody waits for its answer: stopping is fire-and-forget here,
  /// unlike [startAudio], where "the PC said no" is the whole point.
  void stopAudio();

  /// The PC's answer to the `audio.start` sent with [requestId].
  ///
  /// On the handle rather than reached through the frame stream, so the state layer can wait
  /// for one without importing the socket's whole surface. Without it the phone cannot tell
  /// "the PC is capturing" from "the PC said no and there is nothing to hear".
  Stream<AudioState> audioStateFor(String requestId);
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

  /// Whether the PC accepted the request to capture its own audio (§7.2).
  ///
  /// `active` is the PC's answer about *delivering* the request, not a promise that sound
  /// will follow - WASAPI opens asynchronously and can refuse afterwards. It is still worth
  /// waiting for, because a refusal the PC does report is the difference between "the
  /// session agent is not running" and a switch that sits on over silence with nothing
  /// anywhere saying why.
  /// Whether the PC accepted the request to capture its own audio (§7.2).
  ///
  /// `active` is the PC's answer about *delivering* the request, not a promise that sound
  /// will follow - WASAPI opens asynchronously and can refuse afterwards. It is still worth
  /// waiting for, because a refusal the PC does report is the difference between "the
  /// session agent is not running" and a switch that sits on over silence with nothing
  /// anywhere saying why.
  ///
  /// There is deliberately no unqualified "every `audio.state`" getter beside it. The PC
  /// answers `audio.stop` on the same type with `active: false`, so the unqualified reading
  /// is the one that turns a quick Stop-then-Play into a false refusal - and the id filter
  /// below is only load-bearing while a caller can still reach the thing it protects
  /// against.
  Stream<AudioState> audioStateFor(String requestId) =>
      where((f) => f.type == 'audio.state' && (f.id == requestId || f.id == null)).map(
        (f) => AudioState.fromJson(f.data ?? const <String, dynamic>{}),
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
    this.text,
    this.down,
    this.mods,
    this.input,
    this.remote,
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
        text: map['text']?.toString(),
        down: map['down'] is bool ? map['down']! as bool : null,
        mods: map['mods'] is List
            ? (map['mods']! as List).map((e) => e.toString()).toList()
            : null,
        remote: map['remote'] is Map
            ? (map['remote']! as Map).cast<String, dynamic>()
            : null,
        input: map['input'] is Map
            ? (map['input']! as Map).cast<String, dynamic>()
            : null,
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

  /// D51: these four were never modelled, so the only way to send a key was to smuggle the
  /// fields inside `data` - where the server does not look for them. PROTOCOL.md §7.2 puts
  /// them at the top level, and WsClientFrame reads them from there.
  final String? text;
  final bool? down;
  final List<String>? mods;
  final Map<String, dynamic>? input;

  /// D51: `remote.start` carries its parameters at the top level, not in one blob.
  final Map<String, dynamic>? remote;

  String encode() => jsonEncode(<String, dynamic>{
        'p': p,
        't': type,
        if (id != null) 'id': id,
        // D51: this dropped everything except p/t/id/data. That is why `code` was always
        // null on the server no matter what the phone put in it - the field was serialised
        // into a `data` map and the encoder, which never looked at that map for key fields,
        // was the only place it survived. PROTOCOL.md §7.2 puts all of these at the top
        // level, and WsClientFrame reads them from there.
        if (code != null) 'code': code,
        'text': ?text,
        'down': ?down,
        'mods': ?mods,
        if (input != null) 'input': input,
        ...?remote,
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
          ._withRemote(<String, dynamic>{
        'displayId': displayId,
        'fps': fps,
        'quality': quality,
        'grid': grid,
        'inputEnabled': inputEnabled,
      });

  static EsiWsFrame remoteStop(String id) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'remote.stop', id: id);

  /// Asks the agent to open the PC's output device for loopback capture, so the phone can
  /// play what the PC is playing (PROTOCOL.md §7.2).
  ///
  /// [silenceLocal] rides the frame at the **top level**, not in `data` (D51's rule): WsClientFrame
  /// reads `silenceLocal` as a field of its own. Only sent when true - absent means "no
  /// silencing" to every agent, old or new, so a phone with the toggle off is byte-for-byte
  /// what it always was. The reply is an `audio.state` frame saying whether the *request*
  /// was accepted - the device opens asynchronously and can still refuse afterwards.
  static EsiWsFrame audioStart(String id, {bool silenceLocal = false}) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'audio.start', id: id)
          ._withRemote(<String, dynamic>{
        if (silenceLocal) 'silenceLocal': true,
      });

  /// Stops the loopback capture. The counterpart to [audioStart].
  static EsiWsFrame audioStop(String id) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'audio.stop', id: id);

  /// A fresh id for one `audio.start`, in the shape every `audio.*` frame uses.
  ///
  /// Generated by the *caller* rather than chosen inside `startAudio`, because the caller has
  /// to be listening for the answer before the request reaches the wire, and it cannot filter
  /// for an id it does not have yet. Doing it here keeps the `a<microseconds>` convention in
  /// one place instead of splitting it between the socket and the state layer.
  static String newAudioRequestId() => 'a${DateTime.now().microsecondsSinceEpoch}';

  /// Asks the agent to push `gaming/state` on the gaming channel at [intervalMs].
  static EsiWsFrame gamingWatch(String id, {int intervalMs = 1000}) => EsiWsFrame(
        p: EsiProtocol.version,
        type: 'gaming.watch',
        id: id,
        data: <String, dynamic>{'intervalMs': intervalMs},
      );

  /// D51: `input`, not `data`. PROTOCOL.md §7.2 puts the payload under `input`, and
  /// WsClientFrame's only field for it is `[JsonPropertyName("input")]`.
  static EsiWsFrame remoteInput(String id, Map<String, dynamic> input) =>
      EsiWsFrame(p: EsiProtocol.version, type: 'remote.input', id: id, input: input);

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

  /// Carries what the PC reads at the top level; the map is spread into the frame JSON by
  /// the encoder (see the `...?remote` line in [EsiWsFrame.encode]). Takes the keys it is
  /// given, so `remote.start`'s five D51 parameters and `audio.start`'s D87 flag ride the
  /// same road without a helper page per type.
  EsiWsFrame _withRemote(Map<String, dynamic> topLevel) => EsiWsFrame(
        p: p,
        type: type,
        id: id,
        remote: topLevel,
      );

  EsiWsFrame _withKey(
    String code,
    bool down,
    List<String> mods,
    String? text,
  ) =>
      // D51: these were inside `data`, so the server read a top-level `code` and found null.
      // PROTOCOL.md 7.2 puts code/text/down/mods at the top level, and WsClientFrame declares
      // them with JsonPropertyName("code") and so on - it has no field for a nested object at
      // all. Every key press was therefore delivered as "no virtual-key mapping for code
      // (null)" and silently dropped, which is why typing and the on-screen pad did nothing.
      EsiWsFrame(
        p: p,
        type: type,
        id: id,
        code: code,
        down: down,
        mods: mods,
        text: text,
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

  /// An audio chunk (PROTOCOL.md §7.5). The header's picture-shaped members are reused:
  /// [screenWidth] is the sample rate, [screenHeight] the channel count, [gridCols] the
  /// samples per channel, and [payloadLength] the µ-law byte count. No tiles follow.
  static const int typeAudioChunk = 5;

  static const int codecJpeg = 1;

  /// G.711 µ-law, the codec the PC's loopback audio is encoded with. Decoded by
  /// `lib/src/audio/mu_law.dart`.
  static const int codecMuLaw = 3;

  /// Whether this header describes audio rather than a picture.
  ///
  /// True only when both the frame type *and* the codec agree, because a frame that is
  /// audio-shaped but not µ-law is malformed rather than a new codec - and one that is
  /// µ-law but not an audio chunk is a screen frame with a corrupted codec field. Checking
  /// both means the two failure modes land in the same "drop it" branch instead of one of
  /// them reaching the audio path with samples that are not samples.
  bool get isAudio => frameType == typeAudioChunk && codec == codecMuLaw;

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

/// The µ-law payload of one audio chunk, plus the format it was captured at.
class AudioChunk {
  const AudioChunk({
    required this.sampleRate,
    required this.channels,
    required this.samplesPerChannel,
    required this.muLaw,
  });

  final int sampleRate;
  final int channels;
  final int samplesPerChannel;

  /// The raw µ-law bytes, one per sample across all channels. Decode with
  /// `decodeMuLaw` from `lib/src/audio/mu_law.dart`.
  final Uint8List muLaw;
}

/// Extracts the audio payload from a binary frame, or returns null when the frame is not an
/// audio chunk or is truncated.
///
/// Returns null rather than throwing for a frame that merely *looks* broken, because this sits
/// on the same socket as the screen stream: a malformed packet is dropped here and the screen
/// path still gets its own chance to parse the frame it may legitimately be waiting for.
AudioChunk? parseAudioChunk(Uint8List frame) {
  if (frame.length < ScreenFrameHeader.size) return null;
  final header = ScreenFrameHeader.parse(frame);
  if (!header.isAudio) return null;

  // `payloadLength` is what the header declares; the slice is clamped to what actually
  // arrived, so a short frame yields short audio instead of a range error.
  final declared = header.payloadLength;
  final available = frame.length - ScreenFrameHeader.size;
  final length = declared <= available ? declared : available;
  if (length <= 0) return null;

  return AudioChunk(
    sampleRate: header.screenWidth,
    channels: header.screenHeight,
    samplesPerChannel: header.gridCols,
    muLaw: Uint8List.sublistView(frame, ScreenFrameHeader.size, ScreenFrameHeader.size + length),
  );
}

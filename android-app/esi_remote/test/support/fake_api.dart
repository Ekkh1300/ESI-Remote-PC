import 'dart:async';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_api.dart';

/// An [EsiApi] that answers from memory, so tests can count and order requests.
///
/// A real HTTP client is not used here on purpose. The faults this file is about are all about
/// *how many* requests go out and *in what order the replies come back*, and a real transport
/// would make both of those depend on timing rather than on the code under test.
class FakeEsiApi extends EsiApi {
  FakeEsiApi({this.latency = const Duration(milliseconds: 5)})
      : super(
          endpoint: const EsiEndpoint(host: '192.168.1.42', port: 48220, pins: <String>['pin']),
          identity: EsiIdentity(
            deviceId: 'a1b2c3d4e5f60718',
            keyId: 3,
            masterSecret: Uint8List.fromList(List<int>.generate(32, (int i) => i + 1)),
          ),
          clock: () => DateTime.utc(2026, 1, 2, 3, 4, 5),
          nonceSource: () => 'fixed-nonce-for-tests',
        );

  final Duration latency;

  /// Every volume this was asked to set, in the order the requests were sent.
  final List<int> volumeRequests = <int>[];

  /// How many volume posts were open at once. Anything above one is the bug.
  ///
  /// Counted over volume posts alone: the state refresh that follows each one is a GET and
  /// overlaps them by design, and counting that would make the test complain about the wrong
  /// thing.
  int maxConcurrent = 0;
  int _openVolume = 0;

  /// What `/media/state` reports. Tests change this to simulate the PC.
  int reportedVolume = 0;
  bool reportedMuted = false;

  /// Whether the fake claims to have *read* those values.
  ///
  /// Null omits the flags from the response entirely, which is what an agent that predates them
  /// sends. Left null by default so the existing tests keep exercising that path, and the model
  /// keeps having to default the missing flags to "known" rather than to "unknown".
  bool? reportedVolumeKnown;
  bool? reportedMuteKnown;

  String? failWith;

  final List<Completer<void>> _gates = <Completer<void>>[];

  /// Holds every response until [release] is called, so a test can stack up a burst of calls
  /// and watch what the code does with them before any of them come back.
  void gate() => _gates.add(Completer<void>());

  void release() {
    for (final gate in _gates) {
      if (!gate.isCompleted) gate.complete();
    }
    _gates.clear();
  }

  Future<void> _respond() async {
    // Snapshotted, not iterated live: release() empties the list, and a request that was
    // already in flight would otherwise walk a list being emptied underneath it.
    final gates = List<Completer<void>>.from(_gates);
    await Future<void>.delayed(latency);
    for (final gate in gates) {
      await gate.future;
    }
  }

  /// Held open around a volume post so overlap can be observed.
  Future<void> _volumeWindow() async {
    _openVolume++;
    if (_openVolume > maxConcurrent) maxConcurrent = _openVolume;
    try {
      await _respond();
    } finally {
      _openVolume--;
    }
  }

  @override
  Future<Map<String, dynamic>> getJson(
    String path, {
    List<(String, String)>? query,
    bool signed = true,
  }) async {
    await _respond();
    if (failWith != null) {
      throw EsiApiException(statusCode: 500, code: 'esi_internal', message: failWith!);
    }
    if (path == '/media/state') {
      return <String, dynamic>{
        'deviceId': 'output-1',
        'deviceName': 'Speakers (Realtek)',
        'defaultDevice': 'Speakers (Realtek)',
        'volume': reportedVolume,
        'isMuted': reportedMuted,
        if (reportedVolumeKnown != null) 'volumeKnown': reportedVolumeKnown,
        if (reportedMuteKnown != null) 'isMuteKnown': reportedMuteKnown,
        'sessionPeak': 72,
        'availableActions': <String>[
          'volumeSet',
          'volumeUp',
          'volumeDown',
          'toggleMute',
          'playPause',
          'previous',
          'next',
          'stop',
        ],
      };
    }
    return <String, dynamic>{};
  }

  @override
  Future<Map<String, dynamic>> postJson(
    String path, {
    Object? body,
    bool signed = true,
  }) async {
    final map = body is Map<String, dynamic> ? body : <String, dynamic>{};
    final volume = map['volume'];

    // Recorded before the gate, because "a request went out" is the thing these tests are
    // counting. Recording it after the reply would make a gated transport look like silence.
    if (path.endsWith('/volumeSet') && volume is int) {
      volumeRequests.add(volume);
    }

    // A volume post is measured on its own; everything else shares the gate but is not counted,
    // because the state refresh that follows each one is meant to overlap the next post.
    if (path.endsWith('/volumeSet')) {
      await _volumeWindow();
    } else {
      await _respond();
    }
    if (failWith != null) {
      throw EsiApiException(statusCode: 500, code: 'esi_internal', message: failWith!);
    }

    if (path.endsWith('/volumeSet') && volume is int) {
      // The PC applies it and then says so on the next read. Setting it here is what makes a
      // coalesced burst end up at the right place.
      reportedVolume = volume;
      return <String, dynamic>{
        'accepted': true,
        'detail': 'Set to $volume.',
      };
    }

    return <String, dynamic>{'accepted': true};
  }
}

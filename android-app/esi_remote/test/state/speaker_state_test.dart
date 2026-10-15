import 'dart:async';

import 'package:esi_remote/src/audio/phone_speaker.dart';
import 'package:esi_remote/src/net/esi_api.dart';
import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:esi_remote/src/protocol/models.dart';
import 'package:esi_remote/src/state/audio.dart';
import 'package:esi_remote/src/state/connection.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// The speaker must listen to what the PC said, not only to what it hoped.
///
/// Turning the switch on used to report success the moment the *phone's* track opened, and
/// the phone's track opens whether or not the PC is capturing anything. So a PC that had
/// refused - no session agent running, another phone already holding the audio - played
/// silence under a control reading on, which is indistinguishable from a broken codec. The
/// PC's `audio.state` reply is the one thing that tells the two apart, and it was arriving
/// with nothing listening to it.
class _FakeSocket extends EsiWebSocket {
  _FakeSocket(this._replies)
      : super(
          // A real-shaped pin, because the socket refuses to be built without one - and that
          // refusal is correct: this fake must go through the same constructor the app does.
          endpoint: const EsiEndpoint(
            host: '192.0.2.1',
            port: 48220,
            pins: <String>['f9Rl9PkFy20h7EmRGjInZguDr0eBYIEqSaO1yODVzDE='],
          ),
          identity: EsiIdentity(
            deviceId: 'phone-under-test',
            keyId: 1,
            masterSecret: Uint8List(32),
          ),
        );

  final List<AudioState> _replies;
  final StreamController<AudioState> _answers =
      StreamController<AudioState>.broadcast();

  var _sent = 0;

  bool startRequested = false;

  @override
  Stream<AudioState> get audioStates => _answers.stream;

  @override
  void startAudio() {
    startRequested = true;
    if (_sent >= _replies.length) return;

    final reply = _replies[_sent++];

    // On the next turn, the way a real reply arrives. A reply delivered synchronously would
    // pass even if the listener were attached after the send - which is the other half of
    // this fix and the reason it is worth testing at all.
    scheduleMicrotask(() => _answers.add(reply));
  }
}

/// The speaker opens its track lazily on the first frame, so a start that is accepted never
/// reaches the platform channel in these tests. The name is irrelevant; it is never answered.
const MethodChannel _unusedChannel = MethodChannel('esi_remote/test-speaker');

ProviderContainer _containerFor(_FakeSocket socket) => ProviderContainer(
      overrides: [
        webSocketProvider.overrideWithValue(socket),
        phoneSpeakerProvider.overrideWith((ref) => PhoneSpeaker(channel: _unusedChannel)),
      ],
    );

void main() {
  test('a PC that refuses to capture leaves the switch off, and says why', () async {
    const reason =
        "The session agent is not connected, so the PC's audio cannot be captured.";
    final socket = _FakeSocket(
      const <AudioState>[AudioState(active: false, detail: reason)],
    );
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    await container.read(speakerProvider.notifier).start();

    expect(socket.startRequested, isTrue, reason: 'the PC must have been asked at all');
    expect(container.read(speakerProvider), SpeakerState.failed);
    expect(container.read(speakerProvider.notifier).message, reason);
  });

  test('a refusal with no reason still fails rather than reporting silence as on', () async {
    final socket = _FakeSocket(const <AudioState>[AudioState(active: false)]);
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    await container.read(speakerProvider.notifier).start();

    expect(container.read(speakerProvider), SpeakerState.failed);
    expect(
      container.read(speakerProvider.notifier).message,
      isNotNull,
      reason: 'a failure with nothing to explain it still has to be a failure',
    );
  });

  test('a PC that accepts opens the track', () async {
    final socket = _FakeSocket(const <AudioState>[AudioState(active: true)]);
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    await container.read(speakerProvider.notifier).start();

    expect(socket.startRequested, isTrue);
    expect(container.read(speakerProvider), SpeakerState.on);
    expect(container.read(speakerProvider.notifier).message, isNull);
  });
}
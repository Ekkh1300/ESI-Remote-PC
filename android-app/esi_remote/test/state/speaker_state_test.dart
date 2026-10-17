import 'dart:async';

import 'package:esi_remote/src/audio/phone_speaker.dart';
import 'package:esi_remote/src/net/esi_api.dart';
import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:esi_remote/src/protocol/models.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
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
  final StreamController<EsiWsFrame> _answers =
      StreamController<EsiWsFrame>.broadcast();

  var _sent = 0;

  bool startRequested = false;

  /// How many `audio.start` requests went at the PC in total, so the replay-on-reconnect
  /// tests can see the fresh request arriving rather than inferring it from a state label.
  int startRequests = 0;

  /// How many `audio.stop` requests went out, counted rather than asserted by absence:
  /// "we did not ask a dead socket to stop" is the kind of fact that must be measured.
  int stopRequests = 0;

  /// The next answer is delivered as a delayed inactive one, whatever the queue said.
  bool deliverInactiveLate = false;

  @override
  Stream<AudioState> audioStateFor(String requestId) {
    if (deliverInactiveLate) {
      deliverInactiveLate = false;
      // One zero-delay later: late enough that the notifier can change state underneath,
      // early enough that `_settle` catches it, which is the whole shape of the race.
      return Stream.fromFuture(
        Future<AudioState>.delayed(
          Duration.zero,
          () => const AudioState(active: false),
        ),
      );
    }
    return _answers.stream.audioStateFor(requestId);
  }

  @override
  void startAudio(String requestId) {
    startRequested = true;
    startRequests++;
    if (_sent >= _replies.length) return;

    final reply = _replies[_sent++];

    // On the next turn, the way a real reply arrives. A reply delivered synchronously would
    // pass even if the listener were attached after the send - which is the other half of
    // this fix and the reason it is worth testing at all. The id goes back on it the way the
    // real PC echoes it, because the id is what the filter under test reads.
    scheduleMicrotask(() => _answers.add(_stateFrame(requestId, reply)));
  }

  @override
  void stopAudio() {
    stopRequests++;
  }

  /// What the PC sends in answer to an `audio.stop` that has not been collected yet: the
  /// same `audio.state` type, a *different* request's id, and `active: false`.
  ///
  /// This is the frame the id filter exists for. Tapping Stop and then Play quickly leaves
  /// it in flight when the start subscribes, and taken as the start's answer it reads as a
  /// refusal of a request the PC had just accepted.
  void leaveAStopReplyInFlight() => _answers.add(
        _stateFrame('a-stop-that-was-sent-first', const AudioState(active: false)),
      );

  EsiWsFrame _stateFrame(String id, AudioState state) => EsiWsFrame(
        p: EsiProtocol.version,
        type: 'audio.state',
        id: id,
        data: <String, dynamic>{
          'active': state.active,
          if (state.detail != null) 'detail': state.detail,
        },
      );
}

/// The speaker opens its track lazily on the first frame, so a start that is accepted never
/// reaches the platform channel in these tests. The name is irrelevant; it is never answered.
const MethodChannel _unusedChannel = MethodChannel('esi_remote/test-speaker');

/// A connection whose socket state the test moves by hand.
///
/// The real notifier builds itself from the active machine and the machine store; a speaker
/// test should not have to boot either. It keeps [ConnectionNotifier]'s own methods, so
/// `reportSocket` - the same method the app's socket wiring calls - is what the tests use
/// to move the socket state, meaning the speaker is listening to exactly what it would
/// listen to in the app.
class _StaticConnectionNotifier extends ConnectionNotifier {
  @override
  ConnectionStatus build() => const ConnectionStatus();
}

/// The same shape for the selected PC, with a setter the tests drive.
class _StaticActiveDeviceNotifier extends ActiveDeviceNotifier {
  @override
  String build() => '';

  void setDevice(String deviceId) => state = deviceId;
}

ProviderContainer _containerFor(_FakeSocket socket) => ProviderContainer(
      overrides: [
        webSocketProvider.overrideWithValue(socket),
        phoneSpeakerProvider.overrideWith((ref) => PhoneSpeaker(channel: _unusedChannel)),
        connectionProvider.overrideWith(() => _StaticConnectionNotifier()),
        activeDeviceIdProvider.overrideWith(() => _StaticActiveDeviceNotifier()),
      ],
    );

/// Runs the microtask queue enough times for a start attempt and its in-flight answer to
/// land. Named for what it does to the test's readability, not for Riverpod's pump.
Future<void> _settle([int rounds = 4]) async {
  for (var i = 0; i < rounds; i++) {
    await Future<void>.delayed(Duration.zero);
  }
}

void main() {
  // The supervision path stops the speaker's platform channel when a socket drops and a
  // PC changes; with no device half of the channel installed, that call throws
  // MissingPluginException - which stop() catches, but only across a messenger, and the
  // messenger comes up with the binding. Tests that exercise the stop path therefore need
  // it, or they die on the binding assert instead of proving what they came to prove.
  TestWidgetsFlutterBinding.ensureInitialized();

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

  test('a stop reply still in flight is not taken as the answer to the start', () async {
    // The PC answers `audio.stop` on the same `audio.state` type, with `active: false`. Tap
    // Stop and then Play quickly enough and that answer is still travelling when the start
    // subscribes - so `.first` takes it, and the phone reports "The PC refused to capture its
    // audio" about a start the PC had in fact just accepted, while the PC is left capturing
    // for a phone that then refuses to play. Both halves of that are false, and neither
    // shows up anywhere else.
    final socket = _FakeSocket(const <AudioState>[AudioState(active: true)]);
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    // Queued first, so it is the first thing the listener sees - which is exactly the order a
    // reply that was already in flight arrives in. A broadcast stream drops what nobody is
    // listening to yet, so this is scheduled rather than sent: `start()` attaches its listener
    // synchronously and only then hands back to the test, and the microtasks run after that.
    scheduleMicrotask(socket.leaveAStopReplyInFlight);

    await container.read(speakerProvider.notifier).start();

    expect(
      container.read(speakerProvider),
      SpeakerState.on,
      reason: 'a reply naming a different request is not an answer to this one',
    );
    expect(
      container.read(speakerProvider.notifier).message,
      isNull,
      reason: 'the phone must not report a refusal the PC never made',
    );
  });

  test('a PC connection dropping mid-play says so instead of claiming it is playing', () async {
    // The PC stops the capture the moment the socket dies - that is its disconnect handler
    // and not a choice the phone makes - so a switch that keeps saying "on" is a panel
    // claiming success over a stream that has already been ended by the other side. That
    // is the hour of quiet the morning session of October 16 produced (capture ran four
    // hundred packets, the phone closed, the switch stayed on).
    final socket = _FakeSocket(const <AudioState>[AudioState(active: true)]);
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    // Arm the supervision the way the app has it by the time anything streams: the socket
    // has been live, and this drop is live -> gone. Reporting the drop from a never-live
    // start would take the was-live==is-live shortcut and fire nothing, which is exactly
    // the shortcut that keeps the guard from lying about benign handshake churn.
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.live);

    final notifier = container.read(speakerProvider.notifier);
    await notifier.start();
    expect(container.read(speakerProvider), SpeakerState.on);

    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.reconnecting);
    await _settle();

    expect(container.read(speakerProvider), SpeakerState.reconnecting);
    expect(notifier.message, contains('connection dropped'));

    // The PC is somebody else's business now - the state the socket came back to decides
    // when this restarts, so a stop request sent into a dead socket is wrong twice.
    expect(socket.stopRequests, isZero);
  });

  test('the speaker that was on comes back with the socket, without a new tap', () async {
    final socket = _FakeSocket(
      const <AudioState>[AudioState(active: true), AudioState(active: true)],
    );
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    // Live first, for the same reason the drop test arms the supervision with a live
    // socket: the drop has to be a transition away from live to be seen at all.
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.live);

    final notifier = container.read(speakerProvider.notifier);
    await notifier.start();
    expect(container.read(speakerProvider), SpeakerState.on);
    expect(socket.startRequests, 1);

    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.reconnecting);
    await _settle();
    expect(container.read(speakerProvider), SpeakerState.reconnecting);
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.live);
    await _settle();

    expect(socket.startRequests, greaterThanOrEqualTo(2),
        reason: 'the request has to go onto the wire again for the new socket');
    expect(container.read(speakerProvider), SpeakerState.on);
  });

  test('a speaker that was off stays off when the socket cycles', () async {
    final socket = _FakeSocket(const <AudioState>[AudioState(active: true)]);
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.reconnecting);
    await _settle();

    expect(container.read(speakerProvider), SpeakerState.off);
    expect(socket.startRequests, isZero,
        reason: 'nothing was ever requested, so nothing may be replayed');
  });

  test('switching the active PC ends the request instead of replaying it', () async {
    // Asking one machine to send its sound and then finding that request carried over to
    // whichever PC the user next selects is not an upgrade - it is capturing audio on a
    // machine the user never pointed this feature at. The user's yes was scoped to a machine.
    final socket = _FakeSocket(const <AudioState>[AudioState(active: true)]);
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    final notifier = container.read(speakerProvider.notifier);
    await notifier.start();
    expect(container.read(speakerProvider), SpeakerState.on);

    (container.read(activeDeviceIdProvider.notifier) as _StaticActiveDeviceNotifier)
        .setDevice('the-other-pc');
    expect(container.read(speakerProvider), SpeakerState.off);
    expect(notifier.message, isNull);

    // And it stays off across the socket churn the switch is about to cause - no replay.
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.live);
    await _settle();
    expect(container.read(speakerProvider), SpeakerState.off);
  });

  test('an answer that arrives after the connection dropped again does not cancel the wait',
      () async {
    final socket = _FakeSocket(
      const <AudioState>[AudioState(active: true), AudioState(active: true)],
    );
    final container = _containerFor(socket);
    addTearDown(container.dispose);

    final notifier = container.read(speakerProvider.notifier);
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.live);
    await notifier.start();
    expect(container.read(speakerProvider), SpeakerState.on);

    // The connection drops, the socket returns, the automatic restart goes out - and the
    // machine drops again while that restart's answer is in flight. The answer that then
    // lands describes a capture the disconnect handler has already killed; reading it as
    // the restart's own refusal would turn a wait into a dead end, and the wait is the
    // one state that still has a rubber to outrun.
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.reconnecting);
    await _settle();
    expect(container.read(speakerProvider), SpeakerState.reconnecting);

    // Both reports go out synchronously, back to back, deliberately: the time between
    // "restart starts" and "the answer lands" is 1 microtask round, and the test has to
    // hold the same order no matter how the runner interleaves its own microtasks.
    socket.deliverInactiveLate = true;
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.live);
    container.read(connectionProvider.notifier).reportSocket(EsiSocketState.reconnecting);
    await _settle();

    expect(container.read(speakerProvider), SpeakerState.reconnecting,
        reason: 'the late answer belongs to the re-drop, not to the restart');
    expect(notifier.message, contains('connection dropped'));
  });
}
import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../audio/phone_speaker.dart';
import '../protocol/models.dart';
import 'connection.dart';

/// Whether the PC's own audio is being played on this phone.
enum SpeakerState {
  /// Nothing requested, nothing playing.
  off,

  /// `audio.start` is on the wire, or the track is opening.
  starting,

  /// Playing.
  on,

  /// The PC refused, or the track could not open. [SpeakerNotifier.message] says which.
  failed,
}

/// Drives the phone-as-speaker path: asks the PC to capture its output, and plays what comes
/// back.
///
/// The two halves are started and stopped together on purpose. Asking the PC to stream without
/// opening a track would push audio at a phone that never plays it; opening a track without
/// asking would play nothing. They are one decision to the user - "hear the PC here" - so they
/// are one transition here.
///
/// The speaker is read through [phoneSpeakerProvider] rather than held, so a test can replace
/// it with a fake that records the bytes it was given instead of opening an [AudioTrack] that
/// does not exist off a device.
class SpeakerNotifier extends Notifier<SpeakerState> {
  String? _message;

  /// What went wrong, when [state] is [SpeakerState.failed]. Null otherwise.
  String? get message => _message;

  PhoneSpeaker get _speaker => ref.read(phoneSpeakerProvider);

  @override
  SpeakerState build() => SpeakerState.off;

  /// Starts playback. A no-op if already on or starting, so a double-tap on the switch cannot
  /// open two tracks.
  Future<void> start() async {
    if (state == SpeakerState.on || state == SpeakerState.starting) return;

    final socket = ref.read(webSocketProvider);
    if (socket == null) {
      _fail('No PC is selected.');
      return;
    }

    state = SpeakerState.starting;
    _message = null;

    // The listener is attached before the request is written, not after it. The reply can
    // arrive on the next turn of the event loop, and this runs over a LAN, so a listener
    // attached after the send can miss a confirmation that was already given - and then wait
    // forever for one.
    //
    // No reply at all is not a failure. An agent too old to answer `audio.state` is also one
    // that sends no audio, but the track still opens and whatever does arrive is played;
    // where that silence comes from is the PC's business, not a reason to refuse the request.
    final answer = socket.audioStates
        .first
        .then<AudioState?>((value) => value)
        .timeout(
          const Duration(seconds: 4),
          onTimeout: () => null,
        );

    socket.startAudio();

    // This is the half that was missing. Without it the switch reported on the moment the
    // track opened, and a PC that had refused - no session agent running, another phone
    // already taking the audio - played silence under a control that said everything was
    // fine. Silence and a codec fault look identical from here; the PC saying no does not.
    final AudioState? accepted = await answer;
    if (accepted != null && !accepted.active) {
      _fail(accepted.detail ?? 'The PC refused to capture its audio.');
      return;
    }

    try {
      // The socket may be replaced on reconnect. Reading it again here means the speaker is
      // always attached to the socket that is actually live rather than the one that was live
      // when the user tapped.
      await _speaker.start(ref.read(webSocketProvider)!.binary);
      state = SpeakerState.on;
    } on Object catch (e) {
      _fail('The phone could not open its speaker: $e');
    }
  }

  /// Stops playback and tells the PC to stop capturing.
  Future<void> stop() async {
    if (state == SpeakerState.off) return;
    state = SpeakerState.off;
    _message = null;

    // The PC is told to stop as well as the track. Leaving the PC capturing would keep the
    // output device open and the pump thread running for a reader that has gone away.
    final socket = ref.read(webSocketProvider);
    // `stopAudio` is fire-and-forget on the socket, so it is wrapped in an async closure - the
    // parameter awaits it, and `async` is what gives the closure the `Future<void>` return the
    // caller waits on rather than a bare `void`.
    await _speaker.stop(onStopped: socket == null ? null : () async => socket.stopAudio());
  }

  void _fail(String why) {
    _message = why;
    state = SpeakerState.failed;
    unawaited(_speaker.stop());
  }
}

final speakerProvider =
    NotifierProvider<SpeakerNotifier, SpeakerState>(SpeakerNotifier.new);

import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../audio/phone_speaker.dart';
import '../data/speaker_settings.dart';
import '../net/esi_ws.dart';
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

  /// The socket dropped mid-playback and the nudge is waiting for it to come back, at
  /// which point the same request is made all over again. Different from [starting]:
  /// this one carries a reason on the panel, and the PC's disconnect handler killed
  /// the capture the moment the socket died, so *no* retry loop is running here - the
  /// wait is for the socket, not for a capture that might still exist.
  reconnecting,

  /// The PC refused, or the track could not open. [SpeakerNotifier.message] says which.
  failed,
}

/// Drives the phone-as-speaker path: asks the PC to capture its output, and plays what
/// comes back.
///
/// The two halves are started and stopped together on purpose. Asking the PC to stream without
/// opening a track would push audio at a phone that never plays it; opening a track without
/// asking would play nothing. They are one decision to the user - "hear the PC here" - so they
/// are one transition here.
///
/// The speaker is read through [phoneSpeakerProvider] rather than held, so a test can replace
/// it with a fake that records the bytes it was given instead of opening an [AudioTrack] that
/// does not exist off a device.
///
/// # What a dropped socket does to all of this
///
/// The PC's WebSocket handler stops the capture the instant a phone disconnects ("PC audio for
/// … stopped" in its log), so a switch left saying *on* over a broken connection is a lie with
/// a face on it: the meter says playing and no more bytes arrive. That is not hypothetical
/// decoration - the morning session of October 16 ran capture for eight seconds, the phone
/// killed its own socket, and any user looking at the phone from that moment on saw a control
/// that claimed to be working. So this notifier supervises the connection it rides on:
///
/// * the socket dropping turns a live speaker into [SpeakerState.reconnecting] - the panel
///   now says what actually happened rather than reporting success over silence;
/// * the socket coming back puts the request on the wire again. The switch was the user's
///   yes; one wifi blip does not revoke it, and a speaker a user has to re-tap every time
///   the phone's screen blinks off is a speaker nobody uses;
/// * the active PC changing revokes it. A request made of one machine's microphone - here,
///   its output device - must not be replayed silently against whichever PC the user
///   switches to next; they did not ask that machine anything yet.
class SpeakerNotifier extends Notifier<SpeakerState> {
  String? _message;

  /// Whether the current run should be re-requested when the socket returns.
  bool _autoResume = false;

  /// The generation of the request currently in flight.
  ///
  /// Every decision that makes a pending answer moot - a stop, a socket drop, a PC
  /// change, the run being re-requested on the socket's return - ends the generation.
  /// An answer that lands against a dead generation is dropped unread: taken literally
  /// it is a refusal of a request nobody owns any more, and reading it stops the wait
  /// that is still the most promising thing on the page.
  int _run = 0;

  @override
  SpeakerState build() {
    // The supervision is where the honesty lives - see the class doc. `ref.listen` inside
    // `build` is the one legal place for it, and Riverpod retires listeners from a previous
    // build on its own, so there is no teardown hand-holding to write here.
    ref.listen(connectionProvider.select((status) => status.socketState), (previous, next) {
      final was = previous;
      final isLive = next == EsiSocketState.live;
      final wasLive = was == EsiSocketState.live;
      if (wasLive == isLive) return;

      if (!isLive) {
        // The PC has already stopped capturing (its disconnect handler owns that). The
        // local track is fed by the same socket's binary stream, whose controller is
        // closed on dispose but not on a plain reconnect - so the track keeps waiting on
        // a stream that has nothing to say. Hand it back and wait: keep the local
        // resources, re-request everything once the socket is back.
        if (state == SpeakerState.on || state == SpeakerState.starting) {
          if (_autoResume) {
            _message =
                'The PC connection dropped, so the PC stopped sending its sound. '
                'Waiting for it to come back.';
            state = SpeakerState.reconnecting;
          } else {
            state = SpeakerState.off;
          }
          // Any answer still in flight belongs to a capture the PC has just stopped:
          // end the generation so it can neither fail this state nor confirm it.
          ++_run;
          unawaited(_speaker.stop());
        }
        return;
      }

      // Live again. Only a speaker that was already on restarts itself; an off one
      // belongs to the user, and a failed one to the reason it failed.
      if (_autoResume && state == SpeakerState.reconnecting) {
        unawaited(start());
      }
    });

    // A PC change is the one event that has to cut auto-resume even when the socket
    // state has not moved yet - the webSocketProvider is rebuilt for the new machine and
    // only then starts its own connection. This fires before that, while the user's new
    // PC is still not carrying anybody's request. `PhoneSpeaker.stop` is a no-op when
    // nothing is running, so the unconditional call can stay unconditional.
    ref.listen(activeDeviceIdProvider, (previous, next) {
      if (previous == null || previous == next) return;
      _autoResume = false;
      _message = null;
      // The in-flight answer belonged to a request made of the previous machine.
      ++_run;
      unawaited(_speaker.stop());
      state = SpeakerState.off;
    });

    return SpeakerState.off;
  }

  PhoneSpeaker get _speaker => ref.read(phoneSpeakerProvider);

  /// What went wrong, when [state] is [SpeakerState.failed]. Null otherwise.
  String? get message => _message;

  /// Starts playback. A no-op if already on or starting, so a double-tap on the switch cannot
  /// open two tracks.
  Future<void> start() async {
    if (state == SpeakerState.on || state == SpeakerState.starting) return;

    final run = ++_run;

    final socket = ref.read(webSocketProvider);
    if (socket == null) {
      _fail('No PC is selected.');
      return;
    }

    state = SpeakerState.starting;
    _message = null;
    _autoResume = true;

    // The D87 answer is read at the moment of the request, not cached in a field: there is
    // no cached copy to fork from the store, and an auto-resume after a dropped socket
    // re-asks the store as naturally as it re-asks the PC. A store that has never been
    // written answers the store's own default - true, the feature itself.
    final silenceLocal =
        await ref.read(speakerSettingsProvider).silenceLocal();

    // The listener is attached before the request is written, not after it. The reply can
    // arrive on the next turn of the event loop, and this runs over a LAN, so a listener
    // attached after the send can miss a confirmation that was already given - and then wait
    // forever for one.
    //
    // The id is minted first for the same reason: the listener has to be able to recognise
    // its own answer, and it cannot do that from an id it does not have yet.
    //
    // Matching on that id is not decoration. The PC answers `audio.stop` on this very same
    // `audio.state` type with `active: false`, so tapping Stop and then Play quickly enough
    // leaves the stop's answer in flight when the start subscribes. Taken unqualified it
    // reads as a refusal of a start the PC had in fact just accepted - a false "The PC
    // refused" over a capture that is running, and a PC left capturing for a phone that then
    // refuses to play.
    //
    // No reply at all is still not a failure. An agent too old to answer `audio.state` is also
    // one that sends no audio, but the track still opens and whatever does arrive is played;
    // where that silence comes from is the PC's business, not a reason to refuse the request.
    final requestId = EsiWsFrame.newAudioRequestId();
    final answer = socket.audioStateFor(requestId)
        .first
        .then<AudioState?>((value) => value)
        .timeout(
          const Duration(seconds: 4),
          onTimeout: () => null,
        );

    socket.startAudio(requestId, silenceLocal: silenceLocal);

    // This is the half that was missing. Without it the switch reported on the moment the
    // track opened, and a PC that had refused - no session agent running, another phone
    // already taking the audio - played silence under a control that said everything was
    // fine. Silence and a codec fault look identical from here; the PC saying no does not.
    final AudioState? accepted = await answer;
    if (run != _run) {
      // The request was superseded while its answer was in flight: the socket dropped
      // again, the user stopped, the PC changed. The answer describes a capture nobody
      // owns any more - either half, refusing it or accepting it, would write into a
      // state that has moved on.
      return;
    }
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
    ++_run;
    state = SpeakerState.off;
    _message = null;
    _autoResume = false;

    // The PC is told to stop as well as the track. Leaving the PC capturing would keep the
    // output device open and the pump thread running for a reader that has gone away.
    final socket = ref.read(webSocketProvider);
    // `stopAudio` is fire-and-forget on the socket, so it is wrapped in an async closure - the
    // parameter awaits it, and `async` is what gives the closure the `Future<void>` return the
    // caller waits on rather than a bare `void`.
    await _speaker.stop(onStopped: socket == null ? null : () async => socket.stopAudio());
  }

  /// Commits the D87 answer - whether the PC's own speakers should be silenced while the
  /// phone carries its sound.
  ///
  /// The answer is persisted before anything is re-requested of the PC, so a battery death
  /// between persist and restart leaves the preference standing: what the user said is
  /// stronger than what the machine was doing when it died. An ongoing capture is closed
  /// and re-requested, because the PC reads the flag only when the capture opens - leaving
  /// it running would keep the old answer in force until the next manual stop, and a switch
  /// that does nothing while the stream is on is a control that lies.
  ///
  /// The stop-then-start is accepted, not hidden: around three hundred milliseconds of
  /// silence while the PC closes and reopens the device, which the meter shows as a dip.
  Future<void> setSilenceLocal(bool value) async {
    await ref.read(speakerSettingsProvider).setSilenceLocal(value);
    if (state == SpeakerState.on || state == SpeakerState.starting) {
      await stop();
      unawaited(start());
    }
  }

  void _fail(String why) {
    _message = why;
    state = SpeakerState.failed;
    _autoResume = false;
    ++_run;
    unawaited(_speaker.stop());
  }
}

final speakerProvider =
    NotifierProvider<SpeakerNotifier, SpeakerState>(SpeakerNotifier.new);

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../data/speaker_settings.dart';
import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/audio.dart';
import '../../state/connection.dart';
import '../../state/features.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/setting_slider.dart';
import '../widgets/states.dart';
import '../../audio/phone_speaker.dart';

/// Media control, and the rest of the "small controls" tab.
///
/// The volume control is a slider rather than a row of plus and minus buttons, because the
/// protocol exposes `volumeSet` and a slider is what the data supports. Everything else is
/// a transport control, and each one is disabled when the agent says the action is not
/// available for the current output device rather than letting the user tap and fail.
class MediaScreen extends ConsumerWidget {
  const MediaScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => RequiresPairing(
        builder: (context, ref, connection) {
          if (!connection.supports(EsiFeature.media)) {
            return const FeatureUnavailable(feature: 'media', label: 'Media control');
          }
          final slice = ref.watch(mediaProvider);

          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Control',
              subtitle: slice.value?.defaultDevice ?? 'The default audio output on the PC',
              onRefresh: () => ref.read(mediaProvider.notifier).refresh(),
              children: <Widget>[
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _VolumePanel(slice: slice),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _TransportPanel(slice: slice),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _SpeakerPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _ShortcutsPanel(),
                ),
              ],
            ),
          );
        },
      );
}

class _VolumePanel extends ConsumerWidget {
  const _VolumePanel({required this.slice});

  final AsyncSlice<MediaState> slice;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = slice.value;
    final notifier = ref.read(mediaProvider.notifier);
    final muted = state?.isMuted ?? false;
    final volume = state?.volume ?? 0;

    return EsiPanel(
      title: 'Volume',
      severity: muted ? Severity.elevated : Severity.nominal,
      trailing: state == null
          ? null
          : EsiTag(
              label: 'peak ${state.sessionPeak}',
              colour: EsiPalette.dim,
              tooltip: 'The loudest level this output device has produced',
            ),
      // D54: a failed load used to show this spinner for ever. `state == null` is true both
      // while loading and after a failure, and only the first of those is worth waiting for -
      // the second one is a machine that cannot be reached, and a control that says so is more
      // use than one that quietly turns.
      child: state == null
          ? (slice.error != null
              ? _LoadFailure(
                  message: slice.error!,
                  detail: slice.errorDetail,
                  onRetry: () => ref.read(mediaProvider.notifier).refresh(),
                )
              : const EsiLoading(height: 90))
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    EsiIconAction(
                      icon: muted ? Icons.volume_off : Icons.volume_down,
                      tooltip: 'Volume down',
                      onPressed: state.can('volumeDown')
                          ? () => notifier.act('volumeDown', step: 5)
                          : null,
                    ),
                    Expanded(
                      // EsiSettingSlider, not a bare Slider. A bare one sends a request per
                      // division of travel and is bound to the server's copy of the value, so
                      // the thumb springs back between replies and a single swipe lands on
                      // whichever of its hundred replies arrived last.
                      //
                      // Disabled outright when the PC's level could not be read. A slider that
                      // moves and then snaps back to a number nobody measured is worse than one
                      // that says it cannot be used.
                      child: EsiSettingSlider(
                        value: volume.toDouble(),
                        min: 0,
                        max: 100,
                        divisions: 100,
                        enabled: state.canSetVolume,
                        onPreview: (value) =>
                            notifier.setVolume(value.round()),
                        onCommit: (value) async {
                          final outcome =
                              await notifier.setVolume(value.round(), wait: true);
                          if (outcome.ok) return;
                          if (context.mounted) showOutcome(context, outcome);
                        },
                      ),
                    ),
                    EsiIconAction(
                      icon: Icons.volume_up,
                      tooltip: 'Volume up',
                      onPressed: state.can('volumeUp')
                          ? () => notifier.act('volumeUp', step: 5)
                          : null,
                    ),
                    const SizedBox(width: EsiSpace.sm),
                    SizedBox(
                      width: 52,
                      // A dash, not a zero. The PC could not be asked what its volume is, and 0
                      // is also a real volume - showing it as a number is how a phone ended up
                      // displaying a confident silent next to a slider the user had just moved.
                      child: Text(
                        state.volumeKnown ? '$volume' : '—',
                        textAlign: TextAlign.right,
                        style: EsiType.reading,
                      ),
                    ),
                  ],
                ),
                if (!state.volumeKnown) ...<Widget>[
                  const SizedBox(height: EsiSpace.sm),
                  Text(
                    'The PC could not report its volume, so the level above is unknown. '
                    'This happens while nobody is signed in to the PC: the volume is read '
                    'from the signed-in session.',
                    style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 11),
                  ),
                ],
                const SizedBox(height: EsiSpace.md),
                Row(
                  children: <Widget>[
                    OutlinedButton.icon(
                      onPressed: state.can('toggleMute')
                          ? () => notifier.act('toggleMute')
                          : null,
                      icon: Icon(muted ? Icons.volume_up : Icons.volume_off, size: 16),
                      label: Text(muted ? 'Unmute' : 'Mute'),
                    ),
                    const Spacer(),
                    if (state.availableActions.isEmpty)
                      Text(
                        'The agent did not restrict any action for this device.',
                        style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 11),
                      ),
                  ],
                ),
              ],
            ),
    );
  }
}

/// The phone as the PC's speaker: the loopback stream on its own switch.
///
/// Deliberately a switch and not a play button. The state it drives is not "playback" but
/// "should the PC be sending its sound here at all" - the same question the panel above asks
/// about volume - and a switch says that a whole-machine routing decision is being made, where
/// a play button would suggest a track is about to start.
///
/// The meter underneath is the answer to the question the first real session left hanging:
/// eighteen seconds of stream during which the PC captured four hundred packets, the user
/// heard nothing, and neither the PC (it was faithfully capturing a silent mixer) nor the
/// phone (it was faithfully playing them) was lying. A meter that says "the PC is sending
/// silence - nothing is playing on it" turns that hour of unexplained quiet into a sentence.
class _SpeakerPanel extends ConsumerWidget {
  const _SpeakerPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(speakerProvider);
    final notifier = ref.read(speakerProvider.notifier);
    final live =
        state == SpeakerState.on || state == SpeakerState.starting || state == SpeakerState.reconnecting;

    return EsiPanel(
      title: 'This phone as the PC speaker',
      severity: switch (state) {
        SpeakerState.on => Severity.nominal,
        SpeakerState.starting => Severity.unknown,
        SpeakerState.reconnecting => Severity.unknown,
        SpeakerState.failed => Severity.elevated,
        SpeakerState.off => Severity.unknown,
      },
      footnote: 'The PC\'s own sound, played here. 24 kHz stereo, so a video on the PC sounds '
          'like itself rather than like a phone call.',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Row(
            children: <Widget>[
              Expanded(
                child: OutlinedButton.icon(
                  // A running attempt is not tappable so a double-tap cannot open two tracks,
                  // but a reconnecting one is: the wait behind it belongs to the user. Tapping
                  // it stops the speaker and stops the auto-resume it was waiting on.
                  onPressed: state == SpeakerState.starting
                      ? null
                      : () => live ? notifier.stop() : notifier.start(),
                  icon: Icon(
                    live && state != SpeakerState.reconnecting
                        ? Icons.stop
                        : Icons.speaker_phone,
                    size: 16,
                  ),
                  label: Text(switch (state) {
                    SpeakerState.on => 'Stop playing here',
                    SpeakerState.starting => 'Opening…',
                    SpeakerState.reconnecting => 'Reconnecting…',
                    SpeakerState.failed => 'Try again',
                    SpeakerState.off => 'Play the PC here',
                  }),
                ),
              ),
            ],
          ),
          _SilenceLocalToggle(),
          if (state != SpeakerState.off) _SpeakerMeter(tickerKey: state),
          if ((state == SpeakerState.failed || state == SpeakerState.reconnecting) &&
              notifier.message != null) ...<Widget>[
            const SizedBox(height: EsiSpace.sm),
            Text(
              notifier.message!,
              style: EsiType.body.copyWith(
                color: state == SpeakerState.failed ? EsiPalette.flare : EsiPalette.faint,
                fontSize: 11,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// The D87 switch in [_SpeakerPanel]: whether the PC's own speakers go silent while
/// this phone carries the PC's sound.
///
/// A widget with its own copy of the answer rather than a provider read in build, because
/// the answer lives behind flutter_secure_storage - a platform channel a build cannot
/// await. It loads once when the row is first laid out and everything after that is
/// optimistic: the switch turns first and the commit follows, and the commit (through the
/// speaker notifier, not around it) restarts an already-running capture, since the PC
/// reads the flag only when the capture opens.
class _SilenceLocalToggle extends ConsumerStatefulWidget {
  const _SilenceLocalToggle();

  @override
  ConsumerState<_SilenceLocalToggle> createState() => _SilenceLocalToggleState();
}

class _SilenceLocalToggleState extends ConsumerState<_SilenceLocalToggle> {
  /// Null until the store answers; the switch shows the store's default (true) before
  /// that, which is the answer the store would give anyway.
  bool? _committed;

  @override
  void initState() {
    super.initState();
    unawaited(ref
        .read(speakerSettingsProvider)
        .silenceLocal()
        .then((persisted) => mounted ? setState(() => _committed = persisted) : null));
  }

  @override
  Widget build(BuildContext context) {
    final value = _committed ?? true;
    return Padding(
      padding: const EdgeInsets.only(top: EsiSpace.sm),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Text('Silence the PC\'s own speakers', style: EsiType.body.copyWith(fontSize: 13)),
                Text(
                  'While the stream plays, the PC mutes itself and the sound comes from '
                  'here only. Changing it restarts the stream - a moment of silence.',
                  style: EsiType.body.copyWith(fontSize: 11, color: EsiPalette.faint),
                ),
              ],
            ),
          ),
          Switch(
            value: value,
            onChanged: (next) {
              // Optimistic: the switch moves now, the store and the PC follow. If the
              // store refuses, the next layout corrects it - and the store refusing is
              // a storage fault this row cannot fix from here anyway.
              setState(() => _committed = next);
              unawaited(ref.read(speakerProvider.notifier).setSilenceLocal(next));
            },
          ),
        ],
      ),
    );
  }
}

/// The one line under the speaker button that reads the speaker's own counters.
///
/// Ticked once a second by [EsiTicker] rather than driven by the stats notifier's stream,
/// because the state most worth showing - "the packets stopped arriving" - is the one
/// update that cannot fire a rebuild: nothing arrives to announce its own absence.
/// One-second ticks turn a stalled stream into words within a second of it stalling,
/// which the notifier alone could never do.
class _SpeakerMeter extends ConsumerWidget {
  const _SpeakerMeter({required this.tickerKey});

  /// Rebuilds the ticker (and resets its counter) when the speaker state changes, so a
  /// fresh session never opens with a line left over from the previous one.
  final SpeakerState tickerKey;

  @override
  Widget build(BuildContext context, WidgetRef ref) => EsiTicker(
        key: ValueKey<SpeakerState>(tickerKey),
        interval: const Duration(seconds: 1),
        builder: (context, tick) {
          final s = ref.read(phoneSpeakerProvider).stats.value;
          if (s.packets == 0 && s.dropped == 0) return const SizedBox.shrink();
          return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  const SizedBox(height: EsiSpace.sm),
                  Text(
                    '${s.packets} packets received'
                    '${s.dropped > 0 ? ', $s.dropped skipped while opening' : ''} · '
                    '${s.sampleRate ?? '?'} Hz ${s.channels ?? '?'} ch',
                    style: EsiType.mono.copyWith(fontSize: 10, color: EsiPalette.faint),
                  ),
                  if (s.lastPacketAt != null &&
                      DateTime.now().difference(s.lastPacketAt!) > const Duration(seconds: 3) &&
                      s.running) ...<Widget>[
                    const SizedBox(height: EsiSpace.xs),
                    Text(
                      'The sound stream has stopped arriving. The PC connection is probably '
                      'down; the switch above will follow it - and reconnect it - automatically.',
                      style: EsiType.body.copyWith(color: EsiPalette.flare, fontSize: 11),
                    ),
                  ] else if (s.saysSilence) ...<Widget>[
                    const SizedBox(height: EsiSpace.xs),
                    Text(
                      'Silence. The PC is capturing correctly, but it is not playing anything '
                      'right now, so there is nothing to send. Play something on the PC and '
                      'it will appear here within a second or so.',
                      style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 11),
                    ),
                  ],
                ],
              );
        },
      );
}

/// Says what went wrong and offers to try again, in place of a spinner that never resolves.
class _LoadFailure extends StatelessWidget {
  const _LoadFailure({required this.message, this.detail, required this.onRetry});

  final String message;
  final String? detail;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              const Icon(Icons.error_outline, size: 17, color: EsiPalette.alarm),
              const SizedBox(width: EsiSpace.md),
              Expanded(
                child: Text(
                  message,
                  style: EsiType.bodyStrong.copyWith(color: EsiPalette.alarm, fontSize: 13),
                ),
              ),
            ],
          ),
          if (detail != null) ...<Widget>[
            const SizedBox(height: EsiSpace.xs),
            Text(
              detail!,
              style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12),
            ),
          ],
          const SizedBox(height: EsiSpace.sm),
          OutlinedButton.icon(
            onPressed: onRetry,
            icon: const Icon(Icons.refresh, size: 16),
            label: const Text('Try again'),
          ),
        ],
      );
}

class _TransportPanel extends ConsumerWidget {
  const _TransportPanel({required this.slice});

  final AsyncSlice<MediaState> slice;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(mediaProvider.notifier);
    final state = slice.value;
    return EsiPanel(
      title: 'Playback',
      severity: state == null ? Severity.unknown : Severity.nominal,
      // §6.7 documents a state object, not a now-playing metadata object: there is no
      // track title to show, and inventing one would be worse than saying nothing.
      footnote: 'The protocol reports transport state and volume, not what is playing. '
          'Track titles are not available from the agent.',
      child: state == null
          ? (slice.error != null
              ? _LoadFailure(
                  message: slice.error!,
                  detail: slice.errorDetail,
                  onRetry: () => ref.read(mediaProvider.notifier).refresh(),
                )
              : const EsiLoading(height: 90))
          : Row(
              mainAxisAlignment: MainAxisAlignment.spaceEvenly,
              children: <Widget>[
                for (final action in const <MediaAction>[
                  MediaAction.previous,
                  MediaAction.playPause,
                  MediaAction.next,
                  MediaAction.stop,
                ])
                  Column(
                    mainAxisSize: MainAxisSize.min,
                    children: <Widget>[
                      EsiIconAction(
                        icon: _iconFor(action),
                        size: 46,
                        colour: action == MediaAction.playPause
                            ? EsiPalette.signal
                            : EsiPalette.dim,
                        tooltip: state.can(action.wire) ? action.label : '${action.label} unavailable',
                        onPressed: state.can(action.wire)
                            ? () => notifier.act(action.wire)
                            : null,
                      ),
                      const SizedBox(height: EsiSpace.xs),
                      Text(
                        action.label,
                        style: EsiType.label.copyWith(fontSize: 8.5, letterSpacing: 0.2),
                      ),
                    ],
                  ),
              ],
            ),
    );
  }

  static IconData _iconFor(MediaAction action) => switch (action) {
        MediaAction.previous => Icons.skip_previous,
        MediaAction.next => Icons.skip_next,
        MediaAction.stop => Icons.stop,
        MediaAction.playPause => Icons.play_arrow,
        _ => Icons.play_arrow,
      };
}

/// Direct links to the rest of the app, kept in the same tab as the small controls because
/// they are all "one tap to a machine you are not sitting at".
class _ShortcutsPanel extends ConsumerWidget {
  const _ShortcutsPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(notificationsProvider).value?.unreadCount ?? 0;
    return EsiPanel(
      title: 'Elsewhere',
      tick: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          _Shortcut(
            icon: Icons.notifications_none,
            label: 'Notifications',
            detail: unread > 0 ? '$unread unread' : 'Nothing unread',
            onTap: () => context.go('/notifications'),
          ),
          _Shortcut(
            icon: Icons.settings_outlined,
            label: 'Settings',
            detail: 'Agent settings, keys, paired devices',
            onTap: () => context.go('/settings'),
          ),
        ],
      ),
    );
  }
}

class _Shortcut extends StatelessWidget {
  const _Shortcut({
    required this.icon,
    required this.label,
    required this.detail,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final String detail;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: EsiRadius.tag,
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: EsiSpace.sm),
            child: Row(
              children: <Widget>[
                Icon(icon, size: 17, color: EsiPalette.dim),
                const SizedBox(width: EsiSpace.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Text(label, style: EsiType.body.copyWith(fontSize: 13.5)),
                      Text(detail, style: EsiType.mono.copyWith(fontSize: 10)),
                    ],
                  ),
                ),
                const Icon(Icons.chevron_right, size: 16, color: EsiPalette.faint),
              ],
            ),
          ),
        ),
      );
}

/// Notification rules live under admin scope, so the screen checks before offering them.
class NotificationRulesPanel extends ConsumerWidget {
  const NotificationRulesPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(connectionProvider);
    if (!connection.hasScope(EsiScope.admin)) {
      return EsiPanel(
        title: 'Rules',
        tick: false,
        child: Text(
          'This phone does not have the admin scope, so it cannot read or change the '
          'notification rules.',
          style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
        ),
      );
    }
    return const _RulesBody();
  }
}

class _RulesBody extends ConsumerStatefulWidget {
  const _RulesBody();

  @override
  ConsumerState<_RulesBody> createState() => _RulesBodyState();
}

class _RulesBodyState extends ConsumerState<_RulesBody> {
  final Map<String, bool> _draft = <String, bool>{};
  bool _saving = false;

  @override
  Widget build(BuildContext context) {
    final slice = ref.watch(notificationRulesProvider);
    final rules = slice.value ?? const <NotificationRule>[];

    return EsiPanel(
      title: 'Notification rules',
      severity: rules.isEmpty ? Severity.unknown : Severity.nominal,
      trailing: rules.isEmpty
          ? null
          : EsiTag(
              label: _dirty ? 'unsaved' : 'in sync',
              colour: _dirty ? EsiPalette.flare : EsiPalette.dim,
            ),
      child: slice.loading && rules.isEmpty
          ? const EsiLoading(height: 120)
          : rules.isEmpty
              ? Text(
                  slice.error ?? 'The agent did not return any rules.',
                  style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12.5),
                )
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  mainAxisSize: MainAxisSize.min,
                  children: <Widget>[
                    for (final rule in rules)
                      Padding(
                        padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
                        child: Row(
                          children: <Widget>[
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: <Widget>[
                                  Text(
                                    rule.title.isEmpty ? rule.id : rule.title,
                                    style: EsiType.body.copyWith(fontSize: 13),
                                  ),
                                  Text(
                                    '${rule.id} · ${rule.severity}'
                                    '${rule.threshold != null ? ' · above ${rule.threshold}' : ''}'
                                    '${rule.sustainSeconds > 0 ? ' · for ${rule.sustainSeconds}s' : ''}',
                                    style: EsiType.mono.copyWith(fontSize: 10),
                                  ),
                                ],
                              ),
                            ),
                            Switch(
                              value: _draft[rule.id] ?? rule.enabled,
                              onChanged: (next) => setState(() => _draft[rule.id] = next),
                            ),
                          ],
                        ),
                      ),
                    const SizedBox(height: EsiSpace.md),
                    FilledButton(
                      onPressed: _dirty && !_saving ? _save : null,
                      child: Text(_saving ? 'Saving…' : 'Save on the PC'),
                    ),
                  ],
                ),
    );
  }

  bool get _dirty => _draft.isNotEmpty;

  Future<void> _save() async {
    setState(() => _saving = true);
    final rules = ref.read(notificationRulesProvider).value ?? const <NotificationRule>[];
    // §6.10 has no per-rule endpoint, so the whole document goes back with the edited
    // switches applied.
    final updated = <NotificationRule>[
      for (final rule in rules)
        NotificationRule(
          id: rule.id,
          enabled: _draft[rule.id] ?? rule.enabled,
          severity: rule.severity,
          title: rule.title,
          body: rule.body,
          threshold: rule.threshold,
          sustainSeconds: rule.sustainSeconds,
          cooldownSeconds: rule.cooldownSeconds,
          builtIn: rule.builtIn,
          condition: rule.condition,
        ),
    ];
    final outcome = await ref.read(notificationRulesProvider.notifier).save(updated);
    if (!mounted) return;
    setState(() {
      _saving = false;
      if (outcome.ok) _draft.clear();
    });
    showOutcome(context, outcome);
  }
}

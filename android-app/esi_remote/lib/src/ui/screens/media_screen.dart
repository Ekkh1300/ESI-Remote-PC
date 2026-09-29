import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/connection.dart';
import '../../state/features.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

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
          final state = slice.value;

          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Control',
              subtitle: state?.defaultDevice ?? 'The default audio output on the PC',
              onRefresh: () => ref.read(mediaProvider.notifier).refresh(),
              children: <Widget>[
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _VolumePanel(state: state),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _TransportPanel(state: state),
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
  const _VolumePanel({required this.state});

  final MediaState? state;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(mediaProvider.notifier);
    final muted = state?.isMuted ?? false;
    final volume = state?.volume ?? 0;

    return EsiPanel(
      title: 'Volume',
      severity: muted ? Severity.elevated : Severity.nominal,
      trailing: state == null
          ? null
          : EsiTag(
              label: 'peak ${state!.sessionPeak}',
              colour: EsiPalette.dim,
              tooltip: 'The loudest level this output device has produced',
            ),
      child: state == null
          ? const EsiLoading(height: 90)
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    EsiIconAction(
                      icon: muted ? Icons.volume_off : Icons.volume_down,
                      tooltip: 'Volume down',
                      onPressed: state!.can('volumeDown')
                          ? () => notifier.act('volumeDown', step: 5)
                          : null,
                    ),
                    Expanded(
                      child: Slider(
                        value: volume.clamp(0, 100).toDouble(),
                        max: 100,
                        divisions: 100,
                        label: '$volume',
                        onChanged: state!.can('volumeSet')
                            ? (value) => notifier.act('volumeSet', volume: value.round())
                            : null,
                      ),
                    ),
                    EsiIconAction(
                      icon: Icons.volume_up,
                      tooltip: 'Volume up',
                      onPressed: state!.can('volumeUp')
                          ? () => notifier.act('volumeUp', step: 5)
                          : null,
                    ),
                    const SizedBox(width: EsiSpace.sm),
                    SizedBox(
                      width: 52,
                      child: Text(
                        '$volume',
                        textAlign: TextAlign.right,
                        style: EsiType.reading,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: EsiSpace.md),
                Row(
                  children: <Widget>[
                    OutlinedButton.icon(
                      onPressed: state!.can('toggleMute')
                          ? () => notifier.act('toggleMute')
                          : null,
                      icon: Icon(muted ? Icons.volume_up : Icons.volume_off, size: 16),
                      label: Text(muted ? 'Unmute' : 'Mute'),
                    ),
                    const Spacer(),
                    if (state!.availableActions.isEmpty)
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

class _TransportPanel extends ConsumerWidget {
  const _TransportPanel({required this.state});

  final MediaState? state;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(mediaProvider.notifier);
    return EsiPanel(
      title: 'Playback',
      severity: state == null ? Severity.unknown : Severity.nominal,
      // §6.7 documents a state object, not a now-playing metadata object: there is no
      // track title to show, and inventing one would be worse than saying nothing.
      footnote: 'The protocol reports transport state and volume, not what is playing. '
          'Track titles are not available from the agent.',
      child: state == null
          ? const EsiLoading(height: 90)
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
                        tooltip: state!.can(action.wire) ? action.label : '${action.label} unavailable',
                        onPressed: state!.can(action.wire)
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

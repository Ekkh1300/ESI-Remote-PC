import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/features.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/load_trace.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

/// Gaming mode.
///
/// The one number that matters here is the frame rate, and the protocol is explicit that
/// the counter behind it is best-effort, so it is labelled with its source rather than
/// presented as a measurement. Per-app suspend is the second half: gaming mode is only
/// useful if the things that fight it for CPU and disk I/O are actually paused.
class GamingScreen extends ConsumerWidget {
  const GamingScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => RequiresPairing(
        builder: (context, ref, connection) {
          if (!connection.supports(EsiFeature.gaming)) {
            return const FeatureUnavailable(feature: 'gaming', label: 'Gaming mode');
          }
          final slice = ref.watch(gamingProvider);
          final state = slice.value;

          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Gaming mode',
              subtitle: state == null
                  ? 'Reading the machine'
                  : state.isOn
                      ? 'On · ${state.profileId ?? 'no profile'}'
                      : 'Off',
              onRefresh: () => ref.read(gamingProvider.notifier).refresh(),
              children: <Widget>[
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: EsiPanel(
                    title: 'Mode',
                    severity: state == null
                        ? Severity.unknown
                        : state.isOn
                            ? Severity.nominal
                            : Severity.nominal,
                    trailing: _Toggle(
                      value: state?.isOn ?? false,
                      onChanged: (next) => _toggle(context, ref, next),
                    ),
                    child: state == null
                        ? const EsiLoading(height: 90)
                        : EsiReadoutList(
                            children: <Widget>[
                              EsiReadout(
                                label: 'Power plan',
                                value: state.activePowerScheme,
                              ),
                              EsiReadout(
                                label: 'Suspended apps',
                                value: state.suspendedApps.isEmpty
                                    ? null
                                    : state.suspendedApps.join(', '),
                              ),
                              EsiReadout(label: 'Launch app', value: state.launchAppId),
                              EsiReadout(
                                label: 'Session helper',
                                value: state.sessionAgentConnected ? 'connected' : 'not running',
                                severity: state.sessionAgentConnected
                                    ? Severity.nominal
                                    : Severity.elevated,
                              ),
                            ],
                          ),
                  ),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _PerformancePanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _ProfilesPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _SuspendedAppsPanel(),
                ),
              ],
            ),
          );
        },
      );

  Future<void> _toggle(BuildContext context, WidgetRef ref, bool enabled) async {
    final state = ref.read(gamingProvider).value;
    final ok = enabled
        ? await EsiConfirmDialog.show(
            context,
            title: 'Turn gaming mode on?',
            body: 'The PC switches to the gaming power plan, suppresses non-critical '
                'notifications and suspends the background apps you have chosen. '
                'Disabling it restores the state it captured beforehand.',
            confirmLabel: 'Turn on',
          )
        : true;
    if (!ok || !context.mounted) return;
    final outcome = await ref
        .read(gamingProvider.notifier)
        .setMode(enabled: enabled, profileId: state?.profileId);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }
}

class _Toggle extends StatelessWidget {
  const _Toggle({required this.value, required this.onChanged});

  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => Semantics(
        label: 'Gaming mode',
        toggled: value,
        child: Switch(value: value, onChanged: onChanged),
      );
}

class _PerformancePanel extends ConsumerWidget {
  const _PerformancePanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(gamingProvider).value;
    final fps = state?.fps;
    final history = state?.history ?? const <GamingHistoryPoint>[];
    final fpsSeries = <double>[
      for (final point in history)
        if (point.fps != null) point.fps!.toDouble(),
    ];

    final severity = fps == null
        ? Severity.unknown
        : fps < 30
            ? Severity.elevated
            : Severity.nominal;

    return EsiPanel(
      title: 'Frame rate',
      severity: severity,
      trailing: state?.fpsSource == null
          ? null
          : EsiTag(
              label: state!.fpsSource!,
              colour: EsiPalette.dim,
              tooltip: 'Which counter this number comes from. The protocol describes it as '
                  'best effort.',
            ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Expanded(
                child: EsiHeroReadout(
                  label: 'Frames per second',
                  value: fps == null ? null : '$fps',
                  severity: severity,
                ),
              ),
              if (state != null) ...<Widget>[
                const SizedBox(width: EsiSpace.lg),
                Expanded(
                  child: EsiReadoutList(
                    children: <Widget>[
                      EsiReadout(
                        label: 'CPU',
                        value: formatPercent(state.cpuUsagePercent),
                        severity: severityForPercent(state.cpuUsagePercent),
                      ),
                      EsiReadout(
                        label: 'GPU',
                        value: formatPercent(state.gpuUsagePercent),
                        severity: severityForPercent(state.gpuUsagePercent),
                      ),
                      EsiReadout(
                        label: 'GPU temp',
                        value: formatCelsius(state.gpuTemperatureC),
                        severity: severityForCelsius(state.gpuTemperatureC),
                      ),
                      EsiReadout(
                        label: 'Ping',
                        value: formatMilliseconds(state.pingMs),
                      ),
                    ],
                  ),
                ),
              ],
            ],
          ),
          if (fpsSeries.length < 2)
            Padding(
              padding: const EdgeInsets.only(top: EsiSpace.lg),
              child: Text(
                fps == null
                    ? 'No frame rate is being reported. The PC may not have a compatible '
                        'counter, or nothing with a frame loop is running.'
                    : 'Frame history builds up while gaming mode is on.',
                style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 11.5),
              ),
            )
          else
            Padding(
              padding: const EdgeInsets.only(top: EsiSpace.lg),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: <Widget>[
                  EsiHistoryChart(
                    values: fpsSeries,
                    colour: EsiPalette.signal,
                    baseline: _refreshRate(ref),
                    height: 62,
                  ),
                  const SizedBox(height: EsiSpace.xs),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: <Widget>[
                      Text('oldest', style: EsiType.label.copyWith(fontSize: 8.5)),
                      Text('now', style: EsiType.label.copyWith(fontSize: 8.5)),
                    ],
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  /// The dashed reference line, from the primary display's refresh rate when the agent
  /// reports one. It is the only honest "target" available: the game's own frame cap is
  /// not exposed by the protocol.
  static double? _refreshRate(WidgetRef ref) {
    final displays = ref.watch(remoteStatusProvider).value?.displays;
    if (displays == null || displays.isEmpty) return null;
    final primary = displays.firstWhere(
      (d) => d.isPrimary,
      orElse: () => displays.first,
    );
    return primary.refreshRateHz;
  }
}

class _ProfilesPanel extends ConsumerWidget {
  const _ProfilesPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final profiles = ref.watch(gamingProfilesProvider).value ?? const <GamingProfile>[];
    final current = ref.watch(gamingProvider).value?.profileId;

    return EsiPanel(
      title: 'Profiles',
      severity: profiles.isEmpty ? Severity.unknown : Severity.nominal,
      child: profiles.isEmpty
          ? Text(
              'The agent did not return any gaming profiles.',
              style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12.5),
            )
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                for (final profile in profiles)
                  Padding(
                    padding: const EdgeInsets.only(bottom: EsiSpace.sm),
                    child: _ProfileRow(
                      profile: profile,
                      selected: profile.id == current,
                      onSelect: () => _apply(context, ref, profile),
                    ),
                  ),
              ],
            ),
    );
  }

  Future<void> _apply(BuildContext context, WidgetRef ref, GamingProfile profile) async {
    final outcome = await ref
        .read(gamingProvider.notifier)
        .setMode(enabled: true, profileId: profile.id);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }
}

class _ProfileRow extends StatelessWidget {
  const _ProfileRow({
    required this.profile,
    required this.selected,
    required this.onSelect,
  });

  final GamingProfile profile;
  final bool selected;
  final VoidCallback onSelect;

  @override
  Widget build(BuildContext context) => Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onSelect,
          borderRadius: EsiRadius.tag,
          child: Container(
            padding: const EdgeInsets.all(EsiSpace.md),
            decoration: BoxDecoration(
              borderRadius: EsiRadius.tag,
              border: Border.all(
                color: selected ? EsiPalette.signal.withValues(alpha: 0.5) : EsiPalette.edge,
              ),
              color: selected ? EsiPalette.signal.withValues(alpha: 0.07) : Colors.transparent,
            ),
            child: Row(
              children: <Widget>[
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Row(
                        children: <Widget>[
                          Text(profile.name, style: EsiType.bodyStrong.copyWith(fontSize: 13)),
                          if (profile.builtIn) ...<Widget>[
                            const SizedBox(width: EsiSpace.sm),
                            const EsiTag(label: 'built in', colour: EsiPalette.faint),
                          ],
                        ],
                      ),
                      if (profile.powerScheme != null)
                        Text(
                          profile.powerScheme!,
                          style: EsiType.mono.copyWith(fontSize: 10.5),
                        ),
                      if (profile.description != null)
                        Padding(
                          padding: const EdgeInsets.only(top: EsiSpace.xs),
                          child: Text(
                            profile.description!,
                            style: EsiType.body.copyWith(
                              color: EsiPalette.faint,
                              fontSize: 11.5,
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
                if (selected)
                  const Icon(Icons.check, size: 16, color: EsiPalette.signal),
              ],
            ),
          ),
        ),
      );
}

class _SuspendedAppsPanel extends ConsumerWidget {
  const _SuspendedAppsPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final catalog = ref.watch(appCatalogProvider).value?.apps ?? const <AppEntry>[];
    final state = ref.watch(gamingProvider).value;
    final suspended = state?.suspendedApps ?? const <String>[];

    // Only the apps that are actually configured as gaming-mode background apps are
    // offered; suspending arbitrary apps is the agent's decision, not the phone's.
    final candidates = catalog
        .where((app) => app.enabled && !app.pinned)
        .take(12)
        .toList();

    return EsiPanel(
      title: 'Background apps',
      severity: candidates.isEmpty ? Severity.unknown : Severity.nominal,
      footnote: 'Suspending an app frees its CPU and disk bandwidth, and resumes it when '
          'gaming mode is switched off.',
      child: candidates.isEmpty
          ? Text(
              'The agent did not return a launcher catalog, so there is nothing to suspend '
              'from here.',
              style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12.5),
            )
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                for (final app in candidates)
                  Builder(
                    builder: (context) {
                      final isSuspended = suspended.contains(app.id);
                      return Padding(
                    padding: const EdgeInsets.only(bottom: EsiSpace.xs),
                    child: Row(
                      children: <Widget>[
                        Expanded(
                          child: Text(
                            app.name,
                            style: EsiType.body.copyWith(fontSize: 13),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        Text(
                          isSuspended ? 'suspended' : 'running',
                          style: EsiType.label.copyWith(
                            fontSize: 9,
                            color: isSuspended ? EsiPalette.flare : EsiPalette.faint,
                          ),
                        ),
                        const SizedBox(width: EsiSpace.sm),
                        SizedBox(
                          height: 30,
                          child: FilledButton(
                            style: FilledButton.styleFrom(
                              minimumSize: const Size(0, 30),
                              padding: const EdgeInsets.symmetric(horizontal: EsiSpace.md),
                              backgroundColor: isSuspended
                                  ? EsiPalette.edge
                                  : EsiPalette.flare.withValues(alpha: 0.18),
                              foregroundColor:
                                  isSuspended ? EsiPalette.dim : EsiPalette.flare,
                            ),
                            onPressed: () => _toggle(context, ref, app, isSuspended),
                            child: Text(isSuspended ? 'Resume' : 'Suspend'),
                          ),
                        ),
                      ],
                    ),
                  );
                    },
                  ),
              ],
            ),
    );
  }

  Future<void> _toggle(
    BuildContext context,
    WidgetRef ref,
    AppEntry app,
    bool isSuspended,
  ) async {
    final outcome = await ref
        .read(gamingProvider.notifier)
        .setAppSuspended(appId: app.id, suspended: !isSuspended);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }
}

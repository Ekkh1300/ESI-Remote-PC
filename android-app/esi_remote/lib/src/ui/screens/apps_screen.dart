import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/features.dart';
import '../../state/metrics.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

/// The launcher catalog and the processes running on the PC.
///
/// Two tabs, because they answer different questions: "what can I start?" and "what is
/// running that I should stop?". `runAs` is shown on every entry, because "launches on the
/// user's desktop" and "runs silently in session 0" behave very differently and the
/// distinction is invisible otherwise.
class AppsScreen extends ConsumerStatefulWidget {
  const AppsScreen({super.key});

  @override
  ConsumerState<AppsScreen> createState() => _AppsScreenState();
}

class _AppsScreenState extends ConsumerState<AppsScreen> with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 2, vsync: this);

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => RequiresPairing(
        builder: (context, ref, connection) {
          if (!connection.supports(EsiFeature.appLauncher)) {
            return const FeatureUnavailable(feature: 'appLauncher', label: 'The app launcher');
          }
          return OfflineGuard(
            connection: connection,
            child: Column(
              children: <Widget>[
                Padding(
                  padding: const EdgeInsets.fromLTRB(
                    EsiSpace.lg,
                    EsiSpace.lg,
                    EsiSpace.lg,
                    EsiSpace.sm,
                  ),
                  child: Row(
                    children: <Widget>[
                      const Expanded(child: Text('Apps', style: EsiType.screenTitle)),
                      EsiIconAction(
                        icon: Icons.refresh,
                        tooltip: 'Reload',
                        onPressed: () {
                          ref.invalidate(appCatalogProvider);
                          ref.invalidate(runningAppsProvider);
                          ref.invalidate(processesProvider);
                        },
                      ),
                    ],
                  ),
                ),
                TabBar(
                  controller: _tabs,
                  labelColor: EsiPalette.signal,
                  unselectedLabelColor: EsiPalette.dim,
                  indicatorColor: EsiPalette.signal,
                  indicatorSize: TabBarIndicatorSize.tab,
                  dividerColor: EsiPalette.edge,
                  labelStyle: EsiType.button.copyWith(fontSize: 13),
                  tabs: const <Widget>[
                    Tab(text: 'Catalog'),
                    Tab(text: 'Running'),
                  ],
                ),
                Expanded(
                  child: TabBarView(
                    controller: _tabs,
                    children: const <Widget>[_CatalogTab(), _RunningTab()],
                  ),
                ),
              ],
            ),
          );
        },
      );
}

class _CatalogTab extends ConsumerWidget {
  const _CatalogTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final catalog = ref.watch(appCatalogProvider);
    return catalog.when(
      loading: () => const EsiLoading(),
      error: (error, _) => EsiErrorView(
        message: 'The launcher catalog could not be read.',
        detail: error.toString(),
        onRetry: () => ref.invalidate(appCatalogProvider),
      ),
      data: (value) {
        if (value.apps.isEmpty) {
          return const EsiEmptyState(
            icon: Icons.apps_outlined,
            title: 'No applications are configured',
            body: 'The agent returned an empty catalog. Add entries in its settings on the '
                'PC, or launch an executable by path.',
          );
        }
        final byCategory = <String, List<AppEntry>>{};
        for (final app in value.apps) {
          byCategory.putIfAbsent(app.category, () => <AppEntry>[]).add(app);
        }
        final categories = byCategory.keys.toList()..sort();

        return ListView(
          padding: const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.md, EsiSpace.lg, EsiSpace.huge),
          children: <Widget>[
            for (final category in categories) ...<Widget>[
              Padding(
                padding: const EdgeInsets.only(top: EsiSpace.md, bottom: EsiSpace.sm),
                child: Text(category, style: EsiType.label),
              ),
              EsiSurface(
                tint: EsiPalette.hull,
                opacity: 0.5,
                padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
                child: Column(
                  children: <Widget>[
                    for (var i = 0; i < byCategory[category]!.length; i++) ...<Widget>[
                      if (i > 0) const Divider(height: 1),
                      _AppRow(app: byCategory[category]![i]),
                    ],
                  ],
                ),
              ),
            ],
          ],
        );
      },
    );
  }
}

class _AppRow extends ConsumerWidget {
  const _AppRow({required this.app});

  final AppEntry app;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final actions = ref.read(appActionsProvider);
    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: EsiSpace.md,
        vertical: EsiSpace.sm,
      ),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Flexible(
                      child: Text(
                        app.name,
                        style: EsiType.bodyStrong.copyWith(fontSize: 13.5),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    if (app.pinned) ...<Widget>[
                      const SizedBox(width: EsiSpace.sm),
                      const EsiTag(label: 'pinned', colour: EsiPalette.faint),
                    ],
                    if (app.suspended) ...<Widget>[
                      const SizedBox(width: EsiSpace.xs),
                      const EsiTag(label: 'suspended', colour: EsiPalette.flare),
                    ],
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  '${_runAsLabel(app.runAs)}'
                  '${app.lastLaunched != null ? ' · last launched ${formatRelative(app.lastLaunched)}' : ''}',
                  style: EsiType.mono.copyWith(fontSize: 10.5),
                ),
                if (!app.exists)
                  Padding(
                    padding: const EdgeInsets.only(top: EsiSpace.xs),
                    child: Text(
                      'The executable is missing on the PC.',
                      style: EsiType.body.copyWith(color: EsiPalette.flare, fontSize: 11.5),
                    ),
                  ),
              ],
            ),
          ),
          const SizedBox(width: EsiSpace.sm),
          EsiIconAction(
            icon: Icons.play_arrow,
            tooltip: app.enabled ? 'Launch ${app.name}' : 'Disabled on the PC',
            colour: app.enabled ? EsiPalette.signal : null,
            onPressed: app.enabled ? () => _launch(context, actions, app) : null,
          ),
          const SizedBox(width: EsiSpace.xs),
          EsiIconAction(
            icon: Icons.stop_circle_outlined,
            tooltip: 'Close ${app.name}',
            onPressed: () => _confirmTerminate(context, actions, app),
          ),
        ],
      ),
    );
  }

  static String _runAsLabel(String runAs) => switch (runAs) {
        'service' => 'runs in the background, no window',
        'elevatedUser' => 'runs as the user, with administrator rights',
        _ => 'opens on the user\'s desktop',
      };

  Future<void> _launch(BuildContext context, AppActions actions, AppEntry app) async {
    final outcome = await actions.launch(app);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }

  Future<void> _confirmTerminate(
    BuildContext context,
    AppActions actions,
    AppEntry app,
  ) async {
    final graceful = await EsiConfirmDialog.show(
      context,
      title: 'Close ${app.name}?',
      body: 'The process is asked to close normally. If it has unsaved work it may show a '
          'prompt on the PC, or ignore the request entirely.',
      confirmLabel: 'Ask it to close',
    );
    if (!graceful) return;
    if (!context.mounted) return;

    var outcome = await actions.terminate(app);
    if (!context.mounted) return;
    if (outcome.ok) {
      showOutcome(context, outcome);
      return;
    }

    // The graceful attempt did not land; offer the forceful one explicitly rather than
    // escalating on its own.
    final forced = await EsiConfirmDialog.show(
      context,
      title: 'Force it?',
      body: '${app.name} did not close when asked. Forcing ends the process immediately, '
          'and anything it has not written to disk is lost.',
      confirmLabel: 'Force close',
      destructive: true,
      requiresTyping: 'FORCE',
    );
    if (!forced || !context.mounted) return;
    outcome = await actions.terminate(app, force: true);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }
}

class _RunningTab extends ConsumerStatefulWidget {
  const _RunningTab();

  @override
  ConsumerState<_RunningTab> createState() => _RunningTabState();
}

class _RunningTabState extends ConsumerState<_RunningTab> {
  /// What the list is ordered by. Starts on CPU because that is the reading this screen
  /// exists to show; the chips beside "All processes" change it.
  ProcessSort _sort = ProcessSort.cpu;

  @override
  Widget build(BuildContext context) {
    final tracked = ref.watch(runningAppsProvider);
    final processes = ref.watch(processesProvider);

    return ListView(
      padding: const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.md, EsiSpace.lg, EsiSpace.huge),
      children: <Widget>[
        if (tracked.hasValue && tracked.value!.isNotEmpty) ...<Widget>[
          Text('Configured applications', style: EsiType.label),
          const SizedBox(height: EsiSpace.sm),
          EsiSurface(
            tint: EsiPalette.hull,
            opacity: 0.5,
            padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
            child: Column(
              children: <Widget>[
                for (var i = 0; i < tracked.value!.length; i++) ...<Widget>[
                  if (i > 0) const Divider(height: 1),
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: EsiSpace.md,
                      vertical: EsiSpace.sm,
                    ),
                    child: Row(
                      children: <Widget>[
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: <Widget>[
                              Text(
                                tracked.value![i].name,
                                style: EsiType.bodyStrong.copyWith(fontSize: 13),
                              ),
                              Text(
                                'pid ${tracked.value![i].processId} · '
                                '${formatBytes(tracked.value![i].workingSetBytes)} · '
                                'started ${formatRelative(tracked.value![i].startedAt)}',
                                style: EsiType.mono.copyWith(fontSize: 10.5),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(height: EsiSpace.lg),
        ],
        Row(
          children: <Widget>[
            Expanded(child: Text('All processes', style: EsiType.label)),
            for (final option in ProcessSort.values)
              Padding(
                padding: const EdgeInsets.only(left: EsiSpace.xs),
                child: InkWell(
                  borderRadius: EsiRadius.tag,
                  onTap: () => setState(() => _sort = option),
                  child: EsiTag(
                    label: option.label,
                    colour: _sort == option ? EsiPalette.signal : EsiPalette.faint,
                    filled: _sort == option,
                    tooltip: 'Order by ${option.label.toLowerCase()}',
                  ),
                ),
              ),
          ],
        ),
        const SizedBox(height: EsiSpace.sm),
        processes.when(
          loading: () => const EsiLoading(height: 160),
          error: (error, _) => EsiErrorView(
            message: 'The process list could not be read.',
            detail: error.toString(),
            onRetry: () => ref.invalidate(processesProvider),
          ),
          data: (list) {
            if (list.isEmpty) {
              return const EsiEmptyState(
                icon: Icons.memory,
                title: 'No processes reported',
                body: 'The agent returned an empty list.',
              );
            }
            final sorted = _sort.apply(list);
            return EsiSurface(
              tint: EsiPalette.hull,
              opacity: 0.5,
              padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
              child: Column(
                children: <Widget>[
                  for (var i = 0; i < sorted.length; i++) ...<Widget>[
                    if (i > 0) const Divider(height: 1),
                    _ProcessRow(process: sorted[i]),
                  ],
                ],
              ),
            );
          },
        ),
      ],
    );
  }
}

enum ProcessSort {
  cpu('CPU'),
  memory('Memory'),
  name('Name');

  const ProcessSort(this.label);
  final String label;

  List<ProcessInfo> apply(List<ProcessInfo> input) {
    final copy = <ProcessInfo>[...input];
    copy.sort(switch (this) {
      // A process with no reading yet has been listed once, which says nothing about how
      // hard it is working. It sorts last rather than first: "not measured" is not "idle",
      // and it is certainly not "busiest".
      ProcessSort.cpu => (a, b) {
          final aCpu = a.cpuPercent;
          final bCpu = b.cpuPercent;
          if (aCpu == null && bCpu == null) return 0;
          if (aCpu == null) return 1;
          if (bCpu == null) return -1;
          return bCpu.compareTo(aCpu);
        },
      ProcessSort.memory => (a, b) => b.workingSetBytes.compareTo(a.workingSetBytes),
      ProcessSort.name => (a, b) =>
          a.name.toLowerCase().compareTo(b.name.toLowerCase()),
    });
    return copy;
  }
}

class _ProcessRow extends ConsumerWidget {
  const _ProcessRow({required this.process});

  final ProcessInfo process;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final cpu = process.cpuPercent;
    final severity = severityForPercent(cpu ?? 0, elevated: 25, critical: 60);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: EsiSpace.md, vertical: EsiSpace.sm),
      child: Row(
        children: <Widget>[
          SizedBox(
            width: 52,
            child: Text(
              '${process.pid}',
              style: EsiType.mono.copyWith(fontSize: 10.5, color: EsiPalette.faint),
            ),
          ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Flexible(
                      child: Text(
                        process.name,
                        style: EsiType.body.copyWith(fontSize: 12.5),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    if (process.isElevated) ...<Widget>[
                      const SizedBox(width: EsiSpace.sm),
                      const EsiTag(label: 'admin', colour: EsiPalette.lock),
                    ],
                  ],
                ),
                Text(
                  // The agent needs two listings to measure a rate, so the first one carries
                  // no figure at all. Saying so is the difference between "this has not been
                  // measured yet" and a confident 0.0% that reads as a machine at rest.
                  cpu == null
                      ? 'measuring… · ${formatBytes(process.workingSetBytes)}'
                      : '${formatPercent(cpu, decimals: 1)} CPU · '
                            '${formatBytes(process.workingSetBytes)}'
                            '${process.user != null ? ' · ${process.user}' : ''}',
                  style: EsiType.mono.copyWith(fontSize: 10),
                ),
              ],
            ),
          ),
          const SizedBox(width: EsiSpace.sm),
          SizedBox(
            width: 54,
            child: EsiBar(fraction: (cpu ?? 0) / 100, severity: severity, height: 3),
          ),
          const SizedBox(width: EsiSpace.sm),
          EsiIconAction(
            icon: Icons.close,
            size: 30,
            tooltip: 'Ask ${process.name} to close',
            onPressed: () => _kill(context, ref),
          ),
        ],
      ),
    );
  }

  Future<void> _kill(BuildContext context, WidgetRef ref) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Close ${process.name}?',
      body: 'The process is asked to close. If it has unsaved work it may show a prompt on '
          'the PC instead of ending.',
      confirmLabel: 'Ask it to close',
    );
    if (!ok || !context.mounted) return;
    final outcome = await ref.read(appActionsProvider).killProcess(process.pid);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }
}

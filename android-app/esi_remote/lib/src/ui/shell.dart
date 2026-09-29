import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../net/esi_ws.dart';
import '../state/connection.dart';
import '../state/features.dart';
import '../state/metrics.dart';
import 'format.dart';
import 'theme.dart';
import 'widgets/load_trace.dart';
import 'widgets/panel.dart';
import 'widgets/states.dart';

/// The navigation shell: a left rail plus a persistent status spine.
///
/// The rail rather than a bottom bar, because this is a control surface for a machine, not
/// a phone-first consumer app: the destinations are peers of each other, and a vertical
/// rail leaves the full width for the readouts, which are the thing that matters.
class AppShell extends ConsumerWidget {
  const AppShell({super.key, required this.navigationShell});

  final StatefulNavigationShell navigationShell;

  static const List<_Destination> _destinations = <_Destination>[
    _Destination('Dashboard', Icons.speed_outlined, Icons.speed, '/dashboard'),
    _Destination('Power', Icons.power_settings_new_outlined, Icons.power_settings_new, '/power'),
    _Destination('Apps', Icons.apps_outlined, Icons.apps, '/apps'),
    _Destination('Screen', Icons.desktop_windows_outlined, Icons.desktop_windows, '/remote'),
    _Destination('Control', Icons.tune_outlined, Icons.tune, '/media'),
  ];

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(notificationsProvider).value?.unreadCount ?? 0;
    final connection = ref.watch(connectionProvider);

    return Scaffold(
      body: Column(
        children: <Widget>[
          StatusSpine(connection: connection),
          Expanded(
            child: Row(
              children: <Widget>[
                _NavRail(
                  currentIndex: navigationShell.currentIndex,
                  unread: unread,
                  online: connection.isOnline,
                  onSelect: (index) => navigationShell.goBranch(
                    index,
                    // Tapping the current tab pops it back to its root, which is what a
                    // person expects from a tab they are already on.
                    initialLocation: index == navigationShell.currentIndex,
                  ),
                ),
                const VerticalDivider(width: 1),
                Expanded(child: navigationShell),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _Destination {
  const _Destination(this.label, this.icon, this.selectedIcon, this.prefix);
  final String label;
  final IconData icon;
  final IconData selectedIcon;

  /// Every screen under this tab starts with this path segment.
  final String prefix;
}

class _NavRail extends StatelessWidget {
  const _NavRail({
    required this.currentIndex,
    required this.onSelect,
    required this.unread,
    required this.online,
  });

  final int currentIndex;
  final ValueChanged<int> onSelect;
  final int unread;
  final bool online;

  @override
  Widget build(BuildContext context) {
    const destinations = AppShell._destinations;
    return Container(
      width: 74,
      color: EsiPalette.void_.withValues(alpha: 0.6),
      child: SafeArea(
        right: false,
        child: Column(
          children: <Widget>[
            const SizedBox(height: EsiSpace.md),
            for (var i = 0; i < destinations.length; i++)
              Expanded(
                child: _RailButton(
                  destination: destinations[i],
                  selected: i == currentIndex,
                  badge: destinations[i].prefix == '/media' && unread > 0 ? unread : 0,
                  offline: !online,
                  onTap: () => onSelect(i),
                ),
              ),
            const SizedBox(height: EsiSpace.sm),
          ],
        ),
      ),
    );
  }
}

class _RailButton extends StatelessWidget {
  const _RailButton({
    required this.destination,
    required this.selected,
    required this.onTap,
    required this.offline,
    this.badge = 0,
  });

  final _Destination destination;
  final bool selected;
  final VoidCallback onTap;
  final bool offline;
  final int badge;

  @override
  Widget build(BuildContext context) {
    final colour = selected ? EsiPalette.signal : EsiPalette.dim;
    return Semantics(
      selected: selected,
      button: true,
      label: badge > 0 ? '${destination.label}, $badge unread' : destination.label,
      child: InkWell(
        onTap: onTap,
        child: SizedBox(
          width: double.infinity,
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: <Widget>[
              // The selected marker is a bar in the gutter, matching the panel signal
              // ticks: the same structural device everywhere.
              AnimatedContainer(
                duration: const Duration(milliseconds: 180),
                width: selected ? 3 : 0,
                height: 18,
                decoration: BoxDecoration(
                  color: EsiPalette.signal,
                  borderRadius: EsiRadius.chip,
                ),
              ),
              const SizedBox(height: 4),
              Stack(
                clipBehavior: Clip.none,
                children: <Widget>[
                  Icon(
                    selected ? destination.selectedIcon : destination.icon,
                    size: 21,
                    color: offline ? EsiPalette.faint : colour,
                  ),
                  if (badge > 0)
                    Positioned(
                      right: -7,
                      top: -5,
                      child: Container(
                        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                        constraints: const BoxConstraints(minWidth: 15),
                        decoration: BoxDecoration(
                          color: EsiPalette.alarm,
                          borderRadius: EsiRadius.chip,
                        ),
                        child: Text(
                          badge > 99 ? '99+' : '$badge',
                          textAlign: TextAlign.center,
                          style: EsiType.label.copyWith(
                            color: EsiPalette.void_,
                            fontSize: 8.5,
                            letterSpacing: 0,
                          ),
                        ),
                      ),
                    ),
                ],
              ),
              const SizedBox(height: 3),
              Text(
                destination.label,
                style: EsiType.label.copyWith(
                  color: selected ? EsiPalette.ink : EsiPalette.faint,
                  fontSize: 8.5,
                  letterSpacing: 0.3,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The persistent status line above everything.
///
/// It exists because two different failures look identical from inside a screen: "the PC is
/// asleep" and "this phone's key was rotated". The spine always says which machine, whether
/// the live stream is up, and what went wrong last, so no screen has to invent that.
class StatusSpine extends ConsumerWidget {
  const StatusSpine({super.key, required this.connection});

  final ConnectionStatus connection;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final machine = ref.watch(activeMachineProvider);
    final socketLive = connection.socketState == EsiSocketState.live;
    final handshake = connection.handshake;

    final (Color colour, String state) = switch (connection) {
      final c when c.error != null => (EsiPalette.alarm, 'Not reachable'),
      final c when c.handshake == null => (EsiPalette.flare, 'Connecting'),
      _ when socketLive => (EsiPalette.live, 'Live'),
      _ => (EsiPalette.signal, 'Polling'),
    };

    return Container(
      height: 44,
      decoration: const BoxDecoration(
        color: EsiPalette.void_,
        border: Border(bottom: BorderSide(color: EsiPalette.edge)),
      ),
      child: SafeArea(
        bottom: false,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
          child: Row(
            children: <Widget>[
              Container(
                width: 2,
                height: 16,
                decoration: BoxDecoration(color: colour, borderRadius: EsiRadius.chip),
              ),
              const SizedBox(width: EsiSpace.md),
              Text(
                agentLabel(handshake, machine?.displayName),
                style: EsiType.bodyStrong.copyWith(fontSize: 13),
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(width: EsiSpace.sm),
              if (handshake != null)
                Text(
                  machine?.displayAddress ?? '',
                  style: EsiType.mono.copyWith(fontSize: 10.5, color: EsiPalette.faint),
                  overflow: TextOverflow.ellipsis,
                ),
              const Spacer(),
              if (connection.busy)
                const SizedBox(
                  width: 13,
                  height: 13,
                  child: CircularProgressIndicator(strokeWidth: 1.6),
                )
              else
                EsiTag(label: state, colour: colour, filled: true),
              const SizedBox(width: EsiSpace.sm),
              IconButton(
                onPressed: () => ref.read(connectionProvider.notifier).refresh(),
                icon: const Icon(Icons.refresh, size: 18),
                color: EsiPalette.dim,
                tooltip: 'Re-check the connection',
                visualDensity: VisualDensity.compact,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// A shared page frame: title, optional subtitle, actions, and the scrollable content on
/// the same padding everywhere.
class EsiPage extends StatelessWidget {
  const EsiPage({
    super.key,
    required this.title,
    required this.children,
    this.subtitle,
    this.actions,
    this.onRefresh,
    this.floating,
  });

  final String title;
  final String? subtitle;
  final List<Widget> children;
  final List<Widget>? actions;
  final Future<void> Function()? onRefresh;
  final Widget? floating;

  @override
  Widget build(BuildContext context) {
    final content = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        Padding(
          padding: const EdgeInsets.fromLTRB(
            EsiSpace.lg,
            EsiSpace.lg,
            EsiSpace.lg,
            EsiSpace.md,
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: <Widget>[
                    Text(title, style: EsiType.screenTitle),
                    if (subtitle != null) ...<Widget>[
                      const SizedBox(height: EsiSpace.xs),
                      Text(
                        subtitle!,
                        style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
                      ),
                    ],
                  ],
                ),
              ),
              if (actions != null) ...<Widget>[
                const SizedBox(width: EsiSpace.md),
                Wrap(spacing: EsiSpace.sm, children: actions!),
              ],
            ],
          ),
        ),
        ...children,
        const SizedBox(height: EsiSpace.huge),
      ],
    );

    return Scaffold(
      backgroundColor: EsiPalette.void_,
      floatingActionButton: floating,
      body: SafeArea(
        top: false,
        child: onRefresh == null
            ? SingleChildScrollView(
                padding: const EdgeInsets.only(right: EsiSpace.lg),
                child: content,
              )
            : RefreshIndicator(
                onRefresh: onRefresh!,
                color: EsiPalette.signal,
                backgroundColor: EsiPalette.deck,
                child: SingleChildScrollView(
                  padding: const EdgeInsets.only(right: EsiSpace.lg),
                  physics: const AlwaysScrollableScrollPhysics(),
                  child: content,
                ),
              ),
      ),
    );
  }
}

/// Wraps a screen's body so a machine that is not reachable says so once, loudly, at the
/// top, instead of every panel showing its own slightly different failure.
class OfflineGuard extends ConsumerWidget {
  const OfflineGuard({super.key, required this.child, required this.connection});

  final Widget child;
  final ConnectionStatus connection;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (connection.error == null) return child;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        Padding(
          padding: const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.md, EsiSpace.lg, 0),
          child: EsiSurface(
            tint: EsiPalette.alarm,
            opacity: 0.1,
            border: EsiPalette.alarm.withValues(alpha: 0.4),
            padding: const EdgeInsets.all(EsiSpace.md),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                const Icon(Icons.link_off, size: 17, color: EsiPalette.alarm),
                const SizedBox(width: EsiSpace.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Text(
                        connection.error!,
                        style: EsiType.bodyStrong.copyWith(color: EsiPalette.alarm, fontSize: 13),
                      ),
                      if (connection.errorDetail != null) ...<Widget>[
                        const SizedBox(height: EsiSpace.xs),
                        Text(
                          connection.errorDetail!,
                          style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12),
                        ),
                      ],
                    ],
                  ),
                ),
                const SizedBox(width: EsiSpace.sm),
                IconButton(
                  onPressed: () => ref.read(connectionProvider.notifier).refresh(),
                  icon: const Icon(Icons.refresh, size: 17),
                  color: EsiPalette.alarm,
                  visualDensity: VisualDensity.compact,
                  tooltip: 'Try again',
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: EsiSpace.md),
        Expanded(child: child),
      ],
    );
  }
}

/// A compact "not available" note for a feature the agent did not advertise, which is the
/// other half of PROTOCOL.md §10: grey out rather than fail.
class FeatureUnavailable extends StatelessWidget {
  const FeatureUnavailable({super.key, required this.feature, required this.label});

  final String feature;
  final String label;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.all(EsiSpace.lg),
        child: EsiEmptyState(
          icon: Icons.block_outlined,
          title: '$label is switched off on the PC',
          body: 'The agent reports that its "$feature" capability is disabled. Turn it on '
              'in the agent settings on the PC and this screen will start working.',
          accent: EsiPalette.dim,
        ),
      );
}

/// Shows a transient result of an action, with the outcome the agent reported.
void showOutcome(BuildContext context, ({bool ok, String message}) outcome) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(
      SnackBar(
        content: Row(
          children: <Widget>[
            Icon(
              outcome.ok ? Icons.check_circle_outline : Icons.error_outline,
              size: 17,
              color: outcome.ok ? EsiPalette.signal : EsiPalette.alarm,
            ),
            const SizedBox(width: EsiSpace.md),
            Expanded(child: Text(outcome.message)),
          ],
        ),
        duration: Duration(seconds: outcome.ok ? 3 : 6),
      ),
    );
}

/// A small square icon button used throughout the dense screens.
class EsiIconAction extends StatelessWidget {
  const EsiIconAction({
    super.key,
    required this.icon,
    required this.onPressed,
    this.tooltip,
    this.colour,
    this.size = 38,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final String? tooltip;
  final Color? colour;
  final double size;

  @override
  Widget build(BuildContext context) {
    final button = Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onPressed,
        borderRadius: EsiRadius.tag,
        child: Container(
          width: size,
          height: size,
          decoration: BoxDecoration(
            borderRadius: EsiRadius.tag,
            border: Border.all(color: EsiPalette.edge),
            color: EsiPalette.hull.withValues(alpha: 0.5),
          ),
          child: Icon(
            icon,
            size: 18,
            color: onPressed == null ? EsiPalette.faint : (colour ?? EsiPalette.dim),
          ),
        ),
      ),
    );
    if (tooltip == null) return button;
    return Tooltip(message: tooltip!, child: button);
  }
}

/// A two-column grid that collapses to one column on a narrow phone, so the dashboard
/// never squeezes a reading into illegibility.
class EsiGrid extends StatelessWidget {
  const EsiGrid({
    super.key,
    required this.children,
    this.minColumnWidth = 300,
    this.spacing = EsiSpace.md,
  });

  final List<Widget> children;
  final double minColumnWidth;
  final double spacing;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) {
          final columns = math.max(1, (constraints.maxWidth / minColumnWidth).floor());
          if (columns <= 1) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: <Widget>[
                for (var i = 0; i < children.length; i++) ...<Widget>[
                  if (i > 0) SizedBox(height: spacing),
                  children[i],
                ],
              ],
            );
          }
          return Wrap(
            spacing: spacing,
            runSpacing: spacing,
            children: <Widget>[
              for (final child in children)
                SizedBox(
                  width: (constraints.maxWidth - spacing * (columns - 1)) / columns,
                  child: child,
                ),
            ],
          );
        },
      );
}

/// The trace channels the dashboard draws, built from whatever the agent reported.
List<TraceChannel> buildTraceChannels(MetricsState metrics) => <TraceChannel>[
      TraceChannel(
        label: 'CPU',
        series: metrics.cpuHistory,
        colour: EsiPalette.signal,
      ),
      TraceChannel(
        label: 'GPU',
        series: metrics.gpuHistory,
        colour: EsiPalette.flare,
      ),
      TraceChannel(
        label: 'RAM',
        series: metrics.ramHistory,
        colour: EsiPalette.lock,
      ),
    ];

/// Shared helper for the screens that need both a connection and a socket guard.
class RequiresPairing extends ConsumerWidget {
  const RequiresPairing({super.key, required this.builder});

  final Widget Function(BuildContext context, WidgetRef ref, ConnectionStatus status) builder;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final machine = ref.watch(activeMachineProvider);
    final connection = ref.watch(connectionProvider);
    if (machine == null) {
      return Scaffold(
        body: EsiEmptyState(
          icon: Icons.link_off,
          title: 'No PC paired yet',
          body: 'Pair with a PC before using this screen.',
          action: FilledButton(
            onPressed: () => context.go('/onboarding'),
            child: const Text('Find a PC'),
          ),
        ),
      );
    }
    if (!machine.isPaired) {
      return Scaffold(
        body: EsiEmptyState(
          icon: Icons.key_off_outlined,
          title: 'Pairing did not finish',
          body: 'This PC was found but the credentials were never received, so nothing '
              'can be signed. Start pairing again.',
          action: FilledButton(
            onPressed: () => context.go('/onboarding'),
            child: const Text('Pair again'),
          ),
        ),
      );
    }
    return builder(context, ref, connection);
  }
}

/// A time source that ticks once a second, for countdown labels. Kept in one place so the
/// pairing and status screens do not each own a timer.
class EsiTicker extends StatefulWidget {
  const EsiTicker({super.key, required this.builder, this.interval = const Duration(seconds: 1)});

  final Widget Function(BuildContext context, int tick) builder;
  final Duration interval;

  @override
  State<EsiTicker> createState() => _EsiTickerState();
}

class _EsiTickerState extends State<EsiTicker> {
  Timer? _timer;
  int _tick = 0;

  @override
  void initState() {
    super.initState();
    _timer = Timer.periodic(widget.interval, (_) {
      if (mounted) setState(() => _tick++);
    });
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.builder(context, _tick);
}

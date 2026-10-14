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
import 'widgets/nav_bar.dart';
import 'widgets/panel.dart';
import 'widgets/states.dart';

/// The navigation shell: a persistent status spine across the top and a navigation bar
/// across the foot.
///
/// The destinations are peers of each other and the content between the two is the point,
/// so both pieces of chrome hug the horizontal edges and the whole width belongs to the
/// readouts. The bar used to be a rail down the left, which cost that width on every screen
/// to buy back nothing that a bar across the bottom does not already give.
class AppShell extends ConsumerWidget {
  const AppShell({super.key, required this.navigationShell});

  final StatefulNavigationShell navigationShell;

  static const List<EsiNavDestination> destinations = <EsiNavDestination>[
    EsiNavDestination(
      label: 'Dashboard',
      icon: Icons.speed_outlined,
      selectedIcon: Icons.speed,
    ),
    EsiNavDestination(
      label: 'Power',
      icon: Icons.power_settings_new_outlined,
      selectedIcon: Icons.power_settings_new,
    ),
    EsiNavDestination(
      label: 'Apps',
      icon: Icons.apps_outlined,
      selectedIcon: Icons.apps,
    ),
    EsiNavDestination(
      label: 'Screen',
      icon: Icons.desktop_windows_outlined,
      selectedIcon: Icons.desktop_windows,
    ),
    EsiNavDestination(
      label: 'Control',
      icon: Icons.tune_outlined,
      selectedIcon: Icons.tune,
    ),
  ];

  /// Control carries the unread badge, because notifications are what that tab is about.
  static const String badgeDestination = 'Control';

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(notificationsProvider).value?.unreadCount ?? 0;
    final connection = ref.watch(connectionProvider);

    return Scaffold(
      body: Column(
        children: <Widget>[
          StatusSpine(connection: connection),
          Expanded(child: navigationShell),
          EsiBottomBar(
            destinations: destinations,
            currentIndex: navigationShell.currentIndex,
            unread: unread,
            online: connection.isOnline,
            showUnreadOn: badgeDestination,
            onSelect: (index) => navigationShell.goBranch(
              index,
              // Tapping the current tab pops it back to its root, which is what a
              // person expects from a tab they are already on.
              initialLocation: index == navigationShell.currentIndex,
            ),
          ),
        ],
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
      // `bottom: false` because the shell's navigation bar below owns the bottom inset. With
      // both applying it, every screen would end in a band of padding exactly the height of
      // the gesture bar, sitting *above* the bar and holding nothing - and the content would
      // stop short of the thing it is supposed to be resting on.
      body: SafeArea(
        top: false,
        bottom: false,
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
          // D45: this used to say the capability was "switched off on the PC" and to send the
          // reader to the PC's settings. It was wrong, and confidently so: the handshake had
          // `remote: true` the whole time. The screen appeared because a provider here had not
          // finished initialising, so `supports()` fell through to false - and the message
          // described the one thing that was certainly not wrong, on the one machine the reader
          // was not looking at.
          //
          // So it now says what is actually known, and points at the thing that is actually
          // still unknown: this app has not heard from the PC.
          title: '$label is unavailable',
          body: 'This app has not received the PC\'s list of capabilities yet, so it cannot '
              'tell whether "$feature" is available. The PC may well have it switched on. '
              'Re-check the connection below; if the problem persists, update the agent on the '
              'PC.',
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

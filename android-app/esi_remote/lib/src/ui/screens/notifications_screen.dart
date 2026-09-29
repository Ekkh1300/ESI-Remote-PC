import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/connection.dart';
import '../../state/features.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';
import 'media_screen.dart' show NotificationRulesPanel;

/// The agent's notification log, and the rules behind it.
///
/// Notifications arrive on the live socket, but the list is re-read from the agent rather
/// than appended to locally: unread counts, retention and ordering are the agent's
/// answer, and a phone-side guess would drift the moment two devices read the same log.
class NotificationsScreen extends ConsumerStatefulWidget {
  const NotificationsScreen({super.key});

  @override
  ConsumerState<NotificationsScreen> createState() => _NotificationsScreenState();
}

class _NotificationsScreenState extends ConsumerState<NotificationsScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 2, vsync: this);

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => RequiresPairing(
        builder: (context, ref, connection) {
          if (!connection.supports(EsiFeature.notifications)) {
            return const FeatureUnavailable(
              feature: 'notifications',
              label: 'Notifications',
            );
          }
          final slice = ref.watch(notificationsProvider);
          final page = slice.value;
          final unread = page?.unreadCount ?? 0;

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
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: <Widget>[
                            const Text('Notifications', style: EsiType.screenTitle),
                            const SizedBox(height: EsiSpace.xs),
                            Text(
                              unread > 0
                                  ? '$unread unread of ${page?.total ?? 0}'
                                  : 'Nothing unread',
                              style: EsiType.body.copyWith(
                                color: unread > 0 ? EsiPalette.flare : EsiPalette.dim,
                                fontSize: 12.5,
                              ),
                            ),
                          ],
                        ),
                      ),
                      EsiIconAction(
                        icon: Icons.done_all,
                        tooltip: unread > 0 ? 'Mark everything read' : 'Nothing to mark',
                        onPressed: unread > 0
                            ? () => ref.read(notificationsProvider.notifier).markAllRead()
                            : null,
                      ),
                      const SizedBox(width: EsiSpace.sm),
                      EsiIconAction(
                        icon: Icons.delete_sweep_outlined,
                        tooltip: 'Clear the log',
                        onPressed: page == null || page.notifications.isEmpty
                            ? null
                            : () => _confirmClear(context, ref),
                      ),
                    ],
                  ),
                ),
                TabBar(
                  controller: _tabs,
                  labelColor: EsiPalette.signal,
                  unselectedLabelColor: EsiPalette.dim,
                  indicatorColor: EsiPalette.signal,
                  dividerColor: EsiPalette.edge,
                  labelStyle: EsiType.button.copyWith(fontSize: 13),
                  tabs: const <Widget>[
                    Tab(text: 'Log'),
                    Tab(text: 'Rules'),
                  ],
                ),
                Expanded(
                  child: TabBarView(
                    controller: _tabs,
                    children: <Widget>[
                      _LogTab(slice: slice),
                      SingleChildScrollView(
                        padding: const EdgeInsets.all(EsiSpace.lg),
                        child: const NotificationRulesPanel(),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          );
        },
      );

  Future<void> _confirmClear(BuildContext context, WidgetRef ref) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Clear the notification log?',
      body: 'Every notification the PC has recorded is removed. This cannot be undone, and '
          'it does not change the rules that produced them.',
      confirmLabel: 'Clear',
      destructive: true,
    );
    if (!ok || !context.mounted) return;
    await ref.read(notificationsProvider.notifier).clearAll();
  }
}

class _LogTab extends ConsumerWidget {
  const _LogTab({required this.slice});

  final AsyncSlice<NotificationPage> slice;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (slice.loading && slice.value == null) return const EsiLoading();
    if (slice.error != null && slice.value == null) {
      return EsiErrorView(
        message: slice.error!,
        detail: slice.errorDetail,
        onRetry: () => ref.read(notificationsProvider.notifier).refresh(),
      );
    }
    final page = slice.value;
    if (page == null || page.notifications.isEmpty) {
      return EsiEmptyState(
        icon: Icons.notifications_none,
        title: page == null ? 'The log could not be read' : 'Nothing has happened',
        body: page == null
            ? 'The agent did not return a notification list.'
            : 'The PC has not recorded any notifications. Things like a high CPU load, a '
                'full disk or a pairing attempt show up here.',
        action: page == null
            ? FilledButton(
                onPressed: () => ref.read(notificationsProvider.notifier).refresh(),
                child: const Text('Try again'),
              )
            : null,
      );
    }

    return ListView(
      padding: const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.md, EsiSpace.lg, EsiSpace.huge),
      children: <Widget>[
        EsiSurface(
          tint: EsiPalette.hull,
          opacity: 0.5,
          padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
          child: Column(
            children: <Widget>[
              for (var i = 0; i < page.notifications.length; i++) ...<Widget>[
                if (i > 0) const Divider(height: 1),
                _NotificationRow(notification: page.notifications[i]),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _NotificationRow extends ConsumerWidget {
  const _NotificationRow({required this.notification});

  final EsiNotification notification;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final colour = switch (notification.severity) {
      'critical' => EsiPalette.alarm,
      'warning' => EsiPalette.flare,
      _ => EsiPalette.signal,
    };
    final notifier = ref.read(notificationsProvider.notifier);

    return Dismissible(
      key: ValueKey<int>(notification.id),
      direction: DismissDirection.endToStart,
      background: Container(
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.only(right: EsiSpace.lg),
        color: EsiPalette.alarm.withValues(alpha: 0.18),
        child: const Icon(Icons.delete_outline, color: EsiPalette.alarm, size: 18),
      ),
      onDismissed: (_) => notifier.dismiss(notification.id),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: notification.isRead
              ? null
              : () => notifier.markRead(notification.id),
          child: Padding(
            padding: const EdgeInsets.symmetric(
              horizontal: EsiSpace.md,
              vertical: EsiSpace.md,
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Container(
                  margin: const EdgeInsets.only(top: 5),
                  width: 6,
                  height: 6,
                  decoration: BoxDecoration(
                    color: notification.isRead ? EsiPalette.edge : colour,
                    shape: BoxShape.circle,
                    boxShadow: notification.isRead
                        ? null
                        : <BoxShadow>[
                            BoxShadow(color: colour.withValues(alpha: 0.5), blurRadius: 6),
                          ],
                  ),
                ),
                const SizedBox(width: EsiSpace.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Row(
                        children: <Widget>[
                          Expanded(
                            child: Text(
                              notification.title,
                              style: EsiType.body.copyWith(
                                fontSize: 13.5,
                                fontWeight:
                                    notification.isRead ? FontWeight.w400 : FontWeight.w600,
                              ),
                            ),
                          ),
                          Text(
                            formatRelative(notification.createdAt),
                            style: EsiType.mono.copyWith(
                              fontSize: 10,
                              color: EsiPalette.faint,
                            ),
                          ),
                        ],
                      ),
                      if (notification.body != null) ...<Widget>[
                        const SizedBox(height: 2),
                        Text(
                          notification.body!,
                          style: EsiType.body.copyWith(
                            color: EsiPalette.dim,
                            fontSize: 12,
                          ),
                        ),
                      ],
                      const SizedBox(height: EsiSpace.xs),
                      Row(
                        children: <Widget>[
                          EsiTag(label: notification.kind, colour: EsiPalette.faint),
                          const SizedBox(width: EsiSpace.xs),
                          EsiTag(
                            label: notification.severity,
                            colour: notification.isRead ? EsiPalette.faint : colour,
                          ),
                          if (notification.data.isNotEmpty) ...<Widget>[
                            const SizedBox(width: EsiSpace.xs),
                            Expanded(
                              child: Text(
                                notification.data.entries
                                    .map((e) => '${e.key}=${e.value}')
                                    .join(' '),
                                style: EsiType.mono.copyWith(fontSize: 9.5),
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                          ],
                        ],
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

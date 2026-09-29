import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../data/machine_store.dart';
import '../../net/wol.dart';
import '../../protocol/protocol_constants.dart';
import '../../protocol/models.dart';
import '../../state/connection.dart';
import '../../state/discovery.dart';
import '../../state/metrics.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

/// Power, and Wake-on-LAN.
///
/// Every action is behind a confirmation that says what will happen *on the PC*, in the
/// words the person at that keyboard would use. A dialog that says "Are you sure?" tells
/// the user nothing they did not already suspect; one that says "Windows will ask open
/// applications to close, and any that block will keep the machine alive" is worth
/// reading.
class PowerScreen extends ConsumerWidget {
  const PowerScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => RequiresPairing(
        builder: (context, ref, connection) {
          final status = ref.watch(powerProvider);
          final machine = ref.watch(activeMachineProvider)!;
          if (!connection.supports(EsiFeature.power)) {
            return const FeatureUnavailable(feature: 'power', label: 'Power control');
          }

          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Power',
              subtitle: _subtitle(status.value),
              onRefresh: () => ref.read(powerProvider.notifier).refresh(),
              children: <Widget>[
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _PowerStatusPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _WakePanel(machine: machine),
                ),
                const SizedBox(height: EsiSpace.md),
                for (final group in _actionGroups)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(
                      EsiSpace.lg,
                      EsiSpace.md,
                      EsiSpace.lg,
                      0,
                    ),
                    child: _ActionGroup(
                      title: group.title,
                      actions: group.actions,
                    ),
                  ),
              ],
            ),
          );
        },
      );

  static String _subtitle(PowerStatus? status) {
    if (status == null) return 'Reading the machine state';
    final bits = <String>[status.state];
    if (status.isLocked) bits.add('locked');
    if (status.loggedIn && status.activeUser != null) bits.add(status.activeUser!);
    if (status.pendingPowerAction != null) bits.add('${status.pendingPowerAction} pending');
    return bits.join(' · ');
  }
}

class _PowerStatusPanel extends ConsumerWidget {
  const _PowerStatusPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final slice = ref.watch(powerProvider);
    final status = slice.value;
    return EsiPanel(
      title: 'Machine state',
      severity: status == null
          ? Severity.unknown
          : status.state == 'online'
              ? Severity.nominal
              : Severity.elevated,
      child: slice.loading && status == null
          ? const EsiLoading(height: 120)
          : EsiReadoutList(
              children: <Widget>[
                EsiReadout(
                  label: 'State',
                  value: status?.state,
                  severity: status?.state == 'online' ? Severity.nominal : Severity.elevated,
                ),
                EsiReadout(
                  label: 'Signed in',
                  value: status == null ? null : (status.loggedIn ? 'yes' : 'no'),
                  severity: status?.loggedIn ?? false ? Severity.nominal : Severity.elevated,
                ),
                EsiReadout(label: 'User', value: status?.activeUser),
                EsiReadout(
                  label: 'Locked',
                  value: status == null ? null : (status.isLocked ? 'yes' : 'no'),
                ),
                EsiReadout(
                  label: 'Last boot',
                  value: formatRelative(status?.lastBoot),
                ),
                EsiReadout(
                  label: 'Supports sleep',
                  value: status == null ? null : (status.supportsSleep ? 'yes' : 'no'),
                ),
                EsiReadout(
                  label: 'Supports hibernate',
                  value: status == null ? null : (status.supportsHibernate ? 'yes' : 'no'),
                ),
                EsiReadout(
                  label: 'Pending action',
                  value: status?.pendingPowerAction,
                  severity: status?.pendingPowerAction == null ? Severity.nominal : Severity.elevated,
                ),
              ],
            ),
    );
  }
}

class _WakePanel extends ConsumerStatefulWidget {
  const _WakePanel({required this.machine});

  final PairedMachine machine;

  @override
  ConsumerState<_WakePanel> createState() => _WakePanelState();
}

class _WakePanelState extends ConsumerState<_WakePanel> {
  final TextEditingController _mac = TextEditingController();
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _mac.text = widget.machine.macAddress ?? '';
  }

  @override
  void dispose() {
    _mac.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final slice = ref.watch(wakeProvider);
    final normalised = WakeOnLan.normaliseMac(_mac.text);
    final valid = normalised != null;

    return EsiPanel(
      title: 'Wake on LAN',
      severity: valid ? Severity.nominal : Severity.unknown,
      footnote: slice.value == null
          ? 'The phone sends the magic packet directly, which only works while the PC is '
              'asleep but not powered off.'
          : null,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text(
            'The phone sends the magic packet itself, so this works even though nothing on '
            'the PC is running to answer a request. The PC has to be armed for it in the '
            'BIOS and in its network adapter.',
            style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
          ),
          const SizedBox(height: EsiSpace.md),
          TextField(
            controller: _mac,
            autocorrect: false,
            onChanged: (_) => setState(() {}),
            style: EsiType.mono.copyWith(color: EsiPalette.ink, fontSize: 13),
            decoration: InputDecoration(
              labelText: 'MAC address',
              hintText: 'AA:BB:CC:DD:EE:FF',
              errorText: _mac.text.trim().isNotEmpty && !valid
                  ? 'That is not a MAC address'
                  : null,
            ),
          ),
          const SizedBox(height: EsiSpace.md),
          Row(
            children: <Widget>[
              Expanded(
                child: FilledButton.icon(
                  onPressed: valid && !_busy ? _wake : null,
                  icon: const Icon(Icons.bolt, size: 17),
                  label: const Text('Send magic packet'),
                ),
              ),
              const SizedBox(width: EsiSpace.sm),
              EsiIconAction(
                icon: Icons.arrow_forward,
                tooltip: 'Ask the agent to send it instead',
                onPressed: valid && !_busy ? _relay : null,
              ),
            ],
          ),
          if (slice.error != null) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            Text(
              slice.error!,
              style: EsiType.body.copyWith(color: EsiPalette.alarm, fontSize: 12.5),
            ),
          ],
          if (slice.value != null) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            Text(
              'Sent ${slice.value!.sent} datagrams. Sending is not the same as waking: if '
              'the machine does not come up, Wake-on-LAN is probably not armed on it.',
              style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12),
            ),
            if (slice.value!.readiness != null) ...<Widget>[
              const SizedBox(height: EsiSpace.sm),
              const _WolReadinessNote(),
            ],
          ],
        ],
      ),
    );
  }

  Future<void> _wake() async {
    final normalised = WakeOnLan.normaliseMac(_mac.text);
    if (normalised == null) return;
    setState(() => _busy = true);
    await ref.read(wakeProvider.notifier).wake(normalised);
    if (mounted) setState(() => _busy = false);
  }

  Future<void> _relay() async {
    final normalised = WakeOnLan.normaliseMac(_mac.text);
    if (normalised == null) return;
    final outcome = await ref.read(wakeProvider.notifier).relay(normalised);
    if (!mounted) return;
    showOutcome(context, outcome);
  }
}

class _WolReadinessNote extends ConsumerWidget {
  const _WolReadinessNote();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final readiness = ref.watch(wakeProvider).value?.readiness;
    if (readiness == null) return const SizedBox.shrink();
    if (readiness.ready && readiness.issues.isEmpty) {
      return Row(
        children: <Widget>[
          const Icon(Icons.check_circle_outline, size: 14, color: EsiPalette.signal),
          const SizedBox(width: EsiSpace.sm),
          Expanded(
            child: Text(
              'The PC reports that Wake-on-LAN is armed on '
              '${readiness.armedDevices.length} adapter'
              '${readiness.armedDevices.length == 1 ? '' : 's'}.',
              style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12),
            ),
          ),
        ],
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        Row(
          children: <Widget>[
            const Icon(Icons.warning_amber_rounded, size: 14, color: EsiPalette.flare),
            const SizedBox(width: EsiSpace.sm),
            Expanded(
              child: Text(
                'The PC reports problems with Wake-on-LAN:',
                style: EsiType.body.copyWith(color: EsiPalette.flare, fontSize: 12),
              ),
            ),
          ],
        ),
        for (final issue in readiness.issues)
          Padding(
            padding: const EdgeInsets.only(left: EsiSpace.xl, top: EsiSpace.xs),
            child: Text('· $issue', style: EsiType.mono.copyWith(fontSize: 11)),
          ),
        if (readiness.hiberbootEnabled)
          Padding(
            padding: const EdgeInsets.only(left: EsiSpace.xl, top: EsiSpace.xs),
            child: Text(
              '· Fast startup is on, which turns a restart into a hibernation and breaks '
              'Wake-on-LAN after a restart.',
              style: EsiType.mono.copyWith(fontSize: 11, color: EsiPalette.flare),
            ),
          ),
      ],
    );
  }
}

class _ActionGroup extends StatelessWidget {
  const _ActionGroup({required this.title, required this.actions});

  final String title;
  final List<PowerActionRequest> actions;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          Text(title, style: EsiType.label),
          const SizedBox(height: EsiSpace.sm),
          EsiGrid(
            children: <Widget>[
              for (final action in actions) _ActionTile(action: action),
            ],
          ),
        ],
      );
}

class _ActionTile extends ConsumerWidget {
  const _ActionTile({required this.action});

  final PowerActionRequest action;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final accent = action.destructive ? EsiPalette.alarm : EsiPalette.signal;
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: () => _run(context, ref),
        borderRadius: EsiRadius.panel,
        child: EsiSurface(
          padding: const EdgeInsets.all(EsiSpace.lg),
          border: EsiPalette.edge,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Row(
                children: <Widget>[
                  Container(
                    width: 26,
                    height: 26,
                    decoration: BoxDecoration(
                      color: accent.withValues(alpha: 0.12),
                      borderRadius: EsiRadius.chip,
                    ),
                    child: Icon(_iconFor(action.action), size: 15, color: accent),
                  ),
                  const Spacer(),
                  Icon(Icons.chevron_right, size: 16, color: EsiPalette.faint),
                ],
              ),
              const SizedBox(height: EsiSpace.md),
              Text(action.title, style: EsiType.subtitle.copyWith(fontSize: 14)),
              const SizedBox(height: EsiSpace.xs),
              Text(
                action.consequence,
                style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 11.5),
                maxLines: 3,
                overflow: TextOverflow.ellipsis,
              ),
            ],
          ),
        ),
      ),
    );
  }

  static IconData _iconFor(EsiPowerAction action) => switch (action) {
        EsiPowerAction.shutdown => Icons.power_settings_new,
        EsiPowerAction.restart => Icons.restart_alt,
        EsiPowerAction.sleep => Icons.bedtime_outlined,
        EsiPowerAction.hibernate => Icons.ac_unit,
        EsiPowerAction.lock => Icons.lock_outline,
        EsiPowerAction.signout => Icons.logout,
        EsiPowerAction.forceRestart => Icons.dangerous_outlined,
        EsiPowerAction.wake => Icons.bolt,
      };

  Future<void> _run(BuildContext context, WidgetRef ref) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: action.title,
      body: '${action.question}\n\n${action.consequence}',
      confirmLabel: action.title,
      destructive: action.destructive,
      requiresTyping: action.requiresTyping,
      details: <Widget>[
        EsiDetailRow(
          label: 'Countdown',
          value: action.delaySeconds == 0
              ? 'immediate'
              : '${action.delaySeconds} seconds on the PC',
        ),
        if (action.force)
          const EsiDetailRow(
            label: 'Forced',
            value: 'applications are not asked',
            valueColour: EsiPalette.alarm,
          ),
      ],
    );
    if (!ok || !context.mounted) return;

    final outcome = await ref
        .read(powerProvider.notifier)
        .act(PowerActionRequest(
          action: action.action,
          title: action.title,
          question: action.question,
          consequence: action.consequence,
          destructive: action.destructive,
          requiresTyping: action.requiresTyping,
          force: action.force,
          delaySeconds: action.delaySeconds,
        ));
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }
}

class _ActionGroupSpec {
  const _ActionGroupSpec(this.title, this.actions);
  final String title;
  final List<PowerActionRequest> actions;
}

final List<_ActionGroupSpec> _actionGroups = <_ActionGroupSpec>[
  _ActionGroupSpec('The user is still there', <PowerActionRequest>[
    powerActionCatalogue[0],
    powerActionCatalogue[1],
  ]),
  _ActionGroupSpec('Suspend without losing work', <PowerActionRequest>[
    powerActionCatalogue[2],
    powerActionCatalogue[3],
  ]),
  _ActionGroupSpec('Take the machine down', <PowerActionRequest>[
    powerActionCatalogue[4],
    powerActionCatalogue[5],
    powerActionCatalogue[6],
  ]),
];

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app_info.dart';
import '../../data/machine_store.dart';
import '../../net/esi_api.dart';
import '../../protocol/esi_crypto.dart';
import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/connection.dart';
import '../../state/features.dart';
import '../../links.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

/// Agent settings, this phone's identity, key rotation, revocation and the audit trail.
///
/// Split by scope rather than by topic, because the agent enforces scopes: the settings
/// that actually change the machine are behind `admin`, and a phone without it sees a
/// clear statement of that rather than a screen full of switches that would fail.
class SettingsScreen extends ConsumerWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => RequiresPairing(
        builder: (context, ref, connection) {
          final machine = ref.watch(activeMachineProvider)!;
          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Settings',
              subtitle: machine.displayName,
              onRefresh: () => ref.read(settingsProvider.notifier).refresh(),
              // The one place a paired phone can get back to the PC-finding screen.
              // The spine's name row goes there too; this is the version with words on
              // it, for the reader who is looking to move machines rather than to check
              // on the current one.
              actions: <Widget>[
                OutlinedButton.icon(
                  onPressed: () => context.go('/onboarding'),
                  icon: const Icon(Icons.swap_horiz, size: 16),
                  label: const Text('Find / switch PC'),
                ),
              ],
              children: <Widget>[
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _IdentityPanel(machine: machine, connection: connection),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _AgentSettingsPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _DevicesPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _AuditPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _DangerPanel(machine: machine),
                ),
                const SizedBox(height: EsiSpace.md),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _AboutPanel(),
                ),
              ],
            ),
          );
        },
      );
}

class _IdentityPanel extends ConsumerWidget {
  const _IdentityPanel({required this.machine, required this.connection});

  final PairedMachine machine;
  final ConnectionStatus connection;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final who = connection.whoAmI;
    return EsiPanel(
      title: 'This phone',
      severity: Severity.nominal,
      trailing: EsiTag(
        label: 'key ${machine.keyId}',
        colour: EsiPalette.lock,
        icon: Icons.key,
        tooltip: 'The key generation. Rotation moves it forward; the previous one stays '
            'valid for two minutes so requests in flight do not break.',
      ),
      child: EsiReadoutList(
        children: <Widget>[
          EsiDetailRow(label: 'Device id', value: formatDeviceId(machine.deviceId)),
          EsiDetailRow(label: 'Address', value: machine.displayAddress),
          EsiDetailRow(
            label: 'Pinned key',
            value: machine.pins.join('\n'),
            valueColour: EsiPalette.lock,
          ),
          EsiDetailRow(
            label: 'Name on the PC',
            value: who?.name,
          ),
          EsiDetailRow(
            label: 'Scopes granted',
            value: who == null
                ? null
                : who.scopes.isEmpty
                    ? 'none'
                    : who.scopes.join(', '),
            valueColour: (who?.scopes.length ?? 0) < 4 ? EsiPalette.flare : null,
          ),
          EsiDetailRow(label: 'First paired', value: formatRelative(who?.firstPaired)),
          EsiDetailRow(label: 'Last seen', value: formatRelative(who?.lastSeen)),
          EsiDetailRow(label: 'Last address', value: who?.lastAddress),
          if (machine.macAddress != null)
            EsiDetailRow(label: 'Wake-on-LAN MAC', value: machine.macAddress),
          EsiDetailRow(
            label: 'Agent version',
            value: who?.agentVersion.isNotEmpty ?? false ? who!.agentVersion : machine.agentVersion,
          ),
        ],
      ),
    );
  }
}

class _AgentSettingsPanel extends ConsumerStatefulWidget {
  const _AgentSettingsPanel();

  @override
  ConsumerState<_AgentSettingsPanel> createState() => _AgentSettingsPanelState();
}

class _AgentSettingsPanelState extends ConsumerState<_AgentSettingsPanel> {
  bool _saving = false;

  @override
  Widget build(BuildContext context) {
    final connection = ref.watch(connectionProvider);
    if (!connection.hasScope(EsiScope.admin)) {
      return EsiPanel(
        title: 'Agent settings',
        severity: Severity.unknown,
        child: Text(
          'This phone does not have the admin scope, so it cannot read or change the '
          'agent\'s settings. Grant it on the PC, or pair again with the scope included.',
          style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
        ),
      );
    }

    final slice = ref.watch(settingsProvider);
    final settings = slice.value;
    if (settings == null) {
      return EsiPanel(
        title: 'Agent settings',
        severity: Severity.unknown,
        child: slice.error == null
            ? const EsiLoading(height: 140)
            : Text(
                slice.error!,
                style: EsiType.body.copyWith(color: EsiPalette.alarm, fontSize: 12.5),
              ),
      );
    }

    return EsiPanel(
      title: 'Agent settings',
      trailing: EsiIconAction(
        icon: Icons.refresh,
        tooltip: 'Reload from the PC',
        onPressed: () => ref.read(settingsProvider.notifier).refresh(),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text('Network', style: EsiType.label),
          const SizedBox(height: EsiSpace.sm),
          _SwitchRow(
            label: 'Local network only',
            detail: 'Refuses requests that did not arrive from an allowed subnet.',
            value: settings.security.localNetworkOnly,
            onChanged: (next) => _save(
              settings,
              (s) => s.copyWith(security: _copySecurity(s.security, localNetworkOnly: next)),
            ),
          ),
          _SwitchRow(
            label: 'Require signed requests',
            detail: 'Turning this off removes the authentication the phone depends on.',
            value: settings.security.requireSignedRequests,
            onChanged: (next) => _save(
              settings,
              (s) => s.copyWith(security: _copySecurity(s.security, requireSignedRequests: next)),
            ),
          ),
          _SwitchRow(
            label: 'Discovery on UDP',
            detail: 'The broadcast responder on port ${settings.discoveryPort}.',
            value: settings.discoveryEnabled,
            onChanged: (next) => _save(settings, (s) => s.copyWith(discoveryEnabled: next)),
          ),
          _SwitchRow(
            label: 'Advertise over mDNS',
            detail: 'Publishes ${EsiProtocol.mdnsServiceType} on the network.',
            value: settings.mdnsEnabled,
            onChanged: (next) => _save(settings, (s) => s.copyWith(mdnsEnabled: next)),
          ),
          _SwitchRow(
            label: 'Start with Windows',
            value: settings.startWithWindows,
            onChanged: (next) => _save(settings, (s) => s.copyWith(startWithWindows: next)),
          ),
          _SwitchRow(
            label: 'Allow Wake-on-LAN',
            value: settings.wakeOnLanEnabled,
            onChanged: (next) => _save(settings, (s) => s.copyWith(wakeOnLanEnabled: next)),
          ),
          const SizedBox(height: EsiSpace.md),
          Text('Limits', style: EsiType.label),
          const SizedBox(height: EsiSpace.sm),
          EsiReadoutList(
            children: <Widget>[
              EsiReadout(
                label: 'Maximum paired devices',
                value: '${settings.security.maxPairedDevices}',
              ),
              EsiReadout(
                label: 'Requests per minute',
                value: '${settings.security.rateLimitPerMinute}',
              ),
              EsiReadout(
                label: 'Clock skew allowed',
                value: '${settings.security.clockSkewSeconds} s',
                tooltip: 'A signed request outside this window is rejected. If the phone '
                    'and the PC clocks drift apart, requests start failing with '
                    'esi_timestamp.',
              ),
              EsiReadout(
                label: 'Pairing window',
                value: '${settings.security.pairingWindowSeconds} s',
              ),
              EsiReadout(
                label: 'Allowed subnets',
                value: settings.security.allowedSubnets.isEmpty
                    ? null
                    : settings.security.allowedSubnets.join(', '),
              ),
            ],
          ),
          const SizedBox(height: EsiSpace.md),
          Text('Metrics', style: EsiType.label),
          const SizedBox(height: EsiSpace.sm),
          EsiReadoutList(
            children: <Widget>[
              EsiReadout(
                label: 'Sample interval',
                value: '${settings.metrics.sampleIntervalMs} ms',
              ),
              EsiReadout(label: 'History kept', value: '${settings.metrics.historyLimit}'),
              _SwitchRow(
                label: 'Collect GPU telemetry',
                detail: 'Off means the dashboard shows no GPU numbers at all.',
                value: settings.metrics.collectGpu,
                onChanged: (next) => _save(
                  settings,
                  (s) => s.copyWith(metrics: MetricsSettings(
                        sampleIntervalMs: s.metrics.sampleIntervalMs,
                        historyLimit: s.metrics.historyLimit,
                        collectGpu: next,
                        collectTemperatures: s.metrics.collectTemperatures,
                      )),
                ),
              ),
              _SwitchRow(
                label: 'Collect temperatures',
                detail: 'Off makes every temperature read as "not reported", which is '
                    'honest rather than wrong.',
                value: settings.metrics.collectTemperatures,
                onChanged: (next) => _save(
                  settings,
                  (s) => s.copyWith(metrics: MetricsSettings(
                        sampleIntervalMs: s.metrics.sampleIntervalMs,
                        historyLimit: s.metrics.historyLimit,
                        collectGpu: s.metrics.collectGpu,
                        collectTemperatures: next,
                      )),
                ),
              ),
            ],
          ),
          const SizedBox(height: EsiSpace.md),
          Text('File sandbox', style: EsiType.label),
          const SizedBox(height: EsiSpace.sm),
          EsiReadoutList(
            children: <Widget>[
              EsiReadout(
                label: 'Allowed roots',
                value: settings.files.allowedRoots.isEmpty
                    ? null
                    : '${settings.files.allowedRoots.length}',
              ),
              for (final root in settings.files.allowedRoots)
                EsiDetailRow(
                  label: root.readOnly ? 'read only' : 'read/write',
                  value: root.path,
                ),
              EsiReadout(
                label: 'Upload limit',
                value: formatBytes(settings.files.maxUploadBytes),
              ),
            ],
          ),
          const SizedBox(height: EsiSpace.lg),
          OutlinedButton.icon(
            onPressed: _saving ? null : _confirmReset,
            icon: const Icon(Icons.restart_alt, size: 17),
            label: const Text('Reset agent settings to defaults'),
            style: OutlinedButton.styleFrom(
              foregroundColor: EsiPalette.flare,
              side: BorderSide(color: EsiPalette.flare.withValues(alpha: 0.5)),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _save(AgentSettings current, AgentSettings Function(AgentSettings) edit) async {
    setState(() => _saving = true);
    final outcome = await ref.read(settingsProvider.notifier).save(edit(current));
    if (!mounted) return;
    setState(() => _saving = false);
    showOutcome(context, outcome);
  }

  Future<void> _confirmReset() async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Reset the agent settings?',
      body: 'Every setting returns to its default, including the file sandbox roots and '
          'the notification rules. Paired devices are not affected, but the agent may '
          'have to restart before the change takes effect.',
      confirmLabel: 'Reset',
      destructive: true,
    );
    if (!ok || !mounted) return;
    final outcome = await ref.read(settingsProvider.notifier).reset();
    if (!mounted) return;
    showOutcome(context, outcome);
  }

  static SecuritySettings _copySecurity(
    SecuritySettings s, {
    bool? localNetworkOnly,
    bool? requireSignedRequests,
  }) =>
      SecuritySettings(
        localNetworkOnly: localNetworkOnly ?? s.localNetworkOnly,
        requireSignedRequests: requireSignedRequests ?? s.requireSignedRequests,
        allowedSubnets: s.allowedSubnets,
        clockSkewSeconds: s.clockSkewSeconds,
        maxPairedDevices: s.maxPairedDevices,
        rateLimitPerMinute: s.rateLimitPerMinute,
        pairingWindowSeconds: s.pairingWindowSeconds,
        sessionAgentRequiredForLock: s.sessionAgentRequiredForLock,
        tlsEnabled: s.tlsEnabled,
      );
}

class _SwitchRow extends StatelessWidget {
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
    this.detail,
  });

  final String label;
  final String? detail;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
        child: Row(
          children: <Widget>[
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Text(label, style: EsiType.body.copyWith(fontSize: 13)),
                  if (detail != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 1),
                      child: Text(
                        detail!,
                        style: EsiType.body.copyWith(
                          color: EsiPalette.faint,
                          fontSize: 11,
                        ),
                      ),
                    ),
                ],
              ),
            ),
            const SizedBox(width: EsiSpace.sm),
            Switch(value: value, onChanged: onChanged),
          ],
        ),
      );
}

/// Copies a settings document with one field changed, so the UI never has to rebuild the
/// whole nested structure by hand.
extension on AgentSettings {
  AgentSettings copyWith({
    bool? discoveryEnabled,
    bool? mdnsEnabled,
    bool? startWithWindows,
    bool? wakeOnLanEnabled,
    SecuritySettings? security,
    MetricsSettings? metrics,
  }) =>
      AgentSettings(
        port: port,
        bindAddress: bindAddress,
        deviceName: deviceName,
        discoveryEnabled: discoveryEnabled ?? this.discoveryEnabled,
        discoveryPort: discoveryPort,
        mdnsEnabled: mdnsEnabled ?? this.mdnsEnabled,
        startWithWindows: startWithWindows ?? this.startWithWindows,
        wakeOnLanEnabled: wakeOnLanEnabled ?? this.wakeOnLanEnabled,
        security: security ?? this.security,
        metrics: metrics ?? this.metrics,
        files: files,
        apps: apps,
        gaming: gaming,
        notifications: notifications,
        debug: debug,
      );
}

class _DevicesPanel extends ConsumerWidget {
  const _DevicesPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(connectionProvider);
    if (!connection.hasScope(EsiScope.admin)) {
      return EsiPanel(
        title: 'Paired devices',
        severity: Severity.unknown,
        child: Text(
          'Only an admin-scoped phone can list or revoke the devices paired with this PC.',
          style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
        ),
      );
    }
    final devices = ref.watch(pairedDevicesProvider);
    return EsiPanel(
      title: 'Paired devices',
      severity: devices.value?.isEmpty ?? true ? Severity.unknown : Severity.nominal,
      trailing: EsiIconAction(
        icon: Icons.refresh,
        tooltip: 'Reload',
        onPressed: () => ref.invalidate(pairedDevicesProvider),
      ),
      child: devices.when(
        loading: () => const EsiSkeleton(height: 60),
        error: (error, _) => Text(
          'The device list could not be read: $error',
          style: EsiType.body.copyWith(color: EsiPalette.alarm, fontSize: 12.5),
        ),
        data: (list) => list.isEmpty
            ? Text(
                'No devices are paired with this PC.',
                style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12.5),
              )
            : Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  for (final device in list)
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs),
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
                                        device.name,
                                        style: EsiType.body.copyWith(fontSize: 13),
                                        maxLines: 1,
                                        overflow: TextOverflow.ellipsis,
                                      ),
                                    ),
                                    if (!device.enabled) ...<Widget>[
                                      const SizedBox(width: EsiSpace.sm),
                                      const EsiTag(
                                        label: 'disabled',
                                        colour: EsiPalette.alarm,
                                      ),
                                    ],
                                  ],
                                ),
                                Text(
                                  '${formatDeviceId(device.deviceId)} · key ${device.keyId} · '
                                  'seen ${formatRelative(device.lastSeen)}',
                                  style: EsiType.mono.copyWith(fontSize: 10),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(width: EsiSpace.sm),
                          EsiTag(label: device.platform, colour: EsiPalette.faint),
                          const SizedBox(width: EsiSpace.xs),
                          EsiIconAction(
                            icon: Icons.key,
                            size: 30,
                            tooltip: 'Rotate this device\'s keys',
                            onPressed: () => _rotate(context, ref, device),
                          ),
                        ],
                      ),
                    ),
                ],
              ),
      ),
    );
  }

  Future<void> _rotate(
    BuildContext context,
    WidgetRef ref,
    PairedDevice device,
  ) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Rotate keys for ${device.name}?',
      body: 'The device gets a new master secret and its key generation moves forward. '
          'The old one stays valid for two minutes so anything already in flight still '
          'works, and the device has to be re-paired if it misses the new envelope.',
      confirmLabel: 'Rotate',
    );
    if (!ok || !context.mounted) return;
    final api = ref.read(apiProvider);
    if (api == null) return;
    try {
      final result = RotateResult.fromJson(
        await api.postJson('/devices/${Uri.encodeComponent(device.deviceId)}/rotate'),
      );
      if (!context.mounted) return;
      showOutcome(context, (
        ok: true,
        message: 'Rotated to key ${result.keyId}. The previous generation stays valid for '
            '${result.graceSeconds} seconds.'
            '${result.requiresManualDelivery ? ' The new secret has to be delivered out of band.' : ''}',
      ));
      ref.invalidate(pairedDevicesProvider);
    } on EsiApiException catch (e) {
      if (context.mounted) showOutcome(context, (ok: false, message: e.displayMessage));
    } on EsiNetworkException catch (e) {
      if (context.mounted) showOutcome(context, (ok: false, message: e.message));
    }
  }
}

class _AuditPanel extends ConsumerWidget {
  const _AuditPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(connectionProvider);
    if (!connection.hasScope(EsiScope.admin)) {
      return const EsiPanel(
        title: 'Security audit',
        severity: Severity.unknown,
        child: Text(
          'The audit trail is behind the admin scope.',
          style: TextStyle(color: EsiPalette.dim),
        ),
      );
    }
    final trail = ref.watch(auditTrailProvider);
    return EsiPanel(
      title: 'Security audit',
      severity: (trail.value?.chainValid ?? true) ? Severity.nominal : Severity.critical,
      trailing: trail.value == null
          ? null
          : EsiTag(
              label: trail.value!.chainValid ? 'chain intact' : 'chain broken',
              colour: trail.value!.chainValid ? EsiPalette.signal : EsiPalette.alarm,
              tooltip: 'The agent chains each entry to the previous one with a hash. A '
                  'broken chain means the log was edited outside the agent.',
            ),
      child: trail.when(
        loading: () => const EsiSkeleton(height: 60),
        error: (error, _) => Text(
          'The audit trail could not be read: $error',
          style: EsiType.body.copyWith(color: EsiPalette.alarm, fontSize: 12.5),
        ),
        data: (value) => value.entries.isEmpty
            ? Text(
                'The agent has not recorded any audited actions.',
                style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12.5),
              )
            : Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  for (final entry in value.entries.take(20))
                    Padding(
                      padding: const EdgeInsets.only(bottom: EsiSpace.xs),
                      child: Row(
                        children: <Widget>[
                          SizedBox(
                            width: 44,
                            child: Text(
                              formatClock(entry.ts),
                              style: EsiType.mono.copyWith(
                                fontSize: 9.5,
                                color: EsiPalette.faint,
                              ),
                            ),
                          ),
                          SizedBox(
                            width: 58,
                            child: EsiTag(
                              label: entry.result,
                              colour: entry.result == 'ok'
                                  ? EsiPalette.dim
                                  : EsiPalette.alarm,
                            ),
                          ),
                          const SizedBox(width: EsiSpace.sm),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: <Widget>[
                                Text(
                                  entry.action,
                                  style: EsiType.body.copyWith(fontSize: 12),
                                ),
                                if (entry.detail != null)
                                  Text(
                                    entry.detail!,
                                    style: EsiType.mono.copyWith(fontSize: 9.5),
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                  ),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                ],
              ),
      ),
    );
  }
}

class _DangerPanel extends ConsumerWidget {
  const _DangerPanel({required this.machine});

  final PairedMachine machine;

  @override
  Widget build(BuildContext context, WidgetRef ref) => EsiPanel(
        title: 'This pairing',
        severity: Severity.elevated,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            const _DangerNote(
              icon: Icons.sync_lock,
              title: 'Rotate this phone\'s keys',
              body: 'The PC mints a new master secret, wraps it under the key this phone '
                  'already holds, and moves the generation forward. Nothing has to be '
                  'typed. Use it after a suspected compromise.',
            ),
            const SizedBox(height: EsiSpace.sm),
            OutlinedButton.icon(
              onPressed: () => _rotate(context, ref),
              icon: const Icon(Icons.sync_lock, size: 17),
              label: const Text('Rotate keys'),
              style: OutlinedButton.styleFrom(foregroundColor: EsiPalette.lock),
            ),
            const SizedBox(height: EsiSpace.lg),
            const _DangerNote(
              icon: Icons.phonelink_erase,
              title: 'Sign out and forget this PC',
              body: 'Deletes the master secret from this phone. The PC still lists the '
                  'device until it is revoked there, and it will have to be paired again.',
            ),
            const SizedBox(height: EsiSpace.sm),
            OutlinedButton.icon(
              onPressed: () => _forget(context, ref),
              icon: const Icon(Icons.logout, size: 17),
              label: const Text('Sign out'),
              style: OutlinedButton.styleFrom(
                foregroundColor: EsiPalette.flare,
                side: BorderSide(color: EsiPalette.flare.withValues(alpha: 0.5)),
              ),
            ),
          ],
        ),
      );

  Future<void> _rotate(BuildContext context, WidgetRef ref) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Rotate this phone\'s keys?',
      body: 'A new master secret is generated on the PC and delivered wrapped under the '
          'one this phone already holds. The previous generation stays valid for two '
          'minutes.',
      confirmLabel: 'Rotate',
    );
    if (!ok || !context.mounted) return;

    final api = ref.read(apiProvider);
    if (api == null) return;
    try {
      final json = await api.postJson('/auth/rotate', body: <String, dynamic>{
        'confirmDeviceId': machine.deviceId,
      });
      final result = RotateResult.fromJson(json);
      final envelope = result.wrappedSecrets;
      if (envelope == null) {
        if (!context.mounted) return;
        showOutcome(context, (
          ok: false,
          message: 'The PC rotated the keys but did not release a new secret, so this '
              'phone has to be paired again.',
        ));
        return;
      }
      // §5.3: the new secret is wrapped under the *old* wrap key, which is the only key
      // this phone still has. Decrypting it is what makes the rotation in-band.
      final opened = await EsiCrypto.openRotationEnvelope(
        envelope: WrappedSecretEnvelope.fromJson(envelope.json),
        masterSecret: machine.masterSecret!,
        deviceId: machine.deviceId,
      );
      final rotated = machine.copyWith(
        keyId: opened.keyId,
        masterSecret: opened.masterSecret,
        lastSeenAt: DateTime.now(),
      );
      await ref.read(machineListProvider.notifier).update(rotated);
      if (!context.mounted) return;
      showOutcome(context, (
        ok: true,
        message: 'Keys rotated. This phone is now on key generation ${opened.keyId}.',
      ));
    } on EsiApiException catch (e) {
      if (context.mounted) showOutcome(context, (ok: false, message: e.displayMessage));
    } on EsiNetworkException catch (e) {
      if (context.mounted) showOutcome(context, (ok: false, message: e.message));
    } on EsiCryptoException catch (e) {
      if (context.mounted) showOutcome(context, (ok: false, message: e.message));
    }
  }

  Future<void> _forget(BuildContext context, WidgetRef ref) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Sign out of this PC?',
      body: 'The credentials are deleted from this phone. The PC keeps the device in its '
          'list until it is revoked there, and pairing again will create a new device id.',
      confirmLabel: 'Sign out',
      destructive: true,
    );
    if (!ok || !context.mounted) return;
    await ref.read(machineListProvider.notifier).remove(machine.deviceId);
    if (!context.mounted) return;
    context.go('/onboarding');
  }
}

class _DangerNote extends StatelessWidget {
  const _DangerNote({required this.icon, required this.title, required this.body});

  final IconData icon;
  final String title;
  final String body;

  @override
  Widget build(BuildContext context) => Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Icon(icon, size: 16, color: EsiPalette.flare),
          const SizedBox(width: EsiSpace.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text(title, style: EsiType.bodyStrong.copyWith(fontSize: 13)),
                const SizedBox(height: 2),
                Text(
                  body,
                  style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 11.5),
                ),
              ],
            ),
          ),
        ],
      );
}

class _AboutPanel extends StatelessWidget {
  const _AboutPanel();

  @override
  Widget build(BuildContext context) {
    const version = esiAppVersion;
    return EsiPanel(
      title: 'About',
      tick: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          EsiReadoutList(
            children: <Widget>[
              EsiDetailRow(label: 'App', value: 'ESI Remote PC'),
              EsiDetailRow(label: 'Version', value: version),
              EsiDetailRow(label: 'App id', value: 'com.esi.remotepc'),
              const EsiDetailRow(label: 'Wire protocol', value: 'ESIV1'),
              const EsiDetailRow(
                label: 'Request signing',
                value: 'HMAC-SHA256 over an 8-line canonical string',
              ),
              const EsiDetailRow(
                label: 'Key derivation',
                value: 'HKDF-SHA256, salt "ESI-RemotePC/v1"',
              ),
              const EsiDetailRow(
                label: 'Transport',
                value: 'TLS 1.3 with SPKI pinning, signed requests',
              ),
            ],
          ),
          const SizedBox(height: EsiSpace.md),
          // The PC half of this pair has to come from somewhere, and a phone that
          // controls PCs it cannot point at installs nothing. GitHub's `latest` alias
          // resolves to the newest non-prerelease release, so this row outlives
          // every future version number without an edit here.
          OutlinedButton.icon(
            onPressed: () async {
              final failure = await EsiLinks.openWindowsDownload();
              if (failure != null && context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(content: Text(failure)),
                );
              }
            },
            icon: const Icon(Icons.desktop_windows_outlined, size: 18),
            label: const Text('Windows agent — download from GitHub'),
          ),
          const SizedBox(height: EsiSpace.sm),
          OutlinedButton.icon(
            onPressed: () async {
              final failure = await EsiLinks.open(EsiLinks.androidApkUrl);
              if (failure != null && context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(content: Text(failure)),
                );
              }
            },
            icon: const Icon(Icons.android, size: 18),
            label: const Text('Android app — latest APK'),
          ),
        ],
      ),
    );
  }
}

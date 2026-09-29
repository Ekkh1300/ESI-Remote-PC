import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../protocol/models.dart';
import '../../state/connection.dart';
import '../../state/metrics.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/load_trace.dart';
import '../widgets/panel.dart';

/// The machine's vital signs.
///
/// The layout is asymmetric on purpose: the load trace runs full width because it is the
/// thing you glance at, and the panels below it are of unequal weight because a laptop and
/// a workstation do not have the same things worth watching. Everything is a reading, so
/// everything is monospaced and aligned to a shared column.
class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => RequiresPairing(
        builder: (context, ref, connection) {
          final metrics = ref.watch(metricsProvider);
          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: agentLabel(connection.handshake, ref.watch(activeMachineProvider)?.displayName),
              subtitle: _subtitle(metrics, connection),
              onRefresh: () => ref.read(metricsProvider.notifier).refresh(),
              actions: <Widget>[
                EsiIconAction(
                  icon: Icons.refresh,
                  tooltip: 'Refresh now',
                  onPressed: () => ref.read(metricsProvider.notifier).refresh(),
                ),
              ],
              children: <Widget>[
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _LoadPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: EsiGrid(
                    children: <Widget>[
                      const _CpuPanel(),
                      const _MemoryPanel(),
                      const _GpuPanel(),
                      const _StoragePanel(),
                      const _NetworkPanel(),
                      const _MachinePanel(),
                    ],
                  ),
                ),
              ],
            ),
          );
        },
      );

  static String _subtitle(MetricsState metrics, ConnectionStatus connection) {
    final snapshot = metrics.snapshot;
    if (snapshot == null) return 'Waiting for the first sample';
    final parts = <String>[
      'sampled ${formatClock(snapshot.capturedAt)}',
      if (metrics.source == MetricsSource.stream)
        'live stream'
      else if (metrics.source == MetricsSource.poll)
        'polled every 3 s',
      if (snapshot.cpu.processCount > 0) '${snapshot.cpu.processCount} processes',
    ];
    return parts.join(' · ');
  }
}

class _LoadPanel extends ConsumerWidget {
  const _LoadPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final metrics = ref.watch(metricsProvider);
    final snapshot = metrics.snapshot;
    final cpuSeverity = severityForPercent(snapshot?.cpu.usagePercent);
    final ramSeverity = severityForPercent(snapshot?.memory.usedPercent);
    final gpuSeverity = severityForPercent(snapshot?.gpuAggregate.usagePercent);
    final overall = _worst(<Severity>[cpuSeverity, ramSeverity, gpuSeverity]);

    return EsiPanel(
      title: 'Load',
      severity: overall,
      tick: false,
      padding: const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.lg, EsiSpace.lg, EsiSpace.md),
      trailing: snapshot == null
          ? null
          : EsiTag(
              label: formatClock(snapshot.capturedAt),
              colour: metrics.source == MetricsSource.stream
                  ? EsiPalette.live
                  : EsiPalette.dim,
              tooltip: metrics.source == MetricsSource.stream
                  ? 'Live from the WebSocket stream'
                  : 'Polled, because the live stream is not running',
            ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          EsiLoadTrace(
            channels: buildTraceChannels(metrics),
            streaming: metrics.source == MetricsSource.stream,
          ),
          const SizedBox(height: EsiSpace.lg),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Expanded(
                child: EsiHeroReadout(
                  label: 'CPU',
                  value: formatPercent(snapshot?.cpu.usagePercent),
                  severity: cpuSeverity,
                  bar: EsiBar(
                    fraction: (snapshot?.cpu.usagePercent ?? 0) / 100,
                    severity: cpuSeverity,
                  ),
                ),
              ),
              const SizedBox(width: EsiSpace.lg),
              Expanded(
                child: EsiHeroReadout(
                  label: 'Memory',
                  value: formatPercent(snapshot?.memory.usedPercent),
                  severity: ramSeverity,
                  bar: EsiBar(
                    fraction: (snapshot?.memory.usedPercent ?? 0) / 100,
                    severity: ramSeverity,
                  ),
                ),
              ),
              const SizedBox(width: EsiSpace.lg),
              Expanded(
                child: EsiHeroReadout(
                  label: 'GPU',
                  value: formatPercent(snapshot?.gpuAggregate.usagePercent),
                  severity: gpuSeverity,
                  bar: EsiBar(
                    fraction: (snapshot?.gpuAggregate.usagePercent ?? 0) / 100,
                    severity: gpuSeverity,
                  ),
                ),
              ),
            ],
          ),
          if (metrics.error != null) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            Row(
              children: <Widget>[
                const Icon(Icons.warning_amber_rounded, size: 14, color: EsiPalette.flare),
                const SizedBox(width: EsiSpace.sm),
                Expanded(
                  child: Text(
                    metrics.error!,
                    style: EsiType.body.copyWith(color: EsiPalette.flare, fontSize: 12),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _CpuPanel extends ConsumerWidget {
  const _CpuPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final snapshot = ref.watch(metricsProvider).snapshot;
    final cpu = snapshot?.cpu;
    final severity = severityForPercent(cpu?.usagePercent);
    return EsiPanel(
      title: 'Processor',
      severity: severity,
      footnote: cpu == null || cpu.perCore.isEmpty
          ? 'Per-core counters are not available on this machine.'
          : '${cpu.perCore.length} logical processors',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          EsiReadoutList(
            children: <Widget>[
              EsiReadout(label: 'Total', value: formatPercent(cpu?.usagePercent), severity: severity),
              EsiReadout(label: 'User', value: formatPercent(cpu?.userPercent)),
              EsiReadout(label: 'Kernel', value: formatPercent(cpu?.kernelPercent)),
              EsiReadout(
                label: 'Clock',
                value: cpu?.clockMhz == null ? null : cpu!.clockMhz!.toStringAsFixed(0),
                unit: 'MHz',
                tooltip: cpu?.maxClockMhz == null
                    ? null
                    : 'Maximum ${cpu!.maxClockMhz} MHz',
              ),
              EsiReadout(label: 'Temperature', value: formatCelsius(cpu?.temperatureC)),
              EsiReadout(
                label: 'Processes',
                value: cpu == null ? null : '${cpu.processCount}',
              ),
              EsiReadout(
                label: 'Threads',
                value: cpu == null ? null : '${cpu.threadCount}',
              ),
            ],
          ),
          const SizedBox(height: EsiSpace.md),
          EsiCoreGrid(perCore: cpu?.perCore ?? const <double>[]),
        ],
      ),
    );
  }
}

class _MemoryPanel extends ConsumerWidget {
  const _MemoryPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final snapshot = ref.watch(metricsProvider).snapshot;
    final memory = snapshot?.memory;
    final severity = severityForPercent(memory?.usedPercent);
    return EsiPanel(
      title: 'Memory',
      severity: severity,
      child: EsiReadoutList(
        children: <Widget>[
          EsiReadout(
            label: 'In use',
            value: formatBytes(memory?.usedBytes),
            severity: severity,
            emphasised: true,
          ),
          EsiBar(fraction: (memory?.usedPercent ?? 0) / 100, severity: severity),
          const SizedBox(height: EsiSpace.sm),
          EsiReadout(label: 'Available', value: formatBytes(memory?.availableBytes)),
          EsiReadout(label: 'Total', value: formatBytes(memory?.totalBytes)),
          EsiReadout(label: 'Cached', value: formatBytes(memory?.cachedBytes)),
          EsiReadout(label: 'Committed', value: formatBytes(memory?.committedBytes)),
          EsiReadout(
            label: 'Commit limit',
            value: formatBytes(memory?.commitLimitBytes),
          ),
          EsiReadout(
            label: 'Page file',
            value: memory == null ? null : formatBytes(memory.swap.totalBytes),
            tooltip: memory == null
                ? null
                : '${formatBytes(memory.swap.usedBytes)} in use',
          ),
        ],
      ),
    );
  }
}

class _GpuPanel extends ConsumerWidget {
  const _GpuPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final snapshot = ref.watch(metricsProvider).snapshot;
    final gpus = snapshot?.gpu ?? const <GpuMetrics>[];
    final aggregate = snapshot?.gpuAggregate;
    final severity = severityForPercent(aggregate?.usagePercent);

    if (gpus.isEmpty) {
      return const EsiPanel(
        title: 'Graphics',
        severity: Severity.unknown,
        child: EsiReadoutList(
          children: <Widget>[
            EsiReadout(label: 'Load', value: null),
            EsiReadout(label: 'Adapters reporting', value: '0'),
          ],
        ),
      );
    }

    return EsiPanel(
      title: 'Graphics',
      severity: severity,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          EsiReadoutList(
            children: <Widget>[
              EsiReadout(
                label: 'Peak load',
                value: formatPercent(aggregate?.usagePercent),
                severity: severity,
                emphasised: true,
              ),
              EsiReadout(
                label: 'Hottest adapter',
                value: formatCelsius(aggregate?.maxTemperatureC),
                severity: severityForCelsius(aggregate?.maxTemperatureC),
              ),
            ],
          ),
          for (final gpu in gpus) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            _GpuRow(gpu: gpu),
          ],
        ],
      ),
    );
  }
}

class _GpuRow extends StatelessWidget {
  const _GpuRow({required this.gpu});

  final GpuMetrics gpu;

  @override
  Widget build(BuildContext context) {
    final severity = severityForPercent(gpu.usagePercent);
    return Container(
      padding: const EdgeInsets.all(EsiSpace.md),
      decoration: BoxDecoration(
        color: EsiPalette.hull.withValues(alpha: 0.45),
        borderRadius: EsiRadius.tag,
        border: Border.all(color: EsiPalette.edge),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Row(
            children: <Widget>[
              Expanded(
                child: Text(
                  gpu.name,
                  style: EsiType.bodyStrong.copyWith(fontSize: 12.5),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              const SizedBox(width: EsiSpace.sm),
              EsiTag(
                label: gpu.source,
                colour: gpu.source == 'unavailable' ? EsiPalette.faint : EsiPalette.dim,
                tooltip: 'Which telemetry provider produced these numbers',
              ),
            ],
          ),
          const SizedBox(height: EsiSpace.sm),
          EsiReadoutList(
            children: <Widget>[
              EsiReadout(label: 'Load', value: formatPercent(gpu.usagePercent), severity: severity),
              EsiReadout(
                label: 'Temperature',
                value: formatCelsius(gpu.temperatureC),
                severity: severityForCelsius(gpu.temperatureC),
              ),
              EsiReadout(
                label: 'Video memory',
                value: formatBytes(gpu.memoryUsedBytes),
                tooltip: gpu.memoryTotalBytes == null
                    ? null
                    : 'of ${formatBytes(gpu.memoryTotalBytes)}',
              ),
              EsiReadout(label: 'Fan', value: formatRpm(gpu.fanRpm)),
              EsiReadout(label: 'Power', value: formatWatts(gpu.powerWatts)),
            ],
          ),
        ],
      ),
    );
  }
}

class _StoragePanel extends ConsumerWidget {
  const _StoragePanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final volumes = ref.watch(metricsProvider).snapshot?.storage ?? const <StorageMetrics>[];
    if (volumes.isEmpty) {
      return const EsiPanel(
        title: 'Storage',
        severity: Severity.unknown,
        child: EsiReadoutList(
          children: <Widget>[
            EsiReadout(label: 'Volumes reporting', value: '0'),
          ],
        ),
      );
    }
    return EsiPanel(
      title: 'Storage',
      severity: _worst(volumes.map((v) => severityForDisk(v.usedPercent)).toList()),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          for (final volume in volumes) ...<Widget>[
            Padding(
              padding: const EdgeInsets.only(bottom: EsiSpace.sm),
              child: EsiReadoutList(
                children: <Widget>[
                  EsiReadout(
                    label: '${volume.deviceId} used',
                    value: formatPercent(volume.usedPercent),
                    severity: severityForDisk(volume.usedPercent),
                  ),
                  EsiBar(
                    fraction: volume.usedPercent / 100,
                    severity: severityForDisk(volume.usedPercent),
                  ),
                  const SizedBox(height: EsiSpace.xs),
                  EsiReadout(
                    label: 'Free of ${formatBytes(volume.totalBytes)}',
                    value: formatBytes(volume.freeBytes),
                  ),
                  EsiReadout(
                    label: 'Disk activity',
                    value: '${formatRate(volume.readBytesPerSecond)} read · '
                        '${formatRate(volume.writeBytesPerSecond)} write',
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _NetworkPanel extends ConsumerWidget {
  const _NetworkPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final network = ref.watch(metricsProvider).snapshot?.network;
    final primary = network?.primary;
    return EsiPanel(
      title: 'Network',
      severity: primary?.isUp ?? false ? Severity.nominal : Severity.unknown,
      trailing: network?.pingMs == null
          ? null
          : EsiTag(
              label: formatMilliseconds(network!.pingMs),
              colour: EsiPalette.live,
              icon: Icons.network_ping,
            ),
      child: EsiReadoutList(
        children: <Widget>[
          EsiReadout(label: 'Adapter', value: primary?.name),
          EsiReadout(label: 'IPv4', value: primary?.ipv4),
          EsiReadout(label: 'Link speed', value: formatLinkSpeed(primary?.linkSpeedBitsPerSecond)),
          EsiReadout(
            label: 'Download',
            value: formatRate(primary?.downloadBytesPerSecond),
            emphasised: true,
          ),
          EsiReadout(label: 'Upload', value: formatRate(primary?.uploadBytesPerSecond), emphasised: true),
          EsiReadout(
            label: 'Total received',
            value: formatBytes(primary?.rxTotalBytes),
          ),
          EsiReadout(label: 'Total sent', value: formatBytes(primary?.txTotalBytes)),
          if ((primary?.dns.isNotEmpty ?? false)) ...<Widget>[
            const SizedBox(height: EsiSpace.xs),
            Text(
              'DNS ${primary!.dns.join(', ')}',
              style: EsiType.mono.copyWith(fontSize: 10.5),
            ),
          ],
        ],
      ),
    );
  }
}

/// Uptime, the agent's own state, and the security posture — the things that do not change
/// at 1 Hz and would be noise inside the live panels.
class _MachinePanel extends ConsumerWidget {
  const _MachinePanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final info = ref.watch(systemInfoProvider).value;
    final connection = ref.watch(connectionProvider);
    return EsiPanel(
      title: 'Machine',
      severity: info?.agent.sessionAgentConnected ?? false ? Severity.nominal : Severity.elevated,
      footnote: info == null
          ? null
          : 'Agent ${info.agent.version} · protocol v${info.agent.protocolVersion}',
      child: EsiReadoutList(
        children: <Widget>[
          EsiReadout(label: 'Uptime', value: formatUptime(info?.os.uptimeSeconds)),
          EsiReadout(
            label: 'Operating system',
            value: info == null
                ? null
                : info.os.displayVersion == null
                    ? info.os.caption
                    : '${info.os.caption} ${info.os.displayVersion}',
          ),
          EsiReadout(
            label: 'Processor',
            value: info?.cpu.model,
            severity: info == null ? Severity.unknown : Severity.nominal,
          ),
          EsiReadout(
            label: 'Installed memory',
            value: info == null ? null : formatBytes(info.memory.totalBytes),
          ),
          EsiReadout(
            label: 'Power plan',
            value: info?.power.activePowerScheme,
          ),
          EsiReadout(
            label: 'Supports sleep',
            value: info == null ? null : (info.power.supportsSleep ? 'yes' : 'no'),
          ),
          EsiReadout(
            label: 'Supports hibernate',
            value: info == null ? null : (info.power.supportsHibernate ? 'yes' : 'no'),
          ),
          EsiReadout(
            label: 'Session helper',
            value: info?.agent.sessionAgentState,
            severity: (info?.agent.sessionAgentConnected ?? false)
                ? Severity.nominal
                : Severity.elevated,
            tooltip: (info?.agent.sessionAgentConnected ?? false)
                ? null
                : 'Locking, signing out and screen capture all need the session helper '
                    'to be running in the signed-in user session.',
          ),
          EsiReadout(
            label: 'Secure Boot',
            value: _yesNo(info?.security.secureBoot),
          ),
          EsiReadout(label: 'TPM', value: _yesNo(info?.security.tpmPresent)),
          EsiReadout(label: 'Defender', value: _yesNo(info?.security.defenderEnabled)),
          EsiReadout(label: 'BitLocker', value: info?.security.bitLockerStatus),
          EsiReadout(
            label: 'This phone\'s scopes',
            value: connection.whoAmI == null
                ? null
                : connection.whoAmI!.scopes.isEmpty
                    ? 'none granted'
                    : connection.whoAmI!.scopes.join(', '),
          ),
        ],
      ),
    );
  }
}

String? _yesNo(bool? value) => value == null ? null : (value ? 'yes' : 'no');

Severity _worst(List<Severity> severities) {
  var worst = Severity.nominal;
  for (final severity in severities) {
    if (severity == Severity.critical) return Severity.critical;
    if (severity == Severity.elevated) worst = Severity.elevated;
    if (severity == Severity.unknown && worst == Severity.nominal) worst = Severity.unknown;
  }
  return worst;
}

/// Dart mirrors of the C# DTOs in `windows-agent/src/ESI.RemotePc.Protocol/Contracts/`.
///
/// Field names come from the `[JsonPropertyName]` attributes, not from the C# property
/// names, and every model is a pure data holder with a `fromJson` factory. Unknown fields
/// are ignored, as PROTOCOL.md §10 requires.
library;

import 'json_read.dart';
import 'protocol_constants.dart';

// =====================================================================================
// Handshake + pairing  (Handshake.cs)
// =====================================================================================

class Handshake {
  const Handshake({
    required this.protocolVersion,
    required this.agentVersion,
    required this.deviceId,
    required this.deviceName,
    required this.hostName,
    required this.httpsPort,
    required this.discoveryPort,
    required this.spkiSha256,
    required this.tlsSubject,
    required this.notBefore,
    required this.notAfter,
    required this.serverTime,
    required this.pairingRequired,
    required this.pairingOpen,
    required this.maxPairedDevices,
    required this.pairedDeviceCount,
    required this.features,
  });

  factory Handshake.fromJson(Map<String, dynamic> json) => Handshake(
        protocolVersion: asInt(json['protocolVersion']),
        agentVersion: asString(json['agentVersion']),
        deviceId: asString(json['deviceId']),
        deviceName: asString(json['deviceName']),
        hostName: asString(json['hostName']),
        httpsPort: asInt(json['httpsPort'], EsiProtocol.defaultHttpsPort),
        discoveryPort: asInt(json['discoveryPort'], EsiProtocol.defaultDiscoveryPort),
        spkiSha256: asStringOrNull(json['spkiSha256']),
        tlsSubject: asStringOrNull(json['tlsSubject']),
        notBefore: asIntOrNull(json['notBefore']),
        notAfter: asIntOrNull(json['notAfter']),
        serverTime: asInt(json['serverTime']),
        pairingRequired: asBool(json['pairingRequired'], true),
        pairingOpen: asBool(json['pairingOpen'], true),
        maxPairedDevices: asInt(json['maxPairedDevices'], 8),
        pairedDeviceCount: asInt(json['pairedDeviceCount']),
        features: asBoolMap(json['features']),
      );

  final int protocolVersion;
  final String agentVersion;
  final String deviceId;
  final String deviceName;
  final String hostName;
  final int httpsPort;
  final int discoveryPort;

  /// base64(SHA-256(SPKI)). Compared against the pin from the pairing QR.
  final String? spkiSha256;
  final String? tlsSubject;
  final int? notBefore;
  final int? notAfter;
  final int serverTime;
  final bool pairingRequired;
  final bool pairingOpen;
  final int maxPairedDevices;
  final int pairedDeviceCount;
  final Map<String, bool> features;

  /// PROTOCOL.md §1: refuse to operate against a major version we do not understand.
  bool get isCompatible => protocolVersion == EsiProtocol.version;

  bool supports(String feature) => features[feature] ?? false;
}

class PairRequestResult {
  const PairRequestResult({
    required this.pairingId,
    required this.userCode,
    required this.expiresAt,
    required this.approval,
    required this.pollIntervalMs,
  });

  factory PairRequestResult.fromJson(Map<String, dynamic> json) => PairRequestResult(
        pairingId: asString(json['pairingId']),
        userCode: asString(json['userCode']),
        expiresAt: asInt(json['expiresAt']),
        approval: asString(json['approval'], 'desktop-prompt'),
        pollIntervalMs: asInt(json['pollIntervalMs'], 1000),
      );

  final String pairingId;
  final String userCode;
  final int expiresAt;
  final String approval;
  final int pollIntervalMs;
}

/// Terminal and non-terminal states of `GET /pair/session`.
class PairStatus {
  const PairStatus({
    required this.status,
    required this.deviceId,
    required this.keyId,
    required this.spkiSha256,
    required this.wrappedSecrets,
  });

  factory PairStatus.fromJson(Map<String, dynamic> json) => PairStatus(
        status: asString(json['status'], 'pending'),
        deviceId: asStringOrNull(json['deviceId']),
        keyId: asIntOrNull(json['keyId']),
        spkiSha256: asStringOrNull(json['spkiSha256']),
        wrappedSecrets: json['wrappedSecrets'] is Map
            ? WrappedSecretEnvelopeAdapter.from(json['wrappedSecrets'] as Map)
            : null,
      );

  final String status;
  final String? deviceId;
  final int? keyId;
  final String? spkiSha256;
  final WrappedSecretEnvelopeAdapter? wrappedSecrets;

  bool get isPending => status == 'pending';
  bool get isApproved => status == 'approved';
  bool get isDenied => status == 'denied';
  bool get isExpired => status == 'expired';
  bool get isConsumed => status == 'consumed';
}

/// Small indirection so the models file does not need to import the crypto library.
class WrappedSecretEnvelopeAdapter {
  const WrappedSecretEnvelopeAdapter(this.json);
  factory WrappedSecretEnvelopeAdapter.from(Map json) =>
      WrappedSecretEnvelopeAdapter(asObject(json));
  final Map<String, dynamic> json;
}

class WhoAmI {
  const WhoAmI({
    required this.deviceId,
    required this.keyId,
    required this.scopes,
    required this.name,
    required this.platform,
    required this.firstPaired,
    required this.lastSeen,
    required this.lastAddress,
    required this.rotatedAt,
    required this.agentVersion,
  });

  factory WhoAmI.fromJson(Map<String, dynamic> json) => WhoAmI(
        deviceId: asString(json['deviceId']),
        keyId: asInt(json['keyId'], 1),
        scopes: asStringList(json['scopes']),
        name: asString(json['name']),
        platform: asString(json['platform']),
        firstPaired: asInt(json['firstPaired']),
        lastSeen: asIntOrNull(json['lastSeen']),
        lastAddress: asStringOrNull(json['lastAddress']),
        rotatedAt: asIntOrNull(json['rotatedAt']),
        agentVersion: asString(json['agentVersion']),
      );

  final String deviceId;
  final int keyId;
  final List<String> scopes;
  final String name;
  final String platform;
  final int firstPaired;
  final int? lastSeen;
  final String? lastAddress;
  final int? rotatedAt;
  final String agentVersion;

  Set<EsiScope> get scopeSet => EsiScope.parseAll(scopes);
}

class PairedDevice {
  const PairedDevice({
    required this.deviceId,
    required this.name,
    required this.platform,
    required this.scopes,
    required this.enabled,
    required this.firstPaired,
    required this.lastSeen,
    required this.lastAddress,
    required this.keyId,
    required this.suspended,
  });

  factory PairedDevice.fromJson(Map<String, dynamic> json) => PairedDevice(
        deviceId: asString(json['deviceId']),
        name: asString(json['name']),
        platform: asString(json['platform']),
        scopes: asStringList(json['scopes']),
        enabled: asBool(json['enabled'], true),
        firstPaired: asInt(json['firstPaired']),
        lastSeen: asIntOrNull(json['lastSeen']),
        lastAddress: asStringOrNull(json['lastAddress']),
        keyId: asInt(json['keyId'], 1),
        suspended: asBool(json['suspended']),
      );

  final String deviceId;
  final String name;
  final String platform;
  final List<String> scopes;
  final bool enabled;
  final int firstPaired;
  final int? lastSeen;
  final String? lastAddress;
  final int keyId;
  final bool suspended;
}

class RotateResult {
  const RotateResult({
    required this.deviceId,
    required this.previousKeyId,
    required this.keyId,
    required this.graceSeconds,
    required this.wrappedSecrets,
    required this.requiresManualDelivery,
  });

  factory RotateResult.fromJson(Map<String, dynamic> json) => RotateResult(
        deviceId: asString(json['deviceId']),
        previousKeyId: asInt(json['previousKeyId']),
        keyId: asInt(json['keyId']),
        graceSeconds: asInt(json['graceSeconds'], EsiProtocol.keyRotationGraceSeconds),
        wrappedSecrets: json['wrappedSecrets'] is Map
            ? WrappedSecretEnvelopeAdapter.from(json['wrappedSecrets'] as Map)
            : null,
        requiresManualDelivery: asBool(json['requiresManualDelivery']),
      );

  final String deviceId;
  final int previousKeyId;
  final int keyId;
  final int graceSeconds;
  final WrappedSecretEnvelopeAdapter? wrappedSecrets;
  final bool requiresManualDelivery;
}

// =====================================================================================
// System & metrics  (SystemContracts.cs)
// =====================================================================================

class OsInfo {
  const OsInfo({
    required this.caption,
    required this.displayVersion,
    required this.version,
    required this.build,
    required this.revision,
    required this.architecture,
    required this.installDate,
    required this.lastBoot,
    required this.uptimeSeconds,
    required this.locale,
  });

  factory OsInfo.fromJson(Map<String, dynamic> json) => OsInfo(
        caption: asString(json['caption']),
        displayVersion: asStringOrNull(json['displayVersion']),
        version: asStringOrNull(json['version']),
        build: asInt(json['build']),
        revision: asInt(json['revision']),
        architecture: asString(json['architecture']),
        installDate: asIntOrNull(json['installDate']),
        lastBoot: asInt(json['lastBoot']),
        uptimeSeconds: asInt(json['uptimeSeconds']),
        locale: asStringOrNull(json['locale']),
      );

  final String caption;
  final String? displayVersion;
  final String? version;
  final int build;
  final int revision;
  final String architecture;
  final int? installDate;
  final int lastBoot;
  final int uptimeSeconds;
  final String? locale;
}

class CpuInfo {
  const CpuInfo({
    required this.model,
    required this.cores,
    required this.threads,
    required this.maxClockMhz,
    required this.baseClockMhz,
    required this.socket,
  });

  factory CpuInfo.fromJson(Map<String, dynamic> json) => CpuInfo(
        model: asStringOrNull(json['model']),
        cores: asInt(json['cores']),
        threads: asInt(json['threads']),
        maxClockMhz: asInt(json['maxClockMhz']),
        baseClockMhz: asInt(json['baseClockMhz']),
        socket: asStringOrNull(json['socket']),
      );

  final String? model;
  final int cores;
  final int threads;
  final int maxClockMhz;
  final int baseClockMhz;
  final String? socket;
}

class GpuInfo {
  const GpuInfo({
    required this.index,
    required this.name,
    required this.vendor,
    required this.vramBytes,
    required this.driverVersion,
    required this.isPrimary,
  });

  factory GpuInfo.fromJson(Map<String, dynamic> json) => GpuInfo(
        index: asInt(json['index']),
        name: asString(json['name']),
        vendor: asString(json['vendor'], 'unknown'),
        vramBytes: asIntOrNull(json['vramBytes']),
        driverVersion: asStringOrNull(json['driverVersion']),
        isPrimary: asBool(json['isPrimary']),
      );

  final int index;
  final String name;
  final String vendor;
  final int? vramBytes;
  final String? driverVersion;
  final bool isPrimary;
}

class MemoryModule {
  const MemoryModule({
    required this.sizeBytes,
    required this.speedMhz,
    required this.type,
    required this.manufacturer,
    required this.slot,
  });

  factory MemoryModule.fromJson(Map<String, dynamic> json) => MemoryModule(
        sizeBytes: asInt(json['sizeBytes']),
        speedMhz: asIntOrNull(json['speedMhz']),
        type: asStringOrNull(json['type']),
        manufacturer: asStringOrNull(json['manufacturer']),
        slot: asStringOrNull(json['slot']),
      );

  final int sizeBytes;
  final int? speedMhz;
  final String? type;
  final String? manufacturer;
  final String? slot;
}

class MemoryInfo {
  const MemoryInfo({
    required this.totalBytes,
    required this.slots,
    required this.modules,
  });

  factory MemoryInfo.fromJson(Map<String, dynamic> json) => MemoryInfo(
        totalBytes: asInt(json['totalBytes']),
        slots: asIntOrNull(json['slots']),
        modules: (json['modules'] is List)
            ? (json['modules'] as List)
                .map((e) => MemoryModule.fromJson(asObject(e)))
                .toList()
            : const <MemoryModule>[],
      );

  final int totalBytes;
  final int? slots;
  final List<MemoryModule> modules;
}

class StorageVolume {
  const StorageVolume({
    required this.deviceId,
    required this.label,
    required this.fileSystem,
    required this.totalBytes,
    required this.freeBytes,
    required this.usedPercent,
    required this.driveType,
    required this.isSystem,
  });

  factory StorageVolume.fromJson(Map<String, dynamic> json) => StorageVolume(
        deviceId: asString(json['deviceId']),
        label: asStringOrNull(json['label']),
        fileSystem: asStringOrNull(json['fileSystem']),
        totalBytes: asInt(json['totalBytes']),
        freeBytes: asInt(json['freeBytes']),
        usedPercent: asDouble(json['usedPercent']),
        driveType: asString(json['driveType'], 'fixed'),
        isSystem: asBool(json['isSystem']),
      );

  final String deviceId;
  final String? label;
  final String? fileSystem;
  final int totalBytes;
  final int freeBytes;
  final double usedPercent;
  final String driveType;
  final bool isSystem;
}

class SecurityInfo {
  const SecurityInfo({
    required this.secureBoot,
    required this.tpmPresent,
    required this.defenderEnabled,
    required this.uacEnabled,
    required this.bitLockerStatus,
    required this.lanOnly,
  });

  factory SecurityInfo.fromJson(Map<String, dynamic> json) => SecurityInfo(
        secureBoot: _boolOrNull(json['secureBoot']),
        tpmPresent: _boolOrNull(json['tpmPresent']),
        defenderEnabled: _boolOrNull(json['defenderEnabled']),
        uacEnabled: _boolOrNull(json['uacEnabled']),
        bitLockerStatus: asStringOrNull(json['bitLockerStatus']),
        lanOnly: asBool(json['lanOnly'], true),
      );

  final bool? secureBoot;
  final bool? tpmPresent;
  final bool? defenderEnabled;
  final bool? uacEnabled;
  final String? bitLockerStatus;
  final bool lanOnly;
}

bool? _boolOrNull(Object? v) => v is bool ? v : null;

class PowerCapabilities {
  const PowerCapabilities({
    required this.supportsSleep,
    required this.supportsHibernate,
    required this.lastBootState,
    required this.activePowerScheme,
    required this.hiberbootEnabled,
  });

  factory PowerCapabilities.fromJson(Map<String, dynamic> json) => PowerCapabilities(
        supportsSleep: asBool(json['supportsSleep'], true),
        supportsHibernate: asBool(json['supportsHibernate']),
        lastBootState: asStringOrNull(json['lastBootState']),
        activePowerScheme: asStringOrNull(json['activePowerScheme']),
        hiberbootEnabled: asBool(json['hiberbootEnabled']),
      );

  final bool supportsSleep;
  final bool supportsHibernate;
  final String? lastBootState;
  final String? activePowerScheme;
  final bool hiberbootEnabled;
}

class SensorSummary {
  const SensorSummary({
    required this.cpuTemperatureC,
    required this.gpuTemperatureC,
    required this.cpuFanRpm,
    required this.gpuFanRpm,
    required this.sensorsAvailable,
    required this.notes,
  });

  factory SensorSummary.fromJson(Map<String, dynamic> json) => SensorSummary(
        cpuTemperatureC: asDoubleOrNull(json['cpuTemperatureC']),
        gpuTemperatureC: asDoubleOrNull(json['gpuTemperatureC']),
        cpuFanRpm: asIntOrNull(json['cpuFanRpm']),
        gpuFanRpm: asIntOrNull(json['gpuFanRpm']),
        sensorsAvailable: asStringList(json['sensorsAvailable']),
        notes: asStringMap(json['notes']),
      );

  /// null means "this machine has no trustworthy sensor", never "0 degrees".
  final double? cpuTemperatureC;
  final double? gpuTemperatureC;
  final int? cpuFanRpm;
  final int? gpuFanRpm;
  final List<String> sensorsAvailable;
  final Map<String, String> notes;
}

class AgentInfo {
  const AgentInfo({
    required this.version,
    required this.protocolVersion,
    required this.uptimeSeconds,
    required this.debugMode,
    required this.sessionAgentConnected,
    required this.sessionAgentState,
  });

  factory AgentInfo.fromJson(Map<String, dynamic> json) => AgentInfo(
        version: asString(json['version']),
        protocolVersion: asInt(json['protocolVersion'], EsiProtocol.version),
        uptimeSeconds: asInt(json['uptimeSeconds']),
        debugMode: asBool(json['debugMode']),
        sessionAgentConnected: asBool(json['sessionAgentConnected']),
        sessionAgentState: asString(json['sessionAgentState'], 'NotRunning'),
      );

  final String version;
  final int protocolVersion;
  final int uptimeSeconds;
  final bool debugMode;
  final bool sessionAgentConnected;
  final String sessionAgentState;
}

class SystemInfo {
  const SystemInfo({
    required this.hostName,
    required this.deviceName,
    required this.manufacturer,
    required this.model,
    required this.os,
    required this.cpu,
    required this.gpu,
    required this.memory,
    required this.storage,
    required this.biosVersion,
    required this.biosReleaseDate,
    required this.security,
    required this.power,
    required this.sensors,
    required this.agent,
  });

  factory SystemInfo.fromJson(Map<String, dynamic> json) {
    final bios = asObject(json['bios']);
    return SystemInfo(
      hostName: asString(json['hostName']),
      deviceName: asString(json['deviceName']),
      manufacturer: asStringOrNull(json['manufacturer']),
      model: asStringOrNull(json['model']),
      os: OsInfo.fromJson(asObject(json['os'])),
      cpu: CpuInfo.fromJson(asObject(json['cpu'])),
      gpu: (json['gpu'] is List)
          ? (json['gpu'] as List).map((e) => GpuInfo.fromJson(asObject(e))).toList()
          : const <GpuInfo>[],
      memory: MemoryInfo.fromJson(asObject(json['memory'])),
      storage: (json['storage'] is List)
          ? (json['storage'] as List).map((e) => StorageVolume.fromJson(asObject(e))).toList()
          : const <StorageVolume>[],
      biosVersion: asStringOrNull(bios['version']),
      biosReleaseDate: asIntOrNull(bios['releaseDate']),
      security: SecurityInfo.fromJson(asObject(json['security'])),
      power: PowerCapabilities.fromJson(asObject(json['power'])),
      sensors: SensorSummary.fromJson(asObject(json['sensors'])),
      agent: AgentInfo.fromJson(asObject(json['agent'])),
    );
  }

  final String hostName;
  final String deviceName;
  final String? manufacturer;
  final String? model;
  final OsInfo os;
  final CpuInfo cpu;
  final List<GpuInfo> gpu;
  final MemoryInfo memory;
  final List<StorageVolume> storage;
  final String? biosVersion;
  final int? biosReleaseDate;
  final SecurityInfo security;
  final PowerCapabilities power;
  final SensorSummary sensors;
  final AgentInfo agent;

  String get displayName {
    final parts = [manufacturer, model].whereType<String>().where((s) => s.isNotEmpty).toList();
    return parts.isEmpty ? deviceName : parts.join(' ');
  }
}

class CpuMetrics {
  const CpuMetrics({
    required this.usagePercent,
    required this.perCore,
    required this.userPercent,
    required this.kernelPercent,
    required this.temperatureC,
    required this.clockMhz,
    required this.maxClockMhz,
    required this.processCount,
    required this.threadCount,
  });

  factory CpuMetrics.fromJson(Map<String, dynamic> json) => CpuMetrics(
        usagePercent: asDouble(json['usagePercent']),
        perCore: asDoubleList(json['perCore']),
        userPercent: asDouble(json['userPercent']),
        kernelPercent: asDouble(json['kernelPercent']),
        temperatureC: asDoubleOrNull(json['temperatureC']),
        clockMhz: asDoubleOrNull(json['clockMhz']),
        maxClockMhz: asInt(json['maxClockMhz']),
        processCount: asInt(json['processCount']),
        threadCount: asInt(json['threadCount']),
      );

  final double usagePercent;
  final List<double> perCore;
  final double userPercent;
  final double kernelPercent;
  final double? temperatureC;
  final double? clockMhz;
  final int maxClockMhz;
  final int processCount;
  final int threadCount;
}

class SwapMetrics {
  const SwapMetrics({required this.totalBytes, required this.usedBytes});
  factory SwapMetrics.fromJson(Map<String, dynamic> json) => SwapMetrics(
        totalBytes: asInt(json['totalBytes']),
        usedBytes: asInt(json['usedBytes']),
      );
  final int totalBytes;
  final int usedBytes;
}

class MemoryMetrics {
  const MemoryMetrics({
    required this.totalBytes,
    required this.usedBytes,
    required this.availableBytes,
    required this.usedPercent,
    required this.cachedBytes,
    required this.committedBytes,
    required this.commitLimitBytes,
    required this.swap,
  });

  factory MemoryMetrics.fromJson(Map<String, dynamic> json) => MemoryMetrics(
        totalBytes: asInt(json['totalBytes']),
        usedBytes: asInt(json['usedBytes']),
        availableBytes: asInt(json['availableBytes']),
        usedPercent: asDouble(json['usedPercent']),
        cachedBytes: asIntOrNull(json['cachedBytes']),
        committedBytes: asIntOrNull(json['committedBytes']),
        commitLimitBytes: asIntOrNull(json['commitLimitBytes']),
        swap: SwapMetrics.fromJson(asObject(json['swap'])),
      );

  final int totalBytes;
  final int usedBytes;
  final int availableBytes;
  final double usedPercent;
  final int? cachedBytes;
  final int? committedBytes;
  final int? commitLimitBytes;
  final SwapMetrics swap;
}

class GpuMetrics {
  const GpuMetrics({
    required this.index,
    required this.name,
    required this.usagePercent,
    required this.memoryUsedBytes,
    required this.memoryTotalBytes,
    required this.temperatureC,
    required this.fanRpm,
    required this.powerWatts,
    required this.vendor,
    required this.source,
  });

  factory GpuMetrics.fromJson(Map<String, dynamic> json) => GpuMetrics(
        index: asInt(json['index']),
        name: asString(json['name']),
        usagePercent: asDouble(json['usagePercent']),
        memoryUsedBytes: asIntOrNull(json['memoryUsedBytes']),
        memoryTotalBytes: asIntOrNull(json['memoryTotalBytes']),
        temperatureC: asDoubleOrNull(json['temperatureC']),
        fanRpm: asIntOrNull(json['fanRpm']),
        powerWatts: asDoubleOrNull(json['powerWatts']),
        vendor: asString(json['vendor'], 'unknown'),
        source: asString(json['source'], 'unavailable'),
      );

  final int index;
  final String name;
  final double usagePercent;
  final int? memoryUsedBytes;
  final int? memoryTotalBytes;
  final double? temperatureC;
  final int? fanRpm;
  final double? powerWatts;
  final String vendor;
  final String source;
}

class GpuAggregate {
  const GpuAggregate({
    required this.usagePercent,
    required this.maxTemperatureC,
    required this.dominantIndex,
  });

  factory GpuAggregate.fromJson(Map<String, dynamic> json) => GpuAggregate(
        usagePercent: asDouble(json['usagePercent']),
        maxTemperatureC: asDoubleOrNull(json['maxTemperatureC']),
        dominantIndex: asInt(json['dominantIndex']),
      );

  final double usagePercent;
  final double? maxTemperatureC;
  final int dominantIndex;
}

class StorageMetrics {
  const StorageMetrics({
    required this.deviceId,
    required this.usedPercent,
    required this.usedBytes,
    required this.freeBytes,
    required this.totalBytes,
    required this.readBytesPerSecond,
    required this.writeBytesPerSecond,
  });

  factory StorageMetrics.fromJson(Map<String, dynamic> json) => StorageMetrics(
        deviceId: asString(json['deviceId']),
        usedPercent: asDouble(json['usedPercent']),
        usedBytes: asInt(json['usedBytes']),
        freeBytes: asInt(json['freeBytes']),
        totalBytes: asInt(json['totalBytes']),
        readBytesPerSecond: asInt(json['readBytesPerSecond']),
        writeBytesPerSecond: asInt(json['writeBytesPerSecond']),
      );

  final String deviceId;
  final double usedPercent;
  final int usedBytes;
  final int freeBytes;
  final int totalBytes;
  final int readBytesPerSecond;
  final int writeBytesPerSecond;
}

class NetworkInterfaceMetrics {
  const NetworkInterfaceMetrics({
    required this.id,
    required this.name,
    required this.description,
    required this.type,
    required this.isUp,
    required this.macAddress,
    required this.ipv4,
    required this.netmask,
    required this.gateway,
    required this.dns,
    required this.linkSpeedBitsPerSecond,
    required this.mtu,
    required this.downloadBytesPerSecond,
    required this.uploadBytesPerSecond,
    required this.rxTotalBytes,
    required this.txTotalBytes,
    required this.isPrimary,
  });

  factory NetworkInterfaceMetrics.fromJson(Map<String, dynamic> json) =>
      NetworkInterfaceMetrics(
        id: asString(json['id']),
        name: asString(json['name']),
        description: asStringOrNull(json['description']),
        type: asString(json['type'], 'unknown'),
        isUp: asBool(json['isUp']),
        macAddress: asStringOrNull(json['macAddress']),
        ipv4: asStringOrNull(json['ipv4']),
        netmask: asStringOrNull(json['netmask']),
        gateway: asStringOrNull(json['gateway']),
        dns: asStringList(json['dns']),
        linkSpeedBitsPerSecond: asInt(json['linkSpeedBitsPerSecond']),
        mtu: asInt(json['mtu']),
        downloadBytesPerSecond: asInt(json['downloadBytesPerSecond']),
        uploadBytesPerSecond: asInt(json['uploadBytesPerSecond']),
        rxTotalBytes: asInt(json['rxTotalBytes']),
        txTotalBytes: asInt(json['txTotalBytes']),
        isPrimary: asBool(json['isPrimary']),
      );

  final String id;
  final String name;
  final String? description;
  final String type;
  final bool isUp;
  final String? macAddress;
  final String? ipv4;
  final String? netmask;
  final String? gateway;
  final List<String> dns;
  final int linkSpeedBitsPerSecond;
  final int mtu;
  final int downloadBytesPerSecond;
  final int uploadBytesPerSecond;
  final int rxTotalBytes;
  final int txTotalBytes;
  final bool isPrimary;
}

class NetworkMetrics {
  const NetworkMetrics({
    required this.interfaces,
    required this.primary,
    required this.pingMs,
  });

  factory NetworkMetrics.fromJson(Map<String, dynamic> json) => NetworkMetrics(
        interfaces: (json['interfaces'] is List)
            ? (json['interfaces'] as List)
                .map((e) => NetworkInterfaceMetrics.fromJson(asObject(e)))
                .toList()
            : const <NetworkInterfaceMetrics>[],
        primary: json['primary'] is Map
            ? NetworkInterfaceMetrics.fromJson(asObject(json['primary']))
            : null,
        pingMs: asDoubleOrNull(json['pingMs']),
      );

  final List<NetworkInterfaceMetrics> interfaces;
  final NetworkInterfaceMetrics? primary;
  final double? pingMs;
}

class BatteryMetrics {
  const BatteryMetrics({
    required this.isPresent,
    required this.chargePercent,
    required this.isCharging,
    required this.remainingMinutes,
    required this.name,
  });

  factory BatteryMetrics.fromJson(Map<String, dynamic> json) => BatteryMetrics(
        isPresent: asBool(json['isPresent']),
        chargePercent: asDoubleOrNull(json['chargePercent']),
        isCharging: _boolOrNull(json['isCharging']),
        remainingMinutes: asIntOrNull(json['remainingMinutes']),
        name: asStringOrNull(json['name']),
      );

  final bool isPresent;
  final double? chargePercent;
  final bool? isCharging;
  final int? remainingMinutes;
  final String? name;
}

class LoadPoint {
  const LoadPoint({
    required this.ts,
    required this.cpu,
    required this.gpu,
    required this.ram,
  });

  factory LoadPoint.fromJson(Map<String, dynamic> json) => LoadPoint(
        ts: asInt(json['ts']),
        cpu: asDouble(json['cpu']),
        gpu: asDouble(json['gpu']),
        ram: asDouble(json['ram']),
      );

  final int ts;
  final double cpu;
  final double gpu;
  final double ram;
}

class MetricsSnapshot {
  const MetricsSnapshot({
    required this.capturedAt,
    required this.sampleWindowMs,
    required this.cpu,
    required this.memory,
    required this.gpu,
    required this.gpuAggregate,
    required this.storage,
    required this.network,
    required this.battery,
    required this.load,
  });

  factory MetricsSnapshot.fromJson(Map<String, dynamic> json) => MetricsSnapshot(
        capturedAt: asInt(json['capturedAt']),
        sampleWindowMs: asInt(json['sampleWindowMs'], 1000),
        cpu: CpuMetrics.fromJson(asObject(json['cpu'])),
        memory: MemoryMetrics.fromJson(asObject(json['memory'])),
        gpu: (json['gpu'] is List)
            ? (json['gpu'] as List).map((e) => GpuMetrics.fromJson(asObject(e))).toList()
            : const <GpuMetrics>[],
        gpuAggregate: GpuAggregate.fromJson(asObject(json['gpuAggregate'])),
        storage: (json['storage'] is List)
            ? (json['storage'] as List).map((e) => StorageMetrics.fromJson(asObject(e))).toList()
            : const <StorageMetrics>[],
        network: NetworkMetrics.fromJson(asObject(json['network'])),
        battery: json['battery'] is Map
            ? BatteryMetrics.fromJson(asObject(json['battery']))
            : null,
        load: (json['load'] is List)
            ? (json['load'] as List).map((e) => LoadPoint.fromJson(asObject(e))).toList()
            : const <LoadPoint>[],
      );

  final int capturedAt;
  final int sampleWindowMs;
  final CpuMetrics cpu;
  final MemoryMetrics memory;
  final List<GpuMetrics> gpu;
  final GpuAggregate gpuAggregate;
  final List<StorageMetrics> storage;
  final NetworkMetrics network;
  final BatteryMetrics? battery;
  final List<LoadPoint> load;
}

class ProcessInfo {
  const ProcessInfo({
    required this.pid,
    required this.name,
    required this.user,
    required this.cpuPercent,
    required this.workingSetBytes,
    required this.isElevated,
    required this.startedAt,
  });

  factory ProcessInfo.fromJson(Map<String, dynamic> json) => ProcessInfo(
        pid: asInt(json['pid']),
        name: asString(json['name']),
        user: asStringOrNull(json['user']),
        cpuPercent: asDouble(json['cpuPercent']),
        workingSetBytes: asInt(json['workingSetBytes']),
        isElevated: asBool(json['isElevated']),
        startedAt: asIntOrNull(json['startedAt']),
      );

  final int pid;
  final String name;
  final String? user;
  final double cpuPercent;
  final int workingSetBytes;
  final bool isElevated;
  final int? startedAt;
}

// =====================================================================================
// Power  (ControlContracts.cs)
// =====================================================================================

class PowerStatus {
  const PowerStatus({
    required this.state,
    required this.isLocked,
    required this.isSleeping,
    required this.isHibernating,
    required this.activeSessionId,
    required this.activeUser,
    required this.loggedIn,
    required this.supportsSleep,
    required this.supportsHibernate,
    required this.lastBoot,
    required this.lastStateChange,
    required this.pendingPowerAction,
  });

  factory PowerStatus.fromJson(Map<String, dynamic> json) => PowerStatus(
        state: asString(json['state'], 'unknown'),
        isLocked: asBool(json['isLocked']),
        isSleeping: asBool(json['isSleeping']),
        isHibernating: asBool(json['isHibernating']),
        activeSessionId: asInt(json['activeSessionId']),
        activeUser: asStringOrNull(json['activeUser']),
        loggedIn: asBool(json['loggedIn']),
        supportsSleep: asBool(json['supportsSleep'], true),
        supportsHibernate: asBool(json['supportsHibernate']),
        lastBoot: asInt(json['lastBoot']),
        lastStateChange: asInt(json['lastStateChange']),
        pendingPowerAction: asStringOrNull(json['pendingPowerAction']),
      );

  final String state;
  final bool isLocked;
  final bool isSleeping;
  final bool isHibernating;
  final int activeSessionId;
  final String? activeUser;
  final bool loggedIn;
  final bool supportsSleep;
  final bool supportsHibernate;
  final int lastBoot;
  final int lastStateChange;
  final String? pendingPowerAction;
}

class PowerActionResult {
  const PowerActionResult({
    required this.accepted,
    required this.action,
    required this.effectiveMode,
    required this.scheduledAt,
    required this.warning,
    required this.requiresInteractiveSession,
    required this.detail,
  });

  factory PowerActionResult.fromJson(Map<String, dynamic> json) => PowerActionResult(
        accepted: asBool(json['accepted']),
        action: asString(json['action']),
        effectiveMode: asStringOrNull(json['effectiveMode']),
        scheduledAt: asIntOrNull(json['scheduledAt']),
        warning: asStringOrNull(json['warning']),
        requiresInteractiveSession: asBool(json['requiresInteractiveSession']),
        detail: asStringOrNull(json['detail']),
      );

  final bool accepted;
  final String action;
  final String? effectiveMode;
  final int? scheduledAt;
  final String? warning;
  final bool requiresInteractiveSession;
  final String? detail;
}

class WolDevice {
  const WolDevice({
    required this.name,
    required this.macAddress,
    required this.connectionType,
    required this.wakeMagicPacket,
    required this.wakePattern,
  });

  factory WolDevice.fromJson(Map<String, dynamic> json) => WolDevice(
        name: asString(json['name']),
        macAddress: asString(json['macAddress']),
        connectionType: asString(json['connectionType']),
        wakeMagicPacket: asBool(json['wakeMagicPacket']),
        wakePattern: asStringOrNull(json['wakePattern']),
      );

  final String name;
  final String macAddress;
  final String connectionType;
  final bool wakeMagicPacket;
  final String? wakePattern;
}

class WolReadiness {
  const WolReadiness({
    required this.ready,
    required this.armedDevices,
    required this.macAddresses,
    required this.broadcastAddresses,
    required this.hiberbootEnabled,
    required this.issues,
  });

  factory WolReadiness.fromJson(Map<String, dynamic> json) => WolReadiness(
        ready: asBool(json['ready']),
        armedDevices: (json['armedDevices'] is List)
            ? (json['armedDevices'] as List).map((e) => WolDevice.fromJson(asObject(e))).toList()
            : const <WolDevice>[],
        macAddresses: asStringList(json['macAddresses']),
        broadcastAddresses: asStringList(json['broadcastAddresses']),
        hiberbootEnabled: asBool(json['hiberbootEnabled']),
        issues: asStringList(json['issues']),
      );

  final bool ready;
  final List<WolDevice> armedDevices;
  final List<String> macAddresses;
  final List<String> broadcastAddresses;
  final bool hiberbootEnabled;
  final List<String> issues;
}

// =====================================================================================
// Apps + files  (ControlContracts.cs)
// =====================================================================================

class AppEntry {
  const AppEntry({
    required this.id,
    required this.name,
    required this.category,
    required this.path,
    required this.iconPath,
    required this.arguments,
    required this.workingDirectory,
    required this.runAs,
    required this.enabled,
    required this.builtIn,
    required this.pinned,
    required this.suspended,
    required this.lastLaunched,
    required this.exists,
    required this.processName,
  });

  factory AppEntry.fromJson(Map<String, dynamic> json) => AppEntry(
        id: asString(json['id']),
        name: asString(json['name']),
        category: asString(json['category'], 'other'),
        path: asString(json['path']),
        iconPath: asStringOrNull(json['iconPath']),
        arguments: asStringList(json['arguments']),
        workingDirectory: asStringOrNull(json['workingDirectory']),
        runAs: asString(json['runAs'], 'interactiveUser'),
        enabled: asBool(json['enabled'], true),
        builtIn: asBool(json['builtIn']),
        pinned: asBool(json['pinned']),
        suspended: asBool(json['suspended']),
        lastLaunched: asIntOrNull(json['lastLaunched']),
        exists: asBool(json['exists'], true),
        processName: asStringOrNull(json['processName']),
      );

  final String id;
  final String name;
  final String category;
  final String path;
  final String? iconPath;
  final List<String> arguments;
  final String? workingDirectory;
  final String runAs;
  final bool enabled;
  final bool builtIn;
  final bool pinned;
  final bool suspended;
  final int? lastLaunched;
  final bool exists;
  final String? processName;
}

class AppCatalog {
  const AppCatalog({required this.apps, required this.categories});
  factory AppCatalog.fromJson(Map<String, dynamic> json) => AppCatalog(
        apps: (json['apps'] is List)
            ? (json['apps'] as List).map((e) => AppEntry.fromJson(asObject(e))).toList()
            : const <AppEntry>[],
        categories: asStringList(json['categories']),
      );
  final List<AppEntry> apps;
  final List<String> categories;
}

class LaunchResult {
  const LaunchResult({
    required this.launched,
    required this.id,
    required this.processId,
    required this.runAs,
    required this.detail,
  });

  factory LaunchResult.fromJson(Map<String, dynamic> json) => LaunchResult(
        launched: asBool(json['launched']),
        id: asString(json['id']),
        processId: asIntOrNull(json['processId']),
        runAs: asString(json['runAs']),
        detail: asStringOrNull(json['detail']),
      );

  final bool launched;
  final String id;
  final int? processId;
  final String runAs;
  final String? detail;
}

class RunningApp {
  const RunningApp({
    required this.appId,
    required this.processId,
    required this.name,
    required this.startedAt,
    required this.workingSetBytes,
  });

  factory RunningApp.fromJson(Map<String, dynamic> json) => RunningApp(
        appId: asStringOrNull(json['appId']),
        processId: asInt(json['processId']),
        name: asString(json['name']),
        startedAt: asIntOrNull(json['startedAt']),
        workingSetBytes: asInt(json['workingSetBytes']),
      );

  final String? appId;
  final int processId;
  final String name;
  final int? startedAt;
  final int workingSetBytes;
}

class TerminateResult {
  const TerminateResult({required this.terminated, required this.forced});
  factory TerminateResult.fromJson(Map<String, dynamic> json) => TerminateResult(
        terminated: asInt(json['terminated']),
        forced: asBool(json['forced']),
      );
  final int terminated;
  final bool forced;
}

class FileRoot {
  const FileRoot({
    required this.path,
    required this.label,
    required this.readOnly,
    required this.exists,
  });

  factory FileRoot.fromJson(Map<String, dynamic> json) => FileRoot(
        path: asString(json['path']),
        label: asString(json['label']),
        readOnly: asBool(json['readOnly']),
        exists: asBool(json['exists'], true),
      );

  final String path;
  final String label;
  final bool readOnly;
  final bool exists;
}

class FileRoots {
  const FileRoots({
    required this.roots,
    required this.defaultRoot,
    required this.allReadOnly,
    required this.allowDelete,
    required this.allowUpload,
    required this.maxUploadBytes,
  });

  factory FileRoots.fromJson(Map<String, dynamic> json) => FileRoots(
        roots: (json['roots'] is List)
            ? (json['roots'] as List).map((e) => FileRoot.fromJson(asObject(e))).toList()
            : const <FileRoot>[],
        defaultRoot: asStringOrNull(json['defaultRoot']),
        allReadOnly: asBool(json['readOnly']),
        allowDelete: asBool(json['allowDelete'], true),
        allowUpload: asBool(json['allowUpload'], true),
        maxUploadBytes: asInt(json['maxUploadBytes']),
      );

  final List<FileRoot> roots;
  final String? defaultRoot;
  final bool allReadOnly;
  final bool allowDelete;
  final bool allowUpload;
  final int maxUploadBytes;
}

class FileEntry {
  const FileEntry({
    required this.name,
    required this.path,
    required this.isDirectory,
    required this.sizeBytes,
    required this.lastModified,
    required this.isHidden,
    required this.isReadOnly,
    required this.childCount,
    required this.extension,
    required this.mimeType,
    required this.canDelete,
    required this.canRename,
  });

  factory FileEntry.fromJson(Map<String, dynamic> json) => FileEntry(
        name: asString(json['name']),
        path: asString(json['path']),
        isDirectory: asBool(json['isDirectory']),
        sizeBytes: asInt(json['sizeBytes']),
        lastModified: asInt(json['lastModified']),
        isHidden: asBool(json['isHidden']),
        isReadOnly: asBool(json['isReadOnly']),
        childCount: asIntOrNull(json['childCount']),
        extension: asStringOrNull(json['extension']),
        mimeType: asStringOrNull(json['mimeType']),
        canDelete: asBool(json['canDelete'], true),
        canRename: asBool(json['canRename'], true),
      );

  final String name;
  final String path;
  final bool isDirectory;
  final int sizeBytes;
  final int lastModified;
  final bool isHidden;
  final bool isReadOnly;
  final int? childCount;
  final String? extension;
  final String? mimeType;
  final bool canDelete;
  final bool canRename;
}

class FileListing {
  const FileListing({
    required this.path,
    required this.parent,
    required this.entries,
    required this.truncated,
    required this.readOnly,
    required this.totalEntries,
  });

  factory FileListing.fromJson(Map<String, dynamic> json) => FileListing(
        path: asString(json['path']),
        parent: asStringOrNull(json['parent']),
        entries: (json['entries'] is List)
            ? (json['entries'] as List).map((e) => FileEntry.fromJson(asObject(e))).toList()
            : const <FileEntry>[],
        truncated: asBool(json['truncated']),
        readOnly: asBool(json['readOnly']),
        totalEntries: asInt(json['totalEntries']),
      );

  final String path;
  final String? parent;
  final List<FileEntry> entries;
  final bool truncated;
  final bool readOnly;
  final int totalEntries;
}

class FileOperationResult {
  const FileOperationResult({required this.ok, required this.path, required this.detail});
  factory FileOperationResult.fromJson(Map<String, dynamic> json) => FileOperationResult(
        ok: asBool(json['ok'], true),
        path: asStringOrNull(json['path']),
        detail: asStringOrNull(json['detail']),
      );
  final bool ok;
  final String? path;
  final String? detail;
}

class UploadTicket {
  const UploadTicket({
    required this.ticketId,
    required this.expiresAt,
    required this.maxBytes,
    required this.uploadUrl,
  });

  factory UploadTicket.fromJson(Map<String, dynamic> json) => UploadTicket(
        ticketId: asString(json['ticketId']),
        expiresAt: asInt(json['expiresAt']),
        maxBytes: asInt(json['maxBytes']),
        uploadUrl: asString(json['uploadUrl']),
      );

  final String ticketId;
  final int expiresAt;
  final int maxBytes;
  final String uploadUrl;
}

class UploadResult {
  const UploadResult({
    required this.completed,
    required this.path,
    required this.bytesWritten,
    required this.sha256,
  });

  factory UploadResult.fromJson(Map<String, dynamic> json) => UploadResult(
        completed: asBool(json['completed']),
        path: asString(json['path']),
        bytesWritten: asInt(json['bytesWritten']),
        sha256: asStringOrNull(json['sha256']),
      );

  final bool completed;
  final String path;
  final int bytesWritten;
  final String? sha256;
}

// =====================================================================================
// Media, remote, gaming, notifications  (FeatureContracts.cs)
// =====================================================================================

class MediaState {
  const MediaState({
    required this.volume,
    required this.isMuted,
    required this.defaultDevice,
    required this.sessionPeak,
    required this.availableActions,
    required this.supported,
  });

  factory MediaState.fromJson(Map<String, dynamic> json) => MediaState(
        volume: asInt(json['volume']),
        isMuted: asBool(json['isMuted']),
        defaultDevice: asStringOrNull(json['defaultDevice']),
        sessionPeak: asInt(json['sessionPeak']),
        availableActions: asStringList(json['availableActions']),
        supported: asBool(json['supported'], true),
      );

  final int volume;
  final bool isMuted;
  final String? defaultDevice;
  final int sessionPeak;
  final List<String> availableActions;
  final bool supported;

  bool can(String action) => availableActions.isEmpty || availableActions.contains(action);
}

class MediaActionResult {
  const MediaActionResult({
    required this.accepted,
    required this.action,
    required this.volume,
    required this.isMuted,
    required this.detail,
  });

  factory MediaActionResult.fromJson(Map<String, dynamic> json) => MediaActionResult(
        accepted: asBool(json['accepted']),
        action: asString(json['action']),
        volume: asIntOrNull(json['volume']),
        isMuted: _boolOrNull(json['isMuted']),
        detail: asStringOrNull(json['detail']),
      );

  final bool accepted;
  final String action;
  final int? volume;
  final bool? isMuted;
  final String? detail;
}

class DisplayInfo {
  const DisplayInfo({
    required this.id,
    required this.name,
    required this.x,
    required this.y,
    required this.width,
    required this.height,
    required this.isPrimary,
    required this.rotation,
    required this.refreshRateHz,
    required this.scalePercent,
  });

  factory DisplayInfo.fromJson(Map<String, dynamic> json) => DisplayInfo(
        id: asInt(json['id']),
        name: asStringOrNull(json['name']),
        x: asInt(json['x']),
        y: asInt(json['y']),
        width: asInt(json['width']),
        height: asInt(json['height']),
        isPrimary: asBool(json['isPrimary']),
        rotation: asInt(json['rotation']),
        refreshRateHz: asDoubleOrNull(json['refreshRateHz']),
        scalePercent: asIntOrNull(json['scalePercent']),
      );

  final int id;
  final String? name;
  final int x;
  final int y;
  final int width;
  final int height;
  final bool isPrimary;
  final int rotation;
  final double? refreshRateHz;
  final int? scalePercent;
}

class RemoteSessionStatus {
  const RemoteSessionStatus({
    required this.available,
    required this.sessionAgentState,
    required this.active,
    required this.streaming,
    required this.connectedDeviceId,
    required this.captureSource,
    required this.maxFps,
    required this.inputEnabled,
    required this.displays,
    required this.capabilities,
  });

  factory RemoteSessionStatus.fromJson(Map<String, dynamic> json) => RemoteSessionStatus(
        available: asBool(json['available']),
        sessionAgentState: asString(json['sessionAgentState'], 'NotRunning'),
        active: asBool(json['active']),
        streaming: asBool(json['streaming']),
        connectedDeviceId: asStringOrNull(json['connectedDeviceId']),
        captureSource: asString(json['captureSource'], 'gdi'),
        maxFps: asInt(json['maxFps'], 30),
        inputEnabled: asBool(json['inputEnabled']),
        displays: (json['displays'] is List)
            ? (json['displays'] as List).map((e) => DisplayInfo.fromJson(asObject(e))).toList()
            : const <DisplayInfo>[],
        capabilities: asStringList(json['capabilities']),
      );

  final bool available;
  final String sessionAgentState;
  final bool active;
  final bool streaming;
  final String? connectedDeviceId;
  final String captureSource;
  final int maxFps;
  final bool inputEnabled;
  final List<DisplayInfo> displays;
  final List<String> capabilities;
}

class RemoteInputResult {
  const RemoteInputResult({
    required this.accepted,
    required this.delivered,
    required this.sessionAgentState,
    required this.detail,
  });

  factory RemoteInputResult.fromJson(Map<String, dynamic> json) => RemoteInputResult(
        accepted: asBool(json['accepted']),
        delivered: asInt(json['delivered']),
        sessionAgentState: asStringOrNull(json['sessionAgentState']),
        detail: asStringOrNull(json['detail']),
      );

  final bool accepted;
  final int delivered;
  final String? sessionAgentState;
  final String? detail;
}

class GamingHistoryPoint {
  const GamingHistoryPoint({
    required this.ts,
    required this.fps,
    required this.cpu,
    required this.gpu,
    required this.temp,
    required this.ping,
  });

  factory GamingHistoryPoint.fromJson(Map<String, dynamic> json) => GamingHistoryPoint(
        ts: asInt(json['ts']),
        fps: asIntOrNull(json['fps']),
        cpu: asDouble(json['cpu']),
        gpu: asDouble(json['gpu']),
        temp: asDoubleOrNull(json['temp']),
        ping: asDoubleOrNull(json['ping']),
      );

  final int ts;
  final int? fps;
  final double cpu;
  final double gpu;
  final double? temp;
  final double? ping;
}

class GamingState {
  const GamingState({
    required this.mode,
    required this.profileId,
    required this.activePowerScheme,
    required this.availablePowerSchemes,
    required this.fps,
    required this.fpsSource,
    required this.cpuUsagePercent,
    required this.gpuUsagePercent,
    required this.ramUsedPercent,
    required this.gpuTemperatureC,
    required this.gpuMemoryUsedBytes,
    required this.gpuMemoryTotalBytes,
    required this.pingMs,
    required this.networkDownBytesPerSecond,
    required this.networkUpBytesPerSecond,
    required this.suspendedApps,
    required this.launchAppId,
    required this.history,
    required this.sessionAgentConnected,
  });

  factory GamingState.fromJson(Map<String, dynamic> json) => GamingState(
        mode: asString(json['mode'], 'off'),
        profileId: asStringOrNull(json['profileId']),
        activePowerScheme: asStringOrNull(json['activePowerScheme']),
        availablePowerSchemes: asStringList(json['availablePowerSchemes']),
        fps: asIntOrNull(json['fps']),
        fpsSource: asStringOrNull(json['fpsSource']),
        cpuUsagePercent: asDouble(json['cpuUsagePercent']),
        gpuUsagePercent: asDouble(json['gpuUsagePercent']),
        ramUsedPercent: asDouble(json['ramUsedPercent']),
        gpuTemperatureC: asDoubleOrNull(json['gpuTemperatureC']),
        gpuMemoryUsedBytes: asIntOrNull(json['gpuMemoryUsedBytes']),
        gpuMemoryTotalBytes: asIntOrNull(json['gpuMemoryTotalBytes']),
        pingMs: asDoubleOrNull(json['pingMs']),
        networkDownBytesPerSecond: asInt(json['networkDownBytesPerSecond']),
        networkUpBytesPerSecond: asInt(json['networkUpBytesPerSecond']),
        suspendedApps: asStringList(json['suspendedApps']),
        launchAppId: asStringOrNull(json['launchAppId']),
        history: (json['history'] is List)
            ? (json['history'] as List)
                .map((e) => GamingHistoryPoint.fromJson(asObject(e)))
                .toList()
            : const <GamingHistoryPoint>[],
        sessionAgentConnected: asBool(json['sessionAgentConnected']),
      );

  final String mode;
  final String? profileId;
  final String? activePowerScheme;
  final List<String> availablePowerSchemes;
  final int? fps;
  final String? fpsSource;
  final double cpuUsagePercent;
  final double gpuUsagePercent;
  final double ramUsedPercent;
  final double? gpuTemperatureC;
  final int? gpuMemoryUsedBytes;
  final int? gpuMemoryTotalBytes;
  final double? pingMs;
  final int networkDownBytesPerSecond;
  final int networkUpBytesPerSecond;
  final List<String> suspendedApps;
  final String? launchAppId;
  final List<GamingHistoryPoint> history;
  final bool sessionAgentConnected;

  bool get isOn => mode != 'off';
}

class GamingProfile {
  const GamingProfile({
    required this.id,
    required this.name,
    required this.powerScheme,
    required this.description,
    required this.builtIn,
  });

  factory GamingProfile.fromJson(Map<String, dynamic> json) => GamingProfile(
        id: asString(json['id']),
        name: asString(json['name']),
        powerScheme: asStringOrNull(json['powerScheme']),
        description: asStringOrNull(json['description']),
        builtIn: asBool(json['builtIn']),
      );

  final String id;
  final String name;
  final String? powerScheme;
  final String? description;
  final bool builtIn;
}

class GamingModeResult {
  const GamingModeResult({
    required this.enabled,
    required this.profileId,
    required this.previousPowerScheme,
    required this.suspendedApps,
    required this.warnings,
  });

  factory GamingModeResult.fromJson(Map<String, dynamic> json) => GamingModeResult(
        enabled: asBool(json['enabled']),
        profileId: asStringOrNull(json['profileId']),
        previousPowerScheme: asStringOrNull(json['previousPowerScheme']),
        suspendedApps: asStringList(json['suspendedApps']),
        warnings: asStringList(json['warnings']),
      );

  final bool enabled;
  final String? profileId;
  final String? previousPowerScheme;
  final List<String> suspendedApps;
  final List<String> warnings;
}

class EsiNotification {
  const EsiNotification({
    required this.id,
    required this.kind,
    required this.severity,
    required this.title,
    required this.body,
    required this.createdAt,
    required this.isRead,
    required this.source,
    required this.deviceId,
    required this.data,
  });

  factory EsiNotification.fromJson(Map<String, dynamic> json) => EsiNotification(
        id: asInt(json['id']),
        kind: asString(json['kind'], 'system'),
        severity: asString(json['severity'], 'info'),
        title: asString(json['title']),
        body: asStringOrNull(json['body']),
        createdAt: asInt(json['createdAt']),
        isRead: asBool(json['isRead']),
        source: asStringOrNull(json['source']),
        deviceId: asStringOrNull(json['deviceId']),
        data: asStringMap(json['data']),
      );

  final int id;
  final String kind;
  final String severity;
  final String title;
  final String? body;
  final int createdAt;
  final bool isRead;
  final String? source;
  final String? deviceId;
  final Map<String, String> data;
}

class NotificationPage {
  const NotificationPage({
    required this.notifications,
    required this.unreadCount,
    required this.total,
  });

  factory NotificationPage.fromJson(Map<String, dynamic> json) => NotificationPage(
        notifications: (json['notifications'] is List)
            ? (json['notifications'] as List)
                .map((e) => EsiNotification.fromJson(asObject(e)))
                .toList()
            : const <EsiNotification>[],
        unreadCount: asInt(json['unreadCount']),
        total: asInt(json['total']),
      );

  final List<EsiNotification> notifications;
  final int unreadCount;
  final int total;
}

class NotificationRule {
  const NotificationRule({
    required this.id,
    required this.enabled,
    required this.severity,
    required this.title,
    required this.body,
    required this.threshold,
    required this.sustainSeconds,
    required this.cooldownSeconds,
    required this.builtIn,
    required this.condition,
  });

  factory NotificationRule.fromJson(Map<String, dynamic> json) => NotificationRule(
        id: asString(json['id']),
        enabled: asBool(json['enabled'], true),
        severity: asString(json['severity'], 'warning'),
        title: asString(json['title']),
        body: asStringOrNull(json['body']),
        threshold: asDoubleOrNull(json['threshold']),
        sustainSeconds: asInt(json['sustainSeconds']),
        cooldownSeconds: asInt(json['cooldownSeconds'], 300),
        builtIn: asBool(json['builtIn']),
        condition: asString(json['condition']),
      );

  final String id;
  final bool enabled;
  final String severity;
  final String title;
  final String? body;
  final double? threshold;
  final int sustainSeconds;
  final int cooldownSeconds;
  final bool builtIn;
  final String condition;

  Map<String, dynamic> toJson() => <String, dynamic>{
        'id': id,
        'enabled': enabled,
        'severity': severity,
        'title': title,
        if (body != null) 'body': body,
        if (threshold != null) 'threshold': threshold,
        'sustainSeconds': sustainSeconds,
        'cooldownSeconds': cooldownSeconds,
        'builtIn': builtIn,
        'condition': condition,
      };
}

// =====================================================================================
// Settings  (FeatureContracts.cs)
// =====================================================================================

class SecuritySettings {
  const SecuritySettings({
    required this.localNetworkOnly,
    required this.requireSignedRequests,
    required this.allowedSubnets,
    required this.clockSkewSeconds,
    required this.maxPairedDevices,
    required this.rateLimitPerMinute,
    required this.pairingWindowSeconds,
    required this.sessionAgentRequiredForLock,
    required this.tlsEnabled,
  });

  factory SecuritySettings.fromJson(Map<String, dynamic> json) => SecuritySettings(
        localNetworkOnly: asBool(json['localNetworkOnly'], true),
        requireSignedRequests: asBool(json['requireSignedRequests'], true),
        allowedSubnets: asStringList(json['allowedSubnets']),
        clockSkewSeconds: asInt(json['clockSkewSeconds'], EsiProtocol.defaultClockSkewSeconds),
        maxPairedDevices: asInt(json['maxPairedDevices'], 8),
        rateLimitPerMinute: asInt(json['rateLimitPerMinute'], 240),
        pairingWindowSeconds: asInt(json['pairingWindowSeconds'], 120),
        sessionAgentRequiredForLock: asBool(json['sessionAgentRequiredForLock'], true),
        tlsEnabled: asBool(json['tlsEnabled'], true),
      );

  final bool localNetworkOnly;
  final bool requireSignedRequests;
  final List<String> allowedSubnets;
  final int clockSkewSeconds;
  final int maxPairedDevices;
  final int rateLimitPerMinute;
  final int pairingWindowSeconds;
  final bool sessionAgentRequiredForLock;
  final bool tlsEnabled;
}

class MetricsSettings {
  const MetricsSettings({
    required this.sampleIntervalMs,
    required this.historyLimit,
    required this.collectGpu,
    required this.collectTemperatures,
  });

  factory MetricsSettings.fromJson(Map<String, dynamic> json) => MetricsSettings(
        sampleIntervalMs: asInt(json['sampleIntervalMs'], 1000),
        historyLimit: asInt(json['historyLimit'], 300),
        collectGpu: asBool(json['collectGpu'], true),
        collectTemperatures: asBool(json['collectTemperatures'], true),
      );

  final int sampleIntervalMs;
  final int historyLimit;
  final bool collectGpu;
  final bool collectTemperatures;
}

class FileSettings {
  const FileSettings({
    required this.allowedRoots,
    required this.allowDelete,
    required this.allowUpload,
    required this.maxUploadBytes,
  });

  factory FileSettings.fromJson(Map<String, dynamic> json) => FileSettings(
        allowedRoots: (json['allowedRoots'] is List)
            ? (json['allowedRoots'] as List)
                .map((e) => FileRoot.fromJson(asObject(e)))
                .toList()
            : const <FileRoot>[],
        allowDelete: asBool(json['allowDelete'], true),
        allowUpload: asBool(json['allowUpload'], true),
        maxUploadBytes: asInt(json['maxUploadBytes']),
      );

  final List<FileRoot> allowedRoots;
  final bool allowDelete;
  final bool allowUpload;
  final int maxUploadBytes;
}

class GamingSettings {
  const GamingSettings({
    required this.enabled,
    required this.profileId,
    required this.suspendApps,
    required this.launchAppId,
  });

  factory GamingSettings.fromJson(Map<String, dynamic> json) => GamingSettings(
        enabled: asBool(json['enabled']),
        profileId: asString(json['profileId'], 'performance'),
        suspendApps: asStringList(json['suspendApps']),
        launchAppId: asStringOrNull(json['launchAppId']),
      );

  final bool enabled;
  final String profileId;
  final List<String> suspendApps;
  final String? launchAppId;
}

class NotificationSettings {
  const NotificationSettings({
    required this.rules,
    required this.retentionDays,
    required this.toastEnabled,
  });

  factory NotificationSettings.fromJson(Map<String, dynamic> json) => NotificationSettings(
        rules: (json['rules'] is List)
            ? (json['rules'] as List).map((e) => NotificationRule.fromJson(asObject(e))).toList()
            : const <NotificationRule>[],
        retentionDays: asInt(json['retentionDays'], 14),
        toastEnabled: asBool(json['toastEnabled'], true),
      );

  final List<NotificationRule> rules;
  final int retentionDays;
  final bool toastEnabled;
}

class DebugSettings {
  const DebugSettings({
    required this.enabled,
    required this.verboseLogging,
    required this.allowLoopbackOnly,
    required this.logRetentionDays,
  });

  factory DebugSettings.fromJson(Map<String, dynamic> json) => DebugSettings(
        enabled: asBool(json['enabled']),
        verboseLogging: asBool(json['verboseLogging']),
        allowLoopbackOnly: asBool(json['allowLoopbackOnly']),
        logRetentionDays: asInt(json['logRetentionDays'], 14),
      );

  final bool enabled;
  final bool verboseLogging;
  final bool allowLoopbackOnly;
  final int logRetentionDays;
}

class AgentSettings {
  const AgentSettings({
    required this.port,
    required this.bindAddress,
    required this.deviceName,
    required this.discoveryEnabled,
    required this.discoveryPort,
    required this.mdnsEnabled,
    required this.startWithWindows,
    required this.wakeOnLanEnabled,
    required this.security,
    required this.metrics,
    required this.files,
    required this.apps,
    required this.gaming,
    required this.notifications,
    required this.debug,
  });

  factory AgentSettings.fromJson(Map<String, dynamic> json) => AgentSettings(
        port: asInt(json['port'], EsiProtocol.defaultHttpsPort),
        bindAddress: asStringOrNull(json['bindAddress']),
        deviceName: asStringOrNull(json['deviceName']),
        discoveryEnabled: asBool(json['discoveryEnabled'], true),
        discoveryPort: asInt(json['discoveryPort'], EsiProtocol.defaultDiscoveryPort),
        mdnsEnabled: asBool(json['mdnsEnabled'], true),
        startWithWindows: asBool(json['startWithWindows'], true),
        wakeOnLanEnabled: asBool(json['wakeOnLanEnabled'], true),
        security: SecuritySettings.fromJson(asObject(json['security'])),
        metrics: MetricsSettings.fromJson(asObject(json['metrics'])),
        files: FileSettings.fromJson(asObject(json['files'])),
        apps: (json['apps'] is List)
            ? (json['apps'] as List).map((e) => AppEntry.fromJson(asObject(e))).toList()
            : const <AppEntry>[],
        gaming: GamingSettings.fromJson(asObject(json['gaming'])),
        notifications: NotificationSettings.fromJson(asObject(json['notifications'])),
        debug: DebugSettings.fromJson(asObject(json['debug'])),
      );

  final int port;
  final String? bindAddress;
  final String? deviceName;
  final bool discoveryEnabled;
  final int discoveryPort;
  final bool mdnsEnabled;
  final bool startWithWindows;
  final bool wakeOnLanEnabled;
  final SecuritySettings security;
  final MetricsSettings metrics;
  final FileSettings files;
  final List<AppEntry> apps;
  final GamingSettings gaming;
  final NotificationSettings notifications;
  final DebugSettings debug;

  /// Round-trips the document the agent handed us, with the fields the app exposes edited.
  Map<String, dynamic> toJson() => <String, dynamic>{
        'port': port,
        if (bindAddress != null) 'bindAddress': bindAddress,
        if (deviceName != null) 'deviceName': deviceName,
        'discoveryEnabled': discoveryEnabled,
        'discoveryPort': discoveryPort,
        'mdnsEnabled': mdnsEnabled,
        'startWithWindows': startWithWindows,
        'wakeOnLanEnabled': wakeOnLanEnabled,
        'security': <String, dynamic>{
          'localNetworkOnly': security.localNetworkOnly,
          'requireSignedRequests': security.requireSignedRequests,
          'allowedSubnets': security.allowedSubnets,
          'clockSkewSeconds': security.clockSkewSeconds,
          'maxPairedDevices': security.maxPairedDevices,
          'rateLimitPerMinute': security.rateLimitPerMinute,
          'pairingWindowSeconds': security.pairingWindowSeconds,
          'sessionAgentRequiredForLock': security.sessionAgentRequiredForLock,
          'tlsEnabled': security.tlsEnabled,
        },
        'metrics': <String, dynamic>{
          'sampleIntervalMs': metrics.sampleIntervalMs,
          'historyLimit': metrics.historyLimit,
          'collectGpu': metrics.collectGpu,
          'collectTemperatures': metrics.collectTemperatures,
        },
        'files': <String, dynamic>{
          'allowedRoots': <Map<String, dynamic>>[
            for (final root in files.allowedRoots)
              <String, dynamic>{'path': root.path, 'readOnly': root.readOnly},
          ],
          'allowDelete': files.allowDelete,
          'allowUpload': files.allowUpload,
          'maxUploadBytes': files.maxUploadBytes,
        },
        'apps': <Map<String, dynamic>>[
          for (final app in apps)
            <String, dynamic>{
              'id': app.id,
              'name': app.name,
              'category': app.category,
              'path': app.path,
              if (app.iconPath != null) 'iconPath': app.iconPath,
              'arguments': app.arguments,
              if (app.workingDirectory != null) 'workingDirectory': app.workingDirectory,
              'runAs': app.runAs,
              'enabled': app.enabled,
              'builtIn': app.builtIn,
              'pinned': app.pinned,
              'suspended': app.suspended,
              if (app.lastLaunched != null) 'lastLaunched': app.lastLaunched,
            },
        ],
        'gaming': <String, dynamic>{
          'enabled': gaming.enabled,
          'profileId': gaming.profileId,
          'suspendApps': gaming.suspendApps,
          if (gaming.launchAppId != null) 'launchAppId': gaming.launchAppId,
        },
        'notifications': <String, dynamic>{
          'rules': <Map<String, dynamic>>[
            for (final rule in notifications.rules) rule.toJson(),
          ],
          'retentionDays': notifications.retentionDays,
          'toastEnabled': notifications.toastEnabled,
        },
        'debug': <String, dynamic>{
          'enabled': debug.enabled,
          'verboseLogging': debug.verboseLogging,
          'allowLoopbackOnly': debug.allowLoopbackOnly,
          'logRetentionDays': debug.logRetentionDays,
        },
      };
}

class SettingsUpdateResult {
  const SettingsUpdateResult({
    required this.ok,
    required this.restartRequired,
    required this.appliedAt,
    required this.warnings,
  });

  factory SettingsUpdateResult.fromJson(Map<String, dynamic> json) => SettingsUpdateResult(
        ok: asBool(json['ok'], true),
        restartRequired: asBool(json['restartRequired']),
        appliedAt: asInt(json['appliedAt']),
        warnings: asStringList(json['warnings']),
      );

  final bool ok;
  final bool restartRequired;
  final int appliedAt;
  final List<String> warnings;
}

class LogLine {
  const LogLine({
    required this.ts,
    required this.level,
    required this.logger,
    required this.message,
    required this.exception,
  });

  factory LogLine.fromJson(Map<String, dynamic> json) => LogLine(
        ts: asInt(json['ts']),
        level: asString(json['level'], 'Information'),
        logger: asStringOrNull(json['logger']),
        message: asString(json['message']),
        exception: asStringOrNull(json['exception']),
      );

  final int ts;
  final String level;
  final String? logger;
  final String message;
  final String? exception;
}

class LogTail {
  const LogTail({
    required this.lines,
    required this.truncated,
    required this.logDirectory,
  });

  factory LogTail.fromJson(Map<String, dynamic> json) => LogTail(
        lines: (json['lines'] is List)
            ? (json['lines'] as List).map((e) => LogLine.fromJson(asObject(e))).toList()
            : const <LogLine>[],
        truncated: asBool(json['truncated']),
        logDirectory: asStringOrNull(json['logDirectory']),
      );

  final List<LogLine> lines;
  final bool truncated;
  final String? logDirectory;
}

class AuditEntry {
  const AuditEntry({
    required this.id,
    required this.ts,
    required this.deviceId,
    required this.deviceName,
    required this.remoteAddress,
    required this.action,
    required this.result,
    required this.detail,
    required this.chainHash,
  });

  factory AuditEntry.fromJson(Map<String, dynamic> json) => AuditEntry(
        id: asInt(json['id']),
        ts: asInt(json['ts']),
        deviceId: asStringOrNull(json['deviceId']),
        deviceName: asStringOrNull(json['deviceName']),
        remoteAddress: asStringOrNull(json['remoteAddress']),
        action: asString(json['action']),
        result: asString(json['result']),
        detail: asStringOrNull(json['detail']),
        chainHash: asStringOrNull(json['chainHash']),
      );

  final int id;
  final int ts;
  final String? deviceId;
  final String? deviceName;
  final String? remoteAddress;
  final String action;
  final String result;
  final String? detail;
  final String? chainHash;
}

class AuditTrail {
  const AuditTrail({required this.entries, required this.chainValid});
  factory AuditTrail.fromJson(Map<String, dynamic> json) => AuditTrail(
        entries: (json['entries'] is List)
            ? (json['entries'] as List).map((e) => AuditEntry.fromJson(asObject(e))).toList()
            : const <AuditEntry>[],
        chainValid: asBool(json['chainValid'], true),
      );
  final List<AuditEntry> entries;
  final bool chainValid;
}

class Health {
  const Health({
    required this.status,
    required this.version,
    required this.uptimeSeconds,
    required this.serverTime,
    required this.serviceAccount,
    required this.isElevated,
    required this.sessionId,
    required this.debugMode,
  });

  factory Health.fromJson(Map<String, dynamic> json) => Health(
        status: asString(json['status'], 'ok'),
        version: asString(json['version']),
        uptimeSeconds: asInt(json['uptimeSeconds']),
        serverTime: asInt(json['serverTime']),
        serviceAccount: asStringOrNull(json['serviceAccount']),
        isElevated: asBool(json['isElevated']),
        sessionId: asInt(json['sessionId']),
        debugMode: asBool(json['debugMode']),
      );

  final String status;
  final String version;
  final int uptimeSeconds;
  final int serverTime;
  final String? serviceAccount;
  final bool isElevated;
  final int sessionId;
  final bool debugMode;
}

/// `{"error": {"code", "message", "requestId", "details"}}`.
class EsiApiErrorBody {
  const EsiApiErrorBody({
    required this.code,
    required this.message,
    required this.requestId,
    required this.details,
  });

  factory EsiApiErrorBody.fromJson(Map<String, dynamic> json) => EsiApiErrorBody(
        code: asString(json['code'], EsiProtocol.errInternal),
        message: asString(json['message'], 'The agent reported an error.'),
        requestId: asStringOrNull(json['requestId']),
        details: asStringMap(json['details']),
      );

  final String code;
  final String message;
  final String? requestId;
  final Map<String, String> details;
}

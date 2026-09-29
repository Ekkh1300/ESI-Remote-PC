/// Value formatting.
///
/// Two rules run through all of it:
///
/// * A sensor the agent did not report formats as [notReported] — a dim em dash — and
///   never as `0`, `-`, or an invented number. `null` means "no trustworthy reading
///   exists", and a zero is a claim about the hardware that was never made.
/// * Sizes, rates and temperatures get a real unit and a sensible precision, because
///   "1240" without a unit is not a reading, it is a number.
library;

import '../protocol/models.dart';

/// What a panel shows instead of a value the PC did not report.
const String notReported = '—';

/// `false` for a value the agent reported as `null`.
bool reported(double? value) => value != null;

/// Bytes as a binary unit, e.g. `15.4 GB`.
String formatBytes(num? bytes, {int decimals = 1}) {
  if (bytes == null) return notReported;
  const units = <String>['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  var value = bytes.toDouble();
  var unit = 0;
  while (value.abs() >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  final places = value.abs() >= 100 || unit == 0 ? 0 : decimals;
  return '${value.toStringAsFixed(places)} ${units[unit]}';
}

/// Bytes per second as a rate, e.g. `12.5 MB/s`.
String formatRate(num? bytesPerSecond) {
  if (bytesPerSecond == null) return notReported;
  if (bytesPerSecond == 0) return 'idle';
  return '${formatBytes(bytesPerSecond)}/s';
}

/// Bits per second as a link speed, e.g. `866 Mbit/s`.
String formatLinkSpeed(num? bitsPerSecond) {
  if (bitsPerSecond == null || bitsPerSecond == 0) return notReported;
  const units = <String>['bit/s', 'Kbit/s', 'Mbit/s', 'Gbit/s'];
  var value = bitsPerSecond.toDouble();
  var unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  return '${value.toStringAsFixed(value >= 100 || unit == 0 ? 0 : 1)} ${units[unit]}';
}

/// A percentage, or the em dash when there is nothing to report.
String formatPercent(double? value, {int decimals = 0}) {
  if (value == null) return notReported;
  return '${value.toStringAsFixed(decimals)}%';
}

/// Degrees Celsius, or the em dash. Never substitutes a plausible-looking number.
String formatCelsius(double? celsius) {
  if (celsius == null) return notReported;
  return '${celsius.toStringAsFixed(celsius >= 100 ? 0 : 1)} °C';
}

String formatRpm(int? rpm) => rpm == null ? notReported : '$rpm rpm';

String formatWatts(double? watts) =>
    watts == null ? notReported : '${watts.toStringAsFixed(0)} W';

String formatMilliseconds(double? ms) =>
    ms == null ? notReported : '${ms.toStringAsFixed(ms < 10 ? 1 : 0)} ms';

String formatHz(int? hz) => hz == null ? notReported : '$hz Hz';

/// `4 d 7 h` / `7 h 12 m` / `12 m 30 s`, from a duration in seconds.
String formatUptime(int? seconds) {
  if (seconds == null) return notReported;
  if (seconds < 0) return notReported;
  final days = seconds ~/ 86400;
  final hours = (seconds % 86400) ~/ 3600;
  final minutes = (seconds % 3600) ~/ 60;
  if (days > 0) return '${days}d ${hours}h';
  if (hours > 0) return '${hours}h ${minutes}m';
  if (minutes > 0) return '${minutes}m ${seconds % 60}s';
  return '${seconds}s';
}

/// "just now" / "12 min ago" / "3 h ago" / a date, for Unix epoch seconds.
String formatRelative(int? epochSeconds, {DateTime? now}) {
  if (epochSeconds == null || epochSeconds == 0) return notReported;
  final then = DateTime.fromMillisecondsSinceEpoch(epochSeconds * 1000);
  final reference = now ?? DateTime.now();
  final delta = reference.difference(then);
  if (delta.isNegative) {
    final ahead = then.difference(reference);
    if (ahead.inSeconds < 60) return 'in ${ahead.inSeconds}s';
    if (ahead.inMinutes < 60) return 'in ${ahead.inMinutes} min';
    return 'in ${ahead.inHours} h';
  }
  if (delta.inSeconds < 45) return 'just now';
  if (delta.inMinutes < 60) return '${delta.inMinutes} min ago';
  if (delta.inHours < 24) return '${delta.inHours} h ago';
  if (delta.inDays < 7) return '${delta.inDays} d ago';
  return '${then.year}-${_two(then.month)}-${_two(then.day)}';
}

/// `14:03:22` for a Unix epoch second.
String formatClock(int? epochSeconds) {
  if (epochSeconds == null || epochSeconds == 0) return notReported;
  final t = DateTime.fromMillisecondsSinceEpoch(epochSeconds * 1000);
  return '${_two(t.hour)}:${_two(t.minute)}:${_two(t.second)}';
}

String formatDate(int? epochSeconds) {
  if (epochSeconds == null || epochSeconds == 0) return notReported;
  final t = DateTime.fromMillisecondsSinceEpoch(epochSeconds * 1000);
  return '${t.year}-${_two(t.month)}-${_two(t.day)}';
}

String _two(int value) => value.toString().padLeft(2, '0');

/// A long value broken so it wraps in a narrow column: `MIIB…` stays readable.
String formatBytesBase64(String? value, {int head = 12, int tail = 8}) {
  if (value == null || value.isEmpty) return notReported;
  if (value.length <= head + tail + 1) return value;
  return '${value.substring(0, head)}…${value.substring(value.length - tail)}';
}

/// A hex device id, grouped in fours for legibility.
String formatDeviceId(String? deviceId) {
  if (deviceId == null || deviceId.isEmpty) return notReported;
  return deviceId.replaceAllMapped(
    RegExp('.{1,4}'),
    (m) => m.group(0)!,
  ).replaceAll(RegExp('(?<=.{4})(?=.)'), ' ');
}

/// The severity of a reading, used to pick the panel's signal colour. Deliberately
/// conservative: an unreported sensor is *unknown*, not *fine*, and gets its own colour.
enum Severity { nominal, elevated, critical, unknown }

Severity severityForPercent(double? value, {double elevated = 70, double critical = 90}) {
  if (value == null) return Severity.unknown;
  if (value >= critical) return Severity.critical;
  if (value >= elevated) return Severity.elevated;
  return Severity.nominal;
}

Severity severityForCelsius(double? celsius, {double elevated = 75, double critical = 90}) {
  if (celsius == null) return Severity.unknown;
  if (celsius >= critical) return Severity.critical;
  if (celsius >= elevated) return Severity.elevated;
  return Severity.nominal;
}

/// Severity for a disk volume. Full disks are a different failure from hot ones, so the
/// thresholds are closer together than the temperature ones.
Severity severityForDisk(double? usedPercent) {
  if (usedPercent == null) return Severity.unknown;
  if (usedPercent >= 95) return Severity.critical;
  if (usedPercent >= 85) return Severity.elevated;
  return Severity.nominal;
}

/// Turns a `null` reading into the em dash, and otherwise hands the value to [format].
String orNotReported(double? value, String Function(double) format) =>
    value == null ? notReported : format(value);

/// Notification severity → the palette colour it should be drawn in.
String severityColourFor(String severity) => switch (severity) {
      'critical' => 'alarm',
      'warning' => 'flare',
      _ => 'signal',
    };

/// A one-line human summary of an agent event, for the notification list.
String describeEvent(String kind, Map<String, dynamic> data) {
  final state = data['state']?.toString();
  return switch (kind) {
    'power.state' => 'Power state is now ${state ?? 'unknown'}',
    'power.action' => '${data['action'] ?? 'A power action'} was requested'
        '${data['actor'] != null ? ' from ${data['actor']}' : ''}',
    _ => kind.replaceAll('.', ' · ').replaceAll('-', ' '),
  };
}

/// `META-PC` from a `MetricsSnapshot`-free context: the best available name.
String agentLabel(Handshake? handshake, String? fallback) {
  if (handshake == null) return fallback ?? 'No PC';
  if (handshake.deviceName.isNotEmpty) return handshake.deviceName;
  if (handshake.hostName.isNotEmpty) return handshake.hostName;
  return fallback ?? 'PC';
}

/// Defensive JSON accessors.
///
/// The agent is a different codebase on a different OS; the protocol allows new optional
/// fields at any time and a client MUST ignore what it does not understand. Every model in
/// this package therefore reads through these helpers, which never throw on a missing or
/// unexpected value — they return the documented fallback instead.
library;

import 'dart:convert';

int asInt(Object? value, [int fallback = 0]) {
  if (value is int) return value;
  if (value is double) return value.isFinite ? value.round() : fallback;
  if (value is String) return int.tryParse(value) ?? double.tryParse(value)?.round() ?? fallback;
  return fallback;
}

int? asIntOrNull(Object? value) {
  if (value == null) return null;
  if (value is int) return value;
  if (value is double) return value.isFinite ? value.round() : null;
  if (value is String) return int.tryParse(value) ?? double.tryParse(value)?.round();
  return null;
}

double asDouble(Object? value, [double fallback = 0]) {
  if (value is double) return value.isFinite ? value : fallback;
  if (value is int) return value.toDouble();
  if (value is String) return double.tryParse(value) ?? fallback;
  return fallback;
}

/// `null` in, `null` out. This is the accessor that keeps an unreported sensor reading
/// unreported instead of collapsing it to zero.
double? asDoubleOrNull(Object? value) {
  if (value == null) return null;
  if (value is double) return value.isFinite ? value : null;
  if (value is int) return value.toDouble();
  if (value is String) {
    final parsed = double.tryParse(value);
    return (parsed != null && parsed.isFinite) ? parsed : null;
  }
  return null;
}

bool asBool(Object? value, [bool fallback = false]) {
  if (value is bool) return value;
  if (value is num) return value != 0;
  if (value is String) {
    if (value.toLowerCase() == 'true') return true;
    if (value.toLowerCase() == 'false') return false;
  }
  return fallback;
}

String asString(Object? value, [String fallback = '']) {
  if (value is String) return value;
  if (value == null) return fallback;
  return value.toString();
}

String? asStringOrNull(Object? value) {
  if (value is String) return value.isEmpty ? null : value;
  if (value == null) return null;
  final s = value.toString();
  return s.isEmpty ? null : s;
}

List<String> asStringList(Object? value) {
  if (value is List) {
    return value.whereType<Object>().map(asString).where((s) => s.isNotEmpty).toList();
  }
  return const <String>[];
}

List<double> asDoubleList(Object? value) {
  if (value is List) {
    return value.map(asDoubleOrNull).whereType<double>().toList();
  }
  return const <double>[];
}

List<int> asIntList(Object? value) {
  if (value is List) {
    return value.map(asIntOrNull).whereType<int>().toList();
  }
  return const <int>[];
}

Map<String, bool> asBoolMap(Object? value) {
  if (value is Map) {
    return <String, bool>{
      for (final entry in value.entries) asString(entry.key): asBool(entry.value),
    };
  }
  return <String, bool>{};
}

Map<String, String> asStringMap(Object? value) {
  if (value is Map) {
    return <String, String>{
      for (final entry in value.entries) asString(entry.key): asString(entry.value),
    };
  }
  return <String, String>{};
}

Map<String, dynamic> asObject(Object? value) {
  if (value is Map<String, dynamic>) return value;
  if (value is Map) return value.cast<String, dynamic>();
  return <String, dynamic>{};
}

/// Parses a JSON object, returning an empty map rather than throwing. Used on the
/// unauthenticated discovery path, where a random device on the LAN may answer.
Map<String, dynamic> tryDecodeObject(String raw) {
  try {
    return asObject(jsonDecode(raw));
  } on FormatException {
    return <String, dynamic>{};
  }
}

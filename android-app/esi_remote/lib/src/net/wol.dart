import 'dart:async';
import 'dart:io';

import 'ipv4.dart';

/// Wake-on-LAN, sent by the phone rather than relayed through the agent.
///
/// PROTOCOL.md §6.4 says the phone normally emits the magic packet itself; the agent's
/// `/power/wake` endpoint exists for a future relay. Sending it here means a sleeping PC
/// can be woken even though nothing on it is running to receive a request.
class WakeOnLan {
  const WakeOnLan();

  /// Ports 7 and 9 are the conventional magic-packet ports.
  static const List<int> defaultPorts = <int>[9, 7];

  /// Sends [repeat] copies of the magic packet for [macAddress] and returns how many
  /// datagrams went out.
  ///
  /// [broadcastAddresses] defaults to the phone's own subnet-directed broadcast plus
  /// `255.255.255.255`, because plenty of access points drop the limited broadcast.
  Future<int> send(
    String macAddress, {
    List<String>? broadcastAddresses,
    List<int> ports = defaultPorts,
    int repeat = 3,
  }) async {
    final packet = magicPacket(macAddress);
    if (packet == null) {
      throw const FormatException(
        'That is not a valid MAC address. It should look like AA:BB:CC:DD:EE:FF.',
      );
    }
    final targets = (broadcastAddresses ?? await defaultBroadcastAddresses())
        .map(Ipv4.tryParse)
        .whereType<InternetAddress>()
        .toList(growable: false);
    if (targets.isEmpty) {
      throw const SocketException('No usable broadcast address for this network.');
    }

    var sent = 0;
    for (var attempt = 0; attempt < repeat; attempt++) {
      for (final target in targets) {
        for (final port in ports) {
          final socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
          try {
            socket.broadcastEnabled = true;
            socket.send(packet, target, port);
            sent++;
          } finally {
            socket.close();
          }
        }
      }
      // Spread the copies out; three packets in the same millisecond is a common cause of
      // a missed wake.
      await Future<void>.delayed(const Duration(milliseconds: 60));
    }
    return sent;
  }

  /// A 6×0xff preamble followed by the target MAC repeated 16 times.
  static List<int>? magicPacket(String macAddress) {
    final bytes = parseMac(macAddress);
    if (bytes == null) return null;
    return <int>[
      ...List<int>.filled(6, 0xff),
      for (var i = 0; i < 16; i++) ...bytes,
    ];
  }

  /// Accepts `AA:BB:CC:DD:EE:FF` and the `AA-BB-…` / `AABBCCDDEEFF` spellings.
  static List<int>? parseMac(String value) {
    final parts = value.trim().split(RegExp(r'[:\-.\s]'));
    if (parts.length == 1) {
      final hex = parts.first;
      if (hex.length != 12) return null;
      final bytes = <int>[];
      for (var i = 0; i < 12; i += 2) {
        final n = int.tryParse(hex.substring(i, i + 2), radix: 16);
        if (n == null) return null;
        bytes.add(n);
      }
      return bytes;
    }
    if (parts.length != 6) return null;
    final out = <int>[];
    for (final part in parts) {
      if (part.length != 2) return null;
      final n = int.tryParse(part, radix: 16);
      if (n == null || n < 0 || n > 255) return null;
      out.add(n);
    }
    return out;
  }

  /// Normalises a MAC to the agent's `AA:BB:CC:DD:EE:FF` form.
  static String? normaliseMac(String value) {
    final bytes = parseMac(value);
    if (bytes == null) return null;
    return bytes.map((b) => b.toRadixString(16).padLeft(2, '0').toUpperCase()).join(':');
  }

  /// The phone's subnet-directed broadcast addresses, plus the limited broadcast.
  static Future<List<String>> defaultBroadcastAddresses() async {
    final out = <String>['255.255.255.255'];
    try {
      for (final interface in await NetworkInterface.list(
        includeLoopback: false,
        includeLinkLocal: false,
        type: InternetAddressType.IPv4,
      )) {
        for (final address in interface.addresses) {
          final broadcast = Ipv4.broadcastFor(address);
          if (broadcast != null) out.add(broadcast);
        }
      }
    } on SocketException {
      // Best effort: the limited broadcast may still work.
    }
    return out;
  }
}

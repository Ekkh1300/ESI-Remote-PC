import 'dart:io';

/// IPv4 helpers.
///
/// `InternetAddress` only takes a `String`, and the OS gives us netmasks as a prefix
/// length, so the two conversions the app needs live here rather than being reinvented at
/// each call site.
class Ipv4 {
  const Ipv4._();

  /// Parses a dotted-quad literal, or returns null.
  static InternetAddress? tryParse(String value) {
    final parts = value.trim().split('.');
    if (parts.length != 4) return null;
    final octets = <int>[];
    for (final part in parts) {
      if (part.isEmpty || part.length > 3) return null;
      final n = int.tryParse(part);
      if (n == null || n < 0 || n > 255) return null;
      octets.add(n);
    }
    return InternetAddress(octets.join('.'));
  }

  /// Builds an address from four bytes, in network order.
  static InternetAddress fromBytes(List<int> bytes) {
    assert(bytes.length == 4, 'an IPv4 address is four bytes');
    return InternetAddress(bytes.take(4).join('.'));
  }

  /// The broadcast address of the subnet [address] sits on.
  ///
  /// Prefers what the platform reports, because on Android that comes from the kernel and
  /// accounts for non-/24 layouts. Falls back to deriving it from the prefix length for
  /// platforms that leave `broadcast` null.
  static String? broadcastFor(InterfaceAddress address) {
    final reported = address.broadcast?.address;
    if (reported != null && reported.isNotEmpty) return reported;
    return fromPrefix(address.address, address.prefixLength);
  }

  /// `192.168.1.42` with a 24-bit prefix -> `192.168.1.255`.
  static String? fromPrefix(String address, int prefixLength) {
    final octets = address.split('.');
    if (octets.length != 4 || prefixLength < 0 || prefixLength > 32) return null;
    final parsed = <int>[];
    for (final part in octets) {
      final n = int.tryParse(part);
      if (n == null || n < 0 || n > 255) return null;
      parsed.add(n);
    }
    final out = <String>[];
    for (var i = 0; i < 4; i++) {
      final bitsBefore = i * 8;
      final hostBits = bitsBefore >= prefixLength
          ? 8
          : (bitsBefore + 8 > prefixLength ? 8 - (prefixLength - bitsBefore) : 0);
      final hostMask = hostBits == 0 ? 0 : (0xff >> (8 - hostBits)) & 0xff;
      out.add(((parsed[i] & ~hostMask) | hostMask).toString());
    }
    return out.join('.');
  }

  /// The /24 prefix of an address, as `a.b.c`.
  static String? prefixOf(String address) {
    final parts = address.split('.');
    if (parts.length != 4) return null;
    return '${parts[0]}.${parts[1]}.${parts[2]}';
  }

  /// The host portion of an address within its /24, 1..254.
  static String? hostIn(String prefix, int index) {
    if (index < 1 || index > 254) return null;
    return '$prefix.$index';
  }
}

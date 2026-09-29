import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import '../protocol/json_read.dart';
import '../protocol/models.dart';
import '../protocol/protocol_constants.dart';
import 'ipv4.dart';
import 'spki_pinning.dart';

/// How an agent was found, so the UI can be honest about what it actually learned.
enum DiscoveryMethod {
  /// A directed TCP sweep of the local subnets plus `GET /handshake`.
  subnetSweep,

  /// A UDP broadcast probe answered by `UdpDiscoveryResponder`.
  udpBroadcast,

  /// An mDNS answer to a query for `_esiremotepc._tcp.local.`.
  mdns,
}

extension DiscoveryMethodLabel on DiscoveryMethod {
  String get label => switch (this) {
        DiscoveryMethod.subnetSweep => 'Subnet sweep',
        DiscoveryMethod.udpBroadcast => 'UDP broadcast',
        DiscoveryMethod.mdns => 'mDNS',
      };
}

/// An agent seen on the LAN.
///
/// Everything here is a *hint* until pairing. PROTOCOL.md §2.1 is explicit that a pin
/// read from `GET /handshake` could have been written by anything on the wire, and §9.1
/// says the same about a broadcast reply. So a discovered agent is always marked
/// [pinVerified] = false: it becomes true only when the phone has read the pin from the
/// pairing QR, which the human physically transferred.
class DiscoveredAgent {
  const DiscoveredAgent({
    required this.handshake,
    required this.host,
    required this.port,
    required this.methods,
    this.pinVerified = false,
  });

  final Handshake handshake;

  /// The address to connect to.
  final String host;
  final int port;

  /// Every channel that saw this agent, so the UI can say "seen twice, independently".
  final Set<DiscoveryMethod> methods;

  /// True only when the pin came from the pairing QR.
  final bool pinVerified;

  String get deviceId => handshake.deviceId;
  String get displayName => handshake.deviceName.isNotEmpty
      ? handshake.deviceName
      : (handshake.hostName.isNotEmpty ? handshake.hostName : host);
  String get spkiPin => handshake.spkiSha256 ?? '';
  String get displayAddress =>
      port == EsiProtocol.defaultHttpsPort ? host : '$host:$port';

  /// Stable key for de-duplication across the three discovery channels.
  String get key => deviceId.isNotEmpty ? deviceId : displayAddress;

  DiscoveredAgent merge(DiscoveredAgent other) => DiscoveredAgent(
        handshake: other.handshake.protocolVersion > 0 ? other.handshake : handshake,
        host: host,
        port: port,
        methods: <DiscoveryMethod>{...methods, ...other.methods},
        pinVerified: pinVerified || other.pinVerified,
      );

  DiscoveredAgent verified() => DiscoveredAgent(
        handshake: handshake,
        host: host,
        port: port,
        methods: methods,
        pinVerified: true,
      );
}

class DiscoveryResult {
  const DiscoveryResult(
    this.agents, {
    required this.methods,
    this.notes = const <String>[],
  });

  final List<DiscoveredAgent> agents;
  final Set<DiscoveryMethod> methods;

  /// Things that went wrong or were skipped. Rendered verbatim, because "discovery found
  /// nothing" without a reason is useless.
  final List<String> notes;

  bool get isEmpty => agents.isEmpty;
}

/// Finds agents on the LAN.
///
/// Three independent channels, because each one fails in a different place:
///
/// 1. **Subnet sweep** — a directed TCP connect to the HTTPS port across the local
///    subnets, then an unauthenticated `GET /api/v1/handshake` on whatever answers. This
///    is the channel that actually yields a usable *address*: Dart's
///    [RawDatagramSocket] cannot report a datagram's sender, so a broadcast reply can
///    identify an agent but not say where it is.
/// 2. **UDP broadcast** — `ESI_DISCOVER_V1` to `255.255.255.255` and every
///    subnet-directed broadcast address, exactly as PROTOCOL.md §9.1 specifies. Free of
///    Android runtime permissions, unlike mDNS. Yields identity and pin, merged into
///    channel 1's result by device id.
/// 3. **mDNS** — a single PTR query for `_esiremotepc._tcp.local.`, which does carry A
///    records and so can also stand alone. Best effort by design.
class LanDiscovery {
  LanDiscovery({
    this.port = EsiProtocol.defaultHttpsPort,
    this.discoveryPort = EsiProtocol.defaultDiscoveryPort,
    this.probeTimeout = const Duration(milliseconds: 400),
    this.handshakeTimeout = const Duration(milliseconds: 2500),
    this.sweepEnabled = true,
    this.mdnsEnabled = true,
  });

  final int port;
  final int discoveryPort;
  final Duration probeTimeout;
  final Duration handshakeTimeout;
  final bool sweepEnabled;
  final bool mdnsEnabled;

  /// Concurrent connects. Enough to make a /24 finish in well under a second; low enough
  /// not to trip the platform's per-app connection limits.
  static const int _sweepConcurrency = 24;

  Future<DiscoveryResult> scan({void Function(DiscoveryMethod)? onProgress}) async {
    final notes = <String>[];
    final methods = <DiscoveryMethod>{};
    final byKey = <String, DiscoveredAgent>{};

    void absorb(DiscoveredAgent agent, DiscoveryMethod method) {
      methods.add(method);
      onProgress?.call(method);
      final existing = byKey[agent.key];
      byKey[agent.key] = existing == null ? agent : existing.merge(agent);
    }

    // Channels 1 and 2 run together: the sweep supplies addresses, the broadcast supplies
    // an independently-observed identity for the same agents.
    final subnets = await _localSubnets();
    final sweep = sweepEnabled
        ? _sweep(subnets)
        : Future<DiscoveryChannelResult>.value(
            const DiscoveryChannelResult(<DiscoveredAgent>[], <String>[], null),
          );
    final broadcast = _broadcast(subnets);

    final sweepResult = await sweep;
    for (final agent in sweepResult.agents) {
      absorb(agent, DiscoveryMethod.subnetSweep);
    }
    notes.addAll(sweepResult.notes);

    final broadcastResult = await broadcast;
    for (final agent in broadcastResult.agents) {
      absorb(agent, DiscoveryMethod.udpBroadcast);
    }
    notes.addAll(broadcastResult.notes);

    if (mdnsEnabled) {
      final mdnsResult = await MdnsDiscovery(discoveryPort: discoveryPort).scan();
      for (final agent in mdnsResult.agents) {
        absorb(agent, DiscoveryMethod.mdns);
      }
      notes.addAll(mdnsResult.notes);
    }

    final agents = byKey.values.toList()
      ..sort((a, b) => a.displayName.toLowerCase().compareTo(b.displayName.toLowerCase()));

    if (agents.isEmpty) {
      notes.insert(
        0,
        'No agent answered on this network. Check that the ESI Remote PC agent is '
        'running, that this phone is on the same Wi-Fi, and that the router is not '
        'blocking traffic between devices. You can also enter the address by hand.',
      );
    }

    return DiscoveryResult(agents, methods: methods, notes: notes);
  }

  // ------------------------------------------------------------------ channel 1

  Future<DiscoveryChannelResult> _sweep(List<String> subnets) async {
    final notes = <String>[];
    if (subnets.isEmpty) {
      notes.add(
        'The phone reported no IPv4 interface, so the subnet sweep had nowhere to look. '
        'Is Wi-Fi connected?',
      );
      return const DiscoveryChannelResult(<DiscoveredAgent>[], <String>[], null);
    }

    final open = <String>[];
    for (final subnet in subnets) {
      open.addAll(await _sweepSubnet(subnet, port, notes));
    }

    if (open.isEmpty) {
      notes.add(
        'No machine on this subnet answered on port $port. If the agent runs on a '
        'non-default port, add the PC by hand.',
      );
      return DiscoveryChannelResult(const <DiscoveredAgent>[], notes, 0);
    }

    final agents = <DiscoveredAgent>[];
    for (final address in open) {
      final handshake = await _readHandshake(address);
      if (handshake == null) continue;
      agents.add(DiscoveredAgent(
        handshake: handshake,
        host: address,
        port: port,
        methods: const <DiscoveryMethod>{DiscoveryMethod.subnetSweep},
      ));
    }
    return DiscoveryChannelResult(agents, notes, open.length);
  }

  /// TCP connects across the subnet. A closed port answers with an RST in well under a
  /// millisecond on a LAN, so a /24 completes quickly; the per-address timeout only bites
  /// when a firewall silently drops the SYN, and the whole sweep is time-boxed.
  Future<List<String>> _sweepSubnet(String subnet, int targetPort, List<String> notes) async {
    final found = <String>[];
    var next = 0;

    Future<void> worker() async {
      while (true) {
        final index = next++;
        if (index >= 254) return;
        final address = Ipv4.hostIn(subnet, index + 1);
        if (address == null) continue;
        if (await _canConnect(address, targetPort)) found.add(address);
      }
    }

    try {
      await Future.wait(
        List<Future<void>>.generate(_sweepConcurrency, (_) => worker()),
      ).timeout(const Duration(seconds: 6));
    } on TimeoutException {
      // Keep whatever answered before the budget ran out; a partial sweep is still useful,
      // and saying so is better than silently reporting a short search as complete.
      notes.add(
        'The subnet scan reached its time limit after $subnet.x, so a machine on this '
        'network may have been missed. Add it by hand if you know the address.',
      );
    }
    return found;
  }

  Future<bool> _canConnect(String host, int targetPort) async {
    Socket? socket;
    try {
      socket = await Socket.connect(host, targetPort, timeout: probeTimeout);
      return true;
    } on Object {
      return false;
    } finally {
      socket?.destroy();
    }
  }

  /// `GET /api/v1/handshake`, which is unauthenticated and local-network-only.
  ///
  /// The TLS check here is trust-on-first-use and *deliberately* not a pin check: at this
  /// point there is no pin to check against, and PROTOCOL.md §2.1 already says this
  /// endpoint can be lied to. The pin it returns is displayed as unverified until the QR
  /// says otherwise.
  Future<Handshake?> _readHandshake(String host) async {
    final client = HttpClient()
      ..connectionTimeout = handshakeTimeout
      ..badCertificateCallback = (cert, host, port) => true;
    try {
      final authority = port == EsiProtocol.defaultHttpsPort ? host : '$host:$port';
      final request = await client.getUrl(Uri.parse('https://$authority${EsiProtocol.apiBase}/handshake'));
      final response = await request.close().timeout(handshakeTimeout);
      if (response.statusCode != 200) return null;
      final body = await response.transform(utf8.decoder).join().timeout(handshakeTimeout);
      final json = tryDecodeObject(body);
      if (json.isEmpty) return null;
      return Handshake.fromJson(json);
    } on Object {
      return null;
    } finally {
      client.close(force: true);
    }
  }

  // ------------------------------------------------------------------ channel 2

  /// PROTOCOL.md §9.1. Both the limited broadcast and every subnet-directed broadcast
  /// address are tried, because many access points and mobile stacks drop the former.
  Future<DiscoveryChannelResult> _broadcast(List<String> subnets) async {
    final notes = <String>[];
    final payloads = <String>[];
    RawDatagramSocket? socket;

    try {
      socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
      final open = socket;
      open.broadcastEnabled = true;

      final done = Completer<void>();
      open.listen(
        (event) {
          if (event != RawSocketEvent.read) return;
          final datagram = open.receive();
          if (datagram == null) return;
          payloads.add(_decodeLossy(datagram.data));
        },
        onError: (Object error) {
          notes.add('The discovery socket reported an error: $error');
          if (!done.isCompleted) done.complete();
        },
        cancelOnError: false,
      );

      final payload = utf8.encode(EsiProtocol.discoveryProbe);
      final targets = <({InternetAddress address, int port})>[
        (address: InternetAddress('255.255.255.255'), port: discoveryPort),
      ];
      for (final subnet in subnets) {
        targets.add((address: InternetAddress('$subnet.255'), port: discoveryPort));
      }

      for (final target in targets) {
        open.send(payload, target.address, target.port);
      }

      unawaited(Future<void>.delayed(const Duration(milliseconds: 900), () {
        if (!done.isCompleted) done.complete();
      }));
      await done.future;
    } on Object catch (e) {
      notes.add('UDP broadcast discovery failed: $e');
    } finally {
      socket?.close();
    }

    final seen = <String>{};
    final agents = <DiscoveredAgent>[];
    for (final payload in payloads) {
      final json = tryDecodeObject(payload);
      if (json.isEmpty) continue;
      final handshake = Handshake.fromJson(json);
      if (handshake.deviceId.isEmpty) continue;
      if (!seen.add(handshake.deviceId)) continue;
      // The address is unknown at this point — see the class doc. The sweep channel fills
      // it in, and a broadcast-only hit is reported with the address left blank rather
      // than guessed.
      agents.add(DiscoveredAgent(
        handshake: handshake,
        host: '',
        port: handshake.httpsPort > 0 ? handshake.httpsPort : EsiProtocol.defaultHttpsPort,
        methods: const <DiscoveryMethod>{DiscoveryMethod.udpBroadcast},
      ));
    }

    return DiscoveryChannelResult(agents, notes, null);
  }

  static String _decodeLossy(List<int> bytes) =>
      utf8.decode(bytes.where((b) => b != 0).toList(growable: false), allowMalformed: true);

  // ------------------------------------------------------------------ addressing

  /// The /24 prefixes of the phone's non-loopback IPv4 interfaces.
  ///
  /// Capped at a /24 per interface: a /8 would mean sixteen million connects, and no home
  /// or office LAN is that large, so the cap costs nothing in practice and bounds the
  /// worst case.
  static Future<List<String>> _localSubnets() async {
    final out = <String>{};
    try {
      final interfaces = await NetworkInterface.list(
        includeLoopback: false,
        includeLinkLocal: false,
        type: InternetAddressType.IPv4,
      );
      for (final interface in interfaces) {
        for (final address in interface.addresses) {
          final prefix = Ipv4.prefixOf(address.address);
          if (prefix != null) out.add(prefix);
        }
      }
    } on SocketException {
      // Interface enumeration is best effort; the limited broadcast may still work.
    }
    return out.toList(growable: false);
  }
}

/// What one discovery channel found.
class DiscoveryChannelResult {
  const DiscoveryChannelResult(this.agents, this.notes, this.openHostCount);
  final List<DiscoveredAgent> agents;
  final List<String> notes;

  /// Non-null for the sweep: how many hosts answered on the API port. Only used for the
  /// "scanned N machines" line in the UI.
  final int? openHostCount;
}

/// A best-effort mDNS query for `_esiremotepc._tcp.local.` (PROTOCOL.md §9.2).
///
/// Hand-rolled over [RawDatagramSocket] rather than `NsdManager`, which from API 33
/// needs `NEARBY_WIFI_DEVICES` and from Android 17 needs `ACCESS_LOCAL_NETWORK`. Unlike
/// broadcast, mDNS *does* carry the A record, so a reply here yields a usable address on
/// its own. Failures are non-fatal and reported as notes, because the other two channels
/// already cover discovery.
class MdnsDiscovery {
  MdnsDiscovery({
    this.discoveryPort = EsiProtocol.defaultDiscoveryPort,
    this.serviceType = EsiProtocol.mdnsServiceType,
    this.timeout = const Duration(milliseconds: 1800),
  });

  final int discoveryPort;
  final String serviceType;
  final Duration timeout;

  Future<DiscoveryChannelResult> scan() async {
    final notes = <String>[];
    final agents = <DiscoveredAgent>[];
    RawDatagramSocket? socket;
    try {
      socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
      final open = socket;
      open.broadcastEnabled = true;
      final responses = <Uint8List>[];
      final done = Completer<void>();

      open.listen(
        (event) {
          if (event != RawSocketEvent.read) return;
          final datagram = open.receive();
          if (datagram != null) responses.add(Uint8List.fromList(datagram.data));
        },
        onError: (Object error) {
          notes.add('The mDNS socket reported an error: $error');
          if (!done.isCompleted) done.complete();
        },
        cancelOnError: false,
      );

      open.send(_buildQuery(serviceType), InternetAddress('224.0.0.251'), 5353);
      unawaited(Future<void>.delayed(timeout, () {
        if (!done.isCompleted) done.complete();
      }));
      await done.future;

      for (final response in responses) {
        final parsed = _parseAnswers(response);
        for (final answer in parsed) {
          if (answer.address == null) continue;
          agents.add(DiscoveredAgent(
            handshake: Handshake.fromJson(<String, dynamic>{
              'protocolVersion': EsiProtocol.version,
              'deviceId': answer.deviceId ?? '',
              'deviceName': answer.name ?? answer.hostName ?? '',
              'hostName': answer.hostName ?? answer.name ?? '',
              'httpsPort': answer.port,
              'discoveryPort': discoveryPort,
              'serverTime': DateTime.now().millisecondsSinceEpoch ~/ 1000,
              'pairingRequired': true,
              'pairingOpen': true,
              'maxPairedDevices': 8,
              'features': <String, bool>{},
            }),
            host: answer.address!.address,
            port: answer.port,
            methods: const <DiscoveryMethod>{DiscoveryMethod.mdns},
          ));
        }
      }

      if (agents.isEmpty) {
        notes.add(
          'mDNS returned no answer. Networks that filter multicast are common, and this '
          'does not affect the other discovery channels.',
        );
      }
    } on Object catch (e) {
      notes.add('mDNS discovery failed: $e');
    } finally {
      socket?.close();
    }

    return DiscoveryChannelResult(agents, notes, null);
  }

  /// One PTR question. mDNS requires transaction ID 0 and the "standard query" flags.
  static List<int> _buildQuery(String serviceType) {
    final bytes = <int>[
      0x00, 0x00, // ID = 0
      0x00, 0x00, // standard query, no recursion
      0x00, 0x01, // QDCOUNT
      0x00, 0x00, // ANCOUNT
      0x00, 0x00, // NSCOUNT
      0x00, 0x00, // ARCOUNT
    ];
    for (final label in serviceType.split('.').where((l) => l.isNotEmpty)) {
      bytes.addAll(ascii.encode(label));
      bytes.add(0);
    }
    bytes.addAll(<int>[0x00, 0x0c, 0x00, 0x04]); // QTYPE = PTR, QCLASS = IN
    return bytes;
  }

  /// Pulls the A, SRV and TXT rdata out of an mDNS response.
  ///
  /// Deliberately partial: label compression is skipped rather than followed, and a
  /// malformed packet is dropped rather than thrown on. Full DNS parsing is not worth the
  /// surface area for a fallback path.
  static List<_MdnsAnswer> _parseAnswers(Uint8List message) {
    final answers = <_MdnsAnswer>[];
    try {
      if (message.length < 12) return answers;
      final view = ByteData.sublistView(message);
      final questions = view.getUint16(4);
      final records = view.getUint16(6);
      if (records == 0) return answers;

      var offset = 12;
      for (var i = 0; i < questions; i++) {
        offset = _skipName(message, offset) + 4;
        if (offset > message.length) return answers;
      }

      String? deviceId;
      String? deviceName;
      String? hostName;
      InternetAddress? address;
      int? servicePort;

      for (var i = 0; i < records; i++) {
        final nameEnd = _skipName(message, offset);
        if (nameEnd + 10 > message.length) break;
        final type = view.getUint16(nameEnd);
        final rdLength = view.getUint16(nameEnd + 8);
        final rdataStart = nameEnd + 10;
        if (rdataStart + rdLength > message.length) break;

        switch (type) {
          case 1: // A
            if (rdLength == 4) {
              address = Ipv4.fromBytes(message.sublist(rdataStart, rdataStart + 4));
            }
          case 12: // PTR -> the instance name, "<DeviceName>._esiremotepc._tcp.local."
            deviceName ??= _firstLabel(message, rdataStart, rdataStart + rdLength);
          case 16: // TXT
            final text = _parseTxt(message.sublist(rdataStart, rdataStart + rdLength));
            deviceId = text['id'] ?? deviceId;
            deviceName = text['name'] ?? deviceName;
          case 33: // SRV
            if (rdLength >= 6) {
              servicePort = view.getUint16(rdataStart + 4);
              hostName = _firstLabel(message, rdataStart + 6, rdataStart + rdLength);
            }
          default:
            break;
        }
        offset = rdataStart + rdLength;
      }

      if (address != null) {
        answers.add(_MdnsAnswer(
          address: address,
          port: servicePort ?? EsiProtocol.defaultHttpsPort,
          deviceId: deviceId,
          name: deviceName,
          hostName: hostName,
        ));
      }
    } on Object {
      return answers;
    }
    return answers;
  }

  /// The offset just past the (possibly compressed) name starting at [offset].
  static int _skipName(Uint8List message, int offset) {
    var pos = offset;
    while (pos < message.length) {
      final length = message[pos];
      if (length == 0) return pos + 1;
      if (length & 0xc0 == 0xc0) return pos + 2;
      if (length > 63) return message.length;
      pos += 1 + length;
    }
    return message.length;
  }

  static String? _firstLabel(Uint8List message, int start, int end) {
    if (start >= end || start >= message.length) return null;
    final length = message[start];
    if (length == 0 || length > 63 || start + 1 + length > end) return null;
    return ascii.decode(message.sublist(start + 1, start + 1 + length), allowInvalid: true);
  }

  static Map<String, String> _parseTxt(List<int> rdata) {
    final out = <String, String>{};
    var i = 0;
    while (i < rdata.length) {
      final len = rdata[i];
      i += 1;
      if (len == 0) continue;
      if (i + len > rdata.length) break;
      final entry = utf8.decode(rdata.sublist(i, i + len), allowMalformed: true);
      i += len;
      final eq = entry.indexOf('=');
      if (eq > 0) {
        out[entry.substring(0, eq)] = entry.substring(eq + 1);
      }
    }
    return out;
  }
}

class _MdnsAnswer {
  const _MdnsAnswer({
    required this.address,
    required this.port,
    this.deviceId,
    this.name,
    this.hostName,
  });

  final InternetAddress? address;
  final int port;
  final String? deviceId;
  final String? name;
  final String? hostName;
}

/// `base64(SHA-256(SPKI))` of a certificate, for the settings screen's pin display.
String? spkiPinOf(X509Certificate certificate) => pinForCertificate(certificate);

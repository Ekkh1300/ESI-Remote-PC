import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../data/machine_store.dart';
import '../../data/pairing_service.dart';
import '../../net/esi_api.dart';
import '../../net/discovery.dart';
import '../../net/wol.dart';
import '../../protocol/esi_crypto.dart';
import '../../protocol/models.dart';
import '../../protocol/pairing_qr.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/connection.dart';
import '../../state/discovery.dart';
import '../../state/pairing_flow.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

/// The first screen: find a PC, or point at one.
///
/// Three routes in, because three things actually happen in the real world: the PC is on
/// the same network and answers a search, the user knows its address, or the PC is showing
/// a QR code on its screen. Each one ends in the same place — a confirmed endpoint with a
/// pin — and only then can pairing start.
class OnboardingScreen extends ConsumerStatefulWidget {
  const OnboardingScreen({super.key});

  @override
  ConsumerState<OnboardingScreen> createState() => _OnboardingScreenState();
}

class _OnboardingScreenState extends ConsumerState<OnboardingScreen> {
  final TextEditingController _host = TextEditingController();
  final TextEditingController _port =
      TextEditingController(text: '${EsiProtocol.defaultHttpsPort}');
  final TextEditingController _pin = TextEditingController();
  final TextEditingController _secret = TextEditingController();
  final TextEditingController _mac = TextEditingController();

  String? _note;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    // Start searching straight away: the common case is a PC on the same network, and
    // making the user press a button first is a wasted step.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      ref.read(discoveryProvider.notifier).scan();
    });
  }

  @override
  void dispose() {
    _host.dispose();
    _port.dispose();
    _pin.dispose();
    _secret.dispose();
    _mac.dispose();
    super.dispose();
  }

  int get _portValue {
    final value = int.tryParse(_port.text.trim());
    return (value != null && value > 0 && value < 65536)
        ? value
        : EsiProtocol.defaultHttpsPort;
  }

  bool get _canPair =>
      _host.text.trim().isNotEmpty &&
      _pin.text.trim().isNotEmpty &&
      _decodeSecret(_secret.text) != null;

  static List<int>? _decodeSecret(String value) {
    final trimmed = value.trim();
    if (trimmed.isEmpty) return null;
    try {
      final bytes = EsiCrypto.fromBase64Url(trimmed);
      return bytes.length == EsiProtocol.secretLength ? bytes : null;
    } on FormatException {
      return null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final machines = ref.watch(machineListProvider);
    return Scaffold(
      body: DecoratedBox(
        decoration: const BoxDecoration(
          gradient: RadialGradient(
            center: Alignment(-0.6, -0.9),
            radius: 1.4,
            colors: <Color>[Color(0xFF12203A), EsiPalette.void_],
          ),
        ),
        child: SafeArea(
          child: Column(
            children: <Widget>[
              const _Masthead(),
              Expanded(
                child: ListView(
                  padding: const EdgeInsets.fromLTRB(
                    EsiSpace.lg,
                    EsiSpace.sm,
                    EsiSpace.lg,
                    EsiSpace.huge,
                  ),
                  children: <Widget>[
                    if (machines.isNotEmpty) ...<Widget>[
                      _PairedMachines(machines: machines),
                      const SizedBox(height: EsiSpace.md),
                    ],
                    const _DiscoveryPanel(),
                    const SizedBox(height: EsiSpace.md),
                    _ManualPanel(
                      host: _host,
                      port: _port,
                      pin: _pin,
                      secret: _secret,
                      mac: _mac,
                      note: _note,
                      busy: _busy,
                      canPair: _canPair,
                      onChanged: () => setState(() {}),
                      onProbe: _probe,
                      onPair: _pair,
                    ),
                    const SizedBox(height: EsiSpace.md),
                    _QrPanel(owner: this),
                    const SizedBox(height: EsiSpace.lg),
                    const _HowPairingWorks(),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _probe() async {
    setState(() => _busy = true);
    final handshake =
        await ref.read(discoveryProvider.notifier).probe(_host.text.trim(), _portValue);
    if (!mounted) return;
    setState(() => _busy = false);
    if (handshake == null) return;
    // Show what the PC claims, but never fill the pin field from it: PROTOCOL.md §2.1 says
    // this value could have been written by anything on the wire.
    await showDialog<void>(
      context: context,
      builder: (context) => _HandshakeDialog(handshake: handshake, address: _host.text.trim()),
    );
  }

  Future<void> _pair() async {
    final host = _host.text.trim();
    final pin = _pin.text.trim();
    final secret = _decodeSecret(_secret.text);
    if (host.isEmpty || pin.isEmpty || secret == null) {
      _warn(
        'Pairing needs the address, the certificate pin and the pairing secret. The '
        'secret only exists on the PC — on its pairing screen or in its QR code.',
      );
      return;
    }
    final ok = await _confirm(
      host: host,
      port: _portValue,
      pin: pin,
    );
    if (!ok || !mounted) return;
    await _start(
      endpoint: EsiEndpoint(host: host, port: _portValue, pins: <String>[pin]),
      secret: secret,
      mac: _mac.text.trim(),
      source: PairingSource.manual,
    );
  }

  void _warn(String message) {
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message), duration: const Duration(seconds: 6)));
  }

  /// One confirmation before anything is sent, so a mistyped address does not burn one of
  /// the agent's five pairing attempts per hour.
  Future<bool> _confirm({
    required String host,
    required int port,
    required String pin,
  }) =>
      EsiConfirmDialog.show(
        context,
        title: 'Pair with this PC?',
        body: 'The phone will ask the PC to confirm, and you will get a six-digit code to '
            'match against the prompt on the PC screen. Nothing is paired until it is '
            'approved there.',
        confirmLabel: 'Start pairing',
        details: <Widget>[
          EsiDetailRow(
            label: 'Address',
            value: port == EsiProtocol.defaultHttpsPort ? host : '$host:$port',
          ),
          EsiDetailRow(label: 'Pin', value: formatBytesBase64(pin, head: 16, tail: 10)),
        ],
      );

  Future<void> _start({
    required EsiEndpoint endpoint,
    required List<int> secret,
    required String mac,
    required PairingSource source,
  }) async {
    setState(() => _busy = true);
    if (!mounted) return;
    await context.pushNamed<void>(
      '/pairing',
      extra: PairingRequest(
        endpoint: endpoint,
        provisioningSecret: Uint8List.fromList(secret),
        source: source,
        macAddress: mac.isEmpty ? null : WakeOnLan.normaliseMac(mac),
      ),
    );
    if (mounted) setState(() => _busy = false);
  }

  /// Called by the scan and paste buttons once they have a raw code. Public because the
  /// QR panel is a sibling widget, not a child of this state.
  void handleQrPayload(String raw) {
    final result = PairingQr.parse(raw);
    if (!result.isSuccess) {
      _warn(result.error ?? 'That QR code could not be read.');
      return;
    }
    final payload = result.payload!;
    if (payload.hasAddress) _host.text = payload.host;
    _port.text = '${payload.port}';
    if (payload.hasPin) _pin.text = payload.spkiPin;
    if (payload.provisioningSecret.isNotEmpty) {
      _secret.text = payload.provisioningSecret;
    }
    if (payload.macAddress != null) _mac.text = payload.macAddress!;
    setState(() {
      _note = payload.missingFields.isEmpty
          ? 'Read from the QR code. Check the address and pin against the PC, then pair.'
          : 'The QR code did not include ${payload.missingFields.join(' or ')}. '
              'Fill in the rest below.';
    });
    _warn(
      payload.missingFields.isEmpty
          ? 'Pairing details read from the QR code.'
          : 'Read the QR code, but it was missing ${payload.missingFields.join(' and ')}.',
    );
  }
}

/// The app's wordmark and one sentence of orientation.
class _Masthead extends StatelessWidget {
  const _Masthead();

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.xl, EsiSpace.lg, EsiSpace.lg),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Row(
              children: <Widget>[
                Container(
                  width: 3,
                  height: 30,
                  decoration: BoxDecoration(
                    gradient: const LinearGradient(
                      begin: Alignment.topCenter,
                      end: Alignment.bottomCenter,
                      colors: <Color>[EsiPalette.signal, EsiPalette.lock],
                    ),
                    borderRadius: EsiRadius.chip,
                  ),
                ),
                const SizedBox(width: EsiSpace.md),
                const Text('ESI Remote PC', style: EsiType.screenTitle),
              ],
            ),
            const SizedBox(height: EsiSpace.md),
            Text(
              'Control a Windows machine from this phone, on the network it is already on.',
              style: EsiType.body.copyWith(color: EsiPalette.dim),
            ),
          ],
        ),
      );
}

class _DiscoveryPanel extends ConsumerWidget {
  const _DiscoveryPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(discoveryProvider);
    final notifier = ref.read(discoveryProvider.notifier);

    return EsiPanel(
      title: 'On this network',
      severity: state.agents.isEmpty ? Severity.unknown : Severity.nominal,
      tick: false,
      trailing: EsiIconAction(
        icon: Icons.radar,
        tooltip: 'Search again',
        colour: EsiPalette.signal,
        onPressed: state.scanning ? null : notifier.scan,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          if (state.scanning)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: EsiSpace.lg),
              child: Row(
                children: <Widget>[
                  SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                  SizedBox(width: EsiSpace.md),
                  Expanded(child: Text('Searching…', style: EsiType.body)),
                ],
              ),
            )
          else if (state.agents.isEmpty)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: EsiSpace.sm),
              child: Text(
                'Nothing answered yet. An unpaired PC will answer, but only if the agent '
                'is running and the router lets devices talk to each other.',
                style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
              ),
            )
          else
            for (final agent in state.agents) _DiscoveredAgentRow(agent: agent),
          if (state.channelsSeen.isNotEmpty) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            Wrap(
              spacing: EsiSpace.sm,
              runSpacing: EsiSpace.xs,
              children: <Widget>[
                for (final channel in state.channelsSeen)
                  EsiTag(label: channel.label, colour: EsiPalette.dim),
              ],
            ),
          ],
          if (state.notes.isNotEmpty) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            for (final note in state.notes)
              Padding(
                padding: const EdgeInsets.only(bottom: EsiSpace.xs),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    const Padding(
                      padding: EdgeInsets.only(top: 2),
                      child: Icon(Icons.info_outline, size: 12, color: EsiPalette.faint),
                    ),
                    const SizedBox(width: EsiSpace.sm),
                    Expanded(
                      child: Text(
                        note,
                        style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 11.5),
                      ),
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

class _DiscoveredAgentRow extends ConsumerWidget {
  const _DiscoveredAgentRow({required this.agent});

  final DiscoveredAgent agent;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final usable = agent.host.isNotEmpty && agent.spkiPin.isNotEmpty;

    return Padding(
      padding: const EdgeInsets.only(bottom: EsiSpace.md),
      child: EsiSurface(
        tint: EsiPalette.deck,
        opacity: 0.5,
        padding: const EdgeInsets.all(EsiSpace.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            Row(
              children: <Widget>[
                Expanded(
                  child: Text(
                    agent.displayName,
                    style: EsiType.subtitle.copyWith(fontSize: 14),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                // The pin from an unauthenticated handshake is a hint, and the UI says so
                // rather than presenting it as verified.
                const EsiTag(
                  label: 'pin unverified',
                  colour: EsiPalette.lock,
                  icon: Icons.help_outline,
                ),
              ],
            ),
            const SizedBox(height: EsiSpace.xs),
            Text(
              agent.host.isEmpty
                  ? 'Answered a broadcast, but its address could not be determined. Add '
                      'it by hand.'
                  : 'agent ${agent.handshake.agentVersion} · '
                      'protocol v${agent.handshake.protocolVersion} · '
                      '${agent.methods.length} method'
                      '${agent.methods.length == 1 ? '' : 's'}',
              style: EsiType.mono.copyWith(fontSize: 10.5),
            ),
            const SizedBox(height: EsiSpace.sm),
            Row(
              children: <Widget>[
                if (agent.host.isNotEmpty) EsiTag(label: agent.displayAddress, colour: EsiPalette.dim),
                const Spacer(),
                FilledButton(
                  style: FilledButton.styleFrom(
                    minimumSize: const Size(0, 34),
                    padding: const EdgeInsets.symmetric(horizontal: EsiSpace.md),
                    backgroundColor: usable ? EsiPalette.signal : EsiPalette.edge,
                    foregroundColor: usable ? EsiPalette.void_ : EsiPalette.faint,
                  ),
                  onPressed: usable ? () => _use(context, ref) : null,
                  child: const Text('Use this PC'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _use(BuildContext context, WidgetRef ref) async {
    await ref.read(discoveryProvider.notifier).probe(agent.host, agent.port);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(
            'Check the address above, then add the pairing secret from the PC\'s pairing '
            'screen — or scan its QR code, which carries the pin '
            '${formatBytesBase64(agent.spkiPin, head: 10, tail: 6)} for you.',
          ),
          duration: const Duration(seconds: 9),
        ),
      );
  }
}

class _ManualPanel extends StatelessWidget {
  const _ManualPanel({
    required this.host,
    required this.port,
    required this.pin,
    required this.secret,
    required this.mac,
    required this.note,
    required this.busy,
    required this.canPair,
    required this.onChanged,
    required this.onProbe,
    required this.onPair,
  });

  final TextEditingController host;
  final TextEditingController port;
  final TextEditingController pin;
  final TextEditingController secret;
  final TextEditingController mac;
  final String? note;
  final bool busy;
  final bool canPair;
  final VoidCallback onChanged;
  final Future<void> Function() onProbe;
  final Future<void> Function() onPair;

  @override
  Widget build(BuildContext context) => EsiPanel(
        title: 'Add a PC by hand',
        tick: false,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            if (note != null) ...<Widget>[
              Text(note!, style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5)),
              const SizedBox(height: EsiSpace.md),
            ],
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Expanded(
                  flex: 3,
                  child: TextField(
                    controller: host,
                    onChanged: (_) => onChanged(),
                    autocorrect: false,
                    keyboardType: TextInputType.url,
                    style: EsiType.mono.copyWith(color: EsiPalette.ink, fontSize: 13),
                    decoration: const InputDecoration(
                      labelText: 'Address',
                      hintText: '192.168.1.42 or PC-NAME.local',
                    ),
                  ),
                ),
                const SizedBox(width: EsiSpace.sm),
                Expanded(
                  child: TextField(
                    controller: port,
                    onChanged: (_) => onChanged(),
                    keyboardType: TextInputType.number,
                    style: EsiType.mono.copyWith(color: EsiPalette.ink, fontSize: 13),
                    decoration: const InputDecoration(labelText: 'Port'),
                  ),
                ),
              ],
            ),
            const SizedBox(height: EsiSpace.md),
            TextField(
              controller: pin,
              onChanged: (_) => onChanged(),
              autocorrect: false,
              style: EsiType.mono.copyWith(color: EsiPalette.ink, fontSize: 12),
              decoration: const InputDecoration(
                labelText: 'Certificate pin',
                hintText: 'base64 SHA-256 of the SPKI, from the PC\'s pairing screen',
              ),
            ),
            const SizedBox(height: EsiSpace.md),
            TextField(
              controller: secret,
              onChanged: (_) => onChanged(),
              autocorrect: false,
              obscureText: true,
              style: EsiType.mono.copyWith(color: EsiPalette.ink, fontSize: 12),
              decoration: const InputDecoration(
                labelText: 'Pairing secret',
                hintText: 'The one-time secret shown with the pairing code',
              ),
            ),
            const SizedBox(height: EsiSpace.md),
            TextField(
              controller: mac,
              autocorrect: false,
              style: EsiType.mono.copyWith(color: EsiPalette.ink, fontSize: 12),
              decoration: const InputDecoration(
                labelText: 'MAC address (optional)',
                hintText: 'AA:BB:CC:DD:EE:FF — used to wake the PC',
              ),
            ),
            const SizedBox(height: EsiSpace.lg),
            Row(
              children: <Widget>[
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: busy ? null : onProbe,
                    icon: const Icon(Icons.wifi_tethering, size: 17),
                    label: const Text('Check address'),
                  ),
                ),
                const SizedBox(width: EsiSpace.sm),
                Expanded(
                  child: FilledButton.icon(
                    onPressed: canPair && !busy ? onPair : null,
                    icon: const Icon(Icons.link, size: 17),
                    label: const Text('Pair'),
                  ),
                ),
              ],
            ),
          ],
        ),
      );
}

class _QrPanel extends ConsumerStatefulWidget {
  const _QrPanel({required this.owner});

  final _OnboardingScreenState owner;

  @override
  ConsumerState<_QrPanel> createState() => _QrPanelState();
}

class _QrPanelState extends ConsumerState<_QrPanel> {
  bool _pasting = false;

  @override
  Widget build(BuildContext context) {
    return EsiPanel(
      title: 'Pairing code',
      tick: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text(
            'On the PC, open the ESI Remote PC tray menu and choose "Show pairing QR". The '
            'code carries the address, the certificate pin and the one-time secret, which '
            'is the only way to pair without typing them.',
            style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
          ),
          const SizedBox(height: EsiSpace.lg),
          Row(
            children: <Widget>[
              Expanded(
                child: FilledButton.icon(
                  onPressed: () async {
                    final raw = await Navigator.of(context).push<String>(
                      MaterialPageRoute<String>(builder: (_) => const QrScannerScreen()),
                    );
                    if (raw != null) widget.owner.handleQrPayload(raw);
                  },
                  icon: const Icon(Icons.qr_code_scanner, size: 18),
                  label: const Text('Scan'),
                ),
              ),
              const SizedBox(width: EsiSpace.sm),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _pasting ? null : _paste,
                  icon: const Icon(Icons.content_paste, size: 17),
                  label: const Text('Paste'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Future<void> _paste() async {
    setState(() => _pasting = true);
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    if (!mounted) return;
    setState(() => _pasting = false);
    final raw = data?.text?.trim();
    if (raw == null || raw.isEmpty) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(
            content: Text('The clipboard is empty. Copy the pairing code on the PC first.'),
          ),
        );
      return;
    }
    widget.owner.handleQrPayload(raw);
  }
}

/// The camera scanner. Pops with the first code that parses as a pairing payload, so an
/// unrelated QR in the room does not dump a JSON error on the user.
class QrScannerScreen extends ConsumerStatefulWidget {
  const QrScannerScreen({super.key});

  @override
  ConsumerState<QrScannerScreen> createState() => _QrScannerScreenState();
}

class _QrScannerScreenState extends ConsumerState<QrScannerScreen> {
  late final MobileScannerController _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
    formats: const <BarcodeFormat>[BarcodeFormat.qrCode],
  );
  bool _handled = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        backgroundColor: EsiPalette.void_,
        body: Stack(
          fit: StackFit.expand,
          children: <Widget>[
            MobileScanner(controller: _controller, onDetect: _onDetect),
            const _ScannerFrame(),
            SafeArea(
              child: Column(
                children: <Widget>[
                  Padding(
                    padding: const EdgeInsets.all(EsiSpace.md),
                    child: Row(
                      children: <Widget>[
                        EsiIconAction(
                          icon: Icons.close,
                          tooltip: 'Close the scanner',
                          onPressed: () => Navigator.of(context).pop(),
                        ),
                        const SizedBox(width: EsiSpace.md),
                        const Expanded(
                          child: Text('Point at the pairing QR', style: EsiType.subtitle),
                        ),
                        EsiIconAction(
                          icon: Icons.flashlight_on_outlined,
                          tooltip: 'Toggle the torch',
                          onPressed: _controller.toggleTorch,
                        ),
                      ],
                    ),
                  ),
                  const Spacer(),
                  Padding(
                    padding: const EdgeInsets.all(EsiSpace.lg),
                    child: Text(
                      'The code is on the PC\'s screen, under the tray icon. A phone camera '
                      'cannot read a screen that is asleep — wake it first.',
                      textAlign: TextAlign.center,
                      style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      );

  void _onDetect(BarcodeCapture capture) {
    if (_handled) return;
    for (final barcode in capture.barcodes) {
      final raw = barcode.rawValue;
      if (raw == null) continue;
      if (!PairingQr.parse(raw).isSuccess) continue;
      _handled = true;
      HapticFeedback.mediumImpact();
      Navigator.of(context).pop(raw);
      return;
    }
  }
}

class _ScannerFrame extends StatelessWidget {
  const _ScannerFrame();

  @override
  Widget build(BuildContext context) => IgnorePointer(
        child: Center(
          child: Container(
            width: 240,
            height: 240,
            decoration: BoxDecoration(
              border: Border.all(color: EsiPalette.signal.withValues(alpha: 0.6), width: 2),
              borderRadius: const BorderRadius.all(Radius.circular(18)),
            ),
          ),
        ),
      );
}

/// Shows what the PC claims about itself, and makes the user check it against the PC.
class _HandshakeDialog extends StatelessWidget {
  const _HandshakeDialog({required this.handshake, required this.address});

  final Handshake handshake;
  final String address;

  @override
  Widget build(BuildContext context) => AlertDialog(
        backgroundColor: EsiPalette.deck,
        shape: const RoundedRectangleBorder(borderRadius: EsiRadius.dialog),
        title: const Text('What the PC reports'),
        content: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 400),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              EsiDetailRow(label: 'Address', value: address),
              EsiDetailRow(label: 'Device', value: handshake.deviceName),
              EsiDetailRow(label: 'Device id', value: formatDeviceId(handshake.deviceId)),
              EsiDetailRow(label: 'Agent', value: 'v${handshake.agentVersion}'),
              EsiDetailRow(
                label: 'Protocol',
                value: 'v${handshake.protocolVersion}',
                valueColour: handshake.isCompatible ? EsiPalette.signal : EsiPalette.alarm,
              ),
              EsiDetailRow(
                label: 'Paired devices',
                value: '${handshake.pairedDeviceCount} of ${handshake.maxPairedDevices}',
              ),
              EsiDetailRow(
                label: 'Pairing open',
                value: handshake.pairingOpen ? 'yes' : 'no',
                valueColour: handshake.pairingOpen ? null : EsiPalette.flare,
              ),
              EsiDetailRow(
                label: 'Claims pin',
                value: formatBytesBase64(handshake.spkiSha256, head: 14, tail: 8),
                valueColour: EsiPalette.lock,
              ),
              const SizedBox(height: EsiSpace.md),
              Text(
                'The pin above came from an unauthenticated request, so it proves nothing '
                'on its own. Compare it with the one on the PC\'s pairing screen, or scan '
                'the QR code.',
                style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12),
              ),
            ],
          ),
        ),
        actions: <Widget>[
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Close'),
          ),
        ],
      );
}

class _PairedMachines extends ConsumerWidget {
  const _PairedMachines({required this.machines});

  final List<PairedMachine> machines;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final active = ref.watch(activeMachineProvider);
    return EsiPanel(
      title: 'Paired PCs',
      severity: machines.isEmpty ? Severity.unknown : Severity.nominal,
      tick: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          for (final machine in machines)
            Padding(
              padding: const EdgeInsets.only(bottom: EsiSpace.sm),
              child: Row(
                children: <Widget>[
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        Text(machine.displayName, style: EsiType.subtitle.copyWith(fontSize: 14)),
                        Text(
                          '${machine.displayAddress} · '
                          '${machine.isPaired ? 'paired' : 'not paired'} · key ${machine.keyId}',
                          style: EsiType.mono.copyWith(fontSize: 10.5),
                        ),
                      ],
                    ),
                  ),
                  if (machine.isPaired)
                    OutlinedButton(
                      style: OutlinedButton.styleFrom(minimumSize: const Size(0, 34)),
                      onPressed: () async {
                        await ref
                            .read(activeDeviceIdProvider.notifier)
                            .select(machine.deviceId);
                        if (context.mounted) context.go('/dashboard');
                      },
                      child: Text(machine.deviceId == active?.deviceId ? 'Open' : 'Select'),
                    )
                  else
                    const EsiTag(label: 'incomplete', colour: EsiPalette.flare),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

class _HowPairingWorks extends StatelessWidget {
  const _HowPairingWorks();

  @override
  Widget build(BuildContext context) => EsiSurface(
        tint: EsiPalette.deck,
        opacity: 0.4,
        padding: const EdgeInsets.all(EsiSpace.lg),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            Text('What happens when you pair', style: EsiType.subtitle.copyWith(fontSize: 14)),
            const SizedBox(height: EsiSpace.md),
            for (final step in const <String>[
              'This phone asks the PC for a pairing attempt and gets a six-digit code.',
              'The PC shows that code. Nothing is paired until someone approves it there.',
              'The PC encrypts this phone\'s credentials to a one-time secret, so another '
                  'device on the same network that answers the prompt gets nothing.',
              'The credentials are released once. If that step fails, start over on the PC.',
            ]) ...<Widget>[
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Container(
                    margin: const EdgeInsets.only(top: 6),
                    width: 4,
                    height: 4,
                    decoration: const BoxDecoration(
                      color: EsiPalette.signal,
                      shape: BoxShape.circle,
                    ),
                  ),
                  const SizedBox(width: EsiSpace.md),
                  Expanded(
                    child: Text(
                      step,
                      style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: EsiSpace.sm),
            ],
          ],
        ),
      );
}

/// The screen that runs the pairing handshake and stores the result.
class PairingScreen extends ConsumerStatefulWidget {
  const PairingScreen({super.key});

  @override
  ConsumerState<PairingScreen> createState() => _PairingScreenState();
}

class _PairingScreenState extends ConsumerState<PairingScreen> {
  PairingRequest? _request;
  bool _started = false;
  bool _stored = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_started) return;
    final extra = GoRouterState.of(context).extra;
    if (extra is! PairingRequest) return;
    _request = extra;
    _started = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      ref.read(pairingFlowProvider.notifier).start(extra);
    });
  }

  @override
  Widget build(BuildContext context) {
    final flow = ref.watch(pairingFlowProvider);
    final progress = flow.progress;
    final request = _request;

    if (request == null) {
      return Scaffold(
        body: EsiEmptyState(
          icon: Icons.help_outline,
          title: 'Nothing to pair',
          body: 'This screen needs a PC address to work with. Go back and find one.',
          action: FilledButton(
            onPressed: () => context.go('/onboarding'),
            child: const Text('Back'),
          ),
        ),
      );
    }

    return Scaffold(
      body: DecoratedBox(
        decoration: const BoxDecoration(
          gradient: RadialGradient(
            center: Alignment(0, -0.8),
            radius: 1.3,
            colors: <Color>[Color(0xFF14243D), EsiPalette.void_],
          ),
        ),
        child: SafeArea(
          child: Padding(
            padding: const EdgeInsets.all(EsiSpace.lg),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: <Widget>[
                const SizedBox(height: EsiSpace.lg),
                Text('Pairing', style: EsiType.screenTitle),
                const SizedBox(height: EsiSpace.xs),
                Text(
                  request.endpoint.displayAddress,
                  style: EsiType.mono.copyWith(color: EsiPalette.dim),
                ),
                const SizedBox(height: EsiSpace.xl),
                Expanded(child: _body(context, flow, progress, request)),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _body(
    BuildContext context,
    PairingFlowState flow,
    PairingProgress? progress,
    PairingRequest request,
  ) {
    switch (progress?.phase) {
      case PairingPhase.requesting:
      case PairingPhase.awaitingApproval:
      case PairingPhase.decrypting:
        return _Waiting(
          progress: progress!,
          request: request,
          onCancel: () => _confirmCancel(context),
        );
      case PairingPhase.done:
        return _Done(
          progress: progress!,
          request: request,
          onOpen: _finishAndOpen,
        );
      case PairingPhase.cancelled:
      case PairingPhase.failed:
        return _Failed(progress: progress!, onRetry: () => context.go('/onboarding'));
      case null:
        return const EsiLoading(label: 'Starting the handshake…');
    }
  }

  Future<void> _confirmCancel(BuildContext context) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Stop pairing?',
      body: 'The code on the PC stays pending until it expires on its own. Nothing is '
          'paired and nothing is changed on the PC.',
      confirmLabel: 'Stop',
      destructive: true,
    );
    if (!ok) return;
    ref.read(pairingFlowProvider.notifier).cancel();
  }

  Future<void> _finishAndOpen() async {
    if (_stored) return;
    final flow = ref.read(pairingFlowProvider);
    final progress = flow.progress;
    if (progress is! PairingOutcome) return;
    final request = _request;
    if (request == null) return;
    _stored = true;
    await completePairing(
      ref: ref,
      request: request,
      deviceId: progress.deviceId,
      masterSecret: progress.masterSecret,
      keyId: progress.keyId,
    );
    if (mounted) context.go('/dashboard');
  }
}

class _Waiting extends StatelessWidget {
  const _Waiting({required this.progress, required this.request, required this.onCancel});

  final PairingProgress progress;
  final PairingRequest request;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    final code = progress.userCode;
    final remaining = progress.secondsRemaining;
    return EsiTicker(
      builder: (context, _) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          EsiPanel(
            title: progress.phase == PairingPhase.decrypting
                ? 'Opening the credentials'
                : 'Waiting for approval on the PC',
            severity: progress.phase == PairingPhase.decrypting
                ? Severity.nominal
                : Severity.elevated,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                if (progress.phase != PairingPhase.decrypting) ...<Widget>[
                  Text(
                    code == null ? 'Asking the PC for a code…' : 'Match this code on the PC:',
                    style: EsiType.body.copyWith(color: EsiPalette.dim),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: EsiSpace.lg),
                  if (code != null)
                    _CodeDisplay(code: code),
                  const SizedBox(height: EsiSpace.lg),
                  if (remaining != null)
                    Row(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: <Widget>[
                        const Icon(Icons.timer_outlined, size: 14, color: EsiPalette.faint),
                        const SizedBox(width: EsiSpace.sm),
                        Text(
                          'expires in ${remaining}s',
                          style: EsiType.mono.copyWith(color: EsiPalette.faint),
                        ),
                      ],
                    ),
                  const SizedBox(height: EsiSpace.lg),
                  const _PulseBar(),
                ] else
                  const EsiLoading(height: 90, label: 'Decrypting the credentials'),
                if (progress.message != null) ...<Widget>[
                  const SizedBox(height: EsiSpace.lg),
                  Text(
                    progress.message!,
                    textAlign: TextAlign.center,
                    style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
                  ),
                ],
              ],
            ),
          ),
          const Spacer(),
          OutlinedButton(
            onPressed: onCancel,
            child: const Text('Cancel'),
          ),
        ],
      ),
    );
  }
}

class _CodeDisplay extends StatelessWidget {
  const _CodeDisplay({required this.code});

  final String code;

  @override
  Widget build(BuildContext context) {
    // Grouped in threes, because six digits read as two halves of three far more easily
    // than as one run of six.
    final grouped = code.length == 6 ? '${code.substring(0, 3)} ${code.substring(3)}' : code;
    return Center(
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: EsiSpace.xl, vertical: EsiSpace.md),
        decoration: BoxDecoration(
          color: EsiPalette.void_.withValues(alpha: 0.5),
          borderRadius: EsiRadius.panel,
          border: Border.all(color: EsiPalette.signal.withValues(alpha: 0.4)),
        ),
        child: Text(
          grouped,
          style: EsiType.hero.copyWith(
            fontSize: 40,
            color: EsiPalette.signal,
            letterSpacing: 6,
          ),
        ),
      ),
    );
  }
}

/// A slow travelling highlight. One continuous motion, used once, to say "this is still
/// waiting" without the app ever needing to shout.
class _PulseBar extends StatefulWidget {
  const _PulseBar();

  @override
  State<_PulseBar> createState() => _PulseBarState();
}

class _PulseBarState extends State<_PulseBar> with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1800),
  )..repeat();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => ClipRRect(
        borderRadius: EsiRadius.chip,
        child: SizedBox(
          height: 3,
          child: AnimatedBuilder(
            animation: _controller,
            child: const SizedBox.expand(),
            builder: (context, _) => DecoratedBox(
              decoration: BoxDecoration(
                gradient: LinearGradient(
                  begin: Alignment(-1 + _controller.value * 2, 0),
                  end: Alignment(-0.4 + _controller.value * 2, 0),
                  colors: const <Color>[
                    EsiPalette.edge,
                    EsiPalette.signal,
                    EsiPalette.edge,
                  ],
                ),
              ),
            ),
          ),
        ),
      );
}

class _Done extends StatelessWidget {
  const _Done({required this.progress, required this.request, required this.onOpen});

  final PairingProgress progress;
  final PairingRequest request;
  final Future<void> Function() onOpen;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          EsiPanel(
            title: 'Paired',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    const Icon(Icons.check_circle, color: EsiPalette.signal, size: 20),
                    const SizedBox(width: EsiSpace.md),
                    Expanded(
                      child: Text(
                        'This phone is paired as ${formatDeviceId((progress as PairingOutcome).deviceId)}',
                        style: EsiType.bodyStrong,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: EsiSpace.md),
                EsiDetailRow(label: 'Key generation', value: '${(progress as PairingOutcome).keyId}'),
                EsiDetailRow(label: 'Address', value: request.endpoint.displayAddress),
              ],
            ),
          ),
          const Spacer(),
          FilledButton(onPressed: onOpen, child: const Text('Open the dashboard')),
        ],
      );
}

class _Failed extends StatelessWidget {
  const _Failed({required this.progress, required this.onRetry});

  final PairingProgress progress;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          EsiPanel(
            title: progress.phase == PairingPhase.cancelled ? 'Cancelled' : 'Pairing failed',
            severity: Severity.critical,
            child: Text(
              progress.error ?? 'The attempt ended without an explanation.',
              style: EsiType.body,
            ),
          ),
          const Spacer(),
          FilledButton(
            onPressed: onRetry,
            child: Text(
              progress.phase == PairingPhase.cancelled ? 'Back' : 'Start again',
            ),
          ),
        ],
      );
}

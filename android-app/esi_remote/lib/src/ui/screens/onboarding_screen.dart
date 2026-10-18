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
    _mac.dispose();
    super.dispose();
  }

  int get _portValue {
    final value = int.tryParse(_port.text.trim());
    return (value != null && value > 0 && value < 65536)
        ? value
        : EsiProtocol.defaultHttpsPort;
  }

  /// Whether the Pair button may run.
  ///
  /// The pairing secret is not asked for here, and cannot be: the PC mints it while
  /// handling the request this button starts and hands it out on a QR shown after that.
  /// Requiring it up front is what made pairing impossible to begin from the phone.
  bool get _canPair => _host.text.trim().isNotEmpty && _pin.text.trim().isNotEmpty;

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
                    _DiscoveryPanel(onUse: _adopt),
                    const SizedBox(height: EsiSpace.md),
                    _ManualPanel(
                      host: _host,
                      port: _port,
                      pin: _pin,
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

  /// Fills the manual form from a discovered agent.
  ///
  /// The pin is filled only when this phone has already paired with this exact machine and
  /// still holds the pin it verified then. Everything else about discovery is unverified -
  /// it arrived over a broadcast anybody on the network could have sent - and the pin is the
  /// one field that decides whether the machine at the other end is really yours.
  ///
  /// So a pin that comes from the wire cannot be filled in: doing so would mean the pin
  /// authenticated nothing, because the machine supplying it is the machine being trusted.
  /// Reading it back out of this phone's own storage is a different matter - that value was
  /// established once, from the PC's screen or its QR code, so re-reading it trusts the past
  /// rather than the network.
  ///
  /// What gets said afterwards depends on what the field already held. This used to be
  /// announced unconditionally from the discovered row, which told the user the pin had been
  /// left empty even when they had just typed it; read while pairing, that sentence stops
  /// looking like information and starts looking like the reason nothing worked.
  void _adopt(String host, int port, String pinEnds) {
    final alreadyTyped = _pin.text.trim().isNotEmpty;
    final remembered = _rememberedPin(host, port);

    setState(() {
      _host.text = host;
      _port.text = '$port';
      if (!alreadyTyped && remembered != null) _pin.text = remembered;
    });

    final message = alreadyTyped
        ? 'Address filled in from discovery. The pin you typed was left as it is. Check '
            'the ends still match this PC before pairing: $pinEnds.'
        : remembered != null
            ? 'Address and pin filled in from this phone\u2019s own records for $host. That '
                'pin was verified when you paired; it is not being taken from the network. '
                'The PC reports the ends as $pinEnds.'
            : 'Address filled in from discovery. The pin is left empty on purpose: PROTOCOL.md '
                '§2.1 treats a value from the wire as untrusted, and it is the one field that '
                'decides whether this really is your PC. Type the full pin from the PC\'s '
                'pairing screen, or scan its QR code - it carries the whole thing. Only its '
                'first and last characters appear here, to check what you typed against: '
                '$pinEnds.';

    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(message),
          duration: const Duration(seconds: 12),
        ),
      );
  }

  /// The pin this phone already trusts for that address, or null if it has never paired.
  ///
  /// Read from this phone's own records, never from discovery. The distinction is the whole
  /// point: a pin that arrived over the network is the pin of whoever answered, so filling it
  /// in would let anyone on the LAN present their own machine and be paired with. A pin this
  /// phone verified once - against the PC's screen, or its QR code - is a different thing
  /// entirely, and re-reading it trusts that earlier moment rather than the wire.
  ///
  /// If the address has since been taken by a different machine, the stored pin will fail to
  /// verify and the handshake will report it. That is the right outcome: a loud refusal beats
  /// silently pairing with the wrong computer.
  String? _rememberedPin(String host, int port) {
    final machines = ref.read(machineListProvider);
    for (final machine in machines) {
      if (machine.port != port) continue;
      if (machine.host.trim().toLowerCase() != host.trim().toLowerCase()) continue;

      for (final pin in machine.pins) {
        final trimmed = pin.trim();
        if (trimmed.isNotEmpty) return trimmed;
      }
    }

    return null;
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
    if (host.isEmpty || pin.isEmpty) {
      _warn(
        'Pairing needs the address and the certificate pin. The pairing secret is not '
        'something you find first — the PC creates it when it receives this request and '
        'hands it to you on the QR shown while you wait.',
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
      // Always empty. A secret is minted per attempt, so anything known before this one
      // exists belongs to a different attempt - and the agent consumes that attempt before
      // it tries to unwrap, so carrying a stale one would burn the code it just issued.
      secret: const <int>[],
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
    try {
      // Pushed by path, not by name. `pushNamed` resolves through
      // `RouteMatchList.namedLocation`, which walks the match list and dereferences each
      // route's `name`; no route in this app declares one, so it threw a null-check error and
      // left the phone sitting on the form. Pairing was unreachable.
      await context.push<void>(
        '/pairing',
        extra: PairingRequest(
          endpoint: endpoint,
          provisioningSecret: Uint8List.fromList(secret),
          source: source,
          macAddress: mac.isEmpty ? null : WakeOnLan.normaliseMac(mac),
        ),
      );
    } catch (error) {
      // A thrown navigation used to strand the form with the Pair button greyed out and no
      // explanation, because `_busy` was only cleared on the success path.
      _warn('Could not open the pairing screen: $error');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
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
    if (payload.macAddress != null) _mac.text = payload.macAddress!;

    // Only the address and the pin are taken from a QR scanned on this screen. The one-time
    // secret is minted per attempt, so anything read before an attempt starts belongs to a
    // different one - and the agent consumes that attempt before it tries to unwrap, so
    // carrying a stale secret forward would burn the code it just issued. It is collected on
    // the pairing screen instead, from the QR the PC shows while this attempt is waiting.
    final missing = payload.missingFields
        .where((field) => field != 'the pairing secret')
        .toList(growable: false);

    setState(() {
      _note = missing.isNotEmpty
          ? 'The QR code did not include ${missing.join(' or ')}. Fill in the rest below.'
          : payload.hasSecret
              ? 'Address, port and pin read from the QR code. Its pairing secret belongs to '
                  'an attempt that is no longer running, so it is not used here — scan this '
                  'QR again while you wait, on the next screen, to collect the credentials.'
              : 'Read from the QR code. Check the address and pin against the PC, then '
                  'pair. The one-time secret comes back from the PC while you wait.';
    });
    _warn(
      missing.isNotEmpty
          ? 'Read the QR code, but it was missing ${missing.join(' and ')}.'
          : 'Pairing details read from the QR code.',
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
                // Scaled rather than clipped. "ESI Remote PC" at 26px measures about 330px,
                // and a 360px phone gives this Row 328 - 15 for the rule and the gap, so the
                // title ran 17px past the right edge on the first screen every user sees.
                // A fixed smaller size would have cost the wordmark its size on the majority
                // of phones that fit it; Expanded takes the space that is actually there and
                // FittedBox shrinks the glyphs only as far as it has to.
                Expanded(
                  child: FittedBox(
                    fit: BoxFit.scaleDown,
                    alignment: Alignment.centerLeft,
                    child: Text('ESI Remote PC', style: EsiType.screenTitle),
                  ),
                ),
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
  const _DiscoveryPanel({required this.onUse});

  /// Copies a discovered address into the manual form below.
  ///
  /// The pin value is deliberately not part of this. PROTOCOL.md §2.1 says the value in a
  /// discovery answer could have been written by anything on the wire, and the pin is the
  /// one field whose whole job is to decide whether you are talking to the right PC. The
  /// address is not a secret and is what the user would otherwise retype, so it is filled.
  /// [pinEnds] is only the head and tail of that pin, passed along purely so the parent can
  /// show what to check a manually typed pin against.
  final void Function(String host, int port, String pinEnds) onUse;

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
            for (final agent in state.agents)
              _DiscoveredAgentRow(agent: agent, onUse: onUse),
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
  const _DiscoveredAgentRow({required this.agent, required this.onUse});

  final DiscoveredAgent agent;
  final void Function(String host, int port, String pinEnds) onUse;

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

    // The explanation is said by the parent, not here. Filling the form rebuilds the tree
    // this row lives in, so this context cannot be used for a snackbar afterwards - and the
    // parent owns the pin field, making it the only place that can tell whether one has
    // already been typed, which decides which of the two things actually needs saying.
    onUse(
      agent.host,
      agent.port,
      formatBytesBase64(agent.spkiPin, head: 10, tail: 6),
    );
  }
}

class _ManualPanel extends StatelessWidget {
  const _ManualPanel({
    required this.host,
    required this.port,
    required this.pin,
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
            'Pairing starts on this phone: press Pair and a six-digit code appears on the '
            'PC, where someone has to approve it. Nothing else will do it. The QR on the '
            'PC\'s pairing page carries only the PC address and its certificate pin - '
            'nothing secret - and the Scan button reads it into this form. The one-time '
            'secret is born on this phone and is only ever shown while the PC\'s prompt is '
            'waiting for a person to approve; the QR never carries it.\n\n'
            'If nobody is signed in, there is no trusted display for the code, so it cannot '
            'be approved in a browser. Stop the agent, run "esirc pair approve <code>" in a '
            'console on the PC, and start the agent again.\n\n'
            'That is deliberate. The pairing page is reachable by anything on the LAN and the '
            'code is only six digits, so a page that could approve would let anyone approve a '
            'pairing they started themselves.',
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
  MobileScannerController? _camera;

  MobileScannerController get _controller =>
      _camera ??= MobileScannerController(
        detectionSpeed: DetectionSpeed.noDuplicates,
        formats: const <BarcodeFormat>[BarcodeFormat.qrCode],
      );

  bool _handled = false;

  // What the camera has heard (D88). The screen used to be silent on purpose about a
  // decode that did not parse, and that silence is what "the scan did nothing" was made
  // of: the camera sat open, the phone said nothing, and nothing anywhere said which of
  // the two lawful silences this was - a code not yet read, or a code read and refused.
  // Every branch speaks now, and each one is a counted event, not a one-shot banner,
  // because a streaky code fails three times before it fails visibly.
  int _rejected = 0;
  int _unreadable = 0;
  bool _hasHeardAnything = false;
  bool _quietLongEnough = false;
  Timer? _quietTimer;

  @override
  void initState() {
    super.initState();
    // Twenty seconds of pure silence is its own thing to say: nothing has been read at
    // all. It fires only while the camera is faultless; a fault screen owns the screen
    // from there on and needs no timer running under it.
    _quietTimer = Timer(const Duration(seconds: 20), () {
      if (mounted) setState(() => _quietLongEnough = true);
    });
  }

  @override
  void dispose() {
    _quietTimer?.cancel();
    _camera?.dispose();
    super.dispose();
  }

  void _restartCamera() {
    // The controller is what got us here - permission state, focus, the camera itself -
    // so a retry builds a whole new one, the way the screen would on a fresh open.
    _quietTimer?.cancel();
    _camera?.dispose();
    _camera = null;
    _quietTimer = Timer(const Duration(seconds: 20), () {
      if (mounted) setState(() => _quietLongEnough = true);
    });
    setState(() {});
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        backgroundColor: EsiPalette.void_,
        body: Stack(
          fit: StackFit.expand,
          children: <Widget>[
            MobileScanner(
              controller: _controller,
              onDetect: _onDetect,
              errorBuilder: (context, error) => _ScannerFault(
                error: error,
                onRetry: _restartCamera,
              ),
            ),
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
                    child: _ScannerBottomLine(
                      rejected: _rejected,
                      unreadable: _unreadable,
                      hasHeardAnything: _hasHeardAnything,
                      quietLongEnough: _quietLongEnough,
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
      switch (PairingQr.read(barcode.rawValue)) {
        case PairingScanDecision.accept:
          _handled = true;
          _quietTimer?.cancel();
          HapticFeedback.mediumImpact();
          Navigator.of(context).pop(barcode.rawValue);
          return;

        case PairingScanDecision.reject:
          setState(() {
            _hasHeardAnything = true;
            _quietLongEnough = false;
            _rejected++;
          });

        case PairingScanDecision.unreadable:
          setState(() {
            _hasHeardAnything = true;
            _quietLongEnough = false;
            _unreadable++;
          });
      }
    }
  }
}

/// The line under the frame: what the camera has heard about the code, or the standing
/// advice when it has heard nothing at all (D88).
///
/// All states are mutual: a fault screen replaces this line's platform half because the
/// camera itself is what has failed, and a fresh rejection reads over the quiet hint -
/// but only for a moment, because the quiet hint is about this code shot, not about the
/// last refused one.
class _ScannerBottomLine extends StatelessWidget {
  const _ScannerBottomLine({
    required this.rejected,
    required this.unreadable,
    required this.hasHeardAnything,
    required this.quietLongEnough,
  });

  final int rejected;
  final int unreadable;
  final bool hasHeardAnything;
  final bool quietLongEnough;

  @override
  Widget build(BuildContext context) {
    final style = EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5);

    // Non-pairing codes first: they are the loud facts. Counted, so a second refusal is a
    // new sentence rather than the old one unconfirmed.
    if (rejected > 0) {
      return Text(
        rejected == 1
            ? 'That code decoded fine, but it is not a pairing code. Point at the QR on '
                'the PC\'s pairing page.'
            : 'That code decoded fine, but it is not a pairing code ($rejected read '
                'this way). Point at the QR on the PC\'s pairing page.',
        textAlign: TextAlign.center,
        style: style.copyWith(color: EsiPalette.flare),
      );
    }

    // Then unreadable decodes - the camera saw a bar, the digits came out empty.
    if (unreadable > 0) {
      return Text(
        'A code was seen but nothing readable came out of it'
        '${unreadable > 1 ? ' ($unreadable times)' : ''}. Bring the phone closer and '
        'keep the code well lit.',
        textAlign: TextAlign.center,
        style: style.copyWith(color: EsiPalette.flare),
      );
    }

    // Then the standing advice - unchanged from before, because when all that has happened
    // is nothing, the old words are still the right ones.
    return Text(
      'The code is on the pairing page in the PC\'s browser. A phone camera cannot read a '
      'screen that is asleep — wake it first.'
      '${quietLongEnough ? "\n\nNothing has been read yet — bring the phone closer." : ""}',
      textAlign: TextAlign.center,
      style: style,
    );
  }
}

/// The camera fault, in the app's own words (D88).
///
/// mobile_scanner's built-in fault panel is a black box one line of white text, which a
/// user describes as "nothing happens": it has no next step and no words of ours. This one
/// names the fault, tells the user the one real action available for a permission refusal
/// (grant it in the system settings), and gives a retry that rebuilds the controller -
/// because a transient camera fault, the kind a second camera app leaves the device in,
/// often clears on a fresh open and nothing else.
class _ScannerFault extends StatelessWidget {
  const _ScannerFault({required this.error, required this.onRetry});

  final MobileScannerException error;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final permissionFault =
        error.errorCode == MobileScannerErrorCode.permissionDenied;

    return ColoredBox(
      color: EsiPalette.void_,
      child: SafeArea(
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(EsiSpace.lg),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                const Icon(Icons.camera_alt_outlined, size: 34, color: EsiPalette.dim),
                const SizedBox(height: EsiSpace.md),
                Text(
                  permissionFault ? 'The camera was not allowed' : 'The camera could not open',
                  style: EsiType.subtitle,
                ),
                const SizedBox(height: EsiSpace.sm),
                Text(
                  permissionFault
                      ? 'The app needs the camera to read the QR. Grant it for this app in '
                          'the phone\'s system settings, then tap Try again here.'
                      : 'The camera on this phone refused to start. Usually that is '
                          'another app holding it. Close that app, then tap Try again.',
                  style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
                ),
                const SizedBox(height: EsiSpace.lg),
                OutlinedButton.icon(
                  onPressed: onRetry,
                  icon: const Icon(Icons.refresh, size: 18),
                  label: const Text('Try again'),
                ),
              ],
            ),
          ),
        ),
      ),
    );
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
                  // Forget removes this phone's copy of the pairing: the secret, the pins,
                  // the address. The PC does not learn about it - revoking actually lives
                  // there - but a list that keeps every PC the phone ever met grows into
                  // exactly the "old connections" the user asked to have a way out of.
                  // An incomplete pairing has nothing worth keeping on either side, and
                  // its row says so; forgetting it is even more obviously right.
                  EsiIconAction(
                    icon: Icons.cancel_schedule_send_outlined,
                    size: 30,
                    tooltip: 'Forget this PC on this phone',
                    onPressed: () => _forget(context, ref, machine),
                  ),
                  const SizedBox(width: EsiSpace.xs),
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

  Future<void> _forget(
    BuildContext context,
    WidgetRef ref,
    PairedMachine machine,
  ) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Forget ${machine.displayName}?',
      body: 'This phone deletes its copy of the pairing - the keys, the pins and the '
          'address. The PC keeps listing this phone until you revoke or rotate it there; '
          'pair again if the PC is still the one you want.',
      confirmLabel: 'Forget',
    );
    if (!ok || !context.mounted) return;
    await ref.read(machineListProvider.notifier).remove(machine.deviceId);
    if (!context.mounted) return;
    showOutcome(context, (
      ok: true,
      message: 'Forgotten ${machine.displayName} on this phone.',
    ));
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

  /// Leaves this screen by popping it, which is how it was arrived at.
  ///
  /// The form underneath is awaiting the future `context.push('/pairing')` handed back, and
  /// that future's `finally` is the only thing that clears the `_busy` greying out 'Check
  /// address' and 'Pair'. `context.go` replaces the whole stack instead of popping, and
  /// go_router completes a pushed route's future only from `_completeRouteMatch`, which runs
  /// on a pop — so `go` abandoned the await and the form came back still `_busy`, with both
  /// buttons dead until the app was restarted. Popping resolves it, which is also what this
  /// route deserves: it was pushed, not navigated to. `go` remains for arriving here without
  /// having been pushed at all, where there is nothing to pop.
  void _leavePairing(BuildContext context) {
    if (context.canPop()) {
      context.pop<void>();
    } else {
      context.go('/onboarding');
    }
  }

  @override
  Widget build(BuildContext context) {
    final flow = ref.watch(pairingFlowProvider);
    final progress = flow.progress;
    // The flow's copy, not the one handed in by the router: adopting the secret from a
    // scanned QR replaces it, and the waiting panel has to see the updated one.
    final request = flow.request ?? _request;

    if (request == null) {
      return Scaffold(
        body: EsiEmptyState(
          icon: Icons.help_outline,
          title: 'Nothing to pair',
          body: 'This screen needs a PC address to work with. Go back and find one.',
          action: FilledButton(
            onPressed: () => _leavePairing(context),
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
        return _Failed(progress: progress!, onRetry: () => _leavePairing(context));
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
  const _Waiting({
    required this.progress,
    required this.request,
    required this.onCancel,
  });

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
                // Nothing to scan any more (D39). This phone created the one-time secret
                // and sent it with the request, so the PC has nothing left to hand over -
                // the only thing left for the user to do is compare the code above with the
                // one on the PC's prompt and click Allow there.
                if (progress.phase != PairingPhase.decrypting) ...<Widget>[
                  const SizedBox(height: EsiSpace.lg),
                  Text(
                    'Check that the code above matches the one in the prompt on the PC, then '
                    'press Allow this device there. That is the whole job.',
                    textAlign: TextAlign.center,
                    style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12),
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

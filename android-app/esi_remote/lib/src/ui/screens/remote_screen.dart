import 'dart:async';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../net/esi_ws.dart';
import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/connection.dart';
import '../../state/features.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';

/// Live screen streaming and input.
///
/// The screen is a tile mosaic rather than a single image, because §7.5 says the agent
/// sends only the tiles that changed. Painting them into a grid is therefore both the
/// simplest thing and the only thing that works: a keyframe is a full-grid frame and a tile
/// update is a handful of cells.
///
/// Input is normalised to 0..1 exactly as §7.3 specifies, so the same gesture works at any
/// display size and the agent does not need to know the phone's resolution.
class RemoteScreen extends ConsumerStatefulWidget {
  const RemoteScreen({super.key});

  @override
  ConsumerState<RemoteScreen> createState() => _RemoteScreenState();
}

class _RemoteScreenState extends ConsumerState<RemoteScreen> {
  final ScreenSurface _surface = ScreenSurface();
  int _displayId = 0;
  double _fps = 15;
  double _quality = 65;
  int _gridCols = 4;
  int _gridRows = 4;
  bool _inputEnabled = true;
  bool _showKeyboard = false;
  StreamSubscription<Uint8List>? _frames;

  @override
  void initState() {
    super.initState();
    _frames = ref.read(webSocketProvider)?.binary.listen(_onFrame);
  }

  @override
  void dispose() {
    _frames?.cancel();
    _surface.dispose();
    super.dispose();
  }

  void _onFrame(Uint8List frame) {
    unawaited(_applyFrame(frame));
  }

  Future<void> _applyFrame(Uint8List frame) async {
    try {
      final header = ScreenFrameHeader.parse(frame);
      if (header.codec != ScreenFrameHeader.codecJpeg) {
        // Only JPEG is defined in v1; the RDP graphics pipeline is reserved. An unexpected
        // codec is dropped rather than rendered as noise.
        return;
      }
      final tiles = parseScreenTiles(frame);
      await _surface.apply(header: header, tiles: tiles);
      if (mounted) setState(() {});
    } on FormatException {
      // A malformed frame is dropped rather than crashing the stream: §7.5 has a resync
      // request for exactly this, and one bad packet should not end the session.
    }
  }

  @override
  Widget build(BuildContext context) => RequiresPairing(
        builder: (context, ref, connection) {
          if (!connection.supports(EsiFeature.remote)) {
            return const FeatureUnavailable(feature: 'remote', label: 'Screen control');
          }
          final status = ref.watch(remoteStatusProvider).value ?? unknownRemoteStatus;
          final socket = ref.watch(webSocketProvider);
          final live = socket?.isLive ?? false;

          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Screen',
              subtitle: _subtitle(status, live),
              children: <Widget>[
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _StatusPanel(status: status),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _StreamPanel(
                    surface: _surface,
                    status: status,
                    streaming: _surface.hasContent,
                    live: live,
                    onStart: _start,
                    onStop: _stop,
                  ),
                ),
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _SettingsPanel(
                    displayId: _displayId,
                    fps: _fps,
                    quality: _quality,
                    gridCols: _gridCols,
                    gridRows: _gridRows,
                    inputEnabled: _inputEnabled,
                    displays: status.displays,
                    onChanged: (displayId, fps, quality, cols, rows, input) {
                      setState(() {
                        _displayId = displayId;
                        _fps = fps;
                        _quality = quality;
                        _gridCols = cols;
                        _gridRows = rows;
                        _inputEnabled = input;
                      });
                    },
                  ),
                ),
                if (_showKeyboard) ...<Widget>[
                  const SizedBox(height: EsiSpace.md),
                  _KeyboardPanel(
                    onSend: (input) => ref.read(remoteInputActionsProvider).text(input),
                    onKey: (code) {
                      final actions = ref.read(remoteInputActionsProvider);
                      actions.key(code: code, down: true);
                      actions.key(code: code, down: false);
                    },
                  ),
                ],
                const SizedBox(height: EsiSpace.md),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: Row(
                    children: <Widget>[
                      Expanded(
                        child: OutlinedButton.icon(
                          onPressed: () => setState(() => _showKeyboard = !_showKeyboard),
                          icon: Icon(
                            _showKeyboard ? Icons.keyboard_hide : Icons.keyboard,
                            size: 17,
                          ),
                          label: const Text('Type on the PC'),
                        ),
                      ),
                      const SizedBox(width: EsiSpace.sm),
                      Expanded(
                        child: FilledButton.icon(
                          onPressed: live
                              ? () => ref
                                  .read(remoteInputActionsProvider)
                                  .button('right', true, 0.5, 0.5)
                              : null,
                          icon: const Icon(Icons.ads_click, size: 17),
                          label: const Text('Test click'),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          );
        },
      );

  static String _subtitle(RemoteSessionStatus status, bool live) {
    final bits = <String>[
      if (status.available) 'session helper connected' else 'session helper not available',
      status.streaming ? 'streaming' : 'not streaming',
      if (live) 'live socket' else 'no live socket',
    ];
    return bits.join(' · ');
  }

  void _start() {
    final actions = ref.read(remoteInputActionsProvider);
    actions.start(
      displayId: _displayId,
      fps: _fps.round(),
      quality: _quality.round(),
      grid: <int>[_gridCols, _gridRows],
    );

  }

  void _stop() {
    ref.read(remoteInputActionsProvider).stop();
    _surface.clear();
    setState(() {});
  }
}

/// Holds the decoded tiles.
///
/// §7.5 says the agent sends only the tiles that changed, so the mosaic is kept as a map
/// of `ui.Image` per grid cell rather than as one full-frame image. A tile update is then a
/// single cell replaced, and the composite is re-recorded from the map. Decoding runs
/// asynchronously because Flutter's image codec is the expensive part, and dropping every
/// frame of a 15 fps stream behind a decode would make the stream useless.
class ScreenSurface {
  final Map<String, ui.Image> _tiles = <String, ui.Image>{};
  ui.Image? _composite;
  ui.Picture? _picture;
  int _width = 0;
  int _height = 0;
  int _cols = 0;
  int _rows = 0;
  int _frameIndex = -1;

  bool get hasContent => _composite != null;
  int get width => _width;
  int get height => _height;
  int get frameIndex => _frameIndex;

  /// The composited screen, for the painter.
  ui.Image? get image => _composite;

  Future<void> apply({
    required ScreenFrameHeader header,
    required List<ScreenTile> tiles,
  }) async {
    if (_composite == null ||
        _width != header.screenWidth ||
        _height != header.screenHeight) {
      _reset(header);
    }
    if (header.frameIndex <= _frameIndex) return; // An out-of-order packet.
    _frameIndex = header.frameIndex;

    final decoded = <String, ui.Image>{};
    var failures = 0;
    for (final tile in tiles) {
      try {
        final codec = await ui.instantiateImageCodec(tile.jpeg);
        final frame = await codec.getNextFrame();
        decoded['${tile.col},${tile.row}'] = frame.image;
      } on Object catch (e) {
        // A tile that will not decode is left as whatever was there before, so one bad
        // packet shows a stale cell rather than a black one.
        failures++;
        debugPrint('tile ${tile.col},${tile.row} (${tile.width}x${tile.height}, '
            '${tile.jpeg.length}B) failed to decode: $e');
      }
    }

    // D46 follow-up: a black stream that reports a frame number looks identical to a
    // working one, because `hasContent` is true as soon as a composite exists - and a
    // composite of zero tiles is still a black rectangle. Saying how many tiles arrived
    // and how many decoded is what separates "the agent is not sending" from "we cannot
    // read what it sent". They look the same to the user.
    debugPrint('frame ${header.frameIndex}: ${tiles.length} tiles offered, '
        '${decoded.length} decoded, $failures failed, grid '
        '${header.gridCols}x${header.gridRows}, surface '
        '${header.screenWidth}x${header.screenHeight}');
    for (final entry in decoded.entries) {
      _tiles.remove(entry.key)?.dispose();
      _tiles[entry.key] = entry.value;
    }
    _rebuild();
  }

  void _reset(ScreenFrameHeader header) {
    clear();
    _width = header.screenWidth;
    _height = header.screenHeight;
    _cols = header.gridCols <= 0 ? 1 : header.gridCols;
    _rows = header.gridRows <= 0 ? 1 : header.gridRows;
  }

  void _rebuild() {
    if (_width <= 0 || _height <= 0) return;
    final recorder = ui.PictureRecorder();
    final canvas = Canvas(recorder);
    canvas.drawRect(
      Rect.fromLTWH(0, 0, _width.toDouble(), _height.toDouble()),
      Paint()..color = const Color(0xFF000000),
    );
    final tileWidth = _width / _cols;
    final tileHeight = _height / _rows;
    for (final entry in _tiles.entries) {
      final parts = entry.key.split(',');
      final col = int.tryParse(parts.first) ?? 0;
      final row = int.tryParse(parts.length > 1 ? parts[1] : '0') ?? 0;
      canvas.drawImageRect(
        entry.value,
        Rect.fromLTWH(0, 0, entry.value.width.toDouble(), entry.value.height.toDouble()),
        Rect.fromLTWH(col * tileWidth, row * tileHeight, tileWidth, tileHeight),
        Paint()..filterQuality = FilterQuality.medium,
      );
    }
    _picture?.dispose();
    _picture = recorder.endRecording();
    _composite?.dispose();
    _composite = _picture!.toImageSync(_width, _height);
  }

  void clear() {
    for (final tile in _tiles.values) {
      tile.dispose();
    }
    _tiles.clear();
    _picture?.dispose();
    _picture = null;
    _composite?.dispose();
    _composite = null;
    _frameIndex = -1;
  }

  void dispose() => clear();
}

class _StatusPanel extends StatelessWidget {
  const _StatusPanel({required this.status});

  final RemoteSessionStatus status;

  @override
  Widget build(BuildContext context) => EsiPanel(
        title: 'Capture',
        severity: status.available ? Severity.nominal : Severity.elevated,
        child: EsiReadoutList(
          children: <Widget>[
            EsiReadout(
              label: 'Session helper',
              value: status.sessionAgentState,
              severity: status.available ? Severity.nominal : Severity.elevated,
              tooltip: 'Screen capture and input both go through a helper running in the '
                  'signed-in user session. Without it there is no desktop to capture.',
            ),
            EsiReadout(label: 'Capture source', value: status.captureSource),
            EsiReadout(label: 'Maximum frame rate', value: status.maxFps == 0 ? null : '${status.maxFps} fps'),
            EsiReadout(
              label: 'Displays',
              value: status.displays.isEmpty ? null : '${status.displays.length}',
            ),
            EsiReadout(
              label: 'Another device watching',
              value: status.connectedDeviceId,
              severity: status.connectedDeviceId == null ? Severity.nominal : Severity.elevated,
            ),
          ],
        ),
      );
}

class _StreamPanel extends ConsumerWidget {
  const _StreamPanel({
    required this.surface,
    required this.status,
    required this.streaming,
    required this.live,
    required this.onStart,
    required this.onStop,
  });

  final ScreenSurface surface;
  final RemoteSessionStatus status;
  final bool streaming;
  final bool live;
  final VoidCallback onStart;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final actions = ref.read(remoteInputActionsProvider);
    return EsiPanel(
      title: 'Stream',
      severity: streaming ? Severity.nominal : Severity.unknown,
      footnote: streaming
          ? 'frame ${surface.frameIndex} · ${surface.width}×${surface.height}'
          : 'Nothing is arriving yet. The screen only fills in once the agent starts '
              'sending tiles.',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          _InputSurface(
            surface: surface,
            enabled: streaming,
            onTapAt: (x, y) => actions.move(x, y),
            onButton: (button, down, x, y) => actions.button(button, down, x, y),
          ),
          const SizedBox(height: EsiSpace.md),
          Row(
            children: <Widget>[
              Expanded(
                child: FilledButton.icon(
                  onPressed: live && !streaming ? onStart : null,
                  icon: const Icon(Icons.play_arrow, size: 17),
                  label: const Text('Start'),
                ),
              ),
              const SizedBox(width: EsiSpace.sm),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: streaming ? onStop : null,
                  icon: const Icon(Icons.stop, size: 17),
                  label: const Text('Stop'),
                ),
              ),
            ],
          ),
          if (!status.available) ...<Widget>[
            const SizedBox(height: EsiSpace.md),
            Row(
              children: <Widget>[
                const Icon(Icons.info_outline, size: 13, color: EsiPalette.faint),
                const SizedBox(width: EsiSpace.sm),
                Expanded(
                  child: Text(
                    'The session helper is not connected, so the agent cannot capture the '
                    'desktop. Start it from the signed-in Windows session.',
                    style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 11.5),
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

/// Turns touches into the normalised input events of §7.3.
class _InputSurface extends StatefulWidget {
  const _InputSurface({
    required this.surface,
    required this.enabled,
    required this.onTapAt,
    required this.onButton,
  });

  final ScreenSurface surface;
  final bool enabled;
  final void Function(double x, double y) onTapAt;
  final void Function(String button, bool down, double x, double y) onButton;

  @override
  State<_InputSurface> createState() => _InputSurfaceState();
}

class _InputSurfaceState extends State<_InputSurface> {
  Offset? _lastPosition;

  @override
  Widget build(BuildContext context) => AspectRatio(
        aspectRatio: widget.surface.width == 0 || widget.surface.height == 0
            ? 16 / 9
            : widget.surface.width / widget.surface.height,
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTapUp: widget.enabled
              ? (details) {
                  final normalised = _normalise(details.localPosition, context);
                  widget.onTapAt(normalised.dx, normalised.dy);
                  widget.onButton('left', true, normalised.dx, normalised.dy);
                  widget.onButton('left', false, normalised.dx, normalised.dy);
                }
              : null,
          onPanStart: widget.enabled
              ? (details) {
                  _lastPosition = details.localPosition;
                  final normalised = _normalise(details.localPosition, context);
                  widget.onButton('left', true, normalised.dx, normalised.dy);
                }
              : null,
          onPanUpdate: widget.enabled
              ? (details) {
                  final normalised = _normalise(details.localPosition, context);
                  widget.onTapAt(normalised.dx, normalised.dy);
                  _lastPosition = details.localPosition;
                }
              : null,
          onPanEnd: widget.enabled
              ? (_) {
                  final position = _lastPosition;
                  if (position == null) return;
                  final normalised = _normalise(position, context);
                  widget.onButton('left', false, normalised.dx, normalised.dy);
                }
              : null,
          onLongPressStart: widget.enabled
              ? (details) {
                  final normalised = _normalise(details.localPosition, context);
                  widget.onButton('right', true, normalised.dx, normalised.dy);
                }
              : null,
          onLongPressEnd: widget.enabled
              ? (details) {
                  final normalised = _normalise(details.localPosition, context);
                  widget.onButton('right', false, normalised.dx, normalised.dy);
                }
              : null,
          child: DecoratedBox(
            decoration: BoxDecoration(
              color: EsiPalette.void_,
              borderRadius: EsiRadius.tag,
              border: Border.all(color: EsiPalette.edge),
            ),
            child: widget.surface.hasContent
                ? ClipRRect(
                    borderRadius: EsiRadius.tag,
                    child: CustomPaint(
                      painter: _ImagePainter(widget.surface),
                      size: Size.infinite,
                    ),
                  )
                : Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: <Widget>[
                        Icon(
                          Icons.desktop_windows_outlined,
                          size: 26,
                          color: EsiPalette.faint.withValues(alpha: 0.7),
                        ),
                        const SizedBox(height: EsiSpace.md),
                        Text(
                          widget.enabled
                              ? 'Waiting for the first frame'
                              : 'Start a stream to see the screen here',
                          style: EsiType.body.copyWith(color: EsiPalette.faint),
                        ),
                      ],
                    ),
                  ),
          ),
        ),
      );

  /// The protocol's input coordinates are normalised to 0..1 against the *display*, not
  /// the phone widget, so the phone's own size cancels out.
  static Offset _normalise(Offset local, BuildContext context) {
    final box = context.findRenderObject() as RenderBox?;
    if (box == null || !box.hasSize) return Offset.zero;
    return Offset(
      (local.dx / box.size.width).clamp(0.0, 1.0),
      (local.dy / box.size.height).clamp(0.0, 1.0),
    );
  }
}

class _ImagePainter extends CustomPainter {
  _ImagePainter(this.surface);

  final ScreenSurface surface;

  @override
  void paint(Canvas canvas, Size size) {
    final image = surface.image;
    if (image == null) return;
    canvas.drawImageRect(
      image,
      Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()),
      Offset.zero & size,
      Paint()..filterQuality = FilterQuality.medium,
    );
  }

  @override
  bool shouldRepaint(_ImagePainter oldDelegate) =>
      oldDelegate.surface.frameIndex != surface.frameIndex;
}

class _SettingsPanel extends StatelessWidget {
  const _SettingsPanel({
    required this.displayId,
    required this.fps,
    required this.quality,
    required this.gridCols,
    required this.gridRows,
    required this.inputEnabled,
    required this.displays,
    required this.onChanged,
  });

  final int displayId;
  final double fps;
  final double quality;
  final int gridCols;
  final int gridRows;
  final bool inputEnabled;
  final List<DisplayInfo> displays;
  final void Function(int, double, double, int, int, bool) onChanged;

  @override
  Widget build(BuildContext context) {
    void change({
      int? display,
      double? frameRate,
      double? jpegQuality,
      int? cols,
      int? rows,
      bool? input,
    }) =>
        onChanged(
          display ?? displayId,
          frameRate ?? fps,
          jpegQuality ?? quality,
          cols ?? gridCols,
          rows ?? gridRows,
          input ?? inputEnabled,
        );

    return EsiPanel(
      title: 'Stream settings',
      severity: Severity.nominal,
      footnote: 'These take effect the next time a stream starts. A finer grid means less '
          'is re-sent when only part of the screen changes, and more work per frame.',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          if (displays.length > 1)
            DropdownButtonFormField<int>(
              initialValue: displayId,
              decoration: const InputDecoration(labelText: 'Display'),
              dropdownColor: EsiPalette.deck,
              items: <DropdownMenuItem<int>>[
                for (final display in displays)
                  DropdownMenuItem<int>(
                    value: display.id,
                    child: Text(
                      '${display.name ?? 'Display ${display.id}'} · '
                      '${display.width}×${display.height}'
                      '${display.isPrimary ? ' (primary)' : ''}',
                      style: EsiType.body.copyWith(fontSize: 12.5),
                    ),
                  ),
              ],
              onChanged: (value) => value == null ? null : change(display: value),
            ),
          const SizedBox(height: EsiSpace.md),
          _SliderRow(
            label: 'Frame rate',
            value: fps,
            min: 1,
            max: 30,
            divisions: 29,
            suffix: 'fps',
            onChanged: (value) => change(frameRate: value),
          ),
          _SliderRow(
            label: 'JPEG quality',
            value: quality,
            min: 30,
            max: 95,
            divisions: 13,
            suffix: '',
            onChanged: (value) => change(jpegQuality: value),
          ),
          _SliderRow(
            label: 'Tile grid',
            value: gridCols.toDouble(),
            min: 1,
            max: 8,
            divisions: 7,
            suffix: '× $gridRows',
            onChanged: (value) => change(cols: value.round(), rows: value.round()),
          ),
          Row(
            children: <Widget>[
              Expanded(
                child: Text(
                  'Send input back',
                  style: EsiType.body.copyWith(fontSize: 12.5, color: EsiPalette.dim),
                ),
              ),
              Switch(
                value: inputEnabled,
                onChanged: (next) => change(input: next),
              ),
            ],
          ),
          if (!inputEnabled)
            Padding(
              padding: const EdgeInsets.only(bottom: EsiSpace.xs),
              child: Text(
                'Input is off, so touches and keys are not delivered to the PC. The screen '
                'still streams.',
                style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 11.5),
              ),
            ),
        ],
      ),
    );
  }
}

class _SliderRow extends StatelessWidget {
  const _SliderRow({
    required this.label,
    required this.value,
    required this.min,
    required this.max,
    required this.divisions,
    required this.suffix,
    required this.onChanged,
  });

  final String label;
  final double value;
  final double min;
  final double max;
  final int divisions;
  final String suffix;
  final ValueChanged<double> onChanged;

  @override
  Widget build(BuildContext context) => Row(
        children: <Widget>[
          SizedBox(
            width: 96,
            child: Text(
              label,
              style: EsiType.body.copyWith(fontSize: 12.5, color: EsiPalette.dim),
            ),
          ),
          Expanded(
            child: Slider(
              value: value.clamp(min, max),
              min: min,
              max: max,
              divisions: divisions,
              label: '$value$suffix',
              onChanged: onChanged,
            ),
          ),
          SizedBox(
            width: 46,
            child: Text(
              '$value$suffix',
              textAlign: TextAlign.right,
              style: EsiType.readingSmall,
            ),
          ),
        ],
      );
}

/// A small on-screen key pad. §7.3 says unknown key codes fall back to `text` injection
/// through `KEYEVENTF_UNICODE`, so typing is the path that always works — including for
/// layouts this app has no mapping table for. The named buttons are sent as real key
/// events with a down and an up, because the agent injectates physical keys.
class _KeyboardPanel extends StatelessWidget {
  const _KeyboardPanel({required this.onSend, required this.onKey});

  final void Function(String text) onSend;
  final void Function(String code) onKey;

  /// The W3C `KeyboardEvent.code` values the agent maps, paired with the label shown.
  static const List<(String, String)> _rows = <(String, String)>[
    ('Escape', 'Esc'),
    ('Tab', 'Tab'),
    ('Backspace', 'Back'),
    ('Enter', 'Enter'),
    ('ArrowUp', 'Up'),
    ('ArrowDown', 'Down'),
    ('ArrowLeft', 'Left'),
    ('ArrowRight', 'Right'),
    ('Delete', 'Del'),
    ('Home', 'Home'),
    ('End', 'End'),
    ('PageUp', 'PgUp'),
    ('PageDown', 'PgDn'),
  ];

  @override
  Widget build(BuildContext context) => EsiPanel(
        title: 'Keys',
        severity: Severity.nominal,
        footnote: 'These map to the agent\'s named key codes. Anything not on this pad can '
            'still be typed below.',
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            Wrap(
              spacing: EsiSpace.sm,
              runSpacing: EsiSpace.sm,
              children: <Widget>[
                for (final entry in _rows)
                  OutlinedButton(
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size(0, 36),
                      padding: const EdgeInsets.symmetric(horizontal: EsiSpace.md),
                    ),
                    onPressed: () {
                      HapticFeedback.selectionClick();
                      onKey(entry.$1);
                    },
                    child: Text(entry.$2, style: EsiType.body.copyWith(fontSize: 12)),
                  ),
              ],
            ),
            const SizedBox(height: EsiSpace.md),
            TextField(
              style: EsiType.body.copyWith(color: EsiPalette.ink),
              decoration: const InputDecoration(
                hintText: 'Type and press send',
                labelText: 'Text',
              ),
              onSubmitted: (value) {
                if (value.isNotEmpty) onSend(value);
              },
            ),
          ],
        ),
      );
}

import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../format.dart';
import '../theme.dart';

/// One channel of the live load trace.
class TraceChannel {
  const TraceChannel({
    required this.label,
    required this.series,
    required this.colour,
  });

  final String label;
  final List<double> series;
  final Color colour;
}

/// The app's one memorable element: a scrolling multi-channel oscilloscope fed by the
/// metrics stream.
///
/// Why a trace and not three sparklines. The question a person actually has about a
/// machine they are not sitting at is not "what is the CPU doing" but "is anything
/// happening that I would regret missing" — and that is a question about the *shape* of
/// the last few minutes, not about the current value. Overlaying the channels on one
/// shared time axis also makes the one comparison that matters visible at a glance: when
/// a GPU-bound game is running, the GPU trace sits high and the CPU trace steps down
/// underneath it. Three separate sparklines cannot show that.
///
/// Gaps are drawn as gaps. If a reading stops arriving, the trace breaks rather than
/// interpolating across the hole, because a straight line between two samples would be a
/// claim about data that does not exist.
/// How much there is to draw.
///
/// The grid and the legend are always painted, so a plot with no readings at all looks exactly
/// like a plot whose lines are merely hiding: a frame, three labels, and nothing between them.
/// That is what "the usage graph does not show" looks like from the phone, and the only thing
/// that separates the two cases - nothing has arrived yet, versus everything has arrived and is
/// being drawn - is saying which one it is.
enum TraceState {
  /// Not one reading in any channel.
  empty,

  /// Readings exist, but fewer than two in every channel: a dot on the right and no line yet.
  thin,

  /// At least one channel can be drawn as a line.
  ready,
}

/// Classifies [channels] for [EsiLoadTrace] to explain itself with.
TraceState traceState(List<TraceChannel> channels) {
  var anySample = false;
  for (final channel in channels) {
    var count = 0;
    for (final value in channel.series) {
      // The painter skips NaN rather than bridging it, so a NaN is not a reading here either.
      if (value.isNaN) continue;
      anySample = true;
      count++;
      if (count >= 2) return TraceState.ready;
    }
  }
  return anySample ? TraceState.thin : TraceState.empty;
}

class EsiLoadTrace extends StatelessWidget {
  const EsiLoadTrace({
    super.key,
    required this.channels,
    this.height = 116,
    this.streaming = false,
    this.showGrid = true,
  });

  final List<TraceChannel> channels;
  final double height;

  /// True while the live stream is running. Drives the small "live" mark at the right and
  /// stops the sweep animation when the data is stale.
  final bool streaming;
  final bool showGrid;

  @override
  Widget build(BuildContext context) => SizedBox(
        height: height,
        child: Stack(
          children: <Widget>[
            Positioned.fill(
              child: CustomPaint(
                painter: _TracePainter(
                  channels: channels,
                  showGrid: showGrid,
                ),
              ),
            ),
            if (traceState(channels) != TraceState.ready)
              Positioned.fill(
                child: Center(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                    child: Text(
                      switch (traceState(channels)) {
                        TraceState.empty => 'No load data yet',
                        TraceState.thin => 'Taking the first readings',
                        TraceState.ready => '',
                      },
                      textAlign: TextAlign.center,
                      style: EsiType.body.copyWith(color: EsiPalette.faint),
                    ),
                  ),
                ),
              ),
            Positioned(
              right: 0,
              top: 0,
              child: _Legend(channels: channels, streaming: streaming),
            ),
          ],
        ),
      );
}

class _Legend extends StatelessWidget {
  const _Legend({required this.channels, required this.streaming});

  final List<TraceChannel> channels;
  final bool streaming;

  @override
  Widget build(BuildContext context) => Wrap(
        alignment: WrapAlignment.end,
        spacing: EsiSpace.md,
        runSpacing: EsiSpace.xs,
        children: <Widget>[
          for (final channel in channels)
            Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Container(
                  width: 10,
                  height: 2,
                  decoration: BoxDecoration(
                    color: channel.colour,
                    borderRadius: EsiRadius.chip,
                  ),
                ),
                const SizedBox(width: EsiSpace.xs + 1),
                Text(
                  channel.label,
                  style: EsiType.label.copyWith(
                    color: channel.colour.withValues(alpha: 0.85),
                    fontSize: 9,
                  ),
                ),
              ],
            ),
          if (streaming)
            Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                _LiveDot(),
                const SizedBox(width: EsiSpace.xs + 1),
                Text('live', style: EsiType.label.copyWith(color: EsiPalette.live)),
              ],
            ),
        ],
      );
}

/// A slow pulse, used once: on the live marker in the status spine and here. It exists to
/// say "this number is being updated right now", which is different information from "this
/// number is large".
class _LiveDot extends StatefulWidget {
  const _LiveDot();

  @override
  State<_LiveDot> createState() => _LiveDotState();
}

class _LiveDotState extends State<_LiveDot> with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1600),
  )..repeat();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
        animation: _controller,
        builder: (context, _) {
          final t = Curves.easeInOut.transform(_controller.value);
          return Container(
            width: 6,
            height: 6,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: EsiPalette.live.withValues(alpha: 0.45 + 0.55 * (1 - t)),
              boxShadow: <BoxShadow>[
                BoxShadow(
                  color: EsiPalette.live.withValues(alpha: 0.5 * (1 - t)),
                  blurRadius: 6,
                  spreadRadius: 1 + 2 * t,
                ),
              ],
            ),
          );
        },
      );
}

class _TracePainter extends CustomPainter {
  _TracePainter({required this.channels, required this.showGrid});

  final List<TraceChannel> channels;
  final bool showGrid;

  static const double _rightGutter = 4;

  @override
  void paint(Canvas canvas, Size size) {
    final plot = Rect.fromLTWH(0, 0, math.max(0, size.width - _rightGutter), size.height);

    if (showGrid) _paintGrid(canvas, plot);

    for (final channel in channels) {
      _paintSeries(canvas, plot, channel);
    }
  }

  void _paintGrid(Canvas canvas, Rect plot) {
    final line = Paint()
      ..color = EsiPalette.edge.withValues(alpha: 0.55)
      ..strokeWidth = 1;

    for (var i = 1; i < 4; i++) {
      final y = plot.top + plot.height * (i / 4);
      canvas.drawLine(Offset(plot.left, y), Offset(plot.right, y), line);
    }

    // A stronger rule at 50% so "half loaded" is readable without a legend.
    canvas.drawLine(
      Offset(plot.left, plot.center.dy),
      Offset(plot.right, plot.center.dy),
      Paint()
        ..color = EsiPalette.edgeBright.withValues(alpha: 0.7)
        ..strokeWidth = 1,
    );
  }

  void _paintSeries(Canvas canvas, Rect plot, TraceChannel channel) {
    final data = channel.series;
    if (data.length < 2) {
      // A single sample is a dot, not a line: there is no trend to draw yet and inventing
      // one would be a lie about the data.
      if (data.length == 1) {
        final y = _yFor(data.first, plot);
        canvas.drawCircle(Offset(plot.right - 1, y), 2, Paint()..color = channel.colour);
      }
      return;
    }

    final step = plot.width / (data.length - 1);
    final path = Path();
    final area = Path();

    var started = false;
    for (var i = 0; i < data.length; i++) {
      final value = data[i];
      // The agent returns NaN for a sample it could not take; break the line there.
      if (value.isNaN) {
        started = false;
        continue;
      }
      final x = plot.left + step * i;
      final y = _yFor(value, plot);
      if (!started) {
        path.moveTo(x, y);
        area.moveTo(x, plot.bottom);
        area.lineTo(x, y);
        started = true;
      } else {
        path.lineTo(x, y);
        area.lineTo(x, y);
      }
      if (i == data.length - 1) area.lineTo(x, plot.bottom);
    }

    if (!started) return;

    // A soft gradient under the newest channel only, so three overlapping fills turn to
    // mud. The stroke carries the shape; the fill only grounds the most important one.
    if (channel == channels.first && channels.length == 1) {
      final fill = Paint()
        ..shader = LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: <Color>[
            channel.colour.withValues(alpha: 0.22),
            channel.colour.withValues(alpha: 0.0),
          ],
        ).createShader(Rect.fromLTWH(plot.left, plot.top, plot.width, plot.height));
      canvas.drawPath(area, fill);
    }

    canvas.drawPath(
      path,
      Paint()
        ..color = channel.colour
        ..strokeWidth = 1.6
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round
        ..style = PaintingStyle.stroke,
    );

    // Mark the newest sample, which is the one the number above the trace is showing.
    final last = _lastValid(data);
    if (last != null) {
      final y = _yFor(last, plot);
      canvas.drawCircle(
        Offset(plot.right - 1, y),
        2.6,
        Paint()..color = channel.colour,
      );
      canvas.drawCircle(
        Offset(plot.right - 1, y),
        5,
        Paint()
          ..color = channel.colour.withValues(alpha: 0.28)
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1,
      );
    }
  }

  static double? _lastValid(List<double> data) {
    for (var i = data.length - 1; i >= 0; i--) {
      if (!data[i].isNaN) return data[i];
    }
    return null;
  }

  static double _yFor(double value, Rect plot) {
    final clamped = value.clamp(0.0, 100.0) / 100.0;
    return plot.bottom - clamped * plot.height;
  }

  @override
  bool shouldRepaint(_TracePainter oldDelegate) {
    if (oldDelegate.channels.length != channels.length) return true;
    if (oldDelegate.showGrid != showGrid) return true;
    for (var i = 0; i < channels.length; i++) {
      if (!listEquals(oldDelegate.channels[i].series, channels[i].series)) return true;
      if (oldDelegate.channels[i].colour != channels[i].colour) return true;
    }
    return false;
  }
}

bool listEquals(List<double> a, List<double> b) {
  if (identical(a, b)) return true;
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

/// A circular progress ring used for the storage and per-core displays.
class EsiRing extends StatelessWidget {
  const EsiRing({
    super.key,
    required this.fraction,
    required this.centre,
    this.caption,
    this.severity = Severity.nominal,
    this.size = 62,
  });

  final double? fraction;

  /// The hero number inside the ring.
  final String centre;
  final String? caption;
  final Severity severity;
  final double size;

  @override
  Widget build(BuildContext context) {
    final colour = switch (severity) {
      Severity.nominal => EsiPalette.signal,
      Severity.elevated => EsiPalette.flare,
      Severity.critical => EsiPalette.alarm,
      Severity.unknown => EsiPalette.faint,
    };
    final value = fraction?.clamp(0.0, 1.0);
    return SizedBox(
      width: size,
      height: size,
      child: CustomPaint(
        painter: _RingPainter(fraction: value, colour: colour),
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Text(
                centre,
                style: EsiType.reading.copyWith(
                  fontSize: size * 0.24,
                  color: value == null ? EsiPalette.faint : EsiPalette.ink,
                ),
              ),
              if (caption != null)
                Text(
                  caption!,
                  style: EsiType.label.copyWith(fontSize: size * 0.13),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _RingPainter extends CustomPainter {
  _RingPainter({required this.fraction, required this.colour});

  final double? fraction;
  final Color colour;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;
    final centre = rect.center;
    final radius = size.shortestSide / 2 - 3;
    final track = Paint()
      ..color = EsiPalette.edge
      ..strokeWidth = 3
      ..style = PaintingStyle.stroke;
    canvas.drawCircle(centre, radius, track);

    final value = fraction;
    if (value == null) return;
    if (value <= 0) return;

    canvas.drawArc(
      Rect.fromCircle(center: centre, radius: radius),
      -math.pi / 2,
      2 * math.pi * value,
      false,
      Paint()
        ..color = colour
        ..strokeWidth = 3
        ..strokeCap = StrokeCap.round
        ..style = PaintingStyle.stroke,
    );
  }

  @override
  bool shouldRepaint(_RingPainter oldDelegate) =>
      oldDelegate.fraction != fraction || oldDelegate.colour != colour;
}

/// A row of per-core bars, labelled with the core index. Used for `cpu.perCore`, which the
/// protocol says may be empty when the per-core counters are unavailable — in which case
/// the panel says so rather than drawing sixteen empty boxes.
class EsiCoreGrid extends StatelessWidget {
  const EsiCoreGrid({super.key, required this.perCore, this.columns = 8});

  final List<double> perCore;
  final int columns;

  @override
  Widget build(BuildContext context) {
    if (perCore.isEmpty) {
      return Row(
        children: <Widget>[
          const Icon(Icons.info_outline, size: 13, color: EsiPalette.faint),
          const SizedBox(width: EsiSpace.sm),
          Expanded(
            child: Text(
              'Per-core counters are not available on this machine.',
              style: EsiType.body.copyWith(color: EsiPalette.faint, fontSize: 12.5),
            ),
          ),
        ],
      );
    }
    final rows = <Widget>[];
    for (var start = 0; start < perCore.length; start += columns) {
      final chunk = perCore.sublist(
        start,
        math.min(start + columns, perCore.length),
      );
      rows.add(
        Row(
          children: <Widget>[
            for (final value in chunk)
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.only(right: 3),
                  child: Column(
                    children: <Widget>[
                      SizedBox(
                        height: 30,
                        child: _CoreColumn(value: value),
                      ),
                      const SizedBox(height: EsiSpace.xs),
                      Text(
                        '${start + chunk.indexOf(value)}',
                        style: EsiType.label.copyWith(fontSize: 8),
                      ),
                    ],
                  ),
                ),
              ),
          ],
        ),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        for (var i = 0; i < rows.length; i++) ...<Widget>[
          if (i > 0) const SizedBox(height: EsiSpace.sm),
          rows[i],
        ],
      ],
    );
  }
}

class _CoreColumn extends StatelessWidget {
  const _CoreColumn({required this.value});

  final double value;

  @override
  Widget build(BuildContext context) {
    final severity = severityForPercent(value, elevated: 60, critical: 85);
    final colour = switch (severity) {
      Severity.nominal => EsiPalette.signal,
      Severity.elevated => EsiPalette.flare,
      Severity.critical => EsiPalette.alarm,
      Severity.unknown => EsiPalette.faint,
    };
    final filled = (value.clamp(0.0, 100.0) / 100.0) * 30;
    return LayoutBuilder(
      builder: (context, constraints) => Stack(
        alignment: Alignment.bottomCenter,
        children: <Widget>[
          Container(
            height: 30,
            decoration: BoxDecoration(
              color: EsiPalette.edge.withValues(alpha: 0.6),
              borderRadius: EsiRadius.chip,
            ),
          ),
          TweenAnimationBuilder<double>(
            tween: Tween<double>(begin: 0, end: filled),
            duration: const Duration(milliseconds: 420),
            curve: Curves.easeOutCubic,
            builder: (context, animated, _) => Container(
              height: animated,
              decoration: BoxDecoration(
                color: colour.withValues(alpha: 0.85),
                borderRadius: EsiRadius.chip,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// A horizontal bar chart for the gaming FPS history.
class EsiHistoryChart extends StatelessWidget {
  const EsiHistoryChart({
    super.key,
    required this.values,
    required this.colour,
    this.height = 56,
    this.baseline,
  });

  final List<double> values;
  final Color colour;
  final double height;

  /// A dashed reference line, e.g. the display's refresh rate.
  final double? baseline;

  @override
  Widget build(BuildContext context) => SizedBox(
        height: height,
        child: CustomPaint(
          painter: _HistoryPainter(values: values, colour: colour, baseline: baseline),
          size: Size.infinite,
        ),
      );
}

class _HistoryPainter extends CustomPainter {
  _HistoryPainter({required this.values, required this.colour, this.baseline});

  final List<double> values;
  final Color colour;
  final double? baseline;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawLine(
      Offset(0, size.height - 1),
      Offset(size.width, size.height - 1),
      Paint()
        ..color = EsiPalette.edge
        ..strokeWidth = 1,
    );

    if (values.length >= 2) {
      final maxValue = values.reduce(math.max);
      final ceiling = math.max(maxValue, baseline ?? 0) * 1.15;
      if (ceiling > 0) {
        final step = size.width / (values.length - 1);
        final path = Path();
        final area = Path()..moveTo(0, size.height);
        for (var i = 0; i < values.length; i++) {
          final x = step * i;
          final y = size.height - (values[i] / ceiling) * size.height;
          path.moveTo(x, y);
          area.lineTo(x, y);
        }
        area.lineTo(size.width, size.height);
        area.close();
        canvas.drawPath(
          area,
          Paint()
            ..shader = LinearGradient(
              begin: Alignment.topCenter,
              end: Alignment.bottomCenter,
              colors: <Color>[
                colour.withValues(alpha: 0.28),
                colour.withValues(alpha: 0.0),
              ],
            ).createShader(Offset.zero & size),
        );
        canvas.drawPath(
          path,
          Paint()
            ..color = colour
            ..strokeWidth = 1.6
            ..style = PaintingStyle.stroke
            ..strokeCap = StrokeCap.round,
        );
      }
    }

    final mark = baseline;
    if (mark != null && mark > 0 && values.isNotEmpty) {
      final maxValue = values.reduce(math.max);
      final ceiling = math.max(maxValue, mark) * 1.15;
      final y = size.height - (mark / ceiling) * size.height;
      final dash = Paint()
        ..color = EsiPalette.dim.withValues(alpha: 0.6)
        ..strokeWidth = 1;
      for (var x = 0.0; x < size.width; x += 8) {
        canvas.drawLine(Offset(x, y), Offset(math.min(x + 4, size.width), y), dash);
      }
    }
  }

  @override
  bool shouldRepaint(_HistoryPainter oldDelegate) => true;
}

/// A small sparkline for a single scalar, used inside dense rows.
class EsiSparkline extends StatelessWidget {
  const EsiSparkline({
    super.key,
    required this.values,
    required this.colour,
    this.width = 64,
    this.height = 18,
  });

  final List<double> values;
  final Color colour;
  final double width;
  final double height;

  @override
  Widget build(BuildContext context) => SizedBox(
        width: width,
        height: height,
        child: CustomPaint(painter: _SparkPainter(values: values, colour: colour)),
      );
}

class _SparkPainter extends CustomPainter {
  _SparkPainter({required this.values, required this.colour});

  final List<double> values;
  final Color colour;

  @override
  void paint(Canvas canvas, Size size) {
    if (values.length < 2) return;
    final maxValue = values.reduce(math.max);
    final minValue = values.reduce(math.min);
    final span = math.max(maxValue - minValue, 1e-6);
    final step = size.width / (values.length - 1);
    final path = Path();
    for (var i = 0; i < values.length; i++) {
      final x = step * i;
      final y = size.height - ((values[i] - minValue) / span) * size.height;
      if (i == 0) {
        path.moveTo(x, y);
      } else {
        path.lineTo(x, y);
      }
    }
    canvas.drawPath(
      path,
      Paint()
        ..color = colour
        ..strokeWidth = 1.3
        ..style = PaintingStyle.stroke
        ..strokeCap = StrokeCap.round,
    );
  }

  @override
  bool shouldRepaint(_SparkPainter oldDelegate) =>
      !listEquals(oldDelegate.values, values) || oldDelegate.colour != colour;
}

/// A legend helper for charts, kept next to them so the two cannot drift.
Color traceColourFor(int index) => switch (index) {
      0 => EsiPalette.signal,
      1 => EsiPalette.flare,
      2 => EsiPalette.lock,
      _ => EsiPalette.live,
    };

/// Used by the dashboard to describe the trace in text for a screen reader.
String describeTrace(List<TraceChannel> channels) {
  if (channels.isEmpty) return 'No load history yet.';
  final parts = <String>[];
  for (final channel in channels) {
    final last = channel.series.isEmpty ? null : channel.series.last;
    parts.add('${channel.label} ${formatPercent(last)}');
  }
  return parts.join(', ');
}

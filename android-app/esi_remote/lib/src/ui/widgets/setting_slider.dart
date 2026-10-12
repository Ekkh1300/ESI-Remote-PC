import 'dart:async';

import 'package:flutter/material.dart';

import '../theme.dart';

/// A slider that follows the finger instead of the server.
///
/// The obvious implementation - `value: serverValue, onChanged: (v) => send(v)` - is the one
/// this replaces, and it is unusable for anything the PC has to act on. Three faults, all
/// visible in one gesture:
///
/// First, <b>every pixel of travel is a request</b>. Dragging across the track fires the
/// callback once per division, so a single swipe across a 0-100 slider posts a hundred times.
/// Each one waits for a round trip, and the responses come back out of order, so the final
/// value is whichever reply happened to land last rather than the one the finger ended on.
///
/// Second, <b>the thumb fights the user</b>. `value` is the server's copy, so between a drag
/// and the reply carrying the new value, the thumb springs back to where it was. Dragging
/// right feels like pushing something heavy that keeps trying to return.
///
/// Third, <b>a stalled network looks like a stuck control</b>, because the whole widget is
/// waiting on a chain of round trips before it will move again.
///
/// So the thumb reads a local value while a drag is in progress and hands the final number back
/// to the server only when the finger lifts. The server stays the source of truth between
/// gestures, which is what [value] is for.
class EsiSettingSlider extends StatefulWidget {
  const EsiSettingSlider({
    super.key,
    required this.value,
    required this.min,
    required this.max,
    required this.onCommit,
    this.onPreview,
    this.divisions,
    this.enabled = true,
    this.labelBuilder,
    this.debounce = const Duration(milliseconds: 90),
  });

  /// The server's value. Shown whenever no drag is in progress.
  final double value;

  final double min;
  final double max;

  /// Called once, on release, with the value the finger landed on.
  ///
  /// Awaited. When it completes, the thumb hands control back to the server's value - so the
  /// control always ends up showing what the PC actually did, even when the PC refused, clamped
  /// the value, or rounded it to something else. Without that the thumb would keep showing a
  /// number this app invented and the two would sit there disagreeing until the next gesture.
  final FutureOr<void> Function(double value) onCommit;

  /// Called during the drag, at most once per [debounce]. Optional.
  ///
  /// A preview that reaches the PC is what makes a long drag feel continuous rather than
  /// springing at the end, but it is optional because it costs a request per debounce window
  /// and some settings are not worth that.
  final ValueChanged<double>? onPreview;

  final int? divisions;
  final bool enabled;

  /// Builds the bubble label. Defaults to the value rounded to an integer.
  final String Function(double value)? labelBuilder;

  final Duration debounce;

  @override
  State<EsiSettingSlider> createState() => _EsiSettingSliderState();
}

class _EsiSettingSliderState extends State<EsiSettingSlider> {
  /// The value the thumb is showing. Null means "show the server's".
  double? _drag;

  Timer? _pending;

  /// True from the moment the finger lifts until the server's value has been applied and read
  /// back. While it holds, a refresh landing from elsewhere is ignored: that is exactly the
  /// reply from before the drag, and letting it through is the spring-back all over again.
  bool _settling = false;

  @override
  void didUpdateWidget(EsiSettingSlider oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!_settling && _drag != null && oldWidget.value == widget.value) {
      // The server came back to the value the drag started from, which means it never accepted
      // the new one. Showing it is the honest thing.
      _release();
    }
  }

  void _release() {
    _pending?.cancel();
    _pending = null;
    if (_drag == null && !_settling) return;
    setState(() {
      _drag = null;
      _settling = false;
    });
  }

  void _onChanged(double value) {
    setState(() => _drag = value);

    final preview = widget.onPreview;
    if (preview == null) return;

    _pending?.cancel();
    _pending = Timer(widget.debounce, () {
      _pending = null;
      preview(value);
    });
  }

  Future<void> _onChangeEnd(double value) async {
    _pending?.cancel();
    _pending = null;
    setState(() {
      _drag = value;
      _settling = true;
    });

    try {
      await widget.onCommit(value);
    } finally {
      // Hand the thumb back whatever the server says, even if the commit threw. Leaving the
      // local value pinned here is how a control ends up showing a number that never happened.
      if (mounted) _release();
    }
  }

  void _onChangeStart(double value) {
    setState(() => _drag = value);
  }

  @override
  void dispose() {
    _pending?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final shown = _drag ?? widget.value.clamp(widget.min, widget.max);

    return SliderTheme(
      data: SliderTheme.of(context).copyWith(
        trackHeight: 3,
        inactiveTrackColor: EsiPalette.edge,
        activeTrackColor: widget.enabled ? EsiPalette.signal : EsiPalette.faint,
        thumbColor: widget.enabled ? EsiPalette.signal : EsiPalette.faint,
        overlayColor: EsiPalette.signal.withValues(alpha: 0.12),
        // The material default grows a halo the width of the track on every press, which on a
        // 3-pixel track turns the whole control into a glowing bar the moment it is touched.
        overlayShape: const RoundSliderOverlayShape(overlayRadius: 14),
        thumbShape: const RoundSliderThumbShape(enabledThumbRadius: 7),
      ),
      child: Slider(
        value: shown,
        min: widget.min,
        max: widget.max,
        divisions: widget.divisions,
        label: widget.labelBuilder?.call(shown) ?? shown.round().toString(),
        onChangeStart: widget.enabled ? _onChangeStart : null,
        onChanged: widget.enabled ? _onChanged : null,
        onChangeEnd: widget.enabled ? _onChangeEnd : null,
      ),
    );
  }
}

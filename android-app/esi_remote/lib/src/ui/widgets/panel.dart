import 'dart:ui' as ui;

import 'package:flutter/material.dart';

import '../format.dart';
import '../theme.dart';

/// A frosted panel with a signal tick, a title, and an optional trailing control.
///
/// The tick is the app's structural device: a short coloured bar in the top-left whose
/// colour is the subsystem's severity. It is 2×22 px, so a column of panels forms a
/// legible status edge without any of them shouting.
class EsiPanel extends StatelessWidget {
  const EsiPanel({
    super.key,
    required this.title,
    required this.child,
    this.severity = Severity.nominal,
    this.subtitle,
    this.trailing,
    this.footnote,
    this.onTap,
    this.padding = const EdgeInsets.fromLTRB(EsiSpace.lg, EsiSpace.lg, EsiSpace.lg, EsiSpace.md),
    this.tick = true,
  });

  final String title;
  final String? subtitle;
  final Widget child;

  /// Drives the tick colour. [Severity.unknown] gets its own muted treatment so a panel
  /// with no data never looks like a panel with good data.
  final Severity severity;

  final Widget? trailing;
  final String? footnote;
  final VoidCallback? onTap;
  final EdgeInsets padding;
  final bool tick;

  @override
  Widget build(BuildContext context) {
    final body = Stack(
      children: <Widget>[
        Padding(
          padding: tick ? padding.add(const EdgeInsets.only(left: EsiSpace.sm)) : padding,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              _header(),
              if (subtitle != null) ...<Widget>[
                const SizedBox(height: EsiSpace.xs),
                Text(subtitle!, style: EsiType.body.copyWith(color: EsiPalette.faint)),
              ],
              const SizedBox(height: EsiSpace.md),
              child,
              if (footnote != null) ...<Widget>[
                const SizedBox(height: EsiSpace.sm),
                Text(
                  footnote!,
                  style: EsiType.mono.copyWith(color: EsiPalette.faint, height: 1.35),
                ),
              ],
            ],
          ),
        ),
        if (tick)
          Positioned(
            left: 0,
            top: EsiSpace.lg - 2,
            child: _SignalTick(severity: severity),
          ),
      ],
    );

    return EsiSurface(
      padding: EdgeInsets.zero,
      child: onTap == null
          ? body
          : Material(
              type: MaterialType.transparency,
              child: InkWell(
                onTap: onTap,
                borderRadius: EsiRadius.panel,
                child: body,
              ),
            ),
    );
  }

  Widget _header() => Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: <Widget>[
          Expanded(
            child: Text(
              title,
              style: EsiType.title.copyWith(fontSize: 15),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          if (trailing != null) ...<Widget>[
            const SizedBox(width: EsiSpace.sm),
            trailing!,
          ],
        ],
      );
}

class _SignalTick extends StatelessWidget {
  const _SignalTick({required this.severity});

  final Severity severity;

  @override
  Widget build(BuildContext context) {
    final colour = switch (severity) {
      Severity.nominal => EsiPalette.signal,
      Severity.elevated => EsiPalette.flare,
      Severity.critical => EsiPalette.alarm,
      // Unknown is not good news, it is no news. Muted, and hollow, so it cannot be
      // mistaken for a healthy reading.
      Severity.unknown => EsiPalette.faint,
    };
    return Container(
      width: 2,
      height: 22,
      decoration: BoxDecoration(
        color: severity == Severity.unknown ? null : colour,
        borderRadius: EsiRadius.chip,
        border: severity == Severity.unknown
            ? Border.all(color: colour, width: 1)
            : null,
        boxShadow: severity == Severity.nominal
            ? <BoxShadow>[
                BoxShadow(color: colour.withValues(alpha: 0.5), blurRadius: 6),
              ]
            : null,
      ),
    );
  }
}

/// A label on the left, a value on the right, on a shared baseline.
///
/// This is the app's most-used widget. The value is monospaced and tabular so a column of
/// them lines up, and the unit is dimmed and smaller so `15.4 GB` does not read as one
/// blob.
class EsiReadout extends StatelessWidget {
  const EsiReadout({
    super.key,
    required this.label,
    this.value,
    this.unit,
    this.severity = Severity.nominal,
    this.emphasised = false,
    this.trailing,
    this.tooltip,
  });

  final String label;

  /// Null renders the em dash from [notReported]. Every nullable reading in the app goes
  /// through here, which is what makes "unreported" a first-class value rather than a
  /// special case somebody forgot.
  final String? value;

  final String? unit;
  final Severity severity;
  final bool emphasised;
  final Widget? trailing;
  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final reported = value != null && value!.isNotEmpty;
    final valueColour = switch (severity) {
      Severity.nominal => EsiPalette.ink,
      Severity.elevated => EsiPalette.flare,
      Severity.critical => EsiPalette.alarm,
      Severity.unknown => EsiPalette.faint,
    };
    final row = Padding(
      padding: const EdgeInsets.symmetric(vertical: EsiSpace.xs + 1),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.baseline,
        textBaseline: TextBaseline.alphabetic,
        children: <Widget>[
          Expanded(
            child: Text(
              label,
              style: EsiType.body.copyWith(
                color: EsiPalette.dim,
                fontSize: 12.5,
              ),
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          const SizedBox(width: EsiSpace.sm),
          if (trailing != null) ...<Widget>[trailing!, const SizedBox(width: EsiSpace.sm)],
          Text(
            reported ? value! : notReported,
            style: (emphasised ? EsiType.reading : EsiType.readingSmall).copyWith(
              color: reported ? valueColour : EsiPalette.faint,
              fontSize: emphasised ? 17 : 13,
            ),
          ),
          if (unit != null && reported) ...<Widget>[
            const SizedBox(width: 3),
            Text(
              unit!,
              style: EsiType.readingSmall.copyWith(
                color: EsiPalette.faint,
                fontSize: 10.5,
              ),
            ),
          ],
        ],
      ),
    );

    if (tooltip == null) return row;
    return Tooltip(message: tooltip!, child: row);
  }
}

/// A stacked list of readouts with a hairline between each. Used inside a panel so the
/// spacing is identical everywhere.
class EsiReadoutList extends StatelessWidget {
  const EsiReadoutList({super.key, required this.children, this.dense = true});

  final List<Widget> children;
  final bool dense;

  @override
  Widget build(BuildContext context) {
    final rows = <Widget>[];
    for (var i = 0; i < children.length; i++) {
      if (i > 0 && !dense) const SizedBox(height: EsiSpace.sm);
      rows.add(children[i]);
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: rows,
    );
  }
}

/// A thin horizontal meter.
///
/// A filled bar over a recessed track, with an optional marker at the previous reading so
/// a spike leaves a visible trace and a glance catches it. Deliberately not animated on
/// the data itself: a bar that eases to every new value turns a 1 Hz stream into a blur.
class EsiBar extends StatelessWidget {
  const EsiBar({
    super.key,
    required this.fraction,
    this.severity = Severity.nominal,
    this.height = 4,
    this.marker,
  });

  /// 0..1. Null renders an empty track with a stub at the left, never a partial fill —
  /// an unreported reading must not look like a small one.
  final double? fraction;
  final Severity severity;
  final double height;

  /// The previous reading, drawn as a hairline so a change is visible.
  final double? marker;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) {
          final colour = switch (severity) {
            Severity.nominal => EsiPalette.signal,
            Severity.elevated => EsiPalette.flare,
            Severity.critical => EsiPalette.alarm,
            Severity.unknown => EsiPalette.faint,
          };
          final value = fraction?.clamp(0.0, 1.0);
          final ghost = marker?.clamp(0.0, 1.0);
          return SizedBox(
            height: height,
            child: Stack(
              children: <Widget>[
                Positioned.fill(
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      color: EsiPalette.edge,
                      borderRadius: EsiRadius.chip,
                    ),
                  ),
                ),
                if (value != null)
                  Positioned.fill(
                    child: Align(
                      alignment: Alignment.centerLeft,
                      child: TweenAnimationBuilder<double>(
                        tween: Tween<double>(begin: 0, end: value),
                        duration: const Duration(milliseconds: 420),
                        curve: Curves.easeOutCubic,
                        builder: (context, animated, _) => FractionallySizedBox(
                          widthFactor: animated,
                          child: DecoratedBox(
                            decoration: BoxDecoration(
                              color: colour,
                              borderRadius: EsiRadius.chip,
                              boxShadow: <BoxShadow>[
                                BoxShadow(
                                  color: colour.withValues(alpha: 0.4),
                                  blurRadius: 8,
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ),
                  )
                else
                  Positioned(
                    left: 0,
                    top: 0,
                    bottom: 0,
                    width: 2,
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        color: EsiPalette.faint.withValues(alpha: 0.45),
                        borderRadius: EsiRadius.chip,
                      ),
                    ),
                  ),
                if (ghost != null && value != null)
                  Positioned.fill(
                    child: Align(
                      alignment: Alignment.centerLeft,
                      child: FractionallySizedBox(
                        widthFactor: ghost,
                        child: DecoratedBox(
                          decoration: BoxDecoration(
                            border: Border(
                              right: BorderSide(
                                color: colour.withValues(alpha: 0.45),
                                width: 1,
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          );
        },
      );
}

/// A small status tag: a rounded chip with a dot and a word. Used for pin verification,
/// connection state, feature availability.
class EsiTag extends StatelessWidget {
  const EsiTag({
    super.key,
    required this.label,
    this.colour = EsiPalette.dim,
    this.icon,
    this.filled = false,
    this.tooltip,
  });

  final String label;
  final Color colour;
  final IconData? icon;
  final bool filled;
  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final tag = Container(
      padding: const EdgeInsets.symmetric(horizontal: EsiSpace.sm, vertical: 3),
      decoration: BoxDecoration(
        color: filled ? colour.withValues(alpha: 0.16) : Colors.transparent,
        borderRadius: EsiRadius.tag,
        border: Border.all(color: colour.withValues(alpha: filled ? 0.5 : 0.35)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          if (icon != null) ...<Widget>[
            Icon(icon, size: 11, color: colour),
            const SizedBox(width: EsiSpace.xs + 1),
          ] else ...<Widget>[
            Container(
              width: 5,
              height: 5,
              decoration: BoxDecoration(color: colour, borderRadius: EsiRadius.chip),
            ),
            const SizedBox(width: EsiSpace.xs + 2),
          ],
          Text(
            label,
            style: EsiType.label.copyWith(color: colour, fontSize: 9.5),
          ),
        ],
      ),
    );
    if (tooltip == null) return tag;
    return Tooltip(message: tooltip!, child: tag);
  }
}

/// A row of readouts with a hero number on the left, for the dashboard's primary panels.
class EsiHeroReadout extends StatelessWidget {
  const EsiHeroReadout({
    super.key,
    required this.label,
    this.value,
    this.unit,
    this.severity = Severity.nominal,
    this.bar,
  });

  final String label;
  final String? value;
  final String? unit;
  final Severity severity;
  final Widget? bar;

  @override
  Widget build(BuildContext context) {
    final colour = switch (severity) {
      Severity.nominal => EsiPalette.ink,
      Severity.elevated => EsiPalette.flare,
      Severity.critical => EsiPalette.alarm,
      Severity.unknown => EsiPalette.faint,
    };
    final reported = value != null && value!.isNotEmpty;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Text(label, style: EsiType.label),
        const SizedBox(height: EsiSpace.sm),
        Row(
          crossAxisAlignment: CrossAxisAlignment.baseline,
          textBaseline: TextBaseline.alphabetic,
          children: <Widget>[
            Text(
              reported ? value! : notReported,
              style: EsiType.hero.copyWith(color: reported ? colour : EsiPalette.faint),
            ),
            if (unit != null && reported) ...<Widget>[
              const SizedBox(width: EsiSpace.xs + 2),
              Text(
                unit!,
                style: EsiType.readingSmall.copyWith(
                  color: EsiPalette.faint,
                  fontSize: 12,
                ),
              ),
            ],
          ],
        ),
        if (bar != null) ...<Widget>[
          const SizedBox(height: EsiSpace.md),
          bar!,
        ],
      ],
    );
  }
}

/// A key/value line for settings and detail sheets. Mono on the right, because these are
/// ids, paths and pins.
class EsiDetailRow extends StatelessWidget {
  const EsiDetailRow({
    super.key,
    required this.label,
    this.value,
    this.monospace = true,
    this.valueColour,
    this.trailing,
  });

  final String label;
  final String? value;
  final bool monospace;
  final Color? valueColour;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final reported = value != null && value!.isNotEmpty;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: EsiSpace.sm - 1),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          SizedBox(
            width: 116,
            child: Text(
              label,
              style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
            ),
          ),
          const SizedBox(width: EsiSpace.sm),
          Expanded(
            child: Text(
              reported ? value! : notReported,
              style: (monospace ? EsiType.mono : EsiType.body)
                  .copyWith(color: valueColour ?? (reported ? EsiPalette.ink : EsiPalette.faint)),
            ),
          ),
          if (trailing != null) ...<Widget>[const SizedBox(width: EsiSpace.sm), trailing!],
        ],
      ),
    );
  }
}

/// A blurred scrim behind modals, so a dialog reads as floating over the panel glass
/// rather than pasted on top of it.
class EsiScrim extends StatelessWidget {
  const EsiScrim({super.key, required this.child, this.onTap});

  final Widget child;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => GestureDetector(
        onTap: onTap,
        behavior: HitTestBehavior.opaque,
        child: BackdropFilter(
          filter: ui.ImageFilter.blur(sigmaX: 22, sigmaY: 22),
          child: Container(color: EsiPalette.void_.withValues(alpha: 0.72)),
        ),
      );
}

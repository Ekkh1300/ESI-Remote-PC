import 'package:flutter/material.dart';

import '../theme.dart';

/// One entry in the navigation rail.
///
/// Public because the rail is: a test that measures a *copy* of this layout measures the copy,
/// and the copy is exactly the thing that stops being true when the rail changes. See
/// `test/ui/nav_rail_test.dart`, which measures this class and not a stand-in.
class EsiNavDestination {
  const EsiNavDestination({
    required this.label,
    required this.icon,
    required this.selectedIcon,
    this.pinnedToBottom = false,
  });

  final String label;
  final IconData icon;
  final IconData selectedIcon;

  /// Whether this one sits at the foot of the rail instead of in the stack with the rest.
  ///
  /// Control is pinned because it is the tab you go to deliberately, from somewhere else,
  /// rather than one you step through on the way to something. Spreading every destination
  /// evenly down the rail put half a phone of empty space between the peers and made the rail
  /// read as five unrelated things rather than as "here are the peers, and down here is the
  /// other thing".
  final bool pinnedToBottom;
}

/// The persistent navigation rail down the left of every screen.
///
/// A rail rather than a bottom bar, because this is a control surface for a machine rather than
/// a phone-first consumer app: the destinations are peers of each other, and a vertical rail
/// leaves the full width for the readouts, which are the thing that matters.
class EsiNavRail extends StatelessWidget {
  const EsiNavRail({
    super.key,
    required this.destinations,
    required this.currentIndex,
    required this.onSelect,
    this.unread = 0,
    this.online = true,
    this.showUnreadOn,
  });

  /// Icon, the selection tick above it, and the label under it, plus the gap between.
  ///
  /// Fixed rather than flexible on purpose. Giving every destination an equal `Expanded` slice
  /// of a tall phone spread the four peers apart with a finger's width of dead space between
  /// each, which is what the layout looked like before this was pinned down by a test.
  ///
  /// 68 rather than something tighter, because the label inside is text and text grows. At the
  /// default size this column's contents measure about 64, so a smaller number looks fine here
  /// and overflows on the phone whose user has turned their font size up - which is a real
  /// screen full of people, and the failure is a clipped label on a control they cannot read.
  /// `nav_rail_test.dart` renders this at 1.5x and 2x to keep that honest.
  static const double buttonHeight = 68;

  static const double width = 74;

  final List<EsiNavDestination> destinations;
  final int currentIndex;
  final ValueChanged<int> onSelect;
  final int unread;
  final bool online;

  /// Which destination carries the unread badge. Only one can usefully.
  ///
  /// Matched by label rather than by index so that reordering the list cannot silently move
  /// the badge onto a different tab.
  final String? showUnreadOn;

  @override
  Widget build(BuildContext context) {
    final stacked = <EsiNavDestination>[
      for (final d in destinations)
        if (!d.pinnedToBottom) d,
    ];
    final pinned = <EsiNavDestination>[
      for (final d in destinations)
        if (d.pinnedToBottom) d,
    ];

    // A minimum rather than a fixed height. The label inside is text, and text grows with the
    // user's font-size setting; a fixed box clips it at 2x, which costs the entry its name on
    // exactly the rail that tells you which PC you are on. The rail as a whole decides whether
    // there is room, below.
    Widget button(EsiNavDestination destination) => ConstrainedBox(
          constraints: const BoxConstraints(minHeight: buttonHeight),
          child: _RailButton(
            destination: destination,
            selected: destinations.indexOf(destination) == currentIndex,
            unread: destination.label == showUnreadOn ? unread : 0,
            online: online,
            onTap: () => onSelect(destinations.indexOf(destination)),
          ),
        );

    final stackedButtons = <Widget>[for (final d in stacked) button(d)];
    final pinnedButtons = <Widget>[for (final d in pinned) button(d)];
    final chrome = EsiSpace.md + EsiSpace.sm;

    return Container(
      width: width,
      color: EsiPalette.void_.withValues(alpha: 0.6),
      child: SafeArea(
        right: false,
        // Two layouts, chosen by the height actually available.
        //
        // The roomy one puts the pinned entry at the foot with a Spacer above it. The tight one
        // drops the Spacer and scrolls, because the alternative is a RenderFlex overflow - which
        // Flutter reports as an error, paints as a striped bar, and silently clips whatever
        // entry fell past the bottom. A short window is rare but real: a phone in landscape, a
        // split screen, a freeform window dragged short.
        child: LayoutBuilder(
          builder: (context, constraints) {
            final needed = stackedButtons.length * buttonHeight +
                pinnedButtons.length * buttonHeight +
                chrome;
            final roomy = pinnedButtons.isEmpty || constraints.maxHeight >= needed;

            if (!roomy) {
              return SingleChildScrollView(
                child: Column(
                  children: <Widget>[
                    const SizedBox(height: EsiSpace.md),
                    ...stackedButtons,
                    ...pinnedButtons,
                    const SizedBox(height: EsiSpace.sm),
                  ],
                ),
              );
            }

            return Column(
              children: <Widget>[
                const SizedBox(height: EsiSpace.md),
                ...stackedButtons,
                // Between the stack and the pinned entry, so the pinned one lands at the foot. A
                // Spacer placed first would push everything else down instead, which is subtle
                // enough to look right in a screenshot and wrong in a measurement.
                if (pinnedButtons.isNotEmpty) const Spacer(),
                ...pinnedButtons,
                const SizedBox(height: EsiSpace.sm),
              ],
            );
          },
        ),
      ),
    );
  }
}

class _RailButton extends StatelessWidget {
  const _RailButton({
    required this.destination,
    required this.selected,
    required this.onTap,
    required this.online,
    this.unread = 0,
  });

  final EsiNavDestination destination;
  final bool selected;
  final VoidCallback onTap;
  final bool online;
  final int unread;

  @override
  Widget build(BuildContext context) {
    final colour = selected ? EsiPalette.signal : EsiPalette.dim;
    return Semantics(
      selected: selected,
      button: true,
      label: unread > 0 ? '${destination.label}, $unread unread' : destination.label,
      child: InkWell(
        onTap: onTap,
        child: SizedBox(
          width: double.infinity,
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: <Widget>[
              // The selected marker is a bar in the gutter, matching the panel signal
              // ticks: the same structural device everywhere.
              AnimatedContainer(
                duration: const Duration(milliseconds: 180),
                width: selected ? 3 : 0,
                height: 18,
                decoration: BoxDecoration(
                  color: EsiPalette.signal,
                  borderRadius: EsiRadius.chip,
                ),
              ),
              const SizedBox(height: 4),
              Stack(
                clipBehavior: Clip.none,
                children: <Widget>[
                  Icon(
                    selected ? destination.selectedIcon : destination.icon,
                    size: 21,
                    color: online ? colour : EsiPalette.faint,
                  ),
                  if (unread > 0)
                    Positioned(
                      right: -7,
                      top: -5,
                      child: Container(
                        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                        constraints: const BoxConstraints(minWidth: 15),
                        decoration: BoxDecoration(
                          color: EsiPalette.alarm,
                          borderRadius: EsiRadius.chip,
                        ),
                        child: Text(
                          unread > 99 ? '99+' : '$unread',
                          textAlign: TextAlign.center,
                          style: EsiType.label.copyWith(
                            color: EsiPalette.void_,
                            fontSize: 8.5,
                            letterSpacing: 0,
                          ),
                        ),
                      ),
                    ),
                ],
              ),
              const SizedBox(height: 3),
              Text(
                destination.label,
                style: EsiType.label.copyWith(
                  color: selected ? EsiPalette.ink : EsiPalette.faint,
                  fontSize: 8.5,
                  letterSpacing: 0.3,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

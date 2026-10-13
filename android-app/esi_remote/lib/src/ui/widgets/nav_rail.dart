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
    // exactly the rail that tells you which PC you are on. Nothing here predicts the result -
    // the peers simply scroll inside whatever room there is, below.
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
        child: Column(
          children: <Widget>[
            const SizedBox(height: EsiSpace.md),
            // The peers scroll; the pinned entry does not, and that is the whole layout.
            //
            // This used to be a branch: if the rail was tall enough for every button, use a
            // Column with a Spacer; otherwise fall back to scrolling the lot. The test that
            // decided which to use multiplied a fixed buttonHeight by the button count, which is
            // a prediction made before the buttons are measured - they carry a *min*Height and
            // their contents are text, so a large font setting makes every one of them taller
            // than the number that predicted them. Being wrong in one direction ran the roomy
            // layout on a window that could not hold it and pushed Control past the bottom edge;
            // being wrong the other way ran the scrollable layout, which had no Spacer at all
            // and left Control sitting directly under Screen.
            //
            // Putting the scroll region inside an Expanded instead makes the outcome
            // independent of the prediction. There is no threshold to get wrong: the peers take
            // what room there is and scroll inside it, and the pinned entry follows immediately
            // after, so it is at the foot of the rail on a 900px window and on a 220px one.
            Expanded(
              child: SingleChildScrollView(
                child: Column(
                  children: <Widget>[...stackedButtons],
                ),
              ),
            ),
            if (pinnedButtons.isNotEmpty) ...pinnedButtons,
            const SizedBox(height: EsiSpace.sm),
          ],
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

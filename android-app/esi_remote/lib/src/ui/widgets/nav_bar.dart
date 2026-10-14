import 'package:flutter/material.dart';

import '../theme.dart';

/// One entry in the navigation bar.
///
/// Public because the bar is: a test that measures a *copy* of this layout measures the copy,
/// and the copy is exactly the thing that stops being true when the bar changes. See
/// `test/ui/nav_bar_test.dart`, which measures this class and not a stand-in.
class EsiNavDestination {
  const EsiNavDestination({
    required this.label,
    required this.icon,
    required this.selectedIcon,
  });

  final String label;
  final IconData icon;
  final IconData selectedIcon;
}

/// The persistent navigation bar across the foot of every screen.
///
/// A bar across the bottom rather than a rail down the left. The rail version of this bought
/// vertical room for five labels by giving up a strip of the phone's width on *every* screen,
/// and on a 360-wide phone that strip is a fifth of the reading area - which is the thing the
/// rail was supposed to be protecting. Held in the thumb, a bar is also where a hand already
/// is; a rail on the left edge is where a hand has to travel to.
///
/// The destinations are peers of each other, so they are laid out as peers: five equal slots,
/// no pinning. Control used to be pinned to the foot of the rail, which bought it a statement
/// about "the other thing" in exchange for a large dead gap between it and its four peers. In
/// a horizontal bar the peers are contiguous by construction and Control simply sits last, so
/// the statement costs nothing and the gap is gone.
class EsiBottomBar extends StatelessWidget {
  const EsiBottomBar({
    super.key,
    required this.destinations,
    required this.currentIndex,
    required this.onSelect,
    this.unread = 0,
    this.online = true,
    this.showUnreadOn,
  });

  /// The height of the bar's own content, above the gesture-bar inset.
  ///
  /// Fixed rather than intrinsic. This is chrome the shell owns; letting it grow with the
  /// user's font-size setting would make the reading area below it resize as text scales,
  /// and the thing that resizes would be the region that was not asked about. The label
  /// inside shrinks instead (see the `FittedBox` in [_BarButton]), so the bar never clips a
  /// name it cannot afford to lose.
  static const double height = 58;

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
    return Container(
      decoration: const BoxDecoration(
        color: EsiPalette.void_,
        border: Border(top: BorderSide(color: EsiPalette.edge)),
      ),
      // The bar owns the bottom safe area for the whole shell. It has to be exactly one place:
      // the screens above it used to apply their own bottom inset, and with a bar below them
      // that inset would land *above* the bar as a band of dead space with nothing in it.
      child: SafeArea(
        top: false,
        child: SizedBox(
          height: height,
          child: Row(
            children: <Widget>[
              for (var i = 0; i < destinations.length; i++)
                Expanded(
                  child: _BarButton(
                    destination: destinations[i],
                    selected: i == currentIndex,
                    unread: destinations[i].label == showUnreadOn ? unread : 0,
                    online: online,
                    onTap: () => onSelect(i),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _BarButton extends StatelessWidget {
  const _BarButton({
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
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: <Widget>[
            // The selected marker, matching the panel signal ticks: the same structural device
            // everywhere, stood on end for a horizontal bar. It always occupies its row, so
            // selecting a tab moves nothing - only the marker's colour and width change.
            Container(
              width: 24,
              height: 2.5,
              decoration: BoxDecoration(
                color: selected ? EsiPalette.signal : Colors.transparent,
                borderRadius: EsiRadius.chip,
              ),
            ),
            const SizedBox(height: 6),
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
                    right: -8,
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
            const SizedBox(height: 4),
            // `scaleDown` rather than an ellipsis. Five slots across a 360-wide phone leaves
            // about 70 logical pixels each, and "Dashboard" at the user's largest font setting
            // does not fit in 70. Shrinking keeps the whole word; truncating would hide which
            // tab is which on exactly the control whose job is saying which tab is which.
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 3),
              child: FittedBox(
                fit: BoxFit.scaleDown,
                child: Text(
                  destination.label,
                  maxLines: 1,
                  style: EsiType.label.copyWith(
                    color: selected ? EsiPalette.ink : EsiPalette.faint,
                    fontSize: 9.5,
                    letterSpacing: 0.3,
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

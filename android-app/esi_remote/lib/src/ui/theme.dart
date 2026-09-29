import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

/// The app's visual system.
///
/// The brief is a LAN remote control for a Windows workstation, so the content is
/// telemetry: numbers that change every second, and a live screen you can touch. The
/// design follows from that rather than from a generic dashboard kit:
///
/// * **Numbers are the content.** Every reading is set in a monospaced face and aligned
///   on a shared column, with the unit dimmed and smaller. Nothing is centred that could
///   be aligned.
/// * **Structure carries state.** Each panel has a short signal tick in its top-left whose
///   colour is that subsystem's severity, so the left edge of the screen alone tells you
///   whether anything needs attention.
/// * **Two temperatures, not one accent.** Aqua is nominal, amber is load, rose is
///   critical and violet is reserved for identity and key material. A dashboard with one
///   accent colour cannot say "warm" and "wrong" at the same time.
/// * **One memorable thing.** The load trace at the top of the dashboard is a real
///   oscilloscope fed by the metrics stream. Everything else is deliberately quiet.
///
/// The palette is a cool blue-black rather than a neutral grey-black, so that the amber
/// "hot" readings read as genuinely warm against it.
class EsiPalette {
  const EsiPalette._();

  /// The deepest background. Also the Android launch window colour.
  static const Color void_ = Color(0xFF05070C);

  /// Panel fill, one step up from the background.
  static const Color hull = Color(0xFF0A0F18);

  /// A raised surface: dialogs, sheets, the nav rail.
  static const Color deck = Color(0xFF111823);

  /// Hairlines and borders. Structure, not decoration.
  static const Color edge = Color(0xFF1C2634);

  /// A brighter hairline, for the top edge of a glass panel where light catches it.
  static const Color edgeBright = Color(0xFF2A394C);

  /// Primary text. Slightly cool, never pure white — pure white on a dark field buzzes.
  static const Color ink = Color(0xFFE3EAF4);

  /// Secondary text and units.
  static const Color dim = Color(0xFF8A99AF);

  /// Faintest text: axis labels, disabled states.
  static const Color faint = Color(0xFF55647A);

  /// Nominal data. The app's primary accent.
  static const Color signal = Color(0xFF4FD6C0);

  /// Load, heat, "look at this soon".
  static const Color flare = Color(0xFFFFB454);

  /// Critical, destructive, refused.
  static const Color alarm = Color(0xFFFF5D73);

  /// Identity, TLS pins, key material.
  static const Color lock = Color(0xFF9B8CFA);

  /// Cool blue used only for the "live" state of the status spine.
  static const Color live = Color(0xFF5AA9FF);

  /// Resolves a severity name used in the models to a concrete colour.
  static Color forName(String name) => switch (name) {
        'alarm' => alarm,
        'flare' => flare,
        'lock' => lock,
        'signal' => signal,
        'live' => live,
        'dim' => dim,
        'faint' => faint,
        'ink' => ink,
        'edge' => edge,
        'deck' => deck,
        'hull' => hull,
        _ => signal,
      };
}

/// Spacing, in logical pixels. A 4 pt base grid; nothing in the app uses a value that is
/// not on it.
class EsiSpace {
  const EsiSpace._();

  static const double xxs = 2;
  static const double xs = 4;
  static const double sm = 8;
  static const double md = 12;
  static const double lg = 16;
  static const double xl = 24;
  static const double xxl = 32;
  static const double huge = 48;
}

/// Corner radii. Deliberately varied: small radii for chips and tags, a medium one for
/// panels, a large one for sheets and dialogs. One radius everywhere is the tell of a
/// template.
class EsiRadius {
  const EsiRadius._();

  static const BorderRadius chip = BorderRadius.all(Radius.circular(4));
  static const BorderRadius tag = BorderRadius.all(Radius.circular(6));
  static const BorderRadius button = BorderRadius.all(Radius.circular(9));
  static const BorderRadius panel = BorderRadius.all(Radius.circular(12));
  static const BorderRadius dialog = BorderRadius.all(Radius.circular(18));
  static const BorderRadius sheet = BorderRadius.vertical(top: Radius.circular(22));
}

/// The two typefaces, and what each is for.
///
/// Space Grotesk is a technical grotesque with slightly odd, machine-drawn proportions;
/// it carries every piece of language in the app. JetBrains Mono carries every number,
/// path, identifier and pin, because those are read in columns and compared
/// character by character.
class EsiType {
  const EsiType._();

  static const String display = 'SpaceGrotesk';
  static const String data = 'JetBrainsMono';

  /// Micro label above a value or inside a tag. Tracked out so it reads as a label rather
  /// than as a word someone forgot to capitalise.
  static const TextStyle label = TextStyle(
        fontFamily: display,
        fontSize: 10,
        height: 1.1,
        fontWeight: FontWeight.w600,
        letterSpacing: 1.1,
        color: EsiPalette.faint,
      );

  static const TextStyle body = TextStyle(
        fontFamily: display,
        fontSize: 13.5,
        height: 1.45,
        fontWeight: FontWeight.w400,
        letterSpacing: 0.05,
        color: EsiPalette.ink,
      );

  static const TextStyle bodyStrong = TextStyle(
        fontFamily: display,
        fontSize: 13.5,
        height: 1.4,
        fontWeight: FontWeight.w600,
        color: EsiPalette.ink,
      );

  static const TextStyle subtitle = TextStyle(
        fontFamily: display,
        fontSize: 15,
        height: 1.3,
        fontWeight: FontWeight.w500,
        color: EsiPalette.ink,
      );

  /// A panel or section title.
  static const TextStyle title = TextStyle(
        fontFamily: display,
        fontSize: 17,
        height: 1.2,
        fontWeight: FontWeight.w600,
        letterSpacing: -0.2,
        color: EsiPalette.ink,
      );

  /// A screen title, used once per screen at the top of the content column.
  static const TextStyle screenTitle = TextStyle(
        fontFamily: display,
        fontSize: 26,
        height: 1.1,
        fontWeight: FontWeight.w600,
        letterSpacing: -0.6,
        color: EsiPalette.ink,
      );

  /// The hero reading. One number, large, unambiguous.
  static const TextStyle hero = TextStyle(
        fontFamily: data,
        fontSize: 40,
        height: 1.0,
        fontWeight: FontWeight.w500,
        letterSpacing: -1.5,
        color: EsiPalette.ink,
        fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
      );

  /// A reading in a panel.
  static const TextStyle reading = TextStyle(
        fontFamily: data,
        fontSize: 17,
        height: 1.1,
        fontWeight: FontWeight.w500,
        letterSpacing: -0.4,
        color: EsiPalette.ink,
        fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
      );

  static const TextStyle readingSmall = TextStyle(
        fontFamily: data,
        fontSize: 13,
        height: 1.15,
        fontWeight: FontWeight.w400,
        letterSpacing: -0.1,
        color: EsiPalette.dim,
        fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
      );

  /// A path, a device id, a pin, a base64 blob. Wraps and breaks anywhere.
  static const TextStyle mono = TextStyle(
        fontFamily: data,
        fontSize: 11.5,
        height: 1.5,
        fontWeight: FontWeight.w400,
        color: EsiPalette.dim,
      );

  static const TextStyle button = TextStyle(
        fontFamily: display,
        fontSize: 13.5,
        height: 1.2,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.1,
      );

  /// Used for the "not reported" em dash so it sits on the same baseline as a real value.
  static const TextStyle unreported = TextStyle(
        fontFamily: data,
        fontSize: 17,
        height: 1.1,
        fontWeight: FontWeight.w500,
        letterSpacing: -0.4,
        color: EsiPalette.faint,
        fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
      );
}

/// A frosted panel.
///
/// The glass is a translucent fill plus a 1 px top highlight plus a real backdrop blur,
/// which is what makes a dark surface read as glass rather than as a slightly lighter
/// rectangle. The blur radius is modest on purpose: too much and the text behind it turns
/// to soup.
class EsiGlass {
  const EsiGlass._();

  static const double blurSigma = 18;

  static BoxDecoration panel({
    Color tint = EsiPalette.hull,
    double opacity = 0.62,
    Color border = EsiPalette.edge,
    BorderRadius radius = EsiRadius.panel,
  }) =>
      BoxDecoration(
        borderRadius: radius,
        border: Border.all(color: border, width: 1),
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: <Color>[
            Color.lerp(tint, EsiPalette.edgeBright, 0.16)!.withValues(alpha: opacity),
            tint.withValues(alpha: opacity),
          ],
        ),
      );

  /// The blur layer that goes *behind* [panel] in a `BackdropFilter`.
  ///
  /// Not `const`: `ImageFilter.blur` builds a native filter object, and a panel is built
  /// at most a few dozen times per screen, so there is nothing to gain from a
  /// compile-time constant.
  static final List<ui.ImageFilter> filter = <ui.ImageFilter>[
    ui.ImageFilter.blur(sigmaX: blurSigma, sigmaY: blurSigma),
  ];
}

/// A panel wrapped in the blur, with the signal tick in its top-left corner.
class EsiSurface extends StatelessWidget {
  const EsiSurface({
    super.key,
    required this.child,
    this.tint = EsiPalette.hull,
    this.opacity = 0.62,
    this.border = EsiPalette.edge,
    this.radius = EsiRadius.panel,
    this.padding = const EdgeInsets.all(EsiSpace.lg),
  });

  final Widget child;
  final Color tint;
  final double opacity;
  final Color border;
  final BorderRadius radius;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) => ClipRRect(
        borderRadius: radius,
        child: BackdropFilter(
          filter: ui.ImageFilter.blur(
            sigmaX: EsiGlass.blurSigma,
            sigmaY: EsiGlass.blurSigma,
          ),
          child: DecoratedBox(
            decoration: EsiGlass.panel(
              tint: tint,
              opacity: opacity,
              border: border,
              radius: radius,
            ),
            child: Padding(padding: padding, child: child),
          ),
        ),
      );
}

/// Builds the app's [ThemeData].
///
/// Dark only. The app is a control surface for a machine that is usually in another room,
/// often looked at in the evening, and a light variant would need a second palette to be
/// worth having.
ThemeData buildEsiTheme() {
  const scheme = ColorScheme.dark(
    primary: EsiPalette.signal,
    onPrimary: EsiPalette.void_,
    secondary: EsiPalette.lock,
    onSecondary: EsiPalette.void_,
    error: EsiPalette.alarm,
    onError: EsiPalette.void_,
    surface: EsiPalette.hull,
    onSurface: EsiPalette.ink,
    outline: EsiPalette.edge,
  );

  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    colorScheme: scheme,
    scaffoldBackgroundColor: EsiPalette.void_,
    canvasColor: EsiPalette.void_,
    fontFamily: EsiType.display,
    splashFactory: InkSparkle.splashFactory,
    textTheme: TextTheme(
      displaySmall: EsiType.hero,
      headlineSmall: EsiType.screenTitle,
      titleLarge: EsiType.title,
      titleMedium: EsiType.subtitle,
      bodyLarge: EsiType.body,
      bodyMedium: EsiType.body,
      labelLarge: EsiType.button,
      labelSmall: EsiType.label,
    ),
    appBarTheme: const AppBarTheme(
      backgroundColor: Colors.transparent,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      centerTitle: false,
      titleTextStyle: EsiType.title,
      systemOverlayStyle: SystemUiOverlayStyle(
        statusBarColor: Colors.transparent,
        statusBarIconBrightness: Brightness.light,
        systemNavigationBarColor: EsiPalette.void_,
        systemNavigationBarIconBrightness: Brightness.light,
      ),
    ),
    dividerTheme: const DividerThemeData(
      color: EsiPalette.edge,
      thickness: 1,
      space: 1,
    ),
    iconTheme: const IconThemeData(color: EsiPalette.dim, size: 20),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: EsiPalette.signal,
        foregroundColor: EsiPalette.void_,
        disabledBackgroundColor: EsiPalette.edge,
        disabledForegroundColor: EsiPalette.faint,
        minimumSize: const Size(0, 46),
        shape: const RoundedRectangleBorder(borderRadius: EsiRadius.button),
        textStyle: EsiType.button,
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        foregroundColor: EsiPalette.ink,
        side: const BorderSide(color: EsiPalette.edgeBright),
        minimumSize: const Size(0, 46),
        shape: const RoundedRectangleBorder(borderRadius: EsiRadius.button),
        textStyle: EsiType.button,
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        foregroundColor: EsiPalette.signal,
        textStyle: EsiType.button,
        shape: const RoundedRectangleBorder(borderRadius: EsiRadius.button),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: EsiPalette.hull.withValues(alpha: 0.7),
      isDense: true,
      contentPadding: const EdgeInsets.symmetric(
        horizontal: EsiSpace.md,
        vertical: EsiSpace.md,
      ),
      hintStyle: EsiType.body.copyWith(color: EsiPalette.faint),
      labelStyle: EsiType.label,
      floatingLabelStyle: EsiType.label.copyWith(color: EsiPalette.signal),
      border: const OutlineInputBorder(
        borderRadius: EsiRadius.button,
        borderSide: BorderSide(color: EsiPalette.edge),
      ),
      enabledBorder: const OutlineInputBorder(
        borderRadius: EsiRadius.button,
        borderSide: BorderSide(color: EsiPalette.edge),
      ),
      focusedBorder: const OutlineInputBorder(
        borderRadius: EsiRadius.button,
        borderSide: BorderSide(color: EsiPalette.signal),
      ),
      errorBorder: const OutlineInputBorder(
        borderRadius: EsiRadius.button,
        borderSide: BorderSide(color: EsiPalette.alarm),
      ),
      focusedErrorBorder: const OutlineInputBorder(
        borderRadius: EsiRadius.button,
        borderSide: BorderSide(color: EsiPalette.alarm),
      ),
    ),
    dialogTheme: const DialogThemeData(
      backgroundColor: EsiPalette.deck,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(borderRadius: EsiRadius.dialog),
      titleTextStyle: EsiType.title,
      contentTextStyle: EsiType.body,
    ),
    bottomSheetTheme: const BottomSheetThemeData(
      backgroundColor: EsiPalette.deck,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(borderRadius: EsiRadius.sheet),
      showDragHandle: true,
      dragHandleColor: EsiPalette.edgeBright,
    ),
    snackBarTheme: SnackBarThemeData(
      backgroundColor: EsiPalette.deck,
      contentTextStyle: EsiType.body,
      behavior: SnackBarBehavior.floating,
      shape: const RoundedRectangleBorder(borderRadius: EsiRadius.button),
    ),
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith(
        (states) => states.contains(WidgetState.selected)
            ? EsiPalette.signal
            : EsiPalette.faint,
      ),
      trackColor: WidgetStateProperty.resolveWith(
        (states) => states.contains(WidgetState.selected)
            ? EsiPalette.signal.withValues(alpha: 0.22)
            : EsiPalette.edge,
      ),
      trackOutlineColor: const WidgetStatePropertyAll(EsiPalette.edgeBright),
    ),
    sliderTheme: const SliderThemeData(
      activeTrackColor: EsiPalette.signal,
      inactiveTrackColor: EsiPalette.edge,
      thumbColor: EsiPalette.signal,
      overlayColor: Color(0x224FD6C0),
      trackHeight: 3,
    ),
    tooltipTheme: TooltipThemeData(
      decoration: BoxDecoration(
        color: EsiPalette.deck,
        borderRadius: EsiRadius.tag,
        border: Border.all(color: EsiPalette.edge),
      ),
      textStyle: EsiType.body.copyWith(fontSize: 12),
    ),
    progressIndicatorTheme: const ProgressIndicatorThemeData(
      color: EsiPalette.signal,
      linearTrackColor: EsiPalette.edge,
      circularTrackColor: EsiPalette.edge,
    ),
  );
}

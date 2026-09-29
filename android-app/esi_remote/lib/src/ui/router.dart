import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'screens/apps_screen.dart';
import 'screens/dashboard_screen.dart';
import 'screens/files_screen.dart';
import 'screens/gaming_screen.dart';
import 'screens/media_screen.dart';
import 'screens/notifications_screen.dart';
import 'screens/onboarding_screen.dart';
import 'screens/power_screen.dart';
import 'screens/remote_screen.dart';
import 'screens/settings_screen.dart';
import 'shell.dart';
import 'theme.dart';

/// Routes. `StatefulShellRoute` keeps each tab's scroll position and its providers alive
/// while the user moves between them, which matters here because the dashboard holds a
/// live stream and losing it every time the user peeks at notifications would be the
/// difference between an app that feels alive and one that feels broken.
final GoRouter appRouter = GoRouter(
  initialLocation: '/onboarding',
  routes: <RouteBase>[
    GoRoute(
      path: '/onboarding',
      builder: (context, state) => const OnboardingScreen(),
      routes: <RouteBase>[
        GoRoute(path: 'scan', builder: (context, state) => const QrScannerScreen()),
      ],
    ),
    GoRoute(
      path: '/pairing',
      builder: (context, state) => const PairingScreen(),
    ),
    StatefulShellRoute.indexedStack(
      builder: (context, state, navigationShell) => AppShell(navigationShell: navigationShell),
      branches: <StatefulShellBranch>[
        StatefulShellBranch(routes: <RouteBase>[
          GoRoute(path: '/dashboard', builder: (context, state) => const DashboardScreen()),
        ]),
        StatefulShellBranch(routes: <RouteBase>[
          GoRoute(path: '/power', builder: (context, state) => const PowerScreen()),
          GoRoute(path: '/gaming', builder: (context, state) => const GamingScreen()),
        ]),
        StatefulShellBranch(routes: <RouteBase>[
          GoRoute(path: '/apps', builder: (context, state) => const AppsScreen()),
          GoRoute(path: '/files', builder: (context, state) => const FilesScreen()),
        ]),
        StatefulShellBranch(routes: <RouteBase>[
          GoRoute(path: '/remote', builder: (context, state) => const RemoteScreen()),
        ]),
        StatefulShellBranch(routes: <RouteBase>[
          GoRoute(path: '/media', builder: (context, state) => const MediaScreen()),
          GoRoute(path: '/notifications', builder: (context, state) => const NotificationsScreen()),
          GoRoute(path: '/settings', builder: (context, state) => const SettingsScreen()),
        ]),
      ],
    ),
  ],
  errorBuilder: (context, state) => _RouterError(error: state.error),
);

class _RouterError extends StatelessWidget {
  const _RouterError({this.error});
  final Exception? error;

  @override
  Widget build(BuildContext context) => Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(EsiSpace.xl),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                const Icon(Icons.explore_off_outlined, size: 32, color: EsiPalette.flare),
                const SizedBox(height: EsiSpace.lg),
                Text('That screen does not exist', style: EsiType.subtitle),
                const SizedBox(height: EsiSpace.sm),
                Text(
                  error?.toString() ?? 'Unknown destination.',
                  style: EsiType.mono,
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: EsiSpace.xl),
                FilledButton(
                  onPressed: () => context.go('/onboarding'),
                  child: const Text('Back to setup'),
                ),
              ],
            ),
          ),
        ),
      );
}

/// The app root.
///
/// Dark only by design, so the light theme is the only one built. Orientation is left
/// unlocked: the remote-control screen genuinely wants landscape, and locking the whole app
/// to portrait to accommodate one tab would be the wrong trade.
class EsiRemoteApp extends ConsumerWidget {
  const EsiRemoteApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => AnnotatedRegion<SystemUiOverlayStyle>(
        value: const SystemUiOverlayStyle(
          statusBarColor: Colors.transparent,
          statusBarIconBrightness: Brightness.light,
          systemNavigationBarColor: EsiPalette.void_,
          systemNavigationBarIconBrightness: Brightness.light,
          systemNavigationBarDividerColor: EsiPalette.edge,
        ),
        child: MaterialApp.router(
          title: 'ESI Remote PC',
          debugShowCheckedModeBanner: false,
          theme: buildEsiTheme(),
          routerConfig: appRouter,
          builder: (context, child) => MediaQuery.withClampedTextScaling(
            // The data-dense screens become unreadable past about 1.3x, and a person who
            // needs larger text still gets everything legible.
            minScaleFactor: 0.9,
            maxScaleFactor: 1.3,
            child: child ?? const SizedBox.shrink(),
          ),
        ),
      );
}

/// Startup that has to happen before the first frame: nothing to do but give the app a
/// consistent edge-to-edge look.
void configureSystemChrome() {
  SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
  SystemChrome.setSystemUIOverlayStyle(const SystemUiOverlayStyle(
    statusBarColor: Colors.transparent,
    statusBarIconBrightness: Brightness.light,
    systemNavigationBarColor: EsiPalette.void_,
  ));
}

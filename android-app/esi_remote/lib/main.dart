import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'src/state/connection.dart';
import 'src/ui/router.dart';

/// Entry point.
///
/// The only thing done before the first frame is reading which PC was last selected,
/// because the whole app's chrome depends on it. Everything else loads lazily through
/// Riverpod, so a slow or absent PC never delays the shell appearing.
void main() {
  WidgetsFlutterBinding.ensureInitialized();
  configureSystemChrome();
  runApp(const ProviderScope(child: EsiBootstrap()));
}

/// Restores the last-selected machine, then hands over to the router.
class EsiBootstrap extends ConsumerStatefulWidget {
  const EsiBootstrap({super.key});

  @override
  ConsumerState<EsiBootstrap> createState() => _EsiBootstrapState();
}

class _EsiBootstrapState extends ConsumerState<EsiBootstrap> {
  late final Future<void> _ready = ref.read(activeDeviceIdProvider.notifier).initialise();

  @override
  Widget build(BuildContext context) => FutureBuilder<void>(
        future: _ready,
        builder: (context, snapshot) => EsiRemoteApp(),
      );
}

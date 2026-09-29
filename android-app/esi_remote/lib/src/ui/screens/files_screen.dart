import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../protocol/models.dart';
import '../../protocol/protocol_constants.dart';
import '../../state/files.dart';
import '../format.dart';
import '../shell.dart';
import '../theme.dart';
import '../widgets/panel.dart';
import '../widgets/states.dart';

/// A browser for the agent's file sandbox.
///
/// The roots are shown at the top, not buried in a settings screen, because §6.6 makes
/// the sandbox a security boundary rather than a convenience: the agent refuses paths that
/// escape it, and a person using this app should be able to see where the walls are
/// without having to ask.
class FilesScreen extends ConsumerWidget {
  const FilesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => RequiresPairing(
        builder: (context, ref, connection) {
          if (!connection.supports(EsiFeature.fileManager)) {
            return const FeatureUnavailable(feature: 'fileManager', label: 'The file manager');
          }
          final state = ref.watch(filesProvider);
          return OfflineGuard(
            connection: connection,
            child: EsiPage(
              title: 'Files',
              subtitle: _subtitle(state),
              onRefresh: () => _reload(context, ref, state),
              floating: FloatingActionButton.extended(
                backgroundColor: EsiPalette.signal,
                foregroundColor: EsiPalette.void_,
                onPressed: () => _showAddMenu(context, ref, state),
                icon: const Icon(Icons.add, size: 18),
                label: const Text('Add'),
              ),
              children: <Widget>[
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                  child: _RootsPanel(),
                ),
                const SizedBox(height: EsiSpace.md),
                if (state.loading && state.current == null)
                  const Padding(
                    padding: EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                    child: EsiLoading(height: 200),
                  )
                else if (state.error != null && state.current == null)
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                    child: EsiErrorView(
                      message: state.error!,
                      detail: state.errorDetail,
                      onRetry: () => _reload(context, ref, state),
                    ),
                  )
                else
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: EsiSpace.lg),
                    child: _ListingPanel(state: state),
                  ),
              ],
            ),
          );
        },
      );

  static String _subtitle(FilesState state) {
    final listing = state.current;
    if (listing == null) return 'Reading the sandbox';
    final count = listing.entries.length;
    return '${listing.path.isEmpty ? 'default root' : listing.path} · $count '
        'item${count == 1 ? '' : 's'}'
        '${listing.truncated ? ' (truncated)' : ''}';
  }

  static Future<void> _reload(BuildContext context, WidgetRef ref, FilesState state) async {
    final path = state.currentPath;
    final notifier = ref.read(filesProvider.notifier);
    if (path == null || path.isEmpty) {
      ref.invalidate(fileRootsProvider);
      return;
    }
    await notifier.open(path);
  }
}

class _RootsPanel extends ConsumerWidget {
  const _RootsPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final roots = ref.watch(fileRootsProvider);
    return EsiPanel(
      title: 'Allowed roots',
      severity: Severity.nominal,
      subtitle: 'The agent refuses anything outside these. It resolves symlinks and '
          'junctions before comparing, so there is no way around them from here.',
      trailing: roots.hasValue
          ? EsiTag(
              label: roots.value!.allReadOnly ? 'read only' : 'read and write',
              colour: roots.value!.allReadOnly ? EsiPalette.flare : EsiPalette.signal,
            )
          : null,
      child: roots.when(
        loading: () => const EsiSkeleton(height: 40),
        error: (error, _) => Text(
          'The roots could not be read: $error',
          style: EsiType.body.copyWith(color: EsiPalette.alarm, fontSize: 12),
        ),
        data: (value) => Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            Wrap(
              spacing: EsiSpace.sm,
              runSpacing: EsiSpace.sm,
              children: <Widget>[
                for (final root in value.roots)
                  _RootChip(
                    root: root,
                    onTap: () => ref.read(filesProvider.notifier).open(root.path),
                  ),
              ],
            ),
            const SizedBox(height: EsiSpace.md),
            EsiReadoutList(
              children: <Widget>[
                EsiReadout(
                  label: 'Default root',
                  value: value.defaultRoot,
                ),
                EsiReadout(
                  label: 'Delete allowed',
                  value: value.allowDelete ? 'yes' : 'no',
                  severity: value.allowDelete ? Severity.nominal : Severity.elevated,
                ),
                EsiReadout(
                  label: 'Upload allowed',
                  value: value.allowUpload ? 'yes' : 'no',
                  severity: value.allowUpload ? Severity.nominal : Severity.elevated,
                ),
                EsiReadout(
                  label: 'Upload limit',
                  value: value.maxUploadBytes > 0 ? formatBytes(value.maxUploadBytes) : null,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _RootChip extends StatelessWidget {
  const _RootChip({required this.root, required this.onTap});

  final FileRoot root;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: EsiRadius.tag,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: EsiSpace.md, vertical: EsiSpace.sm),
            decoration: BoxDecoration(
              borderRadius: EsiRadius.tag,
              border: Border.all(color: EsiPalette.edge),
              color: EsiPalette.hull.withValues(alpha: 0.5),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Icon(
                  root.readOnly ? Icons.lock_outline : Icons.folder_outlined,
                  size: 13,
                  color: root.readOnly ? EsiPalette.flare : EsiPalette.dim,
                ),
                const SizedBox(width: EsiSpace.sm),
                Text(
                  root.label.isEmpty ? root.path : root.label,
                  style: EsiType.body.copyWith(fontSize: 12.5),
                ),
              ],
            ),
          ),
        ),
      );
}

class _ListingPanel extends ConsumerWidget {
  const _ListingPanel({required this.state});

  final FilesState state;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final listing = state.current;
    if (listing == null) return const SizedBox.shrink();
    if (listing.entries.isEmpty) {
      return EsiPanel(
        title: 'Nothing here',
        child: Text(
          'This folder is empty.',
          style: EsiType.body.copyWith(color: EsiPalette.faint),
        ),
      );
    }
    final folders = listing.entries.where((e) => e.isDirectory).toList();
    final files = listing.entries.where((e) => !e.isDirectory).toList();

    return EsiPanel(
      title: 'Contents',
      severity: listing.readOnly ? Severity.elevated : Severity.nominal,
      trailing: listing.readOnly
          ? const EsiTag(label: 'read only', colour: EsiPalette.flare)
          : null,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          if (state.canGoUp)
            _UpRow(parent: listing.parent!),
          for (final folder in folders) _EntryRow(entry: folder),
          for (final file in files) _EntryRow(entry: file),
        ],
      ),
    );
  }
}

class _UpRow extends ConsumerWidget {
  const _UpRow({required this.parent});

  final String parent;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Padding(
        padding: const EdgeInsets.only(bottom: EsiSpace.xs),
        child: Material(
          color: Colors.transparent,
          child: InkWell(
            onTap: () => ref.read(filesProvider.notifier).open(parent),
            borderRadius: EsiRadius.tag,
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: EsiSpace.sm),
              child: Row(
                children: <Widget>[
                  const Icon(Icons.arrow_upward, size: 15, color: EsiPalette.dim),
                  const SizedBox(width: EsiSpace.md),
                  Expanded(
                    child: Text('Go up', style: EsiType.body.copyWith(fontSize: 13)),
                  ),
                  Flexible(
                    child: Text(
                      parent,
                      style: EsiType.mono.copyWith(fontSize: 10),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      );
}

class _EntryRow extends ConsumerWidget {
  const _EntryRow({required this.entry});

  final FileEntry entry;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(filesProvider.notifier);
    return Padding(
      padding: const EdgeInsets.only(bottom: EsiSpace.xs),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: entry.isDirectory ? () => notifier.open(entry.path) : null,
          borderRadius: EsiRadius.tag,
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: EsiSpace.sm),
            child: Row(
              children: <Widget>[
                Icon(
                  entry.isDirectory ? Icons.folder_outlined : _iconFor(entry.extension),
                  size: 16,
                  color: entry.isDirectory ? EsiPalette.dim : EsiPalette.faint,
                ),
                const SizedBox(width: EsiSpace.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Row(
                        children: <Widget>[
                          Flexible(
                            child: Text(
                              entry.name,
                              style: EsiType.body.copyWith(fontSize: 13),
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          if (entry.isReadOnly) ...<Widget>[
                            const SizedBox(width: EsiSpace.sm),
                            const Icon(Icons.lock_outline, size: 11, color: EsiPalette.faint),
                          ],
                        ],
                      ),
                      Text(
                        entry.isDirectory
                            ? '${entry.childCount ?? 0} item${(entry.childCount ?? 0) == 1 ? '' : 's'}'
                            : '${formatBytes(entry.sizeBytes)} · ${formatRelative(entry.lastModified)}',
                        style: EsiType.mono.copyWith(fontSize: 10),
                      ),
                    ],
                  ),
                ),
                if (!entry.isDirectory) ...<Widget>[
                  EsiIconAction(
                    icon: Icons.download_outlined,
                    size: 30,
                    tooltip: 'Download ${entry.name}',
                    onPressed: () => _download(context, ref, entry),
                  ),
                  const SizedBox(width: EsiSpace.xs),
                ],
                EsiIconAction(
                  icon: Icons.more_vert,
                  size: 30,
                  tooltip: 'More',
                  onPressed: () => _menu(context, ref, entry),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  static IconData _iconFor(String? extension) {
    switch (extension?.toLowerCase()) {
      case 'txt' || 'log' || 'md' || 'json' || 'xml' || 'csv':
        return Icons.description_outlined;
      case 'jpg' || 'jpeg' || 'png' || 'gif' || 'bmp' || 'webp' || 'heic':
        return Icons.image_outlined;
      case 'mp4' || 'mkv' || 'avi' || 'mov' || 'webm':
        return Icons.movie_outlined;
      case 'mp3' || 'wav' || 'flac' || 'aac':
        return Icons.audiotrack_outlined;
      case 'zip' || '7z' || 'rar' || 'gz' || 'tar':
        return Icons.folder_zip_outlined;
      case 'exe' || 'msi' || 'bat' || 'cmd':
        return Icons.terminal_outlined;
      default:
        return Icons.insert_drive_file_outlined;
    }
  }

  Future<void> _menu(BuildContext context, WidgetRef ref, FileEntry entry) async {
    await showModalBottomSheet<void>(
      context: context,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            Padding(
              padding: const EdgeInsets.fromLTRB(EsiSpace.lg, 0, EsiSpace.lg, EsiSpace.md),
              child: Row(
                children: <Widget>[
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        Text(entry.name, style: EsiType.subtitle),
                        Text(entry.path, style: EsiType.mono.copyWith(fontSize: 10)),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            if (entry.canRename)
              ListTile(
                leading: const Icon(Icons.drive_file_rename_outline),
                title: const Text('Rename'),
                onTap: () {
                  Navigator.of(context).pop();
                  _rename(context, ref, entry);
                },
              ),
            if (!entry.isDirectory)
              ListTile(
                leading: const Icon(Icons.drive_file_move_outline),
                title: const Text('Move to…'),
                onTap: () {
                  Navigator.of(context).pop();
                  _move(context, ref, entry);
                },
              ),
            if (entry.canDelete)
              ListTile(
                leading: const Icon(Icons.delete_outline, color: EsiPalette.alarm),
                title: const Text(
                  'Delete',
                  style: TextStyle(color: EsiPalette.alarm),
                ),
                onTap: () {
                  Navigator.of(context).pop();
                  _delete(context, ref, entry);
                },
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _download(BuildContext context, WidgetRef ref, FileEntry entry) async {
    final outcome = await ref.read(filesProvider.notifier).download(entry.path);
    if (!context.mounted) return;
    if (!outcome.ok) {
      showOutcome(context, (ok: false, message: outcome.message));
      return;
    }
    final bytes = outcome.bytes!;
    // The system save dialog writes through the Storage Access Framework, so no storage
    // permission is needed and the user picks the folder.
    final saved = await FilePicker.saveFile(
      fileName: entry.name,
      bytes: bytes,
    );
    if (!context.mounted) return;
    if (saved == null) {
      showOutcome(context, (ok: false, message: 'Not saved. No destination was chosen.'));
      return;
    }
    showOutcome(context, (ok: true, message: 'Saved ${formatBytes(bytes.length)} to $saved'));
  }

  Future<void> _rename(BuildContext context, WidgetRef ref, FileEntry entry) async {
    final controller = TextEditingController(text: entry.name);
    final newName = await _askForText(
      context,
      title: 'Rename',
      body: 'Only the name changes. The file stays where it is.',
      controller: controller,
      confirmLabel: 'Rename',
    );
    controller.dispose();
    if (newName == null || newName.isEmpty || !context.mounted) return;
    final parent = _parentOf(entry.path);
    final outcome = await ref
        .read(filesProvider.notifier)
        .rename(entry.path, '$parent\\$newName');
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }

  Future<void> _move(BuildContext context, WidgetRef ref, FileEntry entry) async {
    final roots = ref.read(fileRootsProvider).value?.roots ?? const <FileRoot>[];
    await showModalBottomSheet<void>(
      context: context,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            const Padding(
              padding: EdgeInsets.all(EsiSpace.lg),
              child: Text('Move into a root folder', style: EsiType.subtitle),
            ),
            for (final root in roots)
              ListTile(
                leading: const Icon(Icons.folder_outlined),
                title: Text(root.label.isEmpty ? root.path : root.label),
                subtitle: Text(root.path, style: EsiType.mono.copyWith(fontSize: 10)),
                onTap: () async {
                  Navigator.of(context).pop();
                  final target = '${root.path}\\${entry.name}';
                  final outcome =
                      await ref.read(filesProvider.notifier).move(entry.path, target);
                  if (!context.mounted) return;
                  showOutcome(context, outcome);
                },
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _delete(BuildContext context, WidgetRef ref, FileEntry entry) async {
    final ok = await EsiConfirmDialog.show(
      context,
      title: 'Delete ${entry.name}?',
      body: entry.isDirectory
          ? 'The folder and everything inside it is removed. This cannot be undone from '
              'here.'
          : 'The file is removed. This cannot be undone from here.',
      confirmLabel: 'Delete',
      destructive: true,
    );
    if (!ok || !context.mounted) return;
    final outcome = await ref.read(filesProvider.notifier).delete(entry.path);
    if (!context.mounted) return;
    showOutcome(context, outcome);
  }

  /// `C:\a\b\c.txt` -> `C:\a\b`. The agent accepts a forward-slash path too, so this only
  /// has to find the last separator, whatever it is.
  static String _parentOf(String path) {
    final index = path.lastIndexOf(RegExp(r'[/\\]'));
    return index <= 0 ? '' : path.substring(0, index);
  }
}

Future<String?> _askForText(
  BuildContext context, {
  required String title,
  required String body,
  required TextEditingController controller,
  required String confirmLabel,
}) =>
    showDialog<String>(
      context: context,
      builder: (context) => _TextPromptDialog(
        title: title,
        body: body,
        controller: controller,
        confirmLabel: confirmLabel,
      ),
    );

class _TextPromptDialog extends StatefulWidget {
  const _TextPromptDialog({
    required this.title,
    required this.body,
    required this.controller,
    required this.confirmLabel,
  });

  final String title;
  final String body;
  final TextEditingController controller;
  final String confirmLabel;

  @override
  State<_TextPromptDialog> createState() => _TextPromptDialogState();
}

class _TextPromptDialogState extends State<_TextPromptDialog> {
  @override
  Widget build(BuildContext context) => AlertDialog(
        backgroundColor: EsiPalette.deck,
        shape: const RoundedRectangleBorder(borderRadius: EsiRadius.dialog),
        title: Text(widget.title, style: EsiType.title),
        content: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 400),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Text(
                widget.body,
                style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
              ),
              const SizedBox(height: EsiSpace.lg),
              TextField(
                controller: widget.controller,
                autofocus: true,
                style: EsiType.mono.copyWith(color: EsiPalette.ink),
              ),
            ],
          ),
        ),
        actions: <Widget>[
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(widget.controller.text.trim()),
            child: Text(widget.confirmLabel),
          ),
        ],
      );
}

Future<void> _showAddMenu(BuildContext context, WidgetRef ref, FilesState state) async {
  final notifier = ref.read(filesProvider.notifier);
  await showModalBottomSheet<void>(
    context: context,
    builder: (context) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          const Padding(
            padding: EdgeInsets.all(EsiSpace.lg),
            child: Text('Add to this folder', style: EsiType.subtitle),
          ),
          ListTile(
            leading: const Icon(Icons.create_new_folder_outlined),
            title: const Text('New folder'),
            onTap: () async {
              Navigator.of(context).pop();
              final current = ref.read(filesProvider).currentPath ?? '';
              final controller = TextEditingController(text: 'New folder');
              final name = await _askForText(
                context,
                title: 'New folder',
                body: 'The name is created inside ${current.isEmpty ? 'the default root' : current}.',
                controller: controller,
                confirmLabel: 'Create',
              );
              controller.dispose();
              if (name == null || name.isEmpty) return;
              final path = current.isEmpty ? name : '$current\\$name';
              final outcome = await notifier.createDirectory(path);
              if (context.mounted) showOutcome(context, outcome);
            },
          ),
          ListTile(
            leading: const Icon(Icons.upload_file),
            title: const Text('Upload from this phone'),
            subtitle: Text(
              'Large files use a one-time ticket so the body is not hashed on every hop.',
              style: EsiType.mono.copyWith(fontSize: 10),
            ),
            onTap: () async {
              Navigator.of(context).pop();
              final path = await _pickFile();
              if (path == null) return;
              final current = ref.read(filesProvider).currentPath ?? '';
              final name = path.split(Platform.pathSeparator).last;
              final target = current.isEmpty ? name : '$current\\$name';
              final outcome = await notifier.upload(targetPath: target, source: File(path));
              if (context.mounted) showOutcome(context, outcome);
            },
          ),
        ],
      ),
    ),
  );
}

/// Opens the system file picker and returns a local path.
///
/// The Android Storage Access Framework needs no storage permission, so the app does not
/// have to ask for one it would then have to justify in a Play listing. Returns null when
/// the user cancels.
Future<String?> _pickFile() async {
  final picked = await FilePicker.pickFile();
  return picked?.path;
}

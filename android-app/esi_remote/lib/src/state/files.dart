import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../net/esi_api.dart';
import '../protocol/models.dart';
import '../protocol/protocol_constants.dart';
import 'connection.dart';

/// The sandbox roots the agent allows.
///
/// Shown at the top of the file screen on purpose. §6.6 is emphatic that the sandbox is
/// agent-enforced and non-negotiable, and a person poking around inside their own machine
/// should be able to see exactly where the walls are rather than discovering them by
/// hitting one.
final fileRootsProvider = FutureProvider<FileRoots>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) {
    return const FileRoots(
      roots: <FileRoot>[],
      defaultRoot: null,
      allReadOnly: true,
      allowDelete: false,
      allowUpload: false,
      maxUploadBytes: 0,
    );
  }
  final slice = await runSlice(() async {
    final json = await api.getJson('/files/roots');
    return FileRoots.fromJson(json);
  });
  return slice.value ??
      const FileRoots(
        roots: <FileRoot>[],
        defaultRoot: null,
        allReadOnly: true,
        allowDelete: false,
        allowUpload: false,
        maxUploadBytes: 0,
      );
});

/// Where the browser currently is, and what is in it.
class FilesNotifier extends Notifier<FilesState> {
  @override
  FilesState build() {
    // Deferred (D45): see metrics.dart. `_openDefault()` reads and writes `state`, and
    // `build()` must return before either happens.
    scheduleMicrotask(() => unawaited(_openDefault()));
    return const FilesState();
  }

  Future<void> _openDefault() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    final slice = await runSlice(() async {
      final json = await api.getJson('/files/list', query: <(String, String)>[('showHidden', 'false')]);
      return FileListing.fromJson(json);
    });
    if (slice.value != null) {
      state = state.copyWith(
        current: slice.value,
        loading: false,
        clearError: true,
      );
    } else {
      state = state.copyWith(
        loading: false,
        error: slice.error,
        errorDetail: slice.errorDetail,
      );
    }
  }

  Future<void> open(String path) async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = state.copyWith(loading: true, currentPath: path, clearError: true);
    final slice = await runSlice(() async {
      final json = await api.getJson('/files/list', query: <(String, String)>[
        ('path', path),
        ('showHidden', 'false'),
      ]);
      return FileListing.fromJson(json);
    });
    if (slice.value != null) {
      state = state.copyWith(current: slice.value, loading: false);
    } else {
      state = state.copyWith(
        loading: false,
        error: slice.error,
        errorDetail: slice.errorDetail,
      );
    }
  }

  Future<void> up() async {
    final parent = state.current?.parent;
    if (parent == null || parent.isEmpty) return;
    await open(parent);
  }

  Future<({bool ok, String message})> createDirectory(String path) =>
      _mutate('/files/mkdir', <String, dynamic>{'path': path}, 'Folder created');

  Future<({bool ok, String message})> rename(String from, String to) => _mutate(
        '/files/rename',
        <String, dynamic>{'from': from, 'to': to},
        'Renamed',
      );

  Future<({bool ok, String message})> move(String from, String to) =>
      _mutate('/files/move', <String, dynamic>{'from': from, 'to': to}, 'Moved');

  Future<({bool ok, String message})> delete(String path) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      await api.deleteJson('/files', query: <(String, String)>[('path', path)]);
      await _refreshCurrent();
      return (ok: true, message: 'Deleted.');
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }

  Future<({bool ok, String message})> _mutate(
    String path,
    Map<String, dynamic> body,
    String success,
  ) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.postJson(path, body: body);
      final result = FileOperationResult.fromJson(json);
      await _refreshCurrent();
      return (ok: result.ok, message: result.ok ? success : (result.detail ?? 'Refused.'));
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }

  Future<void> _refreshCurrent() async {
    final path = state.currentPath;
    if (path == null || path.isEmpty) {
      await _openDefault();
    } else {
      await open(path);
    }
  }

  /// §7.4: a signed request body is capped, so a large upload asks for a single-use ticket
  /// and then PUTs the bytes to the ticket's URL without a signature.
  ///
  /// Small files go as an ordinary signed request, which keeps one code path for the
  /// common case.
  Future<({bool ok, String message})> upload({
    required String targetPath,
    required File source,
    void Function(int sent, int total)? onProgress,
  }) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');

    final size = await source.length();
    try {
      // §7.4: `/api/v1/files/upload` is not a signed endpoint. It authenticates with a
      // single-use ticket, and the ticket is also where the destination path comes from.
      //
      // There used to be a second path for files under the signed-body cap that PUT the bytes
      // straight there without asking for a ticket, on the grounds that it "keeps one code
      // path for the common case". It was the only place that path was ever exercised: the
      // agent requires a ticket on it exactly, so every upload of a file of 8 MiB or less came
      // back 401 "An upload ticket is required" while every file larger than that succeeded.
      // One code path now, and it is the one the endpoint accepts.
      final ticketJson = await api.postJson('/files/upload-ticket', body: <String, dynamic>{
        'path': targetPath,
        'size': size,
      });
      final ticket = UploadTicket.fromJson(ticketJson);
      if (ticket.maxBytes > 0 && size > ticket.maxBytes) {
        return (
          ok: false,
          message: 'The PC limits uploads to ${ticket.maxBytes} bytes and this file is '
              '$size bytes.',
        );
      }

      // The ticket is single-use and expires in two minutes, so the file has to be read
      // and sent immediately.
      final bytes = await source.readAsBytes();
      onProgress?.call(bytes.length, bytes.length);

      // `uploadUrl` comes back as a full request target including the query string. It is
      // split here so the path and the ticket go on the wire exactly as the agent wrote
      // them; the endpoint is ticket-authenticated, so no signature is attached.
      final ticketTarget = Uri.parse(ticket.uploadUrl);
      final uploadPath = ticketTarget.path.startsWith(EsiProtocol.apiBase)
          ? ticketTarget.path.substring(EsiProtocol.apiBase.length)
          : ticketTarget.path;
      final response = await api.sendRaw(
        method: 'PUT',
        path: uploadPath,
        rawQuery: ticketTarget.hasQuery ? ticketTarget.query : '',
        body: bytes,
        contentType: 'application/octet-stream',
        signed: false,
        timeout: const Duration(minutes: 20),
      );
      final result = UploadResult.fromJson(response);
      await _refreshCurrent();
      return (ok: result.completed, message: 'Uploaded ${result.bytesWritten} bytes.');
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    } on FileSystemException catch (e) {
      return (ok: false, message: 'Could not read the file: ${e.message}');
    }
  }

  /// §6.6: the download streams with a content-disposition attachment. The file is written
  /// to the phone's cache, and the caller decides whether to keep it.
  Future<({bool ok, String message, Uint8List? bytes})> download(String path) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.', bytes: null);
    try {
      final response = await api.getBytes(
        '/files/download',
        query: <(String, String)>[('path', path)],
      );
      return (ok: true, message: 'Downloaded ${response.bodyBytes.length} bytes.', bytes: response.bodyBytes);
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage, bytes: null);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message, bytes: null);
    }
  }

  Future<List<FileEntry>> search(String root, String query) async {
    final api = ref.read(apiProvider);
    if (api == null) return const <FileEntry>[];
    final slice = await runSlice(() async {
      final json = await api.getJson('/files/search', query: <(String, String)>[
        ('root', root),
        ('query', query),
        ('maxResults', '200'),
      ]);
      return (json['entries'] is List)
          ? (json['entries'] as List)
              .map((e) => FileEntry.fromJson(e is Map<String, dynamic> ? e : const <String, dynamic>{}))
              .toList()
          : <FileEntry>[];
    });
    return slice.value ?? const <FileEntry>[];
  }
}

class FilesState {
  const FilesState({
    this.current,
    this.currentPath,
    this.loading = false,
    this.error,
    this.errorDetail,
  });

  final FileListing? current;

  /// The path the browser is at, kept separately so a failed navigation can still say
  /// where it was trying to get to.
  final String? currentPath;
  final bool loading;
  final String? error;
  final String? errorDetail;

  bool get canGoUp => current?.parent?.isNotEmpty ?? false;

  FilesState copyWith({
    FileListing? current,
    String? currentPath,
    bool? loading,
    String? error,
    String? errorDetail,
    bool clearError = false,
  }) =>
      FilesState(
        current: current ?? this.current,
        currentPath: currentPath ?? this.currentPath,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        errorDetail: clearError ? null : (errorDetail ?? this.errorDetail),
      );
}

final filesProvider = NotifierProvider<FilesNotifier, FilesState>(FilesNotifier.new);

import 'dart:io';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_api.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
import 'package:esi_remote/src/state/connection.dart';
import 'package:esi_remote/src/state/files.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

/// Uploading a file from the phone, measured at the wire rather than at the screen.
///
/// `/api/v1/files/upload` is authenticated by a single-use ticket, and the ticket is also the
/// only place the destination path is carried. The phone used to have a second path for files
/// under the signed-body cap - 8 MiB - which PUT the bytes straight there without asking for
/// one, on the grounds that it "keeps one code path for the common case". The agent requires a
/// ticket on that endpoint exactly, so every upload of a file of 8 MiB or less came back 401
/// "An upload ticket is required", while every file larger than that succeeded. Photographs,
/// documents, anything anyone actually has on a phone: refused.
///
/// What is measured here is the request sequence, because that is the whole defect. The screen
/// showed a failure; the failure was a request that was never going to be accepted.
class _RecordingApi extends EsiApi {
  _RecordingApi()
      : super(
          endpoint: const EsiEndpoint(host: '127.0.0.1', port: 8443, pins: <String>[]),
          identity: EsiIdentity(
            deviceId: 'test-device',
            keyId: 1,
            masterSecret: Uint8List.fromList(List<int>.filled(32, 7)),
          ),
          // No pinned client: nothing here ever reaches the network, so there is nothing to
          // pin. The client is never used because every method below is overridden.
          httpClient: http.Client(),
        );

  final List<_Call> calls = <_Call>[];

  @override
  Future<Map<String, dynamic>> getJson(
    String path, {
    List<(String, String)>? query,
    bool signed = true,
  }) async {
    calls.add(_Call('GET', path));
    // Tolerant by construction: `FileListing.fromJson` reads every field through a helper
    // that defaults rather than throws, so an empty object is a valid empty listing.
    return <String, dynamic>{};
  }

  @override
  Future<Map<String, dynamic>> postJson(
    String path, {
    Object? body,
    bool signed = true,
  }) async {
    calls.add(_Call('POST', path, body: body));
    if (path == '/files/upload-ticket') {
      return <String, dynamic>{
        'ticketId': 'ticket-1',
        'uploadUrl': '/api/v1/files/upload?ticket=ticket-1',
        'expiresAt': '2030-01-01T00:00:00Z',
        'maxBytes': 0,
      };
    }
    return <String, dynamic>{};
  }

  @override
  Future<Map<String, dynamic>> sendRaw({
    required String method,
    required String path,
    String rawQuery = '',
    Uint8List? body,
    String contentType = 'application/octet-stream',
    bool signed = true,
    Duration? timeout,
  }) async {
    calls.add(_Call(method, path, rawQuery: rawQuery, bodyLength: body?.length ?? 0));
    return <String, dynamic>{'completed': true, 'bytesWritten': body?.length ?? 0};
  }
}

class _Call {
  _Call(
    this.method,
    this.path, {
    this.rawQuery = '',
    this.body,
    this.bodyLength = 0,
  });

  final String method;
  final String path;
  final String rawQuery;
  final Object? body;
  final int bodyLength;

  @override
  String toString() => '$method $path${rawQuery.isEmpty ? '' : '?$rawQuery'}';
}

void main() {
  late Directory temp;

  setUp(() => temp = Directory.systemTemp.createTempSync('esi-upload'));
  tearDown(() => temp.deleteSync(recursive: true));

  File write(String name, Uint8List bytes) {
    final file = File('${temp.path}${Platform.pathSeparator}$name');
    file.writeAsBytesSync(bytes);
    return file;
  }

  /// The notifier under test, wired to [api] and given a moment for `build()`'s deferred
  /// `_openDefault()` to settle before anything is asserted about the call list.
  Future<FilesNotifier> notifierFor(_RecordingApi api) async {
    final container = ProviderContainer(overrides: [apiProvider.overrideWithValue(api)]);
    addTearDown(container.dispose);
    final notifier = container.read(filesProvider.notifier);
    await Future<void>.delayed(Duration.zero);
    return notifier;
  }

  test('an upload asks for a ticket before it sends a single byte', () async {
    final api = _RecordingApi();
    final notifier = await notifierFor(api);

    const target = 'C:/Users/me/Pictures/hello.txt';
    final file = write('hello.txt', Uint8List.fromList(List<int>.filled(64, 65)));

    final result = await notifier.upload(targetPath: target, source: file);

    expect(result.ok, isTrue, reason: 'the upload was refused: ${result.message}');
    expect(result.message, contains('64 bytes'), reason: 'the result was not counted honestly');

    // The order is the whole defect. The notifier opens the current directory before
    // anything is uploaded, so "first request" is not the claim - "before the bytes" is.
    final ticketIndex = api.calls.indexWhere((c) => c.path == '/files/upload-ticket');
    final putIndex = api.calls.indexWhere((c) => c.method == 'PUT');
    expect(ticketIndex, greaterThanOrEqualTo(0),
        reason: 'the upload never asked for a ticket: ${api.calls}');
    expect(putIndex, greaterThan(ticketIndex),
        reason: 'the bytes were sent before the destination was agreed: ${api.calls}');

    final ticket = api.calls.singleWhere((c) => c.path == '/files/upload-ticket');
    final body = ticket.body as Map<String, dynamic>;
    expect(body['path'], target, reason: 'the ticket was issued for a different destination');
    expect(body['size'], 64, reason: 'the ticket was issued for the wrong size');

    final puts = api.calls.where((c) => c.method == 'PUT').toList();
    expect(puts, hasLength(1), reason: 'expected exactly one upload: ${api.calls}');
    expect(puts.single.path, '/files/upload');
    expect(puts.single.rawQuery, contains('ticket=ticket-1'),
        reason: 'the bytes went to a ticket-authenticated endpoint without the ticket');
    expect(puts.single.bodyLength, 64, reason: 'the bytes on the wire were not the file');

    // The defect, named so it cannot come back quietly: nothing may reach the upload
    // endpoint without one, because the agent answers everything else401.
    for (final call in api.calls.where((c) => c.path == '/files/upload')) {
      expect(call.rawQuery, contains('ticket='),
          reason: '$call reached the upload endpoint unauthenticated');
    }
  });

  test('a file past the signed-body cap takes the same path', () async {
    // The branch that caused this used the signed-body cap as its condition, so the two sizes
    // either side of it are the ones worth pinning. If they ever disagree again, the phone has
    // a second way to send bytes and only one of them works.
    final api = _RecordingApi();
    final notifier = await notifierFor(api);

    const size = EsiProtocol.maxSignedBodyBytes + 1;
    final file = write('big.bin', Uint8List(size)..fillRange(0, size, 1));

    final result = await notifier.upload(targetPath: 'C:/Temp/big.bin', source: file);

    expect(result.ok, isTrue, reason: 'the upload was refused: ${result.message}');
    final ticketIndex = api.calls.indexWhere((c) => c.path == '/files/upload-ticket');
    final putIndex = api.calls.indexWhere((c) => c.method == 'PUT');
    expect(ticketIndex, greaterThanOrEqualTo(0),
        reason: 'a large upload took a different route: ${api.calls}');
    expect(putIndex, greaterThan(ticketIndex),
        reason: 'a large upload took a different route: ${api.calls}');
    expect((api.calls[ticketIndex].body as Map<String, dynamic>)['size'], size,
        reason: 'the ticket was issued for the wrong size');
    expect(api.calls[putIndex].rawQuery, contains('ticket='),
        reason: 'the upload skipped its ticket');
  });

  test('an upload that no PC can accept says so rather than looking like a network fault', () async {
    final api = _ApiThatRefusesTheTicket();
    final notifier = await notifierFor(api);

    final file = write('denied.txt', Uint8List.fromList(List<int>.filled(32, 1)));
    final result = await notifier.upload(targetPath: 'C:/Temp/denied.txt', source: file);

    expect(result.ok, isFalse, reason: 'a refused ticket was reported as success');
    expect(api.calls.where((c) => c.method == 'PUT'), isEmpty,
        reason: 'bytes were sent after the PC refused to issue a ticket');
    expect(result.message, isNotEmpty,
        reason: 'a silent failure leaves the phone saying nothing while the file never arrives');
  });
}

/// Stands in for the agent answering the ticket request with a scope or policy refusal.
class _ApiThatRefusesTheTicket extends _RecordingApi {
  @override
  Future<Map<String, dynamic>> postJson(
    String path, {
    Object? body,
    bool signed = true,
  }) async {
    if (path == '/files/upload-ticket') {
      throw EsiApiException(
        statusCode: 403,
        code: 'esi_forbidden_scope',
        message: 'This device is not permitted to upload files.',
      );
    }
    return super.postJson(path, body: body, signed: signed);
  }
}

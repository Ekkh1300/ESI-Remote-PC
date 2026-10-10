import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D45: a provider that kicked off async work from inside `build()`.
///
/// Riverpod does not give a Notifier its `state` until `build()` has returned. Work started
/// inline therefore reaches `state` while the provider is still uninitialised, and Riverpod
/// throws "Tried to read the state of an uninitialized provider".
///
/// It fails *quietly*: an unhandled async error does not stop the app, it just tears that one
/// provider down. The visible consequence was far away from the cause. `connection.dart`'s
/// `_load()` is what fills in the handshake, so when it threw, `supports('remote')` fell through
/// its `?? false` and the Screen tab reported:
///
///   "Screen control is switched off on the PC - the agent reports that its 'remote'
///     capability is disabled."
///
/// while the agent's own handshake plainly said `remote: true`. A working feature reported
/// itself as a disabled one, and the message pointed at the PC rather than at the phone.
///
/// The fix here is mechanical, which is exactly why it needs a guard. This bug had already been
/// found and fixed once - `connection.dart` carries a long comment about it - and with nothing to
/// stop it, the same pattern reappeared in seven other providers. A guard is the only thing that
/// makes "do not call unawaited() from build()" hold for the next person.
void main() {
  final stateDir = Directory('lib/src/state');

  /// True when the line sits inside a `build()` body, by brace depth.
  ///
  /// Scanning text rather than running the providers is what makes this check possible at all:
  /// the throw only happens at runtime, inside a live container, on a device.
  bool callsAsyncInline(List<String> lines, int index) {
    final line = lines[index];
    if (!RegExp(r'\bunawaited\s*\(').hasMatch(line)) return false;
    // A `scheduleMicrotask(() => unawaited(...))` is the fix, not the defect. It only counts if
    // the deferred call is on the same line, which is the shape every fix here takes.
    if (line.contains('scheduleMicrotask') || line.contains('microtask')) return false;
    // An explicit, reviewed opt-out. There is exactly one: `socket.recycle()`, which is the
    // socket's own method and cannot reach this notifier's `state`. Making a judgement call
    // about that invisibly would defeat the guard, so it has to be written down.
    if (line.contains('coverage:ignore-line')) return false;

    var depth = 0;
    var inside = false;
    for (var i = 0; i <= index; i++) {
      if (RegExp(r'^\s{2}[\w<>,\s?]+\sbuild\s*\(\s*\)\s*\{').hasMatch(lines[i])) {
        inside = true;
        depth = 0;
      }
      if (!inside) continue;

      depth += '{'.allMatches(lines[i]).length;
      depth -= '}'.allMatches(lines[i]).length;

      if (i == index) return true;
      // build() ended: its closing brace brings depth back to zero.
      if (depth <= 0) inside = false;
    }
    return false;
  }

  /// True when the line sits inside a closure that `build()` merely schedules.
///
/// `ref.onDispose(() { ... })`, `Future.microtask(() { ... })` and
/// `scheduleMicrotask(() { ... })` all run after `build()` has returned, so an `unawaited` in
/// any of them is correct and must not be flagged. Only a call that runs *during* `build()` is
/// the defect.
  bool inDeferredClosure(List<String> lines, int index) {
    // Any brace opened above this line inside build() that closes *after* it means the line is
    // in a nested scope. Tracked with a stack so a closure closed before this line does not
    // count, which is what made the first version of this flag the fixed sites too.
    final scopes = <String>[];
    var inBuild = false;
    var depth = 0;

    for (var i = 0; i <= index; i++) {
      if (RegExp(r'^\s{2}[\w<>,\s?]+\sbuild\s*\(\s*\)\s*\{').hasMatch(lines[i])) {
        inBuild = true;
        depth = 0;
        scopes.clear();
      }
      if (!inBuild) continue;

      final line = lines[i];
      final deferred =
          RegExp(r'(onDispose|scheduleMicrotask|microtask|then|addPostFrameCallback)\s*\(')
              .hasMatch(line);

      // Count what this line opens after any deferred call on it.
      var net = '{'.allMatches(line).length - '}'.allMatches(line).length;
      if (deferred) {
        // Everything from here until this scope closes is deferred.
        scopes.add('deferred');
        // A one-liner `() => x` opens nothing; only a `() {` does.
        if (!RegExp(r'\(\s*\)\s*(async\s*)?\{').hasMatch(line)) scopes.removeLast();
      }

      if (i == index) return scopes.contains('deferred');

      depth += net;
      for (var d = 0; d < -net; d++) {
        if (scopes.isNotEmpty) scopes.removeLast();
      }
      if (depth <= 0) inBuild = false;
    }
    return false;
  }

  test('no provider starts async work from inside build()', () {
    expect(stateDir.existsSync(), isTrue,
        reason: 'the state directory moved; this guard has to follow it');

    final offenders = <String>[];

    for (final file in stateDir.listSync().whereType<File>()) {
      if (!file.path.endsWith('.dart')) continue;
      final lines = file.readAsLinesSync();
      for (var i = 0; i < lines.length; i++) {
        if (callsAsyncInline(lines, i) && !inDeferredClosure(lines, i)) {
          offenders.add('${file.path}:${i + 1}  ${lines[i].trim()}');
        }
      }
    }

    expect(offenders, isEmpty,
        reason: 'D45. A provider that calls unawaited() inside build() touches state before '
            'Riverpod has given it one, and Riverpod throws. The error is unhandled, so the '
            'provider is torn down silently and the feature it feeds reports itself as '
            'disabled. Defer it:\n'
            '  scheduleMicrotask(() => unawaited(work()));\n'
            'Offenders:\n  ${offenders.join('\n  ')}');
  });

  test('the connection provider really does defer its handshake load', () {
    // The specific instance that broke the Screen tab, pinned separately so that changing the
    // line above cannot quietly make this guard pass while the real defect returns.
    final source = File('lib/src/state/connection.dart').readAsStringSync();

    expect(source, contains('scheduleMicrotask(_load);'),
        reason: 'connection.dart must load the handshake from a microtask, not from build(). '
            'Without the handshake, supports("remote") is false and the Screen tab claims the '
            'PC has remote control switched off.');
  });
}
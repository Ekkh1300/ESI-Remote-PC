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
/// `ref.onDispose(() { ... })`, `Future.microtask(() { ... })`,
/// `scheduleMicrotask(() { ... })` and `ref.listen(..., (…) { ... })` all run after
/// `build()` has returned, so an `unawaited` in any of them is correct and must not be
/// flagged. Only a call that runs *during* `build()` is the defect.
  bool inDeferredClosure(List<String> lines, int index) {
    // Scope tracking that pairs every opened brace with its close. Each brace-open is
    // marked: a deferred call's own blocks are 'deferred', every other open is 'plain'.
    // Only counting pushes-and-pops symmetrically is what makes an ordinary `if (...) {`
    // inside a Riverpod listener unable to end the listener's deference by closing before
    // the callback does, which is exactly the mis-pairing that flagged the fixed sites.
    final scopes = <bool>[]; // true == this scope was opened by a deferred call
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
      // `listen` deliberately matches both of this app's two shapes: Riverpod's
      // `ref.listen(provider, (…) { … })` and a stream's `.listen((…) { … })`. Neither a
      // Riverpod listener nor a stream subscription callback runs during build - they fire
      // when their event does - so a body of either is exactly as deferred as a dispose
      // callback, and the same unawaited() rule that is safe there is safe here.
      final deferred =
          RegExp(r'(onDispose|scheduleMicrotask|microtask|then|addPostFrameCallback|listen)\s*\(')
              .hasMatch(line);

      // The current line's braces decide what runs at its column only through the scopes
      // opened *above* it, so membership is read before this line's own opens land. A
      // deferred line that puts its whole body on itself - `onDispose(() { unawaited(x); });`
      // - still counts, because those braces balance to net zero and nothing changes.
      if (i == index) return scopes.contains(true);

      // Apply this line's brace traffic: every open is remembered as deferred or plain,
      // every close is paired with the most recent open. Even if the two never meet again,
      // the pairing cannot dull a block that merely interleaves with the callback.
      for (var o = 0; o < '{'.allMatches(line).length; o++) {
        scopes.add(deferred);
        depth++;
      }
      for (var d = 0; d < '}'.allMatches(line).length; d++) {
        if (scopes.isNotEmpty) scopes.removeLast();
        depth--;
      }

      // build() ended: its closing brace brings depth back to zero.
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
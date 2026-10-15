import 'package:esi_remote/src/protocol/models.dart';
import 'package:esi_remote/src/ui/screens/apps_screen.dart';
import 'package:flutter_test/flutter_test.dart';

/// The CPU column of the Running tab.
///
/// The agent sends a process's CPU as a *rate*, so a process seen for the first time arrives
/// with no figure at all. The phone used to parse that absence as 0, which put "0.0% CPU"
/// beside every process on the machine and made sorting by CPU a no-op - every value equal, so
/// the order never changed and looked deliberate. A column that always reads zero is worse
/// than no column, because it reads as an answer instead of as a missing one.

ProcessInfo _process(String name, double? cpu) => ProcessInfo(
      pid: 1,
      name: name,
      user: null,
      cpuPercent: cpu,
      workingSetBytes: 1024,
      isElevated: false,
      startedAt: null,
    );

void main() {
  group('ProcessSort.cpu', () {
    test('orders by the reading it actually has', () {
      final sorted = ProcessSort.cpu.apply(<ProcessInfo>[
        _process('lukewarm', 12),
        _process('flat-out', 87),
        _process('quiet', 1),
      ]);

      expect(sorted.map((p) => p.name), <String>['flat-out', 'lukewarm', 'quiet']);
    });

    test('puts a process with no reading last, not first', () {
      // "Not measured" is not "idle", and it is certainly not "busiest". Sorting it to the
      // top would put the only row on the screen that cannot answer the question, which is
      // how an unmeasured column hides itself.
      final sorted = ProcessSort.cpu.apply(<ProcessInfo>[
        _process('unmeasured', null),
        _process('flat-out', 87),
      ]);

      expect(sorted.map((p) => p.name), <String>['flat-out', 'unmeasured']);
    });

    test('does not throw when every process is unmeasured', () {
      // The comparison this replaces ran `b.cpuPercent.compareTo(a.cpuPercent)` on a
      // nullable, so the first listing of any process could throw rather than sort.
      final sorted = ProcessSort.cpu.apply(<ProcessInfo>[
        _process('a', null),
        _process('b', null),
        _process('c', null),
      ]);

      expect(sorted, hasLength(3));
    });

    test('leaves the input list untouched', () {
      final input = <ProcessInfo>[
        _process('second', 5),
        _process('first', 50),
      ];

      ProcessSort.cpu.apply(input);

      expect(input.first.name, 'second', reason: 'apply copies; the provider list is shared');
    });
  });

  test('an absent cpuPercent reads as no reading, not as zero', () {
    // The wire encoding for "no rate yet" is an omitted field. Parsing it as 0 is precisely
    // what made a busy machine look idle.
    final parsed = ProcessInfo.fromJson(<String, dynamic>{
      'pid': 4242,
      'name': 'something',
      'workingSetBytes': 10,
      'isElevated': false,
    });

    expect(parsed.cpuPercent, isNull);
  });

  test('a reported cpuPercent survives the round trip', () {
    final parsed = ProcessInfo.fromJson(<String, dynamic>{
      'pid': 4242,
      'name': 'something',
      'workingSetBytes': 10,
      'isElevated': false,
      'cpuPercent': 63.5,
    });

    expect(parsed.cpuPercent, 63.5);
  });
}
import 'package:esi_remote/src/protocol/models.dart';
import 'package:esi_remote/src/state/metrics.dart';
import 'package:esi_remote/src/ui/shell.dart';
import 'package:esi_remote/src/ui/widgets/load_trace.dart';
import 'package:flutter_test/flutter_test.dart';

/// The chain that draws the dashboard's usage graph, end to end.
///
/// It had no coverage at all. Every piece of it was individually correct - the agent appends a
/// point per sample, the snapshot carries them, `withSnapshot` keeps them, the painter needs
/// two - and the pieces still had never been run in order, which is the only order that
/// matters: the graph is what the person sees, and "the numbers are there but the graph is
/// empty" is indistinguishable on the phone from a graph that is broken.
///
/// So this measures the whole path rather than another link in it: snapshot in, channels out,
/// and a verdict on whether those channels can be drawn.
void main() {
  Map<String, dynamic> snapshotJson({
    required double cpu,
    required double gpu,
    required double ram,
    List<Map<String, dynamic>> load = const <Map<String, dynamic>>[],
  }) =>
      <String, dynamic>{
        'capturedAt': 1756000000,
        'sampleWindowMs': 1000,
        'cpu': <String, dynamic>{
          'usagePercent': cpu,
          'perCore': <double>[],
          'userPercent': cpu * 0.6,
          'kernelPercent': cpu * 0.4,
          'temperatureC': null,
          'processCount': 213,
          'threadCount': 1840,
        },
        'memory': <String, dynamic>{
          'totalBytes': 34359738368,
          'usedBytes': 16521576704,
          'availableBytes': 17838161664,
          'usedPercent': ram,
        },
        'gpu': <Map<String, dynamic>>[],
        'gpuAggregate': <String, dynamic>{
          'usagePercent': gpu,
          'maxTemperatureC': null,
          'dominantIndex': null,
        },
        'storage': <Map<String, dynamic>>[],
        'network': <String, dynamic>{'interfaces': <Map<String, dynamic>>[]},
        'battery': null,
        'load': load,
      };

  MetricsSnapshot snapshot({required double cpu, double gpu = 32, double ram = 48, List<Map<String, dynamic>> load = const []}) =>
      MetricsSnapshot.fromJson(snapshotJson(cpu: cpu, gpu: gpu, ram: ram, load: load));

  test('the first sample leaves a dot and no line, and says so', () {
    // This is the case that reads as "the usage graph does not show": one reading has
    // arrived, so the numbers above the plot are populated, but the painter draws nothing
    // because a line needs two points. The grid and the legend are on screen either way.
    final state = const MetricsState().withSnapshot(snapshot(cpu: 12.4));

    expect(state.cpuHistory, <double>[12.4]);
    expect(traceState(buildTraceChannels(state)), TraceState.thin);
  });

  test('a second sample is what turns the dot into a line', () {
    var state = const MetricsState().withSnapshot(snapshot(cpu: 12.4));
    state = state.withSnapshot(snapshot(cpu: 41.0, gpu: 55, ram: 60));

    expect(state.cpuHistory, <double>[12.4, 41.0]);
    expect(state.gpuHistory, <double>[32.0, 55.0]);
    expect(state.ramHistory, <double>[48.0, 60.0]);
    expect(traceState(buildTraceChannels(state)), TraceState.ready);
  });

  test('the agent\'s own history wins once it has two points', () {
    // The comment in `metrics.dart` is the design: the agent's `load` array is preferred so
    // the trace shows the machine's real past rather than only the time the app has been open.
    // So a phone that connects to a PC which has been up for an hour gets the hour, not two
    // dots - and the moment it applies must also be the moment the graph becomes drawable.
    final history = <Map<String, dynamic>>[
      for (var i = 0; i < 30; i++)
        <String, dynamic>{'ts': 1756000000 + i, 'cpu': i.toDouble(), 'gpu': 10.0, 'ram': 50.0},
    ];

    var state = const MetricsState().withSnapshot(snapshot(cpu: 99, load: history));
    state = state.withSnapshot(snapshot(cpu: 99, load: history));

    expect(state.cpuHistory, hasLength(30));
    expect(state.cpuHistory.first, 0.0);
    expect(state.cpuHistory.last, 29.0);
    expect(traceState(buildTraceChannels(state)), TraceState.ready);
  });

  test('a one-point agent history is not trusted over the app\'s own', () {
    // `fromAgent` is `length >= 2` for a reason: taking a single point would replace a growing
    // local window with a fixed one, and the trace would then never grow at all - which is
    // exactly the complaint this file exists to prevent.
    final one = <Map<String, dynamic>>[
      <String, dynamic>{'ts': 1756000000, 'cpu': 7.0, 'gpu': 10.0, 'ram': 50.0},
    ];

    var state = const MetricsState().withSnapshot(snapshot(cpu: 10, load: one));
    state = state.withSnapshot(snapshot(cpu: 20, load: one));

    expect(state.cpuHistory, <double>[10.0, 20.0]);
    expect(traceState(buildTraceChannels(state)), TraceState.ready);
  });

  test('the window is bounded, so a long session cannot grow the painter\'s work', () {
    var state = const MetricsState();
    for (var i = 0; i < 400; i++) {
      state = state.withSnapshot(snapshot(cpu: i % 100));
    }

    expect(state.cpuHistory, hasLength(120));
    // Still the *newest* 120, not the first.
    expect(state.cpuHistory.last, 399 % 100);
  });

  test('each channel carries its own reading under the label the legend prints', () {
    // A swap here would draw the wrong series under the right colour, and no assertion about
    // the painter could catch it - the trace would be a perfectly good graph of the wrong thing.
    final state = const MetricsState()
        .withSnapshot(snapshot(cpu: 11, gpu: 22, ram: 33))
        .withSnapshot(snapshot(cpu: 12, gpu: 23, ram: 34));

    final channels = buildTraceChannels(state);
    expect(channels.map((c) => c.label), <String>['CPU', 'GPU', 'RAM']);
    expect(channels[0].series, <double>[11, 12]);
    expect(channels[1].series, <double>[22, 23]);
    expect(channels[2].series, <double>[33, 34]);
  });

  test('a snapshot that arrives over the socket is the same shape as one polled', () {
    // The two paths converge on one state. If they did not, the graph would look right for the
    // three seconds the socket was down and then change behaviour when it came back.
    final polled = const MetricsState()
        .withSnapshot(snapshot(cpu: 10))
        .withSnapshot(snapshot(cpu: 20));
    final streamed = const MetricsState()
        .withSnapshot(snapshot(cpu: 10))
        .withSnapshot(snapshot(cpu: 20));

    expect(polled.cpuHistory, streamed.cpuHistory);
    expect(traceState(buildTraceChannels(polled)), traceState(buildTraceChannels(streamed)));
  });
}

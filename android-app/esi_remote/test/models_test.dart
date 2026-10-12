/// Model parsing, against the exact JSON the agent emits.
///
/// The JSON in each case is copied from the shapes in
/// `windows-agent/src/ESI.RemotePc.Protocol/Contracts/` and PROTOCOL.md, field for field.
/// The point of these tests is not that `fromJson` works on well-formed input — it is that
/// a machine with no temperature sensor produces `null` and the app keeps it `null` all the
/// way to the screen, rather than somewhere in between a `double?` quietly becoming a `0`.
library;

import 'package:esi_remote/src/protocol/models.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
import 'package:esi_remote/src/ui/format.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('Handshake', () {
    test('parses the documented response', () {
      final handshake = Handshake.fromJson(<String, dynamic>{
        'protocolVersion': 1,
        'agentVersion': '1.0.0',
        'deviceId': '3f2a9c17d4b8e206',
        'deviceName': 'META-PC',
        'hostName': 'META-PC',
        'httpsPort': 48220,
        'discoveryPort': 48211,
        'spkiSha256': 'MIIBIjANBgkq...=',
        'tlsSubject': 'CN=META-PC',
        'notBefore': 1756000000,
        'notAfter': 1787536000,
        'serverTime': 1756000000,
        'pairingRequired': true,
        'pairingOpen': true,
        'maxPairedDevices': 8,
        'pairedDeviceCount': 1,
        'features': <String, bool>{
          'power': true,
          'metrics': true,
          'remote': false,
        },
      });

      expect(handshake.isCompatible, isTrue);
      expect(handshake.deviceName, 'META-PC');
      expect(handshake.httpsPort, 48220);
      expect(handshake.supports('power'), isTrue);
      expect(handshake.supports('remote'), isFalse);
      // A feature the agent did not mention is not enabled.
      expect(handshake.supports('gaming'), isFalse);
    });

    test('refuses a different major protocol version', () {
      final handshake = Handshake.fromJson(<String, dynamic>{
        'protocolVersion': 2,
        'deviceId': 'x',
      });
      expect(handshake.isCompatible, isFalse);
    });

    test('survives an empty or hostile response', () {
      final handshake = Handshake.fromJson(<String, dynamic>{});
      expect(handshake.deviceId, isEmpty);
      expect(handshake.httpsPort, EsiProtocol.defaultHttpsPort);
      expect(handshake.discoveryPort, EsiProtocol.defaultDiscoveryPort);
      expect(handshake.pairingRequired, isTrue);
      expect(handshake.features, isEmpty);
    });
  });

  group('PairStatus', () {
    test('reads the approved state with its envelope', () {
      final status = PairStatus.fromJson(<String, dynamic>{
        'status': 'approved',
        'deviceId': '3f2a9c17d4b8e206',
        'keyId': 1,
        'spkiSha256': 'pin',
        'wrappedSecrets': <String, dynamic>{
          'nonce': 'QEFCQ0RFRkdISUpL',
          'ciphertext': 'AAAA',
          'tag': 'AAAA',
        },
      });
      expect(status.isApproved, isTrue);
      expect(status.deviceId, '3f2a9c17d4b8e206');
      expect(status.keyId, 1);
      expect(status.wrappedSecrets, isNotNull);
    });

    test('reads the terminal states the agent can return', () {
      for (final state in <String>['pending', 'denied', 'expired', 'consumed', 'error', 'unknown']) {
        final status = PairStatus.fromJson(<String, dynamic>{'status': state});
        expect(status.status, state);
        expect(status.wrappedSecrets, isNull);
      }
    });
  });

  group('MetricsSnapshot', () {
    final Map<String, dynamic> metrics = <String, dynamic>{
      'capturedAt': 1756000000,
      'sampleWindowMs': 1000,
      'cpu': <String, dynamic>{
        'usagePercent': 12.4,
        'perCore': <double>[3.1, 9.0],
        'userPercent': 7.2,
        'kernelPercent': 5.2,
        'temperatureC': 61.5,
        'clockMhz': 4200.0,
        'maxClockMhz': 4900,
        'processCount': 213,
        'threadCount': 1840,
      },
      'memory': <String, dynamic>{
        'totalBytes': 34359738368,
        'usedBytes': 16521576704,
        'availableBytes': 17838161664,
        'usedPercent': 48.1,
        'cachedBytes': 2147483648,
        'committedBytes': 18000000000,
        'commitLimitBytes': 51539607552,
        'swap': <String, dynamic>{'totalBytes': 0, 'usedBytes': 0},
      },
      'gpu': <Map<String, dynamic>>[
        <String, dynamic>{
          'index': 0,
          'name': 'NVIDIA GeForce RTX 4060',
          'usagePercent': 32.0,
          'memoryUsedBytes': 1207959552,
          'memoryTotalBytes': 8589934592,
          'temperatureC': 58,
          'fanRpm': 0,
          'powerWatts': null,
          'vendor': 'nvidia',
          'source': 'nvml',
        },
      ],
      'gpuAggregate': <String, dynamic>{
        'usagePercent': 32.0,
        'maxTemperatureC': 58,
        'dominantIndex': 0,
      },
      'storage': <Map<String, dynamic>>[
        <String, dynamic>{
          'deviceId': 'C:',
          'usedPercent': 78.2,
          'usedBytes': 800000000000,
          'freeBytes': 224000000000,
          'totalBytes': 1024000000000,
          'readBytesPerSecond': 1024,
          'writeBytesPerSecond': 4096,
        },
      ],
      'network': <String, dynamic>{
        'interfaces': <Map<String, dynamic>>[],
        'primary': <String, dynamic>{
          'id': 'wifi-0',
          'name': 'Wi-Fi',
          'type': 'wifi',
          'isUp': true,
          'ipv4': '192.168.1.42',
          'linkSpeedBitsPerSecond': 866000000,
          'downloadBytesPerSecond': 12500,
          'uploadBytesPerSecond': 3200,
        },
        'pingMs': 3.2,
      },
      'battery': null,
      'load': <Map<String, dynamic>>[
        <String, dynamic>{'ts': 1756000000, 'cpu': 12.4, 'gpu': 32.0, 'ram': 48.1},
      ],
    };

    test('parses the documented snapshot', () {
      final snapshot = MetricsSnapshot.fromJson(metrics);
      expect(snapshot.cpu.usagePercent, 12.4);
      expect(snapshot.cpu.perCore, <double>[3.1, 9.0]);
      expect(snapshot.memory.usedBytes, 16521576704);
      expect(snapshot.gpu.single.name, 'NVIDIA GeForce RTX 4060');
      expect(snapshot.gpu.single.source, 'nvml');
      expect(snapshot.storage.single.deviceId, 'C:');
      expect(snapshot.network.primary!.ipv4, '192.168.1.42');
      expect(snapshot.battery, isNull);
      expect(snapshot.load.single.gpu, 32.0);
    });

    test('a null temperature stays null', () {
      // §6.2: "Temperatures are null (never a fabricated number) when no trustworthy
      // sensor exists." This is the single most important assertion in the file.
      final withoutSensors = MetricsSnapshot.fromJson(<String, dynamic>{
        ...metrics,
        'cpu': <String, dynamic>{'usagePercent': 5.0, 'temperatureC': null},
        'gpu': <Map<String, dynamic>>[
          <String, dynamic>{'name': 'AMD Radeon', 'usagePercent': 1.0, 'temperatureC': null},
        ],
        'gpuAggregate': <String, dynamic>{'usagePercent': 1.0, 'maxTemperatureC': null},
      });
      expect(withoutSensors.cpu.temperatureC, isNull);
      expect(withoutSensors.gpu.single.temperatureC, isNull);
      expect(withoutSensors.gpuAggregate.maxTemperatureC, isNull);

      // And it must format as the em dash, not as 0, not as 0.0, and not as a guess.
      expect(formatCelsius(withoutSensors.cpu.temperatureC), notReported);
      expect(formatCelsius(withoutSensors.cpu.temperatureC), isNot('0.0 °C'));
      expect(formatCelsius(withoutSensors.cpu.temperatureC), isNot(contains('0')));
    });

    test('a missing temperature field is treated as unreported, not zero', () {
      final sparse = MetricsSnapshot.fromJson(<String, dynamic>{
        'cpu': <String, dynamic>{'usagePercent': 5.0},
        'memory': <String, dynamic>{'usedBytes': 1, 'totalBytes': 2},
        'gpu': <Map<String, dynamic>>[],
        'storage': <Map<String, dynamic>>[],
        'network': <String, dynamic>{'interfaces': <Map<String, dynamic>>[]},
      });
      expect(sparse.cpu.temperatureC, isNull);
      expect(sparse.cpu.clockMhz, isNull);
      expect(sparse.cpu.perCore, isEmpty);
      expect(sparse.network.primary, isNull);
      expect(sparse.load, isEmpty);
    });

    test('a GPU list is empty rather than null when there is no adapter', () {
      final none = MetricsSnapshot.fromJson(<String, dynamic>{
        'gpu': <Map<String, dynamic>>[],
        'storage': <Map<String, dynamic>>[],
      });
      expect(none.gpu, isEmpty);
      expect(none.storage, isEmpty);
    });

    test('an empty per-core array is preserved, not defaulted', () {
      // §6.2 field note: perCore may be [] when the per-core counters are unavailable.
      final noCores = MetricsSnapshot.fromJson(<String, dynamic>{
        'cpu': <String, dynamic>{'usagePercent': 1.0, 'perCore': <dynamic>[]},
      });
      expect(noCores.cpu.perCore, isEmpty);
    });
  });

  group('SystemInfo', () {
    test('parses and keeps nullable sensor fields nullable', () {
      final info = SystemInfo.fromJson(<String, dynamic>{
        'hostName': 'META-PC',
        'deviceName': 'META-PC',
        'manufacturer': 'Dell Inc.',
        'model': 'XPS 15',
        'os': <String, dynamic>{
          'caption': 'Windows 11 Pro',
          'displayVersion': '24H2',
          'build': 26100,
          'uptimeSeconds': 10000,
          'architecture': 'x64',
        },
        'cpu': <String, dynamic>{'model': 'i7-13700H', 'cores': 14, 'threads': 20},
        'gpu': <Map<String, dynamic>>[
          <String, dynamic>{'name': 'RTX 4060', 'vramBytes': 8589934592, 'isPrimary': true},
        ],
        'memory': <String, dynamic>{'totalBytes': 34359738368, 'slots': 2},
        'storage': <Map<String, dynamic>>[
          <String, dynamic>{'deviceId': 'C:', 'totalBytes': 1, 'freeBytes': 2},
        ],
        'bios': <String, dynamic>{'version': '1.24.0'},
        'security': <String, dynamic>{'secureBoot': true, 'tpmPresent': false},
        'power': <String, dynamic>{'supportsSleep': true, 'activePowerScheme': 'Balanced'},
        'sensors': <String, dynamic>{
          'cpuTemperatureC': null,
          'gpuTemperatureC': null,
          'sensorsAvailable': <String>[],
        },
        'agent': <String, dynamic>{
          'version': '1.0.0',
          'uptimeSeconds': 3600,
          'sessionAgentConnected': false,
          'sessionAgentState': 'NotRunning',
        },
      });

      expect(info.displayName, 'Dell Inc. XPS 15');
      expect(info.os.caption, 'Windows 11 Pro');
      expect(info.cpu.cores, 14);
      expect(info.gpu.single.isPrimary, isTrue);
      expect(info.sensors.cpuTemperatureC, isNull);
      expect(info.security.secureBoot, isTrue);
      expect(info.security.tpmPresent, isFalse);
      expect(info.agent.sessionAgentConnected, isFalse);
      expect(info.biosVersion, '1.24.0');
    });
  });

  group('power', () {
    test('parses a status', () {
      final status = PowerStatus.fromJson(<String, dynamic>{
        'state': 'online',
        'isLocked': false,
        'loggedIn': true,
        'activeUser': 'META-PC\\META',
        'supportsSleep': true,
        'lastBoot': 1755990000,
      });
      expect(status.state, 'online');
      expect(status.activeUser, 'META-PC\\META');
      expect(status.pendingPowerAction, isNull);
    });

    test('parses an action result including a null warning', () {
      final result = PowerActionResult.fromJson(<String, dynamic>{
        'accepted': true,
        'action': 'restart',
        'effectiveMode': 'hybrid',
        'scheduledAt': 1756000005,
        'warning': null,
        'requiresInteractiveSession': false,
      });
      expect(result.accepted, isTrue);
      expect(result.warning, isNull);
      expect(result.scheduledAt, 1756000005);
    });
  });

  group('files', () {
    test('parses a listing with hidden and read-only entries', () {
      final listing = FileListing.fromJson(<String, dynamic>{
        'path': 'C:\\Users\\META\\Downloads',
        'parent': 'C:\\Users\\META',
        'entries': <Map<String, dynamic>>[
          <String, dynamic>{
            'name': 'Videos',
            'path': 'C:\\Users\\META\\Downloads\\Videos',
            'isDirectory': true,
            'childCount': 12,
            'canDelete': true,
            'canRename': true,
          },
          <String, dynamic>{
            'name': 'report.pdf',
            'path': 'C:\\Users\\META\\Downloads\\report.pdf',
            'isDirectory': false,
            'sizeBytes': 1048576,
            'isReadOnly': true,
            'extension': 'pdf',
            'mimeType': 'application/pdf',
            'lastModified': 1755999000,
          },
        ],
        'truncated': false,
        'readOnly': false,
        'totalEntries': 2,
      });
      expect(listing.parent, 'C:\\Users\\META');
      expect(listing.entries, hasLength(2));
      expect(listing.entries.first.isDirectory, isTrue);
      expect(listing.entries.first.sizeBytes, 0);
      expect(listing.entries.last.isReadOnly, isTrue);
      expect(listing.entries.last.extension, 'pdf');
    });

    test('parses the roots document and its flags', () {
      final roots = FileRoots.fromJson(<String, dynamic>{
        'roots': <Map<String, dynamic>>[
          <String, dynamic>{
            'path': 'C:\\Users\\META\\Downloads',
            'label': 'Downloads',
            'readOnly': false,
            'exists': true,
          },
        ],
        'defaultRoot': 'C:\\Users\\META\\Downloads',
        'readOnly': false,
        'allowDelete': true,
        'allowUpload': true,
        'maxUploadBytes': 2147483648,
      });
      expect(roots.roots.single.label, 'Downloads');
      expect(roots.allReadOnly, isFalse);
      expect(roots.maxUploadBytes, 2 * 1024 * 1024 * 1024);
    });
  });

  group('gaming', () {
    test('parses a state with a null fps and null temperature', () {
      final state = GamingState.fromJson(<String, dynamic>{
        'mode': 'off',
        'fps': null,
        'fpsSource': null,
        'gpuTemperatureC': null,
        'cpuUsagePercent': 4.0,
        'suspendedApps': <String>['discord'],
        'history': <Map<String, dynamic>>[
          <String, dynamic>{'ts': 1, 'fps': 144, 'cpu': 88, 'gpu': 97, 'temp': null},
        ],
      });
      expect(state.isOn, isFalse);
      expect(state.fps, isNull);
      expect(state.gpuTemperatureC, isNull);
      expect(state.suspendedApps, <String>['discord']);
      expect(state.history.single.fps, 144);
      expect(state.history.single.temp, isNull);
      expect(formatPercent(state.fps?.toDouble()), notReported);
    });
  });

  group('settings', () {
    test('round-trips through toJson without losing fields', () {
      // The agent treats PUT /settings as a replace, so anything the app does not model
      // has to survive the round trip. This checks the document it sends is complete.
      final original = AgentSettings.fromJson(<String, dynamic>{
        'port': 48220,
        'discoveryEnabled': true,
        'discoveryPort': 48211,
        'mdnsEnabled': true,
        'startWithWindows': true,
        'wakeOnLanEnabled': true,
        'security': <String, dynamic>{
          'localNetworkOnly': true,
          'requireSignedRequests': true,
          'allowedSubnets': <String>['192.168.0.0/16'],
          'maxPairedDevices': 8,
          'rateLimitPerMinute': 240,
          'pairingWindowSeconds': 120,
        },
        'metrics': <String, dynamic>{'sampleIntervalMs': 1000, 'historyLimit': 300},
        'files': <String, dynamic>{
          'allowedRoots': <Map<String, dynamic>>[
            <String, dynamic>{'path': 'C:\\Users\\META\\Downloads', 'readOnly': false},
          ],
          'allowDelete': true,
          'allowUpload': true,
          'maxUploadBytes': 2147483648,
        },
        'apps': <Map<String, dynamic>>[],
        'gaming': <String, dynamic>{'enabled': false, 'profileId': 'performance'},
        'notifications': <String, dynamic>{'retentionDays': 14, 'toastEnabled': true},
        'debug': <String, dynamic>{'enabled': false},
      });

      final reparsed = AgentSettings.fromJson(original.toJson());
      expect(reparsed.toJson(), original.toJson());
      expect(reparsed.security.allowedSubnets, <String>['192.168.0.0/16']);
      expect(reparsed.files.allowedRoots.single.path, 'C:\\Users\\META\\Downloads');
      expect(reparsed.gaming.profileId, 'performance');
    });
  });

  group('MediaState', () {
    test('parses the documented response', () {
      final state = MediaState.fromJson(<String, dynamic>{
        'volume': 62,
        'volumeKnown': true,
        'isMuted': false,
        'isMuteKnown': true,
        'defaultDevice': 'Speakers (Realtek(R) Audio)',
        'sessionPeak': 100,
        'availableActions': <String>['volumeUp', 'volumeDown', 'volumeSet'],
        'supported': true,
      });

      expect(state.volume, 62);
      expect(state.volumeKnown, isTrue);
      expect(state.isMuted, isFalse);
      expect(state.isMuteKnown, isTrue);
      expect(state.defaultDevice, 'Speakers (Realtek(R) Audio)');
    });

    test('a volume that could not be read stays unknown rather than becoming a zero', () {
      // The whole defect this flag exists for. The agent answered a poll from a process that
      // could not open an audio endpoint at all - the Windows service, in session 0 - and the
      // number it had for "unknown" was 0. Zero is also a perfectly ordinary volume, so the
      // phone showed a confident silent beside a slider the user had just moved, and nothing on
      // the screen said which of the two was real.
      //
      // Same shape as the metrics tests above: a value the machine could not supply must stay
      // distinguishable from a value the machine supplied.
      final state = MediaState.fromJson(<String, dynamic>{
        'volume': 0,
        'volumeKnown': false,
        'isMuted': false,
        'isMuteKnown': false,
        'defaultDevice': null,
        'sessionPeak': 100,
        'availableActions': <String>['volumeSet'],
        'supported': true,
      });

      expect(state.volumeKnown, isFalse,
          reason: 'an unreadable volume came back looking like a definite zero');
      expect(state.isMuteKnown, isFalse);

      // And the control goes with it. A slider that still accepts a drag here would move,
      // commit, and then snap back to a number nobody measured - which reads as the PC refusing
      // the change, when the truth is that the PC was never asked.
      expect(state.canSetVolume, isFalse);
    });

    test('the volume control is usable when the agent allows it and the level was read', () {
      final state = MediaState.fromJson(<String, dynamic>{
        'volume': 44,
        'volumeKnown': true,
        'isMuted': false,
        'isMuteKnown': true,
        'sessionPeak': 100,
        'availableActions': <String>['volumeSet', 'volumeUp', 'volumeDown'],
        'supported': true,
      });

      expect(state.canSetVolume, isTrue);
    });

    test('an agent that disallows volumeSet still disables the control', () {
      // The other half of the same getter: a read level is not enough on its own.
      final state = MediaState.fromJson(<String, dynamic>{
        'volume': 44,
        'volumeKnown': true,
        'isMuted': false,
        'isMuteKnown': true,
        'sessionPeak': 100,
        'availableActions': <String>['volumeUp', 'volumeDown'],
        'supported': true,
      });

      expect(state.can('volumeSet'), isFalse);
      expect(state.canSetVolume, isFalse);
    });

    test('an agent that predates the flags is still read as knowing the volume', () {
      // Defaults to true, so a 1.0.1 agent without the flags keeps working instead of greying
      // out every slider it has. The failure to guard against is the flag being *present* and
      // false, not the flag being absent.
      final state = MediaState.fromJson(<String, dynamic>{
        'volume': 30,
        'isMuted': true,
        'sessionPeak': 100,
        'availableActions': <String>[],
        'supported': true,
      });

      expect(state.volumeKnown, isTrue);
      expect(state.isMuteKnown, isTrue);
    });
  });

  group('errors', () {
    test('parses the error envelope', () {
      final body = EsiApiErrorBody.fromJson(<String, dynamic>{
        'code': 'esi_signature',
        'message': 'Signature mismatch',
        'requestId': '9c1f…',
      });
      expect(body.code, 'esi_signature');
      expect(body.message, 'Signature mismatch');
      expect(body.requestId, '9c1f…');
    });

    test('falls back to a usable code when the envelope is empty', () {
      final body = EsiApiErrorBody.fromJson(<String, dynamic>{});
      expect(body.code, EsiProtocol.errInternal);
      expect(body.message, isNotEmpty);
    });
  });

  group('scopes', () {
    test('parses the scope names the agent sends', () {
      final who = WhoAmI.fromJson(<String, dynamic>{
        'deviceId': '3f2a9c17d4b8e206',
        'keyId': 1,
        'scopes': <String>['read', 'files', 'control', 'admin'],
        'name': 'Pixel 9',
        'platform': 'android',
      });
      expect(who.scopeSet, EsiScope.values.toSet());
    });

    test('an unknown scope name does not throw', () {
      final who = WhoAmI.fromJson(<String, dynamic>{'scopes': <String>['telepathy']});
      expect(who.scopes, <String>['telepathy']);
    });
  });
}

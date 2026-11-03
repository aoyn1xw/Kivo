import 'dart:async';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';
import 'package:kivo/core/app_state.dart';
import 'package:kivo/core/models.dart';
import 'package:kivo/core/shadowplay_api.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late SharedPreferences prefs;
  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    FlutterSecureStorage.setMockInitialValues({});
    prefs = await SharedPreferences.getInstance();
  });

  final expected = <ApiFailureKind, ConnectionIssue>{
    ApiFailureKind.timeout: ConnectionIssue.unreachable,
    ApiFailureKind.connectionRefused: ConnectionIssue.unreachable,
    ApiFailureKind.networkUnreachable: ConnectionIssue.unreachable,
    ApiFailureKind.network: ConnectionIssue.unreachable,
    ApiFailureKind.networkIsolation: ConnectionIssue.networkRestricted,
    ApiFailureKind.authenticationFailure:
        ConnectionIssue.authenticationRequired,
    ApiFailureKind.tlsUntrusted: ConnectionIssue.certificateChanged,
    ApiFailureKind.apiIncompatible: ConnectionIssue.updateRequired,
    ApiFailureKind.http: ConnectionIssue.serverError,
    ApiFailureKind.malformedResponse: ConnectionIssue.invalidResponse,
    ApiFailureKind.protocol: ConnectionIssue.invalidResponse,
    ApiFailureKind.unknown: ConnectionIssue.unknown,
  };
  for (final kind in ApiFailureKind.values) {
    for (final statusesOnly in [false, true]) {
      test(
          '$kind classified by ${statusesOnly ? "status" : "library"} refresh and recovers',
          () async {
        final connection = Connection(
            serverId: 'pc',
            computerName: 'PC',
            address: '127.0.0.1',
            port: 1,
            httpsPort: 2,
            certSha256: 'ab' * 32);
        final cached = Clip(
            id: 'cached',
            fileName: 'cached.mp4',
            sizeBytes: 1,
            lastWriteTimeUtc: DateTime.utc(2026));
        final api = ScriptedApi(connection)
          ..failure = ApiException('Friendly failure', kind: kind);
        final state = AppState.forTesting(prefs,
            connections: [connection],
            activeServerId: 'pc',
            activeToken: 'token',
            remoteClips: [cached],
            apiFactory: (connection, token) => api);
        Future<void> refresh() =>
            statusesOnly ? state.refreshDeviceStatuses() : state.refreshClips();
        await refresh();
        expect(state.activeStatus.issue, expected[kind]);
        expect(
            state.activeStatus.availability,
            expected[kind] == ConnectionIssue.unreachable
                ? DeviceAvailability.offline
                : DeviceAvailability.error);
        expect(state.remoteClips.single.id, 'cached');
        expect(connection.certSha256, 'ab' * 32);
        expect(connection.usesTls, isTrue);
        final gate = Completer<void>();
        api.gate = gate;
        final retry = refresh();
        expect(state.activeStatus.issue, expected[kind],
            reason: 'checking must not hide the recovery action');
        api.failure = null;
        gate.complete();
        await retry;
        expect(
            state.activeStatus.availability,
            statusesOnly && state.clipLoadError != null
                ? DeviceAvailability.error
                : DeviceAvailability.online);
        expect(state.clipLoadError, isNull);
        state.dispose();
      });
    }
  }

  test('unexpected exceptions do not expose raw details', () {
    final status = AppState.connectionFailure(StateError('private details'));
    expect(status.issue, ConnectionIssue.unknown);
    expect(status.message, isNot(contains('private details')));
  });

  test('missing token requires pairing without creating a transport', () async {
    final state = AppState.forTesting(prefs,
        connections: [
          Connection(
              serverId: 'pc', computerName: 'PC', address: '127.0.0.1', port: 1)
        ],
        activeServerId: 'pc',
        tokens: {},
        apiFactory: (connection, token) =>
            throw StateError('must not create transport'));
    await state.refreshClips();
    expect(state.activeStatus.issue, ConnectionIssue.authenticationRequired);
    await state.refreshDeviceStatuses();
    expect(state.activeStatus.issue, ConnectionIssue.authenticationRequired);
    state.dispose();
  });

  test('newer status failure supersedes an older library success', () async {
    final connection = Connection(
        serverId: 'pc', computerName: 'PC', address: '127.0.0.1', port: 1);
    final gate = Completer<void>();
    final old = ScriptedApi(connection)..gate = gate;
    final newer = ScriptedApi(connection)
      ..failure = const ApiException('Revoked',
          kind: ApiFailureKind.authenticationFailure);
    var calls = 0;
    final state = AppState.forTesting(prefs,
        connections: [connection],
        activeServerId: 'pc',
        activeToken: 'token',
        apiFactory: (connection, token) => calls++ == 0 ? old : newer);
    final refresh = state.refreshClips();
    await state.refreshDeviceStatuses();
    gate.complete();
    await refresh;
    expect(state.activeStatus.issue, ConnectionIssue.authenticationRequired);
    expect(state.remoteClips, isEmpty);
    expect(state.clipsLoading, isFalse);
    state.dispose();
  });
}

class ScriptedApi extends ShadowPlayApi {
  ScriptedApi(Connection connection) : super(connection, 'token');
  ApiException? failure;
  Completer<void>? gate;
  @override
  Future<ServerInfo> serverInfo({http.Client? client}) async {
    await gate?.future;
    if (failure != null) throw failure!;
    return ServerInfo(
        serverId: connection.serverId, computerName: 'PC', clipCount: 0);
  }

  @override
  Future<List<Clip>> clips({http.Client? client}) async => [];
}

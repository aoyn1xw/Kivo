import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';
import 'package:kivo/app/app.dart';
import 'package:kivo/core/app_state.dart';
import 'package:kivo/core/models.dart';
import 'package:kivo/core/shadowplay_api.dart';
import 'package:kivo/features/onboarding/onboarding_flow.dart';
import 'package:kivo/features/player/player_screen.dart';

class _FailureApi extends ShadowPlayApi {
  _FailureApi(Connection connection, this.kind) : super(connection, 'token');
  final ApiFailureKind kind;
  int calls = 0;
  @override
  Future<ServerInfo> serverInfo({http.Client? client}) async {
    calls++;
    throw ApiException('Check this PC connection.', kind: kind);
  }
}

void main() {
  for (final sample in [
    (ApiFailureKind.networkUnreachable, 'PC unreachable', 'Try Again'),
    (ApiFailureKind.networkIsolation, 'Local network restricted', 'Try Again'),
    (ApiFailureKind.authenticationFailure, 'Pairing required', 'Re-pair'),
    (ApiFailureKind.tlsUntrusted, 'PC identity changed', 'Verify and re-pair'),
    (ApiFailureKind.apiIncompatible, 'Update required', 'Update instructions'),
    (ApiFailureKind.http, 'PC request failed', 'Try Again'),
    (ApiFailureKind.malformedResponse, 'Invalid PC response', 'Try Again'),
    (ApiFailureKind.unknown, 'Connection problem', 'Try Again'),
  ]) {
    testWidgets('${sample.$1} keeps cached clips visible with recovery',
        (tester) async {
      SharedPreferences.setMockInitialValues({});
      final pc = Connection(
          serverId: 'pc', computerName: 'PC', address: '127.0.0.1', port: 1);
      final api = _FailureApi(pc, sample.$1);
      final state = AppState.forTesting(await SharedPreferences.getInstance(),
          onboardingCompleted: true,
          connections: [pc],
          activeServerId: 'pc',
          activeToken: 'token',
          apiFactory: (connection, token) => api,
          remoteClips: [
            Clip(
                id: 'cached',
                fileName: 'cached.mp4',
                sizeBytes: 10,
                lastWriteTimeUtc: DateTime.utc(2026))
          ],
          downloadedClips: [
            DownloadedClip(
                id: 'local',
                fileName: 'local.mp4',
                sizeBytes: 10,
                lastWriteTimeUtc: DateTime.utc(2026),
                localPath: 'missing-local.mp4')
          ]);
      await tester.pumpWidget(KivoApp(state: state));
      await tester.pumpAndSettle();
      expect(find.text(sample.$2), findsOneWidget);
      await tester.scrollUntilVisible(find.text('local.mp4'), 100,
          scrollable: find.byType(Scrollable).first);
      await tester.tap(find.text('local.mp4'));
      await tester.pumpAndSettle();
      expect(
          tester.widget<PlayerScreen>(find.byType(PlayerScreen)).api, isNull);
      await tester.pageBack();
      await tester.pumpAndSettle();
      await tester.tap(find.text('Clips'));
      await tester.pumpAndSettle();
      expect(find.text('No recordings on PC'), findsNothing);
      expect(
          find.textContaining('Showing cached PC recordings'), findsOneWidget);
      await tester.scrollUntilVisible(find.text('cached.mp4'), 100,
          scrollable: find.byType(Scrollable).first);
      expect(find.text('cached.mp4'), findsOneWidget);
      final action = find.text(sample.$3);
      await tester.ensureVisible(action);
      final calls = api.calls;
      await tester.tap(action);
      await tester.pumpAndSettle();
      if (sample.$3 == 'Try Again') {
        expect(api.calls, greaterThan(calls));
        expect(find.text(sample.$2), findsOneWidget);
      } else if (sample.$3 == 'Update instructions') {
        expect(find.text('Update Kivo'), findsOneWidget);
        await tester.tap(find.text('Close'));
        await tester.pumpAndSettle();
      } else {
        if (sample.$3 == 'Verify and re-pair') {
          expect(find.text('Verify your PC first'), findsOneWidget);
          await tester.tap(find.text('Scan PC QR code'));
          await tester.pumpAndSettle();
        }
        expect(find.byType(OnboardingFlow), findsOneWidget);
        await tester.binding.handlePopRoute();
        await tester.pumpAndSettle();
        expect(state.downloadedClips.single.id, 'local');
      }
      await tester.pumpWidget(const SizedBox());
      state.dispose();
    });
  }

  for (final dark in [false, true]) {
    testWidgets(
        'connection recovery fits narrow large text ${dark ? "dark" : "light"}',
        (tester) async {
      tester.view.physicalSize = const Size(320, 568);
      tester.view.devicePixelRatio = 1;
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      SharedPreferences.setMockInitialValues({});
      final pc = Connection(
          serverId: 'pc', computerName: 'PC', address: '127.0.0.1', port: 1);
      final state = AppState.forTesting(await SharedPreferences.getInstance(),
          onboardingCompleted: true,
          connections: [pc],
          activeServerId: 'pc',
          activeToken: 'token',
          apiFactory: (connection, token) =>
              _FailureApi(connection, ApiFailureKind.tlsUntrusted));
      state.themeMode = dark ? ThemeMode.dark : ThemeMode.light;
      await tester.pumpWidget(KivoApp(state: state));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Clips'));
      await tester.pumpAndSettle();
      await tester.scrollUntilVisible(find.text('Verify and re-pair'), 100,
          scrollable: find.byType(Scrollable).first);
      await tester.pump();
      expect(tester.takeException(), isNull);
      expect(find.text('No recordings on PC'), findsNothing);
      await tester.pumpWidget(const SizedBox());
      state.dispose();
    });
  }
}

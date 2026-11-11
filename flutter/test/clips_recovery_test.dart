import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:kivo/core/app_state.dart';
import 'package:kivo/core/models.dart';
import 'package:kivo/core/resumable_download.dart';
import 'package:kivo/core/shadowplay_api.dart';
import 'package:kivo/features/clips/clips_screen.dart';
import 'package:kivo/features/shell/main_shell.dart';

/// Recovery and trust regression tests: failed downloads must offer an
/// explicit retry, batch failures must be actionable, stale lists must be
/// marked offline, and the notification preference must not promise behavior
/// the app does not deliver.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late Directory tempDocuments;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    tempDocuments = await Directory.systemTemp.createTemp('kivo-test-docs');
    const channel = MethodChannel('plugins.flutter.io/path_provider');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async => tempDocuments.path);
    addTearDown(() {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, null);
      tempDocuments.delete(recursive: true);
    });
  });

  Connection connection() => Connection(
      serverId: 'server-1',
      computerName: 'GAMING-PC',
      address: '127.0.0.1',
      port: 59998);

  AppState stateWith(
    SharedPreferences preferences, {
    required _RecordingApi api,
    required List<Clip> clips,
  }) {
    final conn = connection();
    final state = AppState.forTesting(
      preferences,
      connections: [conn],
      activeServerId: conn.serverId,
      activeToken: 'tok',
      onboardingCompleted: true,
      remoteClips: clips,
      apiFactory: (_, __) => api,
    );
    state.deviceStatuses[conn.serverId] = const DeviceStatus.online();
    return state;
  }

  Clip clip(String id, String name) => Clip(
        id: id * 64,
        fileName: name,
        sizeBytes: 1000000,
        lastWriteTimeUtc: DateTime.utc(2026, 9, 4, 18, 30),
      );

  /// Gives the default 800x600 test window room for the selection bar and a
  /// snackbar at the same time.
  void useTallWindow(WidgetTester tester) {
    tester.view.physicalSize = const Size(800, 1200);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
  }

  testWidgets(
      'a failed download row offers an explicit retry that targets '
      'only the failed clip and keeps selection untouched', (tester) async {
    final preferences = await SharedPreferences.getInstance();
    final api = _RecordingApi();
    final state = stateWith(
      preferences,
      api: api,
      clips: [clip('a', 'Alpha.mp4'), clip('b', 'Bravo.mp4')],
    );
    state.downloadFailures[AppState.deviceKey('server-1', 'a' * 64)] =
        'Connection interrupted';
    useTallWindow(tester);

    await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: ClipsScreen(state: state)),
    ));
    await tester.pump();

    expect(find.text('Download failed.'), findsOneWidget);
    expect(find.text('Tap to retry.'), findsNothing);
    expect(find.text('Retry'), findsOneWidget);

    // Tapping Retry must not toggle selection (no action bar appears) and must
    // re-attempt exactly the failed clip.
    await tester.tap(find.text('Retry'));
    await waitUntil(tester,
        () => api.requested.length == 1 && state.downloadFailures.isNotEmpty);
    await tester.pump();

    expect(api.requested, ['a' * 64],
        reason: 'retry must not silently re-download healthy clips');
    expect(state.downloadFailures[AppState.deviceKey('server-1', 'a' * 64)],
        isNotNull,
        reason: 'the retry ran the real download pipeline and failed again');
    expect(find.byType(SelectionActionBar), findsNothing,
        reason: 'retry is a download action, not a selection toggle');
    state.dispose();
  });

  testWidgets(
      'a batch download failure names the failures and retries them '
      'from the snackbar', (tester) async {
    final preferences = await SharedPreferences.getInstance();
    final api = _RecordingApi();
    final state = stateWith(
      preferences,
      api: api,
      clips: [clip('a', 'Alpha.mp4'), clip('b', 'Bravo.mp4')],
    );
    useTallWindow(tester);

    await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: ClipsScreen(state: state)),
    ));
    await tester.pump();

    await tester.tap(find.text('Alpha.mp4'));
    await tester.pump();
    await tester.tap(find.text('Bravo.mp4'));
    await tester.pump();
    await tester.tap(find.text('Download 2 clips'));
    await waitUntil(tester, () => api.requested.length == 2);
    await tester.pump();

    expect(api.requested.length, 2,
        reason: 'both selected clips were attempted');
    expect(find.text('2 downloads failed.'), findsOneWidget);
    expect(find.text('Tap to retry.'), findsNothing);

    final action = tester.widget<SnackBarAction>(
      find.descendant(
        of: find.byType(SnackBar),
        matching: find.byType(SnackBarAction),
      ),
    );
    expect(action.label, 'Retry failed');
    action.onPressed();
    await waitUntil(tester, () => api.requested.length == 4);
    await tester.pump();

    expect(api.requested.length, 4,
        reason: 'the snackbar action retries exactly the failed clips');
    state.dispose();
  });

  testWidgets(
      'a failed refresh after a loaded list keeps the rows visible '
      'with an offline banner and retry', (tester) async {
    final preferences = await SharedPreferences.getInstance();
    final api = _RecordingApi();
    final state = stateWith(
      preferences,
      api: api,
      clips: [clip('a', 'Alpha.mp4'), clip('b', 'Bravo.mp4')],
    );

    await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: ClipsScreen(state: state)),
    ));
    await tester.pump();
    expect(find.text('PC offline · showing the last loaded recordings'),
        findsNothing);

    state.deviceStatuses[state.activeServerId!] =
        const DeviceStatus.offline('PC is asleep');
    state.clipLoadError = 'PC is asleep';
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: ClipsScreen(state: state)),
    ));
    await tester.pump();

    expect(find.text('PC offline · showing the last loaded recordings'),
        findsOneWidget);
    expect(find.text('PC is asleep'), findsOneWidget);
    expect(find.text('Alpha.mp4'), findsOneWidget,
        reason: 'cached rows stay visible when the PC goes offline');
    expect(find.text('Try Again'), findsOneWidget);

    expect(api.requested, isEmpty,
        reason: 'the banner must not itself trigger transfers');

    state.deviceStatuses[state.activeServerId!] = const DeviceStatus.online();
    state.clipLoadError = null;
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: ClipsScreen(state: state)),
    ));
    await tester.pump();
    expect(find.text('PC offline · showing the last loaded recordings'),
        findsNothing);
    state.dispose();
  });

  testWidgets('settings no longer promises download-finished notifications',
      (tester) async {
    final preferences = await SharedPreferences.getInstance();
    final conn = connection();
    final state = AppState.forTesting(
      preferences,
      connections: [conn],
      activeServerId: conn.serverId,
      onboardingCompleted: true,
    );

    await tester.pumpWidget(MaterialApp(home: MainShell(state: state)));
    await tester.pump();
    await tester.tap(find.text('Settings'));
    await tester.pump();

    expect(find.text('Notifications'), findsNothing);
    expect(find.text('Notify when selected clips finish downloading'),
        findsNothing);
    expect(find.text('Notify when new PC clips are available'), findsNothing);
    await tester.pumpWidget(const SizedBox());
    state.dispose();
  });
}

/// Captures which clip ids reached the download transport and fails them like
/// the real transport would when the PC is unreachable mid-transfer.
class _RecordingApi extends ShadowPlayApi {
  _RecordingApi()
      : super(
            Connection(
              serverId: 'server-1',
              computerName: 'GAMING-PC',
              address: '127.0.0.1',
              port: 59998,
            ),
            'tok');

  final List<String> requested = [];

  @override
  Future<DownloadSegment> openDownload(Clip clip, {int? rangeStart}) async {
    requested.add(clip.id);
    throw const ApiException('Connection interrupted',
        kind: ApiFailureKind.network);
  }
}

/// Lets real dart:io work (file checks under the download flow) continue while
/// the FakeAsync widget zone waits, polling until [condition] becomes true.
Future<void> waitUntil(WidgetTester tester, bool Function() condition) async {
  await tester.runAsync(() async {
    for (var i = 0; i < 100 && !condition(); i++) {
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
  });
  await tester.pump();
  expect(condition(), isTrue);
}

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:kivo/core/app_state.dart';
import 'package:kivo/core/shadowplay_api.dart';
import 'package:kivo/features/onboarding/onboarding_flow.dart';

/// Pairing preflight trust tests: a completed connection check authorizes
/// exactly the endpoint (address, port and trust parameters) it checked, and
/// any edit after a check (including edits during an in-flight check) requires
/// a fresh check before the one-time code can be exchanged.
void main() {
  Completer<void>? healthGate;
  int healthRequests = 0;
  bool probeFails = false;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    healthRequests = 0;
    healthGate = null;
    probeFails = false;
  });

  Future<AppState> openManualPairing(WidgetTester tester) async {
    final state = AppState.forTesting(await SharedPreferences.getInstance());
    await tester.pumpWidget(MaterialApp(
      home: OnboardingFlow(
        state: state,
        showIntro: false,
        pairingHealthCheck: ({
          required String address,
          required int port,
          bool useTls = false,
          String? pinnedCertSha256,
        }) async {
          healthRequests++;
          final gate = healthGate;
          if (gate != null) await gate.future;
          if (port < 1 || port > 65535) {
            throw const ApiException('bad port', kind: ApiFailureKind.network);
          }
          if (probeFails) {
            throw const ApiException('PC is unreachable',
                kind: ApiFailureKind.connectionRefused);
          }
        },
        onFinished: () async {},
      ),
    ));
    await tester.pump();
    await tester.tap(find.text('Continue'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Manual Mode'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField).first, '127.0.0.1');
    await tester.enterText(find.byType(TextField).at(1), '5177');
    await tester.pump();
    return state;
  }

  testWidgets(
      'pairing requires a completed connection check and a fresh '
      'check after the address changes', (tester) async {
    await openManualPairing(tester);

    // Before any check the exchange is not offered.
    expect(find.text('Pair Device'), findsNothing);
    expect(find.text('Check PC connection'), findsOneWidget);
    expect(
        find.textContaining('without certificate verification'), findsOneWidget,
        reason: 'manual pairing must state its transport honestly');

    await tester.tap(find.text('Check PC connection'));
    await tester
        .pumpAndWait(() => find.text('Pair Device').evaluate().isNotEmpty);
    expect(find.text('PC reachable on this Wi‑Fi'), findsOneWidget);

    // Any edit to the round-trip endpoint invalidates the check.
    await tester.enterText(find.byType(TextField).first, '127.0.0.2');
    await tester.pump();
    expect(find.text('Pair Device'), findsNothing);
    expect(find.text('PC reachable on this Wi‑Fi'), findsNothing);
    expect(find.text('Check PC connection'), findsOneWidget);
  });

  testWidgets(
      'an in-flight check result cannot authorize edited or '
      'reverted connection details', (tester) async {
    healthGate = Completer<void>();
    await openManualPairing(tester);

    await tester.tap(find.text('Check PC connection'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
    expect(find.text('Requesting Local Network access…'), findsOneWidget,
        reason: 'the in-flight check keeps its pending label');

    // Edit while the check is still pending.
    await tester.enterText(find.byType(TextField).first, '10.9.9.9');
    await tester.pump();

    healthGate!.complete();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));

    // The stale result for the checked endpoint must not pair the edited one.
    expect(find.text('Pair Device'), findsNothing);
    expect(find.text('PC reachable on this Wi‑Fi'), findsNothing);
    expect(find.text('Requesting Local Network access…'), findsNothing,
        reason: 'a superseded check must not linger');

    // Even reverting the edit requires a fresh check.
    await tester.enterText(find.byType(TextField).first, '127.0.0.1');
    await tester.pump();
    expect(find.text('Pair Device'), findsNothing);
    expect(find.text('Checking PC connection…'), findsNothing);

    // A new check for exactly the current endpoint authorizes pairing again.
    await tester.tap(find.text('Check PC connection'));
    await tester
        .pumpAndWait(() => find.text('Pair Device').evaluate().isNotEmpty);
    expect(find.text('PC reachable on this Wi‑Fi'), findsOneWidget);
    expect(healthRequests, 2,
        reason: 'the discarded stale check and the fresh check ran');
  });

  testWidgets('port edits also invalidate the completed check', (tester) async {
    await openManualPairing(tester);

    await tester.tap(find.text('Check PC connection'));
    await tester
        .pumpAndWait(() => find.text('Pair Device').evaluate().isNotEmpty);

    await tester.enterText(find.byType(TextField).at(1), '9999');
    await tester.pump();
    expect(find.text('Pair Device'), findsNothing);
    expect(find.text('PC reachable on this Wi‑Fi'), findsNothing);
  });

  testWidgets('check failures keep the exchange hidden', (tester) async {
    probeFails = true;
    final state = await openManualPairing(tester);

    await tester.tap(find.text('Check PC connection'));
    // Two scripted retry laps (1s fake delay each) pass before the failure
    // surfaces, so advance the clock until the error is visible.
    await tester.pumpAndRetryOnError(
        () => find.textContaining('PC is unreachable').evaluate().isNotEmpty);

    expect(find.text('Pair Device'), findsNothing);
    expect(state.downloadFailures, isEmpty);
  });
}

extension _TestPump on WidgetTester {
  /// Pumps in steps until [condition] holds, advancing the fake clock enough
  /// for the preflight retries and probe to settle.
  Future<void> pumpAndWait(bool Function() condition) async {
    for (var i = 0; i < 60 && !condition(); i++) {
      await pump(const Duration(milliseconds: 25));
    }
    expect(condition(), isTrue);
  }

  /// Steps fake time forward in seconds-sized chunks until [condition] holds
  /// (or ~12 simulated seconds pass).
  Future<void> pumpAndRetryOnError(bool Function() condition) async {
    for (var i = 0; i < 40 && !condition(); i++) {
      await pump(const Duration(milliseconds: 300));
    }
    expect(condition(), isTrue);
  }
}

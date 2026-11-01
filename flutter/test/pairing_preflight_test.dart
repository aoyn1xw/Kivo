import 'package:flutter_test/flutter_test.dart';
import 'package:kivo/core/app_permissions.dart';
import 'package:kivo/core/pairing_preflight.dart';
import 'package:kivo/core/shadowplay_api.dart';

void main() {
  test('waits for permission before health and recovers without rescanning',
      () async {
    var permissions = 0;
    var checks = 0;
    final result = await runPairingPreflight(
      requestPermission: () async => ++permissions == 1
          ? AppPermissionState.denied
          : AppPermissionState.granted,
      checkConnection: () async => checks++,
      waitBeforeRetry: () async {},
    );
    expect(result, AppPermissionState.granted);
    expect(permissions, 2);
    expect(checks, 1);
  });

  test('permission-related network failure retries health after access settles',
      () async {
    var checks = 0;
    final result = await runPairingPreflight(
      requestPermission: () async => AppPermissionState.granted,
      checkConnection: () async {
        if (++checks == 1) {
          throw const ApiException('Operation not permitted',
              kind: ApiFailureKind.network);
        }
      },
      waitBeforeRetry: () async {},
    );
    expect(result, AppPermissionState.granted);
    expect(checks, 2);
  });

  test('persistent denial stops after three attempts without HTTPS', () async {
    var permissions = 0;
    final result = await runPairingPreflight(
      requestPermission: () async {
        permissions++;
        return AppPermissionState.denied;
      },
      checkConnection: () async => fail('Must not connect before access'),
      waitBeforeRetry: () async {},
    );
    expect(result, AppPermissionState.denied);
    expect(permissions, 3);
  });

  test('genuine pin mismatch is surfaced immediately without retries',
      () async {
    var checks = 0;
    await expectLater(
      runPairingPreflight(
        requestPermission: () async => AppPermissionState.granted,
        checkConnection: () async {
          checks++;
          throw const ApiException('Different certificate',
              kind: ApiFailureKind.tlsUntrusted);
        },
        waitBeforeRetry: () async => fail('Must not retry a pin mismatch'),
      ),
      throwsA(isA<ApiException>()
          .having((e) => e.kind, 'kind', ApiFailureKind.tlsUntrusted)),
    );
    expect(checks, 1);
  });

  test('persistent network failure stops after three health checks', () async {
    var checks = 0;
    await expectLater(
      runPairingPreflight(
        requestPermission: () async => AppPermissionState.granted,
        checkConnection: () async {
          checks++;
          throw const ApiException('Unreachable',
              kind: ApiFailureKind.networkUnreachable);
        },
        waitBeforeRetry: () async {},
      ),
      throwsA(isA<ApiException>()
          .having((e) => e.kind, 'kind', ApiFailureKind.networkUnreachable)),
    );
    expect(checks, 3);
  });

  test('disposed onboarding does not connect after permission completes',
      () async {
    var active = true;
    final result = await runPairingPreflight(
      requestPermission: () async {
        active = false;
        return AppPermissionState.granted;
      },
      checkConnection: () async => fail('Onboarding was disposed'),
      isActive: () => active,
    );
    expect(result, AppPermissionState.unavailable);
  });
}

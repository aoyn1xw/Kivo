import 'app_permissions.dart';
import 'shadowplay_api.dart';

/// Retry only the non-consuming health check while iOS permission settles.
/// Never retry a pairing exchange or replace a certificate pin.
Future<AppPermissionState> runPairingPreflight({
  required Future<AppPermissionState> Function() requestPermission,
  required Future<void> Function() checkConnection,
  Future<void> Function()? waitBeforeRetry,
  bool Function()? isActive,
}) async {
  for (var attempt = 0; attempt < 3; attempt++) {
    if (isActive != null && !isActive()) {
      return AppPermissionState.unavailable;
    }
    final permission = await requestPermission();
    if (isActive != null && !isActive()) {
      return AppPermissionState.unavailable;
    }
    if (permission == AppPermissionState.restricted) return permission;
    if (permission == AppPermissionState.granted) {
      try {
        await checkConnection();
        return permission;
      } on ApiException catch (error) {
        final transient = error.kind == ApiFailureKind.network ||
            error.kind == ApiFailureKind.timeout ||
            error.kind == ApiFailureKind.networkUnreachable ||
            error.kind == ApiFailureKind.connectionRefused;
        if (!transient || attempt == 2) rethrow;
      }
    } else if (attempt == 2) {
      return permission;
    }
    await (waitBeforeRetry?.call() ??
        Future<void>.delayed(const Duration(seconds: 1)));
  }
  return AppPermissionState.unavailable;
}

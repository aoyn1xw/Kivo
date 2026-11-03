import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:kivo/app/app.dart';
import 'package:kivo/core/app_state.dart';
import 'package:kivo/core/models.dart';
import 'package:kivo/features/player/player_screen.dart';
import 'package:kivo/features/onboarding/onboarding_flow.dart';

void main() {
  for (final populated in [false, true]) {
    testWidgets(
        'unpaired shell ${populated ? "with downloads" : "empty"} stays accessible',
        (tester) async {
      SharedPreferences.setMockInitialValues({});
      final state = AppState.forTesting(await SharedPreferences.getInstance(),
          onboardingCompleted: true,
          downloadedClips: [
            if (populated)
              DownloadedClip(
                  id: 'local',
                  fileName: 'Offline.mp4',
                  sizeBytes: 3,
                  lastWriteTimeUtc: DateTime.utc(2026),
                  localPath: 'missing-offline.mp4'),
          ]);
      await tester.pumpWidget(KivoApp(state: state));
      await tester.pump();
      expect(find.text('Downloaded clips'), findsOneWidget);
      expect(find.byType(NavigationDestination), findsNWidgets(3));
      if (populated) {
        await tester.ensureVisible(find.text('Offline.mp4'));
        await tester.tap(find.text('Offline.mp4'));
        await tester.pumpAndSettle();
        final player = tester.widget<PlayerScreen>(find.byType(PlayerScreen));
        expect(player.localPath, 'missing-offline.mp4');
        expect(player.api, isNull);
        await tester.pageBack();
        await tester.pumpAndSettle();
      }
      await tester.tap(find.text('Clips'));
      await tester.pump();
      await tester.tap(find.text('Pair a PC').last);
      await tester.pumpAndSettle();
      expect(find.byType(OnboardingFlow), findsOneWidget);
      await tester.binding.handlePopRoute();
      await tester.pumpAndSettle();
      expect(find.byType(NavigationDestination), findsNWidgets(3));
      await tester.pumpWidget(const SizedBox());
      state.dispose();
    });
  }
}

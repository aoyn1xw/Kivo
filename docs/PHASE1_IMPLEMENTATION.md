# Phase 1 implementation review

Scope: UX findings #1–5 only. No dependencies, API endpoints, QR protocol,
certificate policy, persistent schema, CI, installer, or release changes.

## Review order

1. **Desktop first run** — `src/Kivo.Windows/App.xaml.cs` opens the existing main
   window after accepted setup; cancellation and configured tray startup remain
   unchanged. `Views/MainWindow.xaml` explains closing versus Exit. Verify the
   actual window handoff on Windows.
2. **Pairing lifecycle** — `Services/AppController.cs` captures one immutable
   pairing snapshot when sharing starts or the user regenerates a code. Reads
   never create offers. Stop clears the snapshot before asynchronous shutdown.
   `ViewModels/MainViewModel.cs` reconciles the snapshot on controller events and
   a one-second timer, reusing the bitmap until the payload changes. Disposal
   stops the timer and unsubscribes events. Tests: `AppControllerPairingTests.cs`,
   `MainViewModelPairingTests.cs`, and the test-only `TestSupport/StaTest.cs`.
3. **Manual pairing details** — `Views/MainWindow.xaml` shows accessible,
   selectable address, HTTP port, and code from that same snapshot. Paused,
   expired, consumed, starting, or error states expose no usable details.
   View-model tests compare the fields to the current payload and exercise
   reopen, regenerate, consume, expire, pause, and resume.
4. **Offline access** — Flutter `core/app_state.dart` bases onboarding on its
   existing persisted completion flag. Home renders local downloads without an
   active PC. Home and Clips open the existing pairing flow directly; Settings
   explains that forgetting retains downloads. Tests exercise unpaired routing,
   actual state restoration from preferences and a temporary file, local-player
   navigation, pairing cancellation, and delayed results after Forget.
5. **Typed connection state** — `core/models.dart` retains a connection issue
   alongside availability. Both refresh paths preserve API failure categories,
   handle missing tokens, and reject superseded/removed/replaced-connection
   results. The active PC's status check uses a complete library refresh so a
   status-only success cannot conceal a clip-list failure. Device selection is
   protected against background refreshes while secure storage loads. Tests:
   `connection_failure_test.dart` and `app_state_test.dart`.
6. **Recovery presentation** — `shared/widgets.dart` provides a persistent
   connection banner on Home/Clips, including cached-data labeling. Actions are
   Retry, Re-pair, explicit identity-verification/QR instructions, or update
   instructions. Settings and headers use the same classified status. Tests:
   `connection_widgets_test.dart`, `offline_access_test.dart`, and the existing
   widget/layout/model tests updated for the approved behavior.

The implementation is organized into six sequential commits on
`feat/phase1-pairing-offline-access`, based on commit `2a9e90d`. The matching
review patches supplied outside the repository are an alternative review
representation, not additional changes to apply on top of the implementation.
Intermediate slices have not each received the final full verification.

## Fresh verification

- **Flutter:** 157 tests passed, including real loopback HTTP/TLS tests, offline
  restore, classification, recovery actions, request races, and layout checks.
- **Flutter analysis:** no issues. Dart format check: 32 files, zero changes.
- **Current widget renders:** eight layout tests passed with screenshot capture;
  offline and unpaired-library screenshots were inspected at 320×568/200% text.
  These use synthetic files and are not physical-device captures. Screenshots
  remain in the ignored `docs/ui-review/flutter/` directory.
- **Regression sensitivity:** both new unpaired-shell widget tests fail against
  an isolated copy of the original commit because onboarding hides the library.
- **.NET Release cross-build:** all four projects compile on Linux using Windows
  targeting, with zero warnings/errors. This includes the new WPF tests.
- **.NET formatting:** solution-wide verify-no-changes passes.
- **Windows test project:** execution aborts on Linux because
  `Microsoft.WindowsDesktop.App` is unavailable. No WPF test pass is claimed.
- **Supplemental Linux harness:** a temporary, untracked net8.0 runner compiled
  the unchanged Core/API sources and actual controller/test sources without WPF.
  All 49 focused pairing, bearer-token, QR, API, HTTPS, and private-network tests
  passed, including the three new controller lifecycle tests.
- **Exploratory broad Linux harness:** 136 passed and 12 failed in unchanged
  Windows-oriented filename (2), firewall path (3), and clip-monitor (7) tests.
  Filename/firewall tests depend on Windows path semantics; the monitor returns
  lowercased candidate paths, which cannot reliably address mixed-case temporary
  directories on Linux. These were not changed or treated as a Windows pass.
- **Android debug build:** blocked: no Android SDK is installed.
- **iOS build/device tests:** unavailable on Linux; require macOS/Xcode/signing.

Toolchain downloads and caches were placed outside the repository. Package and
lock files remain unchanged. The temporary Linux harness is supplementary and
is not a replacement project or CI change.

## Required Windows and physical-device verification

- [ ] Run the normal Release build and complete `Kivo.sln` test suite on Windows,
      including both STA view-model tests, then the existing published smoke test.
- [ ] Fresh setup opens exactly one main window; cancellation does not. A saved
      folder with a sharing error shows that error. Later launches stay in tray.
- [ ] Window close retains sharing; tray Exit stops it. Reopen retains the current
      valid offer without changing its expiration.
- [ ] QR and manual pairing both complete using only desktop-visible details;
      the manual field shows the HTTP port, while QR retains existing TLS fields.
- [ ] Pause/restart/settings restart replace the displayed QR and manual code;
      prior codes fail. Consumption/expiry removes details within one second.
- [ ] On Android and iOS, forget the final PC, relaunch offline, and play a real
      retained clip. Verify share, gallery retry, and local deletion still work.
- [ ] Revoke the phone: show Pairing required with Re-pair, while local playback
      remains usable. Canceling pairing returns to the retained library.
- [ ] Test actual pin mismatch, incompatible API, unreachable PC, and restricted
      LAN; inspect recovery with and without cached clips. Ordinary interrupted
      TLS handshakes must remain network failures, not identity-change warnings.
- [ ] A certificate warning does not update trust by checking/retrying; verify
      the PC and explicitly complete QR pairing before accepting new identity.
- [ ] Inspect manual fields and recovery controls with keyboard/screen reader,
      desktop scaling, and native Android/iOS text scaling.

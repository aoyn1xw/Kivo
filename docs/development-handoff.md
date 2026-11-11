# Kivo development handoff

Audit date: 2026-11-11. CLI verification on Windows, .NET SDK 8.0.427,
Flutter 3.47.2 / Dart 3.13.2. No application code changed.

## Git and PR state

- Current branch: `stabilization/cursor-handoff`, created from
  `origin/fix/winui-sidebar-overlap` at `d652af7f694b3ed2ebbbc0a8a1d5c82a33b6f032`.
  Working tree was clean before this report; this report is an uncommitted addition.
  The branch currently tracks PR #53's remote branch: avoid an implicit push.
- Remote: `origin`, https://github.com/aoyn1xw/Kivo.git. Fetch completed;
  no local branches were reset, deleted, merged, or pushed.
- Remote `main`: `6edd2acbfc564eda25e8f7738ec4190665361a45`.
  Local `main` remains at `26c0e56`, one commit behind. That older baseline
  contained conflict markers repaired by #54; do not start from local `main`.
- [#54](https://github.com/aoyn1xw/Kivo/pull/54) is merged (06:50 UTC).
  It repairs WPF conflict markers, Flutter duplicate offline recovery banners,
  and STA/dispatcher setup in revocation tests.
- [#53](https://github.com/aoyn1xw/Kivo/pull/53) is open and mergeable.
  Its `d652af7` merge already incorporates #54. The WPF conflict resolution
  retains revocation confirmation and window-close disposal. The final diff
  against main has no additional changes to `Views/MainWindow.xaml.cs`.
  GitHub marks #53 non-draft despite draft wording in its description.
- #53 adds a WinUI candidate, multi-folder configuration, shared controller,
  monitor and device-persistence changes plus regression tests. It is broader
  than a sidebar fix. WPF remains the release workflow/default installer host.
- [#52](https://github.com/aoyn1xw/Kivo/pull/52) is the other open PR:
  mobile_scanner 7.4.2 to 7.5.0. It is mergeable but was not checked out or tested
  here. Keep dependency changes separate from stabilization.
- #47 rebrand, #48 TLS reconnect, #49 first-run setup, #50 iOS permission
  recovery and #51 pairing/offline access are already in main.

## Preserved unfinished work and cleanup candidates

- `fix/winui-sidebar-overlap` locally remains at `7ca6a0e`, two commits behind
  its remote (main's fix plus the merge commit). Use the stabilization branch.
- `feat/winui3-desktop-redesign` and `fix/ios-release-ota` point at `26c0e56`;
  their names do not describe the latest implementation state.
- `fix/ios-onboarding-permission-recovery`, `fix/windows-first-run-setup`, and
  `rebrand/kivo` have gone upstream refs. Their PRs were merged, sometimes by
  squash; ancestry alone is insufficient to authorize deletion.
- `stash@{0}`: `Pre-Kivo crypto lockfile update`, four changed lines in
  `flutter/pubspec.lock`. Preserved; compare with current dependencies before
  deciding whether it is obsolete. Do not blindly apply it.
- No branches or stash entries were deleted. No unresolved source conflict
  markers were found. No main or PR metadata was modified.

## Fresh validation of d652af7

| Command/check | Result |
| --- | --- |
| `dotnet restore Kivo.sln` | Exit 0 |
| `dotnet build Kivo.sln -c Release --no-restore` | Exit 0, zero warnings/errors in final run; includes WinUI |
| `dotnet test Kivo.sln -c Release --no-build` | Exit 0; 180 passed, zero skipped/failed |
| `dotnet format Kivo.sln --verify-no-changes --no-restore --verbosity quiet` | Exit 0 |
| WPF publish, Release win-x64, `--self-contained true`, `-o publish/win-x64` | Exit 0 |
| WinUI publish, Release win-x64, `--self-contained false`, `-o publish/winui-x64` | Exit 0; Kivo.pri present |
| Both published executables: `--smoke --folder <synthetic-clips> --port 0 --smoke-seconds 12 --data-directory <isolated-data>` | Each exit 0, SMOKE_OK and clips=2 |
| Installed Inno Setup 6: `ISCC.exe installer/Kivo.iss` | Exit 2; resource-update error 110 at Updating icons (Setup.exe) |
| `flutter analyze` (from flutter/) | Exit 0; no issues |
| `flutter test --reporter compact` | Exit 0; 165 passed |
| `dart format --output=none --set-exit-if-changed lib test` | Exit 0; 34 files checked, zero changed |
| `git diff --check` | Passed before report; repeated after report |

Publish commands use `dotnet publish src/Kivo.Windows/Kivo.Windows.csproj` or
`src/Kivo.WinUI/Kivo.WinUI.csproj` with the options above. CLI logs and disposable
smoke fixtures/settings are under ignored `artifacts/audit/`; publishes are ignored.
Never commit those outputs: isolated settings can contain generated TLS identity.

Earlier sandbox restore attempts reported NU1900 vulnerability-feed connectivity
warnings. An overlapping retry caused XAML output file-lock errors; this was an
audit execution error, resolved by the sequential run above, not a source defect.
Interrupted publish/Flutter output was not accepted as success: final checks were
rerun with explicit exit codes. No toolchain/dependency upgrades were performed.

GitHub independently reported all #53 CI jobs green (Windows, Flutter, Android,
iOS) at its current tip. Remote main's CI and Release runs are also successful.
These are remote results, not local manual verification. Main was not separately
checked out and built here; local results cover the candidate containing main.

## Remaining verification and findings

- Installer packaging is **failed locally**. The compiler suggests antivirus
  interference, but the cause is unconfirmed. Do not disable security tooling
  or change installer behavior speculatively. Reproduce on a clean Windows
  runner and inspect compiler/security logs. No successful local installer
  artifact or installation/upgrade/uninstall test is claimed. The custom WinUI
  installer override was not separately compiled.
- Normal interactive startup and sidebar behavior are **unverified in this
  audit**. Computer Use's native pipe was unavailable; that is a tooling
  limitation. The XAML uses docked `PaneDisplayMode="Left"`; code inspection
  and headless smoke do not establish correct layout or interactive startup.
- Manually test Overview/Recordings/Devices/Settings navigation, pane open/close,
  narrow windows and high DPI; first-run onboarding/native folder picker;
  tray Open/Exit, hide/reopen, background launch and single-instance activation.
- Verify physical phone pairing, existing pinned TLS reconnection, range/offline
  playback, device revocation and transfer continuity while closing the window.
  Existing automated coverage and earlier migration notes are not fresh manual
  evidence for these behaviors.
- Android/iOS builds and physical-device behavior were not run locally. iOS
  compilation requires macOS/Xcode. Remote CI compilation does not prove device
  behavior. No local Android APK build or release/signing validation is claimed.
- AGENTS.md still describes only three production projects/WPF. The candidate
  solution has four production projects plus tests; use source/workflows and
  `docs/WINUI_MIGRATION.md` for the candidate's actual layout. Update onboarding
  documentation when migration decisions are settled.

## Cursor starting point and merge order

Start from **`stabilization/cursor-handoff`**, retaining this report. First task:
perform the focused WinUI interactive checklist and reproduce installer error
110 on a clean Windows environment. Do not promote WinUI as the release host.
This repository is ready for continued development in Cursor; WinUI release
readiness remains partial pending those checks.

1. #54 is already merged: do not merge/cherry-pick it again.
2. Resolve #53's manual and installer verification gaps, then request owner
   approval to merge #53. It already contains current main; no conflict repair
   is currently needed. Keep WPF release fallback.
3. Update #52 against the resulting main and rerun Flutter checks and scanner
   device pairing tests before seeking approval for that dependency update.

After approval and integration, fast-forward local main explicitly, reconcile
stale branch names, review the saved stash and update migration/onboarding docs.
Those cleanup actions can wait; none is necessary to preserve current work.
No production-code fix was justified by the reproduced results of this audit.

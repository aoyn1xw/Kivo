# Kivo Rebranding & Multi-Agent Migration Guide

## 1. Overview & Purpose

This repository has been officially renamed from **ShadowPlay** to **Kivo** (`https://github.com/aoyn1xw/Kivo`).
This document is the central specification, naming inventory, compatibility ledger, verification report, and multi-agent handoff record for the **ShadowPlay → Kivo** rebrand on branch `rebrand/kivo`.

All coding agents working on this repository (**Google Antigravity**, **OpenCode**, and **OpenAI Codex**) must read and maintain this document so work can proceed seamlessly without relying on prior chat history.

---

## 2. Repository Architecture

Kivo is a Windows desktop tray application paired with a Flutter Android/iOS client that monitors completed `.mp4` game recordings and serves original files over a private LAN.

| Layer               | Pre-Rebrand Path / Name                            | Target Kivo Path / Name                | Notes                                                                                   |
| :------------------ | :------------------------------------------------- | :------------------------------------- | :-------------------------------------------------------------------------------------- |
| Solution            | `ShadowPlay.sln`                                   | `Kivo.sln`                             | Four-project .NET 8 solution                                                            |
| Core Library        | `src/ShadowPlay.Core/ShadowPlay.Core.csproj`       | `src/Kivo.Core/Kivo.Core.csproj`       | UI-free clip monitoring, pairing, settings, HTTPS identity (`Kivo.Core`)                |
| LAN API Library     | `src/ShadowPlay.Api/ShadowPlay.Api.csproj`         | `src/Kivo.Api/Kivo.Api.csproj`         | Kestrel HTTP/HTTPS LAN API, bearer auth, private-network guard (`Kivo.Api`)             |
| Windows Desktop App | `src/ShadowPlay.Windows/ShadowPlay.Windows.csproj` | `src/Kivo.Windows/Kivo.Windows.csproj` | WPF UI, system tray, firewall helper, smoke runner (`Kivo.Windows`, outputs `Kivo.exe`) |
| Desktop Test Suite  | `tests/ShadowPlay.Tests/ShadowPlay.Tests.csproj`   | `tests/Kivo.Tests/Kivo.Tests.csproj`   | xUnit unit + Kestrel integration tests (`Kivo.Tests`)                                   |
| Flutter Mobile App  | `flutter/` (`name: shadowplay`)                    | `flutter/` (`name: kivo`)              | Dart package `kivo`, Android & iOS clients                                              |
| Windows Installer   | `installer/ShadowPlay.iss`                         | `installer/Kivo.iss`                   | Inno Setup script producing `Kivo-Setup-<version>.exe`                                  |
| CI / Release / OTA  | `.github/workflows/*.yml`, `docs/*`                | Updated for `Kivo`                     | GitHub Actions workflows and GitHub Pages OTA / download site                           |

---

## 3. Naming Inventory & Compatibility Matrix

### 3.1 Rebranded Identifiers (Updated to Kivo)

1. **Windows Solution, Projects, Namespaces & Assemblies:**
   - `ShadowPlay.sln` → `Kivo.sln`
   - `src/ShadowPlay.Core` → `src/Kivo.Core` (`RootNamespace` / `AssemblyName`: `Kivo.Core`)
   - `src/ShadowPlay.Api` → `src/Kivo.Api` (`RootNamespace` / `AssemblyName`: `Kivo.Api`)
   - `src/ShadowPlay.Windows` → `src/Kivo.Windows` (`RootNamespace`: `Kivo.Windows`, `AssemblyName`: `Kivo`, output binary: `Kivo.exe`)
   - `tests/ShadowPlay.Tests` → `tests/Kivo.Tests` (`RootNamespace` / `AssemblyName`: `Kivo.Tests`)
   - `Directory.Build.props`: `<Company>Kivo</Company>`, `<Product>Kivo</Product>`
   - C# namespaces across all source and test files: `ShadowPlay.*` → `Kivo.*`
2. **Windows User-Facing UI & OS Integration:**
   - Main window, setup wizard, settings window, tray icon tooltip/context menu/balloon notifications (`Kivo`)
   - Default PC display name fallback in `MainViewModel`: `"Kivo PC"`
3. **Flutter & Mobile Client:**
   - `flutter/pubspec.yaml`: `name: kivo`, description updated to Kivo
   - Dart package imports: `package:shadowplay/...` → `package:kivo/...`
   - Root widget `KivoApp` (with `typedef ShadowPlayApp = KivoApp;` alias for backwards compatibility)
   - User-facing strings across `home_screen.dart`, `onboarding_flow.dart`, `settings_screen.dart`, `diagnostics_screen.dart`, `main.dart`, `shadowplay_api.dart`, `diagnostics.dart` (`Kivo Diagnostics Report`, `app: Kivo mobile 0.1.0 (1)`)
   - Media gallery album name default in `MediaGallery.saveVideo`: `'Kivo'`
   - Android display label in `AndroidManifest.xml`: `android:label="Kivo"`
   - iOS display names in `Info.plist`: `CFBundleDisplayName` = `Kivo`, `CFBundleName` = `Kivo`, and permission usage descriptions (`NSLocalNetworkUsageDescription`, `NSCameraUsageDescription`, `NSPhotoLibraryAddUsageDescription`, `NSPhotoLibraryUsageDescription`)
   - Flutter Windows runner (`flutter/windows/`): `CMakeLists.txt`, `Runner.rc`, `main.cpp`
4. **CI/CD, Installer & Distribution:**
   - `installer/Kivo.iss`: `MyAppExeName "Kivo.exe"`, `DefaultDirName={autopf}\Kivo`, `DefaultGroupName=Kivo`, `OutputBaseFilename=Kivo-Setup-{#MyAppVersion}`, `AppPublisherURL=https://github.com/aoyn1xw/Kivo`
   - `.github/dependabot.yml`: nuget directory list points at `/src/Kivo.Api`, `/src/Kivo.Core`, `/src/Kivo.Windows`, `/tests/Kivo.Tests`
   - `.github/workflows/ci.yml`: builds/tests `Kivo.sln`, publishes `src/Kivo.Windows/Kivo.Windows.csproj`, runs smoke test on `Kivo.exe`
   - `.github/workflows/release.yml`: resolves `KIVO_VERSION`, publishes `src/Kivo.Windows/Kivo.Windows.csproj`, compiles `installer/Kivo.iss`, outputs `Kivo-Setup-*.exe`, `Kivo-Android.apk`, and preserves `Kivo.ipa` (with fallback migration from `ShadowPlay.ipa`) on rolling `latest` releases
   - `.github/workflows/ios-ota.yml`, `docs/index.html`, `docs/manifest.plist`: updated repository links (`https://github.com/aoyn1xw/Kivo`), GitHub Pages links (`https://aoyn1xw.github.io/Kivo/`), IPA filename (`Kivo.ipa`), archive path (`Kivo.xcarchive`), and display titles (`Kivo`), while keeping personal OTA signing intact

### 3.2 Intentionally Preserved Legacy Identifiers (DO NOT CHANGE)

To guarantee zero data loss, seamless upgrade behavior, unbroken TLS certificate pinning, and mobile OS upgrade continuity, the following identifiers **must remain unchanged**:

| Component                          | Identifier / Path                                                                                                                    | Location                                                                                                           | Reason for Preservation                                                                                                                                                                                                              |
| :--------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Windows AppData                    | `%LocalAppData%\ShadowPlay` (`AppDataFolderName = "ShadowPlay"`)                                                                     | `src/Kivo.Windows/Bootstrap/AppHostFactory.cs`, `installer/Kivo.iss`                                               | Preserves `settings.json`, paired devices, bearer token hashes, logs, and thumbnail cache across upgrades.                                                                                                                           |
| TLS Certificate Identity           | `CertificateSubject = "CN=ShadowPlay Local"`, `FriendlyName = "ShadowPlay Local HTTPS"`, `LoopbackFallbackName = "shadowplay.local"` | `src/Kivo.Core/Networking/HttpsIdentityService.cs`                                                                 | Desktop looks up existing self-signed cert in Windows `CurrentUser\My` X509Store by subject & friendly name. Changing these would regenerate the certificate and break TLS SHA-256 fingerprint pinning on all already-paired phones. |
| Single-Instance Mutex & Event      | `Local\ShadowPlay.SingleInstance.9f2c1a`, `Local\ShadowPlay.Activate.9f2c1a`                                                         | `src/Kivo.Windows/Bootstrap/SingleInstanceGuard.cs`, `installer/Kivo.iss`                                          | Matches `AppMutex` in Inno Setup so the installer detects a running pre-upgrade or post-upgrade app instance.                                                                                                                        |
| Inno Setup AppId                   | `AppId={{16C8742E-AD7A-4A83-AF71-EAA2F1405DB3}`                                                                                      | `installer/Kivo.iss`                                                                                               | Ensures Windows treats Kivo installer as an in-place upgrade over existing ShadowPlay installations (`UsePreviousAppDir=yes`). Eligible registered default in-place upgrades retire explicit legacy binaries after file installation and retarget verified shortcuts. Ambiguous/custom/different-directory cleanup is skipped. |
| Firewall Rule Names                | `RuleName = "ShadowPlay LAN API (Private)"`, `RuleNamePrefix = "ShadowPlay LAN API (Private) TCP "`                                  | `src/Kivo.Windows/Services/WindowsFirewallService.cs`                                                              | Matches existing netsh rule cleanup logic, `FirewallRuleScriptTests`, and `FirewallRuleMigrationTests`. Rules kept under these names but still pointing at the pre-rebrand `ShadowPlay.exe` are classified as legacy and migrated through the guided "Allow private LAN access" repair (UAC), which deletes stale/legacy rules before re-adding private-profile-only per-port rules. |
| Android Application ID & Namespace | `com.ayon1xw.shadowplay`                                                                                                             | `flutter/android/app/build.gradle.kts`, `MainActivity.kt`                                                          | Explicit operational constraint: preserves Android app identity, Keystore/EncryptedSharedPreferences, and in-place APK upgrades.                                                                                                     |
| iOS Bundle Identifiers             | `app.winter6686.date9988` (OTA/signing) & `com.example.shadowplay` (project default)                                                 | `.github/workflows/ios-ota.yml`, `docs/manifest.plist`, `project.pbxproj`                                          | Explicit operational constraint: required by existing provisioning profile and personal signed iOS OTA installer.                                                                                                                    |
| Flutter Platform Channels          | `shadowplay/media`, `shadowplay/local_network`                                                                                       | `flutter/lib/core/media_gallery.dart`, `app_permissions.dart`, `AppDelegate.swift`                                 | Internal Dart ↔ Native method channel names; preserved per instructions to avoid native bridge breakage.                                                                                                                             |
| iOS Diagnostics Path               | `/ShadowPlayDiagnostics/`                                                                                                            | `flutter/lib/core/media_gallery.dart`, `flutter/lib/core/diagnostics.dart`, `flutter/ios/Runner/AppDelegate.swift` | Internal temp folder marker used by native iOS high-frame-rate Photos export and diagnostics bundler.                                                                                                                                |
| Desktop Log Filename               | `shadowplay.log`                                                                                                                     | `src/Kivo.Windows/Infrastructure/RollingFileLogger.cs`                                                             | Preserves existing rolling log file continuity under `%LocalAppData%\ShadowPlay\logs\shadowplay.log`.                                                                                                                                |

---

## 4. Migration Phases & Status

- [x] **Phase 1 — Inspect and document**
  - Inspected full repository across C#, WPF, Flutter, Android, iOS, Inno Setup, GitHub Actions, and docs.
  - Created `docs/KIVO_REBRAND.md` and updated `AGENTS.md`.
- [x] **Phase 2 — Rebrand the Windows application**
  - Renamed `ShadowPlay.sln` → `Kivo.sln` and project directories `src/Kivo.Core`, `src/Kivo.Api`, `src/Kivo.Windows`, `tests/Kivo.Tests` (and `.csproj` files) via `git mv`.
  - Updated `.csproj` files, `Directory.Build.props`, C# & XAML namespaces (`Kivo.*`), UI strings, tray UI, executable metadata (`Kivo.exe`), and all 18 test files in `tests/Kivo.Tests/`.
  - Preserved `%LocalAppData%\ShadowPlay`, `HttpsIdentityService` X509 constants, `SingleInstanceGuard` mutexes, and `WindowsFirewallService` rule names.
- [x] **Phase 3 — Rebrand Flutter, Android, and iOS**
  - Updated `flutter/pubspec.yaml` (`name: kivo`), Dart package imports (`package:kivo/...`), `KivoApp` root widget, user-facing strings, diagnostics header (`Kivo Diagnostics Report`), media album (`Kivo`), `AndroidManifest.xml` label, `Info.plist` display names and permission strings, `flutter/windows` runner metadata, and all 8 Flutter test files.
  - Preserved Android `applicationId`, iOS bundle IDs, platform channels, and secure storage keys.
- [x] **Phase 4 — CI/CD, installer, and distribution**
  - Renamed `installer/ShadowPlay.iss` → `installer/Kivo.iss` and updated installer metadata, exe names (`Kivo.exe`), output filename (`Kivo-Setup-{#MyAppVersion}`), shortcuts, and GitHub links while preserving `AppId`, `AppMutex`, `UsePreviousAppDir=yes`, and `{localappdata}\ShadowPlay`.
  - Updated `.github/workflows/ci.yml`, `.github/workflows/release.yml`, `.github/workflows/ios-ota.yml`, `docs/index.html`, and `docs/manifest.plist`.
- [x] **Phase 5 — Documentation and cleanup**
  - Updated `README.md`, `flutter/README.md`, `flutter/design-qa.md`, and `AGENTS.md`.
  - Audited and classified all remaining `ShadowPlay` references.
- [x] **Phase 6 — Verification** *(as reported by Google Antigravity at implementation time; see §7 for the verification ledger distinguishing executed runs, Antigravity-reports, CI-only checks, and remaining manual validation)*
  - Ran `dotnet restore`, `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, Windows publish + `--smoke` test, `flutter pub get`, `flutter analyze`, `flutter test`, `dart format --output=none --set-exit-if-changed lib test`, and `flutter build apk --debug`.
- [x] **Phase 7 — Multi-agent handoff**
  - Recorded complete file inventory, test results, compatibility decisions, and handoff instructions for Google Antigravity, OpenCode, and OpenAI Codex.

---

## 5. Modified & Renamed Files Inventory

### 5.1 Renamed Files (`git mv`)

- `ShadowPlay.sln` → `Kivo.sln`
- `src/ShadowPlay.Core/*` → `src/Kivo.Core/*` (including `ShadowPlay.Core.csproj` → `Kivo.Core.csproj` and 31 `.cs` files)
- `src/ShadowPlay.Api/*` → `src/Kivo.Api/*` (including `ShadowPlay.Api.csproj` → `Kivo.Api.csproj` and 6 `.cs` files)
- `src/ShadowPlay.Windows/*` → `src/Kivo.Windows/*` (including `ShadowPlay.Windows.csproj` → `Kivo.Windows.csproj` and 19 `.cs` + 4 `.xaml` files)
- `tests/ShadowPlay.Tests/*` → `tests/Kivo.Tests/*` (including `ShadowPlay.Tests.csproj` → `Kivo.Tests.csproj` and 18 `.cs` files)
- `installer/ShadowPlay.iss` → `installer/Kivo.iss`

### 5.2 Modified Files (In-Place)

- **Root & Docs:** `AGENTS.md`, `Directory.Build.props`, `README.md`, `docs/index.html`, `docs/manifest.plist`, `docs/KIVO_REBRAND.md` (new)
- **GitHub Workflows:** `.github/workflows/ci.yml`, `.github/workflows/release.yml`, `.github/workflows/ios-ota.yml`
- **Flutter Core & UI:** `flutter/pubspec.yaml`, `flutter/README.md`, `flutter/design-qa.md`, `flutter/lib/main.dart`, `flutter/lib/app/app.dart`, `flutter/lib/core/diagnostics.dart`, `flutter/lib/core/media_gallery.dart`, `flutter/lib/core/shadowplay_api.dart`, `flutter/lib/features/home/home_screen.dart`, `flutter/lib/features/onboarding/onboarding_flow.dart`, `flutter/lib/features/settings/diagnostics_screen.dart`, `flutter/lib/features/settings/settings_screen.dart`
- **Flutter Tests:** `flutter/test/app_permissions_test.dart`, `flutter/test/app_state_test.dart`, `flutter/test/diagnostics_test.dart`, `flutter/test/media_gallery_test.dart`, `flutter/test/models_test.dart`, `flutter/test/resumable_download_test.dart`, `flutter/test/ui_layout_test.dart`, `flutter/test/widget_test.dart`
- **Native Mobile & Runner Metadata:** `flutter/android/app/src/main/AndroidManifest.xml`, `flutter/ios/Runner/Info.plist`, `flutter/windows/CMakeLists.txt`, `flutter/windows/runner/Runner.rc`, `flutter/windows/runner/main.cpp`

---

## 6. Classification of Remaining `ShadowPlay` References

Every remaining occurrence of `ShadowPlay` or `shadowplay` in the repository has been audited via `git grep -i "shadowplay"` and belongs to one of four categories:

1. **Intentional Compatibility Identifiers (Must Keep):**
   - `src/Kivo.Windows/Bootstrap/AppHostFactory.cs`, `src/Kivo.Core/Models/AppSettingsData.cs`, `src/Kivo.Core/Settings/JsonSettingsStore.cs`, `installer/Kivo.iss`: `AppDataFolderName = "ShadowPlay"` (`%LocalAppData%\ShadowPlay`)
   - `src/Kivo.Core/Networking/HttpsIdentityService.cs`, `tests/Kivo.Tests/HttpsIdentityServiceTests.cs`: `CertificateSubject = "CN=ShadowPlay Local"`, `FriendlyName = "ShadowPlay Local HTTPS"`, `LoopbackFallbackName = "shadowplay.local"`
   - `src/Kivo.Windows/Bootstrap/SingleInstanceGuard.cs`, `installer/Kivo.iss`: `Local\ShadowPlay.SingleInstance.9f2c1a`, `Local\ShadowPlay.Activate.9f2c1a`
   - `src/Kivo.Windows/Services/WindowsFirewallService.cs`, `tests/Kivo.Tests/FirewallRuleScriptTests.cs`, `tests/Kivo.Tests/FirewallRuleMigrationTests.cs`: `RuleName = "ShadowPlay LAN API (Private)"`, `RuleNamePrefix = "ShadowPlay LAN API (Private) TCP "`
   - `installer/Kivo.iss`: `[Code]` legacy-migration list targeting installer-published `ShadowPlay.exe`, `ShadowPlay.dll`, `ShadowPlay.Core.*`, `ShadowPlay.Api.*`, `ShadowPlay.Windows.*` binaries and legacy `ShadowPlay` start-menu/desktop shortcuts only
   - `src/Kivo.Windows/Infrastructure/RollingFileLogger.cs`: `shadowplay.log`
   - `flutter/android/app/build.gradle.kts`, `flutter/android/app/src/main/kotlin/com/ayon1xw/shadowplay/MainActivity.kt`, `flutter/test/diagnostics_test.dart`: Android `namespace` / `applicationId` = `com.ayon1xw.shadowplay`
   - `flutter/ios/Runner.xcodeproj/project.pbxproj`: default `PRODUCT_BUNDLE_IDENTIFIER = com.example.shadowplay` (overridden by `app.winter6686.date9988` in `ios-ota.yml`)
   - `flutter/lib/core/media_gallery.dart`, `flutter/lib/core/app_permissions.dart`, `flutter/lib/core/diagnostics.dart`, `flutter/ios/Runner/AppDelegate.swift`, `flutter/test/media_gallery_test.dart`, `flutter/test/app_permissions_test.dart`, `flutter/test/diagnostics_test.dart`: MethodChannel names `shadowplay/media` and `shadowplay/local_network`, and iOS HFR temp directory `ShadowPlayDiagnostics`
   - `flutter/lib/core/shadowplay_api.dart` (with `typedef KivoApi = ShadowPlayApi;`) and `flutter/lib/app/app.dart` (`typedef ShadowPlayApp = KivoApp;`): internal Dart symbol aliases
   - `.github/workflows/release.yml`: fallback check for legacy `ShadowPlay.ipa` asset when migrating the rolling `latest` release to `Kivo.ipa`, and verification that superseded `ShadowPlay-win-x64.zip` / `ShadowPlay-Android-debug.apk` assets are absent
2. **External / Product References & Disclaimers (Intentional):**
   - `README.md`, `docs/index.html`, `src/Kivo.Windows/Views/SetupWindow.xaml`: references to NVIDIA ShadowPlay / NVIDIA GeForce Experience recordings and the NVIDIA non-affiliation disclaimer
3. **Historical References (Intentional):**
   - `docs/KIVO_REBRAND.md`, `AGENTS.md`, `flutter/design-qa.md`
4. **Generated or Third-Party Content:**
   - Historical design-QA XML snapshots retain the old application label/package. These are archived evidence. `pubspec.lock` has no root-package entry and no rebrand change.

---

## 7. Verification Report

This section separates four verification categories so nobody mistakes a
configuration for a completed run:

1. **Executed locally by OpenCode** — the post-rebrand fix session (this round).
2. **Reported by Antigravity** — claims made during the rebrand implementation; not independently verified by Codex in that session; see the individual category 1 exclusions.
3. **CI-configured but not executed locally** — GitHub Actions workflows that will exercise these paths on push/PR/tag/dispatch.
4. **Remaining manual validation** — checks that cannot run unattended on a dev box.

### 7.1 Executed by OpenCode during the Codex-review fix session

Machine: Windows x64, .NET SDK 8.0.427, Flutter 3.47.2, Inno Setup 6.7.3.

| Check                                   | Command                                                                                                                                   | Result              | Details                                                                                                                                                          |
| :-------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------- | :------------------ | :--------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET Restore                            | `dotnet restore Kivo.sln`                                                                                                                 | **PASSED** (exit 0) | Restored `Kivo.Core`, `Kivo.Api`, `Kivo.Windows`, `Kivo.Tests`.                                                                                                  |
| .NET Build                              | `dotnet build Kivo.sln -c Release`                                                                                                        | **PASSED** (exit 0) | 0 warnings, 0 errors (after fixing two CA1305 warnings introduced by the firewall-migration refactor).                                                            |
| .NET Tests                              | `dotnet test Kivo.sln -c Release --no-build`                                                                                              | **PASSED** (exit 0) | **132 passed, 0 failed, 0 skipped** — 120 pre-existing + 12 new firewall-migration/script tests.                                                                  |
| .NET Format                             | `dotnet format Kivo.sln --verify-no-changes`                                                                                              | **PASSED** (exit 0) | No formatting issues.                                                                                                                                            |
| Windows Publish                         | `dotnet publish src/Kivo.Windows/Kivo.Windows.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64`                      | **PASSED** (exit 0) | Output removed after validation.                                                                                                                                 |
| Windows Smoke                           | `Kivo.exe --smoke --folder <temp> --port 0 --smoke-seconds 12`                                                                             | **PASSED** (exit 0) | Exercises startup + sharing + the refactored firewall `Check` path (real COM enumeration) with the temp recordings folder removed afterwards.                     |
| Flutter Dependencies                    | `flutter pub get` (flutter/)                                                                                                              | **PASSED** (exit 0) | Resolved for `kivo`.                                                                                                                                             |
| Flutter Analyze                         | `flutter analyze`                                                                                                                         | **PASSED** (exit 0) | No issues found.                                                                                                                                                 |
| Flutter Tests                           | `flutter test`                                                                                                                            | **PASSED** (exit 0) | **96 passed, 0 failed.**                                                                                                                                         |
| Dart Format                             | `dart format --output=none --set-exit-if-changed lib test`                                                                                 | **PASSED** (exit 0) | 26 files, 0 changed.                                                                                                                                             |
| Installer Compile                       | `ISCC.exe installer\Kivo.iss` (Inno 6.7.3) against the published `win-x64` output                                                          | **PASSED** (exit 0) | `[Code]` section (legacy migration Pascal) compiles cleanly; produced `artifacts/installer/Kivo-Setup-*.exe`, removed after validation. The first compile attempt failed with `EndUpdateResource failed (110)` — an antivirus-stamp race on the freshly written `Setup.exe`, not a script error; the immediate retry compiled successfully. |
| Dependabot Directories                  | `Test-Path` over every configured directory                                                                                                | **PASSED**          | `/src/Kivo.Api`, `/src/Kivo.Core`, `/src/Kivo.Windows`, `/tests/Kivo.Tests`, `/flutter`, `/` all exist.                                                           |
| Android Debug APK                   | —                                                                                                                                         | **NOT RE-RUN**      | Antigravity reported success (§7.2); no Android outputs were touched by this session, and the CI `android` job rebuilds it on push.                                |
| iOS compile (`flutter build ios`)       | —                                                                                                                                         | **NOT RUN LOCALLY** | Requires macOS + Xcode. See §7.3/§7.4.                                                                                                                           |

### 7.2 Reported by Antigravity during the rebrand implementation (not executed by the reviewers)

These were recorded in the original version of this document and drive the
baseline counts above; they were not run by Codex or OpenCode:

| Check                       | Reported result                                    |
| :-------------------------- | :------------------------------------------------- |
| `dotnet restore/build/test/format` | 120 passed / 0 failed (baseline before the 12 migration tests added later); build & format clean. |
| Windows publish + `--smoke` | `SMOKE_OK {"port":61966,"clipsAtReady":0}`, `SMOKE_CLIPS {"clips":2}`. |
| `flutter pub get`/`analyze`/`test`/`dart format` | 96 passed, analyze clean, 26 files formatted. |
| `flutter build apk --debug` | Built `app-debug.apk` via Gradle `assembleDebug`.   |
| iOS compile, ISCC installer compile | **NOT RUN** at implementation time (macOS-required / Inno not installed). |

### 7.3 CI-configured, but not executed locally

- **`.github/workflows/ci.yml`** (main pushes/PRs/manual): Windows build + tests, framework-dependent publish (`--self-contained false`) + `--smoke` fixture, `flutter-checks` (`pub get`, `analyze`, `test`, format guard), `android` debug APK build, `ios` unsigned build on `macos-15`.
- **`.github/workflows/release.yml`** (main pushes, manual dispatch, and version tags): resolves `KIVO_VERSION`, self-contained publish, compiles `installer/Kivo.iss` via ISCC, verifies installer version vs. `Kivo.exe`, publishes `Kivo-Setup-*.exe`/`Kivo-Android.apk`, and migrates rolling `latest` assets (`ShadowPlay.ipa` → `Kivo.ipa` fallback).
- **`.github/workflows/ios-ota.yml`** (manual dispatch): signs `Kivo.ipa` with the preserved personal profile and refreshes `docs/manifest.plist`.

### 7.4 Remaining manual validation

1. **Upgrade install** on a machine with an existing ShadowPlay installation: run `Kivo-Setup-<version>.exe`, confirm settings/pairing/certificates/recordings survive, eligible legacy binaries are removed after file installation and verified shortcuts target Kivo, and the uninstall ("Remove Kivo application data too?") flow is unchanged.
2. **Upgrade with ShadowPlay.exe running**: the shared `AppMutex` + `CloseApplications=yes` path should prompt/require closing the old app before files are touched.
3. **Firewall repair**: with legacy netsh rules present, the main window should display the "previous ShadowPlay installation" guidance and the *Allow private LAN access* button; approving the UAC prompt must migrate rules without opening any non-private profile; phone pairing should continue working.
4. **iOS signed OTA**: dispatch `ios-ota.yml`, install on a physical phone, verify OTA manifest and HFR export.

---

## 8. Multi-Agent Handoff Instructions (Google Antigravity, OpenCode, OpenAI Codex)

### Current Git State

- **Branch:** `rebrand/kivo` (branched from `main` at `853fffa`)
- **Stash:** `stash@{0}: On main: Pre-Kivo crypto lockfile update` is untouched and preserved. **Do not pop or drop `stash@{0}`.**
- **Working Tree:** All Phase 1–7 changes plus the post-rebrand fix session (§9 — installer upgrade migration, firewall rule migration, dependabot paths, documentation corrections) are in the working tree on `rebrand/kivo`, unstaged/staged and ready for user-supervised commit. Nothing has been committed, pushed, published, or deployed.

### Next Safe Steps for OpenCode / OpenAI Codex / Google Antigravity

1. **Review & Commit (When Approved by User):**
   - Inspect `git status` and `git diff` on `rebrand/kivo`.
   - Stage and commit the rebrand changes on `rebrand/kivo` once the user approves committing/pushing.
2. **CI & Release Verification (Post-Push):**
   - When the user pushes `rebrand/kivo` or opens a PR, verify that GitHub Actions `CI` (`windows`, `flutter-checks`, `android`, and `ios` on `macos-15`) passes.
   - When merged to `main`, `release.yml` will build `Kivo-Setup-<version>.exe` and `Kivo-Android.apk`, migrate any existing `ShadowPlay.ipa` to `Kivo.ipa` on the `latest` release, and publish `Kivo (Latest Build)`.
   - When `ios-ota.yml` is manually dispatched, it will sign `Kivo.ipa` with `app.winter6686.date9988`, upload `Kivo.ipa` to the `latest` release, and update `docs/manifest.plist`.
3. **Optional Future Enhancements (Require Explicit User Approval First):**
   - **Android Application ID / iOS Bundle ID:** Currently preserved as `com.ayon1xw.shadowplay` and `app.winter6686.date9988` / `com.example.shadowplay`. Do not change unless the user explicitly approves a breaking mobile identity migration and updates the Apple provisioning profile.
   - **Platform Channels & Internal File Names:** `shadowplay/media`, `shadowplay/local_network`, and `flutter/lib/core/shadowplay_api.dart` (`ShadowPlayApi` / `KivoApi` typedef) are preserved for stability. If renamed in the future, update Dart, `AppDelegate.swift`, and `media_gallery_test.dart` in lockstep.

---

## 9. Post-Rebrand Fix Session (Codex-Review Issues)

Codex independently reviewed the working tree and raised four issues. OpenCode
resolved them without redoing the rebrand; this section is the fix record.

### 9.1 Installer upgrade safety - final Codex repair

The installer preserves the original AppId, mutex and `UsePreviousAppDir=yes`.
Automatic legacy cleanup is deliberately narrower than installation support:

- Only the native `HKLM64` uninstall registration for the original AppId is
  considered. No HKCU, WOW6432Node guessing, portable-copy fallback, or executable
  filename marker grants permission to delete files.
- The recorded directory must normalize to the exact historical default
  `{autopf}\ShadowPlay`. Its uninstall command must identify its own
  `unins000.exe`, and both uninstall inventory files must exist. Existing local
  absolute paths are checked for traversal, alternate streams, aliases, and
  reparse points at the target and every ancestor. Custom paths are preserved.
- Cleanup runs only in `ssPostInstall`, only if `{app}` is the same verified
  directory, and rechecks path safety before operating. A different-directory
  installation preserves all old files and its uninstall inventory. No generic
  uninstaller files or installation directories are deleted by migration code.
- Only the explicit legacy application filename list is deleted. Each existing
  target is checked before deletion, retried, checked afterwards, and failures
  logged individually. Exceptions are reported per target. No application data,
  recordings, pairing state, or certificate store is a cleanup target.
- Existing `{group}\ShadowPlay.lnk` and common desktop links are retargeted to
  Kivo only if their actual target matches the verified old executable and they have no arguments. Unknown
  links and user-profile locations are left alone. The old shortcut label is
  retained as an upgrade alias. Inno retains the previous Start Menu group by
  default, so upgraded installations can show Kivo within a ShadowPlay group.
  Custom groups or user-created links outside these locations may remain.
- `SetupLogging=yes` enables diagnostics in silent and interactive setup. Skips
  and failures are logged; interactive setup shows a warning summary. A failed
  operation is never logged as successfully cleaned. The log can contain local
  paths and should be handled as diagnostic data.
- AppMutex/CloseApplications behavior is preserved. Locked-file retry does not
  schedule reboot deletion. Leftovers require review/manual removal; the app
  is not forced closed by the cleanup code.

Custom, per-user, unregistered, redirected, or ambiguous installations may keep
harmless legacy binaries. A different-directory install preserves the old
uninstaller files, but Windows registration may now point to Kivo; this is not
an automatic relocation/uninstallation mechanism. Prefer in-place upgrades.
The existing uninstall-time opt-in data-removal prompt is unchanged.

Compilation does not establish runtime upgrade safety. Test these cases in a
disposable VM: registered default in-place upgrade; custom/different directory;
missing/mismatched uninstall metadata; junction/symlink and unsafe candidates;
locked binaries; read-only or foreign-target shortcuts; custom Start Menu group;
silent install logs; cancellation; uninstall with data retained. Compare settings,
paired devices, TLS fingerprint and recordings before/after.

### 9.2 Windows Firewall migration (MEDIUM) — fixed

Before: rules created by pre-rebrand builds kept the names
`ShadowPlay LAN API (Private)*` but pointed at the old `ShadowPlay.exe` path;
`Check` called them "stale" with no mention of the upgrade, and the elevation
script only replaced rules for the currently requested ports, leaving
retired-port rules for `ShadowPlay.exe` behind.

`src/Kivo.Windows/Services/WindowsFirewallService.cs`:

- `FirewallRuleSnapshot` (record) + `CollectRuleSnapshots` normalize each
  app-owned COM rule (matched by the preserved rule-name set) into a testable
  value; `AnalyzeRules` is now a pure function with unchanged state semantics
  (`Ready`, `Mismatched`, `BlockedByNetworkProfile`).
- Rules that satisfy every security restriction (enabled, inbound, allow, TCP,
  private profile, exact port) except the **program path** are classified as
  **legacy**: `Check` reports `Mismatched` with explicit migration guidance —
  "…still point at the previous ShadowPlay installation; click the button and
  approve the administrator prompt to update them for Kivo." The existing
  `MainViewModel` surfaces the detail text and the *Allow private LAN access*
  button whenever `NeedsSetup` is true.
- `CollectRuleNamesToDelete` lists app-owned rule names that are not part of
  the current per-port set; `TryCollectStaleRuleNames` enumerates them via the
  same COM API (non-elevated, best-effort) and
  `BuildRuleScript(path, ports, extraRuleNamesToDelete)` deletes them, plus the
  legacy base name, **before** re-adding the requested rules in the same
  elevated PowerShell run.
- Permissions: repair goes through the existing `runas`-verb UAC prompt with
  guidance in the status text; enumeration failures degrade to the base
  replace script and are logged, never thrown at the user.
- Security stance unchanged and regression-guarded by tests: rules are always
  `action=allow protocol=TCP localport=<exact> profile=private program=<Kivo
  path>`; the script never emits `profile=any/all/domain/public` or
  `localport=any`, and delete-before-add ordering guarantees no widening even
  mid-repair. Delete operations only accept the exact legacy name or canonical decimal per-port names in 1..65535. Both discovery and script generation enforce this restriction, including rejection of whitespace, signs, leading zeroes, arbitrary suffixes and quoted input.

Tests (12 added / updated): `FirewallRuleMigrationTests`
(ready/mismatched/state semantics, legacy guidance text and scoping, stale
vs. legacy, cleanup-name computation including foreign-name safety) and
`FirewallRuleScriptTests` (stale/legacy deletion before re-add, quote
escaping in extra names, no-broadening invariants). `AppController`
was inspected and needed no change — it already routes `Check` during
sharing start and `EnsurePrivateRuleAsync` on user repair, and re-checks
after the elevated run.

### 9.3 Dependabot configuration (MEDIUM) — fixed

`.github/dependabot.yml` nuget `directories` now list
`/src/Kivo.Api`, `/src/Kivo.Core`, `/src/Kivo.Windows`, `/tests/Kivo.Tests`
(the rebranded paths; the previous `ShadowPlay.*` directories no longer
exist). `pub` on `/flutter` and `github-actions` on `/` were already correct.
All configured directories verified to exist (§7.1).

### 9.4 Migration documentation (LOW) — fixed

`docs/KIVO_REBRAND.md` corrections made by this session:

- §7 fully restructured to separate **executed** (this session), **reported**
  (Antigravity claims, explicitly marked), **CI-configured** (not run locally),
  and **remaining manual validation**. The previous table implied Antigravity's
  runs were an executed verification report.
- File-inventory counts corrected against the tree: `src/Kivo.Core` 31 `.cs`
  files (was 25), `src/Kivo.Windows` 19 `.cs` + 4 `.xaml` (was "24"). The four
  count fixes came from a recursive count excluding `bin`/`obj`.
- §3.1/§3.2 updated for dependabot paths and the installer/firewall migration
  behavior; §4 Phase 6 now labelled as Antigravity-reported; §5.1 corrected;
  §6 inventory extended with the new compatibility identifiers
  (`FirewallRuleMigrationTests.cs`, installer legacy-name list).
- `AGENTS.md` updated with the upgrade-migration and firewall-migration
  constraints future agents must respect.


## 10. Final Codex safety repair verification

This section records checks personally run by Codex for the final repair;
section 7.1 remains OpenCode's earlier report, not a claim of Codex execution.

- `dotnet build Kivo.sln -c Release`: passed, zero warnings/errors.
- `dotnet test Kivo.sln -c Release --no-build`: 145/145 passed outside the
  filesystem sandbox. The first sandboxed run passed 132 and failed 13 TLS
  tests at temporary Windows key-container creation (CryptographicException);
  rerunning with normal user-profile access passed all tests. No TLS behavior
  or certificate identifiers were changed to bypass those failures.
- Firewall regression coverage: 11 malformed-name cases and both valid port
  boundaries exercise ownership and deletion filtering. The script test now
  rejects foreign quoted names instead of permitting their deletion. These
  checks do not modify firewall rules.
- `dotnet format Kivo.sln --verify-no-changes --no-restore`: passed.
- `dotnet publish src/Kivo.Windows/Kivo.Windows.csproj -c Release -r win-x64
  --self-contained false -o publish/win-x64`: passed.
- Inno Setup 6.7.3 compiler: passed against that published payload. An initial
  Pascal line beginning with a preprocessor marker was corrected; a subsequent
  resource-update error 110 disappeared on retry. The cause of that packaging
  error was not established. Compilation does not exercise cleanup at runtime.
- `git diff main --check`: passed. The existing full rebrand diff and this
  repair's files were reviewed; no Flutter source or platform identity changed
  in this repair. Flutter checks were therefore not re-run.
- Standalone desktop smoke was not run: it writes current-user settings and
  may create a persisted HTTPS identity. Use a disposable Windows profile/VM
  for that check, together with the manual installer cases in section 9.1.
  The .NET suite exercised real HTTP/HTTPS integration tests successfully.
- No installer was executed, no firewall rules were changed, and nothing was
  committed, pushed, merged, published, or deployed.

Remaining limitations: ownership is based on the protected machine registration
and default Program Files installation, not on authenticated old binary hashes.
Custom/unregistered/ambiguous installations intentionally retain old files.
Administrator-controlled changes can race filesystem checks; the migration is
not a security boundary against another administrator. Installer runtime safety,
shortcut retargeting, failure logging, data retention, and signed mobile upgrade
continuity still require disposable-environment manual validation.

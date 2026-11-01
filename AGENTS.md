# Agent Guide

## Overview and layout

Kivo (formerly ShadowPlay) is an unofficial Windows tray app that watches completed `.mp4` recordings and serves original files over the LAN to a Flutter Android/iOS client. No NVIDIA installation is needed to build or test.

- `Kivo.sln`: three production C# projects and one test project.
- `src/Kivo.Core/`: UI-free monitoring, clip catalog, pairing, devices, settings, and HTTPS identity.
- `src/Kivo.Api/`: ASP.NET Core/Kestrel library hosted by the desktop app; endpoints, DTOs, bearer authentication, and private-network middleware. Not a standalone executable.
- `src/Kivo.Windows/`: WPF UI, WinForms tray icon, Windows services. Startup is `App.xaml.cs`; `Bootstrap/AppHostFactory.cs` composes dependencies; `Services/AppController.cs` manages sharing.
- `tests/Kivo.Tests/`: xUnit unit tests and real Kestrel HTTP/HTTPS integration tests, including Windows internals.
- `flutter/lib/`: `main.dart` entry point, `app/` app composition, `core/` state/API/downloads/storage, `features/` screens, and `shared/` widgets. Tests live in `flutter/test/`.
- `flutter/android/`, `flutter/ios/`: native hosts and permissions; iOS has custom media export via the `shadowplay/media` method channel. `flutter/windows/` is a Flutter runner, not the C# desktop app; its icon is also used by WPF.
- `.github/workflows/`: CI, release, CodeQL, and signed iOS OTA workflows. `installer/` contains Inno Setup packaging; `docs/` contains the download site, OTA manifest, and UI review material. Root `ios/` contains an export-options file; the active iOS app is under `flutter/ios/`.

## Kivo rebrand & multi-agent handoff

This repository was rebranded from **ShadowPlay** to **Kivo**. Before changing identifiers, paths, certificates, package IDs, or platform channels, read [`docs/KIVO_REBRAND.md`](docs/KIVO_REBRAND.md). Several legacy `ShadowPlay` identifiers (such as `%LocalAppData%\ShadowPlay`, local X509 certificate subjects, Android `applicationId`, iOS bundle IDs, and `shadowplay/*` method channels) are intentionally preserved for data, TLS pinning, and upgrade compatibility.

## Desktop: build, run, test

Use Windows and the .NET 8 SDK (`global.json`: 8.0.421, `latestFeature` roll-forward). Core/API target `net8.0`; WPF and tests target `net8.0-windows`. Run from the repository root:

```powershell
dotnet restore Kivo.sln
dotnet build Kivo.sln -c Release
dotnet test Kivo.sln -c Release
dotnet format Kivo.sln --verify-no-changes
dotnet run --project src/Kivo.Windows -c Release
```

Choose a test recordings folder on first launch. Closing the window hides it; use tray **Exit** to terminate. For startup/monitoring changes, follow the publish-and-smoke fixture in `.github/workflows/ci.yml`: the published executable accepts `--smoke --folder <test-folder> --port 0 --smoke-seconds 12` and CI checks readiness and clip count. Use disposable clips.

## Mobile: build, run, test

Use Flutter 3.47.2 (CI version; bundled Dart must satisfy `pubspec.yaml`), Java 17, and Android SDK API 37 (`platforms;android-37.0` in CI). Run from `flutter/`:

```powershell
flutter pub get
flutter analyze
flutter test
dart format --output=none --set-exit-if-changed lib test
flutter run -d <device-id>
flutter build apk --debug
```

Use `flutter devices` to select an emulator or device. Pair a physical phone with the desktop on the same private LAN for end-to-end checks. iOS builds require macOS/Xcode: `flutter build ios --release --no-codesign` (CI also checks debug). Physical iOS installation requires signing; use the separate OTA workflow for signed distribution. Android release currently uses debug signing; do not treat it as store-ready.

## Contracts, configuration, conventions

- No shared C#/Dart source library: the contract is `/api/v1`, camelCase JSON DTOs, and versioned QR payloads. Coordinate `Endpoints/Dtos.cs` and Core `Networking/QrPayload.cs` with Dart `core/models.dart` and `core/shadowplay_api.dart`; preserve compatibility and test both sides of contract changes.
- Desktop hosts legacy HTTP plus optional certificate-pinned HTTPS. QR protocol v2 carries TLS details. Never silently downgrade or replace an established certificate pin. Preserve private-network restrictions, bearer authentication, single-use pairing, and token hashing.
- Desktop runtime settings/device data are in `%LocalAppData%\ShadowPlay\settings.json`; logs and thumbnail cache are alongside it. Mobile uses secure token storage, preferences, and app-private downloads/diagnostics. Treat tokens, QR codes, paths, and request data as sensitive; sanitize logs.
- Installer upgrades: `installer/Kivo.iss` preserves the AppId and `UsePreviousAppDir`. Automatic post-install cleanup requires the native machine registration, exact historical Program Files directory, verified uninstall inventory, no reparse-point ancestors, and the same `{app}` directory. Custom/ambiguous/different-directory installs are preserved. Never delete generic uninstaller files or use HKCU/portable executable markers to authorize cleanup. Verified common shortcuts are retargeted; previous Start Menu groups/legacy shortcut labels can remain. `SetupLogging=yes` records individual skips/failures, including silent installs. Do not add pre-install deletion or per-user default installs.
- Windows Firewall migration: `WindowsFirewallService` accepts only the exact legacy `ShadowPlay LAN API (Private)` name or canonical `ShadowPlay LAN API (Private) TCP <port>` names (1 through 65535), in discovery and script generation, classifies rules pointing at the pre-rebrand `ShadowPlay.exe` as legacy, and migrates them only through the user-approved elevated repair ("Allow private LAN access"). Rules must stay private-profile, TCP, exact-port, exact-program; `FirewallRuleScriptTests`/`FirewallRuleMigrationTests` guard the script shape.
- Keep Core UI-free and Windows integration in the Windows project. Follow existing C# four-space indentation, PascalCase members, camelCase locals, nullable references, and structured logging. `Directory.Build.props` sets shared analyzers/version; `.csproj` files declare NuGet dependencies. Tests use descriptive snake_case names.
- Dart follows `flutter_lints` (`analysis_options.yaml`) and `dart format`; dependencies are in `pubspec.yaml`/`pubspec.lock`. Native manifests, entitlements, and platform channels matter for permissions, storage, and media export.
- `ci.yml` checks main pushes/PRs/manual runs: Windows build/tests/publish/smoke, Flutter analyze/tests, Android debug, and unsigned iOS. `release.yml` publishes Windows/Android from main/version tags; `ios-ota.yml` handles signed OTA separately. Prefer source and workflow files over stale README claims.

## Hard rules and done when

- Implement only the requested scope. No unsolicited redesigns, broad refactors, dependency/SDK upgrades, repository-wide formatting, or release/signing changes.
- Never edit, move, delete, or re-encode source recordings. Publish only stable, readable files; expose opaque clip IDs rather than filesystem paths. Preserve streaming/range downloads and offline mobile playback.
- Never commit secrets, certificates/private keys, pairing tokens, local SDK configuration, or generated build outputs. Do not weaken authentication, TLS checks, network guards, or firewall profiles to make a test pass.
- Done means the requested behavior is implemented with focused regression coverage where appropriate, relevant checks above pass, and the diff contains only intended changes. For cross-client changes, verify both suites; for UI/native behavior, include a targeted device/manual check and screenshots where useful.
- Report exactly what was verified and any unavailable platform/toolchain checks. Documentation-only changes need content/diff review, not application builds. Do not claim unrun tests passed.

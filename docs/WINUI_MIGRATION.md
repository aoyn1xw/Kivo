# WinUI desktop migration

## Architecture assessment and plan (2026-11-10)

The inspected tree uses WPF (`Kivo.Windows`), WinForms NotifyIcon, a Generic Host,
and a UI-free AppController. Core contains settings, devices, pairing, TLS identity,
catalog and stable-file monitoring. Api is an embedded Kestrel library. Flutter
consumes the existing `/api/v1` contract and v2 QR payload. Those contracts remain
unchanged. Graphify's index is three days older than the working tree; findings
were checked against source. The clean baseline contains conflict markers in two
WPF files; retaining timer disposal and revocation confirmation is prerequisite
repair, not a UI feature change.

Implementation order:

1. Repair the baseline build and keep WPF as the release fallback.
2. Add an unpackaged x64 .NET 8 WinUI 3 host, compile it, and reuse the existing
   controller, firewall, logging, single-instance and preview services.
3. Implement a compact dark native shell with Overview, Recordings, Devices and
   Settings. Use the supplied colors, native controls, system icon glyphs and
   font fallbacks; no downloaded fonts or branding packages.
4. Add two-step source/folder onboarding, explicit confirmation, native picker,
   read-only validation and backward-compatible multi-root settings.
5. Integrate real server status, QR/manual offers, revocation, tray and shutdown.
6. Defer the first scan to a cancellable worker, scope reconciliation per root,
   coalesce visible UI refreshes and avoid enumerating clips for a count.
7. Build/test, publish locally, exercise UI/lifecycle and record measured results
   and unavailable checks before changing any release entry point.

## Deployment decision

Use unpackaged WinUI, WindowsPackageType=None, x64, with an app-local Windows App
SDK runtime. Inno Setup can continue copying a normal directory tree; no MSIX
identity, Store dependency or package activation migration is needed. Windows App
SDK self-contained deployment increases disk/download size and ships runtime
fixes with app updates. .NET self-contained remains a publish-time choice.
The legacy mutex/event and AppData path are shared with WPF, preventing duplicate
instances and preserving installer detection. Startup uses a per-user Run entry
only after explicit selection in Settings, with `--background`. Tray integration
uses the framework's WinForms NotifyIcon on the WinUI dispatcher; it is not a
built-in WinUI control. Closing hides the window while the host remains alive;
Exit awaits monitor/API shutdown.

The new host initially links the existing UI-free Windows source files rather
than copying their implementations or loading the WPF application. This is a
controlled transition seam; both frontends compile the same controller/security
code. WPF remains available until runtime and installation validation is complete.
CI/release and the default installer must not switch to an unverified entry point.

## Recording suggestions

Suggestions are candidates, never detected configuration. Only existing readable
directories are offered and the user must confirm a path. Windows Known Folders
handles redirected Videos. NVIDIA/AMD paths can be customized; Intel tools differ
by version, so no universal Intel-specific default is claimed. OBS output settings
are authoritative. Kivo only supports completed MP4 files and never changes capture
software or converts recordings.

References:
- [WinUI unpackaged deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)
- [Native picker ownership](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp)
- [NVIDIA overlay/gallery and save location](https://www.nvidia.com/en-my/geforce/news/nvidia-app-download-and-features/)
- [AMD configurable save folder](https://www.amd.com/en/resources/support-articles/faqs/DH-023.html)
- [Game Bar Captures under Videos](https://blogs.windows.com/windowsexperience/2016/10/24/windows-10-tip-get-started-with-the-game-bar-on-your-windows-10-pc/)
- [OBS recording path](https://obsproject.com/kb/standard-recording-output-guide)

## Verification record

The baseline build reproduced CS8300 in two tracked WPF files; both conflict
markers were resolved while retaining timer disposal and revocation confirmation.
The Windows test suite passed 180 tests, including settings, monitoring, pairing,
HTTP and HTTPS integration. A self-contained WinUI publish succeeded, and its
published window opened after adding the generated PRI resource to the payload.
Manual verification exercised onboarding, manual pairing, range downloads,
single-instance activation, and a 64 MiB transfer continuing after window close.
The sidebar was subsequently changed to dock the navigation pane; its build passed,
but the change has not yet been visually verified.

Final WinUI installer compilation encountered Inno Setup resource-update error
110. Native folder selection, tray menu interaction, physical phone compatibility,
and expanded accessibility and DPI checks remain unverified. The release workflow
continues to ship WPF pending the remaining checks and an explicit cutover.
No performance improvement or production readiness is claimed.

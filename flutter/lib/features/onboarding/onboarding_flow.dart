import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/app_state.dart';
import '../../core/app_permissions.dart';
import '../../core/models.dart';
import '../../core/pairing_preflight.dart';
import '../../core/shadowplay_api.dart';

enum _OnboardingStep { intro, permission, pairing, success }

enum _PairMode { qr, manual }

/// Signature for the outgoing PC health probe used by the pairing preflight.
/// Production code uses [ShadowPlayApi.health]; tests may inject a scripted
/// probe (the same pattern as AppState's transport factory seam).
typedef PairingHealthCheck = Future<void> Function({
  required String address,
  required int port,
  bool useTls,
  String? pinnedCertSha256,
});

class OnboardingFlow extends StatefulWidget {
  const OnboardingFlow({
    required this.state,
    required this.onFinished,
    this.showIntro = true,
    this.pairingHealthCheck,
    super.key,
  });

  final AppState state;
  final Future<void> Function() onFinished;
  final PairingHealthCheck? pairingHealthCheck;
  final bool showIntro;

  @override
  State<OnboardingFlow> createState() => _OnboardingFlowState();
}

class _OnboardingFlowState extends State<OnboardingFlow> {
  late _OnboardingStep _step;

  @override
  void initState() {
    super.initState();
    _step =
        widget.showIntro ? _OnboardingStep.intro : _OnboardingStep.permission;
  }

  void _back() {
    setState(() {
      _step = switch (_step) {
        _OnboardingStep.permission when widget.showIntro =>
          _OnboardingStep.intro,
        _OnboardingStep.pairing => _OnboardingStep.permission,
        _ => _step,
      };
    });
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar:
            _step == _OnboardingStep.intro || _step == _OnboardingStep.success
                ? null
                : AppBar(
                    leading: IconButton(
                      tooltip: 'Back',
                      onPressed: _back,
                      icon: const Icon(Icons.arrow_back),
                    ),
                    title: const Text('Pair your PC'),
                  ),
        body: SafeArea(
          child: switch (_step) {
            _OnboardingStep.intro => _IntroPage(
                onContinue: () =>
                    setState(() => _step = _OnboardingStep.permission),
              ),
            _OnboardingStep.permission => _PermissionPage(
                onContinue: () =>
                    setState(() => _step = _OnboardingStep.pairing),
              ),
            _OnboardingStep.pairing => _PairingPage(
                state: widget.state,
                healthCheck: widget.pairingHealthCheck,
                onPaired: () => setState(() => _step = _OnboardingStep.success),
              ),
            _OnboardingStep.success => _SuccessPage(
                computerName: widget.state.active?.computerName ?? 'Your PC',
                onContinue: widget.onFinished,
              ),
          },
        ),
      );
}

class _IntroPage extends StatelessWidget {
  const _IntroPage({required this.onContinue});
  final VoidCallback onContinue;

  @override
  Widget build(BuildContext context) => _SetupPage(
        icon: Icons.sports_esports_outlined,
        title: 'Your recordings. On your phone.',
        message:
            'Connect to Kivo on your PC to browse gameplay recordings, download originals and watch them offline.',
        action:
            FilledButton(onPressed: onContinue, child: const Text('Continue')),
        children: const [
          ListTile(
              contentPadding: EdgeInsets.zero,
              leading: Icon(Icons.wifi),
              title: Text('Start on the same Wi-Fi'),
              subtitle: Text('Keep Kivo running on your PC.')),
          ListTile(
              contentPadding: EdgeInsets.zero,
              leading: Icon(Icons.qr_code_scanner),
              title: Text('Pair once with a QR code'),
              subtitle: Text('A manual pairing code works too.')),
        ],
      );
}

class _PermissionPage extends StatelessWidget {
  const _PermissionPage({required this.onContinue});
  final VoidCallback onContinue;

  @override
  Widget build(BuildContext context) => _SetupPage(
        icon: Icons.phonelink_outlined,
        title: 'Before you pair',
        message:
            'Kivo needs access to your PC and, if you scan a code, your camera.',
        action:
            FilledButton(onPressed: onContinue, child: const Text('Continue')),
        children: const [
          ListTile(
              contentPadding: EdgeInsets.zero,
              leading: Icon(Icons.wifi),
              title: Text('Local network'),
              subtitle: Text('Find and connect to your PC on the same Wi-Fi.')),
          ListTile(
              contentPadding: EdgeInsets.zero,
              leading: Icon(Icons.camera_alt_outlined),
              title: Text('Camera'),
              subtitle: Text(
                  'Only used to scan the one-time QR code. Manual pairing does not need it.')),
        ],
      );
}

class _SetupPage extends StatelessWidget {
  const _SetupPage(
      {required this.icon,
      required this.title,
      required this.message,
      required this.action,
      this.children = const []});
  final IconData icon;
  final String title;
  final String message;
  final Widget action;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => ListView(
        padding: const EdgeInsets.all(24),
        children: [
          const SizedBox(height: 24),
          Align(
              alignment: Alignment.centerLeft,
              child: Icon(icon,
                  size: 36, color: Theme.of(context).colorScheme.primary)),
          const SizedBox(height: 24),
          Text(title,
              style: Theme.of(context)
                  .textTheme
                  .titleLarge
                  ?.copyWith(fontWeight: FontWeight.w600)),
          const SizedBox(height: 12),
          Text(message,
              style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                  height: 1.45)),
          const SizedBox(height: 24),
          ...children,
          const SizedBox(height: 24),
          action,
        ],
      );
}

class _PairingPage extends StatefulWidget {
  const _PairingPage({
    required this.state,
    required this.healthCheck,
    required this.onPaired,
  });
  final AppState state;
  final PairingHealthCheck? healthCheck;
  final VoidCallback onPaired;

  @override
  State<_PairingPage> createState() => _PairingPageState();
}

class _PairingPageState extends State<_PairingPage>
    with WidgetsBindingObserver {
  final _address = TextEditingController();
  final _port = TextEditingController(text: '5177');
  final _code = TextEditingController();
  final _deviceName = TextEditingController(text: 'My phone');
  late final MobileScannerController _scannerController;

  _PairMode _mode = _PairMode.qr;
  PairingPayload? _payload;
  bool _scanning = false;
  bool _busy = false;
  bool _preflightBusy = false;
  bool _pinRejected = false;

  /// The exact endpoint and trust parameters that a completed preflight
  /// validated. Pairing is only offered while the current fields still match,
  /// so edits always require a fresh check.
  ({String address, int port, String? pin})? _validatedTarget;
  int _preflightGeneration = 0;
  int _busyGeneration = 0;
  AppPermissionState _permissionState = AppPermissionState.unknown;
  String? _error;
  final _localNetworkPermissions = const LocalNetworkPermissionService();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _address.addListener(_connectionFieldsEdited);
    _port.addListener(_connectionFieldsEdited);
    _scannerController = MobileScannerController(
      formats: const [BarcodeFormat.qrCode],
      detectionSpeed: DetectionSpeed.noDuplicates,
    );
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _address.removeListener(_connectionFieldsEdited);
    _port.removeListener(_connectionFieldsEdited);
    _address.dispose();
    _port.dispose();
    _code.dispose();
    _deviceName.dispose();
    _scannerController.dispose();
    super.dispose();
  }

  void _connectionFieldsEdited() {
    if (_preflightGeneration == 0 && _validatedTarget == null) return;
    setState(() {
      _validatedTarget = null;
      _preflightGeneration++;
      // A superseded check must not keep the pending indicator alive.
      _preflightBusy = false;
    });
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed &&
        _payload != null &&
        !_validated &&
        !_pinRejected &&
        !_preflightBusy &&
        !_busy) {
      unawaited(_runPreflight());
    }
  }

  /// The address, port and trust parameters used for the next preflight or
  /// pairing exchange, or null when they are incomplete.
  ({String address, int port, String? pin})? get _currentTarget {
    final port = int.tryParse(_port.text);
    final address = _address.text.trim();
    if (address.isEmpty || port == null || port < 1 || port > 65535) {
      return null;
    }
    final pin = _payload?.tlsPinned == true
        ? normalizeFingerprint(_payload!.certSha256)
        : null;
    return (address: address, port: port, pin: pin);
  }

  /// True while a completed preflight still matches the current fields.
  bool get _validated =>
      !_preflightBusy &&
      _validatedTarget != null &&
      _validatedTarget == _currentTarget;

  void _handleCapture(BarcodeCapture capture) {
    for (final barcode in capture.barcodes) {
      final value = barcode.rawValue;
      if (value == null) continue;
      try {
        final payload = PairingPayload.fromJson(
          jsonDecode(value) as Map<String, dynamic>,
        );
        _scannerController.stop();
        setState(() {
          _payload = payload;
          _pinRejected = false;
          _scanning = false;
          _validatedTarget = null;
          _permissionState = AppPermissionState.unknown;
          _address.text = payload.address;
          _port.text = payload.port.toString();
          _code.text = payload.code;
          _error = null;
        });
        unawaited(_runPreflight());
      } catch (_) {
        widget.state.diagnostics
            .warning('pairing', 'Scanned payload is not a valid pairing QR');
        setState(() => _error = 'That is not a valid Kivo pairing QR code.');
      }
      return;
    }
  }

  Future<void> _pair() async {
    if (_code.text.trim().isEmpty) {
      setState(() => _error = 'Enter the PC address, port, and pairing code.');
      return;
    }
    final target = _currentTarget;
    if (target == null || _validatedTarget != target) {
      setState(() => _error =
          'The PC address or port changed. Run the connection check again before pairing.');
      return;
    }
    final port = target.port;
    final pin = target.pin;
    final tlsPort = pin != null ? _payload!.httpsPort : null;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final response = await ShadowPlayApi.pair(
        address: _address.text.trim(),
        port: tlsPort ?? port,
        code: _code.text.replaceAll(' ', '-').toUpperCase(),
        deviceName:
            _deviceName.text.trim().isEmpty ? 'Phone' : _deviceName.text.trim(),
        useTls: pin != null,
        pinnedCertSha256: pin,
      );
      await widget.state.addPaired(
        response,
        _address.text.trim(),
        port,
        httpsPort: tlsPort,
        certSha256: pin,
      );
      if (mounted) widget.onPaired();
    } catch (error) {
      widget.state.diagnostics.warning('pairing', 'Pairing rejected', {
        'address': _address.text.trim(),
        'error': error.toString(),
      });
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => ListView(
        padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
        children: [
          Text(
            'Choose how you want to connect your PC.',
            textAlign: TextAlign.center,
            style: TextStyle(
                color: Theme.of(context).colorScheme.onSurfaceVariant),
          ),
          const SizedBox(height: 20),
          SegmentedButton<_PairMode>(
            segments: const [
              ButtonSegment(
                  value: _PairMode.qr,
                  icon: Icon(Icons.qr_code_scanner),
                  label: Text('Scan QR Code')),
              ButtonSegment(
                  value: _PairMode.manual,
                  icon: Icon(Icons.keyboard),
                  label: Text('Manual Mode')),
            ],
            selected: {_mode},
            showSelectedIcon: false,
            onSelectionChanged: (value) {
              _scannerController.stop();
              setState(() {
                _mode = value.first;
                _scanning = false;
                _validatedTarget = null;
                _preflightGeneration++;
                _preflightBusy = false;
                _permissionState = AppPermissionState.unknown;
                _error = null;
              });
            },
          ),
          const SizedBox(height: 20),
          if (_mode == _PairMode.qr) _qrContent() else _manualContent(),
          if (_error != null) ...[
            const SizedBox(height: 16),
            Material(
              color: Theme.of(context).colorScheme.errorContainer,
              borderRadius: BorderRadius.circular(12),
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(Icons.error_outline,
                        color: Theme.of(context).colorScheme.onErrorContainer),
                    const SizedBox(width: 10),
                    Expanded(child: Text(_error!)),
                    if (shouldShowLocalNetworkSettingsAction(_permissionState))
                      TextButton(
                        onPressed: _openSettings,
                        child: const Text('Open Settings'),
                      ),
                  ],
                ),
              ),
            ),
          ],
          const SizedBox(height: 20),
          if (_payload != null || _mode == _PairMode.manual) _preflightAction(),
          if ((_payload != null || _mode == _PairMode.manual) && _validated)
            FilledButton(
              onPressed: _busy ? null : _pair,
              child: Text(_busy ? 'Pairing…' : 'Pair Device'),
            ),
        ],
      );

  Widget _qrContent() {
    if (_payload != null) {
      return Container(
        padding: const EdgeInsets.all(20),
        decoration: BoxDecoration(
          color: Theme.of(context).colorScheme.surfaceContainerLow,
          borderRadius: BorderRadius.circular(8),
        ),
        child: Column(
          children: [
            Icon(Icons.desktop_windows,
                size: 42, color: Theme.of(context).colorScheme.primary),
            const SizedBox(height: 12),
            Text(_payload!.computerName,
                style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 4),
            Text('${_payload!.address}:${_payload!.port}'),
            const SizedBox(height: 12),
            TextButton.icon(
              onPressed: () => setState(() {
                _payload = null;
                _validatedTarget = null;
                _preflightGeneration++;
                _preflightBusy = false;
                _permissionState = AppPermissionState.unknown;
                _error = null;
              }),
              icon: const Icon(Icons.qr_code_scanner),
              label: const Text('Scan again'),
            ),
          ],
        ),
      );
    }
    if (_scanning) {
      return Column(
        children: [
          ClipRRect(
            borderRadius: BorderRadius.circular(8),
            child: SizedBox(
              height: 300,
              child: MobileScanner(
                controller: _scannerController,
                onDetect: _handleCapture,
                errorBuilder: (context, error) => _ScannerError(
                  permissionDenied: error.errorCode ==
                      MobileScannerErrorCode.permissionDenied,
                  onUseManual: () => setState(() {
                    _mode = _PairMode.manual;
                    _scanning = false;
                  }),
                ),
              ),
            ),
          ),
          const SizedBox(height: 12),
          const Text('Point your camera at the QR code shown on your PC.'),
        ],
      );
    }
    return Column(
      children: [
        const SizedBox(height: 20),
        Icon(Icons.qr_code_2,
            size: 64, color: Theme.of(context).colorScheme.primary),
        const SizedBox(height: 20),
        const Text('Scan the one-time QR code shown in Kivo on your PC.',
            textAlign: TextAlign.center),
        const SizedBox(height: 24),
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: _preflightBusy ? null : _openCamera,
            icon: const Icon(Icons.camera_alt_outlined),
            label: Text(
              _preflightBusy ? 'Requesting permissions…' : 'Open Camera',
            ),
          ),
        ),
      ],
    );
  }

  Widget _manualContent() => Column(
        children: [
          TextField(
            controller: _address,
            autocorrect: false,
            keyboardType: TextInputType.url,
            decoration: const InputDecoration(
                labelText: 'PC address', hintText: '192.168.1.20'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _port,
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(labelText: 'Port'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _code,
            textCapitalization: TextCapitalization.characters,
            autocorrect: false,
            decoration: const InputDecoration(
                labelText: 'Pairing code', hintText: 'XXXX-XXXX'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _deviceName,
            decoration: const InputDecoration(labelText: 'Device name'),
          ),
          const SizedBox(height: 8),
          Text(
            'Manual pairing connects to the address above over your Wi-Fi '
            'network without certificate verification. Scanning the QR code '
            'on your PC instead lets the phone verify the PC and use an '
            'encrypted connection whenever the PC supports one.',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: Theme.of(context).colorScheme.onSurfaceVariant),
          ),
        ],
      );

  Widget _preflightAction() {
    final label = _preflightBusy
        ? _permissionState == AppPermissionState.requestingPermission
            ? 'Requesting Local Network access…'
            : 'Checking PC connection…'
        : _validated
            ? 'PC reachable on this Wi‑Fi'
            : _mode == _PairMode.qr
                ? 'Check PC connection again'
                : 'Check PC connection';
    return Column(
      children: [
        OutlinedButton.icon(
          onPressed: _preflightBusy ? null : _runPreflight,
          icon: Icon(_validated ? Icons.check : Icons.wifi_find),
          label: Text(label),
        ),
        if (!_validated)
          const Padding(
            padding: EdgeInsets.only(top: 6),
            child: Text(
              'We check the PC before using the one-time pairing code.',
              textAlign: TextAlign.center,
            ),
          ),
      ],
    );
  }

  Future<void> _runPreflight() async {
    if (_preflightBusy || _busy) return;
    final target = _currentTarget;
    if (target == null) {
      if (mounted) {
        setState(() => _error = 'Enter the PC address and port first.');
      }
      return;
    }
    final generation = ++_preflightGeneration;

    setState(() {
      _preflightBusy = true;
      _busyGeneration = generation;
      _validatedTarget = null;
      _permissionState = AppPermissionState.requestingPermission;
      _error = null;
    });
    final pin = target.pin;
    final tlsPort =
        pin != null && _payload?.httpsPort != null ? _payload!.httpsPort : null;
    try {
      final probe = widget.healthCheck;
      final permission = await runPairingPreflight(
        requestPermission: _localNetworkPermissions.requestLocalNetworkAccess,
        isActive: () => mounted && generation == _preflightGeneration,
        checkConnection: () async {
          if (probe != null) {
            return probe(
              address: target.address,
              port: tlsPort ?? target.port,
              useTls: pin != null,
              pinnedCertSha256: pin,
            );
          }
          await ShadowPlayApi.health(
            address: target.address,
            port: tlsPort ?? target.port,
            useTls: pin != null,
            pinnedCertSha256: pin,
          );
        },
      );
      if (generation != _preflightGeneration || !mounted) return;
      if (permission != AppPermissionState.granted) {
        widget.state.diagnostics.warning('pairing',
            'Local Network permission not granted before preflight', {
          'state': permission.name,
        });
        if (mounted) {
          setState(() {
            _permissionState = permission;
            _error = permission == AppPermissionState.denied
                ? 'Local Network access is required. Allow it in iPhone Settings > Kivo, then try again.'
                : 'Local Network access is not available yet. Connect to Wi‑Fi and try again.';
          });
        }
        return;
      }
      if (mounted) {
        setState(() => _permissionState = AppPermissionState.granted);
      }
      widget.state.diagnostics.info('pairing', 'PC reachable for pairing', {
        'address': target.address,
        'port': tlsPort ?? target.port,
        'transport': pin != null ? 'HTTPS with pinned certificate' : 'HTTP',
      });
      if (mounted) {
        setState(() {
          if (generation == _preflightGeneration) {
            _validatedTarget = target;
          }
        });
      }
    } catch (error) {
      if (generation != _preflightGeneration || !mounted) return;
      widget.state.diagnostics.warning('pairing', 'Pairing preflight failed',
          {'address': target.address, 'error': error.toString()});
      _pinRejected =
          error is ApiException && error.kind == ApiFailureKind.tlsUntrusted;
      if (mounted) setState(() => _error = error.toString());
    } finally {
      // Only the run that owns the pending indicator clears it; a superseded
      // run was already cleared by the edit that superseded it.
      if (mounted && _busyGeneration == generation) {
        setState(() => _preflightBusy = false);
      }
    }
  }

  Future<void> _openCamera() async {
    setState(() {
      _preflightBusy = true;
      _permissionState = AppPermissionState.requestingPermission;
      _error = null;
    });
    try {
      final permission = await runPairingPreflight(
        requestPermission: _localNetworkPermissions.requestLocalNetworkAccess,
        checkConnection: () async {},
        isActive: () => mounted,
      );
      if (permission != AppPermissionState.granted) {
        if (mounted) {
          setState(() {
            _permissionState = permission;
            _error = permission == AppPermissionState.denied
                ? 'Local Network access is required before scanning. Allow it in iPhone Settings > Kivo, then try again.'
                : 'Local Network access is not available yet. Connect to Wi‑Fi and try again.';
          });
        }
        return;
      }
      if (mounted) {
        setState(() {
          _permissionState = AppPermissionState.granted;
          _scanning = true;
        });
      }
    } finally {
      if (mounted) setState(() => _preflightBusy = false);
    }
  }

  Future<void> _openSettings() => _localNetworkPermissions.openSettings();
}

class _ScannerError extends StatelessWidget {
  const _ScannerError(
      {required this.permissionDenied, required this.onUseManual});
  final bool permissionDenied;
  final VoidCallback onUseManual;

  @override
  Widget build(BuildContext context) => ColoredBox(
        color: Theme.of(context).colorScheme.surfaceContainerHigh,
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(Icons.no_photography_outlined, size: 42),
                const SizedBox(height: 12),
                Text(permissionDenied
                    ? 'Camera permission denied'
                    : 'Camera unavailable'),
                const SizedBox(height: 12),
                OutlinedButton(
                    onPressed: onUseManual,
                    child: const Text('Use Manual Mode')),
              ],
            ),
          ),
        ),
      );
}

class _SuccessPage extends StatelessWidget {
  const _SuccessPage({required this.computerName, required this.onContinue});
  final String computerName;
  final Future<void> Function() onContinue;

  @override
  Widget build(BuildContext context) => _SetupPage(
        icon: Icons.check_circle_outline,
        title: 'PC paired',
        message:
            'Your phone is connected to $computerName. Open Clips to choose recordings to download.',
        action: FilledButton(
            onPressed: onContinue, child: const Text('Continue to App')),
      );
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kivo.Core.Models;
using Kivo.Core.Networking;
using Kivo.Windows.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

namespace Kivo.Windows.ViewModels;

public sealed record ClipRow(string Id, string FileName, string SizeDisplay, string ModifiedDisplay);

public sealed record DeviceRow(string DeviceId, string Name, string PairedDisplay);

/// <summary>Main window state: status, address, QR, clips and paired devices.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppController _controller;
    private readonly Action _openSettings;
    private readonly Dispatcher? _dispatcher;
    private ImageSource? _qrImage;
    private string _statusText = "Starting...";
    private string? _statusDetail;
    private Brush _statusBrush = Brushes.Gray;
    private string _addressText = "-";
    private bool _isRunning;
    private string _firewallStatusText = "Firewall status has not been checked yet.";
    private bool _firewallNeedsSetup;
    private string _pairingHint = "";
    private readonly DispatcherTimer _pairingTimer;
    private readonly Action _stateChangedHandler;
    private readonly TimeProvider _timeProvider;
    private PairingDisplaySnapshot? _displayedPairing;
    private DateTimeOffset? _lastPairingExpiresAt;
    private bool _disposed;
    private string _pairingAddress = "";
    private string _pairingPort = "";
    private string _pairingCode = "";
    private bool _hasPairingDetails;

    /// <summary>
    /// Shows a confirmation for revoking the named device; returns true when the
    /// user confirms. The desktop view supplies a dialog; a null callback keeps
    /// the legacy direct-revoke behavior for composition sites without a dialog.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDeviceRevocation { get; set; }

    public MainViewModel(AppController controller, Action openSettings)
        : this(controller, openSettings, Application.Current.Dispatcher, TimeProvider.System)
    {
    }

    internal MainViewModel(AppController controller, Action openSettings, Dispatcher dispatcher, TimeProvider timeProvider)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
<<<<<<< Updated upstream
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _stateChangedHandler = () =>
        {
            if (!_disposed && !_dispatcher.HasShutdownStarted)
            {
                _dispatcher.BeginInvoke(() => { if (!_disposed) RefreshAll(); });
            }
        };
        _controller.StateChanged += _stateChangedHandler;
        _pairingTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _pairingTimer.Tick += OnPairingTick;
        _pairingTimer.Start();
=======
        _dispatcher = Application.Current?.Dispatcher;

        _controller.StateChanged += () => _dispatcher?.BeginInvoke(RefreshAll);
>>>>>>> Stashed changes

        RefreshAll();
    }

    public ObservableCollection<ClipRow> Clips { get; } = [];

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string? StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }

    public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

    public string AddressText { get => _addressText; private set => SetProperty(ref _addressText, value); }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    private string _pauseResumeLabel = "Pause sharing";

    public string PauseResumeLabel
    {
        get => _pauseResumeLabel;
        private set => SetProperty(ref _pauseResumeLabel, value);
    }

    public ImageSource? QrImage { get => _qrImage; private set => SetProperty(ref _qrImage, value); }

    public string PairingAddress { get => _pairingAddress; private set => SetProperty(ref _pairingAddress, value); }
    public string PairingPort { get => _pairingPort; private set => SetProperty(ref _pairingPort, value); }
    public string PairingCode { get => _pairingCode; private set => SetProperty(ref _pairingCode, value); }
    public bool HasPairingDetails { get => _hasPairingDetails; private set => SetProperty(ref _hasPairingDetails, value); }

    public string PairingHint { get => _pairingHint; private set => SetProperty(ref _pairingHint, value); }

    public ICommand RegeneratePairingCommand => new RelayCommand(
        () =>
        {
            if (_controller.RegeneratePairingOffer()) RefreshPairing();
        },
        () => _controller.Status.State == SharingState.Running);

    public ICommand ToggleSharingCommand => new RelayCommand(async () => await _controller.ToggleSharingAsync());

    public ICommand ConfigureFirewallCommand => new RelayCommand(async () =>
    {
        await _controller.ConfigureFirewallAsync();
        RefreshAll();
    });

    public ICommand ChangeFolderCommand => new RelayCommand(_openSettings);

    public ICommand OpenFolderCommand => new RelayCommand(() => _controller.OpenRecordingsFolderInExplorer());

    public ICommand RevokeDeviceCommand => new RelayCommand<string>(deviceId =>
        _ = RevokeDeviceAsync(deviceId));

    /// <summary>
    /// Revokes the selected device only after the user confirms. Cancellation
    /// must leave the device paired, and the command refreshes the device list
    /// whenever a revocation succeeds.
    /// </summary>
    public async Task RevokeDeviceAsync(string? deviceId)
    {
        if (deviceId is null)
        {
            return;
        }

        var confirm = ConfirmDeviceRevocation;
        if (confirm is not null)
        {
            var deviceName = _controller.PairedDevices
                .FirstOrDefault(device => device.DeviceId == deviceId)?.Name
                ?? "this device";
            if (!await confirm(deviceName))
            {
                return;
            }
        }

        _controller.RevokeDevice(deviceId);
        RefreshDevices();
    }

    /// <summary>Refreshes everything from the controller. Must run on the UI thread.</summary>
    public void RefreshAll()
    {
        var status = _controller.Status;
        (StatusText, StatusBrush) = status.State switch
        {
            SharingState.Running => ($"Sharing on port {status.Port}", Brushes.Green),
            SharingState.Starting => ("Starting sharing...", Brushes.DarkOrange),
            SharingState.Paused => ("Sharing paused", Brushes.DarkOrange),
            SharingState.NotConfigured => ("Choose a recordings folder to begin", Brushes.Gray),
            SharingState.Error => ("Problem detected", Brushes.Red),
            _ => ("Unknown", Brushes.Gray),
        };

        StatusDetail = status.Detail ?? _controller.RecordingsFolder;
        IsRunning = status.State == SharingState.Running;
        PauseResumeLabel = IsRunning ? "Pause sharing" : "Start sharing";

        AddressText = IsRunning
            ? $"{LocalIpLocator.FindBestIpv4()?.ToString() ?? "localhost"}:{status.Port}"
            : "-";

        FirewallStatusText = _controller.FirewallStatus.Detail;
        FirewallNeedsSetup = IsRunning && _controller.FirewallStatus.NeedsSetup;

        RefreshPairing();
        CommandManager.InvalidateRequerySuggested();

        RefreshClips();
        RefreshDevices();

        OnPropertyChanged(nameof(RecordingsFolder));
    }

    public string? RecordingsFolder => _controller.RecordingsFolder;

    public string FirewallStatusText
    {
        get => _firewallStatusText;
        private set => SetProperty(ref _firewallStatusText, value);
    }

    public bool FirewallNeedsSetup
    {
        get => _firewallNeedsSetup;
        private set => SetProperty(ref _firewallNeedsSetup, value);
    }

    private void OnPairingTick(object? sender, EventArgs args) => RefreshPairing();

    internal void RefreshPairing()
    {
        var snapshot = _controller.PairingSnapshot;
        if (snapshot is null)
        {
            var previouslyDisplayed = _displayedPairing is not null;
            _displayedPairing = null;
            QrImage = null;
            PairingAddress = PairingPort = PairingCode = "";
            HasPairingDetails = false;
            if (_controller.Status.State != SharingState.Running)
            {
                _lastPairingExpiresAt = null;
                PairingHint = "Start sharing to generate a pairing code.";
            }
            else
            {
                PairingHint = _lastPairingExpiresAt is not null && _timeProvider.GetUtcNow() >= _lastPairingExpiresAt
                    ? "Pairing code expired. Generate a new code."
                    : "Pairing code no longer available. Generate a new code.";
            }
            if (previouslyDisplayed) RefreshDevices();
            return;
        }

        if (snapshot != _displayedPairing)
        {
            _displayedPairing = snapshot;
            _lastPairingExpiresAt = snapshot.ExpiresAtUtc;
            PairingAddress = snapshot.Payload.LanAddress;
            PairingPort = snapshot.Payload.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PairingCode = snapshot.Payload.PairingCode;
            HasPairingDetails = true;
            QrImage = Services.QrCodeService.CreatePng(snapshot.Payload.ToJson());
        }
        var remaining = snapshot.ExpiresAtUtc - _timeProvider.GetUtcNow();
        PairingHint = $"Pairing code expires in {Math.Max(1, Math.Ceiling(remaining.TotalMinutes))} min. Scan with the phone app.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.StateChanged -= _stateChangedHandler;
        _pairingTimer.Stop();
        _pairingTimer.Tick -= OnPairingTick;
        GC.SuppressFinalize(this);
    }

    private void RefreshClips()
    {
        Clips.Clear();
        foreach (var clip in _controller.CatalogSnapshot())
        {
            Clips.Add(new ClipRow(
                clip.Id,
                clip.FileName,
                $"{clip.SizeBytes / (1024d * 1024d):F1} MB",
                clip.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var device in _controller.PairedDevices)
        {
            Devices.Add(new DeviceRow(
                device.DeviceId,
                device.Name,
                $"paired {device.CreatedAtUtc.ToLocalTime():yyyy-MM-dd}"));
        }
    }
}

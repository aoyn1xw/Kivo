using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kivo.Api;
using Kivo.Core.Clips;
using Kivo.Core.Devices;
using Kivo.Core.Models;
using Kivo.Core.Networking;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Kivo.Core.Validation;

namespace Kivo.Windows.Services;

public enum SharingState
{
    NotConfigured,
    Paused,
    Starting,
    Running,
    Error,
}

public sealed record ControllerStatus(SharingState State, string? Detail = null, int Port = 0)
{
    public static readonly ControllerStatus NotConfigured = new(SharingState.NotConfigured);

    public static readonly ControllerStatus Paused = new(SharingState.Paused);
}

public sealed record PairingDisplaySnapshot(QrPayload Payload, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// UI-free coordinator. Owns the LAN API host and clip monitor lifetimes, pairing offers
/// and status transitions. WPF layers subscribe to <see cref="StateChanged"/> and marshal to the UI thread.
/// </summary>
public sealed class AppController : IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly IFolderValidator _validator;
    private readonly ClipCatalog _catalog;
    private readonly IPairingService _pairing;
    private readonly IDeviceRegistry _devices;
    private readonly IWindowsFirewallService _firewall;
    private readonly IClipPreviewProvider _clipPreview;
    private readonly IHttpsIdentityService? _httpsIdentity;
    private readonly ILogger<AppController>? _logger;

    private const string SettingsSaveFailedDetail =
        "Settings could not be saved. Check that your Windows profile has free space and write permission.";

    private const string NothingWatchedDetail =
        "No recording folders are being watched. Check that each folder exists and is readable.";

    private WebApplication? _apiApp;
    private readonly List<WatchedRoot> _monitors = [];
    private readonly ConcurrentDictionary<string, string> _directoryErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly MonitorOptions _monitorOptions;
    private bool _disposed;
    private bool _sharingWatchFault;
    private int _actualPort;
    private volatile PairingDisplaySnapshot? _pairingSnapshot;

    /// <summary>Live pinned-TLS listener port; 0 when HTTPS is not active.</summary>
    private int _actualHttpsPort;

    /// <summary>SHA-256 thumbprint of the live TLS certificate; null when HTTPS is not active.</summary>
    private string? _httpsThumbprint;

    public AppController(
        ISettingsService settings,
        IFolderValidator validator,
        ClipCatalog catalog,
        IPairingService pairing,
        IDeviceRegistry devices,
        IWindowsFirewallService firewall,
        IClipPreviewProvider clipPreview,
        IHttpsIdentityService? httpsIdentity = null,
        ILogger<AppController>? logger = null,
        MonitorOptions? monitorOptions = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _clipPreview = clipPreview ?? throw new ArgumentNullException(nameof(clipPreview));
        _httpsIdentity = httpsIdentity;
        _logger = logger;
        _monitorOptions = monitorOptions ?? new MonitorOptions();

        _catalog.Changed += NotifyStateChanged;
    }

    public event Action? StateChanged;

    public ControllerStatus Status { get; private set; } = ControllerStatus.NotConfigured;

    public FirewallStatus FirewallStatus { get; private set; } = new(
        FirewallRuleState.Unavailable,
        "Firewall status has not been checked yet.",
        false);

    public bool IsConfigured => RecordingDirectories.Count > 0;

    public IReadOnlyList<RecordingDirectory> RecordingDirectories => RecordingConfiguration.GetDirectories(_settings.Current);

    public IReadOnlyDictionary<string, string> DirectoryErrors => new Dictionary<string, string>(_directoryErrors);

    public int RecordingCount => _catalog.Count;

    public string? ConfigurationError { get; private set; }

    public string? RecordingsFolder => _settings.Current.RecordingsFolder;

    /// <summary>The persisted port (may differ from the live one while paused).</summary>
    public int ConfiguredPort => _settings.Current.Port;

    /// <summary>The persisted sharing preference.</summary>
    public bool SharingEnabledSetting => _settings.Current.SharingEnabled;

    public bool IsSharingRunning => Status.State == SharingState.Running || Status.State == SharingState.Starting;

    /// <summary>
    /// Applies folder + port + sharing preference from the settings dialog and
    /// restarts sharing so they take effect immediately. Returns false on failure
    /// (validation, settings save, or bind error); <see cref="Status.Detail"/> carries a friendly message.
    /// A failed save leaves the previous snapshot and sharing state in place.
    /// </summary>
    public async Task<bool> ApplySettingsAsync(string? folder, int port, bool sharingEnabled)
    {
        var validation = _validator.Validate(folder);
        if (!validation.IsValid)
        {
            SetStatus(new ControllerStatus(SharingState.Error, validation.Error));
            return false;
        }

        if (port is < 1 or > 65535)
        {
            SetStatus(new ControllerStatus(SharingState.Error, "Port must be between 1 and 65535."));
            return false;
        }

        var saved = _settings.TryUpdate(data =>
        {
            var directories = RecordingConfiguration.GetDirectories(data).ToList();
            var source = directories.FirstOrDefault()?.Source ?? "custom";
            if (directories.Count > 0) directories.RemoveAt(0);
            directories.Insert(0, new RecordingDirectory { Path = Path.GetFullPath(folder!), Source = source });
            RecordingConfiguration.Replace(data, directories);
            data.Port = port;
            data.SharingEnabled = sharingEnabled;
        });
        if (!saved)
        {
            return ReportSettingsSaveFailure();
        }

        ConfigurationError = null;
        await RestartSharingAsync().ConfigureAwait(false);

        if (Status.State == SharingState.Error)
        {
            return false;
        }

        StateChanged?.Invoke();
        return true;
    }

    public async Task InitializeAsync()
    {
        var data = _settings.Current;
        if (RecordingConfiguration.GetDirectories(data).Count == 0)
        {
            SetStatus(ControllerStatus.NotConfigured);
            return;
        }

        if (!data.SharingEnabled)
        {
            SetStatus(ControllerStatus.Paused);
            return;
        }

        await StartSharingAsync().ConfigureAwait(false);
    }

    /// <summary>Validates a folder choice; saves it and restarts sharing when valid.</summary>
    public async Task<bool> SelectFolderAsync(string? path)
    {
        var result = _validator.Validate(path);
        if (!result.IsValid)
        {
            SetStatus(new ControllerStatus(SharingState.Error, result.Error));
            return false;
        }

        return await ApplyRecordingDirectoriesAsync(
            [new RecordingDirectory { Path = Path.GetFullPath(path!) }]).ConfigureAwait(false);
    }

    public async Task<bool> ApplyRecordingDirectoriesAsync(IEnumerable<RecordingDirectory> directories, bool completeOnboarding = false)
    {
        ConfigurationError = null;
        IReadOnlyList<RecordingDirectory> normalized;
        try
        {
            normalized = RecordingConfiguration.Normalize(directories);
        }
        catch (ArgumentException ex)
        {
            ConfigurationError = ex.Message;
            return false;
        }

        foreach (var directory in normalized)
        {
            var validation = _validator.Validate(directory.Path);
            if (!validation.IsValid)
            {
                ConfigurationError = validation.Error;
                return false;
            }
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                ConfigurationError = "Kivo is shutting down. Reopen it before changing recording folders.";
                return false;
            }

            if (!_settings.TryUpdate(data => RecordingConfiguration.Replace(data, normalized, completeOnboarding)))
            {
                ConfigurationError = SettingsSaveFailedDetail;
                return false;
            }

            await StopCoreAsync().ConfigureAwait(false);
            _catalog.ReplaceAll([]);
            if (normalized.Count == 0) SetStatus(ControllerStatus.NotConfigured);
            else if (_settings.Current.SharingEnabled) await StartSharingCoreAsync().ConfigureAwait(false);
            else SetStatus(ControllerStatus.Paused);
            ConfigurationError = Status.State == SharingState.Error ? Status.Detail : null;
            return Status.State != SharingState.Error;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task StartSharingAsync() => RunExclusiveAsync(() =>
        Status.State == SharingState.Running && _apiApp is not null ? Task.CompletedTask : StartSharingCoreAsync());

    private async Task RunExclusiveAsync(Func<Task> operation)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed) await operation().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<bool> RunExclusiveAsync(Func<Task<bool>> operation)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return false;
            }

            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StartSharingCoreAsync()
    {
        var directories = RecordingDirectories;
        if (directories.Count == 0)
        {
            SetStatus(ControllerStatus.NotConfigured);
            return;
        }

        SetStatus(new ControllerStatus(SharingState.Starting));

        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var data = _settings.Current;
            var serverInfo = new StaticServerInfo(data.ServerId, Environment.MachineName);

            // Pinned TLS: a self-signed certificate covering the current LAN
            // addresses. Failing to provide an identity is NOT fatal - sharing
            // falls back to plain HTTP so existing paired phones keep working.
            var identity = ResolveHttpsIdentity();

            WebApplication app;
            int configuredHttpsPort;
            if (identity is not null)
            {
                try
                {
                    (app, configuredHttpsPort) = await StartApiAsync(
                        serverInfo, identity, data.Port, tls: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The TLS listener itself failed (port conflict, store
                    // problem). Existing pinned phones keep their HTTPS
                    // endpoints and surface failures until the PC recovers;
                    // legacy HTTP-only phones must keep working.
                    _logger?.LogWarning(
                        ex,
                        "Starting the pinned-TLS listener failed; retrying with HTTP only on port {Port}.",
                        data.Port);
                    (app, configuredHttpsPort) = await StartApiAsync(
                        serverInfo, null, data.Port, tls: false).ConfigureAwait(false);
                }
            }
            else
            {
                (app, configuredHttpsPort) = await StartApiAsync(
                    serverInfo, null, data.Port, tls: false).ConfigureAwait(false);
            }

            var port = ResolvePort(app, "http");
            if (port is null)
            {
                await app.StopAsync().ConfigureAwait(false);
                SetStatus(new ControllerStatus(SharingState.Error, "The sharing service could not start."));
                return;
            }

            _apiApp = app;
            _actualPort = port.Value;

            // Only advertise TLS once the HTTPS listener is verified live.
            if (configuredHttpsPort > 0)
            {
                _actualHttpsPort = ResolvePort(app, "https") ?? 0;
                _httpsThumbprint = _actualHttpsPort > 0 ? identity!.Sha256Thumbprint : null;
                if (_actualHttpsPort <= 0)
                {
                    _logger?.LogWarning(
                        "The HTTPS listener did not report a bound port; TLS advertisement is disabled.");
                }
            }
            else
            {
                _actualHttpsPort = 0;
                _httpsThumbprint = null;
            }

            var listenerAddresses = GetServerAddresses(app);
            _logger?.LogInformation(
                "LAN API listening on {ListenerAddresses}; HTTP TCP {HttpPort}, pinned TLS TCP {HttpsPort}",
                string.Join(", ", listenerAddresses),
                _actualPort,
                _actualHttpsPort > 0 ? _actualHttpsPort : 0);

            var executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var firewallPorts = FirewallPorts();
                FirewallStatus = _firewall.Check(executablePath, firewallPorts);
                _logger?.LogInformation(
                    "Firewall check for {ExecutablePath} TCP {PortList}: {State}. {Detail} Private profile active: {PrivateProfileActive}",
                    executablePath,
                    string.Join(", ", firewallPorts),
                    FirewallStatus.State,
                    FirewallStatus.Detail,
                    FirewallStatus.PrivateNetworkActive);
            }

            _directoryErrors.Clear();
            _sharingWatchFault = false;
            foreach (var directory in directories)
            {
                var root = directory.Path;
                var validation = _validator.Validate(root);
                if (!validation.IsValid)
                {
                    _directoryErrors[root] = validation.Error ?? "Folder unavailable. Check its location and read permission.";
                    continue;
                }

                var monitor = new ClipMonitorService(
                    () => root, new ScopedClipStore(_catalog, root), TimeProvider.System,
                    _monitorOptions, logger: _logger, previewProvider: _clipPreview);
                monitor.RootUnavailable += _ => OnRootUnavailable(root);
                monitor.RootRecovered += _ => OnRootRecovered(root);
                _monitors.Add(new WatchedRoot(root, monitor));
                await monitor.StartAsync().ConfigureAwait(false);
            }

            // Fresh offer so the QR code in the UI is immediately usable once a folder is watched.
            CreatePairingSnapshot();
            if (!IsAnyFolderWatched())
            {
                _sharingWatchFault = true;
                SetStatus(new ControllerStatus(SharingState.Error, DescribeNothingWatched(), _actualPort));
                return;
            }

            SetStatus(new ControllerStatus(SharingState.Running, null, _actualPort));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start sharing");
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(new ControllerStatus(SharingState.Error, DescribeStartError(ex)));
        }
    }

    private HttpsIdentity? ResolveHttpsIdentity()
    {
        if (_httpsIdentity is null || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var lanAddresses = LocalIpLocator.AllowedLanIpv4Addresses();
        if (lanAddresses.Count == 0)
        {
            _logger?.LogInformation("No private LAN IPv4 addresses were found; the HTTPS listener is disabled.");
            return null;
        }

        return _httpsIdentity.GetOrCreateIdentity(lanAddresses, _logger);
    }

    private async Task<(WebApplication App, int HttpsPort)> StartApiAsync(
        IServerInfoProvider serverInfo,
        HttpsIdentity? identity,
        int port,
        bool tls)
    {
        // HTTPS never reuses the HTTP port. Fixed ports bind port + 1; the
        // ephemeral port 0 (tests/smoke) binds an ephemeral HTTPS port too.
        var httpsPort = tls && identity is not null && port > 0 && port < 65535
            ? port + 1
            : 0;

        var options = new LanApiOptions
        {
            Catalog = _catalog,
            Pairing = _pairing,
            Devices = _devices,
            ServerInfo = serverInfo,
            ClipPreview = _clipPreview,
            Port = port,
            HttpsPort = httpsPort,
            HttpsCertificate = tls && identity is not null ? identity.Certificate : null,
        };

        var app = LanApiFactory.Build(options);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await DisposeAppQuietlyAsync(app).ConfigureAwait(false);
            throw;
        }

        // Port 0 requests an ephemeral listener; advertise its actual bound port too.
        return (app, tls && identity is not null ? ResolvePort(app, "https") ?? 0 : 0);
    }

    private static async Task DisposeAppQuietlyAsync(WebApplication app)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // The app may never have fully started; disposal below is enough.
        }

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: nothing else references this instance.
        }
    }

    public Task<bool> PauseSharingAsync() => RunExclusiveAsync(async () =>
    {
        if (!_settings.TryUpdate(d => d.SharingEnabled = false))
        {
            return ReportSettingsSaveFailure();
        }

        await StopCoreAsync().ConfigureAwait(false);
        SetStatus(ControllerStatus.Paused);
        return true;
    });

    public Task<bool> ResumeSharingAsync() => RunExclusiveAsync(async () =>
    {
        if (!_settings.TryUpdate(d => d.SharingEnabled = true))
        {
            return ReportSettingsSaveFailure();
        }

        await StartSharingCoreAsync().ConfigureAwait(false);
        return Status.State != SharingState.Error;
    });

    public Task RestartSharingAsync() => RunExclusiveAsync(async () =>
    {
        await StopCoreAsync().ConfigureAwait(false);
        if (_settings.Current.SharingEnabled)
        {
            await StartSharingCoreAsync().ConfigureAwait(false);
        }
        else
        {
            SetStatus(ControllerStatus.Paused);
        }
    });

    public async Task<bool> ToggleSharingAsync()
    {
        if (IsSharingRunning)
        {
            return await PauseSharingAsync().ConfigureAwait(false);
        }

        return await ResumeSharingAsync().ConfigureAwait(false);
    }

    /// <summary>A read never creates or extends an offer, or advertises a stopped listener.</summary>
    public PairingDisplaySnapshot? PairingSnapshot
    {
        get
        {
            var snapshot = _pairingSnapshot;
            var offer = _pairing.CurrentOffer;
            return Status.State == SharingState.Running && snapshot is not null
                && offer is not null && snapshot.Payload.PairingCode == offer.Code
                && snapshot.ExpiresAtUtc == offer.ExpiresAtUtc
                ? snapshot : null;
        }
    }

    public bool RegeneratePairingOffer()
    {
        if (Status.State != SharingState.Running || _apiApp is null || _pairingSnapshot is null)
        {
            return false;
        }

        CreatePairingSnapshot();
        StateChanged?.Invoke();
        return true;
    }

    private void CreatePairingSnapshot()
    {
        var offer = _pairing.NewPairingOffer();
        var endpoint = LocalIpLocator.FindBestEndpoint();
        var address = endpoint?.Address.ToString() ?? IPAddress.Loopback.ToString();
        _logger?.LogInformation(
            "Selected LAN interface {InterfaceName} with IPv4 {Address} for pairing QR (default gateway: {HasDefaultGateway})",
            endpoint?.InterfaceName ?? "loopback",
            address,
            endpoint?.HasDefaultGateway ?? false);
        var payload = new QrPayload
        {
            ServerId = _settings.Current.ServerId,
            ComputerName = Environment.MachineName,
            LanAddress = address,
            Port = _actualPort > 0 ? _actualPort : _settings.Current.Port,
            PairingCode = offer.Code,
        };

        // Only advertise pinned TLS when the listener is verified live; a QR
        // that points at a dead HTTPS port would break pairing outright.
        if (_actualHttpsPort > 0 && !string.IsNullOrEmpty(_httpsThumbprint))
        {
            payload = payload with
            {
                HttpsPort = _actualHttpsPort,
                CertSha256 = _httpsThumbprint,
            };
        }

        _pairingSnapshot = new PairingDisplaySnapshot(payload, offer.ExpiresAtUtc);
    }

    public IReadOnlyList<ClipEntry> CatalogSnapshot() => _catalog.GetClips();

    public IReadOnlyList<PairedDeviceInfo> PairedDevices => _devices.List();

    public bool RevokeDevice(string deviceId)
    {
        var revoked = _devices.Revoke(deviceId);
        NotifyStateChanged();
        return revoked;
    }

    public async Task<bool> ConfigureFirewallAsync()
    {
        var ports = FirewallPorts();
        if (!IsSharingRunning || _actualPort <= 0 || ports.Length == 0)
        {
            FirewallStatus = new(
                FirewallRuleState.Unavailable,
                "Start sharing before configuring Windows Firewall.",
                false);
            StateChanged?.Invoke();
            return false;
        }

        var executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            FirewallStatus = new(
                FirewallRuleState.Unavailable,
                "The running Kivo executable path could not be determined.",
                false);
            StateChanged?.Invoke();
            return false;
        }

        FirewallStatus = await _firewall.EnsurePrivateRuleAsync(executablePath, ports).ConfigureAwait(false);
        _logger?.LogInformation(
            "Firewall setup for {ExecutablePath} TCP {PortList}: {State}. {Detail}",
            executablePath,
            string.Join(", ", ports),
            FirewallStatus.State,
            FirewallStatus.Detail);
        StateChanged?.Invoke();
        return FirewallStatus.State == FirewallRuleState.Ready;
    }

    public void OpenRecordingsFolderInExplorer()
    {
        var folder = RecordingsFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }

    private async Task StopCoreAsync()
    {
        _sharingWatchFault = false;
        _pairingSnapshot = null;
        StateChanged?.Invoke();
        foreach (var watched in _monitors)
        {
            await watched.Monitor.DisposeAsync().ConfigureAwait(false);
        }
        _monitors.Clear();

        if (_apiApp is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _apiApp.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await _apiApp.DisposeAsync().ConfigureAwait(false);
                _apiApp = null;
            }
        }

        // TLS state belongs to the (now stopped) listener; the QR must not
        // advertise a pin while sharing is paused.
        _actualHttpsPort = 0;
        _httpsThumbprint = null;
    }

    private static int? ResolvePort(WebApplication app, string scheme)
    {
        var addresses = GetServerAddresses(app);

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && uri.Scheme == scheme
                && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return null;
    }

    private int[] FirewallPorts()
    {
        var ports = new List<int> { _actualPort };
        if (_actualHttpsPort > 0)
        {
            ports.Add(_actualHttpsPort);
        }

        return ports.Distinct().ToArray();
    }

    private static string[] GetServerAddresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
            ?? [];

    private static string DescribeStartError(Exception ex) =>
        ex is SocketException || ex.InnerException is SocketException || ex is IOException
            ? "Could not bind the local network port. Try changing the port in settings."
            : "Sharing failed to start. See local logs for details.";

    private void OnRootUnavailable(string root)
    {
        _directoryErrors[root] = "Folder unavailable. Check the drive and read permission, then restart sharing.";
        PublishWatchStatus();
    }

    private void OnRootRecovered(string root)
    {
        _directoryErrors.TryRemove(root, out _);
        PublishWatchStatus();
    }

    /// <summary>
    /// WPF reads <see cref="Status"/> and not <see cref="DirectoryErrors"/>.
    /// A partial outage stays <see cref="SharingState.Running"/>; a total outage becomes <see cref="SharingState.Error"/>.
    /// </summary>
    private void PublishWatchStatus()
    {
        if (_apiApp is null || Status.State is SharingState.Paused or SharingState.NotConfigured or SharingState.Starting)
        {
            NotifyStateChanged();
            return;
        }

        if (IsAnyFolderWatched())
        {
            if (_sharingWatchFault && Status.State == SharingState.Error)
            {
                _sharingWatchFault = false;
                SetStatus(new ControllerStatus(SharingState.Running, null, _actualPort));
                return;
            }

            NotifyStateChanged();
            return;
        }

        _sharingWatchFault = true;
        var detail = DescribeNothingWatched();
        if (Status.State == SharingState.Error && Status.Detail == detail)
        {
            NotifyStateChanged();
            return;
        }

        SetStatus(new ControllerStatus(SharingState.Error, detail, _actualPort));
    }

    private bool IsAnyFolderWatched()
    {
        foreach (var watched in _monitors)
        {
            if (watched.Monitor.IsRunning && !_directoryErrors.ContainsKey(watched.Root))
            {
                return true;
            }
        }

        return false;
    }

    private string DescribeNothingWatched() =>
        _directoryErrors.Count == 1
            ? _directoryErrors.Values.First()
            : NothingWatchedDetail;

    private bool ReportSettingsSaveFailure()
    {
        ConfigurationError = SettingsSaveFailedDetail;
        SetStatus(new ControllerStatus(Status.State, SettingsSaveFailedDetail, Status.Port));
        return false;
    }

    private void SetStatus(ControllerStatus status)
    {
        Status = status;
        StateChanged?.Invoke();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _catalog.Changed -= NotifyStateChanged;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private sealed record WatchedRoot(string Root, ClipMonitorService Monitor);

    private sealed class StaticServerInfo(string serverId, string computerName) : IServerInfoProvider
    {
        public string ServerId { get; } = serverId;

        public string ComputerName { get; } = computerName;

        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    }
}

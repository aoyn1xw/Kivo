using System.Diagnostics;
using System.Net;
using Kivo.Core.Clips;
using Kivo.Core.Devices;
using Kivo.Core.Models;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Kivo.Core.Validation;
using Kivo.Windows.Services;
using Xunit;

namespace Kivo.Tests;

public sealed class AppControllerSharingStatusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_settings_keeps_the_running_share_when_the_snapshot_cannot_be_saved(bool accessDenied)
    {
        await using var fixture = new SharingStatusFixture();
        await fixture.Controller.InitializeAsync();
        var livePort = fixture.Controller.Status.Port;
        var serverId = fixture.Settings.Current.ServerId;
        var replacement = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "replacement")).FullName;
        fixture.Store.Failure = accessDenied
            ? new UnauthorizedAccessException("Test denied")
            : new IOException("Test full disk");

        var saved = await fixture.Controller.ApplySettingsAsync(replacement, 18443, sharingEnabled: false);

        Assert.False(saved);
        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(livePort, fixture.Controller.Status.Port);
        Assert.Contains("could not be saved", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Controller.Status.Detail, fixture.Controller.ConfigurationError);
        Assert.Equal(fixture.Root, fixture.Controller.RecordingsFolder);
        Assert.Equal(fixture.Root, fixture.Settings.Current.RecordingsFolder);
        Assert.Equal(0, fixture.Controller.ConfiguredPort);
        Assert.True(fixture.Settings.Current.SharingEnabled);
        Assert.Equal(serverId, fixture.Settings.Current.ServerId);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
        var persisted = new JsonSettingsStore(fixture.SettingsDirectory).Load();
        Assert.Equal(fixture.Root, persisted.RecordingsFolder);
        Assert.Equal(0, persisted.Port);
        Assert.True(persisted.SharingEnabled);
        Assert.Equal(serverId, persisted.ServerId);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{livePort}/"), Timeout = TimeSpan.FromSeconds(5) };
        using var health = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Select_folder_keeps_existing_roots_when_the_snapshot_cannot_be_saved(bool accessDenied)
    {
        await using var fixture = new SharingStatusFixture();
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "second")).FullName;
        fixture.Configure(
            new RecordingDirectory { Path = fixture.Root, Source = "nvidia" },
            new RecordingDirectory { Path = secondRoot, Source = "obs" });
        await fixture.Controller.InitializeAsync();
        var livePort = fixture.Controller.Status.Port;
        var added = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "added")).FullName;
        fixture.Store.Failure = accessDenied
            ? new UnauthorizedAccessException("Test denied")
            : new IOException("Test full disk");

        Assert.False(await fixture.Controller.SelectFolderAsync(added));

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(livePort, fixture.Controller.Status.Port);
        Assert.Contains("could not be saved", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Controller.Status.Detail, fixture.Controller.ConfigurationError);
        Assert.Equal([fixture.Root, secondRoot], fixture.Controller.RecordingDirectories.Select(directory => directory.Path).ToArray());
        var persisted = new JsonSettingsStore(fixture.SettingsDirectory).Load();
        Assert.Equal([fixture.Root, secondRoot], persisted.RecordingDirectories.Select(directory => directory.Path).ToArray());
        Assert.NotNull(fixture.Controller.PairingSnapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_keeps_sharing_when_the_preference_cannot_be_saved(bool accessDenied)
    {
        await using var fixture = new SharingStatusFixture();
        await fixture.Controller.InitializeAsync();
        var livePort = fixture.Controller.Status.Port;
        fixture.Store.Failure = accessDenied
            ? new UnauthorizedAccessException("Test denied")
            : new IOException("Test full disk");

        Assert.False(await fixture.Controller.PauseSharingAsync());
        Assert.False(await fixture.Controller.ToggleSharingAsync());

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(livePort, fixture.Controller.Status.Port);
        Assert.True(fixture.Settings.Current.SharingEnabled);
        Assert.True(new JsonSettingsStore(fixture.SettingsDirectory).Load().SharingEnabled);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{livePort}/"), Timeout = TimeSpan.FromSeconds(5) };
        using var health = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Resume_stays_paused_when_the_preference_cannot_be_saved()
    {
        await using var fixture = new SharingStatusFixture();
        await fixture.Controller.InitializeAsync();
        Assert.True(await fixture.Controller.PauseSharingAsync());
        fixture.Store.Failure = new IOException("Test full disk");

        Assert.False(await fixture.Controller.ResumeSharingAsync());

        Assert.Equal(SharingState.Paused, fixture.Controller.Status.State);
        Assert.False(fixture.Settings.Current.SharingEnabled);
        Assert.False(new JsonSettingsStore(fixture.SettingsDirectory).Load().SharingEnabled);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.Contains("could not be saved", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sharing_is_an_error_when_every_configured_folder_fails_validation()
    {
        await using var fixture = new SharingStatusFixture();
        var missing = Path.Combine(fixture.Recordings.Path, "disconnected-drive");
        var otherMissing = Path.Combine(fixture.Recordings.Path, "other-disconnected-drive");
        fixture.Configure(
            new RecordingDirectory { Path = missing, Source = "nvidia" },
            new RecordingDirectory { Path = otherMissing, Source = "obs" });

        await fixture.Controller.InitializeAsync();

        Assert.Equal(SharingState.Error, fixture.Controller.Status.State);
        Assert.Equal(
            "No recording folders are being watched. Check that each folder exists and is readable.",
            fixture.Controller.Status.Detail);
        Assert.Contains(missing, fixture.Controller.DirectoryErrors.Keys);
        Assert.Contains(otherMissing, fixture.Controller.DirectoryErrors.Keys);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.False(fixture.Controller.IsSharingRunning);
    }

    [Fact]
    public async Task Sharing_is_an_error_when_the_only_folder_fails_validation()
    {
        await using var fixture = new SharingStatusFixture();
        var missing = Path.Combine(fixture.Recordings.Path, "disconnected-drive");
        fixture.Configure(new RecordingDirectory { Path = missing });

        await fixture.Controller.InitializeAsync();

        Assert.Equal(SharingState.Error, fixture.Controller.Status.State);
        Assert.Contains("does not exist", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, fixture.Controller.DirectoryErrors.Keys);
        Assert.Null(fixture.Controller.PairingSnapshot);
    }

    [Fact]
    public async Task Sharing_is_an_error_when_a_monitor_never_starts()
    {
        await using var fixture = new SharingStatusFixture(new AcceptingValidator());
        var missing = Path.Combine(fixture.Recordings.Path, "accepted-but-missing");
        fixture.Configure(new RecordingDirectory { Path = missing });

        await fixture.Controller.InitializeAsync();

        Assert.Equal(SharingState.Error, fixture.Controller.Status.State);
        Assert.Contains("unavailable", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, fixture.Controller.DirectoryErrors.Keys);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.False(fixture.Controller.IsSharingRunning);
    }

    [Fact]
    public async Task Sharing_stays_running_when_one_folder_is_still_watched()
    {
        await using var fixture = new SharingStatusFixture();
        var missing = Path.Combine(fixture.Recordings.Path, "disconnected-drive");
        fixture.Configure(
            new RecordingDirectory { Path = fixture.Root, Source = "nvidia" },
            new RecordingDirectory { Path = missing, Source = "custom" });

        await fixture.Controller.InitializeAsync();

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Contains(missing, fixture.Controller.DirectoryErrors.Keys);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
        Assert.True(fixture.Controller.Status.Port > 0);
    }

    [Fact]
    public async Task Sharing_is_an_error_when_the_last_watched_folder_becomes_unavailable()
    {
        var options = new MonitorOptions
        {
            ReconcileInterval = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(100),
            DebounceDelay = TimeSpan.FromMilliseconds(20),
            StabilityWindow = TimeSpan.FromMilliseconds(20),
        };
        await using var fixture = new SharingStatusFixture(monitorOptions: options);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "target")).FullName;
        var root = Path.Combine(fixture.Recordings.Path, "linked-root");
        CreateJunction(root, target);
        try
        {
            // The file watcher holds the target open, so the configured junction is removed instead.
            fixture.Configure(new RecordingDirectory { Path = root });
            await fixture.Controller.InitializeAsync();
            Assert.Equal(SharingState.Running, fixture.Controller.Status.State);

            Directory.Delete(root);
            await WaitUntilAsync(() => fixture.Controller.Status.State == SharingState.Error);

            Assert.Contains("unavailable", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(root, fixture.Controller.DirectoryErrors.Keys);
            Assert.Null(fixture.Controller.PairingSnapshot);
            Assert.False(fixture.Controller.IsSharingRunning);

            CreateJunction(root, target);
            await WaitUntilAsync(() => fixture.Controller.Status.State == SharingState.Running);

            Assert.DoesNotContain(root, fixture.Controller.DirectoryErrors.Keys);
            Assert.NotNull(fixture.Controller.PairingSnapshot);
            Assert.Null(fixture.Controller.Status.Detail);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }
        }
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        Assert.True(Directory.Exists(junction));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private sealed class SharingStatusFixture : IAsyncDisposable
    {
        private readonly TempFolder _settingsFolder = new();
        private readonly TempFolder _recordings = new();

        public SharingStatusFixture(IFolderValidator? validator = null, MonitorOptions? monitorOptions = null)
        {
            SettingsDirectory = _settingsFolder.Path;
            Root = Directory.CreateDirectory(Path.Combine(_recordings.Path, "clips")).FullName;
            Store = new FailingSettingsStore(SettingsDirectory);
            Settings = new SettingsService(Store);
            Settings.Update(data =>
            {
                data.RecordingsFolder = Root;
                data.Port = 0;
            });
            var devices = new DeviceRegistry(Settings, TimeProvider.System);
            var pairing = new PairingService(new SecureTokenGenerator(), devices, TimeProvider.System);
            Controller = new AppController(
                Settings,
                validator ?? new FolderValidator(),
                new ClipCatalog(),
                pairing,
                devices,
                new IdleFirewall(),
                new NoPreview(),
                monitorOptions: monitorOptions);
        }

        public string SettingsDirectory { get; }

        public TempFolder Recordings => _recordings;

        public string Root { get; }

        public FailingSettingsStore Store { get; }

        public SettingsService Settings { get; }

        public AppController Controller { get; }

        public void Configure(params RecordingDirectory[] directories) =>
            Settings.Update(data => RecordingConfiguration.Replace(data, directories));

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            _recordings.Dispose();
            _settingsFolder.Dispose();
        }
    }

    private sealed class FailingSettingsStore(string baseDirectory) : ISettingsStore
    {
        private readonly JsonSettingsStore _inner = new(baseDirectory);

        public Exception? Failure { get; set; }

        public AppSettingsData Load() => _inner.Load();

        public void Save(AppSettingsData data)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            _inner.Save(data);
        }
    }

    private sealed class AcceptingValidator : IFolderValidator
    {
        public FolderValidationResult Validate(string? path) => FolderValidationResult.Ok;
    }

    private sealed class NoPreview : IClipPreviewProvider
    {
        public bool SupportsThumbnails => false;

        public TimeSpan? GetDuration(string fullPath) => null;

        public byte[]? GetThumbnail(ClipEntry entry) => null;
    }

    private sealed class IdleFirewall : IWindowsFirewallService
    {
        private static readonly FirewallStatus Ready = new(FirewallRuleState.Ready, "Ready", true);

        public FirewallStatus Check(string executablePath, int port) => Ready;

        public FirewallStatus Check(string executablePath, IReadOnlyCollection<int> ports) => Ready;

        public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, int port) => Task.FromResult(Ready);

        public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, IReadOnlyCollection<int> ports) =>
            Task.FromResult(Ready);
    }
}

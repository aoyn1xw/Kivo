using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Kivo.Core.Clips;
using Kivo.Core.Devices;
using Kivo.Core.Models;
using Kivo.Core.Networking;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Kivo.Core.Validation;
using Kivo.Windows.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kivo.Tests;

public sealed class AppControllerDirectoryTests
{
    [Fact]
    public async Task Concurrent_starts_reuse_one_listener_and_disposal_closes_it()
    {
        await using var fixture = new DirectoryControllerFixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            await fixture.Controller.StartSharingAsync();
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
        var currentPort = fixture.Controller.Status.Port;
        using var client = fixture.CreateClient();
        using var health = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var startedPorts = fixture.Firewall.CheckedPorts.Distinct().ToArray();
        Assert.Single(fixture.Firewall.CheckedPorts);
        Assert.Contains(currentPort, startedPorts);
        foreach (var port in startedPorts.Where(port => port != currentPort))
        {
            Assert.False(await CanConnectAsync(port), $"An earlier listener remains open on TCP {port}.");
        }

        await fixture.Controller.DisposeAsync();

        foreach (var port in startedPorts)
        {
            Assert.False(await CanConnectAsync(port), $"Disposal left TCP {port} open.");
        }
    }

    [Fact]
    public async Task Two_roots_are_available_through_the_API_and_removing_one_keeps_original_files_and_credentials()
    {
        await using var fixture = new DirectoryControllerFixture();
        var firstPath = fixture.Recordings.CreateMp4("first/nvidia.mp4", 4096);
        var secondPath = fixture.Recordings.CreateMp4("second/obs.mp4", 2048);
        var firstBytes = await File.ReadAllBytesAsync(firstPath);
        var secondBytes = await File.ReadAllBytesAsync(secondPath);
        var secondRoot = Path.GetDirectoryName(secondPath)!;
        fixture.Configure(
            new RecordingDirectory { Path = fixture.FirstRoot, Source = "nvidia" },
            new RecordingDirectory { Path = secondRoot, Source = "obs" });
        await fixture.Controller.InitializeAsync();
        using var client = fixture.CreateClient();
        var token = await PairAsync(fixture.Controller, client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var clips = await WaitForClipsAsync(client, 2);

        Assert.Collection(clips.Select(clip => clip.GetProperty("fileName").GetString()).Order(StringComparer.Ordinal),
            name => Assert.Equal("nvidia.mp4", name), name => Assert.Equal("obs.mp4", name));
        var firstId = clips.Single(clip => clip.GetProperty("fileName").GetString() == "nvidia.mp4")
            .GetProperty("id").GetString();
        var secondId = clips.Single(clip => clip.GetProperty("fileName").GetString() == "obs.mp4")
            .GetProperty("id").GetString();
        Assert.Equal(firstBytes, await client.GetByteArrayAsync($"api/v1/clips/{firstId}/download"));
        Assert.Equal(secondBytes, await client.GetByteArrayAsync($"api/v1/clips/{secondId}/download"));

        Assert.True(await fixture.Controller.ApplyRecordingDirectoriesAsync(
            [new RecordingDirectory { Path = secondRoot, Source = "obs" }]));
        using var restartedClient = fixture.CreateClient();
        restartedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var remaining = await WaitForClipsAsync(restartedClient, 1);

        Assert.Equal(secondId, Assert.Single(remaining).GetProperty("id").GetString());
        Assert.Equal(secondRoot, fixture.Settings.Current.RecordingsFolder);
        Assert.Single(fixture.Controller.PairedDevices);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(secondPath));
        Assert.Equal(secondBytes, await restartedClient.GetByteArrayAsync($"api/v1/clips/{secondId}/download"));
        using var removed = await restartedClient.GetAsync($"api/v1/clips/{firstId}/download");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
    }

    [Fact]
    public async Task Missing_secondary_root_reports_a_directory_error_while_API_and_pairing_remain_available()
    {
        await using var fixture = new DirectoryControllerFixture();
        var missing = Path.Combine(fixture.Recordings.Path, "disconnected-drive");
        fixture.Configure(
            new RecordingDirectory { Path = fixture.FirstRoot, Source = "nvidia" },
            new RecordingDirectory { Path = missing, Source = "custom" });

        await fixture.Controller.InitializeAsync();

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Contains(missing, fixture.Controller.DirectoryErrors.Keys);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
        Assert.False(Directory.Exists(missing));
        using var client = fixture.CreateClient();
        using var health = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var token = await PairAsync(fixture.Controller, client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var server = await client.GetAsync("api/v1/server");
        Assert.Equal(HttpStatusCode.OK, server.StatusCode);
        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
    }

    [Fact]
    public async Task Invalid_directory_changes_preserve_the_live_listener_offer_and_existing_configuration()
    {
        await using var fixture = new DirectoryControllerFixture();
        await fixture.Controller.InitializeAsync();
        var port = fixture.Controller.Status.Port;
        var offer = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);

        Assert.False(await fixture.Controller.ApplyRecordingDirectoriesAsync(
        [
            new RecordingDirectory { Path = fixture.FirstRoot },
            new RecordingDirectory { Path = Path.Combine(fixture.FirstRoot, "nested") },
        ]));
        Assert.False(string.IsNullOrWhiteSpace(fixture.Controller.ConfigurationError));
        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(port, fixture.Controller.Status.Port);
        Assert.Same(offer, fixture.Controller.PairingSnapshot);

        var missing = Path.Combine(fixture.Recordings.Path, "missing");
        Assert.False(await fixture.Controller.ApplyRecordingDirectoriesAsync(
            [new RecordingDirectory { Path = missing }]));

        Assert.False(string.IsNullOrWhiteSpace(fixture.Controller.ConfigurationError));
        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(port, fixture.Controller.Status.Port);
        Assert.Same(offer, fixture.Controller.PairingSnapshot);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Controller.RecordingDirectories).Path);
        Assert.Equal(fixture.FirstRoot, fixture.Settings.Current.RecordingsFolder);
        Assert.False(Directory.Exists(missing));
        using var client = fixture.CreateClient();
        using var health = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Ephemeral_https_advertisement_matches_the_live_certificate_and_LAN_availability()
    {
        using var identity = new TestHttpsIdentity();
        await using var fixture = new DirectoryControllerFixture(identity);
        await fixture.Controller.InitializeAsync();
        var snapshot = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);
        if (LocalIpLocator.AllowedLanIpv4Addresses().Count == 0)
        {
            Assert.Null(snapshot.Payload.HttpsPort);
            Assert.Null(snapshot.Payload.CertSha256);
            return;
        }

        Assert.True(snapshot.Payload.HttpsPort > 0);
        Assert.NotEqual(snapshot.Payload.Port, snapshot.Payload.HttpsPort);
        Assert.Equal(identity.Fingerprint, snapshot.Payload.CertSha256);
        var certificateObserved = false;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                certificateObserved = certificate is not null
                    && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        snapshot.Payload.CertSha256, StringComparison.Ordinal);
                return certificateObserved;
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        using var health = await client.GetAsync($"https://127.0.0.1:{snapshot.Payload.HttpsPort}/api/v1/health");

        Assert.True(certificateObserved);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Select_folder_adds_a_root_without_removing_the_folders_already_configured()
    {
        await using var fixture = new DirectoryControllerFixture();
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "second")).FullName;
        fixture.Configure(
            new RecordingDirectory { Path = fixture.FirstRoot, Source = "nvidia" },
            new RecordingDirectory { Path = secondRoot, Source = "obs" });
        await fixture.Controller.InitializeAsync();
        var added = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "added")).FullName;

        Assert.True(await fixture.Controller.SelectFolderAsync(added + Path.DirectorySeparatorChar));

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
        Assert.Equal(
            [fixture.FirstRoot, secondRoot, added],
            fixture.Controller.RecordingDirectories.Select(directory => directory.Path).ToArray());
        Assert.Equal("nvidia", fixture.Controller.RecordingDirectories[0].Source);
        Assert.Equal("obs", fixture.Controller.RecordingDirectories[1].Source);
        Assert.Equal("custom", fixture.Controller.RecordingDirectories[2].Source);
        Assert.Equal(fixture.FirstRoot, fixture.Settings.Current.RecordingsFolder);
        Assert.Equal(added, fixture.Settings.Current.RecordingDirectories[2].Path);
    }

    [Fact]
    public async Task Select_folder_with_no_existing_roots_still_saves_that_folder()
    {
        await using var fixture = new DirectoryControllerFixture();
        fixture.Configure();

        Assert.True(await fixture.Controller.SelectFolderAsync(fixture.FirstRoot));

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Controller.RecordingDirectories).Path);
        Assert.Equal("custom", fixture.Controller.RecordingDirectories[0].Source);
        Assert.Equal(fixture.FirstRoot, fixture.Settings.Current.RecordingsFolder);
    }

    [Fact]
    public async Task Select_folder_retries_sharing_after_the_saved_folder_failed_to_start()
    {
        await using var fixture = new DirectoryControllerFixture();
        fixture.Configure();
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var blockedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        fixture.Settings.Update(data => data.Port = blockedPort);

        Assert.False(await fixture.Controller.SelectFolderAsync(fixture.FirstRoot));
        Assert.Equal(SharingState.Error, fixture.Controller.Status.State);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Controller.RecordingDirectories).Path);
        Assert.DoesNotContain("duplicate", fixture.Controller.ConfigurationError, StringComparison.OrdinalIgnoreCase);

        Assert.False(await fixture.Controller.SelectFolderAsync(fixture.FirstRoot));
        Assert.DoesNotContain("duplicate", fixture.Controller.ConfigurationError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Settings.Current.RecordingDirectories).Path);

        listener.Stop();

        Assert.True(await fixture.Controller.SelectFolderAsync(fixture.FirstRoot));
        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Controller.RecordingDirectories).Path);
        Assert.Equal("custom", fixture.Controller.RecordingDirectories[0].Source);
    }

    [Fact]
    public async Task Select_folder_accepts_a_relative_path_that_validation_accepts()
    {
        var name = "kivo-relative-" + Guid.NewGuid().ToString("N");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(name));
        Directory.CreateDirectory(full);
        try
        {
            await using var fixture = new DirectoryControllerFixture();
            var existing = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "kept")).FullName;
            fixture.Configure(new RecordingDirectory { Path = existing, Source = "nvidia" });
            Assert.False(Path.IsPathFullyQualified(name));

            Assert.True(await fixture.Controller.SelectFolderAsync(name));

            Assert.Equal(
                [existing, full],
                fixture.Controller.RecordingDirectories.Select(directory => directory.Path).ToArray());
            Assert.Equal("nvidia", fixture.Controller.RecordingDirectories[0].Source);
            Assert.Equal("custom", fixture.Controller.RecordingDirectories[1].Source);
        }
        finally
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Select_folder_rejects_a_duplicate_or_nested_path_without_changing_the_saved_roots()
    {
        await using var fixture = new DirectoryControllerFixture();
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "second")).FullName;
        fixture.Configure(
            new RecordingDirectory { Path = fixture.FirstRoot, Source = "nvidia" },
            new RecordingDirectory { Path = secondRoot, Source = "obs" });
        await fixture.Controller.InitializeAsync();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.FirstRoot, "nested")).FullName;

        Assert.True(await fixture.Controller.SelectFolderAsync(fixture.FirstRoot.ToUpperInvariant()));
        Assert.Equal(
            [fixture.FirstRoot, secondRoot],
            fixture.Controller.RecordingDirectories.Select(directory => directory.Path).ToArray());
        var port = fixture.Controller.Status.Port;
        Assert.False(await fixture.Controller.SelectFolderAsync(nested));
        Assert.Contains("duplicate", fixture.Controller.ConfigurationError, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(port, fixture.Controller.Status.Port);
        Assert.Equal(
            [fixture.FirstRoot, secondRoot],
            fixture.Controller.RecordingDirectories.Select(directory => directory.Path).ToArray());
        Assert.Equal("nvidia", fixture.Settings.Current.RecordingDirectories[0].Source);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
    }

    [Fact]
    public async Task Select_folder_rejects_an_invalid_path_and_keeps_the_running_share()
    {
        await using var fixture = new DirectoryControllerFixture();
        await fixture.Controller.InitializeAsync();
        var port = fixture.Controller.Status.Port;
        var missing = Path.Combine(fixture.Recordings.Path, "missing");

        Assert.False(await fixture.Controller.SelectFolderAsync(missing));

        Assert.Equal(SharingState.Running, fixture.Controller.Status.State);
        Assert.Equal(port, fixture.Controller.Status.Port);
        Assert.Contains("does not exist", fixture.Controller.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Controller.Status.Detail, fixture.Controller.ConfigurationError);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Controller.RecordingDirectories).Path);
        Assert.NotNull(fixture.Controller.PairingSnapshot);
    }

    [Fact]
    public async Task Wpf_settings_replace_only_the_primary_folder()
    {
        await using var fixture = new DirectoryControllerFixture();
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "second")).FullName;
        fixture.Configure(
            new RecordingDirectory { Path = fixture.FirstRoot, Source = "nvidia" },
            new RecordingDirectory { Path = secondRoot, Source = "obs" });
        var replacement = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "replacement")).FullName;

        Assert.True(await fixture.Controller.ApplySettingsAsync(replacement, 18443, sharingEnabled: true));

        Assert.Equal(
            [replacement, secondRoot],
            fixture.Controller.RecordingDirectories.Select(directory => directory.Path).ToArray());
        Assert.Equal("nvidia", fixture.Controller.RecordingDirectories[0].Source);
        Assert.Equal("obs", fixture.Controller.RecordingDirectories[1].Source);
        Assert.Equal(18443, fixture.Settings.Current.Port);
    }

    [Fact]
    public async Task Directory_changes_after_disposal_cannot_persist_or_restart_sharing()
    {
        await using var fixture = new DirectoryControllerFixture();
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Recordings.Path, "second")).FullName;
        await fixture.Controller.DisposeAsync();

        Assert.False(await fixture.Controller.ApplyRecordingDirectoriesAsync(
            [new RecordingDirectory { Path = secondRoot, Source = "obs" }]));

        Assert.Equal(fixture.FirstRoot, fixture.Settings.Current.RecordingsFolder);
        Assert.Equal(fixture.FirstRoot, Assert.Single(fixture.Controller.RecordingDirectories).Path);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.Empty(fixture.Firewall.CheckedPorts);
    }

    private static async Task<string> PairAsync(AppController controller, HttpClient client)
    {
        var offer = Assert.IsType<PairingDisplaySnapshot>(controller.PairingSnapshot);
        using var response = await client.PostAsJsonAsync("api/v1/pair/exchange",
            new { pairingCode = offer.Payload.PairingCode, deviceName = "Directory test phone" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement[]> WaitForClipsAsync(HttpClient client, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement[] clips;
        do
        {
            clips = await client.GetFromJsonAsync<JsonElement[]>("api/v1/clips") ?? [];
            if (clips.Length == expectedCount) return clips;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Equal(expectedCount, clips.Length);
        return clips;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class DirectoryControllerFixture : IAsyncDisposable
    {
        private readonly TempFolder _settingsFolder = new();
        public TempFolder Recordings { get; } = new();
        public string FirstRoot { get; }
        public SettingsService Settings { get; }
        public RecordingFirewall Firewall { get; } = new();
        public AppController Controller { get; }

        public DirectoryControllerFixture(IHttpsIdentityService? identity = null)
        {
            FirstRoot = Directory.CreateDirectory(Path.Combine(Recordings.Path, "first")).FullName;
            Settings = new SettingsService(new JsonSettingsStore(_settingsFolder.Path));
            Configure(new RecordingDirectory { Path = FirstRoot });
            Settings.Update(data => data.Port = 0);
            var devices = new DeviceRegistry(Settings, TimeProvider.System);
            var pairing = new PairingService(new SecureTokenGenerator(), devices, TimeProvider.System);
            Controller = new AppController(Settings, new FolderValidator(), new ClipCatalog(),
                pairing, devices, Firewall, new NoPreview(), identity);
        }

        public void Configure(params RecordingDirectory[] directories) =>
            Settings.Update(data => RecordingConfiguration.Replace(data, directories));

        public HttpClient CreateClient() => new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Controller.Status.Port}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            Recordings.Dispose();
            _settingsFolder.Dispose();
        }
    }

    private sealed class NoPreview : IClipPreviewProvider
    {
        public bool SupportsThumbnails => false;
        public TimeSpan? GetDuration(string fullPath) => null;
        public byte[]? GetThumbnail(ClipEntry entry) => null;
    }

    private sealed class RecordingFirewall : IWindowsFirewallService
    {
        private static readonly FirewallStatus Ready = new(FirewallRuleState.Ready, "Ready", true);
        public ConcurrentQueue<int> CheckedPorts { get; } = new();
        public FirewallStatus Check(string executablePath, int port) => Check(executablePath, [port]);
        public FirewallStatus Check(string executablePath, IReadOnlyCollection<int> ports)
        {
            foreach (var port in ports) CheckedPorts.Enqueue(port);
            return Ready;
        }

        public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, int port) => Task.FromResult(Ready);
        public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, IReadOnlyCollection<int> ports) => Task.FromResult(Ready);
    }

    private sealed class TestHttpsIdentity : IHttpsIdentityService, IDisposable
    {
        private readonly X509Certificate2 _certificate = HttpsIdentityService.CreateCertificate(
            [IPAddress.Loopback], DateTimeOffset.UtcNow.AddMinutes(-1));
        public string Fingerprint => _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        public HttpsIdentity GetOrCreateIdentity(IReadOnlyCollection<IPAddress> lanAddresses, ILogger? logger = null) =>
            new(_certificate, Fingerprint);
        public void Dispose() => _certificate.Dispose();
    }
}

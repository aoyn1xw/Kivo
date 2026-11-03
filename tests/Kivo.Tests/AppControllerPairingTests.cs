using Kivo.Core.Clips;
using Kivo.Core.Devices;
using Kivo.Core.Models;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Kivo.Core.Validation;
using Kivo.Windows.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Kivo.Tests;

public sealed class AppControllerPairingTests
{
    [Fact]
    public async Task Resume_and_settings_restart_replace_the_offer_and_stopping_hides_it()
    {
        await using var fixture = new ControllerPairingFixture();
        Assert.Null(fixture.Controller.PairingSnapshot);
        await fixture.Controller.StartSharingAsync();
        var first = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);
        Assert.Same(first, fixture.Controller.PairingSnapshot);
        Assert.Equal(first.Payload.PairingCode, fixture.Pairing.CurrentOffer!.Code);
        Assert.Equal(first.ExpiresAtUtc, fixture.Pairing.CurrentOffer.ExpiresAtUtc);
        Assert.Null(first.Payload.HttpsPort);
        Assert.Null(first.Payload.CertSha256);

        var clearedBeforePaused = false;
        fixture.Controller.StateChanged += () =>
        {
            if (fixture.Controller.Status.State == SharingState.Running && fixture.Controller.PairingSnapshot is null)
            {
                clearedBeforePaused = true;
            }
        };
        await fixture.Controller.PauseSharingAsync();
        Assert.True(clearedBeforePaused);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.False(fixture.Controller.RegeneratePairingOffer());
        await fixture.Controller.ResumeSharingAsync();
        var second = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);
        Assert.NotEqual(first.Payload.PairingCode, second.Payload.PairingCode);
        Assert.False(fixture.Pairing.Exchange(first.Payload.PairingCode, "old phone").Success);

        await fixture.Controller.ApplySettingsAsync(fixture.Recordings.Path, second.Payload.Port, true);
        var third = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);
        Assert.NotEqual(second.Payload.PairingCode, third.Payload.PairingCode);
        Assert.False(fixture.Pairing.Exchange(second.Payload.PairingCode, "old phone").Success);
        Assert.True(fixture.Pairing.Exchange(third.Payload.PairingCode, "phone").Success);
        Assert.Null(fixture.Controller.PairingSnapshot);
    }

    [Fact]
    public async Task Reads_do_not_rotate_or_extend_offers_and_expiry_requires_explicit_regeneration()
    {
        await using var fixture = new ControllerPairingFixture();
        await fixture.Controller.StartSharingAsync();
        var original = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);
        fixture.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Same(original, fixture.Controller.PairingSnapshot);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.Null(fixture.Pairing.CurrentOffer);
        Assert.True(fixture.Controller.RegeneratePairingOffer());
        var next = Assert.IsType<PairingDisplaySnapshot>(fixture.Controller.PairingSnapshot);
        Assert.NotEqual(original.Payload.PairingCode, next.Payload.PairingCode);
        Assert.True(fixture.Pairing.Exchange(next.Payload.PairingCode, "phone").Success);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.Null(fixture.Pairing.CurrentOffer);
    }

    [Fact]
    public async Task Starting_and_error_states_never_publish_an_offer()
    {
        await using var fixture = new ControllerPairingFixture();
        fixture.Controller.StateChanged += () =>
        {
            if (fixture.Controller.Status.State == SharingState.Starting)
            {
                Assert.Null(fixture.Controller.PairingSnapshot);
                Assert.False(fixture.Controller.RegeneratePairingOffer());
            }
        };
        await fixture.Controller.StartSharingAsync();
        await fixture.Controller.ApplySettingsAsync("", fixture.Controller.ConfiguredPort, true);
        Assert.Equal(SharingState.Error, fixture.Controller.Status.State);
        Assert.Null(fixture.Controller.PairingSnapshot);
        Assert.False(fixture.Controller.RegeneratePairingOffer());
    }
}

internal sealed class ControllerPairingFixture : IAsyncDisposable
{
    private readonly TempFolder _settingsFolder = new();
    public TempFolder Recordings { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public PairingService Pairing { get; }
    public AppController Controller { get; }

    public ControllerPairingFixture()
    {
        var settings = new SettingsService(new JsonSettingsStore(_settingsFolder.Path));
        settings.Update(data => { data.RecordingsFolder = Recordings.Path; data.Port = 0; });
        var devices = new DeviceRegistry(settings, Time);
        Pairing = new PairingService(new SecureTokenGenerator(), devices, Time);
        Controller = new AppController(settings, new FolderValidator(), new ClipCatalog(),
            Pairing, devices, new TestFirewall(), new TestPreview());
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.DisposeAsync();
        Recordings.Dispose();
        _settingsFolder.Dispose();
    }

    private sealed class TestPreview : IClipPreviewProvider
    {
        public bool SupportsThumbnails => false;
        public TimeSpan? GetDuration(string fullPath) => null;
        public byte[]? GetThumbnail(ClipEntry entry) => null;
    }

    private sealed class TestFirewall : IWindowsFirewallService
    {
        private static readonly FirewallStatus Ready = new(FirewallRuleState.Ready, "Ready", true);
        public FirewallStatus Check(string executablePath, int port) => Ready;
        public FirewallStatus Check(string executablePath, IReadOnlyCollection<int> ports) => Ready;
        public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, int port) => Task.FromResult(Ready);
        public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, IReadOnlyCollection<int> ports) => Task.FromResult(Ready);
    }
}

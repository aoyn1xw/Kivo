using System.Windows.Threading;
using Kivo.Core.Clips;
using Kivo.Core.Devices;
using Kivo.Core.Models;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Kivo.Core.Validation;
using Kivo.Windows.Services;
using Kivo.Windows.ViewModels;
using Xunit;

namespace Kivo.Tests;

/// <summary>
/// Revoking a paired device is consequential: the device must lose access and
/// pair again from scratch. A desktop revocation therefore requires an explicit
/// confirmation naming the device, and cancellation preserves access.
/// </summary>
public sealed class MainViewModelRevocationTests
{
    [Fact]
    public async Task Revoke_confirmation_names_device_and_cancel_keeps_pairing()
    {
        await StaTest.Run(() =>
        {
            using var viewModel = Create(out var devices);
            string? askedAbout = null;
            viewModel.ConfirmDeviceRevocation = name =>
            {
                askedAbout = name;
                return Task.FromResult(false);
            };

            viewModel.RevokeDeviceAsync("d1").GetAwaiter().GetResult();

            Assert.Equal("My iPhone", askedAbout);
            Assert.Equal(0, devices.Revocations);
            Assert.Single(devices.List());
            Assert.Single(viewModel.Devices);
        });
    }

    [Fact]
    public async Task Confirmed_revocation_removes_exactly_that_device_and_refreshes_the_list()
    {
        await StaTest.Run(() =>
        {
            using var viewModel = Create(out var devices);
            devices.Items.Add(
                new PairedDeviceInfo("d2", "Other phone", "hash2", DateTimeOffset.UtcNow));
            viewModel.ConfirmDeviceRevocation = _ => Task.FromResult(true);

            viewModel.RevokeDeviceAsync("d1").GetAwaiter().GetResult();

            Assert.Equal(1, devices.Revocations);
            Assert.DoesNotContain(devices.List(), device => device.DeviceId == "d1");
            Assert.Contains(devices.List(), device => device.DeviceId == "d2");
            Assert.Single(viewModel.Devices);
            Assert.Equal("Other phone", viewModel.Devices.Single().Name);
        });
    }

    [Fact]
    public async Task Null_device_id_never_reaches_confirmation_or_revocation()
    {
        await StaTest.Run(() =>
        {
            using var viewModel = Create(out var devices);
            var asked = false;
            viewModel.ConfirmDeviceRevocation = _ =>
            {
                asked = true;
                return Task.FromResult(true);
            };

            viewModel.RevokeDeviceAsync(null).GetAwaiter().GetResult();

            Assert.False(asked);
            Assert.Equal(0, devices.Revocations);
        });
    }

    private static MainViewModel Create(out FakeDevices devices)
    {
        devices = new FakeDevices();
        var controller = new AppController(
            new FakeSettingsService(),
            new FakeValidator(),
            new ClipCatalog(),
            new FakePairingService(),
            devices,
            new FakeFirewallService(),
            new FakeClipPreviewProvider());
        // Tests must use the internal overload: the public constructor reads
        // Application.Current.Dispatcher, which is null outside a WPF app.
        return new MainViewModel(
            controller, () => { }, Dispatcher.CurrentDispatcher, TimeProvider.System);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettingsData Current { get; private set; } = new();

        public void Update(Action<AppSettingsData> mutate) => mutate(Current);

        public void Replace(AppSettingsData data) => Current = data;

        public bool Save() => true;
    }

    private sealed class FakeValidator : IFolderValidator
    {
        public FolderValidationResult Validate(string? path) =>
            FolderValidationResult.Ok;
    }

    private sealed class FakePairingService : IPairingService
    {
        public PairingOffer? CurrentOffer => null;

        public PairingOffer NewPairingOffer() =>
            throw new NotSupportedException();

        public PairExchangeResult Exchange(string? code, string? name) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDevices : IDeviceRegistry
    {
        public List<PairedDeviceInfo> Items { get; } =
        [
            new PairedDeviceInfo("d1", "My iPhone", "hash", DateTimeOffset.UtcNow),
        ];

        public int Revocations { get; private set; }

        public PairedDeviceInfo Register(string name, string tokenHash) =>
            throw new NotSupportedException();

        public PairedDeviceInfo? ValidateToken(string token) => null;

        public bool Revoke(string deviceId)
        {
            var existed = Items.RemoveAll(d => d.DeviceId == deviceId) > 0;
            if (existed) Revocations++;
            return existed;
        }

        public IReadOnlyList<PairedDeviceInfo> List() => Items;
    }

    private sealed class FakeFirewallService : IWindowsFirewallService
    {
        public FirewallStatus Check(string executablePath, int port) =>
            new(FirewallRuleState.Ready, "Ready", false);

        public FirewallStatus Check(string executablePath,
            IReadOnlyCollection<int> ports) =>
            new(FirewallRuleState.Ready, "Ready", false);

        public Task<FirewallStatus> EnsurePrivateRuleAsync(
            string executablePath, int port) =>
            Task.FromResult(Check(executablePath, port));

        public Task<FirewallStatus> EnsurePrivateRuleAsync(
            string executablePath, IReadOnlyCollection<int> ports) =>
            Task.FromResult(Check(executablePath, ports));
    }

    private sealed class FakeClipPreviewProvider : IClipPreviewProvider
    {
        public bool SupportsThumbnails => false;

        public TimeSpan? GetDuration(string fullPath) => null;

        public byte[]? GetThumbnail(ClipEntry entry) => null;
    }
}

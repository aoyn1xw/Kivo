using Kivo.Core.Devices;
using Kivo.Core.Models;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Xunit;

namespace Kivo.Tests;

public sealed class DevicePersistenceTests : IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public void Failed_registration_does_not_claim_a_persisted_device_or_change_existing_credentials()
    {
        var store = new UnavailableSettingsStore(_folder.Path);
        var settings = new SettingsService(store);
        var devices = new DeviceRegistry(settings);
        var original = devices.Register("Existing phone", Hashing.Sha256Hex("existing-token"));
        var serverId = settings.Current.ServerId;
        store.IsUnavailable = true;

        var error = Assert.Throws<IOException>(() => devices.Register("New phone", Hashing.Sha256Hex("new-token")));

        Assert.Contains("could not be saved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.DeviceId, Assert.Single(devices.List()).DeviceId);
        Assert.Equal(original.DeviceId, devices.ValidateToken("existing-token")?.DeviceId);
        Assert.Null(devices.ValidateToken("new-token"));
        var persisted = new JsonSettingsStore(_folder.Path).Load();
        Assert.Equal(serverId, persisted.ServerId);
        Assert.Equal(original.DeviceId, Assert.Single(persisted.Devices).DeviceId);
    }

    [Fact]
    public void Failed_revocation_reports_failure_and_retry_revokes_persistently()
    {
        var store = new UnavailableSettingsStore(_folder.Path);
        var settings = new SettingsService(store);
        var devices = new DeviceRegistry(settings);
        var original = devices.Register("Phone", Hashing.Sha256Hex("test-token"));
        store.IsUnavailable = true;

        Assert.False(devices.Revoke(original.DeviceId));

        Assert.Equal(original.DeviceId, devices.ValidateToken("test-token")?.DeviceId);
        Assert.Equal(original.DeviceId, Assert.Single(new JsonSettingsStore(_folder.Path).Load().Devices).DeviceId);
        store.IsUnavailable = false;

        Assert.True(devices.Revoke(original.DeviceId));

        Assert.Null(devices.ValidateToken("test-token"));
        Assert.Empty(devices.List());
        Assert.Empty(new JsonSettingsStore(_folder.Path).Load().Devices);
    }

    private sealed class UnavailableSettingsStore(string baseDirectory) : ISettingsStore
    {
        private readonly JsonSettingsStore _inner = new(baseDirectory);
        public bool IsUnavailable { get; set; }
        public AppSettingsData Load() => _inner.Load();
        public void Save(AppSettingsData data)
        {
            if (IsUnavailable) throw new IOException("Test settings storage unavailable");
            _inner.Save(data);
        }
    }

    public void Dispose() => _folder.Dispose();
}

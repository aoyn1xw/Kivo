using Kivo.Core.Models;
using Kivo.Core.Settings;
using Xunit;

namespace Kivo.Tests;

public sealed class SettingsTransactionTests : IDisposable
{
    private readonly TempFolder _folder = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_save_preserves_in_memory_configuration_identity_devices_and_disk(bool accessDenied)
    {
        var store = new FailingSettingsStore(_folder.Path);
        var original = new AppSettingsData
        {
            RecordingsFolder = Path.Combine(_folder.Path, "original"),
            ServerId = "established-server",
            Port = 6000,
            Devices = [new PairedDeviceInfo("existing-phone", "My phone", "established-token-hash", DateTimeOffset.UtcNow)],
        };
        store.Save(original);
        var settings = new SettingsService(store);
        store.Failure = accessDenied ? new UnauthorizedAccessException("Test denied") : new IOException("Test full disk");

        Assert.False(settings.TryUpdate(data =>
        {
            RecordingConfiguration.Replace(data,
                [new RecordingDirectory { Path = Path.Combine(_folder.Path, "replacement"), Source = "obs" }], true);
            data.ServerId = "replacement-server";
            data.Port = 7000;
            data.Devices.Clear();
        }));

        AssertOriginal(settings.Current, original);
        AssertOriginal(new JsonSettingsStore(_folder.Path).Load(), original);
        Assert.Empty(settings.Current.RecordingDirectories);
        Assert.False(settings.Current.OnboardingCompleted);
    }

    [Fact]
    public void Successful_update_commits_one_snapshot_and_does_not_retain_the_mutators_reference()
    {
        var settings = new SettingsService(new JsonSettingsStore(_folder.Path));
        AppSettingsData? captured = null;
        var root = Path.Combine(_folder.Path, "recordings");

        Assert.True(settings.TryUpdate(data =>
        {
            captured = data;
            RecordingConfiguration.Replace(data, [new RecordingDirectory { Path = root, Source = "nvidia" }], true);
        }));
        captured!.RecordingsFolder = "subsequent external change";
        captured.RecordingDirectories.Clear();

        Assert.Equal(root, settings.Current.RecordingsFolder);
        Assert.Equal("nvidia", Assert.Single(settings.Current.RecordingDirectories).Source);
        Assert.True(settings.Current.OnboardingCompleted);
        var reloaded = new JsonSettingsStore(_folder.Path).Load();
        Assert.Equal(root, reloaded.RecordingsFolder);
        Assert.Single(reloaded.RecordingDirectories);
    }

    [Fact]
    public void Legacy_update_still_swallows_write_errors_without_partially_changing_the_snapshot()
    {
        var store = new FailingSettingsStore(_folder.Path);
        var settings = new SettingsService(store);
        var originalId = settings.Current.ServerId;
        store.Failure = new IOException("Test full disk");

        settings.Update(data => data.ServerId = "discarded");

        Assert.Equal(originalId, settings.Current.ServerId);
    }

    private static void AssertOriginal(AppSettingsData actual, AppSettingsData original)
    {
        Assert.Equal(original.RecordingsFolder, actual.RecordingsFolder);
        Assert.Equal("established-server", actual.ServerId);
        Assert.Equal(6000, actual.Port);
        var device = Assert.Single(actual.Devices);
        Assert.Equal("existing-phone", device.DeviceId);
        Assert.Equal("established-token-hash", device.TokenHash);
    }

    private sealed class FailingSettingsStore(string baseDirectory) : ISettingsStore
    {
        private readonly JsonSettingsStore _inner = new(baseDirectory);
        public Exception? Failure { get; set; }
        public AppSettingsData Load() => _inner.Load();
        public void Save(AppSettingsData data)
        {
            if (Failure is not null) throw Failure;
            _inner.Save(data);
        }
    }

    public void Dispose() => _folder.Dispose();
}

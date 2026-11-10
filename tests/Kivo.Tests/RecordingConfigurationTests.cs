using Kivo.Core.Models;
using Kivo.Core.Settings;
using Xunit;

namespace Kivo.Tests;

public sealed class RecordingConfigurationTests : IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public void Legacy_settings_keep_primary_folder_identity_and_devices()
    {
        var file = Path.Combine(_folder.Path, "settings.json");
        File.WriteAllText(file, """
            {"recordingsFolder":"C:\\Videos\\Captures","serverId":"existing-server","port":6000,
             "devices":[{"deviceId":"phone","name":"My phone","tokenHash":"existing-hash","createdAtUtc":"2026-01-01T00:00:00Z"}]}
            """);
        var data = new JsonSettingsStore(_folder.Path).Load();

        var directories = RecordingConfiguration.GetDirectories(data);

        Assert.Single(directories);
        Assert.Equal(@"C:\Videos\Captures", directories[0].Path);
        Assert.Equal("custom", directories[0].Source);
        Assert.Empty(data.RecordingDirectories);
        Assert.Equal("existing-server", data.ServerId);
        Assert.Equal("existing-hash", data.Devices[0].TokenHash);
        Assert.Equal(6000, data.Port);
        Assert.True(data.CloseToTray);
    }

    [Fact]
    public void Multiple_sources_and_onboarding_preferences_roundtrip_without_changing_credentials()
    {
        var data = new AppSettingsData { ServerId = "preserved", StartMinimized = true, CloseToTray = false };
        RecordingConfiguration.Replace(data,
        [
            new RecordingDirectory { Path = Path.Combine(_folder.Path, "first"), Source = "nvidia" },
            new RecordingDirectory { Path = Path.Combine(_folder.Path, "second"), Source = "obs" },
        ], completeOnboarding: true);
        var store = new JsonSettingsStore(_folder.Path);
        store.Save(data);

        var loaded = store.Load();

        Assert.Equal(2, loaded.RecordingDirectories.Count);
        Assert.Equal("nvidia", loaded.RecordingDirectories[0].Source);
        Assert.Equal("obs", loaded.RecordingDirectories[1].Source);
        Assert.Equal(loaded.RecordingDirectories[0].Path, loaded.RecordingsFolder);
        Assert.True(loaded.OnboardingCompleted);
        Assert.True(loaded.StartMinimized);
        Assert.False(loaded.CloseToTray);
        Assert.Equal("preserved", loaded.ServerId);
        Assert.False(Directory.Exists(loaded.RecordingsFolder));
    }

    [Theory]
    [InlineData("clips", "clips")]
    [InlineData("clips", "clips/child")]
    [InlineData("clips/child", "clips")]
    [InlineData("clips", "CLIPS")]
    public void Duplicate_and_nested_roots_are_rejected(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => RecordingConfiguration.Normalize(
        [
            new RecordingDirectory { Path = Path.Combine(_folder.Path, first) },
            new RecordingDirectory { Path = Path.Combine(_folder.Path, second) },
        ]));
    }

    [Fact]
    public void Sibling_names_are_distinct_and_paths_are_canonicalized()
    {
        var roots = RecordingConfiguration.Normalize(
        [
            new RecordingDirectory { Path = Path.Combine(_folder.Path, "clips", ".") },
            new RecordingDirectory { Path = Path.Combine(_folder.Path, "clips-backup") },
        ]);

        Assert.Equal(2, roots.Count);
        Assert.Equal(Path.Combine(_folder.Path, "clips"), roots[0].Path);
        Assert.False(RecordingConfiguration.IsWithinRoot(roots[1].Path, roots[0].Path));
    }

    [Theory]
    [InlineData("relative", "custom")]
    [InlineData("C:\\Clips", "unknown")]
    public void Invalid_path_or_source_is_rejected(string path, string source)
    {
        Assert.Throws<ArgumentException>(() => RecordingConfiguration.Normalize(
            [new RecordingDirectory { Path = path, Source = source }]));
    }

    [Fact]
    public void Failed_replacement_does_not_change_the_saved_configuration()
    {
        var data = new AppSettingsData { RecordingsFolder = _folder.Path, OnboardingCompleted = true };

        Assert.Throws<ArgumentException>(() => RecordingConfiguration.Replace(data,
            [new RecordingDirectory { Path = "relative" }]));

        Assert.Equal(_folder.Path, data.RecordingsFolder);
        Assert.Empty(data.RecordingDirectories);
        Assert.True(data.OnboardingCompleted);
    }

    [Fact]
    public void Removing_last_root_clears_legacy_primary()
    {
        var data = new AppSettingsData { RecordingsFolder = _folder.Path };

        RecordingConfiguration.Replace(data, []);

        Assert.Empty(RecordingConfiguration.GetDirectories(data));
        Assert.Null(data.RecordingsFolder);
    }

    public void Dispose() => _folder.Dispose();
}

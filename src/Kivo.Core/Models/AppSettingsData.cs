using System.Text.Json.Serialization;

namespace Kivo.Core.Models;

/// <summary>
/// Persisted application settings. Stored under %LocalAppData%\ShadowPlay.
/// Bearer tokens are never persisted in raw form - only hashes (see PairedDeviceInfo).
/// </summary>
public sealed class AppSettingsData
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("recordingsFolder")]
    public string? RecordingsFolder { get; set; }

    [JsonPropertyName("recordingDirectories")]
    public List<RecordingDirectory> RecordingDirectories { get; set; } = [];

    [JsonPropertyName("onboardingCompleted")]
    public bool OnboardingCompleted { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; } = true;

    [JsonPropertyName("serverId")]
    public string ServerId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("port")]
    public int Port { get; set; } = DefaultPort;

    [JsonPropertyName("sharingEnabled")]
    public bool SharingEnabled { get; set; } = true;

    [JsonPropertyName("devices")]
    public List<PairedDeviceInfo> Devices { get; set; } = [];

    public const int DefaultPort = 5177;
}

/// <summary>A user-confirmed recording root and its capture software identifier.</summary>
public sealed class RecordingDirectory
{
    public string Path { get; set; } = string.Empty;

    public string Source { get; set; } = "custom";
}

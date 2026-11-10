using Kivo.Core.Clips;
using Kivo.Core.Devices;
using Kivo.Core.Networking;
using Kivo.Core.Pairing;
using Kivo.Core.Settings;
using Kivo.Core.Validation;
using Kivo.Windows.Infrastructure;
using Kivo.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kivo.Windows.Bootstrap;

public static class AppHostFactory
{
    public const string AppDataFolderName = "ShadowPlay";
    public static string DefaultBaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataFolderName);
    public static string LogsDirectory => Path.Combine(DefaultBaseDirectory, "logs");

    public static IHost Build(CliOptions cli)
    {
        var builder = Host.CreateApplicationBuilder();
        var baseDirectory = cli.DataDirectory ?? DefaultBaseDirectory;
        builder.Logging.ClearProviders();
        builder.Logging.AddKivoFileLog(Path.Combine(baseDirectory, "logs"));
        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(baseDirectory));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<IFolderValidator, FolderValidator>();
        services.AddSingleton<ClipCatalog>();
        services.AddSingleton<IClipCatalog>(sp => sp.GetRequiredService<ClipCatalog>());
        services.AddSingleton<IClipStore>(sp => sp.GetRequiredService<ClipCatalog>());
        services.AddSingleton<ISecretGenerator, SecureTokenGenerator>();
        services.AddSingleton<IDeviceRegistry, DeviceRegistry>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IWindowsFirewallService, WindowsFirewallService>();
        services.AddSingleton<IHttpsIdentityService, HttpsIdentityService>();
        services.AddSingleton<IClipPreviewProvider>(_ => new WindowsClipPreviewProvider(Path.Combine(baseDirectory, "thumbnails")));
        services.AddSingleton<AppController>();
        var host = builder.Build();
        if (cli.Folder is not null || cli.Port is not null)
        {
            host.Services.GetRequiredService<ISettingsService>().Update(data =>
            {
                if (cli.Folder is not null)
                {
                    data.RecordingsFolder = Path.GetFullPath(cli.Folder);
                    data.RecordingDirectories.Clear();
                }
                if (cli.Port is not null) data.Port = cli.Port.Value;
            });
        }

        return host;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kivo.Core.Settings;
using Kivo.Core.Validation;
using Kivo.Windows.Bootstrap;
using Kivo.Windows.Services;

namespace Kivo.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WinUI owns the Application lifetime; RequestExitAsync disposes the tray, host and guard before Exit.")]
public partial class App : Application
{
    private MainWindow? _window;
    private IHost? _host;
    private SingleInstanceGuard? _guard;
    private DesktopTray? _tray;
    private DispatcherQueue? _dispatcher;
    private bool _exiting;

    internal AppController Controller => _host!.Services.GetRequiredService<AppController>();
    internal ISettingsService Settings => _host!.Services.GetRequiredService<ISettingsService>();
    internal IFolderValidator Validator => _host!.Services.GetRequiredService<IFolderValidator>();
    internal bool IsExiting => _exiting;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var cli = CliOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _guard = SingleInstanceGuard.TryAcquire();
        if (!_guard.Acquired) { Exit(); return; }
        _guard.ActivationRequested += ActivateFromOtherInstance;
        if (cli.SmokeTest)
        {
            Environment.ExitCode = await Task.Run(() => SmokeRunner.RunAsync(cli));
            _guard.Dispose();
            Exit();
            return;
        }

        try
        {
            _host = AppHostFactory.Build(cli);
            await _host.StartAsync();
            _tray = new DesktopTray(this, _dispatcher!);
            var data = Settings.Current;
            if (!cli.Background && !data.StartMinimized) ShowWindow();
            var validConfiguration = await Task.Run(() => RecordingConfiguration.GetDirectories(data)
                .Any(directory => Validator.Validate(directory.Path).IsValid));
            var needsOnboarding = !data.OnboardingCompleted && !validConfiguration;
            if (validConfiguration && !data.OnboardingCompleted)
                Settings.Update(current => current.OnboardingCompleted = true);

            if (needsOnboarding || (!cli.Background && !data.StartMinimized)) ShowWindow(needsOnboarding);
            await Task.Run(Controller.InitializeAsync);
        }
        catch (Exception ex)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogError(ex, "Desktop startup could not initialize local services");
            var message = ex is IOException or UnauthorizedAccessException
                ? "Kivo could not read or save its settings. Check your Windows profile permissions and free space."
                : "Kivo could not initialize its local services. Restart Kivo. Check local logs if this continues.";
            if (_window is not null) _window.ShowError(message);
            else
            {
                // This path must work even if DI/settings construction failed.
                System.Windows.Forms.MessageBox.Show(message, "Kivo could not start",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                _tray?.Dispose();
                _host?.Dispose();
                _guard?.Dispose();
                Exit();
            }
        }
    }

    private void ActivateFromOtherInstance() => _dispatcher?.TryEnqueue(() => ShowWindow());

    internal void ShowWindow(bool onboarding = false)
    {
        if (_exiting || _host is null) return;
        _window ??= new MainWindow(this);
        _window.Show(onboarding);
    }

    internal async Task RequestExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.StopUpdates();
        _tray?.Dispose();
        try
        {
            if (_host is not null)
            {
                await Controller.DisposeAsync();
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        finally
        {
            if (_guard is not null) _guard.ActivationRequested -= ActivateFromOtherInstance;
            _guard?.Dispose();
            _window?.Close();
            Exit();
        }
    }
}

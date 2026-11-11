using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kivo.Desktop;

public sealed partial class MainWindow
{
    private void ShowSettings()
    {
        var data = _app.Settings.Current;
        var port = new NumberBox
        {
            Header = "HTTP port",
            Minimum = 1,
            Maximum = 65535,
            Value = data.Port,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var minimized = new ToggleSwitch { Header = "Start in the system tray", IsOn = data.StartMinimized };
        var close = new ToggleSwitch { Header = "Keep sharing when the window closes", IsOn = data.CloseToTray };
        var startup = new ToggleSwitch { Header = "Launch Kivo when I sign in to Windows" };
        try { startup.IsOn = StartupPreference.IsEnabled(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { startup.IsEnabled = false; }
        var previousStartup = startup.IsOn;
        var saved = Text("", 13, true);
        var save = ActionButton("Save preferences", async () =>
        {
            if (double.IsNaN(port.Value) || port.Value != Math.Truncate(port.Value) || port.Value is < 1 or > 65535)
            { ShowError("Enter a whole port number between 1 and 65535."); return; }
            if (!close.IsOn && _app.Settings.Current.CloseToTray)
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    RequestedTheme = ElementTheme.Dark,
                    Title = "Exit when the window closes?",
                    Content = "Closing Kivo will stop the server and active transfers. Keep this option on to continue sharing in the tray.",
                    PrimaryButtonText = "Use this preference",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var selectedPort = (int)port.Value;
            var previousPort = _app.Settings.Current.Port;
            var persisted = _app.Settings.TryUpdate(current =>
            {
                current.StartMinimized = minimized.IsOn;
                current.CloseToTray = close.IsOn;
                current.Port = selectedPort;
            });
            if (!persisted) { ShowError("Preferences could not be saved. Check profile permissions and free space."); return; }
            if (startup.IsEnabled && startup.IsOn != previousStartup)
            {
                try
                {
                    StartupPreference.SetEnabled(startup.IsOn);
                    previousStartup = startup.IsOn;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    startup.IsOn = previousStartup;
                    ShowError("Other preferences were saved, but Windows startup could not be changed. Check Windows account restrictions and retry.");
                }
            }
            if (selectedPort != previousPort) await Task.Run(_app.Controller.RestartSharingAsync);
            saved.Text = "Preferences saved.";
            if (_app.Controller.Status.State == Kivo.Windows.Services.SharingState.Error) ShowError(_app.Controller.Status.Detail ?? "The new port could not be opened. Choose an unused port and retry.");
        }, true);
        PageHost.Content = Stack(Heading("Settings", "Simple controls for a local-first companion."),
            Card(Stack(Text("RECORDINGS & DEVICES", 11, true), GoButton("Recording folders and capture software", "recordings"),
                GoButton("Pairing and device access", "devices"),
                ActionButton("Restart onboarding", () => { ShowSourceStep(); return Task.CompletedTask; }),
                Text("Onboarding can add a source or update an existing folder. It keeps your paired devices and configured folders.", 13, true))),
            Card(Stack(Text("WINDOW & STARTUP", 11, true), startup, minimized, close,
                Text("Tray Exit always shuts down sharing. Startup runs in the background when a recording folder is configured.", 13, true))),
            Card(Stack(Text("NETWORK", 11, true), port,
                Text("HTTPS uses the next port when available. Changing ports restarts sharing and requires updating the address on paired phones. Firewall access is limited to private networks.", 13, true),
                GoButton("Server status and connection diagnostics", "overview"))), save, saved,
            Card(Stack(Text("ABOUT KIVO", 11, true), Text($"Kivo {typeof(App).Assembly.GetName().Version?.ToString(3)} · WinUI 3", 17),
                Text("Local recording server and mobile pairing companion. No account, cloud uploads, analytics or telemetry.", 13, true),
                ActionButton("Exit Kivo", _app.RequestExitAsync))));
    }
}

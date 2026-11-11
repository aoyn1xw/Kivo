using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using System.Net.NetworkInformation;
using Kivo.Core.Networking;
using Kivo.Windows.Services;

namespace Kivo.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly App _app;
    private bool _commandRunning;
    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _page = "overview";
    private bool _visible;
    private int _refreshQueued;
    private Action? _refreshPage;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Navigation.PaneOpened += (_, _) => FooterCopy.Visibility = Visibility.Visible;
        Navigation.PaneClosed += (_, _) => FooterCopy.Visibility = Visibility.Collapsed;
        PageHost.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) =>
            PageScroll.ChangeView(0, 0, null, disableAnimation: true));
        Title = "Kivo";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1040, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = true;
        AppWindow.TitleBar.BackgroundColor = global::Windows.UI.Color.FromArgb(255, 9, 11, 15);
        AppWindow.TitleBar.ForegroundColor = global::Windows.UI.Color.FromArgb(255, 242, 244, 248);
        AppWindow.TitleBar.ButtonBackgroundColor = global::Windows.UI.Color.FromArgb(255, 9, 11, 15);
        AppWindow.TitleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(255, 242, 244, 248);
        AppWindow.Closing += OnClosing;
        _pairingTimer.Tick += (_, _) => _refreshPage?.Invoke();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    internal void Show(bool onboarding = false)
    {
        if (!_visible)
        {
            _visible = true;
            Navigation.IsPaneVisible = true;
            _app.Controller.StateChanged += QueueRefresh;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            Navigate(_page);
        }

        AppWindow.Show();
        Activate();
        if (onboarding) ShowSourceStep();
    }

    internal void StopUpdates()
    {
        _visible = false;
        _pairingTimer.Stop();
        _app.Controller.StateChanged -= QueueRefresh;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _refreshPage = null;
        _displayedPairing = null;
        PageHost.Content = null;
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_app.IsExiting) return;
        args.Cancel = true;
        if (_app.Settings.Current.CloseToTray)
        {
            StopUpdates();
            AppWindow.Hide();
        }
        else _ = _app.RequestExitAsync();
    }

    private void OnNetworkChanged(object? sender, EventArgs args) => QueueRefresh();

    private void QueueRefresh()
    {
        if (!_visible || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_visible) _refreshPage?.Invoke();
        });
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PageHost is null) return;
        Navigate(args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "overview");
    }

    private void Navigate(string page)
    {
        _page = page;
        _pairingTimer.Stop();
        _refreshPage = null;
        _displayedPairing = null;
        Notice.IsOpen = false;
        switch (page)
        {
            case "recordings": ShowRecordings(); break;
            case "devices": ShowDevices(); _pairingTimer.Start(); break;
            case "settings": ShowSettings(); break;
            default: ShowOverview(); break;
        }
    }

    internal void ShowError(string message)
    {
        Notice.Title = "Action needed";
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Error;
        Notice.IsOpen = true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_commandRunning) return;
        _commandRunning = true;
        try
        {
            Navigation.IsEnabled = false;
            Notice.IsOpen = false;
            await action();
        }
        catch (Exception ex)
        {
            ShowError(ex switch
            {
                UnauthorizedAccessException => "Access was denied. Choose a readable folder and check your Windows profile permissions.",
                IOException => "The file or settings could not be read or saved. Check the drive, free space and permissions.",
                ArgumentException => ex.Message,
                _ => "This action could not finish. Retry, or restart Kivo from the tray if the problem continues.",
            });
        }
        finally
        {
            Navigation.IsEnabled = true;
            _commandRunning = false;
            QueueRefresh();
        }
    }

    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string value, double size = 14, bool secondary = false) => new()
    {
        Text = value,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = (FontFamily)Application.Current.Resources["UIFont"],
        Foreground = ResourceBrush(secondary ? "SecondaryText" : "PrimaryText"),
    };

    private static TextBlock Technical(string value, double size = 14)
    {
        var text = Text(value, size);
        text.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        text.IsTextSelectionEnabled = true;
        return text;
    }

    private static StackPanel Stack(params UIElement[] elements)
    {
        var stack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var element in elements) stack.Children.Add(element);
        return stack;
    }

    private static StackPanel Heading(string title, string subtitle) => Stack(Text(title, 28), Text(subtitle, 14, true));

    private static Border Card(UIElement content) => new()
    {
        Background = ResourceBrush("Surface"),
        BorderBrush = ResourceBrush("BorderColor"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(22),
        Child = content,
    };

    private Button ActionButton(string label, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 36 };
        AutomationProperties.SetName(button, label);
        if (accent)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 77, 142, 255));
            button.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black);
        }
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private Button GoButton(string label, string page) => ActionButton(label, () =>
    {
        Navigation.SelectedItem = page == "settings" ? Navigation.SettingsItem
            : Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == page);
        return Task.CompletedTask;
    });

    private void ShowOverview()
    {
        var status = Text("Starting", 22);
        var detail = Text("", 14, true);
        var address = Technical("");
        var network = Text("", 13, true);
        var firewall = Text("", 13, true);
        var count = Text("", 20);
        var devices = Text("", 20);
        var toggle = ActionButton("Start sharing", () => Task.Run(_app.Controller.ToggleSharingAsync), true);
        var repair = ActionButton("Allow private LAN access", async () => { await _app.Controller.ConfigureFirewallAsync(); });
        var page = Stack(
            Heading("Your recordings, within reach", "Keep Kivo running on your PC. Pair your phone on the same private network."),
            Card(Stack(Text("LOCAL SERVER", 11, true), status, detail, address, network, toggle)),
            Card(Stack(Text("RECORDING LIBRARY", 11, true), count, Text("Completed MP4 files. Original quality. Nothing is uploaded to a cloud.", 13, true), GoButton("Manage recordings", "recordings"))),
            Card(Stack(Text("PAIRED DEVICES", 11, true), devices, GoButton("Pair a device", "devices"))),
            Card(Stack(Text("CONNECTION CHECK", 11, true), firewall, repair)));
        page.Spacing = 20;
        PageHost.Content = page;
        _refreshPage = () =>
        {
            var controller = _app.Controller;
            var state = controller.Status;
            status.Text = state.State switch
            {
                SharingState.Running => "Sharing is running",
                SharingState.Starting => "Starting local server…",
                SharingState.Paused => "Sharing is stopped",
                SharingState.NotConfigured => "Choose your recording folder",
                _ => "Sharing needs attention",
            };
            status.Foreground = new SolidColorBrush(state.State == SharingState.Running
                ? global::Windows.UI.Color.FromArgb(255, 110, 213, 163)
                : global::Windows.UI.Color.FromArgb(255, 242, 196, 111));
            detail.Text = state.Detail ?? (state.State == SharingState.Running ? "Ready for paired devices on your local network." : "Use Start sharing when you're ready.");
            var endpoint = LocalIpLocator.FindBestEndpoint();
            address.Text = state.State == SharingState.Running
                ? $"http://{endpoint?.Address.ToString() ?? "127.0.0.1"}:{state.Port}"
                : $"Configured HTTP port  {controller.ConfiguredPort}";
            network.Text = endpoint is null ? "No suitable LAN address found. Connect to a private Wi-Fi or Ethernet network."
                : $"{endpoint.InterfaceName} · LAN address available; reachability depends on your network and firewall.";
            count.Text = $"{CountLabel(controller.RecordingCount, "recording")} · {CountLabel(controller.RecordingDirectories.Count, "folder")}";
            devices.Text = CountLabel(controller.PairedDevices.Count, "paired device");
            firewall.Text = controller.FirewallStatus.Detail;
            repair.Visibility = controller.FirewallStatus.NeedsSetup ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = controller.IsSharingRunning ? "Stop sharing" : "Start sharing";
            AutomationProperties.SetName(toggle, (string)toggle.Content);
            toggle.IsEnabled = controller.IsConfigured && state.State != SharingState.Starting;
        };
        _refreshPage();
    }

    private static string CountLabel(int count, string singular) => $"{count:N0} {singular}{(count == 1 ? "" : "s")}";
}

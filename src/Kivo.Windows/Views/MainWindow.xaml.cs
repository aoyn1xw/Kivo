using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Kivo.Windows.Services;
using Kivo.Windows.ViewModels;

namespace Kivo.Windows.Views;

public partial class MainWindow : Window
{
    private readonly App _app;

    public MainWindow(App app, MainViewModel viewModel)
    {
        InitializeComponent();
        // The view owns confirmation dialogs; devices can never be revoked here
        // without an explicit confirmation naming the device.
        viewModel.ConfirmDeviceRevocation ??= ConfirmDeviceRevocation;
        _app = app;
        DataContext = viewModel;
    }
    protected override void OnClosed(EventArgs e)
    {
        ((MainViewModel)DataContext).Dispose();
        base.OnClosed(e);
    }

    private Task<bool> ConfirmDeviceRevocation(string deviceName)
    {
        var result = System.Windows.MessageBox.Show(
            $"Revoke {deviceName}? This device will immediately lose access to your recordings and will need a new pairing code to connect again.",
            "Revoke device",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return Task.FromResult(result == MessageBoxResult.OK);
    }

    /// <summary>Closing hides to tray; only explicit Exit terminates the process.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}

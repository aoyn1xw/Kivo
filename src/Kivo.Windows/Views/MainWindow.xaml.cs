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
        _app = app;
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        ((MainViewModel)DataContext).Dispose();
        base.OnClosed(e);
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

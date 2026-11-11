using Microsoft.UI.Dispatching;
using Forms = System.Windows.Forms;

namespace Kivo.Desktop;

/// <summary>NotifyIcon uses the native UI thread's message pump; no extra polling thread.</summary>
internal sealed class DesktopTray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly System.Drawing.Icon _image;

    public DesktopTray(App app, DispatcherQueue dispatcher)
    {
        _image = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open Kivo", null, (_, _) => dispatcher.TryEnqueue(() => app.ShowWindow()));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => dispatcher.TryEnqueue(async () => await app.RequestExitAsync()));
        _icon = new Forms.NotifyIcon { Text = "Kivo · local recording server", Icon = _image, ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => dispatcher.TryEnqueue(() => app.ShowWindow());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }
}

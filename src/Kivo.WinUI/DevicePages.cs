using Kivo.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace Kivo.Desktop;

public sealed partial class MainWindow
{
    private PairingDisplaySnapshot? _displayedPairing;

    private void ShowDevices()
    {
        var qr = new Image { Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(qr, "Pairing QR code. Manual pairing details are provided below.");
        var qrBorder = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Child = qr, HorizontalAlignment = HorizontalAlignment.Left };
        var manual = Technical("", 16);
        var expiry = Text("", 13, true);
        var known = Stack();
        var regenerate = ActionButton("New pairing code", () =>
        {
            if (!_app.Controller.RegeneratePairingOffer()) ShowError("Start sharing before creating a pairing code.");
            return Task.CompletedTask;
        });
        PageHost.Content = Stack(Heading("Devices", "Pair your phone once. Your recordings stay on your local network."),
            Card(Stack(Text("PAIR ANOTHER DEVICE", 11, true), Text("Open Kivo on your phone → Add PC → Scan QR", 17), qrBorder,
                expiry, Text("Or choose manual pairing on your phone", 14, true), manual, regenerate,
                Text("QR pairing includes the PC's HTTPS certificate pin when TLS is available. Manual pairing retains the existing LAN pairing flow.", 12, true))),
            Text("Paired devices", 20), Text("Paired means authorized. Kivo does not track live device presence.", 13, true), known);
        string? signature = null;
        _refreshPage = () =>
        {
            var controller = _app.Controller;
            var snapshot = controller.PairingSnapshot;
            regenerate.IsEnabled = controller.Status.State == SharingState.Running;
            if (snapshot is null)
            {
                _displayedPairing = null;
                qr.Source = null;
                qrBorder.Visibility = Visibility.Collapsed;
                manual.Text = "";
                expiry.Text = controller.Status.State == SharingState.Running
                    ? "This offer expired or was used. Select New pairing code to pair another device."
                    : "Start sharing from Overview to pair a device.";
            }
            else
            {
                var remaining = snapshot.ExpiresAtUtc - DateTimeOffset.UtcNow;
                expiry.Text = $"Single-use offer · expires in {Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}";
                manual.Text = $"Address  {snapshot.Payload.LanAddress}\nPort     {snapshot.Payload.Port}\nCode     {snapshot.Payload.PairingCode}";
                qrBorder.Visibility = Visibility.Visible;
                if (!ReferenceEquals(snapshot, _displayedPairing))
                {
                    _displayedPairing = snapshot;
                    _ = RenderQrAsync(snapshot, qr);
                }
            }

            var devices = controller.PairedDevices;
            var nextSignature = string.Join('|', devices.Select(device => device.DeviceId));
            if (signature == nextSignature) return;
            signature = nextSignature;
            known.Children.Clear();
            if (devices.Count == 0) known.Children.Add(Card(Text("No devices paired yet. Scan the QR code above to connect your phone.", 14, true)));
            foreach (var device in devices)
            {
                known.Children.Add(Card(Stack(Text(device.Name, 17),
                    Text($"Paired {device.CreatedAtUtc.ToLocalTime():d} · authorized", 13, true),
                    ActionButton($"Revoke {device.Name}", async () =>
                    {
                        var dialog = new ContentDialog
                        {
                            XamlRoot = Root.XamlRoot,
                            RequestedTheme = ElementTheme.Dark,
                            Title = $"Revoke {device.Name}?",
                            Content = "This device will lose access immediately. Pair it again to restore access.",
                            PrimaryButtonText = "Revoke access",
                            CloseButtonText = "Cancel",
                            DefaultButton = ContentDialogButton.Close,
                        };
                        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !controller.RevokeDevice(device.DeviceId))
                            ShowError("Device access could not be revoked. Check settings write permission and free space, then retry. Access remains authorized until revocation succeeds.");
                    }))));
            }
        };
        _refreshPage();
    }

    private async Task RenderQrAsync(PairingDisplaySnapshot snapshot, Image target)
    {
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(snapshot.Payload.ToJson(), QRCodeGenerator.ECCLevel.Q);
                using var png = new PngByteQRCode(data);
                return png.GetGraphic(6);
            });
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (_visible && ReferenceEquals(_displayedPairing, snapshot) && _page == "devices") target.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (_visible && _page == "devices") ShowError("The QR image could not be generated. Use the manual address, port and code, or create a new code.");
        }
    }
}

using Kivo.Windows.ViewModels;
using System.Windows.Threading;
using Xunit;

namespace Kivo.Tests;

public sealed class MainViewModelPairingTests
{
    [Fact]
    public async Task Open_view_refresh_and_restart_keep_qr_and_manual_details_on_the_current_offer()
    {
        await using var fixture = new ControllerPairingFixture();
        await fixture.Controller.StartSharingAsync();
        await StaTest.Run(() =>
        {
            using var vm = new MainViewModel(fixture.Controller, () => { }, Dispatcher.CurrentDispatcher, fixture.Time);
            var first = fixture.Controller.PairingSnapshot!;
            var firstImage = vm.QrImage;
            using (var reopened = new MainViewModel(fixture.Controller, () => { }, Dispatcher.CurrentDispatcher, fixture.Time))
            {
                Assert.Same(first, fixture.Controller.PairingSnapshot);
                Assert.Equal(vm.PairingCode, reopened.PairingCode);
            }
            fixture.Controller.PauseSharingAsync().GetAwaiter().GetResult();
            vm.RefreshAll();
            Assert.Null(vm.QrImage);
            Assert.Empty(vm.PairingCode);
            Assert.False(vm.RegeneratePairingCommand.CanExecute(null));
            fixture.Controller.ResumeSharingAsync().GetAwaiter().GetResult();
            vm.RefreshAll();
            Assert.NotNull(vm.QrImage);
            Assert.NotSame(firstImage, vm.QrImage);
            Assert.NotEqual(first.Payload.PairingCode, vm.PairingCode);
            Assert.Equal(fixture.Controller.PairingSnapshot!.Payload.PairingCode, vm.PairingCode);
            Assert.Contains("10 min", vm.PairingHint);
        });
    }

    [Fact]
    public async Task Refresh_reuses_images_and_clears_consumed_expired_and_paused_codes()
    {
        await using var fixture = new ControllerPairingFixture();
        await fixture.Controller.StartSharingAsync();
        await StaTest.Run(() =>
        {
            using var vm = new MainViewModel(fixture.Controller, () => { }, Dispatcher.CurrentDispatcher, fixture.Time);
            var image = vm.QrImage;
            Assert.NotNull(image);
            vm.RefreshAll();
            Assert.Same(image, vm.QrImage);
            var first = fixture.Controller.PairingSnapshot!;
            Assert.Equal(first.Payload.PairingCode, vm.PairingCode);
            Assert.Equal(first.Payload.LanAddress, vm.PairingAddress);
            Assert.Equal(first.Payload.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.PairingPort);
            Assert.True(vm.HasPairingDetails);
            Assert.True(fixture.Pairing.Exchange(first.Payload.PairingCode, "phone").Success);
            vm.RefreshPairing();
            Assert.Null(vm.QrImage);
            Assert.Contains("no longer available", vm.PairingHint);
            Assert.False(vm.HasPairingDetails);
            Assert.Empty(vm.PairingCode);
            Assert.Empty(vm.PairingAddress);
            Assert.Empty(vm.PairingPort);
            vm.RegeneratePairingCommand.Execute(null);
            Assert.NotNull(vm.QrImage);
            Assert.NotSame(image, vm.QrImage);
            Assert.Equal(fixture.Controller.PairingSnapshot!.Payload.PairingCode, vm.PairingCode);
            Assert.NotEqual(first.Payload.PairingCode, vm.PairingCode);
            fixture.Time.Advance(TimeSpan.FromMinutes(10));
            vm.RefreshPairing();
            Assert.Null(vm.QrImage);
            Assert.Contains("expired", vm.PairingHint);
            Assert.False(vm.HasPairingDetails);
            Assert.Empty(vm.PairingCode);
        });
        await fixture.Controller.PauseSharingAsync();
        await StaTest.Run(() =>
        {
            using var vm = new MainViewModel(fixture.Controller, () => { }, Dispatcher.CurrentDispatcher, fixture.Time);
            Assert.Null(vm.QrImage);
            Assert.False(vm.RegeneratePairingCommand.CanExecute(null));
        });
    }
}

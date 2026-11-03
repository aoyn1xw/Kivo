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
            }
            fixture.Controller.PauseSharingAsync().GetAwaiter().GetResult();
            vm.RefreshAll();
            Assert.Null(vm.QrImage);
            Assert.False(vm.RegeneratePairingCommand.CanExecute(null));
            fixture.Controller.ResumeSharingAsync().GetAwaiter().GetResult();
            vm.RefreshAll();
            Assert.NotNull(vm.QrImage);
            Assert.NotSame(firstImage, vm.QrImage);
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
            vm.RefreshPairing();
            Assert.Null(vm.QrImage);
            Assert.Contains("no longer available", vm.PairingHint);
            vm.RegeneratePairingCommand.Execute(null);
            Assert.NotNull(vm.QrImage);
            Assert.NotSame(image, vm.QrImage);
            fixture.Time.Advance(TimeSpan.FromMinutes(10));
            vm.RefreshPairing();
            Assert.Null(vm.QrImage);
            Assert.Contains("expired", vm.PairingHint);
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

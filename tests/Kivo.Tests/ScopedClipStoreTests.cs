using Kivo.Core.Clips;
using Kivo.Core.Models;
using Xunit;

namespace Kivo.Tests;

public sealed class ScopedClipStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public void Scoped_replacement_preserves_other_roots_and_similar_sibling_names()
    {
        var catalog = new ClipCatalog();
        var first = Entry(_folder.CreateMp4("clips/first.mp4"));
        var sibling = Entry(_folder.CreateMp4("clips-other/second.mp4"));
        catalog.AddOrUpdate(first);
        catalog.AddOrUpdate(sibling);
        var scoped = new ScopedClipStore(catalog, Path.Combine(_folder.Path, "clips"));

        Assert.Single(scoped.Snapshot());
        Assert.Null(scoped.FindByPath(sibling.FullPath));
        Assert.False(scoped.RemoveByPath(sibling.FullPath));
        Assert.Throws<ArgumentException>(() => scoped.AddOrUpdate(sibling));
        scoped.ReplaceAll([]);

        Assert.Equal(1, catalog.Count);
        Assert.NotNull(catalog.Find(sibling.Id));
    }

    [Fact]
    public async Task Independent_monitors_do_not_remove_each_others_recordings()
    {
        var first = _folder.CreateMp4("first/one.mp4");
        var second = _folder.CreateMp4("second/two.mp4");
        var catalog = new ClipCatalog();
        var options = new MonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(30),
            ReconcileInterval = TimeSpan.FromMilliseconds(80),
            StabilityWindow = TimeSpan.FromMilliseconds(50),
            DebounceDelay = TimeSpan.FromMilliseconds(10),
        };
        var firstRoot = Path.GetDirectoryName(first)!;
        var secondRoot = Path.GetDirectoryName(second)!;
        await using var firstMonitor = new ClipMonitorService(
            () => firstRoot, new ScopedClipStore(catalog, firstRoot), TimeProvider.System, options);
        await using var secondMonitor = new ClipMonitorService(
            () => secondRoot, new ScopedClipStore(catalog, secondRoot), TimeProvider.System, options);
        await firstMonitor.StartAsync();
        await secondMonitor.StartAsync();
        await WaitForCountAsync(catalog, 2);

        File.Delete(first);
        await WaitForCountAsync(catalog, 1);
        await Task.Delay(250); // Exercise several independent reconciliation passes.

        Assert.NotNull(catalog.FindByPath(second));
        Assert.Null(catalog.FindByPath(first));
    }

    [Fact]
    public async Task Startup_returns_without_posting_a_scan_to_the_callers_context()
    {
        _folder.CreateMp4("existing.mp4");
        var catalog = new ClipCatalog();
        await using var monitor = new ClipMonitorService(() => _folder.Path, catalog, TimeProvider.System);
        var context = new CountingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task started;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            started = monitor.StartAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.True(started.IsCompletedSuccessfully);
        Assert.Equal(0, context.Posts);
        await monitor.StopAsync();
        Assert.False(monitor.IsRunning);
        await monitor.StartAsync();
        Assert.True(monitor.IsRunning);
    }

    private static async Task WaitForCountAsync(ClipCatalog catalog, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (catalog.Count != count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(count, catalog.Count);
    }

    private static ClipEntry Entry(string path) => new(
        ClipIdCalculator.Calculate(path), Path.GetFileName(path), path,
        new FileInfo(path).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(path)));

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => Posts++;
    }

    public void Dispose() => _folder.Dispose();
}

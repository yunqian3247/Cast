using serial.Serial;

namespace serial.Tests;

public sealed class PortPresenceTests
{
    [Fact]
    public async Task PresenceMonitorDetectsReplacementWithSameComNumber()
    {
        var catalog = new ChangingCatalog();
        using var monitor = new PortPresenceMonitor(catalog, TimeSpan.FromMilliseconds(20));
        var change = new TaskCompletionSource<PortsChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, e) => change.TrySetResult(e);
        catalog.Ports = [new("COM5", "COM5 - Replacement", "new-device")];
        var result = await change.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new-device", result.Current.Single().DeviceInstanceId);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public async Task PresenceMonitorReportsEnumerationFailureAndRecovers()
    {
        var catalog = new ChangingCatalog { Fail = true };
        using var monitor = new PortPresenceMonitor(catalog, TimeSpan.FromMilliseconds(20));
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var change = new TaskCompletionSource<PortsChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.EnumerationFailed += (_, e) => error.TrySetResult(e);
        monitor.Changed += (_, e) => change.TrySetResult(e);
        Assert.IsType<IOException>(await error.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        catalog.Fail = false;
        Assert.Equal("COM5", (await change.Task.WaitAsync(TimeSpan.FromSeconds(5))).Added.Single());
    }

    private sealed class ChangingCatalog : IPortCatalog
    {
        public volatile bool Fail;
        public IReadOnlyList<PortInfo> Ports = [new("COM5", "COM5 - Original", "original-device")];
        public IReadOnlyList<PortInfo> Enumerate() => Fail ? throw new IOException("Test failure") : Ports;
    }
}

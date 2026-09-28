using System.ComponentModel;
using cast.Serial;

namespace cast.Tests;

public sealed class SerialPortCatalogTests
{
    [Fact]
    public void MissingDeviceDetailsFallBackToLivePortNamesAndKeepKnownIdentities()
    {
        var queried = new List<string>();
        var known = new PortInfo("COM3", "COM3 - Adapter", "device-3");
        var ports = SerialPortCatalog.EnumerateWindows(
            () => ["com3", " COM8\0driver-data", "COM8", "COM9", "LPT1", "COMbad"],
            () => [known, new("COM12", "COM12 - Vendor", "device-12")],
            name => { queried.Add(name); return name == "COM8"; });

        Assert.Equal(3, ports.Count);
        Assert.Contains(known, ports);
        Assert.Contains(new PortInfo("COM8", "COM8"), ports);
        Assert.Contains(ports, port => port.DeviceInstanceId == "device-12");
        Assert.Equal(new[] { "COM8", "COM9" }, queried);
    }

    [Fact]
    public void SetupApiFailureStillReturnsLiveRegistryPorts()
    {
        var ports = SerialPortCatalog.EnumerateWindows(() => ["COM7"],
            () => throw new Win32Exception(5), _ => true);
        Assert.Equal(new PortInfo("COM7", "COM7"), Assert.Single(ports));
    }

    [Fact]
    public void RegistryFailureStillReturnsSetupApiPorts()
    {
        var known = new PortInfo("COM3", "Adapter", "device");
        var ports = SerialPortCatalog.EnumerateWindows(() => throw new UnauthorizedAccessException(),
            () => [known], _ => throw new InvalidOperationException("Unexpected fallback"));
        Assert.Equal(known, Assert.Single(ports));
    }

    [Fact]
    public void FailureWithoutAnyUsablePortsIsReported()
    {
        var failure = new Win32Exception(5);
        var error = Assert.Throws<IOException>(() => SerialPortCatalog.EnumerateWindows(
            () => [], () => throw failure, _ => false));
        Assert.Same(failure, error.InnerException);
        Assert.Empty(SerialPortCatalog.EnumerateWindows(() => [], () => [], _ => false));
    }

    [Fact]
    public void NewAndRemovedPortsAreReevaluatedOnEveryScan()
    {
        string[] names = [];
        var present = new HashSet<string>();
        IReadOnlyList<PortInfo> Scan() => SerialPortCatalog.EnumerateWindows(() => names, () => [], present.Contains);
        Assert.Empty(Scan());
        names = ["COM7"];
        present.Add("COM7");
        Assert.Equal("COM7", Assert.Single(Scan()).PortName);
        present.Clear();
        Assert.Empty(Scan());
    }
}

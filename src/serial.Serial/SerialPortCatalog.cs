using System.IO.Ports;

namespace serial.Serial;

public sealed class SerialPortCatalog : IPortCatalog
{
    public IReadOnlyList<PortInfo> Enumerate()
    {
        var ports = OperatingSystem.IsWindows()
            ? WindowsPortCatalog.Enumerate()
            : SerialPort.GetPortNames().Select(static name => new PortInfo(name, name));
        return ports
            .DistinctBy(static port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static port => ParsePortNumber(port.PortName))
            .ThenBy(static port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ParsePortNumber(string name)
    {
        if (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(3), out var number))
        {
            return number;
        }

        return int.MaxValue;
    }
}

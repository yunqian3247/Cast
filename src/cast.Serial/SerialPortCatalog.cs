using System.ComponentModel;
using System.IO.Ports;
using System.Security;

namespace cast.Serial;

public sealed class SerialPortCatalog : IPortCatalog
{
    public IReadOnlyList<PortInfo> Enumerate()
    {
        var ports = OperatingSystem.IsWindows()
            ? EnumerateWindows(SerialPort.GetPortNames, WindowsPortCatalog.Enumerate, WindowsPortCatalog.IsPortPresent)
            : SerialPort.GetPortNames().Select(static name => new PortInfo(name, name));
        return ports
            .DistinctBy(static port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static port => ParsePortNumber(port.PortName))
            .ThenBy(static port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<PortInfo> EnumerateWindows(Func<string[]> getNames,
        Func<IReadOnlyList<PortInfo>> getDetails, Func<string, bool> isPresent)
    {
        Exception? failure = null;
        string[] names = [];
        IReadOnlyList<PortInfo> details = [];
        try { names = getNames(); }
        catch (Exception ex) when (IsEnumerationError(ex)) { failure = ex; }
        try { details = getDetails(); }
        catch (Exception ex) when (IsEnumerationError(ex)) { failure = ex; }

        var ports = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in details)
        {
            var name = NormalizeWindowsPortName(port.PortName);
            if (name is not null) ports.TryAdd(name, port with { PortName = name });
        }
        foreach (var value in names)
        {
            var name = NormalizeWindowsPortName(value);
            if (name is null || ports.ContainsKey(name)) continue;
            // The registry can retain old COM names. Check the DOS mapping without opening the port.
            try { if (isPresent(name)) ports.Add(name, new PortInfo(name, name)); }
            catch (Exception ex) when (IsEnumerationError(ex)) { failure = ex; }
        }
        if (ports.Count == 0 && failure is not null)
            throw new IOException("Unable to enumerate serial ports.", failure);
        return ports.Values.ToArray();
    }

    internal static string? NormalizeWindowsPortName(string value)
    {
        var name = value.Split('\0', 2)[0].Trim();
        if (!name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.Length <= 3) return null;
        foreach (var digit in name.AsSpan(3))
            if (digit is < '0' or > '9') return null;
        return name.ToUpperInvariant();
    }

    private static bool IsEnumerationError(Exception error) =>
        error is Win32Exception or IOException or UnauthorizedAccessException or SecurityException;

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

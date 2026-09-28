using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace cast.Serial;

[SupportedOSPlatform("windows")]
internal static class WindowsPortCatalog
{
    private const uint Present = 0x2;
    private const uint AllClasses = 0x4;
    private const int NoMoreItems = 259;
    private const int InsufficientBuffer = 122;

    public static IReadOnlyList<PortInfo> Enumerate()
    {
        // Include vendor and modem classes as well as Ports, while excluding absent devices.
        var devices = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, Present | AllClasses);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var ports = new List<PortInfo>();
            for (uint index = 0; ; index++)
            {
                var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref device))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NoMoreItems) break;
                    throw new Win32Exception(error);
                }

                var portName = ReadPortName(devices, ref device);
                if (portName is null) continue;
                var name = ReadProperty(devices, ref device, 0xC)
                    ?? ReadProperty(devices, ref device, 0) ?? portName;
                var suffix = $" ({portName})";
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
                var displayName = name.Equals(portName, StringComparison.OrdinalIgnoreCase) ? portName : $"{portName} - {name}";
                ports.Add(new PortInfo(portName, displayName, ReadInstanceId(devices, ref device)));
            }
            return ports;
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    private static string? ReadPortName(IntPtr devices, ref DeviceInfoData device)
    {
        const uint globalScope = 1, deviceKey = 1, queryValue = 1;
        var handle = SetupDiOpenDevRegKey(devices, ref device, globalScope, 0, deviceKey, queryValue);
        if (handle == new IntPtr(-1)) return null;
        using var safeHandle = new SafeRegistryHandle(handle, ownsHandle: true);
        try
        {
            using var key = RegistryKey.FromHandle(safeHandle);
            return key.GetValue("PortName") is string value ? SerialPortCatalog.NormalizeWindowsPortName(value) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A device may disappear or deny registry access during enumeration.
            return null;
        }
    }

    internal static bool IsPortPresent(string name)
    {
        var buffer = new StringBuilder(512);
        if (QueryDosDeviceW(name, buffer, (uint)buffer.Capacity) != 0) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == InsufficientBuffer) return true;
        if (error == 2) return false;
        throw new Win32Exception(error);
    }

    private static string? ReadProperty(IntPtr devices, ref DeviceInfoData device, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(devices, ref device, property, out _, null, 0, out var size);
        if (Marshal.GetLastWin32Error() != InsufficientBuffer) return null;
        var buffer = new byte[size];
        return SetupDiGetDeviceRegistryPropertyW(devices, ref device, property, out var type, buffer, size, out _) && type == 1
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : null;
    }

    private static string? ReadInstanceId(IntPtr devices, ref DeviceInfoData device)
    {
        SetupDiGetDeviceInstanceIdW(devices, ref device, null, 0, out var size);
        if (Marshal.GetLastWin32Error() != InsufficientBuffer) return null;
        var buffer = new StringBuilder((int)size);
        return SetupDiGetDeviceInstanceIdW(devices, ref device, buffer, size, out _) ? buffer.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfoData device);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInfoData device, uint scope, uint profile, uint keyType, uint access);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devices, ref DeviceInfoData device, uint property,
        out uint type, [Out] byte[]? buffer, uint size, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devices, ref DeviceInfoData device, StringBuilder? buffer, uint size, out uint requiredSize);

    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string name, StringBuilder target, uint size);
}

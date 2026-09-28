using cast.Core;

namespace cast.Serial;

public static class SerialWriteTiming
{
    public static TimeSpan GetTimeout(int byteCount, SerialProfile profile) => GetTimeout(byteCount,
        profile.BaudRate, profile.DataBits, profile.Parity != ParitySetting.None,
        profile.StopBits == StopBitsSetting.Two ? 2 : profile.StopBits == StopBitsSetting.OnePointFive ? 1.5 : 1);

    public static TimeSpan GetTimeout(int byteCount, int baudRate, int dataBits, bool parity, double stopBits, int graceMs = 5000)
    {
        if (byteCount < 0 || baudRate < 1 || dataBits is < 5 or > 8 || graceMs < 1)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        var bitsPerByte = 1 + dataBits + (parity ? 1 : 0) + stopBits;
        var milliseconds = graceMs + byteCount * bitsPerByte * 1000d / baudRate;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, uint.MaxValue - 4000d));
    }
}

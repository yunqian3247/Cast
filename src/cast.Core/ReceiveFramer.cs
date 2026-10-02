namespace cast.Core;

public sealed record ReceiveFraming(string Mode = "raw", int IdleMs = 30, int Length = 8, string Delimiter = "0D 0A")
{
    public void Validate()
    {
        if (Mode is not ("raw" or "auto" or "fixed" or "delimiter" or "idle") || IdleMs is < 5 or > 5000
            || Length is < 1 or > ReceiveFramer.MaxBufferedBytes)
            throw new FormatException("接收分帧参数无效：空闲间隔 5～5000 ms，固定长度 1～65536 字节");
        if (Delimiter is null || Delimiter.Length > 256 || (Mode == "delimiter" && HexCodec.Parse(Delimiter).Length == 0))
            throw new FormatException("分隔符须为 1～128 个完整 HEX 字节");
    }
}

// Operates on bytes; callers choose the encoding after complete frames have been extracted.
public sealed class ReceiveFramer
{
    public const int MaxBufferedBytes = 64 * 1024;
    private readonly ReceiveFraming _settings;
    private readonly byte[] _delimiter;
    private readonly byte[][] _known;
    private readonly List<byte> _buffer = [];
    public int BufferedBytes => _buffer.Count;

    public ReceiveFramer(ReceiveFraming settings, IEnumerable<byte[]>? knownFrames = null)
    {
        settings.Validate(); _settings = settings;
        _delimiter = settings.Mode == "delimiter" ? HexCodec.Parse(settings.Delimiter) : [];
        _known = (knownFrames ?? []).Where(bytes => bytes.Length is >= 3 and <= MaxBufferedBytes)
            .OrderByDescending(bytes => bytes.Length).ToArray();
    }

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> bytes)
    {
        if (_settings.Mode == "raw") return bytes.IsEmpty ? [] : [bytes.ToArray()];
        var output = new List<byte[]>();
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(MaxBufferedBytes - _buffer.Count, bytes.Length);
            _buffer.AddRange(bytes[..count].ToArray()); bytes = bytes[count..];
            Extract(output);
            if (_buffer.Count == MaxBufferedBytes) output.Add(Take(_buffer.Count));
        }
        return output;
    }

    public byte[] Flush() => Take(_buffer.Count);

    private void Extract(List<byte[]> output)
    {
        while (_buffer.Count > 0)
        {
            var size = _settings.Mode switch
            {
                "fixed" => _buffer.Count >= _settings.Length ? _settings.Length : 0,
                "delimiter" => DelimitedLength(_delimiter),
                "auto" => AutoLength(),
                _ => 0
            };
            if (size == 0) break;
            output.Add(Take(size));
        }
    }

    private int AutoLength()
    {
        var knownPrefix = false;
        foreach (var known in _known)
        {
            var prefix = Math.Min(known.Length, _buffer.Count);
            if (!_buffer.Take(prefix).SequenceEqual(known.Take(prefix))) continue;
            if (_buffer.Count >= known.Length) return known.Length;
            knownPrefix = true;
        }
        if (_buffer.Count >= 2 && _buffer[0] <= 247)
        {
            var function = _buffer[1];
            var candidates = function switch
            {
                >= 0x81 and <= 0x90 => new[] { 5 },
                1 or 2 or 3 or 4 => new[] { 8, _buffer.Count >= 3 ? 5 + _buffer[2] : 0 },
                5 or 6 => new[] { 8 },
                15 or 16 => new[] { 8, _buffer.Count >= 7 ? 9 + _buffer[6] : 0 },
                _ => []
            };
            foreach (var size in candidates.Distinct())
                if (size >= 5 && size <= _buffer.Count && ValidCrc(size)) return size;
            if (candidates.Length > 0) return 0;
        }
        if (knownPrefix) return 0;
        var lf = _buffer.IndexOf(10);
        return lf >= 0 ? lf + 1 : 0;
    }

    private bool ValidCrc(int size)
    {
        var crc = 0xFFFF;
        for (var i = 0; i < size - 2; i++)
        {
            crc ^= _buffer[i];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xA001 : 0);
        }
        return crc == (_buffer[size - 2] | _buffer[size - 1] << 8);
    }

    private int DelimitedLength(byte[] delimiter)
    {
        for (var i = 0; i <= _buffer.Count - delimiter.Length; i++)
            if (_buffer.Skip(i).Take(delimiter.Length).SequenceEqual(delimiter)) return i + delimiter.Length;
        return 0;
    }

    private byte[] Take(int count)
    {
        var result = _buffer.GetRange(0, count).ToArray(); _buffer.RemoveRange(0, count); return result;
    }
}

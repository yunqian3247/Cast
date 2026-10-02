using System.Text;

namespace cast.Desktop;

internal sealed class PresetImportSession
{
    public const int ChunkChars = 128 * 1024;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    private readonly string _content;
    public int Length => _content.Length;
    public PresetImportSession(string content)
    {
        if (content.Length > CommandPreset.MaxImportChars) throw new FormatException("预设文件超过 64 Mi 字符");
        _content = content;
    }

    public string Chunk(string id, int offset)
    {
        if (id != Id || offset < 0 || offset >= Length) throw new FormatException("预设传输标识或位置无效");
        var end = Math.Min(Length, offset + ChunkChars);
        if (end < Length && char.IsHighSurrogate(_content[end - 1])) end--;
        return _content[offset..end];
    }

    public static async Task<string> ReadAsync(string path)
    {
        if (new FileInfo(path).Length > 4L * CommandPreset.MaxImportChars) throw new FormatException("预设文件超过容量上限");
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true, 64 * 1024);
        var buffer = new char[64 * 1024];
        var content = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (content.Length > CommandPreset.MaxImportChars - count) throw new FormatException("预设文件超过 64 Mi 字符");
            content.Append(buffer, 0, count);
        }
        return content.ToString();
    }
}

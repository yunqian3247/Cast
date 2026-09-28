using System.Text;
using System.Text.Json;

namespace cast.Desktop;

internal sealed class DocumentSaveBuffer
{
    public const int MaxChunkChars = 128 * 1024;
    private string? _id;
    private int _length;
    private StringBuilder? _buffer;

    public void Start(string id, int length)
    {
        if (_buffer is not null) throw new InvalidOperationException("已有配置传输正在进行");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || length is < 1 or > AppDocument.MaxSerializedChars)
            throw new FormatException("配置传输标识或容量无效，配置上限为 64 Mi 字符");
        _id = id; _length = length; _buffer = new StringBuilder(Math.Min(length, MaxChunkChars));
    }

    public void Append(string id, int offset, string content)
    {
        Check(id);
        if (offset != _buffer!.Length || content.Length is < 1 or > MaxChunkChars || content.Length > _length - offset)
            throw new FormatException("配置分块顺序或大小无效");
        _buffer.Append(content);
    }

    public AppDocument Finish(string id)
    {
        Check(id);
        if (_buffer!.Length != _length) throw new FormatException("配置传输尚未完成");
        try
        {
            var document = JsonSerializer.Deserialize<AppDocument>(_buffer.ToString(), AppService.Json)
                ?? throw new FormatException("配置内容为空");
            document.Validate();
            return document;
        }
        finally { Abort(id); }
    }

    public void Abort(string? id = null)
    {
        if (id is not null && id != _id) return;
        _buffer = null; _id = null; _length = 0;
    }

    private void Check(string id)
    {
        if (_buffer is null || id != _id) throw new InvalidOperationException("配置传输已结束或标识无效");
    }
}

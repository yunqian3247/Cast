using System.Text;

namespace cast.Desktop;

internal sealed class LogExportSession : IDisposable
{
    private FileStream? _stream;
    private readonly string _target;
    internal string TempPath { get; }

    public LogExportSession(string target, string format)
    {
        if (format is not ("txt" or "csv" or "json")) throw new FormatException("导出格式无效");
        _target = Path.GetFullPath(target);
        TempPath = _target + $".cast-export-{Guid.NewGuid():N}.tmp";
        try
        {
            _stream = new FileStream(TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            if (format == "csv") _stream.Write(Encoding.UTF8.GetPreamble());
        }
        catch { Dispose(); throw; }
    }

    public async Task AppendAsync(string content)
    {
        if (_stream is null) throw new InvalidOperationException("导出任务已结束");
        if (content.Length > 1_048_576) throw new FormatException("导出分块过大");
        await using var writer = new StreamWriter(_stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
    }

    public async Task FinishAsync()
    {
        if (_stream is null) throw new InvalidOperationException("导出任务已结束");
        await _stream.FlushAsync();
        await _stream.DisposeAsync();
        _stream = null;
        File.Move(TempPath, _target, overwrite: true);
    }

    public void Dispose()
    {
        _stream?.Dispose(); _stream = null;
        try { File.Delete(TempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

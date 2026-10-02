using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace cast.Desktop;

// Disk I/O stays off the transport thread. Queue exhaustion is counted and surfaced in status.
internal sealed class ContinuousLogWriter : IAsyncDisposable
{
    private const long MaxQueuedBytes = 32 * 1024 * 1024;
    private readonly Channel<AppLog> _queue = Channel.CreateBounded<AppLog>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly string _directory;
    private long _maxBytes;
    private int _files;
    private long _queuedBytes, _dropped;
    private string? _error;
    private static readonly JsonSerializerOptions Json = new(AppService.Json) { WriteIndented = false };
    public string? Error => Volatile.Read(ref _error);
    public long Dropped => Interlocked.Read(ref _dropped);
    public void ResetDropped() => Interlocked.Exchange(ref _dropped, 0);

    public ContinuousLogWriter(string directory, int fileMiB, int files)
    {
        _directory = directory; _maxBytes = fileMiB * 1024L * 1024; _files = files;
        _worker = Task.Run(WriteLoopAsync);
    }

    public void Configure(int fileMiB, int files)
    { Volatile.Write(ref _maxBytes, fileMiB * 1024L * 1024); Volatile.Write(ref _files, files); }

    public void Append(AppLog log)
    {
        var bytes = AppService.LogTextBytes(log);
        if (Error is not null) { Interlocked.Increment(ref _dropped); return; }
        if (Interlocked.Add(ref _queuedBytes, bytes) > MaxQueuedBytes)
        { Interlocked.Add(ref _queuedBytes, -bytes); Interlocked.Increment(ref _dropped); return; }
        if (!_queue.Writer.TryWrite(log)) { Interlocked.Add(ref _queuedBytes, -bytes); Interlocked.Increment(ref _dropped); }
    }

    private async Task WriteLoopAsync()
    {
        FileStream? stream = null;
        StreamWriter? writer = null;
        long length = 0;
        var writingRecord = false;
        try
        {
            Directory.CreateDirectory(_directory);
            await foreach (var log in _queue.Reader.ReadAllAsync())
            {
                Interlocked.Add(ref _queuedBytes, -AppService.LogTextBytes(log));
                writingRecord = true;
                var json = JsonSerializer.Serialize(log, Json);
                var size = Encoding.UTF8.GetByteCount(json) + 1L;
                if (stream is null || (length > 0 && length + size > Volatile.Read(ref _maxBytes)))
                {
                    if (writer is not null) await writer.DisposeAsync();
                    var path = Path.Combine(_directory, $"cast-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.jsonl");
                    stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, true);
                    writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    length = 0;
                    foreach (var old in Directory.GetFiles(_directory, "cast-*.jsonl").OrderByDescending(Path.GetFileName).Skip(Volatile.Read(ref _files)))
                        File.Delete(old);
                }
                await writer!.WriteLineAsync(json); length += size; writingRecord = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("持续日志写入", ex);
            Volatile.Write(ref _error, "持续日志写入失败：" + ex.Message);
            if (writingRecord) Interlocked.Increment(ref _dropped);
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var remaining))
            { Interlocked.Add(ref _queuedBytes, -AppService.LogTextBytes(remaining)); Interlocked.Increment(ref _dropped); }
        }
        finally
        {
            try { if (writer is not null) await writer.DisposeAsync(); else if (stream is not null) await stream.DisposeAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Volatile.Write(ref _error, ex.Message); }
        }
    }

    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _worker; }
}

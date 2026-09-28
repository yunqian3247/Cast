using System.Text.Json;

namespace cast.Core;

public sealed class JsonFileStore<T> where T : class
{
    private readonly JsonSerializerOptions _options;
    private readonly Action<T>? _validate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileStore(JsonSerializerOptions? options = null, Action<T>? validate = null)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        _validate = validate;
    }

    public async Task<T?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                if (!File.Exists(fullPath + ".bak")) return null;
                try { return await ReadAsync(fullPath + ".bak", cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsDataException(ex)) { throw new JsonFileStoreException(fullPath, ex); }
            }

            try
            {
                return await ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception primaryException) when (IsDataException(primaryException))
            {
                var backupPath = fullPath + ".bak";
                if (!File.Exists(backupPath))
                {
                    throw new JsonFileStoreException(fullPath, primaryException);
                }

                try
                {
                    return await ReadAsync(backupPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception backupException) when (IsDataException(backupException))
                {
                    throw new JsonFileStoreException(fullPath, new AggregateException(primaryException, backupException));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string path, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fullPath = Path.GetFullPath(path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _validate?.Invoke(value);
            // Serialize while holding the gate so a burst of UI saves preserves
            // invocation order and never writes a stale snapshot after a newer one.
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = fullPath + ".tmp";
            var backupPath = fullPath + ".bak";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(fullPath))
                {
                    var valid = true;
                    try { await ReadAsync(fullPath, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (IsDataException(ex)) { valid = false; }
                    if (valid) File.Copy(fullPath, backupPath, overwrite: true);
                    else File.Copy(fullPath, fullPath + $".corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}", overwrite: false);
                }

                File.Move(tempPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("JSON 文件为空");
        _validate?.Invoke(value);
        return value;
    }

    private static bool IsDataException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or FormatException;
}

public sealed class JsonFileStoreException : IOException
{
    public string FilePath { get; }

    public JsonFileStoreException(string filePath, Exception innerException)
        : base($"JSON 文件无法读取: {filePath}", innerException)
    {
        FilePath = filePath;
    }
}

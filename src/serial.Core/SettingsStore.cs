using System.Text.Json;

namespace serial.Core;

public sealed class SettingsStore
{
    private readonly JsonSerializerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    }

    public async Task<AppSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("设置文件为空");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or NotSupportedException)
            {
                var backupPath = path + ".bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        await using var backup = File.OpenRead(backupPath);
                        return await JsonSerializer.DeserializeAsync<AppSettings>(backup, _options, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidDataException("设置备份为空", ex);
                    }
                    catch (Exception backupException) when (backupException is IOException or JsonException or InvalidDataException or NotSupportedException)
                    {
                        throw new SettingsStoreException(ErrorCodes.ConfigCorrupt, path, new AggregateException(ex, backupException));
                    }
                }

                throw new SettingsStoreException(ErrorCodes.ConfigCorrupt, path, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string path, AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        var fullPath = System.IO.Path.GetFullPath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var payload = JsonSerializer.SerializeToUtf8Bytes(settings, _options);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
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
                File.Copy(fullPath, backupPath, overwrite: true);
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
}

public sealed class SettingsStoreException : IOException
{
    public string ErrorCode { get; }
    public string FilePath { get; }

    public SettingsStoreException(string errorCode, string filePath, Exception innerException)
        : base($"设置文件无法读取: {filePath}", innerException)
    {
        ErrorCode = errorCode;
        FilePath = filePath;
    }
}

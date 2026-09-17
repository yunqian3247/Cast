using System.Text;
using System.Text.Json;

namespace serial.Core;

public sealed class SessionLogWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _stream;
    private bool _initialized;
    private int _disposeState;

    public Session Session { get; }
    public string Path => _path;

    public SessionLogWriter(string path, Session session, JsonSerializerOptions? jsonOptions = null)
    {
        _path = System.IO.Path.GetFullPath(path);
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _jsonOptions = new JsonSerializerOptions(jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)) { WriteIndented = false };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            sessionEvent.SessionId = Session.Id;
            var line = JsonSerializer.Serialize(new LogLine("event", null, sessionEvent), _jsonOptions) + Environment.NewLine;
            await WriteLineUnsafeAsync(line, cancellationToken).ConfigureAwait(false);
            Session.EventCount++;
            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (Session.EndedAt is null)
            {
                Session.EndedAt = DateTimeOffset.UtcNow;
                var line = JsonSerializer.Serialize(new LogLine("session-ended", Session, null), _jsonOptions) + Environment.NewLine;
                await WriteLineUnsafeAsync(line, cancellationToken).ConfigureAwait(false);
            }

            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Appends the current session metadata without ending the session. This
    /// checkpoint makes long-running task results recoverable after a process
    /// interruption while keeping the JSONL format append-only.
    /// </summary>
    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var line = JsonSerializer.Serialize(new LogLine("session", Session, null), _jsonOptions) + Environment.NewLine;
            await WriteLineUnsafeAsync(line, cancellationToken).ConfigureAwait(false);
            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionExportDocument> ReadExportAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);

            var document = new SessionExportDocument { Session = Session };
            await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var logLine = JsonSerializer.Deserialize<LogLine>(line, _jsonOptions);
                if (logLine?.Kind == "event" && logLine.Event is not null)
                {
                    document.Events.Add(logLine.Event);
                }
                else if (logLine?.Kind == "session" && logLine.Session is not null)
                {
                    document.Session = logLine.Session;
                }
                else if (logLine?.Kind == "session-ended" && logLine.Session is not null)
                {
                    document.Session = logLine.Session;
                }
            }

            // The first JSONL record contains the session as it looked when the
            // port opened. Use the live instance so exports made before closing
            // also include current counters and task run records.
            document.Session = Session;

            return document;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_stream is not null)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_stream is not null)
            {
                await _stream.FlushAsync().ConfigureAwait(false);
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }

            Volatile.Write(ref _disposeState, 1);

        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        var line = JsonSerializer.Serialize(new LogLine("session", Session, null), _jsonOptions) + Environment.NewLine;
        await WriteLineUnsafeAsync(line, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task WriteLineUnsafeAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        await _stream!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private sealed record LogLine(string Kind, Session? Session, SessionEvent? Event);
}

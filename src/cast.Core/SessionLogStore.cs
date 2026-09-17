using System.Text;
using System.Text.Json;

namespace cast.Core;

public sealed class SessionLogStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _options;

    public SessionLogStore(string directory, JsonSerializerOptions? options = null)
    {
        _directory = Path.GetFullPath(directory);
        _options = new JsonSerializerOptions(options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)) { WriteIndented = false };
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var summaries = new List<SessionSummary>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                summaries.Add(CreateSummary(path, document));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A damaged log remains on disk so the user can recover it manually.
            }
        }

        return summaries
            .OrderByDescending(static item => item.StartedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<SessionSummary>> QueryAsync(
        SessionQuery query,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var summaries = await ListAsync(cancellationToken).ConfigureAwait(false);
        var referenceTime = now ?? DateTimeOffset.Now;
        return summaries
            .Where(summary => SessionQueryMatcher.Matches(summary, query, referenceTime))
            .ToArray();
    }

    public async Task<int> RecoverIncompleteSessionsAsync(
        string reason = "应用在会话结束前退出",
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        var recovered = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (document.Session.EndedAt is not null)
                {
                    continue;
                }

                document.Session.EndedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                foreach (var run in document.Session.WorkflowRuns.Where(static run => run.State is WorkflowRunState.Running or WorkflowRunState.WaitingNextRun))
                {
                    run.State = WorkflowRunState.Failed;
                    run.Error ??= reason;
                    run.StopReason ??= reason;
                    run.EndedAt ??= document.Session.EndedAt;
                }

                foreach (var run in document.Session.AutoSendRuns.Where(static run => run.State == AutoSendRunState.Running))
                {
                    run.State = AutoSendRunState.Failed;
                    run.Error ??= reason;
                    run.StopReason ??= reason;
                    run.EndedAt ??= document.Session.EndedAt;
                }

                if (!document.Session.ExceptionEvents.Contains(reason, StringComparer.Ordinal))
                {
                    document.Session.ExceptionEvents.Add(reason);
                }

                var line = JsonSerializer.Serialize(
                    new LogLine("session-ended", document.Session, null),
                    _options) + Environment.NewLine;
                await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                recovered++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Damaged logs stay untouched for manual recovery.
            }
        }

        return recovered;
    }

    public async Task<SessionExportDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = EnsureOwnedPath(path);
        var document = new SessionExportDocument();
        var hasSession = false;
        var lineNumber = 0;

        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogLine>(line, _options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"会话日志第 {lineNumber} 行损坏", ex);
            }

            if (entry?.Event is not null && entry.Kind == "event")
            {
                document.Events.Add(entry.Event);
            }
            else if (entry?.Session is not null && entry.Kind is "session" or "session-ended")
            {
                document.Session = entry.Session;
                hasSession = true;
            }
        }

        if (!hasSession)
        {
            throw new InvalidDataException("会话日志缺少会话信息");
        }

        return document;
    }

    public async Task ExportAsync(
        string sourcePath,
        string destinationPath,
        Func<SessionEvent, bool>? filter = null,
        CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (filter is not null)
        {
            document.Events = document.Events.Where(filter).ToList();
        }

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await JsonSerializer.SerializeAsync(output, document, new JsonSerializerOptions(_options) { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }

    public void Delete(string path)
    {
        File.Delete(EnsureOwnedPath(path));
    }

    private string EnsureOwnedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_directory, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("会话路径超出日志目录");
        }

        return fullPath;
    }

    private static SessionSummary CreateSummary(string path, SessionExportDocument document)
    {
        var events = document.Events;
        var workflowNames = document.Session.WorkflowRuns
            .Select(static run => run.WorkflowName)
            .Concat(events.Select(static item =>
                item.Metadata is not null && item.Metadata.TryGetValue("workflowName", out var name)
                    ? name
                    : string.Empty))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasErrors = document.Session.ExceptionEvents.Count > 0
            || document.Session.WorkflowRuns.Any(static run => run.State == WorkflowRunState.Failed)
            || document.Session.AutoSendRuns.Any(static run => run.State == AutoSendRunState.Failed)
            || events.Any(static item => item.EventType == SessionEventType.Error || !string.IsNullOrWhiteSpace(item.ErrorCode));
        return new SessionSummary
        {
            Id = document.Session.Id,
            Path = path,
            StartedAt = document.Session.StartedAt,
            EndedAt = document.Session.EndedAt,
            PortName = document.Session.SerialProfileSnapshot.PortName,
            BaudRate = document.Session.SerialProfileSnapshot.BaudRate,
            EventCount = Math.Max(document.Session.EventCount, events.Count),
            ReceivedBytes = events.Where(static item => item.Direction == EventDirection.Rx).Sum(static item => (long)item.RawBytes.Length),
            SentBytes = events.Where(static item => item.Direction == EventDirection.Tx).Sum(static item => (long)item.RawBytes.Length),
            IsActive = document.Session.EndedAt is null,
            HasErrors = hasErrors,
            WorkflowNames = workflowNames
        };
    }

    private sealed record LogLine(string Kind, Session? Session, SessionEvent? Event);
}

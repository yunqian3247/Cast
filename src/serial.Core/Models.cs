namespace serial.Core;

public enum DataMode
{
    Text,
    Hex
}

public enum LogContentMode
{
    Text,
    Hex,
    TextAndHex
}

public enum TimestampDisplayMode
{
    Hidden,
    Seconds,
    Milliseconds
}

public enum TextEncodingKind
{
    Utf8,
    Gbk,
    Ascii
}

public enum LineEndingMode
{
    None,
    Cr,
    Lf,
    CrLf,
    CustomHex
}

public enum StopBitsSetting
{
    One,
    OnePointFive,
    Two
}

public enum ParitySetting
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum FlowControlSetting
{
    None,
    RtsCts,
    XOnXOff,
    RtsCtsAndXOnXOff
}

public enum EventDirection
{
    Rx,
    Tx,
    System
}

public enum EventOrigin
{
    Manual,
    System,
    Workflow,
    Auto,
    File,
    Serial
}

public enum WorkflowTriggerMode
{
    Once,
    Loop,
    Scheduled
}

public enum WorkflowRunState
{
    Running,
    WaitingNextRun,
    Completed,
    Stopped,
    Failed
}

public enum AutoSendRunState
{
    Running,
    Completed,
    Stopped,
    Failed
}

public enum SessionEventType
{
    Data,
    SessionStarted,
    SessionEnded,
    Error
}

public sealed class SerialProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认配置";
    public string PortName { get; set; } = string.Empty;
    public string? DeviceInstanceId { get; set; }
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public StopBitsSetting StopBits { get; set; } = StopBitsSetting.One;
    public ParitySetting Parity { get; set; } = ParitySetting.None;
    public FlowControlSetting FlowControl { get; set; } = FlowControlSetting.None;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
    public TextEncodingKind Encoding { get; set; } = TextEncodingKind.Utf8;
    public LineEndingMode LineEnding { get; set; } = LineEndingMode.None;
    public string CustomLineEndingHex { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public SerialProfile SerialProfile { get; set; } = new();
    public DataMode ReceiveMode { get; set; } = DataMode.Text;
    public LogContentMode ReceiveContentMode { get; set; } = LogContentMode.Text;
    public TimestampDisplayMode TimestampMode { get; set; } = TimestampDisplayMode.Milliseconds;
    public bool ShowDirection { get; set; } = true;
    public bool ShowSource { get; set; } = true;
    public bool ShowByteCount { get; set; }
    public bool WrapLogLines { get; set; }
    public bool ShowRx { get; set; } = true;
    public bool ShowTx { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public DataMode SendMode { get; set; } = DataMode.Text;
    public bool AutoScroll { get; set; } = true;
    public int MaxDisplayEvents { get; set; } = 10000;
    public List<WorkflowDefinition> Workflows { get; set; } = [];
    public string? SelectedWorkflowId { get; set; }
    public string? SelectedPresetId { get; set; }
    public string? SelectedAutoPresetId { get; set; }
    public Dictionary<string, string> WorkspacePanels { get; set; } = new(StringComparer.Ordinal);

    // Window/workspace preferences are kept with the rest of the user profile so
    // a restart restores the same working posture without restoring transient runs.
    public string Theme { get; set; } = "浅色";
    public string CurrentWorkspace { get; set; } = "monitor";
    public bool TaskbarCollapsed { get; set; }
    public bool FunctionPanelCollapsed { get; set; }
    public int FunctionPanelWidth { get; set; } = 320;
    public int SendPaneHeight { get; set; } = 210;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 760;
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }

    public void Normalize()
    {
        SerialProfile ??= new SerialProfile();
        Workflows ??= [];
        WorkspacePanels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        MaxDisplayEvents = Math.Clamp(MaxDisplayEvents, 100, 100_000);
        FunctionPanelWidth = Math.Clamp(FunctionPanelWidth, 280, 400);
        SendPaneHeight = Math.Clamp(SendPaneHeight, 150, 320);
        WindowWidth = Math.Max(WindowWidth, 960);
        WindowHeight = Math.Max(WindowHeight, 620);
    }
}

public sealed class WorkflowDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名工作流";
    public WorkflowTriggerMode TriggerMode { get; set; } = WorkflowTriggerMode.Once;
    public int PeriodMs { get; set; } = 5000;
    public int RunCount { get; set; } = 1;
    public List<WorkflowStep> Steps { get; set; } = [];
}

public sealed class WorkflowStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PresetId { get; set; } = string.Empty;
    public int WaitAfterMs { get; set; } = 1000;
}

public sealed class WorkflowSnapshot
{
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public WorkflowTriggerMode TriggerMode { get; set; }
    public int PeriodMs { get; set; }
    public int RunCount { get; set; }
    public List<WorkflowSnapshotStep> Steps { get; set; } = [];
}

public sealed class WorkflowSnapshotStep
{
    public string StepId { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Mode { get; set; } = "文本";
    public string Ending { get; set; } = "无";
    public TextEncodingKind Encoding { get; set; } = TextEncodingKind.Utf8;
    public string CustomEndingHex { get; set; } = string.Empty;
    public int WaitAfterMs { get; set; }
}

public sealed class WorkflowRunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public WorkflowRunState State { get; set; } = WorkflowRunState.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public int CompletedRounds { get; set; }
    public int CompletedSteps { get; set; }
    public string? StopReason { get; set; }
    public string? Error { get; set; }
    public WorkflowSnapshot Snapshot { get; set; } = new();
    public List<WorkflowStepResult> Steps { get; set; } = [];
}

/// <summary>
/// Immutable-at-runtime description of one automatic-send execution.
/// The snapshot is retained with the session so later preset edits do not
/// change the meaning of an already completed run.
/// </summary>
public sealed class AutoSendRunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PresetId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public AutoSendRunState State { get; set; } = AutoSendRunState.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public int SentCount { get; set; }
    public int Interval { get; set; }
    public int Count { get; set; }
    public string? StopReason { get; set; }
    public string? Error { get; set; }
    public PresetCommandModel Snapshot { get; set; } = new();
}

public sealed class WorkflowStepResult
{
    public string StepId { get; set; } = string.Empty;
    public int Round { get; set; }
    public string PresetName { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int SentBytes { get; set; }
    public string State { get; set; } = "已发送";
    public string? Error { get; set; }
}

public static class WorkflowFactory
{
    public static WorkflowSnapshot CreateSnapshot(WorkflowDefinition workflow, IReadOnlyList<PresetCommandModel> presets)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(presets);
        var steps = workflow.Steps ?? [];
        var byId = presets
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var missing = steps
            .Where(step => step is null || !byId.ContainsKey(step.PresetId))
            .Select(step => step?.PresetId ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"工作流包含缺失预设：{string.Join(", ", missing)}");
        }

        return new WorkflowSnapshot
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            TriggerMode = workflow.TriggerMode,
            PeriodMs = Math.Max(10, workflow.PeriodMs),
            RunCount = Math.Max(0, workflow.RunCount),
            Steps = steps.Select(step =>
            {
                var preset = byId[step.PresetId];
                return new WorkflowSnapshotStep
                {
                    StepId = step.Id,
                    PresetId = preset.Id,
                    PresetName = preset.Name,
                    Content = preset.Content,
                    Mode = preset.Mode,
                    Ending = preset.Ending,
                    Encoding = preset.Encoding,
                    CustomEndingHex = preset.CustomEndingHex,
                    WaitAfterMs = Math.Max(0, step.WaitAfterMs)
                };
            }).ToList()
        };
    }
}

public sealed class PresetCommandModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名指令";
    public string Content { get; set; } = string.Empty;
    public string Mode { get; set; } = "文本";
    public string Ending { get; set; } = "无";
    public TextEncodingKind Encoding { get; set; } = TextEncodingKind.Utf8;
    public string CustomEndingHex { get; set; } = string.Empty;
    public int Interval { get; set; } = 1000;
    public int Count { get; set; }
    public string Note { get; set; } = string.Empty;
}

public enum FileTransferState
{
    Queued,
    Sending,
    Completed,
    Stopped,
    Failed
}

public sealed class FileTransferItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public long SentBytes { get; set; }
    public FileTransferState State { get; set; } = FileTransferState.Queued;
    public string? Error { get; set; }

    public static FileTransferItem FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        return new FileTransferItem
        {
            Path = info.FullName,
            Name = info.Name,
            Size = info.Exists ? info.Length : 0
        };
    }
}

public sealed class SessionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; }
    public long EventCount { get; set; }
    public long ReceivedBytes { get; set; }
    public long SentBytes { get; set; }
    public bool IsActive { get; set; }
    public bool HasErrors { get; set; }
    public List<string> WorkflowNames { get; set; } = [];

    public string StateText => HasErrors ? "异常" : IsActive ? "运行中" : "已完成";
    public string DisplayName => $"{StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {PortName} · {StateText}";
}

public sealed class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public SerialProfile SerialProfileSnapshot { get; set; } = new();
    public string ToolVersion { get; set; } = "1.0.0";
    public long EventCount { get; set; }
    public List<string> ExceptionEvents { get; set; } = [];
    public List<WorkflowRunRecord> WorkflowRuns { get; set; } = [];
    public List<AutoSendRunRecord> AutoSendRuns { get; set; } = [];
}

public sealed class SessionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public EventDirection Direction { get; set; }
    public byte[] RawBytes { get; set; } = [];
    public string? DisplayText { get; set; }
    public SessionEventType EventType { get; set; } = SessionEventType.Data;
    public string? ErrorCode { get; set; }
    public EventOrigin Origin { get; set; } = EventOrigin.Manual;
    public Dictionary<string, string>? Metadata { get; set; }
}

public sealed class SessionExportDocument
{
    public Session Session { get; set; } = new();
    public List<SessionEvent> Events { get; set; } = [];
}

public static class ErrorCodes
{
    public const string PortBusy = "CONN-PORT-BUSY";
    public const string PortGone = "CONN-PORT-GONE";
    public const string OpenFailed = "CONN-OPEN-FAIL";
    public const string DriverMissing = "CONN-DRIVER-MISSING";
    public const string InvalidHex = "TX-INVALID-HEX";
    public const string WriteFailed = "TX-WRITE-FAIL";
    public const string DecodeFailed = "DATA-DECODE-FAIL";
    public const string BufferFull = "DATA-BUFFER-FULL";
    public const string LogWriteFailed = "LOG-WRITE-FAIL";
    public const string LogRotate = "LOG-ROTATE";
    public const string InstanceConflict = "SYS-INSTANCE-CONFLICT";
    public const string ConfigCorrupt = "SYS-CONFIG-CORRUPT";
}

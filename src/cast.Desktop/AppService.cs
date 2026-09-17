using System.Text;
using System.Text.Json;
using cast.Core;
using cast.Serial;

namespace cast.Desktop;

public sealed class AppService : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ISerialConnection _connection;
    private readonly IPortCatalog _catalog;
    private readonly JsonFileStore<AppDocument> _store = new(Json);
    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _documentGate = new(1, 1);
    private readonly object _sync = new();
    public const int MaxLogCount = 10000;
    public const long MaxLogTextBytes = 16 * 1024 * 1024;
    private readonly Queue<AppLog> _logs = new();
    private long _logTextBytes;
    public static long LogTextBytes(AppLog log) => 2L * (log.Text.Length + log.Hex.Length);
    private CancellationTokenSource? _runCancellation;
    private Task _runner = Task.CompletedTask;
    private SemaphoreSlim _runSignal = new(0, 1);
    private RunStatus _run = new("idle", false, -1, 0, "");
    private Decoder _decoder = TextCodec.GetEncoding(TextEncodingKind.Utf8).GetDecoder();
    private long _tx, _rx, _logId;
    private bool _disposed, _disconnecting, _saving, _transportUnavailable;
    public AppDocument Document { get; private set; } = new();
    public event Action<string, object>? Changed;
    public IReadOnlyList<AppLog> Logs { get { lock (_sync) return _logs.ToArray(); } }

    public AppService(string directory, ISerialConnection? connection = null, IPortCatalog? catalog = null, string? legacyPath = null)
    {
        _path = Path.Combine(directory, "cast.json");
        _legacyPath = legacyPath;
        _connection = connection ?? new SerialPortConnection();
        _catalog = catalog ?? new SerialPortCatalog();
        _connection.DataReceived += OnData;
        _connection.Error += OnError;
        _connection.StateChanged += OnConnectionChanged;
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (_legacyPath is not null) await LegacyDataMigration.MigrateAsync(_legacyPath, _path);
            var saved = await _store.LoadAsync(_path);
            if (saved is not null) { saved.Validate(); Document = saved; }
        }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException)
        {
            AddLog("SYS", "读取配置失败：" + ex.Message, [], "配置");
        }
        AddLog("SYS", "cast 已就绪", [], "系统");
    }

    public IReadOnlyList<PortInfo> GetPorts() => _catalog.Enumerate();

    public async Task SaveAsync(AppDocument document)
    {
        document.Validate();
        var snapshot = JsonSerializer.Deserialize<AppDocument>(JsonSerializer.Serialize(document, Json), Json)!;
        await _documentGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_run.Kind != "idle") throw new InvalidOperationException("运行期间请先停止任务再保存编辑");
                if (_disposed) throw new InvalidOperationException("cast 正在关闭");
                _saving = true;
                if (_connection.State == SerialConnectionState.Open) snapshot.Profile = Document.Profile;
            }
            await _store.SaveAsync(_path, snapshot);
            Document = snapshot;
        }
        finally { lock (_sync) _saving = false; _documentGate.Release(); }
    }

    public static void ValidateProfile(SerialProfile profile, bool requirePort = true)
    {
        if ((requirePort && string.IsNullOrWhiteSpace(profile.PortName)) || profile.BaudRate is < 1 or > 12_000_000
            || profile.DataBits is < 5 or > 8 || !Enum.IsDefined(profile.Parity) || !Enum.IsDefined(profile.StopBits)
            || !Enum.IsDefined(profile.FlowControl) || !Enum.IsDefined(profile.Encoding))
            throw new FormatException("串口参数无效，请检查端口与波特率");
    }

    public async Task ConnectAsync(SerialProfile profile)
    {
        ValidateProfile(profile);
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || _disconnecting || _transportUnavailable) throw new InvalidOperationException("串口正在关闭或驱动尚未释放，请重新启动 cast");
            if (_connection.State == SerialConnectionState.Open) throw new InvalidOperationException("请先关闭当前串口");
            var port = GetPorts().FirstOrDefault(p => p.PortName.Equals(profile.PortName, StringComparison.OrdinalIgnoreCase)
                && (profile.DeviceInstanceId is null || p.DeviceInstanceId == profile.DeviceInstanceId));
            if (port is null) throw new InvalidOperationException("串口已移除或硬件已更换，请刷新后重新选择");
            profile.DeviceInstanceId = port.DeviceInstanceId;
            Document.Profile = profile;
            lock (_sync) _decoder = TextCodec.GetEncoding(profile.Encoding).GetDecoder();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Observe(_connection.OpenAsync(profile, timeout.Token)).WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { _transportUnavailable = true; throw new TimeoutException("打开串口超时，请检查设备后重新启动 cast"); }
            AddLog("SYS", $"已打开 {profile.PortName} · {profile.BaudRate}", [], "连接");
            try { await _store.SaveAsync(_path, Document); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog("SYS", "串口已连接，保存参数失败：" + ex.Message, [], "配置"); }
        }
        finally { _lifecycle.Release(); PublishStatus(); }
    }

    public async Task DisconnectAsync()
    {
        _disconnecting = true;
        Stop();
        await _lifecycle.WaitAsync();
        try
        {
            await Observe(_connection.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            await Observe(_runner).WaitAsync(TimeSpan.FromSeconds(1));
            AddLog("SYS", "串口已关闭", [], "连接");
        }
        catch (TimeoutException)
        {
            _transportUnavailable = true;
            throw new TimeoutException("关闭串口超时，发送已停用；请重新启动 cast");
        }
        finally { _disconnecting = false; _lifecycle.Release(); PublishStatus(); }
    }

    public EncodedPayload Encode(SendRequest request)
    {
        if (request.Text is null || request.Text.Length > 1_048_576) throw new FormatException("发送内容超出限制");
        var ending = request.Ending switch
        {
            "none" => LineEndingMode.None, "cr" => LineEndingMode.Cr, "lf" => LineEndingMode.Lf,
            "crlf" => LineEndingMode.CrLf, "custom" => LineEndingMode.CustomHex,
            _ => throw new FormatException("发送后缀无效")
        };
        return PayloadCodec.Encode(request.Text, request.Hex ? DataMode.Hex : DataMode.Text,
            Document.Profile.Encoding, ending, request.CustomEnding);
    }

    private byte[][] Prepare(SendRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new FormatException("请输入发送内容");
        return request.Lines
            ? request.Text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0)
                .Select(line => Encode(request with { Text = line, Lines = false }).Bytes).ToArray()
            : [Encode(request).Bytes];
    }

    public async Task SendAsync(SendRequest request)
    {
        var frames = Prepare(request);
        Claim("manual", "手动发送");
        var token = _runCancellation!.Token;
        _runner = SendManualCoreAsync(frames, token);
        await _runner;
    }

    private async Task SendManualCoreAsync(byte[][] frames, CancellationToken token)
    {
        try { foreach (var bytes in frames) await WriteAsync(bytes, "手动", token); }
        finally { FinishRun(); }
    }

    public void StartRepeat(SendRequest request, int interval)
    {
        if (interval is < 20 or > 86_400_000) throw new FormatException("循环间隔须为 20 至 86400000 ms");
        var frames = Prepare(request);
        Claim("repeat", "定时循环");
        var token = _runCancellation!.Token;
        _runner = RunGuardedAsync(async () =>
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var bytes in frames) await WriteAsync(bytes, "循环", token);
                await Task.Delay(interval, token);
            }
        });
    }

    public void StartWorkflow(string id, string mode, bool stepOnly = false)
    {
        if (mode is not ("once" or "loop" or "count")) throw new FormatException("工作流运行模式无效");
        if (!Document.Workflows.TryGetValue(id, out var workflow) || workflow.Steps.Count == 0)
            throw new InvalidOperationException("工作流无可用步骤");
        var steps = workflow.Steps.Select(step =>
        {
            var preset = Document.Presets.FirstOrDefault(p => p.Id == step.PresetId)
                ?? throw new InvalidOperationException("工作流引用的预设已删除");
            return (Name: preset.Name, Bytes: Encode(new(preset.Content, preset.Format == "hex", preset.Format == "hex" ? "none" : "crlf")).Bytes, Wait: step.Wait);
        }).ToArray();
        Claim("workflow", workflow.Name);
        var token = _runCancellation!.Token;
        lock (_sync)
        {
            _runSignal = new SemaphoreSlim(stepOnly ? 1 : 0, 1);
            _run = _run with { Paused = stepOnly };
        }
        _runner = RunGuardedAsync(async () =>
        {
            var rounds = mode == "once" ? 1 : mode == "count" ? 5 : int.MaxValue;
            for (var round = 1; round <= rounds; round++)
                for (var index = 0; index < steps.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    bool paused;
                    lock (_sync) paused = _run.Paused;
                    if (paused) await _runSignal.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    lock (_sync) _run = _run with { Step = index, Round = round };
                    PublishStatus();
                    await WriteAsync(steps[index].Bytes, steps[index].Name, token);
                    await Task.Delay(steps[index].Wait, token);
                }
            AddLog("SYS", "工作流执行完成", [], "工作流");
        });
    }

    private void Claim(string kind, string name)
    {
        lock (_sync)
        {
            if (_disposed || _disconnecting || _transportUnavailable || _connection.State != SerialConnectionState.Open)
                throw new InvalidOperationException("请先打开串口");
            if (_saving) throw new InvalidOperationException("正在保存配置，请稍后重试");
            if (_run.Kind != "idle") throw new InvalidOperationException("发送通道正在运行，请先停止当前任务");
            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            _run = new(kind, false, -1, 0, name);
        }
        PublishStatus();
    }

    public void Pause(bool paused)
    {
        lock (_sync)
        {
            if (_run.Kind != "workflow") throw new InvalidOperationException("当前没有运行中的工作流");
            _run = _run with { Paused = paused };
            if (!paused && _runSignal.CurrentCount == 0) _runSignal.Release();
            if (paused) while (_runSignal.Wait(0)) { }
        }
        PublishStatus();
    }

    public void Step()
    {
        lock (_sync)
        {
            if (_run.Kind != "workflow" || !_run.Paused) throw new InvalidOperationException("暂停工作流后可单步执行");
            if (_runSignal.CurrentCount == 0) _runSignal.Release();
        }
    }

    public void Stop()
    {
        lock (_sync) _runCancellation?.Cancel();
    }

    private async Task RunGuardedAsync(Func<Task> run)
    {
        try { await run(); }
        catch (OperationCanceledException) { AddLog("SYS", "发送任务已停止", [], "运行"); }
        catch (Exception ex) { AddLog("SYS", "发送任务失败：" + ex.Message, [], "错误"); }
        finally { FinishRun(); }
    }

    private void FinishRun()
    {
        lock (_sync) _run = new("idle", false, -1, 0, "");
        PublishStatus();
    }

    private async Task WriteAsync(byte[] bytes, string source, CancellationToken token)
    {
        await _sendGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await Observe(_connection.SendAsync(bytes, token)).WaitAsync(TimeSpan.FromSeconds(30), token);
            Interlocked.Add(ref _tx, bytes.Length);
            AddLog("TX", TextCodec.Decode(bytes, Document.Profile.Encoding), bytes, source);
            PublishStatus();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // An uncooperative transport may still own a native write after cancellation.
            // Keep subsequent sends disabled until the close has completed.
            _transportUnavailable = true;
            try { await Observe(_connection.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(3)); _transportUnavailable = false; }
            catch (Exception closeError) { System.Diagnostics.Trace.WriteLine(closeError); }
            throw;
        }
        finally { _sendGate.Release(); }
    }

    private void OnData(object? sender, SerialDataReceivedEventArgs e)
    {
        if (_disposed || _disconnecting || _transportUnavailable || _connection.State != SerialConnectionState.Open) return;
        string text;
        lock (_sync)
        {
            var chars = new char[TextCodec.GetEncoding(Document.Profile.Encoding).GetMaxCharCount(e.Bytes.Length)];
            var count = _decoder.GetChars(e.Bytes, 0, e.Bytes.Length, chars, 0, false);
            text = new string(chars, 0, count);
        }
        Interlocked.Add(ref _rx, e.Bytes.Length);
        AddLog("RX", text, e.Bytes, "串口", e.Timestamp);
        PublishStatus();
    }

    private void OnError(object? sender, SerialConnectionErrorEventArgs e)
    {
        Stop();
        AddLog("SYS", e.Message, [], "错误");
        PublishStatus();
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (_connection.State != SerialConnectionState.Open) Stop();
        PublishStatus();
    }

    private void AddLog(string direction, string text, byte[] bytes, string source, DateTimeOffset? timestamp = null)
    {
        var entry = new AppLog(Interlocked.Increment(ref _logId), timestamp ?? DateTimeOffset.Now,
            direction, text, HexCodec.Format(bytes), bytes.Length, source);
        lock (_sync)
        {
            _logs.Enqueue(entry);
            _logTextBytes += LogTextBytes(entry);
            while (_logs.Count > MaxLogCount || (_logTextBytes > MaxLogTextBytes && _logs.Count > 1))
                _logTextBytes -= LogTextBytes(_logs.Dequeue());
        }
        Changed?.Invoke("log", entry);
    }

    public void ClearLogs() { lock (_sync) { _logs.Clear(); _logTextBytes = 0; } }
    public void ResetStats() { Interlocked.Exchange(ref _tx, 0); Interlocked.Exchange(ref _rx, 0); PublishStatus(); }
    public AppStatus Status()
    {
        SerialPinState? pins = null;
        string? pinError = null;
        var connected = !_transportUnavailable && _connection.State == SerialConnectionState.Open;
        if (connected && _connection is ISerialPinControl control)
        {
            try { pins = control.ReadPins(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { pinError = ex.Message; }
        }
        RunStatus run;
        lock (_sync) run = _run;
        return new(connected, _connection.Port?.PortName ?? "", Interlocked.Read(ref _tx), Interlocked.Read(ref _rx), run, pins, pinError);
    }

    public void SetPins(bool dtr, bool rts)
    {
        if (_connection.State != SerialConnectionState.Open || _connection is not ISerialPinControl pins)
            throw new InvalidOperationException("当前串口无法控制引脚");
        pins.SetPins(dtr, rts);
        Document.Profile.DtrEnable = dtr;
        if (Document.Profile.FlowControl is not (FlowControlSetting.RtsCts or FlowControlSetting.RtsCtsAndXOnXOff)) Document.Profile.RtsEnable = rts;
        PublishStatus();
    }

    public void PublishStatus() => Changed?.Invoke("status", Status());

    public static Task Observe(Task task)
    {
        _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _connection.DataReceived -= OnData;
        _connection.Error -= OnError;
        _connection.StateChanged -= OnConnectionChanged;
        try { await Observe(Task.Run(async () => await _connection.DisposeAsync())).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        try { await Observe(_runner).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }
}

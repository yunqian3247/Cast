using System.Buffers;
using System.Text;
using System.Text.Json;
using cast.Core;
using cast.Serial;

namespace cast.Desktop;

public sealed class AppService : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly ISerialConnection _connection;
    private readonly IPortCatalog _catalog;
    private readonly JsonFileStore<AppDocument> _store = new(Json, static document => document.Validate());
    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _documentGate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _changeSync = new();
    private readonly object _portSync = new();
    private readonly object _receiveSync = new();
    private Task<IReadOnlyList<PortInfo>>? _portScan;
    public const long MaxLogTextBytes = 16 * 1024 * 1024;
    private MonitorSettings _monitorSettings = new();
    public MonitorSettings MonitorSettings => Volatile.Read(ref _monitorSettings);
    private readonly Queue<AppLog> _logs = new();
    private long _logTextBytes;
    public static long LogTextBytes(AppLog log) => 2L * (log.Text.Length + log.Hex.Length);
    private CancellationTokenSource? _runCancellation;
    private Task _runner = Task.CompletedTask;
    private SemaphoreSlim _runSignal = new(0, 1);
    private RunStatus _run = new("idle", false, -1, 0, "");
    private Decoder _decoder = TextCodec.GetEncoding(TextEncodingKind.Utf8).GetDecoder();
    private long _tx, _rx, _logId, _droppedLogs;
    private ReceiveFramer _framer = new(new());
    private ReliabilitySettings _reliability = new(new());
    private string[] _framePresets = [];
    private readonly System.Threading.Timer _receiveIdle;
    private DateTimeOffset _receiveTimestamp;
    private ContinuousLogWriter? _continuousLog;
    private ResponseWaiter? _responseWaiter;
    public string LogDirectory { get; }
    private bool _disposed, _disconnecting, _saving, _transportUnavailable;
    public AppDocument Document { get; private set; } = new();
    public event Action<string, object>? Changed;
    public IReadOnlyList<AppLog> Logs { get { lock (_sync) return _logs.ToArray(); } }

    public AppService(string directory, ISerialConnection? connection = null, IPortCatalog? catalog = null, string? legacyPath = null)
    {
        _path = Path.Combine(directory, "cast.json");
        LogDirectory = Path.Combine(directory, "Logs");
        _receiveIdle = new(_ => { lock (_receiveSync) if (!_disposed) FlushReceiveCore(); }, null, Timeout.Infinite, Timeout.Infinite);
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
            if (saved is not null) { saved.Validate(); Document = saved; ApplyMonitorSettings(saved); await ApplyReliabilityAsync(saved); }
        }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException)
        {
            AddLog("SYS", "读取配置失败：" + ex.Message, [], "配置");
        }
        AddLog("SYS", "cast 已就绪", [], "系统");
    }

    public Task<IReadOnlyList<PortInfo>> GetPortsAsync()
    {
        lock (_portSync)
        {
            // Share an active native scan between refresh and connection requests.
            if (_portScan is null || _portScan.IsCompleted) _portScan = Task.Run(_catalog.Enumerate);
            return _portScan;
        }
    }

    public async Task SaveAsync(AppDocument document, bool closing = false)
    {
        document.Validate();
        var snapshot = JsonSerializer.Deserialize<AppDocument>(JsonSerializer.Serialize(document, Json), Json)!;
        await _documentGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (!closing && _run.Kind != "idle") throw new InvalidOperationException("运行期间请先停止任务再保存编辑");
                if (_disposed) throw new InvalidOperationException("cast 正在关闭");
                _saving = true;
                if (_connection.State == SerialConnectionState.Open) snapshot.Profile = Document.Profile;
            }
            await _store.SaveAsync(_path, snapshot);
            Document = snapshot;
            ApplyMonitorSettings(snapshot);
            await ApplyReliabilityAsync(snapshot);
        }
        finally { lock (_sync) _saving = false; _documentGate.Release(); }
    }

    private void ApplyMonitorSettings(AppDocument document)
    {
        lock (_sync)
        {
            Volatile.Write(ref _monitorSettings, MonitorSettings.FromUi(document.Ui));
            TrimLogs();
        }
    }

    private void TrimLogs()
    {
        while (_logs.Count > _monitorSettings.MaxLogCount || (_logTextBytes > MaxLogTextBytes && _logs.Count > 1))
        { _logTextBytes -= LogTextBytes(_logs.Dequeue()); Interlocked.Increment(ref _droppedLogs); }
    }

    private async Task ApplyReliabilityAsync(AppDocument document)
    {
        var next = ReliabilitySettings.FromUi(document.Ui);
        var previousSettings = _reliability;
        var framePresets = next.Framing.Mode == "auto"
            ? document.Presets.Where(p => p.Format == "hex").Select(p => p.Content).ToArray() : [];
        lock (_receiveSync)
        {
            if (next.Framing != previousSettings.Framing || !framePresets.SequenceEqual(_framePresets))
            {
                FlushReceiveCore();
                _framer = new(next.Framing, framePresets.Select(HexCodec.Parse));
                _framePresets = framePresets;
            }
            _reliability = next;
        }
        if (next.ContinuousLog != previousSettings.ContinuousLog)
        {
            ContinuousLogWriter? previous;
            lock (_changeSync)
            {
                previous = _continuousLog;
                _continuousLog = next.ContinuousLog ? new(LogDirectory, next.LogFileMiB, next.LogFiles) : null;
            }
            if (previous is not null) await previous.DisposeAsync();
        }
        else _continuousLog?.Configure(next.LogFileMiB, next.LogFiles);
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
        await _documentGate.WaitAsync();
        try
        {
            if (_disposed || _disconnecting || _transportUnavailable) throw new InvalidOperationException("串口正在关闭或驱动尚未释放，请重新启动 cast");
            if (_connection.State == SerialConnectionState.Open) throw new InvalidOperationException("请先关闭当前串口");
            var ports = await GetPortsAsync();
            if (_disposed || _disconnecting || _transportUnavailable) throw new InvalidOperationException("cast 正在关闭或串口不可用");
            var port = ports.FirstOrDefault(p => p.PortName.Equals(profile.PortName, StringComparison.OrdinalIgnoreCase)
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
        finally { _documentGate.Release(); _lifecycle.Release(); PublishStatus(); }
    }

    public async Task DisconnectAsync()
    {
        _disconnecting = true;
        Stop();
        FlushReceive();
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

    public const int MaxSendChars = 1_048_576;
    public const int MaxSendLines = 100_000;
    public const int MaxSendBytes = 4 * 1024 * 1024;

    private static string[] SendParts(SendRequest request)
    {
        if (request.Text is null || request.Text.Length > MaxSendChars || request.CustomEnding is null || request.CustomEnding.Length > MaxSendChars)
            throw new FormatException("发送内容或后缀超过 1 Mi 字符");
        if (request.Lines && request.Text.Count(c => c == '\n') >= MaxSendLines)
            throw new FormatException("逐行发送最多 100000 行");
        return request.Lines ? request.Text.Replace("\r\n", "\n").Split('\n') : [request.Text];
    }

    private IEnumerable<byte[]> EncodeFrames(SendRequest request)
    {
        var parts = SendParts(request);
        long total = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            if (request.Lines && parts[index].Length == 0) continue;
            byte[] bytes;
            try { bytes = Encode(request with { Text = parts[index], Lines = false }).Bytes; }
            catch (PayloadEncodeException ex) when (request.Lines)
            { throw new PayloadEncodeException(ex.ErrorCode, $"第 {index + 1} 行：{ex.Message}", ex); }
            total += bytes.Length;
            if (total > MaxSendBytes) throw new FormatException("本次发送的编码总量超过 4 MiB");
            yield return bytes;
        }
    }

    public SendPreview Preview(SendRequest request)
    {
        var total = EncodeFrames(request).Sum(bytes => (long)bytes.Length);
        return new(request.Text.Length == 0 ? 0 : request.Text.Count(c => c == '\n') + 1, total);
    }

    private byte[][] Prepare(SendRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new FormatException("请输入发送内容");
        return EncodeFrames(request).ToArray();
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

    public void StartWorkflow(string id, string mode, bool stepOnly = false, int count = 5)
    {
        if (mode is not ("once" or "loop" or "count")) throw new FormatException("工作流运行模式无效");
        if (count is < 1 or > 1000000) throw new FormatException("工作流循环次数须为 1～1000000");
        if (!Document.Workflows.TryGetValue(id, out var workflow) || workflow.Steps.Count == 0)
            throw new InvalidOperationException("工作流无可用步骤");
        var encodedPresets = new Dictionary<string, (string Name, byte[] Bytes)>(StringComparer.Ordinal);
        var steps = workflow.Steps.Select(step =>
        {
            step.Validate();
            if (!encodedPresets.TryGetValue(step.PresetId, out var encoded))
            {
                var preset = Document.Presets.FirstOrDefault(p => p.Id == step.PresetId)
                    ?? throw new InvalidOperationException("工作流引用的预设已删除");
                encoded = (preset.Name, Encode(new(preset.Content, preset.Format == "hex", preset.Format == "hex" ? "none" : "crlf")).Bytes);
                encodedPresets.Add(step.PresetId, encoded);
            }

            if (step.WaitForResponse && step.ResponseFormat == "text") _ = TextCodec.Encode(step.Response, Document.Profile.Encoding);
            return (Name: encoded.Name, Bytes: encoded.Bytes, Wait: step.Wait, step.WaitForResponse, step.Response, step.ResponseFormat, step.TimeoutMs, step.Retries);
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
            var rounds = mode == "once" ? 1 : mode == "count" ? count : long.MaxValue;
            for (long round = 1; round <= rounds; round++)
                for (var index = 0; index < steps.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    bool paused;
                    lock (_sync) paused = _run.Paused;
                    if (paused) await _runSignal.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    lock (_sync) _run = _run with { Step = index, Round = (int)Math.Min(round, int.MaxValue) };
                    PublishStatus();
                    var step = steps[index];
                    for (var attempt = 0; ; attempt++)
                    {
                        var response = step.WaitForResponse ? new ResponseWaiter(step.Response, step.ResponseFormat, Document.Profile.Encoding) : null;
                        var awaitingResponse = false;
                        lock (_receiveSync) _responseWaiter = response;
                        try
                        {
                            await WriteAsync(step.Bytes, step.Name, token);
                            if (response is not null) { awaitingResponse = true; await response.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(step.TimeoutMs), token); }
                            break;
                        }
                        catch (TimeoutException) when (awaitingResponse && attempt < step.Retries)
                        { AddLog("SYS", $"{step.Name} 应答超时，重试 {attempt + 1}/{step.Retries}", [], "工作流"); }
                        catch (TimeoutException ex) when (awaitingResponse)
                        { throw new TimeoutException($"{step.Name} 等待应答超时（{step.TimeoutMs} ms），工作流已停止", ex); }
                        finally { lock (_receiveSync) if (_responseWaiter == response) _responseWaiter = null; }
                    }
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
        catch (Exception ex) { DiagnosticLog.Write("发送任务", ex); AddLog("SYS", "发送任务失败：" + ex.Message, [], "错误"); }
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
            var deadline = SerialWriteTiming.GetTimeout(bytes.Length, Document.Profile) + TimeSpan.FromSeconds(3);
            await Observe(_connection.SendAsync(bytes, token)).WaitAsync(deadline, token);
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
        Interlocked.Add(ref _rx, e.Bytes.Length);
        lock (_receiveSync)
        {
            _responseWaiter?.Feed(e.Bytes);
            if (_framer.BufferedBytes == 0) _receiveTimestamp = e.Timestamp;
            foreach (var frame in _framer.Feed(e.Bytes)) ReceiveFrame(frame, _receiveTimestamp);
            if (_framer.BufferedBytes > 0) _receiveIdle.Change(_reliability.Framing.IdleMs, Timeout.Infinite);
            else _receiveIdle.Change(Timeout.Infinite, Timeout.Infinite);
        }
        PublishStatus();
    }

    private void FlushReceive()
    {
        lock (_receiveSync) FlushReceiveCore();
    }

    private void FlushReceiveCore()
    {
        _receiveIdle.Change(Timeout.Infinite, Timeout.Infinite);
        var bytes = _framer.Flush();
        if (bytes.Length > 0) ReceiveFrame(bytes, _receiveTimestamp);
    }

    private void ReceiveFrame(byte[] bytes, DateTimeOffset timestamp)
    {
        string text;
        lock (_sync)
        {
            var chars = ArrayPool<char>.Shared.Rent(TextCodec.GetEncoding(Document.Profile.Encoding).GetMaxCharCount(bytes.Length));
            try
            {
                var count = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0, false);
                text = new string(chars, 0, count);
            }
            finally { ArrayPool<char>.Shared.Return(chars); }
        }
        AddLog("RX", text, bytes, "串口", timestamp);
    }

    private void OnError(object? sender, SerialConnectionErrorEventArgs e)
    {
        Stop();
        if (e.Exception is not null) DiagnosticLog.Write("串口", e.Exception);
        AddLog("SYS", e.Message, [], "错误");
        PublishStatus();
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (_connection.State != SerialConnectionState.Open) { Stop(); FlushReceive(); }
        PublishStatus();
    }

    private void AddLog(string direction, string text, byte[] bytes, string source, DateTimeOffset? timestamp = null)
    {
        var hex = HexCodec.Format(bytes);
        // Commit the queue entry and its notification together. This keeps the
        // UI delivery order equal to the backend log order under concurrent RX/TX.
        lock (_changeSync)
        {
            AppLog entry;
            lock (_sync)
            {
                entry = new AppLog(++_logId, timestamp ?? DateTimeOffset.Now,
                    direction, text, hex, bytes.Length, source);
                _logs.Enqueue(entry);
                _logTextBytes += LogTextBytes(entry);
                TrimLogs();
            }
            _continuousLog?.Append(entry);
            Changed?.Invoke("log", entry);
        }
    }

    public void ClearLogs()
    {
        lock (_changeSync)
        {
            lock (_sync) { _logs.Clear(); _logTextBytes = 0; }
        }
    }
    public void ResetStats() { Interlocked.Exchange(ref _tx, 0); Interlocked.Exchange(ref _rx, 0); Interlocked.Exchange(ref _droppedLogs, 0); _continuousLog?.ResetDropped(); PublishStatus(); }
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
        return new(connected, _connection.Port?.PortName ?? "", Interlocked.Read(ref _tx), Interlocked.Read(ref _rx), run, pins, pinError,
            Interlocked.Read(ref _droppedLogs), _continuousLog?.Error, _continuousLog?.Dropped ?? 0, LogDirectory);
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
        FlushReceive();
        _connection.DataReceived -= OnData;
        _connection.Error -= OnError;
        _connection.StateChanged -= OnConnectionChanged;
        try { await Observe(Task.Run(async () => await _connection.DisposeAsync())).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        try { await Observe(_runner).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        if (_continuousLog is not null)
        {
            try { await Observe(_continuousLog.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { DiagnosticLog.Write("关闭持续日志", ex); }
        }
        _receiveIdle.Dispose();
    }
}

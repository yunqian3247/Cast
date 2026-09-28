using System.Text.Json;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using cast.Core;
using cast.Serial;

namespace cast.Desktop;

public sealed class MainForm : Form
{
    private const string PageUrl = "https://cast.example/index.html";
    private const int MaxBridgeMessageChars = 4_194_304;
    private const long MaxEventBatchBytes = 512 * 1024;
    private static readonly Icon AppIcon = new(typeof(MainForm), "Assets.cast.ico");
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
    private readonly AppService _service;
    private readonly AppUpdateService _updates;
    private readonly string _directory;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 750 };
    private readonly System.Windows.Forms.Timer _events = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _startupTimeout = new() { Interval = 120000 };
    private static readonly JsonSerializerOptions BridgeJson = new(JsonSerializerDefaults.Web);
    private Task<IReadOnlyList<PortInfo>>? _startupPorts;
    private readonly object _eventSync = new();
    private readonly Queue<AppLog> _pendingLogs = new();
    private long _pendingLogBytes, _lastLogSentId;
    private object? _pendingStatus;
    private readonly Label _loading = new() { Dock = DockStyle.Fill, Text = "正在打开cast", TextAlign = ContentAlignment.MiddleCenter, UseCompatibleTextRendering = true };
    private readonly PrivateFontCollection _loadingFonts = new();
    private readonly Font _loadingFont;
    private StartupSplash? _startupSplash;
    private bool _ready, _closing, _closed, _restartRequested;
    private bool _pageInitialized, _pageReady, _navigationCompleted, _startupFailed;
    private bool _preparingPage, _startupFramePrepared;
    private int _pageGeneration;
    private bool _nativeNonNormal;
    private LogExportSession? _export;
    private bool _exportOpening, _closePending;
    private readonly DocumentSaveBuffer _documentTransfer = new();
    private TaskCompletionSource<string?>? _closeResult;
    private string? _closeId;
    public WebView2 Browser => _web;
    public AppService Service => _service;
    public bool Ready => _ready;
    internal StartupSplash? StartupAnimation => _startupSplash;
    internal bool StartupFramePrepared => _startupFramePrepared;

    public MainForm(string? dataDirectory = null, ISerialConnection? connection = null, IPortCatalog? catalog = null)
    {
        _loadingFonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Web", "fonts", "SarasaGothicSC-Regular.ttf"));
        _loadingFont = new Font(_loadingFonts.Families[0], 9F);
        _loading.Font = _loadingFont;
        Opacity = 0;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = dataDirectory ?? Path.Combine(localAppData, "cast", "Data");
        _service = new(_directory, connection, catalog, dataDirectory is null ? LegacyDataMigration.SettingsPath(localAppData) : null);
        _updates = new(Path.Combine(AppContext.BaseDirectory, "update-settings.json"));
        _updates.Changed += OnUpdateChanged;
        Text = "cast";
        Icon = AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(1120, 700);
        MinimumSize = new Size(880, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Controls.Add(_web);
        Controls.Add(_loading);
        _loading.BringToFront();
        _service.Changed += OnServiceChanged;
        Shown += async (_, _) =>
        {
            _startupSplash = new(AppIcon, _loadingFont);
            _startupSplash.CloseRequested += (_, _) => Close();
            await InitializeAsync();
        };
        _poll.Tick += (_, _) => { if (_ready && !_closing) _service.PublishStatus(); };
        _events.Tick += (_, _) => FlushEvents();
        _startupTimeout.Tick += (_, _) => FailStartup("cast 初始化超时，请关闭后重试。");
        Resize += (_, _) => { if (_ready) Post(new { @event = "window", data = WindowStatus() }); };
        FormClosing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closePending || _closing) return;
            _closePending = true;
            _service.Stop();
            try
            {
                if (_ready && !_restartRequested)
                {
                    _closeId = Guid.NewGuid().ToString("N");
                    _closeResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Post(new { @event = "closing", data = new { id = _closeId } });
                    var error = await _closeResult.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    if (error is not null) throw new IOException(error);
                }
            }
            catch (Exception ex)
            {
                _closePending = false; _closeResult = null; _closeId = null;
                Post(new { @event = "closeCancelled", data = new { error = "关闭前保存失败：" + ex.Message } });
                return;
            }
            _closing = true;
            _startupSplash?.Dismiss();
            _updates.Dispose();
            _poll.Stop();
            _events.Stop();
            _startupTimeout.Stop();
            Enabled = false;
            ExportAbort(); _documentTransfer.Abort();
            await _service.DisposeAsync();
            _closed = true;
            Close();
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            _startupTimeout.Start();
            var serviceInitialization = Task.Run(_service.InitializeAsync);
            _ = AppService.Observe(serviceInitialization);
            _startupPorts = _service.GetPortsAsync();
            _ = AppService.Observe(_startupPorts);
            var environmentCreation = CoreWebView2Environment.CreateAsync(null, Path.Combine(_directory, "Browser"));
            var environment = await environmentCreation;
            if (_closing || _startupFailed) return;
            await _web.EnsureCoreWebView2Async(environment);
            await serviceInitialization;
            if (_closing || _startupFailed) return;
            ApplyMonitorSettings();
            var core = _web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("cast.example", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) =>
            {
                e.Cancel = e.Uri != PageUrl;
                if (e.Cancel || _closing || _startupFailed) return;
                _ready = _pageInitialized = _pageReady = _navigationCompleted = false;
                _preparingPage = _startupFramePrepared = false;
                _pageGeneration++;
                _documentTransfer.Abort(); ExportAbort();
                _poll.Stop();
                _events.Stop();
                lock (_eventSync) { _pendingLogs.Clear(); _pendingLogBytes = 0; _pendingStatus = null; }
                _loading.Visible = true;
                _loading.BringToFront();
                _startupTimeout.Start();
            };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += HandleMessage;
            core.NavigationCompleted += (_, e) =>
            {
                if (_closing) return;
                if (e.IsSuccess) { _navigationCompleted = true; TryShowPage(); }
                else FailStartup("cast 加载失败：" + e.WebErrorStatus);
            };
            core.Navigate(PageUrl);
        }
        catch (Exception ex)
        {
            FailStartup("无法打开 cast。请检查 Microsoft Edge WebView2 Runtime。\n" + ex.Message);
        }
    }

    private async void TryShowPage()
    {
        if (_closing || _startupFailed || _ready || _preparingPage || !_navigationCompleted || !_pageReady) return;
        _preparingPage = true;
        var generation = _pageGeneration;
        try
        {
            _loading.Visible = false;
            var core = _web.CoreWebView2;
            var colors = await core.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor.match(/[\\d.]+/g).slice(0,3).map(Number)");
            if (_closing || IsDisposed || _startupFailed || generation != _pageGeneration) return;
            var rgb = JsonSerializer.Deserialize<int[]>(colors);
            if (rgb is { Length: 3 })
            {
                BackColor = _web.DefaultBackgroundColor = Color.FromArgb(rgb[0], rgb[1], rgb[2]);
                _loading.BackColor = BackColor;
                _loading.ForeColor = BackColor.GetBrightness() < .5F ? Color.Gainsboro : Color.Black;
            }
            // Warm the actual WebView surface after fonts and layout are ready.
            await using var frame = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, frame);
            if (_closing || IsDisposed || _startupFailed || generation != _pageGeneration) return;
            if (frame.Length == 0) throw new IOException("主界面首帧尚未完成");
            _startupFramePrepared = true;
            _startupTimeout.Stop();
            _ready = true;
            _poll.Start(); _events.Start();
            _service.PublishStatus();
            Post(new { @event = "window", data = WindowStatus() });
            RevealStartup();
        }
        catch (Exception ex)
        {
            if (!_closing && !IsDisposed && generation == _pageGeneration)
                FailStartup("cast 主界面准备失败：" + ex.Message);
        }
        finally { if (generation == _pageGeneration) _preparingPage = false; }
    }

    private bool PageReady()
    {
        if (!_pageInitialized || _startupFailed) throw new InvalidOperationException("cast 初始化尚未完成或已中止");
        _pageReady = true;
        TryShowPage();
        return true;
    }

    private bool FailStartup(string message)
    {
        if (_closing || IsDisposed) return false;
        _startupFailed = true;
        _ready = false;
        _startupTimeout.Stop();
        _poll.Stop();
        _events.Stop();
        _loading.Text = message;
        _loading.Visible = true;
        _loading.BringToFront();
        RevealStartup(immediately: true);
        return false;
    }

    private void RevealStartup(bool immediately = false)
    {
        void Reveal()
        {
            if (_closing || IsDisposed) return;
            Opacity = 1;
            Activate();
        }
        if (_startupSplash is null || _startupSplash.IsDisposed) Reveal();
        else if (immediately || !_startupSplash.MotionEnabled)
        {
            _startupSplash.Dismiss(); Reveal();
        }
        else
        {
            _startupSplash.Complete(Reveal);
            _startupSplash.Show(this);
        }
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != PageUrl || _closing || _restartRequested) return;
        string? id = null;
        try
        {
            var json = e.WebMessageAsJson;
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 100) throw new FormatException("请求标识无效");
            if (json.Length > MaxBridgeMessageChars) throw new FormatException("请求数据超出限制");
            var command = root.GetProperty("command").GetString();
            if (_closePending && command is not ("save" or "saveStart" or "saveChunk" or "saveFinish" or "saveAbort" or "closeReady"))
                throw new InvalidOperationException("cast 正在保存并关闭");
            var data = root.GetProperty("data");
            object? result = command switch
            {
                "init" => await InitializePageAsync(),
                "ready" => PageReady(),
                "startupError" => FailStartup("cast 初始化失败：" + data.GetProperty("message").GetString()),
                "window" => WindowCommand(data),
                "updateStatus" => _updates.Status,
                "updateCheck" => await _updates.CheckAsync(),
                "updateDownload" => await _updates.DownloadAsync(),
                "updateInstall" => await InstallUpdate(data),
                "ports" => await _service.GetPortsAsync(),
                "connect" => await Connect(data),
                "disconnect" => await Disconnect(),
                "send" => await Send(data),
                "repeat" => Repeat(data),
                "workflow" => Workflow(data),
                "pause" => Pause(data),
                "step" => Step(),
                "stop" => Stop(),
                "save" => await Save(data),
                "saveStart" => SaveStart(data),
                "saveChunk" => SaveChunk(data),
                "saveFinish" => await SaveFinish(data),
                "saveAbort" => SaveAbort(data),
                "closeReady" => CloseReady(data),
                "encode" => Encode(data),
                "validateSend" => _service.Preview(Read<SendRequest>(data)),
                "pins" => Pins(data),
                "resetStats" => ResetStats(),
                "clearLogs" => ClearLogs(),
                "copy" => Copy(data),
                "exportStart" => ExportStart(data),
                "exportChunk" => await ExportChunk(data),
                "exportFinish" => await ExportFinish(),
                "exportAbort" => ExportAbort(),
                "export" => await Export(data),
                "import" => await Import(),
                _ => throw new FormatException("未知操作")
            };
            Post(new { id, ok = true, result });
        }
        catch (Exception ex) { Post(new { id, ok = false, error = ex.Message }); }
    }

    private async Task<object> InitializePageAsync()
    {
        IReadOnlyList<PortInfo> ports = [];
        string? error = null;
        try { ports = await (_startupPorts ?? _service.GetPortsAsync()); } catch (Exception ex) { error = ex.Message; }
        finally { _startupPorts = null; }
        _pageInitialized = true;
        var logs = _service.Logs;
        _lastLogSentId = logs.LastOrDefault()?.Id ?? _lastLogSentId;
        return new { document = _service.Document, ports, portError = error, logs, status = _service.Status(), window = WindowStatus(), update = _updates.Status, version = typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion };
    }

    private async Task<object> InstallUpdate(JsonElement data)
    {
        if (!_updates.Status.CanInstall) throw new InvalidOperationException("请先下载更新");
        if (_service.Status().Run.Kind != "idle") throw new InvalidOperationException("请先停止发送或工作流，再安装更新");
        _restartRequested = true;
        try
        {
            var document = data.TryGetProperty("useSavedDocument", out var saved) && saved.GetBoolean()
                ? _service.Document : Read<AppDocument>(data);
            await _service.SaveAsync(document);
            _updates.PrepareRestart();
            BeginInvoke(Close);
            return _updates.Status;
        }
        catch { _restartRequested = false; throw; }
    }

    private void OnUpdateChanged(AppUpdateStatus status)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnUpdateChanged(_updates.Status)); } catch (InvalidOperationException) { }
            return;
        }
        Post(new { @event = "update", data = status });
    }

    private object WindowStatus() => new { topMost = TopMost, maximized = WindowState == FormWindowState.Maximized };

    private object WindowCommand(JsonElement data)
    {
        switch (data.GetProperty("action").GetString())
        {
            case "pin": TopMost = !TopMost; break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "close": BeginInvoke(Close); break;
            default: throw new FormatException("窗口操作无效");
        }
        return WindowStatus();
    }

    // Keep the native resize frame and system menu while the page draws the caption.
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~0x00C00000; // WS_CAPTION
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WmNcCalcSize = 0x0083;
        const int WmWindowPosChanged = 0x0047;
        if (m.Msg == WmWindowPosChanged)
        {
            var nonNormal = IsZoomed(Handle) || IsIconic(Handle);
            var leavingNormal = nonNormal && !_nativeNonNormal;
            _nativeNonNormal = nonNormal;
            var restoreBounds = Bounds;
            base.WndProc(ref m);
            // Store outer bounds so WinForms restores the custom frame size exactly.
            if (leavingNormal) SetBoundsCore(restoreBounds.X, restoreBounds.Y, restoreBounds.Width, restoreBounds.Height, BoundsSpecified.All);
            return;
        }
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero && !IsZoomed(Handle))
        {
            var windowTop = Marshal.ReadInt32(m.LParam, sizeof(int));
            base.WndProc(ref m);
            // Extend the caption buttons through the native top frame.
            Marshal.WriteInt32(m.LParam, sizeof(int), windowTop);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    private async Task<object> Connect(JsonElement data) { await _service.ConnectAsync(Read<SerialProfile>(data)); return _service.Status(); }
    private async Task<object> Disconnect() { await _service.DisconnectAsync(); return _service.Status(); }
    private async Task<object> Send(JsonElement data) { await _service.SendAsync(Read<SendRequest>(data)); return _service.Status(); }
    private object Repeat(JsonElement data) { _service.StartRepeat(Read<SendRequest>(data.GetProperty("request")), data.GetProperty("interval").GetInt32()); return _service.Status(); }
    private object Workflow(JsonElement data) { _service.StartWorkflow(data.GetProperty("id").GetString()!, data.GetProperty("mode").GetString()!, data.GetProperty("stepOnly").GetBoolean()); return _service.Status(); }
    private object Pause(JsonElement data) { _service.Pause(data.GetProperty("paused").GetBoolean()); return _service.Status(); }
    private object Step() { _service.Step(); return _service.Status(); }
    private object Stop() { _service.Stop(); return _service.Status(); }
    private async Task<object> Save(JsonElement data)
    {
        await _service.SaveAsync(Read<AppDocument>(data), closing: _closePending);
        ApplyMonitorSettings();
        return true;
    }

    private object SaveStart(JsonElement data)
    {
        _documentTransfer.Start(data.GetProperty("id").GetString()!, data.GetProperty("length").GetInt32());
        return true;
    }

    private object SaveChunk(JsonElement data)
    {
        _documentTransfer.Append(data.GetProperty("id").GetString()!, data.GetProperty("offset").GetInt32(),
            data.GetProperty("content").GetString()!);
        return true;
    }

    private async Task<object> SaveFinish(JsonElement data)
    {
        var document = _documentTransfer.Finish(data.GetProperty("id").GetString()!);
        await _service.SaveAsync(document, closing: _closePending);
        ApplyMonitorSettings();
        return true;
    }

    private object SaveAbort(JsonElement data)
    {
        _documentTransfer.Abort(data.GetProperty("id").GetString()); return true;
    }

    private object CloseReady(JsonElement data)
    {
        if (!_closePending || data.GetProperty("id").GetString() != _closeId)
            throw new InvalidOperationException("关闭请求已结束");
        _closeResult!.TrySetResult(data.TryGetProperty("error", out var error) ? error.GetString() : null);
        return true;
    }

    private void ApplyMonitorSettings()
    {
        var settings = _service.MonitorSettings;
        if (_events.Interval != settings.RefreshIntervalMs) _events.Interval = settings.RefreshIntervalMs;
        lock (_eventSync)
        {
            while (_pendingLogs.Count > settings.MaxLogCount || (_pendingLogBytes > AppService.MaxLogTextBytes && _pendingLogs.Count > 1))
                _pendingLogBytes -= AppService.LogTextBytes(_pendingLogs.Dequeue());
        }
    }
    private object Encode(JsonElement data) { var payload = _service.Encode(Read<SendRequest>(data)); return new { hex = HexCodec.Format(payload.Bytes), byteCount = payload.Bytes.Length }; }
    private object Pins(JsonElement data) { _service.SetPins(data.GetProperty("dtr").GetBoolean(), data.GetProperty("rts").GetBoolean()); return _service.Status(); }
    private object ResetStats() { _service.ResetStats(); return true; }
    private object ClearLogs()
    {
        _service.ClearLogs();
        lock (_eventSync) { _pendingLogs.Clear(); _pendingLogBytes = 0; }
        return true;
    }
    private static object Copy(JsonElement data)
    {
        var text = data.GetProperty("text").GetString() ?? "";
        if (text.Length > 1_048_576) throw new FormatException("复制内容超出限制");
        if (text.Length > 0) Clipboard.SetText(text);
        return true;
    }
    private static T Read<T>(JsonElement element) => element.Deserialize<T>(AppService.Json) ?? throw new FormatException("请求为空");

    private async Task<object> Export(JsonElement data)
    {
        var content = data.GetProperty("content").GetString() ?? "";
        if (!BeginExport(data)) return new { saved = false };
        try
        {
            for (var offset = 0; offset < content.Length;)
            {
                var end = Math.Min(content.Length, offset + 1_048_576);
                if (end < content.Length && char.IsHighSurrogate(content[end - 1])) end--;
                await _export!.AppendAsync(content[offset..end]);
                offset = end;
            }
            return await ExportFinish();
        }
        finally { ExportAbort(); }
    }

    private object ExportStart(JsonElement data) => new { saved = BeginExport(data) };

    private bool BeginExport(JsonElement data)
    {
        if (_export is not null || _exportOpening) throw new InvalidOperationException("已有导出任务正在进行");
        var format = data.GetProperty("format").GetString();
        if (format is not ("txt" or "csv" or "json")) throw new FormatException("导出格式无效");
        var fileName = Path.GetFileName(data.GetProperty("fileName").GetString());
        using var dialog = new SaveFileDialog { FileName = fileName, DefaultExt = format, Filter = $"{format.ToUpperInvariant()} 文件|*.{format}", AddExtension = true, OverwritePrompt = true };
        _exportOpening = true;
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            if (_closing || _closePending) throw new InvalidOperationException("cast 正在关闭");
            _export = new(dialog.FileName!, format!);
            return true;
        }
        finally { _exportOpening = false; }
    }

    private async Task<object> ExportChunk(JsonElement data)
    {
        if (_export is null) throw new InvalidOperationException("导出任务未开始");
        var content = data.GetProperty("content").GetString() ?? string.Empty;
        try { await _export.AppendAsync(content); return true; }
        catch { ExportAbort(); throw; }
    }

    private async Task<object> ExportFinish()
    {
        if (_export is null) throw new InvalidOperationException("导出任务未开始");
        try { await _export.FinishAsync(); return new { saved = true }; }
        finally { ExportAbort(); }
    }

    private bool ExportAbort()
    {
        _export?.Dispose(); _export = null;
        return true;
    }

    private async Task<object?> Import()
    {
        using var dialog = new OpenFileDialog { Filter = "预设文件 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        if (new FileInfo(dialog.FileName).Length > 4_194_304) throw new FormatException("预设文件超过 4 MB");
        return CommandPreset.ParseImport(await File.ReadAllTextAsync(dialog.FileName));
    }

    private void OnServiceChanged(string name, object data)
    {
        if (!_pageInitialized || _closing || IsDisposed || !IsHandleCreated) return;
        if (name is "log" or "status")
        {
            lock (_eventSync)
            {
                if (name == "status") _pendingStatus = data;
                else
                {
                    var log = (AppLog)data;
                    _pendingLogs.Enqueue(log); _pendingLogBytes += AppService.LogTextBytes(log);
                    while (_pendingLogs.Count > _service.MonitorSettings.MaxLogCount || (_pendingLogBytes > AppService.MaxLogTextBytes && _pendingLogs.Count > 1))
                        _pendingLogBytes -= AppService.LogTextBytes(_pendingLogs.Dequeue());
                }
            }
            return;
        }
        if (InvokeRequired) { try { BeginInvoke(() => OnServiceChanged(name, data)); } catch (InvalidOperationException) { } return; }
        Post(new { @event = name, data });
    }

    private void FlushEvents()
    {
        if (!_ready || _closing) return;
        AppLog[] logs;
        object? status;
        lock (_eventSync)
        {
            var batch = new List<AppLog>();
            long batchBytes = 0;
            while (_pendingLogs.Count > 0 && _pendingLogs.Peek().Id <= _lastLogSentId) {
                _pendingLogBytes -= AppService.LogTextBytes(_pendingLogs.Dequeue());
            }
            while (_pendingLogs.Count > 0)
            {
                var next = _pendingLogs.Peek();
                var size = AppService.LogTextBytes(next);
                if (batch.Count > 0 && batchBytes + size > MaxEventBatchBytes) break;
                batch.Add(_pendingLogs.Dequeue());
                batchBytes += size;
                _pendingLogBytes -= size;
            }
            logs = [.. batch];
            status = _pendingStatus; _pendingStatus = null;
        }
        if (logs.Length > 0) { _lastLogSentId = logs[^1].Id; Post(new { @event = "logs", data = logs }); }
        if (status is not null) Post(new { @event = "status", data = status });
    }

    private void Post(object data)
    {
        if (_closing || IsDisposed || _web.CoreWebView2 is null || _web.CoreWebView2.Source != PageUrl) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data, BridgeJson));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ExportAbort(); _documentTransfer.Abort();
            _startupSplash?.Dispose();
            _poll.Dispose(); _events.Dispose(); _startupTimeout.Dispose(); _service.Changed -= OnServiceChanged; _updates.Changed -= OnUpdateChanged; _updates.Dispose(); _web.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) { _loadingFont.Dispose(); _loadingFonts.Dispose(); }
    }
}

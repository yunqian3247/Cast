using System.Text;
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
    private static readonly Icon AppIcon = new(typeof(MainForm), "Assets.cast.ico");
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
    private readonly AppService _service;
    private readonly AppUpdateService _updates;
    private readonly string _directory;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 750 };
    private readonly System.Windows.Forms.Timer _events = new() { Interval = 50 };
    private readonly object _eventSync = new();
    private readonly Queue<AppLog> _pendingLogs = new();
    private long _pendingLogBytes, _lastLogSentId;
    private object? _pendingStatus;
    private readonly Label _loading = new() { Dock = DockStyle.Fill, Text = "正在打开cast", TextAlign = ContentAlignment.MiddleCenter, UseCompatibleTextRendering = true };
    private readonly PrivateFontCollection _loadingFonts = new();
    private readonly Font _loadingFont;
    private bool _ready, _closing, _closed, _restartRequested;
    private bool _nativeNonNormal;
    public WebView2 Browser => _web;
    public AppService Service => _service;
    public bool Ready => _ready;

    public MainForm(string? dataDirectory = null, ISerialConnection? connection = null, IPortCatalog? catalog = null)
    {
        _loadingFonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Web", "fonts", "SarasaGothicSC-Regular.ttf"));
        _loadingFont = new Font(_loadingFonts.Families[0], 9F);
        _loading.Font = _loadingFont;
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
        _service.Changed += OnServiceChanged;
        Shown += async (_, _) => await InitializeAsync();
        _poll.Tick += (_, _) => { if (_ready && !_closing) _service.PublishStatus(); };
        _events.Tick += (_, _) => FlushEvents();
        Resize += (_, _) => { if (_ready) Post(new { @event = "window", data = WindowStatus() }); };
        FormClosing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _updates.Dispose();
            _poll.Stop();
            _events.Stop();
            Enabled = false;
            await _service.DisposeAsync();
            _closed = true;
            Close();
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _service.InitializeAsync();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_directory, "Browser"));
            if (_closing) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_closing) return;
            var core = _web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("cast.example", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != PageUrl;
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += HandleMessage;
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) { _loading.Visible = false; _poll.Start(); _events.Start(); }
                else _loading.Text = "cast 加载失败：" + e.WebErrorStatus;
            };
            core.Navigate(PageUrl);
        }
        catch (Exception ex)
        {
            _loading.Text = "无法打开 cast。请检查 Microsoft Edge WebView2 Runtime。\n" + ex.Message;
        }
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != PageUrl || _closing || _restartRequested) return;
        string? id = null;
        try
        {
            var json = e.WebMessageAsJson;
            if (json.Length > 4_194_304) throw new FormatException("请求数据超出限制");
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 100) throw new FormatException("请求标识无效");
            var command = root.GetProperty("command").GetString();
            var data = root.GetProperty("data");
            object? result = command switch
            {
                "init" => InitializePage(),
                "window" => WindowCommand(data),
                "updateStatus" => _updates.Status,
                "updateCheck" => await _updates.CheckAsync(),
                "updateDownload" => await _updates.DownloadAsync(),
                "updateInstall" => await InstallUpdate(data),
                "ports" => _service.GetPorts(),
                "connect" => await Connect(data),
                "disconnect" => await Disconnect(),
                "send" => await Send(data),
                "repeat" => Repeat(data),
                "workflow" => Workflow(data),
                "pause" => Pause(data),
                "step" => Step(),
                "stop" => Stop(),
                "save" => await Save(data),
                "encode" => Encode(data),
                "pins" => Pins(data),
                "resetStats" => ResetStats(),
                "clearLogs" => ClearLogs(),
                "copy" => Copy(data),
                "export" => await Export(data),
                "import" => await Import(),
                _ => throw new FormatException("未知操作")
            };
            Post(new { id, ok = true, result });
        }
        catch (Exception ex) { Post(new { id, ok = false, error = ex.Message }); }
    }

    private object InitializePage()
    {
        _ready = true;
        IReadOnlyList<PortInfo> ports = [];
        string? error = null;
        try { ports = _service.GetPorts(); } catch (Exception ex) { error = ex.Message; }
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
            await _service.SaveAsync(Read<AppDocument>(data));
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
    private async Task<object> Save(JsonElement data) { await _service.SaveAsync(Read<AppDocument>(data)); return true; }
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
        var format = data.GetProperty("format").GetString();
        if (format is not ("txt" or "csv" or "json")) throw new FormatException("导出格式无效");
        var fileName = Path.GetFileName(data.GetProperty("fileName").GetString());
        var content = data.GetProperty("content").GetString() ?? "";
        using var dialog = new SaveFileDialog { FileName = fileName, DefaultExt = format, Filter = $"{format.ToUpperInvariant()} 文件|*.{format}", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return new { saved = false };
        await File.WriteAllTextAsync(dialog.FileName!, content, new UTF8Encoding(format == "csv"));
        return new { saved = true };
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
        if (!_ready || _closing || IsDisposed || !IsHandleCreated) return;
        if (name is "log" or "status")
        {
            lock (_eventSync)
            {
                if (name == "status") _pendingStatus = data;
                else
                {
                    var log = (AppLog)data;
                    _pendingLogs.Enqueue(log); _pendingLogBytes += AppService.LogTextBytes(log);
                    while (_pendingLogs.Count > AppService.MaxLogCount || (_pendingLogBytes > AppService.MaxLogTextBytes && _pendingLogs.Count > 1))
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
            logs = _pendingLogs.Where(log => log.Id > _lastLogSentId).ToArray();
            _pendingLogs.Clear(); _pendingLogBytes = 0;
            status = _pendingStatus; _pendingStatus = null;
        }
        if (logs.Length > 0) { _lastLogSentId = logs[^1].Id; Post(new { @event = "logs", data = logs }); }
        if (status is not null) Post(new { @event = "status", data = status });
    }

    private void Post(object data)
    {
        if (_closing || IsDisposed || _web.CoreWebView2 is null || _web.CoreWebView2.Source != PageUrl) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data, AppService.Json));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _poll.Dispose(); _events.Dispose(); _service.Changed -= OnServiceChanged; _updates.Changed -= OnUpdateChanged; _updates.Dispose(); _web.Dispose(); }
        base.Dispose(disposing);
        if (disposing) { _loadingFont.Dispose(); _loadingFonts.Dispose(); }
    }
}

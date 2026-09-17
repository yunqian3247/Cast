using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Sources;
using Velopack.Logging;

namespace serial.Desktop;

public sealed record UpdateSettings(string FeedUrl = "", string Channel = "win-x64-preview")
{
    public static UpdateSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), AppService.Json)
            ?? throw new FormatException("更新配置为空");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Channel) || !Regex.IsMatch(Channel, "^[a-z0-9][a-z0-9-]{0,63}$"))
            throw new FormatException("更新通道格式无效");
        if (string.IsNullOrWhiteSpace(FeedUrl)) return;
        if (!Uri.TryCreate(FeedUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException("更新地址需要使用 HTTPS 目录地址；本机测试可使用 HTTP");
    }
}

public sealed record AppUpdateStatus(string Phase, string Message, string? Version = null, int? Progress = null,
    bool CanCheck = false, bool CanDownload = false, bool CanInstall = false);

public interface IAppUpdateClient
{
    bool IsInstalled { get; }
    VelopackAsset? PendingRestart { get; }
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken);
    void PrepareRestart(VelopackAsset update);
}

public sealed class VelopackUpdateClient : IAppUpdateClient
{
    private readonly UpdateManager _manager;

    public VelopackUpdateClient(UpdateSettings settings) : this(new UpdateManager(
        new AppUpdateSource(settings.FeedUrl),
        new UpdateOptions { ExplicitChannel = settings.Channel, AllowVersionDowngrade = false })) { }

    public VelopackUpdateClient(UpdateManager manager) => _manager = manager;
    public bool IsInstalled => _manager.IsInstalled;
    public VelopackAsset? PendingRestart => _manager.UpdatePendingRestart;
    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken) =>
        _manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(update, progress, cancellationToken);
    public void PrepareRestart(VelopackAsset update) => _manager.WaitExitThenApplyUpdates(update, silent: false, restart: true);

    private sealed class AppUpdateSource(string url) : SimpleWebSource(url, timeout: 30)
    {
        // Feed requests are small; package downloads keep the longer timeout (minutes).
        public override Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            new SimpleWebSource(BaseUri, Downloader, timeout: 0.5).GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
    }
}

public sealed class AppUpdateService : IDisposable
{
    private readonly IAppUpdateClient? _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private AppUpdateStatus _status;
    private UpdateInfo? _available;
    private VelopackAsset? _downloaded;
    private bool _disposed;
    public event Action<AppUpdateStatus>? Changed;
    public AppUpdateStatus Status => Volatile.Read(ref _status);

    public AppUpdateService(string settingsPath, Func<UpdateSettings, IAppUpdateClient>? factory = null)
    {
        _status = new("unconfigured", "更新地址尚未配置");
        try
        {
            var settings = UpdateSettings.Load(settingsPath);
            if (string.IsNullOrWhiteSpace(settings.FeedUrl)) return;
            _client = (factory ?? (settings => new VelopackUpdateClient(settings)))(settings);
            if (!_client.IsInstalled)
            {
                _status = new("unavailable", "请使用 serial 安装包或便携包启用在线更新");
                return;
            }
            _downloaded = _client.PendingRestart;
            _status = _downloaded is null
                ? new("idle", "尚未检查更新", CanCheck: true)
                : ReadyStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            _status = new("unconfigured", "更新配置无法使用：" + ex.Message);
        }
    }

    public async Task<AppUpdateStatus> CheckAsync()
    {
        if (_disposed || !_operation.Wait(0)) return Status;
        try
        {
            if (!Status.CanCheck || _client is null) return Status;
            _available = null;
            Set(new("checking", "正在检查更新..."));
            _available = await _client.CheckAsync(_lifetime.Token);
            if (_disposed) return Status;
            Set(_available is null
                ? new("idle", "已是最新版本", CanCheck: true)
                : new("available", "发现新版本", _available.TargetFullRelease.Version.ToString(), CanCheck: true, CanDownload: true));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            Set(new("error", "检查更新失败，请检查网络或更新源后重试", CanCheck: true));
        }
        finally { _operation.Release(); }
        return Status;
    }

    public async Task<AppUpdateStatus> DownloadAsync()
    {
        if (_disposed || !_operation.Wait(0)) return Status;
        try
        {
            if (!Status.CanDownload || _available is null || _client is null) return Status;
            var version = _available.TargetFullRelease.Version.ToString();
            Set(new("downloading", "正在下载更新...", version, 0));
            await _client.DownloadAsync(_available, progress =>
            {
                if (!_disposed && Status.Phase == "downloading")
                    Set(new("downloading", "正在下载更新...", version, Math.Clamp(progress, 0, 100)));
            }, _lifetime.Token);
            if (_disposed) return Status;
            _downloaded = _available.TargetFullRelease;
            Set(ReadyStatus());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            Set(new("error", "下载更新失败，请检查网络或更新源后重试", _available?.TargetFullRelease.Version.ToString(),
                CanCheck: true, CanDownload: _available is not null));
        }
        finally { _operation.Release(); }
        return Status;
    }

    public void PrepareRestart()
    {
        if (_disposed || !_operation.Wait(0)) throw new InvalidOperationException("更新操作正在进行");
        try
        {
            if (!Status.CanInstall || _downloaded is null || _client is null)
                throw new InvalidOperationException("请先下载更新");
            _client.PrepareRestart(_downloaded);
            Set(new("restarting", "正在安装更新并重启...", _downloaded.Version.ToString()));
        }
        catch
        {
            if (_downloaded is not null) Set(ReadyStatus() with { Message = "启动安装失败，请重试" });
            throw;
        }
        finally { _operation.Release(); }
    }

    private AppUpdateStatus ReadyStatus() => new("ready", "更新已下载", _downloaded!.Version.ToString(), 100, CanInstall: true);

    private void Set(AppUpdateStatus status)
    {
        if (_disposed) return;
        Volatile.Write(ref _status, status);
        Changed?.Invoke(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
    }
}

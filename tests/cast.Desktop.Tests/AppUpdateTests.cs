using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using cast.Desktop;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace cast.Desktop.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cast-update-tests-" + Guid.NewGuid());
    private static VelopackAsset Asset => new() { PackageId = "cast", Version = SemanticVersion.Parse("1.0.2-preview.20260917"),
        Type = VelopackAssetType.Full, FileName = "cast-1.0.2-preview.20260917-full.nupkg" };

    public AppUpdateTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private string Settings(string url = "https://updates.example.com/cast")
    {
        var path = Path.Combine(_directory, "update-settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new UpdateSettings(url), AppService.Json));
        return path;
    }

    [Fact]
    public async Task MissingOrInvalidConfigurationNeverCreatesNetworkClient()
    {
        foreach (var contents in new[] { "{}", "null", "{", "{\"feedUrl\":\"http://updates.example.com\"}", "{\"channel\":\"../bad\"}" })
        {
            var path = Path.Combine(_directory, "invalid.json");
            File.WriteAllText(path, contents);
            using var service = new AppUpdateService(path, _ => throw new Xunit.Sdk.XunitException("Unexpected network client"));
            Assert.Equal("unconfigured", (await service.CheckAsync()).Phase);
            Assert.False(service.Status.CanCheck);
        }
        using var missing = new AppUpdateService(Path.Combine(_directory, "missing.json"));
        Assert.Equal("unconfigured", missing.Status.Phase);
    }

    [Theory]
    [InlineData("https://updates.example.com/cast")]
    [InlineData("http://127.0.0.1:8319/cast")]
    public void ValidFeedDirectoriesAreAccepted(string url) => new UpdateSettings(url).Validate();

    [Theory]
    [InlineData("file:///c:/updates")]
    [InlineData("https://user:password@updates.example.com")]
    [InlineData("https://updates.example.com?token=secret")]
    [InlineData("https://updates.example.com/#fragment")]
    public void InvalidFeedDirectoriesAreRejected(string url) => Assert.Throws<FormatException>(() => new UpdateSettings(url).Validate());

    [Theory]
    [InlineData("https://github.com/yunqian3247")]
    [InlineData("https://github.com/yunqian3247/Cast/releases")]
    [InlineData("https://github.com/yunqian3247/Cast.git")]
    [InlineData("https://github.com:8443/yunqian3247/Cast")]
    [InlineData("https://example.com/yunqian3247/Cast")]
    [InlineData("https://github.com/yunqian3247/Cast?token=secret")]
    public void InvalidGithubRepositoriesAreRejected(string url) =>
        Assert.Throws<FormatException>(() => new UpdateSettings(url, Source: "github").Validate());

    [Fact]
    public void SourceConfigurationPreservesStaticFeedsAndEnablesBundledGithubPrereleases()
    {
        var legacy = UpdateSettings.Load(Settings());
        Assert.IsAssignableFrom<SimpleWebSource>(VelopackUpdateClient.CreateSource(legacy));
        Assert.Throws<FormatException>(() => new UpdateSettings(Source: "unknown").Validate());
        var bundled = UpdateSettings.Load(Path.Combine(AppContext.BaseDirectory, "update-settings.json"));
        var source = Assert.IsType<GithubSource>(VelopackUpdateClient.CreateSource(bundled));
        Assert.Equal("https://github.com/yunqian3247/Cast", source.RepoUri.ToString());
        Assert.True(source.Prerelease);
        Assert.Equal("win-x64-preview", bundled.Channel);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task GithubSourceFiltersPrereleasesAndVerifiesPublicDownloads(bool includePrereleases, bool corrupt)
    {
        byte[] package;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("cast.nuspec").Open());
                writer.Write("<package><metadata><id>cast</id><version>1.0.2-preview.20260917</version><authors>cast</authors><description>Update fixture</description></metadata></package>");
            }
            package = buffer.ToArray();
        }
        var asset = Asset;
        const string repository = "https://github.com/yunqian3247/Cast";
        const string downloadBase = repository + "/releases/download/v1.0.2-preview.20260917/";
        const string feedName = "releases.win-x64-preview.json";
        var feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] { new {
            asset.PackageId, Version = asset.Version.ToString(), Type = "Full", asset.FileName,
            Size = package.Length, SHA1 = Convert.ToHexString(SHA1.HashData(package)), SHA256 = Convert.ToHexString(SHA256.HashData(package)) } } });
        var releases = JsonSerializer.SerializeToUtf8Bytes(new[] { new {
            name = "cast preview", prerelease = true, published_at = "2026-09-17T00:00:00Z",
            assets = new[] { feedName, asset.FileName }.Select(name => new {
                name, browser_download_url = downloadBase + name, url = "https://api.github.com/assets/unused" }) } });
        if (corrupt) package[0] ^= 0xff;
        var downloader = new FixtureDownloader(new()
        {
            ["https://api.github.com/repos/yunqian3247/Cast/releases?per_page=10&page=1"] = releases,
            [downloadBase + feedName] = feed,
            [downloadBase + asset.FileName] = package
        });
        var settings = new UpdateSettings(repository, Source: "github", IncludePrereleases: includePrereleases);
        var source = VelopackUpdateClient.CreateSource(settings, downloader);
        var locator = new TestVelopackLocator("cast", "1.0.1-preview.20260917", _directory,
            _directory, _directory, Path.Combine(_directory, "Update.exe"), settings.Channel);
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = settings.Channel }, locator);
        var settingsPath = Path.Combine(_directory, "github.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, AppService.Json));
        using var service = new AppUpdateService(settingsPath, _ => new VelopackUpdateClient(manager));
        Assert.True(service.Status.CanCheck);
        Assert.Equal(includePrereleases ? "available" : "idle", (await service.CheckAsync()).Phase);
        if (!includePrereleases)
        {
            Assert.Single(downloader.Requests);
            Assert.False(service.Status.CanDownload);
            return;
        }
        Assert.Equal(corrupt ? "error" : "ready", (await service.DownloadAsync()).Phase);
        Assert.Equal(!corrupt, service.Status.CanInstall);
        Assert.Contains(downloadBase + feedName, downloader.Requests);
        Assert.Contains(downloadBase + asset.FileName, downloader.Requests);
        if (!corrupt)
        {
            Assert.Equal(package, await File.ReadAllBytesAsync(Path.Combine(_directory, asset.FileName)));
            using var resumed = new AppUpdateService(settingsPath, _ => new VelopackUpdateClient(manager));
            Assert.True(resumed.Status.CanInstall);
        }
    }

    [Fact]
    public async Task UnpackagedBuildsCannotCheckOrInstall()
    {
        var client = new FakeClient { IsInstalled = false };
        using var service = new AppUpdateService(Settings(), _ => client);
        Assert.Equal("unavailable", (await service.CheckAsync()).Phase);
        Assert.Equal(0, client.Checks);
        Assert.Throws<InvalidOperationException>(service.PrepareRestart);
    }

    [Fact]
    public async Task CheckDownloadAndRestartAreSeparateAndRetryable()
    {
        var client = new FakeClient();
        using var service = new AppUpdateService(Settings(), _ => client);
        var states = new List<AppUpdateStatus>();
        service.Changed += states.Add;
        client.FailCheck = true;
        Assert.True((await service.CheckAsync()).CanCheck);
        Assert.Equal("error", service.Status.Phase);
        client.FailCheck = false;
        Assert.Equal("available", (await service.CheckAsync()).Phase);
        Assert.Throws<InvalidOperationException>(service.PrepareRestart);
        client.FailDownload = true;
        Assert.True((await service.DownloadAsync()).CanDownload);
        Assert.False(service.Status.CanInstall);
        client.FailDownload = false;
        Assert.Equal("ready", (await service.DownloadAsync()).Phase);
        Assert.Contains(states, state => state.Progress == 42);
        Assert.Equal(0, client.Restarts);
        client.FailRestart = true;
        Assert.Throws<IOException>(service.PrepareRestart);
        Assert.True(service.Status.CanInstall);
        client.FailRestart = false;
        service.PrepareRestart();
        Assert.Equal("restarting", service.Status.Phase);
        Assert.Equal(1, client.Restarts);
    }

    [Fact]
    public async Task NoNewReleaseAndPendingRestartAreHandled()
    {
        var client = new FakeClient { Update = null };
        using var service = new AppUpdateService(Settings(), _ => client);
        Assert.Equal("idle", (await service.CheckAsync()).Phase);
        Assert.False(service.Status.CanDownload);
        client.PendingRestart = Asset;
        using var resumed = new AppUpdateService(Settings(), _ => client);
        Assert.True(resumed.Status.CanInstall);
        Assert.False(resumed.Status.CanCheck);
    }

    [Fact]
    public async Task RepeatedRequestsAndDisposalDoNotStartAdditionalOperations()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient { CheckGate = release.Task };
        using var service = new AppUpdateService(Settings(), _ => client);
        var first = service.CheckAsync();
        Assert.Equal("checking", (await service.CheckAsync()).Phase);
        Assert.Equal(1, client.Checks);
        service.Dispose();
        release.SetResult();
        await first;
        Assert.False(service.Status.CanInstall);
    }

    [Fact]
    public async Task RealVelopackReadsHttpFeedAndRejectsCorruptPackage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = new SimpleWebSource($"http://127.0.0.1:{port}/cast/", timeout: 0.1);
        var locator = new TestVelopackLocator("cast", "1.0.1-preview.20260917", _directory,
            _directory, _directory, Path.Combine(_directory, "Update.exe"), "win-x64-preview");
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64-preview" }, locator);
        var asset = Asset;
        asset.Size = 100;
        asset.SHA1 = new string('0', 40);
        asset.SHA256 = new string('0', 64);
        var feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] {
            new { asset.PackageId, Version = asset.Version.ToString(), Type = "Full", asset.FileName, asset.Size, asset.SHA1, asset.SHA256 } } });
        var responses = Serve(listener, ("/cast/releases.win-x64-preview.json", feed), ("/cast/" + asset.FileName, new byte[100]));
        using var service = new AppUpdateService(Settings(source.BaseUri.ToString()), _ => new VelopackUpdateClient(manager));
        Assert.Equal("available", (await service.CheckAsync()).Phase);
        Assert.Equal("error", (await service.DownloadAsync()).Phase);
        Assert.False(service.Status.CanInstall);
        await responses.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RealVelopackDownloadsPackageAndRecoversPendingRestart()
    {
        byte[] package;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("cast.nuspec").Open());
                writer.Write("<package><metadata><id>cast</id><version>1.0.2-preview.20260917</version><authors>cast</authors><description>Update fixture</description></metadata></package>");
            }
            package = buffer.ToArray();
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new SimpleWebSource($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/cast/", timeout: 0.1);
        var locator = new TestVelopackLocator("cast", "1.0.1-preview.20260917", _directory,
            _directory, _directory, Path.Combine(_directory, "Update.exe"), "win-x64-preview");
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64-preview" }, locator);
        var asset = Asset;
        var feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] { new {
            asset.PackageId, Version = asset.Version.ToString(), Type = "Full", asset.FileName,
            Size = package.Length, SHA1 = Convert.ToHexString(SHA1.HashData(package)), SHA256 = Convert.ToHexString(SHA256.HashData(package)) } } });
        var responses = Serve(listener, ("/cast/releases.win-x64-preview.json", feed), ("/cast/" + asset.FileName, package));
        using var service = new AppUpdateService(Settings(source.BaseUri.ToString()), _ => new VelopackUpdateClient(manager));
        Assert.Equal("available", (await service.CheckAsync()).Phase);
        Assert.Equal("ready", (await service.DownloadAsync()).Phase);
        Assert.Equal(package, await File.ReadAllBytesAsync(Path.Combine(_directory, asset.FileName)));
        using var resumed = new AppUpdateService(Settings(source.BaseUri.ToString()), _ => new VelopackUpdateClient(manager));
        Assert.True(resumed.Status.CanInstall);
        await responses.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task Serve(TcpListener listener, params (string Path, byte[] Body)[] responses)
    {
        foreach (var response in responses)
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var request = await reader.ReadLineAsync();
            Assert.StartsWith("GET " + response.Path, request);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(response.Body);
        }
    }

    private sealed class FixtureDownloader(Dictionary<string, byte[]> responses) : IFileDownloader
    {
        public List<string> Requests { get; } = [];

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Assert.False(headers?.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true);
            Requests.Add(url);
            Assert.True(responses.ContainsKey(url), "Unexpected request: " + url);
            return Task.FromResult(responses[url]);
        }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout));

        public async Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            await File.WriteAllBytesAsync(targetFile, await DownloadBytes(url, headers, timeout), cancelToken);
            progress(100);
        }
    }

    private sealed class FakeClient : IAppUpdateClient
    {
        public bool IsInstalled { get; set; } = true;
        public VelopackAsset? PendingRestart { get; set; }
        public UpdateInfo? Update { get; set; } = new(Asset, false);
        public bool FailCheck, FailDownload, FailRestart;
        public int Checks, Restarts;
        public Task CheckGate { get; set; } = Task.CompletedTask;
        public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            await CheckGate.WaitAsync(cancellationToken);
            if (FailCheck) throw new HttpRequestException("offline");
            return Update;
        }
        public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken)
        {
            progress(42);
            if (FailDownload) throw new IOException("incomplete download");
            progress(100);
            return Task.CompletedTask;
        }
        public void PrepareRestart(VelopackAsset update)
        {
            if (FailRestart) throw new IOException("updater unavailable");
            Restarts++;
        }
    }
}

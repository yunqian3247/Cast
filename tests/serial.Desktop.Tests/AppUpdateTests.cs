using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using serial.Desktop;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace serial.Desktop.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "serial-update-tests-" + Guid.NewGuid());
    private static VelopackAsset Asset => new() { PackageId = "serial", Version = SemanticVersion.Parse("1.0.2-preview.20260917"),
        Type = VelopackAssetType.Full, FileName = "serial-1.0.2-preview.20260917-full.nupkg" };

    public AppUpdateTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private string Settings(string url = "https://updates.example.com/serial")
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
    [InlineData("https://updates.example.com/serial")]
    [InlineData("http://127.0.0.1:8319/serial")]
    public void ValidFeedDirectoriesAreAccepted(string url) => new UpdateSettings(url).Validate();

    [Theory]
    [InlineData("file:///c:/updates")]
    [InlineData("https://user:password@updates.example.com")]
    [InlineData("https://updates.example.com?token=secret")]
    [InlineData("https://updates.example.com/#fragment")]
    public void InvalidFeedDirectoriesAreRejected(string url) => Assert.Throws<FormatException>(() => new UpdateSettings(url).Validate());

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
        var source = new SimpleWebSource($"http://127.0.0.1:{port}/serial/", timeout: 0.1);
        var locator = new TestVelopackLocator("serial", "1.0.1-preview.20260917", _directory,
            _directory, _directory, Path.Combine(_directory, "Update.exe"), "win-x64-preview");
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64-preview" }, locator);
        var asset = Asset;
        asset.Size = 100;
        asset.SHA1 = new string('0', 40);
        asset.SHA256 = new string('0', 64);
        var feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] {
            new { asset.PackageId, Version = asset.Version.ToString(), Type = "Full", asset.FileName, asset.Size, asset.SHA1, asset.SHA256 } } });
        var responses = Serve(listener, ("/serial/releases.win-x64-preview.json", feed), ("/serial/" + asset.FileName, new byte[100]));
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
                using var writer = new StreamWriter(zip.CreateEntry("serial.nuspec").Open());
                writer.Write("<package><metadata><id>serial</id><version>1.0.2-preview.20260917</version><authors>serial</authors><description>Update fixture</description></metadata></package>");
            }
            package = buffer.ToArray();
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new SimpleWebSource($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/serial/", timeout: 0.1);
        var locator = new TestVelopackLocator("serial", "1.0.1-preview.20260917", _directory,
            _directory, _directory, Path.Combine(_directory, "Update.exe"), "win-x64-preview");
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64-preview" }, locator);
        var asset = Asset;
        var feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] { new {
            asset.PackageId, Version = asset.Version.ToString(), Type = "Full", asset.FileName,
            Size = package.Length, SHA1 = Convert.ToHexString(SHA1.HashData(package)), SHA256 = Convert.ToHexString(SHA256.HashData(package)) } } });
        var responses = Serve(listener, ("/serial/releases.win-x64-preview.json", feed), ("/serial/" + asset.FileName, package));
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

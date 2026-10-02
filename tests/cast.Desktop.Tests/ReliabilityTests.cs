using System.Text;
using System.Text.Json;
using cast.Core;
using cast.Desktop;
using cast.Serial;
using Xunit;

namespace cast.Desktop.Tests;

public sealed class ReliabilityTests
{
    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "CastReliability", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReceiveModesApplyWhileConnectedFlushIdleAndDisconnect()
    {
        var connection = new ReplyConnection();
        await using var service = new AppService(TempDirectory(), connection, new FakeCatalog());
        await service.InitializeAsync(); await service.ConnectAsync(new() { PortName = "COM3" });
        var document = service.Document;
        document.Ui["receiveFrameMode"] = "auto"; await service.SaveAsync(document); service.ClearLogs();
        connection.Receive(HexCodec.Parse("01 03 00 00"));
        Assert.Empty(service.Logs);
        connection.Receive(HexCodec.Parse("00 02 C4 0B 01 03 00 00 00 02 C4 0B"));
        Assert.Equal(2, service.Logs.Count);
        Assert.All(service.Logs, log => Assert.Equal("01 03 00 00 00 02 C4 0B", log.Hex));
        Assert.Equal(16, service.Status().Rx);
        document = service.Document; document.Ui["receiveFrameMode"] = "idle"; document.Ui["receiveIdleMs"] = 10;
        await service.SaveAsync(document); service.ClearLogs(); connection.Receive([65, 66]);
        await AppServiceTests.Wait(() => service.Logs.Count == 1);
        Assert.Equal("AB", service.Logs[0].Text);
        connection.Receive([67]); await service.DisconnectAsync();
        Assert.Contains(service.Logs, log => log.Text == "C");
    }

    [Fact]
    public async Task UnrelatedSavePreservesPartialFrameAndLogQueueOverloadIsVisible()
    {
        var connection = new ReplyConnection();
        await using var service = new AppService(TempDirectory(), connection, new FakeCatalog());
        await service.InitializeAsync(); await service.ConnectAsync(new() { PortName = "COM3" });
        var document = service.Document; document.Ui["receiveFrameMode"] = "fixed";
        document.Ui["receiveFrameLength"] = 4; document.Ui["receiveIdleMs"] = 5000;
        await service.SaveAsync(document); service.ClearLogs(); connection.Receive([65, 66]);
        document = service.Document; document.Ui["theme"] = "dark"; await service.SaveAsync(document);
        Assert.Empty(service.Logs);
        connection.Receive([67, 68]); Assert.Equal("ABCD", Assert.Single(service.Logs).Text);
        var writer = new ContinuousLogWriter(TempDirectory(), 1, 2);
        writer.Append(new(1, DateTimeOffset.Now, "RX", new string('A', 17 * 1024 * 1024), "", 1, "test"));
        await writer.DisposeAsync(); Assert.Equal(1, writer.Dropped);
    }

    [Fact]
    public async Task FastSplitResponseRetriesAndCustomRoundsUseFixedSnapshot()
    {
        var connection = new ReplyConnection();
        await using var service = new AppService(TempDirectory(), connection, new FakeCatalog());
        await service.InitializeAsync(); await service.ConnectAsync(new() { PortName = "COM3" });
        service.Document.Presets = [new() { Id = "a", Name = "A", Content = "AT" }];
        service.Document.Workflows["wf"] = new() { Name = "Reply", Steps = [new() { PresetId = "a", Wait = 0, WaitForResponse = true, Response = "OK", TimeoutMs = 30, Retries = 1 }] };
        connection.ReplyOnWrite = count => { if (count > 1) { connection.Receive([79]); connection.Receive([75]); } };
        service.StartWorkflow("wf", "count", count: 3);
        await AppServiceTests.Wait(() => service.Status().Run.Kind == "idle");
        Assert.Equal(4, connection.Writes);
        Assert.Contains(service.Logs, log => log.Text.Contains("重试 1/1"));
        Assert.Contains(service.Logs, log => log.Text == "工作流执行完成");
    }

    [Fact]
    public async Task MissingResponseStopsFollowingStepAndCancellationStopsWait()
    {
        var connection = new ReplyConnection();
        await using var service = new AppService(TempDirectory(), connection, new FakeCatalog());
        await service.InitializeAsync(); await service.ConnectAsync(new() { PortName = "COM3" });
        service.Document.Presets = [new() { Id = "a", Name = "A", Content = "AT" }];
        service.Document.Workflows["wf"] = new() { Name = "Reply", Steps = [new() { PresetId = "a", Wait = 0, WaitForResponse = true, Response = "OK", TimeoutMs = 20, Retries = 1 }, new() { PresetId = "a", Wait = 0 }] };
        service.StartWorkflow("wf", "once"); await AppServiceTests.Wait(() => service.Status().Run.Kind == "idle");
        Assert.Equal(2, connection.Writes);
        Assert.Contains(service.Logs, log => log.Text.Contains("等待应答超时"));
        service.Document.Workflows["wf"].Steps[0].TimeoutMs = 600000;
        service.StartWorkflow("wf", "once"); service.Stop();
        await AppServiceTests.Wait(() => service.Status().Run.Kind == "idle");
        Assert.Equal(3, connection.Writes);
    }

    [Fact]
    public async Task ContinuousLogsFlushRawHexAndRetentionCountOnClose()
    {
        var directory = TempDirectory(); var connection = new ReplyConnection();
        var service = new AppService(directory, connection, new FakeCatalog());
        await service.InitializeAsync(); await service.ConnectAsync(new() { PortName = "COM3" });
        var document = service.Document; document.Ui["continuousLog"] = true; document.Ui["maxLogCount"] = 100;
        await service.SaveAsync(document); service.ClearLogs();
        for (var i = 0; i < 150; i++) connection.Receive([0, 255, (byte)i]);
        Assert.Equal(100, service.Logs.Count); Assert.Equal(50, service.Status().DroppedLogs);
        await service.DisposeAsync();
        var lines = Directory.GetFiles(service.LogDirectory, "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<AppLog>(line, AppService.Json)!).ToArray();
        Assert.Equal(150, lines.Length);
        Assert.Equal("00 FF 00", lines[0].Hex); Assert.Equal("00 FF 95", lines[^1].Hex);
    }

    [Fact]
    public async Task LogRotationAndWriteFailureAreVisible()
    {
        var directory = TempDirectory(); var writer = new ContinuousLogWriter(directory, 1, 2);
        for (var i = 0; i < 4; i++) writer.Append(new(i, DateTimeOffset.Now, "RX", new string('A', 600000), "41", 1, "test"));
        await writer.DisposeAsync();
        Assert.Null(writer.Error); Assert.Equal(2, Directory.GetFiles(directory, "*.jsonl").Length);
        var invalid = TempDirectory(); Directory.CreateDirectory(Path.GetDirectoryName(invalid)!); File.WriteAllText(invalid, "file");
        var failed = new ContinuousLogWriter(invalid, 1, 2); failed.Append(new(1, DateTimeOffset.Now, "RX", "A", "41", 1, "test"));
        await failed.DisposeAsync(); Assert.Contains("持续日志写入失败", failed.Error); Assert.Equal(1, failed.Dropped);
        failed.ResetDropped(); Assert.Equal(0, failed.Dropped);
    }

    [Fact]
    public async Task LargePresetImportChunksPreserveUnicodeAndRejectOffsets()
    {
        var presets = Enumerable.Range(0, 5).Select(i => new CommandPreset { Id = $"p{i}", Name = "大预设", Content = new string('A', 1000000) + "😀" }).ToList();
        var content = JsonSerializer.Serialize(presets, AppService.Json);
        Assert.True(content.Length > 4 * 1024 * 1024);
        var directory = TempDirectory(); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "presets.json");
        await File.WriteAllTextAsync(path, content);
        var read = await PresetImportSession.ReadAsync(path); Assert.Equal(content, read);
        Assert.Equal(5, CommandPreset.ParseImport(read).Count);
        var session = new PresetImportSession(content); var combined = new StringBuilder();
        for (var offset = 0; offset < session.Length;) { var chunk = session.Chunk(session.Id, offset); Assert.False(char.IsHighSurrogate(chunk[^1])); combined.Append(chunk); offset += chunk.Length; }
        Assert.Equal(content, combined.ToString());
        Assert.Throws<FormatException>(() => session.Chunk("wrong", 0));
        Assert.Throws<FormatException>(() => session.Chunk(session.Id, -1));
        var exportedPath = Path.Combine(directory, "exported.json");
        using (var export = new LogExportSession(exportedPath, "json"))
        {
            for (var offset = 0; offset < session.Length;) { var chunk = session.Chunk(session.Id, offset); await export.AppendAsync(chunk); offset += chunk.Length; }
            await export.FinishAsync();
        }
        Assert.Equal(content, await File.ReadAllTextAsync(exportedPath));
    }

    private sealed class ReplyConnection : ISerialConnection
    {
        public int Writes { get; private set; }
        public Action<int>? ReplyOnWrite { get; set; }
        public SerialConnectionState State { get; private set; }
        public PortInfo? Port => new("COM3", "Test");
        public event EventHandler<SerialDataReceivedEventArgs>? DataReceived;
        public event EventHandler<SerialConnectionErrorEventArgs>? Error { add { } remove { } }
        public event EventHandler? StateChanged;
        public Task OpenAsync(SerialProfile profile, CancellationToken cancellationToken = default) { State = SerialConnectionState.Open; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Writes++; ReplyOnWrite?.Invoke(Writes); return Task.CompletedTask; }
        public void Receive(byte[] bytes) => DataReceived?.Invoke(this, new() { Bytes = bytes });
        public Task CloseAsync(CancellationToken cancellationToken = default) { State = SerialConnectionState.Closed; StateChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public async ValueTask DisposeAsync() => await CloseAsync();
    }
}

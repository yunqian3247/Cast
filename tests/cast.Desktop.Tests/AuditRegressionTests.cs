using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using cast.Core;
using cast.Desktop;
using cast.Serial;
using Xunit;

namespace cast.Desktop.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CastAuditTests", Guid.NewGuid().ToString("N"));
    public AuditRegressionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("{")]
    [InlineData("{\"history\":null}")]
    [InlineData("{\"profile\":{\"baudRate\":0}}")]
    public async Task InvalidPrimaryRecoversBackupAndPreservesBothOnSave(string corrupt)
    {
        var path = Path.Combine(_directory, "cast.json");
        var backup = JsonSerializer.Serialize(new AppDocument { History = ["recover-me"] }, AppService.Json);
        await File.WriteAllTextAsync(path, corrupt);
        await File.WriteAllTextAsync(path + ".bak", backup);
        await using var app = new AppService(_directory, new FakeConnection(), new FakeCatalog());
        await app.InitializeAsync();
        Assert.Equal("recover-me", Assert.Single(app.Document.History));
        app.Document.Ui["inputDraft"] = "latest";
        await app.SaveAsync(app.Document);
        Assert.Equal(backup, await File.ReadAllTextAsync(path + ".bak"));
        var preserved = Assert.Single(Directory.GetFiles(_directory, "cast.json.corrupt-*"));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(preserved));
        await using var restored = new AppService(_directory, new FakeConnection(), new FakeCatalog());
        await restored.InitializeAsync();
        Assert.Equal("recover-me", Assert.Single(restored.Document.History));
        Assert.Equal("latest", restored.Document.Ui["inputDraft"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPrimaryRecoversBackup(bool legacyAvailable)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "cast.json.bak"),
            JsonSerializer.Serialize(new AppDocument { History = ["backup-only"] }, AppService.Json));
        string? legacy = null;
        if (legacyAvailable)
        {
            legacy = Path.Combine(_directory, "serial.json");
            await File.WriteAllTextAsync(legacy,
                JsonSerializer.Serialize(new AppDocument { History = ["legacy"] }, AppService.Json));
        }
        await using var app = new AppService(_directory, new FakeConnection(), new FakeCatalog(), legacy);
        await app.InitializeAsync();
        Assert.Equal("backup-only", Assert.Single(app.Document.History));
        await app.SaveAsync(app.Document);
        Assert.True(File.Exists(Path.Combine(_directory, "cast.json")));
    }

    [Fact]
    public void LargeDocumentTransferValidatesOrderCompletenessAndAbort()
    {
        var original = new AppDocument { History = Enumerable.Range(0, 50).Select(i => i + ":" + new string('A', 90000)).ToList() };
        var content = JsonSerializer.Serialize(original, AppService.Json);
        Assert.True(content.Length > 4_194_304);
        var transfer = new DocumentSaveBuffer();
        transfer.Start("large", content.Length);
        Assert.Throws<FormatException>(() => transfer.Append("large", 1, "x"));
        Assert.Throws<InvalidOperationException>(() => transfer.Append("other", 0, "x"));
        Assert.Throws<FormatException>(() => transfer.Finish("large"));
        for (var offset = 0; offset < content.Length; offset += DocumentSaveBuffer.MaxChunkChars)
            transfer.Append("large", offset, content.Substring(offset, Math.Min(DocumentSaveBuffer.MaxChunkChars, content.Length - offset)));
        Assert.Equal(original.History, transfer.Finish("large").History);
        transfer.Start("cancelled", 10); transfer.Append("cancelled", 0, "abc");
        transfer.Abort("other");
        Assert.Throws<InvalidOperationException>(() => transfer.Start("next", 1));
        transfer.Abort("cancelled");
        transfer.Start("next", 2); transfer.Append("next", 0, "{}");
        Assert.Empty(transfer.Finish("next").History);
        Assert.Throws<FormatException>(() => transfer.Start("too-big", AppDocument.MaxSerializedChars + 1));
    }

    [Fact]
    public async Task SerializedCapacityRejectsAggregateBeforeReplacingSavedDocument()
    {
        await using var app = new AppService(_directory, new FakeConnection(), new FakeCatalog());
        await app.InitializeAsync();
        await app.SaveAsync(new() { History = ["preserved"] });
        var path = Path.Combine(_directory, "cast.json");
        var previous = await File.ReadAllTextAsync(path);
        var content = new string('A', 1_048_576);
        var oversized = new AppDocument
        {
            Presets = Enumerable.Range(0, 65).Select(index => new CommandPreset
                { Id = index.ToString(), Name = "valid preset", Content = content }).ToList()
        };
        var error = await Assert.ThrowsAsync<FormatException>(() => app.SaveAsync(oversized));
        Assert.Contains("64 Mi", error.Message);
        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        Assert.Equal("preserved", Assert.Single(app.Document.History));
    }

    [Fact]
    public async Task SlowLargeManualSendCompletesBeyondThirtySeconds()
    {
        var connection = new FakeConnection { DelayMs = 31_050 };
        await using var app = new AppService(_directory, connection, new FakeCatalog());
        await app.InitializeAsync();
        await app.ConnectAsync(new() { PortName = "COM3", BaudRate = 9600 });
        await app.SendAsync(new(new string('A', 32000), Ending: "none"));
        Assert.Equal(32000, Assert.Single(connection.Writes).Length);
        Assert.True(app.Status().Connected);
        Assert.Equal(32000, app.Status().Tx);
    }

    [Fact]
    public void SendDeadlineIncludesFrameFormatAndLineSpeed()
    {
        var normal = SerialWriteTiming.GetTimeout(32000, new SerialProfile { BaudRate = 9600 });
        var parity = SerialWriteTiming.GetTimeout(32000, new SerialProfile { BaudRate = 9600, Parity = ParitySetting.Even, StopBits = StopBitsSetting.Two });
        Assert.InRange(normal.TotalSeconds, 38.3, 38.4);
        Assert.InRange(parity.TotalSeconds, 45, 45.1);
    }

    [Fact]
    public async Task ManualStopCancelsWriteAndReturnsChannelToIdle()
    {
        var connection = new FakeConnection { DelayMs = 2000 };
        await using var app = new AppService(_directory, connection, new FakeCatalog());
        await app.InitializeAsync(); await app.ConnectAsync(new() { PortName = "COM3" });
        var sending = app.SendAsync(new("cancel-me"));
        app.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Empty(connection.Writes);
        Assert.Equal("idle", app.Status().Run.Kind);
    }

    [Fact]
    public async Task PreviewCountsManyLinesAndRejectsLimitsBeforeWriting()
    {
        var connection = new FakeConnection();
        await using var app = new AppService(_directory, connection, new FakeCatalog());
        await app.InitializeAsync(); await app.ConnectAsync(new() { PortName = "COM3" });
        Assert.Equal(new SendPreview(20001, 40000), app.Preview(new(string.Concat(Enumerable.Repeat("A\n", 20000)), Ending: "lf", Lines: true)));
        var invalid = Assert.Throws<PayloadEncodeException>(() => app.Preview(new("01\nGG", Hex: true, Ending: "none", Lines: true)));
        Assert.Contains("第 2 行", invalid.Message);
        await Assert.ThrowsAsync<FormatException>(() => app.SendAsync(new(new string('A', AppService.MaxSendChars + 1), Lines: true)));
        await Assert.ThrowsAsync<FormatException>(() => app.SendAsync(new(string.Concat(Enumerable.Repeat("A\n", AppService.MaxSendLines)), Lines: true)));
        await Assert.ThrowsAsync<FormatException>(() => app.SendAsync(new(string.Join('\n', Enumerable.Repeat("A", 10)),
            Ending: "custom", CustomEnding: new string('0', AppService.MaxSendChars), Lines: true)));
        Assert.Empty(connection.Writes);
    }

    [Theory]
    [InlineData("csv", true, 100)]
    [InlineData("csv", true, 530000)]
    [InlineData("txt", false, 100)]
    [InlineData("txt", false, 530000)]
    [InlineData("json", false, 100)]
    [InlineData("json", false, 530000)]
    public async Task ExportChunksHaveConsistentEncodingAndCleanup(string format, bool bom, int size)
    {
        var path = Path.Combine(_directory, "large." + format);
        var content = "\"中文\",\"含\"\"引号\"" + new string('A', size) + "😀\"";
        string temporary;
        using (var session = new LogExportSession(path, format))
        {
            temporary = session.TempPath;
            var firstChunk = Math.Min(300000, content.Length);
            await session.AppendAsync(content[..firstChunk]);
            await session.AppendAsync(content[firstChunk..]);
            await session.FinishAsync();
        }
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(bom, bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal(content, Encoding.UTF8.GetString(bytes.AsSpan(bom ? 3 : 0)));
        Assert.False(File.Exists(temporary));
        var cancelled = new LogExportSession(Path.Combine(_directory, "cancelled." + format), format);
        temporary = cancelled.TempPath;
        await cancelled.AppendAsync("partial"); cancelled.Dispose();
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task FailedExportKeepsTargetAndAllowsRetry()
    {
        var target = Path.Combine(_directory, "target.csv");
        Directory.CreateDirectory(target);
        string temporary;
        using (var session = new LogExportSession(target, "csv"))
        {
            temporary = session.TempPath;
            await session.AppendAsync("partial");
            var error = await Record.ExceptionAsync(() => session.FinishAsync());
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.True(Directory.Exists(target)); Assert.False(File.Exists(temporary));
        Directory.Delete(target);
        using var retry = new LogExportSession(target, "csv");
        await retry.AppendAsync("成功"); await retry.FinishAsync();
        Assert.Equal("成功", await File.ReadAllTextAsync(target));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

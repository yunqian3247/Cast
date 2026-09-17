using System.Collections.Concurrent;
using System.Diagnostics;
using cast.Core;
using cast.Desktop;
using cast.Serial;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace cast.Desktop.Tests;

public sealed class AppServiceTests
{
    [Fact]
    public async Task LogRetentionBoundsCountAndTextBytesAndClearsBudget()
    {
        await using var fixture = await Fixture.Create();
        fixture.Service.ClearLogs();
        for (var i = 0; i < 10005; i++) fixture.Connection.Receive([65]);
        Assert.Equal(10000, fixture.Service.Logs.Count);
        var lastId = fixture.Service.Logs[^1].Id;
        Assert.Equal(lastId - 9999, fixture.Service.Logs[0].Id);
        var payload = Enumerable.Repeat((byte)65, 16384).ToArray();
        for (var i = 0; i < 150; i++) fixture.Connection.Receive(payload);
        Assert.True(fixture.Service.Logs.Sum(AppService.LogTextBytes) <= AppService.MaxLogTextBytes);
        Assert.True(fixture.Service.Logs.Count < 150);
        fixture.Service.ClearLogs();
        fixture.Connection.Receive([66]);
        Assert.Single(fixture.Service.Logs);
        Assert.Equal("B", fixture.Service.Logs[0].Text);
    }

    [Fact]
    public async Task SendsExactUtf8HexSuffixAndLinesAndRejectsInvalidHex()
    {
        await using var fixture = await Fixture.Create();
        var service = fixture.Service;
        await service.SendAsync(new("你好", Ending: "custom", CustomEnding: "0D 0A"));
        await service.SendAsync(new("01 03", true, "none"));
        await service.SendAsync(new("A\r\nB", Ending: "lf", Lines: true));
        Assert.Equal(new byte[] { 0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD, 13, 10 }, fixture.Connection.Writes[0]);
        Assert.Equal(new byte[] { 1, 3 }, fixture.Connection.Writes[1]);
        Assert.Equal(new byte[] { 65, 10 }, fixture.Connection.Writes[2]);
        Assert.Equal(new byte[] { 66, 10 }, fixture.Connection.Writes[3]);
        await Assert.ThrowsAsync<PayloadEncodeException>(() => service.SendAsync(new("GG", true)));
        await Assert.ThrowsAsync<PayloadEncodeException>(() => service.SendAsync(new("01\nG2", true, Lines: true)));
        Assert.Equal(4, fixture.Connection.Writes.Count);
        Assert.Equal(14, service.Status().Tx);
    }

    [Fact]
    public async Task ReceiveDecoderRetainsSplitUtf8AndTracksRawBytes()
    {
        await using var fixture = await Fixture.Create();
        fixture.Connection.Receive([0xE4, 0xBD]);
        fixture.Connection.Receive([0xA0]);
        Assert.Equal("你", string.Concat(fixture.Service.Logs.Where(log => log.Dir == "RX").Select(log => log.Text)));
        Assert.Equal(3, fixture.Service.Status().Rx);
        Assert.Equal("E4 BD", fixture.Service.Logs.First(log => log.Dir == "RX").Hex);
        fixture.Service.ResetStats();
        Assert.Equal(0, fixture.Service.Status().Rx);
        fixture.Service.ClearLogs();
        Assert.Empty(fixture.Service.Logs);
    }

    [Fact]
    public async Task RepeatUsesSnapshotAndStopsWithoutOverlappingWrites()
    {
        await using var fixture = await Fixture.Create();
        fixture.Connection.DelayMs = 35;
        fixture.Service.StartRepeat(new("AT", Ending: "crlf"), 20);
        await Wait(() => fixture.Connection.Writes.Count >= 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendAsync(new("other")));
        fixture.Service.Stop();
        await Wait(() => fixture.Service.Status().Run.Kind == "idle");
        var count = fixture.Connection.Writes.Count;
        await Task.Delay(120);
        Assert.Equal(count, fixture.Connection.Writes.Count);
        Assert.Equal(1, fixture.Connection.MaximumConcurrentWrites);
        Assert.All(fixture.Connection.Writes, bytes => Assert.Equal(new byte[] { 65, 84, 13, 10 }, bytes));
    }

    [Fact]
    public async Task WorkflowSupportsStepPauseResumeAndFiveRounds()
    {
        await using var fixture = await Fixture.Create();
        fixture.Service.Document.Presets = [new() { Id = "a", Name = "A", Content = "A" }, new() { Id = "b", Name = "B", Content = "01", Format = "hex" }];
        fixture.Service.Document.Workflows["wf"] = new() { Name = "Test", Steps = [new() { PresetId = "a", Wait = 20 }, new() { PresetId = "b", Wait = 20 }] };
        fixture.Service.StartWorkflow("wf", "once", stepOnly: true);
        await Wait(() => fixture.Connection.Writes.Count == 1);
        await Task.Delay(100);
        Assert.Single(fixture.Connection.Writes);
        Assert.True(fixture.Service.Status().Run.Paused);
        fixture.Service.Step();
        await Wait(() => fixture.Service.Status().Run.Kind == "idle");
        Assert.Equal(2, fixture.Connection.Writes.Count);
        fixture.Service.StartWorkflow("wf", "count", stepOnly: true);
        await Wait(() => fixture.Connection.Writes.Count == 3);
        fixture.Service.Pause(false);
        await Wait(() => fixture.Service.Status().Run.Kind == "idle");
        Assert.Equal(12, fixture.Connection.Writes.Count);
        Assert.Equal(new byte[] { 1 }, fixture.Connection.Writes[1]);
    }

    [Fact]
    public async Task InvalidWorkflowAndFailedWritesNeverAdvanceToNextStep()
    {
        await using var fixture = await Fixture.Create();
        fixture.Service.Document.Workflows["wf"] = new() { Name = "Test", Steps = [new() { PresetId = "missing" }] };
        Assert.Throws<InvalidOperationException>(() => fixture.Service.StartWorkflow("wf", "once"));
        Assert.Equal("idle", fixture.Service.Status().Run.Kind);
        fixture.Service.Document.Presets = [new() { Id = "p", Name = "P", Content = "AT" }];
        fixture.Service.Document.Workflows["wf"].Steps = [new() { PresetId = "p", Wait = 0 }, new() { PresetId = "p", Wait = 0 }];
        fixture.Connection.FailWrites = true;
        fixture.Service.StartWorkflow("wf", "once");
        await Wait(() => fixture.Service.Status().Run.Kind == "idle");
        Assert.Empty(fixture.Connection.Writes);
        Assert.Equal(0, fixture.Service.Status().Tx);
        Assert.Contains(fixture.Service.Logs, log => log.Text.Contains("失败"));
    }

    [Fact]
    public async Task PortIdentityIsRecheckedAndDisconnectStopsRepeat()
    {
        await using var fixture = await Fixture.Create();
        fixture.Service.StartRepeat(new("AT"), 20);
        await Wait(() => fixture.Connection.Writes.Count >= 1);
        await fixture.Service.DisconnectAsync();
        Assert.False(fixture.Service.Status().Connected);
        Assert.Equal("idle", fixture.Service.Status().Run.Kind);
        fixture.Catalog.Ports = [new("COM3", "replacement", "other")];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(new() { PortName = "COM3", DeviceInstanceId = "device" }));
    }

    [Fact]
    public async Task PinsReflectTransportAndHardwareFlowOwnsRts()
    {
        await using var fixture = await Fixture.Create();
        fixture.Connection.Pins = new(false, false, true, false);
        fixture.Service.SetPins(true, true);
        Assert.Equal(new SerialPinState(true, true, true, false), fixture.Service.Status().Pins);
        fixture.Service.Document.Profile.FlowControl = FlowControlSetting.RtsCts;
        fixture.Service.SetPins(false, false);
        Assert.False(fixture.Service.Document.Profile.DtrEnable);
    }

    [Fact]
    public async Task DocumentRoundTripKeepsEmptyCollectionsAndRejectsCorruptImport()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.DisconnectAsync();
        var document = new AppDocument { History = ["AT"], Presets = [new() { Id = "p", Name = "P", Content = "01 02", Format = "hex", RxMatch = "01", RxDesc = "response" }] };
        await fixture.Service.SaveAsync(document);
        await using var reloaded = new AppService(fixture.Directory, new FakeConnection(), fixture.Catalog);
        await reloaded.InitializeAsync();
        Assert.Equal("response", reloaded.Document.Presets[0].RxDesc);
        var invalid = new AppDocument { Presets = [new() { Id = "p", Name = "P", Content = "0G", Format = "hex" }] };
        await Assert.ThrowsAsync<HexParseException>(() => fixture.Service.SaveAsync(invalid));
        Assert.Single(fixture.Service.Document.Presets);
        await fixture.Service.SaveAsync(new AppDocument());
        await using var empty = new AppService(fixture.Directory, new FakeConnection(), fixture.Catalog);
        await empty.InitializeAsync();
        Assert.Empty(empty.Document.Presets);
        Assert.Empty(empty.Document.Workflows);
    }

    [Fact]
    public async Task DeviceRemovalStopsWorkflowAndKeepsSnapshotDuringPause()
    {
        await using var fixture = await Fixture.Create();
        fixture.Service.Document.Presets = [new() { Id = "p", Name = "P", Content = "01", Format = "hex" }];
        fixture.Service.Document.Workflows["wf"] = new() { Name = "Test", Steps = [new() { PresetId = "p", Wait = 20 }] };
        fixture.Service.StartWorkflow("wf", "loop", stepOnly: true);
        await Wait(() => fixture.Connection.Writes.Count == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(new()));
        fixture.Service.Document.Presets[0].Content = "02";
        fixture.Service.Step();
        await Wait(() => fixture.Connection.Writes.Count == 2);
        Assert.All(fixture.Connection.Writes, bytes => Assert.Equal(new byte[] { 1 }, bytes));
        fixture.Connection.Disconnect();
        await Wait(() => fixture.Service.Status().Run.Kind == "idle");
        Assert.False(fixture.Service.Status().Connected);
        await Task.Delay(80);
        Assert.Equal(2, fixture.Connection.Writes.Count);
    }

    [Fact]
    public async Task ClosingHasBoundedWaitWhenDriverDisposalBlocks()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var connection = new FakeConnection { BlockDispose = true };
        var service = new AppService(directory, connection, new FakeCatalog());
        var watch = Stopwatch.StartNew();
        await service.DisposeAsync();
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.5, 4.5);
        connection.ReleaseDispose.TrySetResult();
    }

    [Fact]
    public async Task ImportedVoicePresetSendsVerifiedFrameWithoutExtraSuffix()
    {
        await using var fixture = await Fixture.Create();
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/presets/main-controller-voice/main-controller-voice-presets.json"));
        fixture.Service.Document.Presets = CommandPreset.ParseImport(File.ReadAllText(path));
        await fixture.Service.SaveAsync(fixture.Service.Document);
        await using var restored = new AppService(fixture.Directory, new FakeConnection(), new FakeCatalog());
        await restored.InitializeAsync();
        Assert.Equal(60, restored.Document.Presets.Count);
        var preset = restored.Document.Presets.Single(p => p.Id == "main-voice-81-00-05");
        await fixture.Service.SendAsync(new(preset.Content, true, "none"));
        fixture.Service.Document.Workflows["wf"] = new() { Name = "Test", Steps = [new() { PresetId = preset.Id, Wait = 0 }] };
        fixture.Service.StartWorkflow("wf", "once");
        await Wait(() => fixture.Connection.Writes.Count == 2 && fixture.Service.Status().Run.Kind == "idle");
        Assert.All(fixture.Connection.Writes, bytes => Assert.Equal(new byte[] { 0xA5, 0xFA, 0x81, 0x00, 0x05, 0x25, 0xFB }, bytes));
    }

    internal static async Task Wait(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(condition(), "Condition timed out");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "PebrelTests", Guid.NewGuid().ToString("N"));
        public FakeConnection Connection { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public AppService Service { get; }
        private Fixture() => Service = new(Directory, Connection, Catalog);
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Service.InitializeAsync();
            await fixture.Service.ConnectAsync(new() { PortName = "COM3", DeviceInstanceId = "device" });
            return fixture;
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}

internal sealed class FakeCatalog : IPortCatalog
{
    public IReadOnlyList<PortInfo> Ports { get; set; } = [new("COM3", "COM3 - Test Adapter", "device")];
    public IReadOnlyList<PortInfo> Enumerate() => Ports;
}

internal sealed class FakeConnection : ISerialConnection, ISerialPinControl
{
    private readonly ConcurrentQueue<byte[]> _writes = new();
    private int _concurrent;
    public List<byte[]> Writes => _writes.ToList();
    public int DelayMs { get; set; }
    public int MaximumConcurrentWrites { get; private set; }
    public bool FailWrites { get; set; }
    public bool BlockDispose { get; set; }
    public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SerialConnectionState State { get; private set; }
    public PortInfo? Port { get; private set; }
    public SerialPinState Pins { get; set; } = new(false, false, false, false);
    public event EventHandler<SerialDataReceivedEventArgs>? DataReceived;
    public event EventHandler<SerialConnectionErrorEventArgs>? Error;
    public event EventHandler? StateChanged;
    public Task OpenAsync(SerialProfile profile, CancellationToken cancellationToken = default)
    {
        Port = new(profile.PortName, profile.PortName, profile.DeviceInstanceId);
        State = SerialConnectionState.Open; StateChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask;
    }
    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var concurrent = Interlocked.Increment(ref _concurrent);
        MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, concurrent);
        try
        {
            if (FailWrites) throw new IOException("write failed");
            if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Enqueue(bytes.ToArray());
        }
        finally { Interlocked.Decrement(ref _concurrent); }
    }
    public void Receive(byte[] bytes) => DataReceived?.Invoke(this, new() { Bytes = bytes });
    public void Disconnect() { State = SerialConnectionState.Disconnected; StateChanged?.Invoke(this, EventArgs.Empty); Error?.Invoke(this,new() { ErrorCode = "gone", Message = "设备已移除" }); }
    public Task CloseAsync(CancellationToken cancellationToken = default) { State = SerialConnectionState.Closed; Port = null; StateChanged?.Invoke(this,EventArgs.Empty); return Task.CompletedTask; }
    public SerialPinState ReadPins() => Pins;
    public void SetPins(bool dtr, bool rts) => Pins = Pins with { Dtr = dtr, Rts = rts };
    public async ValueTask DisposeAsync() { if (BlockDispose) await ReleaseDispose.Task; await CloseAsync(); }
}

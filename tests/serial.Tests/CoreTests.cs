using System.Text.Json;
using serial.Core;

namespace serial.Tests;

public sealed class CoreTests
{
    [Fact]
    public void HexCodec_ParsesCommonSeparatorsAndPrefixes()
    {
        var bytes = HexCodec.Parse("0x01, 02  aF");

        Assert.Equal(new byte[] { 0x01, 0x02, 0xAF }, bytes);
        Assert.Equal("01 02 AF", HexCodec.Format(bytes));
    }

    [Fact]
    public void HexCodec_RejectsOddLength()
    {
        var exception = Assert.Throws<HexParseException>(() => HexCodec.Parse("A"));

        Assert.Equal(0, exception.Position);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, "串口")]
    [InlineData(TextEncodingKind.Gbk, "串口")]
    [InlineData(TextEncodingKind.Ascii, "serial")]
    public void TextCodec_RoundTripsSupportedEncodings(TextEncodingKind kind, string value)
    {
        var bytes = TextCodec.Encode(value, kind);

        Assert.Equal(value, TextCodec.Decode(bytes, kind));
    }

    [Fact]
    public void PayloadCodec_AppendsConfiguredLineEnding()
    {
        var payload = PayloadCodec.Encode("AT", DataMode.Text, TextEncodingKind.Ascii, LineEndingMode.CrLf, null);

        Assert.Equal(new byte[] { (byte)'A', (byte)'T', 0x0D, 0x0A }, payload.Bytes);
    }

    [Fact]
    public void PayloadCodec_ReportsInvalidHexWithStableCode()
    {
        var exception = Assert.Throws<PayloadEncodeException>(() =>
            PayloadCodec.Encode("0x1", DataMode.Hex, TextEncodingKind.Utf8, LineEndingMode.None, null));

        Assert.Equal(ErrorCodes.InvalidHex, exception.ErrorCode);
    }

    [Fact]
    public async Task SessionLogWriter_AppendsAndReadsRawBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serial-debug-{Guid.NewGuid():N}.jsonl");
        SessionExportDocument document;
        await using (var writer = new SessionLogWriter(path, new Session()))
        {
            await writer.AppendAsync(new SessionEvent
            {
                Direction = EventDirection.Rx,
                RawBytes = new byte[] { 0x00, 0xFF },
                DisplayText = "ok"
            });
            await writer.CompleteAsync();
            document = await writer.ReadExportAsync();
        }

        Assert.Single(document.Events);
        Assert.Equal(new byte[] { 0x00, 0xFF }, document.Events[0].RawBytes);
        Assert.NotNull(document.Session.EndedAt);
        File.Delete(path);
    }

    [Fact]
    public async Task SettingsStore_UsesBackupOnCorruptPrimary()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"serial-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore();
        await store.SaveAsync(path, new AppSettings { SerialProfile = new SerialProfile { Name = "first" } });
        await store.SaveAsync(path, new AppSettings { SerialProfile = new SerialProfile { Name = "second" } });
        await File.WriteAllTextAsync(path, "{");

        var settings = await store.LoadAsync(path);

        Assert.Equal("first", settings.SerialProfile.Name);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void WorkflowFactory_CreatesSnapshotWithResolvedPresetAndStepWait()
    {
        var preset = new PresetCommandModel { Id = "wake", Name = "设备唤醒", Content = "AT+WAKE", Mode = "文本", Ending = "CRLF" };
        var workflow = new WorkflowDefinition
        {
            Id = "wf-1",
            Name = "启动检查",
            TriggerMode = WorkflowTriggerMode.Loop,
            PeriodMs = 2500,
            RunCount = 0,
            Steps = [new WorkflowStep { Id = "step-1", PresetId = "wake", WaitAfterMs = 350 }]
        };

        var snapshot = WorkflowFactory.CreateSnapshot(workflow, [preset]);

        Assert.Equal("启动检查", snapshot.WorkflowName);
        Assert.Equal(WorkflowTriggerMode.Loop, snapshot.TriggerMode);
        Assert.Equal(2500, snapshot.PeriodMs);
        Assert.Equal(0, snapshot.RunCount);
        Assert.Single(snapshot.Steps);
        Assert.Equal("AT+WAKE", snapshot.Steps[0].Content);
        Assert.Equal(350, snapshot.Steps[0].WaitAfterMs);
    }

    [Fact]
    public void WorkflowFactory_RejectsMissingPresetReference()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "缺失预设",
            Steps = [new WorkflowStep { PresetId = "deleted" }]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => WorkflowFactory.CreateSnapshot(workflow, []));

        Assert.Contains("缺失预设", exception.Message);
    }

    [Fact]
    public void WorkflowFactory_ClampsInvalidTimingValues()
    {
        var preset = new PresetCommandModel { Id = "p", Name = "P" };
        var workflow = new WorkflowDefinition
        {
            PeriodMs = 0,
            Steps = [new WorkflowStep { PresetId = "p", WaitAfterMs = -1 }]
        };

        var snapshot = WorkflowFactory.CreateSnapshot(workflow, [preset]);

        Assert.Equal(10, snapshot.PeriodMs);
        Assert.Equal(0, snapshot.Steps[0].WaitAfterMs);
    }

    [Fact]
    public void WorkflowFactory_FreezesEncodingAndCustomEnding()
    {
        var preset = new PresetCommandModel
        {
            Id = "p",
            Encoding = TextEncodingKind.Gbk,
            Ending = "自定义 HEX",
            CustomEndingHex = "0D 0A 00"
        };
        var workflow = new WorkflowDefinition { Steps = [new WorkflowStep { PresetId = "p" }] };

        var snapshot = WorkflowFactory.CreateSnapshot(workflow, [preset]);
        preset.Encoding = TextEncodingKind.Utf8;
        preset.CustomEndingHex = string.Empty;

        Assert.Equal(TextEncodingKind.Gbk, snapshot.Steps[0].Encoding);
        Assert.Equal("0D 0A 00", snapshot.Steps[0].CustomEndingHex);
    }

    [Fact]
    public async Task SessionLogStore_ListsReadsFiltersAndDeletesSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"serial-sessions-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "session.jsonl");
        var session = new Session
        {
            SerialProfileSnapshot = new SerialProfile { PortName = "COM8", BaudRate = 57600 }
        };
        await using (var writer = new SessionLogWriter(path, session))
        {
            await writer.AppendAsync(new SessionEvent { Direction = EventDirection.Rx, RawBytes = [1, 2, 3] });
            await writer.AppendAsync(new SessionEvent { Direction = EventDirection.Tx, RawBytes = [4, 5] });
            await writer.CompleteAsync();
        }

        var store = new SessionLogStore(directory);
        var summaries = await store.ListAsync();
        var document = await store.ReadAsync(path);
        var exportPath = Path.Combine(directory, "filtered.json");
        await store.ExportAsync(path, exportPath, item => item.Direction == EventDirection.Rx);
        var exported = JsonSerializer.Deserialize<SessionExportDocument>(await File.ReadAllTextAsync(exportPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Single(summaries);
        Assert.Equal("COM8", summaries[0].PortName);
        Assert.Equal(3, summaries[0].ReceivedBytes);
        Assert.Equal(2, summaries[0].SentBytes);
        Assert.Equal(2, document.Events.Count);
        Assert.Single(exported!.Events);

        store.Delete(path);
        Assert.False(File.Exists(path));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task SettingsStore_ReturnsDefaultsWhenFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "settings.json");

        var settings = await new SettingsStore().LoadAsync(path);

        Assert.Equal(10000, settings.MaxDisplayEvents);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsWorkflowDefinitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"serial-workflow-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings
        {
            SelectedWorkflowId = "workflow-1",
            Workflows =
            [
                new WorkflowDefinition
                {
                    Id = "workflow-1",
                    Name = "启动检查",
                    TriggerMode = WorkflowTriggerMode.Scheduled,
                    PeriodMs = 3000,
                    RunCount = 4,
                    Steps = [new WorkflowStep { PresetId = "wake", WaitAfterMs = 700 }]
                }
            ]
        };

        var store = new SettingsStore();
        await store.SaveAsync(path, settings);
        var loaded = await store.LoadAsync(path);

        Assert.Equal("workflow-1", loaded.SelectedWorkflowId);
        Assert.Single(loaded.Workflows);
        Assert.Equal(WorkflowTriggerMode.Scheduled, loaded.Workflows[0].TriggerMode);
        Assert.Equal(700, loaded.Workflows[0].Steps[0].WaitAfterMs);
        Directory.Delete(directory, recursive: true);
    }
}

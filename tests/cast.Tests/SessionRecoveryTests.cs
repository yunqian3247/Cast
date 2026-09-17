using System.Text.Json;
using cast.Core;

namespace cast.Tests;

public sealed class SessionRecoveryTests
{
    [Fact]
    public async Task IndentedOptionsKeepJsonLinesValidAndIncompleteRunsRecover()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cast-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "session.jsonl");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var session = new Session
        {
            AutoSendRuns = [new() { PresetName = "Auto", SentCount = 2 }],
            WorkflowRuns = [new() { WorkflowName = "Flow", CompletedSteps = 1 }]
        };
        try
        {
            await using (var writer = new SessionLogWriter(path, session, options))
            {
                await writer.AppendAsync(new SessionEvent { Direction = EventDirection.Rx, RawBytes = [1, 2], DisplayText = "line1\nline2" });
                await writer.CheckpointAsync();
            }
            Assert.True(options.WriteIndented);
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                using var json = JsonDocument.Parse(line);
                Assert.True(json.RootElement.TryGetProperty("kind", out _));
            }
            var store = new SessionLogStore(directory, options);
            Assert.Equal(1, await store.RecoverIncompleteSessionsAsync());
            Assert.Equal(0, await store.RecoverIncompleteSessionsAsync());
            var document = await store.ReadAsync(path);
            Assert.Equal(AutoSendRunState.Failed, document.Session.AutoSendRuns.Single().State);
            Assert.Equal(2, document.Session.AutoSendRuns.Single().SentCount);
            Assert.Equal(WorkflowRunState.Failed, document.Session.WorkflowRuns.Single().State);
            Assert.NotNull(document.Session.EndedAt);
            Assert.Equal("line1\nline2", document.Events.Single().DisplayText);
            Assert.True((await store.ListAsync()).Single().HasErrors);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

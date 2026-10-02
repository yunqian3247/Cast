using System.Diagnostics;
using System.Text.Json;
using cast.Desktop;
using Xunit;

namespace cast.Desktop.Tests;

public sealed class AuditWebViewTests
{
    [Fact]
    public void LargePresetImportUsesRealChunkBridge()
    {
        Run(async (form, _, directory) =>
        {
            var presets = Enumerable.Range(0, 5).Select(i => new CommandPreset { Id = $"large-{i}", Name = "分块预设", Content = new string('A', 900000) + "😀" }).ToList();
            var text = JsonSerializer.Serialize(presets, AppService.Json);
            var path = Path.Combine(directory, "large-presets.json"); await File.WriteAllTextAsync(path, text);
            var session = new PresetImportSession(await PresetImportSession.ReadAsync(path));
            // Supply the file-selection result; chunk transfer and parsing use the real host.
            var field = typeof(MainForm).GetField("_import", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            field.SetValue(form, session);
            await form.Browser.ExecuteScriptAsync($$"""
                window.importDone=false;(async()=>{
                  const id={{JsonSerializer.Serialize(session.Id)}};const parts=[];let offset=0;
                  while(offset<{{session.Length}}){const reply=await request('importChunk',{id,offset});parts.push(reply.content);offset+=reply.content.length;}
                  const presets=validatePresets(JSON.parse(parts.join('')));
                  window.importCount=presets.length;window.importUnicode=presets.every(p=>p.content.endsWith('😀'));
                  await request('importFinish',{id});window.importDone=true;
                })().catch(error=>window.importError=error.message);
                """);
            await WaitScript(form, "window.importDone===true");
            Assert.Equal("5", await form.Browser.ExecuteScriptAsync("window.importCount"));
            Assert.Equal("true", await form.Browser.ExecuteScriptAsync("window.importUnicode"));
            Assert.Null(field.GetValue(form));
            form.Close();
        }, _ => { });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeClosePersistsLatestInputIncludingRunningTask(bool running)
    {
        Run(async (form, connection, directory) =>
        {
            if (!running)
            {
                await form.Browser.ExecuteScriptAsync("documentState.history=Array.from({length:50},(_,i)=>String(i)+':'+ 'A'.repeat(90000));documentState.history[0]='chunk-marker';captureUi();const markerOffset=JSON.stringify(documentState).indexOf('chunk-marker');documentState.history[0]='A'.repeat(131071-markerOffset)+'😀'+'B'.repeat(10000);window.largeSaveDone=false;saveNow().then(()=>window.largeSaveDone=true).catch(error=>window.largeSaveError=error.message);");
                await WaitScript(form, "window.largeSaveDone===true");
                Assert.Equal(50, form.Service.Document.History.Count);
                await using var restored = new AppService(directory, new FakeConnection(), new FakeCatalog());
                await restored.InitializeAsync();
                Assert.Equal(form.Service.Document.History, restored.Document.History);
                await form.Browser.ExecuteScriptAsync("window.abortedDone=false;(async()=>{await request('saveStart',{id:'abort-probe',length:100});await request('saveChunk',{id:'abort-probe',offset:0,content:'{'});await request('saveAbort',{id:'abort-probe'});window.abortedDone=true;})();");
                await WaitScript(form, "window.abortedDone===true");
                Assert.Equal(50, form.Service.Document.History.Count);
            }
            else
            {
                await form.Service.ConnectAsync(new() { PortName = "COM3" });
                connection.DelayMs = 100;
                form.Service.StartRepeat(new("repeat"), 20);
                await AppServiceTests.Wait(() => connection.Writes.Count >= 1);
            }
            await form.Browser.ExecuteScriptAsync("window.pendingCloseSave=saveNow();document.getElementById('manualInput').value='native-latest';document.getElementById('manualInput').dispatchEvent(new Event('input',{bubbles:true}));theme='dark';applyAppearance();");
            form.Close();
        }, document =>
        {
            Assert.Equal("native-latest", document.Ui["inputDraft"]!.GetValue<string>());
            Assert.Equal("dark", document.Ui["theme"]!.GetValue<string>());
        });
    }

    [Fact]
    public void NativeCloseSaveFailureKeepsWindowAndInputAndRetryWorks()
    {
        Run(async (form, _, directory) =>
        {
            var blocked = Path.Combine(directory, "cast.json.tmp");
            Directory.CreateDirectory(blocked);
            await form.Browser.ExecuteScriptAsync("document.getElementById('manualInput').value='retry-latest';document.getElementById('manualInput').dispatchEvent(new Event('input',{bubbles:true}));theme='dark';applyAppearance();");
            form.Close();
            await WaitScript(form, "!closePending && document.getElementById('globalToast').textContent.includes('关闭前保存失败')");
            Assert.False(form.IsDisposed); Assert.True(form.Enabled);
            Assert.Equal(JsonSerializer.Serialize("retry-latest"), await form.Browser.ExecuteScriptAsync("document.getElementById('manualInput').value"));
            Assert.Equal("true", await form.Browser.ExecuteScriptAsync("document.body.classList.contains('theme-dark')"));
            Directory.Delete(blocked);
            form.Close();
        }, document =>
        {
            Assert.Equal("retry-latest", document.Ui["inputDraft"]!.GetValue<string>());
            Assert.Equal("dark", document.Ui["theme"]!.GetValue<string>());
        });
    }

    private static async Task WaitScript(MainForm form, string script)
    {
        var watch = Stopwatch.StartNew();
        while (await form.Browser.ExecuteScriptAsync(script) != "true")
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Script timed out: " + script);
            await Task.Delay(20);
        }
    }

    private static void Run(Func<MainForm, FakeConnection, string, Task> scenario, Action<AppDocument> inspect)
    {
        Exception? failure = null;
        var directory = Path.Combine(Path.GetTempPath(), "CastAuditWebView", Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            var connection = new FakeConnection();
            using var form = new MainForm(directory, connection, new FakeCatalog()) { ShowInTaskbar = false };
            form.Shown += async (_, _) =>
            {
                try { await AppServiceTests.Wait(() => form.Ready); await scenario(form, connection, directory); }
                catch (Exception ex)
                {
                    failure = ex;
                    var blocked = Path.Combine(directory, "cast.json.tmp");
                    if (Directory.Exists(blocked)) Directory.Delete(blocked);
                    form.Close();
                }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Audit WebView2 test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        var document = JsonSerializer.Deserialize<AppDocument>(File.ReadAllText(Path.Combine(directory, "cast.json")), AppService.Json)!;
        inspect(document);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
}

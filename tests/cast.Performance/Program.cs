using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using cast.Core;
using cast.Desktop;
using cast.Serial;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [STAThread]
    private static void Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var kind = args.Length > 1 ? args[1] : "short";
        var liveSeconds = args.Length > 3 ? int.Parse(args[3]) : 6;
        var output = Path.Combine(root, "artifacts", "performance", args.Length > 2 ? args[2] : "");
        Directory.CreateDirectory(output);
        var data = Path.Combine(Path.GetTempPath(), "CastAppPerformance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var presets = CommandPreset.ParseImport(File.ReadAllText(Path.Combine(root, "samples/presets/main-controller-voice/main-controller-voice-presets.json")));
        File.WriteAllText(Path.Combine(data, "cast.json"), JsonSerializer.Serialize(new AppDocument { Presets = presets }, Json));
        ApplicationConfiguration.Initialize();
        var connection = new BenchmarkConnection();
        using var form = new MainForm(data, connection, new BenchmarkCatalog()) { ShowInTaskbar = false };
        var payloads = kind == "short" ? presets.Select(p => HexCodec.Parse(p.Content)).ToArray()
            : Enumerable.Range(0, 100).Select(i => Encoding.ASCII.GetBytes($"SEQ={i:D8} TEMP=25.4 STATUS=OK VALUE=0123456789".PadRight(64, '.'))).ToArray();
        var results = new List<object>();
        var boot = Stopwatch.StartNew();
        form.Shown += async (_, _) =>
        {
            try
            {
                await WaitReady(form);
                await Evaluate(form, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "probe.js")));
                await Task.Delay(1000);
                results.Add(new { stage = "empty", bootMs = boot.Elapsed.TotalMilliseconds, memory = Memory(form), page = await Evaluate(form, "perfProbe.snapshot()") });
                Console.WriteLine($"{kind}: baseline captured");

                // Preload the service while page delivery is disabled, then measure the real init payload.
                await form.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setScriptExecutionDisabled", "{\"value\":true}");
                form.Service.ClearLogs();
                for (var i = 0; i < 10000; i++) connection.Receive(payloads[i % payloads.Length]);
                await form.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setScriptExecutionDisabled", "{\"value\":false}");
                await Task.Delay(250);
                var load = Stopwatch.StartNew();
                var navigated = new TaskCompletionSource();
                void Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult();
                form.Browser.CoreWebView2.NavigationCompleted += Loaded;
                form.Browser.Reload();
                await navigated.Task.WaitAsync(TimeSpan.FromSeconds(90));
                form.Browser.CoreWebView2.NavigationCompleted -= Loaded;
                await WaitReady(form);
                var loadMs = load.Elapsed.TotalMilliseconds;
                await Evaluate(form, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "probe.js")));
                await Task.Delay(1000);
                results.Add(new { stage = "loaded-10000", loadMs, memory = Memory(form), page = await Evaluate(form, "perfProbe.snapshot()") });
                Console.WriteLine($"{kind}: 10000 loaded in {loadMs:F0} ms");

                foreach (var (mode, protocol) in new[] { ("text", false), ("text", true), ("both", false), ("both", true) })
                {
                    var timing = await Evaluate(form, $"perfProbe.redraw('{mode}',{protocol.ToString().ToLowerInvariant()})");
                    results.Add(new { stage = $"redraw-{mode}-protocol-{protocol}", timing, modeChangeMs = await Evaluate(form, "perfProbe.lastModeChangeMs"), memory = Memory(form), page = await Evaluate(form, "perfProbe.snapshot()") });
                    Console.WriteLine($"{kind}: {mode} protocol={protocol}: {timing.GetProperty("median").GetDouble():F0} ms/redraw");
                }
                await Evaluate(form, "perfProbe.configure('text',false)");
                results.Add(new { stage = "search", timing = await Evaluate(form, "perfProbe.search()"), workMs = await Evaluate(form, "perfProbe.searchWorkMs"), memory = Memory(form) });
                await Evaluate(form, "perfProbe.configure('text',false)");
                results.Add(new { stage = "settings-static", elapsedMs = await Evaluate(form, "perfProbe.settings()") });
                await using (var screenshot = File.Create(Path.Combine(output, $"10000-{kind}.png")))
                    await form.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);

                await Evaluate(form, "perfProbe.reset()");
                var startId = form.Service.Logs.Last().Id;
                Console.WriteLine($"{kind}: live start backend ID={startId}, state={connection.State}");
                var live = Stopwatch.StartNew();
                var sent = 0;
                var samples = new List<object>();
                var latencies = new List<double>();
                var producer = Task.Run(async () =>
                {
                    while (live.Elapsed < TimeSpan.FromSeconds(liveSeconds))
                    {
                        connection.Receive(payloads[Interlocked.Increment(ref sent) % payloads.Length]);
                        await Task.Delay(10);
                    }
                });
                while (!producer.IsCompleted)
                {
                    samples.Add(Memory(form));
                    var ping = Stopwatch.StartNew();
                    await Evaluate(form, "performance.now()");
                    latencies.Add(ping.Elapsed.TotalMilliseconds);
                    await Task.Delay(200);
                }
                await producer;
                var sendElapsedMs = live.Elapsed.TotalMilliseconds;
                var drain = Stopwatch.StartNew();
                var lastId = form.Service.Logs.Last().Id;
                if (lastId <= startId) throw new InvalidOperationException($"Simulation produced no service logs: sent={sent}, state={connection.State}, connected={form.Service.Status().Connected}");
                while (true)
                {
                    var latest = await Evaluate(form, "logs.at(-1)?.id");
                    if (latest.GetInt64() == lastId) break;
                    if (drain.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Live log queue failed to drain");
                    await Task.Delay(100);
                }
                await Evaluate(form, "new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
                var livePage = await Evaluate(form, "perfProbe.snapshot()");
                if (livePage.GetProperty("rendersMs").GetProperty("count").GetInt32() == 0) throw new InvalidOperationException($"No live renders despite service IDs {startId} -> {lastId}");
                results.Add(new { stage = "live", sent, startId, lastId, sendElapsedMs, drainMs = drain.Elapsed.TotalMilliseconds,
                    probeRoundTripMs = latencies, memorySamples = samples, memory = Memory(form), page = livePage, backendRows = form.Service.Logs.Count });
                Console.WriteLine($"{kind}: live {sent} records, max probe delay {latencies.Max():F0} ms");
                form.Service.ClearLogs();
                results.Add(new { stage = "clear", elapsedMs = await Evaluate(form, "perfProbe.clear()") });
                await Task.Delay(3000);
                results.Add(new { stage = "after-clear-idle", memory = Memory(form), page = await Evaluate(form, "perfProbe.snapshot()") });
                await form.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("HeapProfiler.collectGarbage", "{}");
                GC.Collect(); GC.WaitForPendingFinalizers();
                await Task.Delay(500);
                results.Add(new { stage = "after-clear-gc-diagnostic", memory = Memory(form), page = await Evaluate(form, "perfProbe.snapshot()") });
                File.WriteAllText(Path.Combine(output, $"10000-{kind}.json"), JsonSerializer.Serialize(new { kind, payloadBytes = payloads[0].Length,
                    presetCount = presets.Count, viewport = form.ClientSize, webViewVersion = form.Browser.CoreWebView2.Environment.BrowserVersionString, results }, Json));
                Console.WriteLine($"{kind}: completed");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, $"10000-{kind}-failure.json"), JsonSerializer.Serialize(new { error = error.ToString(), results }, Json));
                Console.Error.WriteLine(error); Environment.ExitCode = 1;
            }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private static async Task WaitReady(MainForm form)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (form.Ready && await form.Browser.ExecuteScriptAsync("document.body.dataset.ready") == "\"true\"") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("WebView page did not become ready");
    }

    private static async Task<JsonElement> Evaluate(MainForm form, string expression)
    {
        var response = await form.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(90));
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("exceptionDetails", out var exception)) throw new InvalidOperationException(exception.ToString());
        return document.RootElement.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : JsonSerializer.SerializeToElement<object?>(null);
    }

    private static object Memory(MainForm form)
    {
        var processes = form.Browser.CoreWebView2.Environment.GetProcessInfos().Select(info => (Id: info.ProcessId, Kind: info.Kind.ToString()))
            .Append((Id: Environment.ProcessId, Kind: "Host")).DistinctBy(info => info.Id);
        var rows = new List<object>();
        long working = 0, committed = 0;
        foreach (var (id, kind) in processes)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                working += process.WorkingSet64; committed += process.PrivateMemorySize64;
                rows.Add(new { id, kind, workingBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64 });
            }
            catch (ArgumentException) { }
        }
        return new { at = DateTimeOffset.Now, workingBytes = working, privateBytes = committed, processes = rows };
    }
}

internal sealed class BenchmarkCatalog : IPortCatalog
{
    public IReadOnlyList<PortInfo> Enumerate() => [new("BENCH", "BENCH - Simulated input", "performance")];
}

internal sealed class BenchmarkConnection : ISerialConnection
{
    public SerialConnectionState State { get; private set; } = SerialConnectionState.Open;
    public PortInfo? Port => new("BENCH", "BENCH", "performance");
    public event EventHandler<SerialDataReceivedEventArgs>? DataReceived;
    public event EventHandler<SerialConnectionErrorEventArgs>? Error { add { } remove { } }
    public event EventHandler? StateChanged;
    public void Receive(byte[] bytes) => DataReceived?.Invoke(this, new() { Bytes = bytes });
    public Task OpenAsync(SerialProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Receive-only benchmark");
    public Task CloseAsync(CancellationToken cancellationToken = default) { State = SerialConnectionState.Closed; StateChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public async ValueTask DisposeAsync() => await CloseAsync();
}

using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using cast.Desktop;
using Xunit;

namespace cast.Desktop.Tests;

public sealed class WebViewTests
{
    [Fact]
    public void DesktopLoadsLocalPageAndCompletesRealHostMessages()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var directory = Path.Combine(Path.GetTempPath(), "PebrelWebViewTests", Guid.NewGuid().ToString("N"));
            var connection = new FakeConnection();
            using var form = new MainForm(directory, connection, new FakeCatalog()) { ShowInTaskbar = false };
            Assert.Equal("cast", form.Text);
            Assert.Equal("cast", typeof(MainForm).Assembly.GetName().Name);
            Assert.Equal(new Size(1120, 700), form.Size);
            form.Shown += async (_, _) =>
            {
                try
                {
                    await AppServiceTests.Wait(() => form.Ready);
                    await Task.Delay(300);
                    Assert.Equal("\"true\"", await form.Browser.ExecuteScriptAsync("document.body.dataset.ready"));
                    var loadingLabel = Assert.Single(form.Controls.OfType<Label>());
                    Assert.Equal("正在打开cast", loadingLabel.Text);
                    Assert.Equal("https://cast.example/index.html", form.Browser.CoreWebView2.Source);
                    Assert.Equal("\"cast\"", await form.Browser.ExecuteScriptAsync("document.title"));
                    Assert.Equal("\"cast\"", await form.Browser.ExecuteScriptAsync("document.querySelector('.titlebar-brand').textContent"));
                    Assert.Equal("Sarasa Gothic SC", loadingLabel.Font.FontFamily.GetName(1033));
                    await form.Browser.ExecuteScriptAsync("document.fonts.ready.then(() => window.bundledFontsLoaded = ['400','700'].every(weight => [...document.fonts].some(font => font.family === 'Sarasa Gothic SC' && font.weight === weight && font.status === 'loaded')));");
                    var fontTimeout = System.Diagnostics.Stopwatch.StartNew();
                    while (await form.Browser.ExecuteScriptAsync("window.bundledFontsLoaded === true") != "true")
                    {
                        Assert.True(fontTimeout.Elapsed < TimeSpan.FromSeconds(10), "Bundled Sarasa fonts failed to load in WebView2");
                        await Task.Delay(100);
                    }
                    var version = typeof(MainForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
                    Assert.Equal(JsonSerializer.Serialize("v" + version), await form.Browser.ExecuteScriptAsync("document.getElementById('aboutVersion').textContent"));
                    Assert.Equal("0", await form.Browser.ExecuteScriptAsync("document.querySelectorAll('.workspace-buttons,.file-transfer,.session-workspace').length"));
                    Assert.True(form.Browser.CoreWebView2.Settings.IsNonClientRegionSupportEnabled);
                    Assert.Equal("36", await form.Browser.ExecuteScriptAsync("document.getElementById('desktopTitlebar').offsetHeight"));
                    Assert.Equal(form.Top, form.PointToScreen(Point.Empty).Y);
                    Assert.InRange(form.Height - form.ClientSize.Height, 0, 20);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnPinWindow').click();");
                    await AppServiceTests.Wait(() => form.TopMost);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnPinWindow').click();");
                    await AppServiceTests.Wait(() => !form.TopMost);
                    var originalBounds = form.Bounds;
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnWinMax').click();");
                    await AppServiceTests.Wait(() => form.WindowState == FormWindowState.Maximized);
                    Assert.True(Screen.FromControl(form).WorkingArea.Contains(form.RectangleToScreen(form.ClientRectangle)), $"Maximized client {form.RectangleToScreen(form.ClientRectangle)} outside {Screen.FromControl(form).WorkingArea}");
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnWinMax').click();");
                    await AppServiceTests.Wait(() => form.WindowState == FormWindowState.Normal);
                    Assert.Equal(originalBounds, form.Bounds);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnWinMin').click();");
                    await AppServiceTests.Wait(() => form.WindowState == FormWindowState.Minimized);
                    form.WindowState = FormWindowState.Normal;
                    Assert.Equal(originalBounds, form.Bounds);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('selPort').value='COM3';document.getElementById('selPort').dispatchEvent(new Event('change'));document.getElementById('btnConnect').click();");
                    await AppServiceTests.Wait(() => form.Service.Status().Connected);
                    await Task.Delay(100);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('manualInput').value='AT+PING';document.getElementById('btnSend').click();");
                    await AppServiceTests.Wait(() => connection.Writes.Count == 1);
                    Assert.Equal(System.Text.Encoding.UTF8.GetBytes("AT+PING\r\n"), connection.Writes[0]);
                    connection.Receive(System.Text.Encoding.UTF8.GetBytes("+PONG"));
                    await Task.Delay(150);
                    Assert.Contains("+PONG", await form.Browser.ExecuteScriptAsync("document.getElementById('logStream').textContent"));
                    await form.Browser.ExecuteScriptAsync("setLogExpanded(logs.find(log => log.text === '+PONG'), true);");
                    Assert.Equal("true", await form.Browser.ExecuteScriptAsync("logView.follow && logView.followEnabled"));
                    await form.Browser.ExecuteScriptAsync("window.chrome.webview.postMessage({id:'invalid-test',command:'send',data:{text:'GG',hex:true}});");
                    await Task.Delay(100);
                    Assert.Single(connection.Writes);
                    await form.Browser.ExecuteScriptAsync("window.chrome.webview.postMessage({id:'unknown-test',command:'unknown',data:{}});");
                    await Task.Delay(100);
                    Assert.True(form.Service.Status().Connected);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('terminalSearch').value='';logs=[];renderLogs();");
                    form.Service.ClearLogs();
                    for (var i = 0; i < 10005; i++) connection.Receive(System.Text.Encoding.ASCII.GetBytes($"BENCH-{i:D5}"));
                    var expectedLastId = form.Service.Logs[^1].Id;
                    var batchTimeout = System.Diagnostics.Stopwatch.StartNew();
                    while (await form.Browser.ExecuteScriptAsync("logs.at(-1)?.id") != expectedLastId.ToString())
                    {
                        Assert.True(batchTimeout.Elapsed < TimeSpan.FromSeconds(10), "Batched logs timed out");
                        await Task.Delay(100);
                    }
                    await Task.Delay(150);
                    Assert.Equal("10000", await form.Browser.ExecuteScriptAsync("logs.length"));
                    Assert.Equal(form.Service.Logs[0].Id.ToString(), await form.Browser.ExecuteScriptAsync("logs[0].id"));
                    Assert.Equal("true", await form.Browser.ExecuteScriptAsync("document.querySelectorAll('.log-entry').length < 150"));
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnClearTerminal').click();document.getElementById('btnConfirmAction').click();");
                    await AppServiceTests.Wait(() => form.Service.Logs.Count == 0);
                    await Task.Delay(150);
                    Assert.Equal("0", await form.Browser.ExecuteScriptAsync("logs.length"));
                    Assert.Equal("true", await form.Browser.ExecuteScriptAsync("logView.follow"));
                    connection.Receive(System.Text.Encoding.ASCII.GetBytes("AFTER-CLEAR"));
                    await Task.Delay(250);
                    Assert.Equal("1", await form.Browser.ExecuteScriptAsync("logs.length"));
                    Assert.Equal("\"AFTER-CLEAR\"", await form.Browser.ExecuteScriptAsync("logs[0].text"));
                    var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/pebrel-checks"));
                    Directory.CreateDirectory(output);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnToggleRight').click();");
                    foreach (var size in new[] { new Size(880, 560), new Size(1120, 700), new Size(1280, 800) })
                    {
                        form.Size = size;
                        await Task.Delay(150);
                        await using var screenshot = File.Create(Path.Combine(output, $"desktop-{size.Width}.png"));
                        await form.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                    }
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnConnect').click();");
                    await AppServiceTests.Wait(() => !form.Service.Status().Connected);
                    var before = form.Browser.CoreWebView2.Source;
                    form.Browser.CoreWebView2.Navigate("https://example.com/");
                    await Task.Delay(150);
                    Assert.Equal(before, form.Browser.CoreWebView2.Source);
                    await form.Browser.ExecuteScriptAsync("document.getElementById('btnWinClose').click();");
                    await AppServiceTests.Wait(() => form.IsDisposed);
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WebView2 test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}

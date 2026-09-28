using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using cast.Desktop;
using cast.Serial;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace cast.Desktop.Tests;

public sealed class StartupSplashTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnimationPreferencePreservesCompletionAndCleanup(bool motion)
    {
        Exception? failure = null;
        var revealed = false;
        var revealedAfter = TimeSpan.Zero;
        var thread = new Thread(() =>
        {
            using var icon = new Icon(typeof(MainForm), "Assets.cast.ico");
            using var font = new Font(FontFamily.GenericSansSerif, 9F);
            using var splash = new StartupSplash(icon, font, motion);
            splash.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Delay(210);
                    Assert.False(splash.AnimationRunning);
                    Assert.Equal(motion, splash.MotionEnabled);
                    Assert.Equal(motion ? 0 : 1, splash.Opacity);
                    var watch = Stopwatch.StartNew();
                    splash.Complete(() => { revealed = true; revealedAfter = watch.Elapsed; });
                    if (motion)
                    {
                        Assert.True(splash.AnimationRunning); Assert.False(revealed);
                        await Task.Delay(210);
                        Assert.Equal(1, splash.Opacity); Assert.False(revealed);
                    }
                }
                catch (Exception ex) { failure = ex; splash.Dismiss(); }
            };
            Application.Run(splash);
            Assert.True(splash.IsDisposed); Assert.False(splash.AnimationRunning);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Splash preference test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.True(revealed);
        if (motion) Assert.True(revealedAfter >= TimeSpan.FromMilliseconds(600));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void LoadedFramePrecedesCenteredAnimationAndRemainsStableAfterReveal(string theme)
    {
        StartupSplash? observed = null;
        Run(async (form, catalog) =>
        {
            var splash = observed = Assert.IsType<StartupSplash>(form.StartupAnimation);
            Assert.False(splash.Visible); Assert.False(splash.AnimationRunning);
            Assert.Equal(0, form.Opacity);
            Assert.False(form.Ready); Assert.False(form.StartupFramePrepared);
            await Task.Delay(200);
            Assert.False(splash.Visible); Assert.False(splash.AnimationRunning);
            Assert.Equal(0, form.Opacity);
            catalog.Release();
            await AppServiceTests.Wait(() => form.Ready && form.StartupFramePrepared);
            if (splash.MotionEnabled)
            {
                Assert.True(splash.Visible); Assert.True(splash.AnimationRunning);
                Assert.Equal(0, form.Opacity);
            }
            Assert.Equal("\"true\"", await form.Browser.ExecuteScriptAsync("document.body.dataset.ready"));
            Assert.Equal("true", await form.Browser.ExecuteScriptAsync("document.fonts.status==='loaded'&&document.fonts.check('400 12px \"Sarasa Gothic SC\"')&&document.fonts.check('700 12px \"Sarasa Gothic SC\"')"));
            var background = await form.Browser.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor.match(/[\\d.]+/g).slice(0,3).map(Number)");
            var rgb = JsonSerializer.Deserialize<int[]>(background)!;
            var expectedBackground = Color.FromArgb(rgb[0], rgb[1], rgb[2]);
            Assert.Equal(expectedBackground, form.BackColor);
            Assert.Equal(expectedBackground, form.Browser.DefaultBackgroundColor);
            Assert.Equal(theme == "dark" ? "true" : "false", await form.Browser.ExecuteScriptAsync("document.body.classList.contains('theme-dark')"));
            const string layout = "JSON.stringify(['#desktopTitlebar','.cast-shell','.terminal-bar','#manualInput'].map(selector=>{const node=document.querySelector(selector),rect=node.getBoundingClientRect(),style=getComputedStyle(node);return [rect.x,rect.y,rect.width,rect.height,style.fontFamily,style.fontSize]}))";
            var preparedLayout = await form.Browser.ExecuteScriptAsync(layout);
            var output = OutputDirectory();
            await SavePreview(form, Path.Combine(output, $"main-prepared-{theme}.png"));
            Assert.False(splash.ShowInTaskbar);
            Assert.Equal(FormBorderStyle.None, splash.FormBorderStyle);
            var scale = splash.DeviceDpi / 96F;
            Assert.InRange(splash.ClientSize.Width / scale, 175, 177);
            Assert.InRange(splash.ClientSize.Height / scale, 187, 189);
            Assert.Equal("cast 正在启动", splash.AccessibleName);
            if (splash.MotionEnabled)
            {
                var area = Screen.FromControl(form).WorkingArea;
                Assert.InRange(splash.Left + splash.Width / 2 - (area.Left + area.Width / 2), -1, 1);
                Assert.InRange(splash.Top + splash.Height / 2 - (area.Top + area.Height / 2), -1, 1);
                using var first = Capture(splash);
                await Task.Delay(90);
                using var second = Capture(splash);
                AssertLogoCentered(second, scale);
                Assert.NotEqual(Hash(first), Hash(second));
                Assert.Equal(0, form.Opacity);
                first.Save(Path.Combine(output, "startup-frame-1.png"), ImageFormat.Png);
                second.Save(Path.Combine(output, "startup-frame-2.png"), ImageFormat.Png);
            }
            await AppServiceTests.Wait(() => form.Ready && form.Opacity == 1 && splash.IsDisposed);
            Assert.False(splash.AnimationRunning);
            Assert.DoesNotContain(splash, form.OwnedForms);
            Assert.Equal(preparedLayout, await form.Browser.ExecuteScriptAsync(layout));
            Assert.False(Assert.Single(form.Controls.OfType<Label>()).Visible);
            await SavePreview(form, Path.Combine(output, $"main-ready-{theme}.png"));
            using var prepared = new Bitmap(Path.Combine(output, $"main-prepared-{theme}.png"));
            using var ready = new Bitmap(Path.Combine(output, $"main-ready-{theme}.png"));
            Assert.Equal(prepared.Size, ready.Size);
            // The empty lower log area keeps its theme background across the reveal.
            Assert.Equal(prepared.GetPixel(prepared.Width / 2, prepared.Height / 3), ready.GetPixel(ready.Width / 2, ready.Height / 3));
            form.Close();
        }, theme);
        Assert.NotNull(observed); Assert.True(observed.IsDisposed);
    }

    [Fact]
    public void ClosingDuringPreparationPreventsAnimationAndLateReveal()
    {
        StartupSplash? observed = null;
        Run(async (form, catalog) =>
        {
            var splash = observed = Assert.IsType<StartupSplash>(form.StartupAnimation);
            await Task.Delay(150);
            Assert.False(form.Ready); Assert.False(form.StartupFramePrepared);
            Assert.False(splash.Visible); Assert.False(splash.AnimationRunning);
            form.Close();
            catalog.Release();
        });
        Assert.NotNull(observed); Assert.True(observed.IsDisposed); Assert.False(observed.AnimationRunning);
    }

    [Fact]
    public void ClosingAnimationClosesApplicationAndReleasesResources()
    {
        StartupSplash? observed = null;
        Run(async (form, catalog) =>
        {
            var splash = observed = Assert.IsType<StartupSplash>(form.StartupAnimation);
            catalog.Release();
            await AppServiceTests.Wait(() => form.Ready && form.StartupFramePrepared);
            if (splash.MotionEnabled) { Assert.True(splash.Visible); splash.Close(); }
            else form.Close();
        });
        Assert.NotNull(observed); Assert.True(observed.IsDisposed); Assert.False(observed.AnimationRunning);
    }

    [Fact]
    public void InitializationFailureRevealsErrorAndReleasesAnimation()
    {
        Run(async (form, catalog) =>
        {
            var splash = Assert.IsType<StartupSplash>(form.StartupAnimation);
            Assert.False(splash.Visible); Assert.False(splash.AnimationRunning);
            await AppServiceTests.Wait(() => form.Browser.CoreWebView2 is not null
                && form.Browser.CoreWebView2.Source == "https://cast.example/index.html");
            var watch = Stopwatch.StartNew();
            while (await form.Browser.ExecuteScriptAsync("typeof request") != "\"function\"")
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
                await Task.Delay(20);
            }
            await form.Browser.ExecuteScriptAsync("request('startupError',{message:'启动故障验证'}).catch(()=>{});");
            await AppServiceTests.Wait(() => form.Opacity == 1 && splash.IsDisposed);
            Assert.False(form.Ready);
            Assert.True(form.Visible); Assert.True(form.Enabled);
            var error = Assert.Single(form.Controls.OfType<Label>());
            Assert.True(error.Visible); Assert.Contains("启动故障验证", error.Text);
            Assert.False(splash.AnimationRunning);
            catalog.Release(); form.Close();
        });
    }

    private static async Task SavePreview(MainForm form, string path)
    {
        await using var preview = File.Create(path);
        await form.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
    }

    private static Bitmap Capture(StartupSplash splash)
    {
        var bitmap = new Bitmap(splash.ClientSize.Width, splash.ClientSize.Height);
        splash.DrawToBitmap(bitmap, new Rectangle(Point.Empty, splash.ClientSize));
        return bitmap;
    }

    private static string Hash(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void AssertLogoCentered(Bitmap bitmap, float scale)
    {
        var left = bitmap.Width; var top = bitmap.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < bitmap.Height * 2 / 3; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A < 200 || pixel.R > 110 || pixel.G > 110 || pixel.B > 110) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        Assert.True(right >= left && bottom >= top, "Startup icon was not rendered");
        Assert.InRange((left + right) / 2F - bitmap.Width / 2F, -3 * scale, 3 * scale);
        Assert.InRange((top + bottom) / 2F - bitmap.Height / 2F, -3 * scale, 3 * scale);
    }

    private static string OutputDirectory()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/startup-checks"));
        Directory.CreateDirectory(directory); return directory;
    }

    private static void Run(Func<MainForm, DelayedCatalog, Task> scenario, string theme = "light")
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var directory = Path.Combine(Path.GetTempPath(), "CastStartupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var document = new AppDocument(); document.Ui["theme"] = theme;
            File.WriteAllText(Path.Combine(directory, "cast.json"), JsonSerializer.Serialize(document, AppService.Json));
            var catalog = new DelayedCatalog();
            using var form = new MainForm(directory, new FakeConnection(), catalog) { ShowInTaskbar = false };
            form.Shown += async (_, _) =>
            {
                try { await scenario(form, catalog); }
                catch (Exception ex) { failure = ex; catalog.Release(); form.Close(); }
            };
            Application.Run(form);
            catalog.Release();
            Assert.True(form.IsDisposed);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Startup splash test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class DelayedCatalog : IPortCatalog
    {
        private readonly ManualResetEventSlim _release = new(false);
        public void Release() => _release.Set();
        public IReadOnlyList<PortInfo> Enumerate()
        {
            if (!_release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Startup test gate timed out");
            return [new("COM3", "COM3 - Test Adapter", "device")];
        }
    }
}

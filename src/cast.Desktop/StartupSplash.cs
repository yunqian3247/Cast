using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace cast.Desktop;

internal sealed class StartupSplash : Form
{
    private const double EnterMs = 180;
    private const double ExitMs = 180;
    private const double MinimumVisibleMs = 450;
    private readonly Bitmap _logo;
    private readonly Font _titleFont;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 16 };
    private readonly Stopwatch _elapsed = new();
    private Action? _reveal;
    private double _exitAt = -1;
    private double _revealAt = MinimumVisibleMs;
    private bool _allowClose;
    private Size _shapeSize;
    private int _shapeDpi;

    internal bool MotionEnabled { get; }
    internal bool AnimationRunning => _animation.Enabled;
    internal event EventHandler? CloseRequested;

    public StartupSplash(Icon icon, Font font, bool? motionEnabled = null)
    {
        MotionEnabled = motionEnabled ?? SystemInformation.UIEffectsEnabled;
        using var largeIcon = new Icon(icon, new Size(256, 256));
        _logo = largeIcon.ToBitmap();
        _titleFont = new Font(font.FontFamily, 14.5F, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "cast 正在启动";
        AccessibleName = Text;
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(176, 188);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.White;
        DoubleBuffered = true;
        KeyPreview = true;
        Opacity = MotionEnabled ? 0 : 1;
        _animation.Tick += (_, _) => Advance();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        CenterOnScreen();
        UpdateShape();
        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        CenterOnScreen();
        UpdateShape();
        if (_reveal is not null) BeginAnimation();
        base.OnShown(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated)
        {
            UpdateShape();
            if (_elapsed.IsRunning) CenterOnScreen();
        }
    }

    private void CenterOnScreen()
    {
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    private void UpdateShape()
    {
        if (_shapeSize == ClientSize && _shapeDpi == DeviceDpi) return;
        _shapeSize = ClientSize; _shapeDpi = DeviceDpi;
        var scale = DeviceDpi / 96F;
        using var outline = RoundedRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 14 * scale);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    internal void Complete(Action reveal, bool immediately = false)
    {
        if (IsDisposed || _allowClose || _reveal is not null) return;
        if (immediately || !MotionEnabled) { reveal(); Dismiss(); return; }
        _reveal = reveal;
        _revealAt = MinimumVisibleMs;
        if (Visible) BeginAnimation();
    }

    private void BeginAnimation()
    {
        _elapsed.Restart();
        _animation.Start();
        Advance();
    }

    internal void Dismiss()
    {
        if (IsDisposed) return;
        _allowClose = true;
        _animation.Stop();
        _reveal = null;
        Close();
    }

    private void Advance()
    {
        if (IsDisposed || _allowClose) return;
        var milliseconds = _elapsed.Elapsed.TotalMilliseconds;
        if (_reveal is not null && _exitAt < 0 && milliseconds >= _revealAt)
            _exitAt = milliseconds;
        if (_exitAt >= 0)
        {
            var progress = (milliseconds - _exitAt) / ExitMs;
            if (progress >= 1)
            {
                var reveal = _reveal; _reveal = null;
                reveal?.Invoke(); Dismiss(); return;
            }
            SetOpacity(1 - Ease(progress));
        }
        else if (MotionEnabled) SetOpacity(Ease(milliseconds / EnterMs));
        Invalidate();
    }

    private void SetOpacity(double value)
    {
        if (Opacity != value) Opacity = value;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scale = DeviceDpi / 96F;
        graphics.ScaleTransform(scale, scale);
        var width = ClientSize.Width / scale;
        var height = ClientSize.Height / scale;
        using var border = new Pen(Color.FromArgb(229, 232, 235));
        using var outline = RoundedRectangle(new RectangleF(.5F, .5F, width - 1, height - 1), 14);
        graphics.DrawPath(border, outline);

        var seconds = _elapsed.Elapsed.TotalSeconds;
        var enter = MotionEnabled ? Ease(_elapsed.Elapsed.TotalMilliseconds / EnterMs) : 1;
        var pulse = MotionEnabled ? .018 * Math.Sin(seconds * Math.PI * 2 / 1.6) : 0;
        var size = (float)(112 * (.94 + .06 * enter + pulse));
        graphics.DrawImage(_logo, new RectangleF((width - size) / 2, (height - size) / 2, size, size));
        using var text = new SolidBrush(Color.FromArgb(44, 48, 53));
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("cast", _titleFont, text, new RectangleF(16, height / 2 + 42, width - 32, 24), centered);
        for (var i = 0; i < 3; i++)
        {
            var strength = MotionEnabled ? (Math.Sin(seconds * Math.PI * 2 / 1.1 - i * .9) + 1) / 2 : .45;
            using var dot = new SolidBrush(Color.FromArgb((int)(55 + 120 * strength), 52, 57, 62));
            var lift = MotionEnabled ? (float)(1.6 * strength) : 0;
            graphics.FillEllipse(dot, width / 2 - 12 + i * 10, height - 18 - lift, 4, 4);
        }
    }

    private static double Ease(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return 1 - Math.Pow(1 - value, 3);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            // The owner completes its save handshake before dismissing this window.
            if (e.CloseReason != CloseReason.FormOwnerClosing)
                BeginInvoke(() => { if (!IsDisposed && !_allowClose) CloseRequested?.Invoke(this, EventArgs.Empty); });
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Stop(); _animation.Dispose();
            _logo.Dispose(); _titleFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

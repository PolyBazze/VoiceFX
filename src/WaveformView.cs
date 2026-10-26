using System.Drawing.Drawing2D;

namespace VoiceFx;

/// <summary>
/// Oscilloscope-style live view: a dark blue grid with glowing lines for the
/// voice sent to Teams (cyan) and the plain microphone (soft white).
/// </summary>
sealed class WaveformView : Control
{
    const int WindowMs = 30;
    static readonly Color Wet = Color.FromArgb(80, 220, 255);
    static readonly Color Dry = Color.FromArgb(235, 235, 255);

    readonly Func<EffectChain?> source;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    float[] dry = Array.Empty<float>(), wet = Array.Empty<float>();
    float scale = 4f;
    bool wasRunning = true;

    public WaveformView(Func<EffectChain?> source)
    {
        this.source = source;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        timer.Tick += (_, _) =>
        {
            // Redraw ~30 times a second while audio runs; once more when it stops.
            bool running = source() != null;
            if (running || wasRunning) Invalidate();
            wasRunning = running;
        };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        timer.Enabled = Visible; // no work while the section is folded
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var bg = new LinearGradientBrush(r, Color.FromArgb(8, 12, 45), Color.FromArgb(28, 38, 105), LinearGradientMode.Vertical))
            g.FillRectangle(bg, r);
        DrawGrid(g, r);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float mid = r.Height / 2f;
        var chain = source();
        if (chain == null)
        {
            DrawGlow(g, new[] { new PointF(0, mid), new PointF(r.Width, mid) }, Wet);
            TextRenderer.DrawText(g, "Press Enable to see your voice", Font, new Point(8, 6), Color.FromArgb(150, 170, 230));
            return;
        }

        int window = chain.WaveFormat.SampleRate * WindowMs / 1000;
        if (wet.Length != window * 2)
        {
            dry = new float[window * 2];
            wet = new float[window * 2];
        }
        chain.DryTap.ReadLatest(dry, dry.Length);
        chain.WetTap.ReadLatest(wet, wet.Length);

        // Start at a rising zero crossing so the wave stands still instead of jittering.
        int start = 0;
        for (int i = 1; i < window; i++)
            if (wet[i - 1] < 0 && wet[i] >= 0) { start = i; break; }

        // Auto-zoom: quiet voices still fill the view; silence stays flat.
        float peak = 0;
        for (int i = start; i < start + window; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(wet[i]), Math.Abs(dry[i])));
        float target = 0.85f / Math.Max(peak, 0.05f);
        scale += (target - scale) * (target < scale ? 0.5f : 0.1f);

        DrawGlow(g, Trace(dry, start, window, r, mid), Color.FromArgb(110, Dry), thin: true);
        DrawGlow(g, Trace(wet, start, window, r, mid), Wet);

        TextRenderer.DrawText(g, "— to Teams", Font, new Point(8, r.Height - Font.Height - 4), Wet);
        TextRenderer.DrawText(g, "— your mic", Font, new Point(90, r.Height - Font.Height - 4), Color.FromArgb(170, Dry));
    }

    PointF[] Trace(float[] samples, int start, int count, Rectangle r, float mid)
    {
        var pts = new PointF[r.Width];
        for (int x = 0; x < pts.Length; x++)
        {
            float v = samples[start + x * (count - 1) / Math.Max(1, pts.Length - 1)] * scale;
            pts[x] = new PointF(x, mid - Math.Clamp(v, -1f, 1f) * (mid - 4));
        }
        return pts;
    }

    static void DrawGrid(Graphics g, Rectangle r)
    {
        using var grid = new Pen(Color.FromArgb(45, 120, 150, 230));
        const int step = 20;
        for (int x = step; x < r.Width; x += step) g.DrawLine(grid, x, 0, x, r.Height);
        for (int y = r.Height / 2 % step; y < r.Height; y += step) g.DrawLine(grid, 0, y, r.Width, y);
    }

    // A wide faint stroke, a medium one and a thin bright core give the neon glow.
    static void DrawGlow(Graphics g, PointF[] pts, Color c, bool thin = false)
    {
        if (pts.Length < 2) return;
        float k = thin ? 0.6f : 1f;
        using (var p = new Pen(Color.FromArgb(c.A * 18 / 255, c), 9 * k)) g.DrawLines(p, pts);
        using (var p = new Pen(Color.FromArgb(c.A * 60 / 255, c), 4.5f * k)) g.DrawLines(p, pts);
        using (var p = new Pen(Color.FromArgb(c.A, Blend(c, Color.White, 0.4f)), 1.8f * k)) g.DrawLines(p, pts);
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

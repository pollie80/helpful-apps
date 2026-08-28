using System.Drawing.Drawing2D;

namespace StutterDoctor;

/// <summary>Rolling 30-second view of frame pacing and system stalls. Each pixel column shows the
/// *worst* frame in its slice — averaging would hide the very spikes we are hunting.</summary>
public sealed class GraphPanel : Panel
{
    public double WindowSeconds = 30.0;
    public int RefreshHz = 240;

    private FrameSample[] _frames = Array.Empty<FrameSample>();
    private StallSample[] _stalls = Array.Empty<StallSample>();
    private CounterSample[] _counters = Array.Empty<CounterSample>();
    private double _now;
    private bool _haveFrames;

    public GraphPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    public void SetData(FrameSample[] frames, StallSample[] stalls, CounterSample[] counters, double now, bool haveFrames)
    {
        _frames = frames ?? Array.Empty<FrameSample>();
        _stalls = stalls ?? Array.Empty<StallSample>();
        _counters = counters ?? Array.Empty<CounterSample>();
        _now = now;
        _haveFrames = haveFrames;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Panel);

        var r = new Rectangle(46, 10, Math.Max(10, Width - 58), Math.Max(10, Height - 34));
        double t1 = _now, t0 = _now - WindowSeconds;

        double refreshMs = RefreshHz > 0 ? 1000.0 / RefreshHz : 16.67;
        double yMax = ComputeYMax(refreshMs);

        float X(double t) => (float)(r.Left + (t - t0) / WindowSeconds * r.Width);
        float Y(double ms) => (float)(r.Bottom - Math.Min(ms, yMax) / yMax * r.Height);

        using var grid = new Pen(Theme.Grid);
        using var axis = new Pen(Theme.GridStrong);
        using var font = new Font("Segoe UI", 7.5f);
        using var brushDim = new SolidBrush(Theme.TextDim);

        // horizontal grid + labels
        int steps = 5;
        for (int i = 0; i <= steps; i++)
        {
            double ms = yMax * i / steps;
            float y = Y(ms);
            g.DrawLine(grid, r.Left, y, r.Right, y);
            // Units go on the top tick only; a separate "ms" caption collides with it.
            g.DrawString(i == steps ? $"{ms:F0} ms" : $"{ms:F0}", font, brushDim, 4, y - 7);
        }
        g.DrawLine(axis, r.Left, r.Bottom, r.Right, r.Bottom);

        // reference lines: one refresh interval, and two (the V-Sync cliff)
        using (var pRef = new Pen(Theme.Good, 1) { DashStyle = DashStyle.Dash })
        using (var pRef2 = new Pen(Theme.Warn, 1) { DashStyle = DashStyle.Dash })
        {
            if (refreshMs < yMax)
            {
                g.DrawLine(pRef, r.Left, Y(refreshMs), r.Right, Y(refreshMs));
                g.DrawString($"{RefreshHz} Hz", font, new SolidBrush(Theme.Good), r.Right - 46, Y(refreshMs) - 12);
            }
            if (refreshMs * 2 < yMax)
            {
                g.DrawLine(pRef2, r.Left, Y(refreshMs * 2), r.Right, Y(refreshMs * 2));
                g.DrawString("2x (vsync miss)", font, new SolidBrush(Theme.Warn), r.Right - 84, Y(refreshMs * 2) - 12);
            }
        }

        // DPC% as a faint filled area behind everything, scaled to its own 0-20% range
        if (_counters.Length > 1)
        {
            var pts = new List<PointF>();
            foreach (var c in _counters)
            {
                if (c.T < t0 || c.T > t1) continue;
                double frac = Math.Min(1.0, c.DpcPct / 20.0);
                pts.Add(new PointF(X(c.T), (float)(r.Bottom - frac * r.Height)));
            }
            if (pts.Count > 1)
            {
                using var pen = new Pen(Color.FromArgb(70, Theme.Accent2), 1.2f);
                g.DrawLines(pen, pts.ToArray());
                g.DrawString("DPC%", font, new SolidBrush(Color.FromArgb(140, Theme.Accent2)), r.Left + 4, r.Top + 2);
            }
        }

        // frametime: max per pixel column
        if (_haveFrames && _frames.Length > 1)
        {
            int cols = Math.Max(1, r.Width);
            var colMax = new double[cols];
            var colMin = new double[cols];
            for (int i = 0; i < cols; i++) colMin[i] = double.MaxValue;

            foreach (var f in _frames)
            {
                if (f.T < t0 || f.T > t1) continue;
                int c = (int)((f.T - t0) / WindowSeconds * (cols - 1));
                if (c < 0 || c >= cols) continue;
                if (f.FrameMs > colMax[c]) colMax[c] = f.FrameMs;
                if (f.FrameMs < colMin[c]) colMin[c] = f.FrameMs;
            }

            using var penBody = new Pen(Color.FromArgb(120, Theme.Accent), 1f);
            using var penPeak = new Pen(Theme.Accent, 1.4f);
            PointF? prev = null;
            for (int i = 0; i < cols; i++)
            {
                if (colMax[i] <= 0) { prev = null; continue; }
                float x = r.Left + i;
                // vertical bar from the fastest to the slowest frame in this slice
                if (colMin[i] < double.MaxValue && colMax[i] - colMin[i] > 0.4)
                    g.DrawLine(penBody, x, Y(colMin[i]), x, Y(colMax[i]));

                var pt = new PointF(x, Y(colMax[i]));
                if (prev.HasValue) g.DrawLine(penPeak, prev.Value, pt);
                prev = pt;
            }
        }

        // stalls as red ticks along the bottom
        using (var bad = new SolidBrush(Theme.Bad))
        {
            foreach (var s in _stalls)
            {
                if (s.T < t0 || s.T > t1) continue;
                float x = X(s.T);
                float h = (float)Math.Min(r.Height, s.Ms / yMax * r.Height);
                g.FillRectangle(bad, x, r.Bottom - h, 1.6f, h);
            }
        }

        if (!_haveFrames)
        {
            using var b = new SolidBrush(Theme.TextDim);
            g.DrawString("frametime capture off — showing system stalls only (red)",
                font, b, r.Left + 6, r.Top + 14);
        }

        // time axis
        using (var b = new SolidBrush(Theme.TextDim))
        {
            g.DrawString($"-{WindowSeconds:F0}s", font, b, r.Left, r.Bottom + 4);
            g.DrawString("now", font, b, r.Right - 22, r.Bottom + 4);
        }
    }

    private double ComputeYMax(double refreshMs)
    {
        double peak = refreshMs * 3;
        double t0 = _now - WindowSeconds;
        foreach (var f in _frames) if (f.T >= t0 && f.FrameMs > peak) peak = f.FrameMs;
        foreach (var s in _stalls) if (s.T >= t0 && s.Ms > peak) peak = s.Ms;
        peak *= 1.15;
        // snap to something readable
        double[] snaps = { 10, 20, 33, 50, 100, 200, 350, 500, 1000, 2000 };
        foreach (var s in snaps) if (peak <= s) return s;
        return Math.Ceiling(peak / 1000) * 1000;
    }
}

public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x12, 0x14, 0x18);
    public static readonly Color Panel = Color.FromArgb(0x1A, 0x1E, 0x25);
    public static readonly Color PanelAlt = Color.FromArgb(0x22, 0x27, 0x30);
    public static readonly Color Grid = Color.FromArgb(0x28, 0x2E, 0x38);
    public static readonly Color GridStrong = Color.FromArgb(0x3A, 0x42, 0x50);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE9, 0xEF);
    public static readonly Color TextDim = Color.FromArgb(0x8B, 0x95, 0xA5);
    public static readonly Color Accent = Color.FromArgb(0x4E, 0xA3, 0xFF);
    public static readonly Color Accent2 = Color.FromArgb(0xB0, 0x8C, 0xFF);
    public static readonly Color Good = Color.FromArgb(0x46, 0xD1, 0x7F);
    public static readonly Color Warn = Color.FromArgb(0xFF, 0xB0, 0x20);
    public static readonly Color Bad = Color.FromArgb(0xFF, 0x5C, 0x5C);
    public static readonly Color Crit = Color.FromArgb(0xFF, 0x3B, 0x8E);

    public static Color For(Sev s) => s switch
    {
        Sev.Critical => Crit,
        Sev.High => Bad,
        Sev.Medium => Warn,
        Sev.Low => Color.FromArgb(0xD8, 0xD0, 0x70),
        Sev.Good => Good,
        _ => TextDim,
    };

    public static Color ForSeverity(string s) => s switch
    {
        "Freeze" => Crit,
        "Major" => Bad,
        "Minor" => Warn,
        "Marked" => Accent,
        _ => Text,
    };
}

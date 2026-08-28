using System.Diagnostics;

namespace StutterDoctor;

/// <summary>
/// Owns the samplers and turns raw detections into diagnosed events.
///
/// Detections are deliberately held in a pending queue for a moment before analysis: the counter
/// sweep runs at 4 Hz and nvidia-smi at 4 Hz, so analysing a stutter the instant it is detected
/// would look at a half-empty window and blame nothing.
/// </summary>
public sealed class Engine : IDisposable
{
    public readonly SessionStore Store = new();
    public readonly StallProbe Stall;
    public readonly CounterSampler Counters;
    public readonly ProcessSampler Processes;
    public readonly GpuSampler Gpu;
    public readonly PresentMonSource Frames;

    /// <summary>How long to let telemetry accumulate around a detection before diagnosing it.</summary>
    private const double SettleSeconds = 1.3;

    /// <summary>Below this, a wake-up overrun is noise rather than something you would feel.</summary>
    public double NotableStallMs = 10.0;

    /// <summary>Runs the stall probe and kernel counters only — no process table walk, no
    /// nvidia-smi. Diagnosis gets coarser, but the monitor's own footprint drops to near zero.</summary>
    public bool LowOverhead;

    public bool Running { get; private set; }
    public event Action<StutterEvent> EventDiagnosed;

    private readonly object _pendingGate = new();
    private readonly List<Pending> _pending = new();
    private int _stallCursor;
    private double _lastEventT = double.NegativeInfinity;

    private readonly struct Pending
    {
        public readonly double T, DurMs;
        public readonly StutterKind Kind;
        public Pending(double t, double durMs, StutterKind kind) { T = t; DurMs = durMs; Kind = kind; }
    }

    public Engine()
    {
        Stall = new StallProbe(Store);
        Counters = new CounterSampler(Store);
        Processes = new ProcessSampler(Store);
        Gpu = new GpuSampler(Store);
        Frames = new PresentMonSource(Store);
        Frames.Hitch += (t, f) => Enqueue(new Pending(t, f.FrameMs, StutterKind.FrameHitch));
    }

    public void Start()
    {
        if (Running) return;
        Store.Start();
        _stallCursor = 0;
        _lastEventT = double.NegativeInfinity;
        lock (_pendingGate) _pending.Clear();

        Stall.Start();
        Counters.Start();
        if (!LowOverhead)
        {
            Processes.Start();
            Gpu.Start();
        }
        else
        {
            Store.GpuStatus = "disabled (low-overhead mode)";
        }
        Frames.Start();
        Running = true;
    }

    public void Stop()
    {
        if (!Running) return;
        Frames.Dispose();
        Gpu.Dispose();
        Processes.Dispose();
        Counters.Dispose();
        Stall.Dispose();
        Running = false;
    }

    private void Enqueue(Pending p)
    {
        lock (_pendingGate) _pending.Add(p);
    }

    /// <summary>Called by the UI timer a few times a second. Cheap.</summary>
    public void Tick()
    {
        if (!Running) return;
        UpdateForegroundState();
        HarvestStalls();
        DrainPending();
    }

    /// <summary>User pressed the hotkey: "I felt one right now."</summary>
    public void MarkNow()
    {
        if (!Running) return;
        Enqueue(new Pending(Store.Now(), 0, StutterKind.UserMarked));
    }

    private bool _priorityLowered;
    private int _fgTick;

    private void UpdateForegroundState()
    {
        // In low-overhead mode the process sampler is not running, so nothing else would ever set
        // ValorantRunning — and the UI's "only while Valorant runs" filter would hide everything.
        if (LowOverhead && _fgTick++ % 4 == 0)
        {
            try
            {
                var found = Process.GetProcessesByName("VALORANT-Win64-Shipping");
                Store.ValorantRunning = found.Length > 0;
                foreach (var p in found) p.Dispose();
            }
            catch { }
        }

        bool fg = false;
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                fg = p.ProcessName.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        Store.ValorantForeground = fg;
        if (fg) Store.ValorantRunning = true;

        // While the game has focus, get out of its way. The stall probe is unaffected: it sits at
        // base priority 15 regardless of the process class.
        if (fg != _priorityLowered)
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass =
                    fg ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                _priorityLowered = fg;
            }
            catch { }
        }
    }

    /// <summary>Turns the raw stall log into events, collapsing bursts so one freeze is one row.</summary>
    private void HarvestStalls()
    {
        var all = Store.Stalls.Snapshot();
        if (all.Length <= _stallCursor) return;

        int i = _stallCursor;
        while (i < all.Length)
        {
            double clusterEnd = all[i].T;
            double worst = all[i].Ms;
            int j = i + 1;
            while (j < all.Length && all[j].T - clusterEnd < 0.20)
            {
                clusterEnd = all[j].T;
                if (all[j].Ms > worst) worst = all[j].Ms;
                j++;
            }

            // Leave an unfinished cluster for the next tick so it is not split in half.
            if (j >= all.Length && Store.Now() - clusterEnd < 0.25) break;

            if (worst >= NotableStallMs)
                Enqueue(new Pending(all[i].T, worst, StutterKind.SystemStall));

            i = j;
        }
        _stallCursor = i;
    }

    private void DrainPending()
    {
        double now = Store.Now();
        List<Pending> ready = null;

        lock (_pendingGate)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now - _pending[i].T < SettleSeconds) continue;
                (ready ??= new List<Pending>()).Add(_pending[i]);
                _pending.RemoveAt(i);
            }
        }
        if (ready == null) return;
        ready.Sort((a, b) => a.T.CompareTo(b.T));

        // A frame hitch and a system stall at the same instant are one event, described twice.
        // Keep the richer of the two.
        for (int i = 0; i < ready.Count; i++)
        {
            for (int j = ready.Count - 1; j > i; j--)
            {
                if (Math.Abs(ready[j].T - ready[i].T) > 0.25) continue;
                var keepFrame = ready[i].Kind == StutterKind.FrameHitch || ready[j].Kind == StutterKind.FrameHitch;
                var keepUser = ready[i].Kind == StutterKind.UserMarked || ready[j].Kind == StutterKind.UserMarked;
                if (!keepUser && keepFrame)
                {
                    if (ready[i].Kind != StutterKind.FrameHitch) ready[i] = ready[j];
                    ready.RemoveAt(j);
                }
            }
        }

        foreach (var p in ready)
        {
            // Never emit two events closer together than a quarter second.
            if (p.Kind != StutterKind.UserMarked && p.T - _lastEventT < 0.25) continue;
            _lastEventT = p.T;

            var e = new StutterEvent
            {
                T = p.T,
                Kind = p.Kind,
                DurationMs = p.DurMs,
                GameRunning = Store.ValorantRunning,
                Severity = Classify(p.DurMs, p.Kind),
            };

            try { Diagnoser.Analyze(Store, e, Store.RefreshHz); }
            catch (Exception ex) { e.Evidence.Add("analysis error: " + ex.Message); }

            Store.AddEvent(e);
            EventDiagnosed?.Invoke(e);
        }
    }

    private static string Classify(double ms, StutterKind kind)
    {
        if (kind == StutterKind.UserMarked) return "Marked";
        if (ms >= 100) return "Freeze";
        if (ms >= 25) return "Major";
        return "Minor";
    }

    public void Dispose() => Stop();
}

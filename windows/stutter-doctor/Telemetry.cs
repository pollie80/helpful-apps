using System.Diagnostics;

namespace StutterDoctor;

// ------------------------------------------------------------------ sample types

/// <summary>One PDH / system counter sweep. ~4 Hz.</summary>
public struct CounterSample
{
    public double T;                 // seconds since session start
    public float DpcPct;             // % DPC time (all cores)
    public float IsrPct;             // % interrupt time
    public float CpuPct;             // % processor time
    public float CpuPerfPct;         // % processor performance (>100 = boosting, <100 = downclocked)
    public float DpcsQueuedSec;
    public float InterruptsSec;
    public float ContextSwitchesSec;
    public float ProcQueueLen;
    public float AvailMB;
    public float PagesInSec;         // hard faults resolved from disk
    public float DiskQueueLen;
    public float DiskIdlePct;
    public float NetKBs;
    public float MaxCorePct;         // busiest single logical core
    public int MaxCoreIdx;
}

/// <summary>One presented frame, from PresentMon. Only populated when frametime capture is on.</summary>
public struct FrameSample
{
    public double T;
    public float FrameMs;            // wall time this frame occupied
    public float CpuBusyMs;
    public float CpuWaitMs;
    public float GpuBusyMs;
    public float GpuWaitMs;
    public float DisplayedMs;        // time on screen (0 => dropped)
    public byte PresentModeId;
    public bool Dropped;
}

public struct GpuSample
{
    public double T;
    public float UtilPct, MemUtilPct, SmClockMhz, MemClockMhz, TempC, HotspotC, PowerW;
    public uint ThrottleMask;
    public byte PState;
}

/// <summary>A system-wide responsiveness stall measured by <see cref="StallProbe"/>.</summary>
public struct StallSample
{
    public double T;                 // when the stall ended
    public float Ms;                 // how long the OS failed to run a high-priority thread
}

public struct ProcDelta
{
    public int Pid;
    public string Name;
    public float CpuPct;             // % of one core... normalised to % of whole CPU
    public float HardFaultsSec;
    public long WorkingSetMB;
}

public struct ProcSnapshot
{
    public double T;
    public ProcDelta[] Top;          // top ~12 by CPU in this interval
    public float TotalHardFaultsSec;
}

// ------------------------------------------------------------------ ring buffer

/// <summary>Fixed-capacity circular buffer. Reads are bounded: <see cref="Tail"/> and
/// <see cref="WindowByTime"/> walk backwards from newest and stop early, so a writer running at
/// 700 Hz is never blocked behind a full-buffer copy.</summary>
public sealed class Ring<T> where T : struct
{
    private readonly T[] _buf;
    private int _write;
    private int _count;
    private readonly object _gate = new();

    public Ring(int capacity) { _buf = new T[capacity]; }

    public int Capacity => _buf.Length;
    public int Count { get { lock (_gate) return _count; } }

    public void Add(in T item)
    {
        lock (_gate)
        {
            _buf[_write] = item;
            _write = (_write + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    /// <summary>Full copy, chronological. Only used when writing the report.</summary>
    public T[] Snapshot()
    {
        lock (_gate) return CopyTail(_count);
    }

    /// <summary>Newest <paramref name="n"/> entries, chronological.</summary>
    public T[] Tail(int n)
    {
        lock (_gate) return CopyTail(Math.Min(n, _count));
    }

    /// <summary>Entries whose timestamp falls in [from, to], chronological. Scans backwards and
    /// bails as soon as it passes <paramref name="from"/>, so cost is proportional to the window.</summary>
    public T[] WindowByTime(Func<T, double> time, double from, double to)
    {
        lock (_gate)
        {
            var hits = new List<T>(128);
            for (int i = 1; i <= _count; i++)
            {
                var item = _buf[(_write - i + _buf.Length) % _buf.Length];
                double t = time(item);
                if (t > to) continue;
                if (t < from) break;
                hits.Add(item);
            }
            hits.Reverse();
            return hits.ToArray();
        }
    }

    private T[] CopyTail(int n)
    {
        var outp = new T[n];
        int start = (_write - n + _buf.Length) % _buf.Length;
        for (int i = 0; i < n; i++) outp[i] = _buf[(start + i) % _buf.Length];
        return outp;
    }
}

/// <summary>Append-only log for a single writer and many readers, with no locking at all.
/// The stall probe runs at TIME_CRITICAL priority — it must never block on a reader, because
/// blocking would manufacture the very stalls it is trying to measure.</summary>
public sealed class SpscLog<T> where T : struct
{
    private readonly T[] _buf;
    private int _published;      // written last, read first
    public int Dropped { get; private set; }

    public SpscLog(int capacity) { _buf = new T[capacity]; }

    public int Count => Volatile.Read(ref _published);

    /// <summary>Writer side. Must only ever be called from one thread.</summary>
    public void Add(in T item)
    {
        int i = _published;
        if (i >= _buf.Length) { Dropped++; return; }
        _buf[i] = item;
        Volatile.Write(ref _published, i + 1);   // publish only after the payload is committed
    }

    public T[] Snapshot()
    {
        int n = Volatile.Read(ref _published);
        var outp = new T[n];
        Array.Copy(_buf, outp, n);
        return outp;
    }

    public T[] WindowByTime(Func<T, double> time, double from, double to)
    {
        int n = Volatile.Read(ref _published);
        var hits = new List<T>(32);
        for (int i = n - 1; i >= 0; i--)
        {
            double t = time(_buf[i]);
            if (t > to) continue;
            if (t < from) break;
            hits.Add(_buf[i]);
        }
        hits.Reverse();
        return hits.ToArray();
    }

    public void Clear() { Volatile.Write(ref _published, 0); Dropped = 0; }
}

// ------------------------------------------------------------------ stutter events

public enum StutterKind { FrameHitch, SystemStall, UserMarked }

public sealed class Suspect
{
    public string Name = "";
    public int Score;                // 0..100 confidence
    public string Why = "";
    public string Fix = "";
    public override string ToString() => $"{Name} ({Score})";
}

public sealed class StutterEvent
{
    public int Id;
    public double T;                 // seconds since session start
    public DateTime Wall;
    public StutterKind Kind;
    public double DurationMs;
    public string Severity = "";     // Minor / Major / Freeze
    public bool GameRunning;
    public List<Suspect> Suspects = new();
    public List<string> Evidence = new();
    public string Verdict => Suspects.Count > 0 ? Suspects[0].Name : "Unattributed";
}

// ------------------------------------------------------------------ session store

/// <summary>Central store all samplers write into and the diagnoser reads from.</summary>
public sealed class SessionStore
{
    public readonly Stopwatch Clock = new();
    public DateTime StartedWall { get; private set; }

    /// <summary>QPC value captured at session start. Stopwatch and PresentMon's --qpc_time both
    /// read QueryPerformanceCounter, so every source lands on one common timeline.</summary>
    public long StartTicks { get; private set; }

    private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    /// <summary>Every process name seen at any point while recording. The audit used to scan once at
    /// app launch, which is before the user has opened Discord, a browser, or the game itself — so
    /// the report reliably under-listed exactly the apps it was meant to catch.</summary>
    public readonly HashSet<string> ProcessesSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Converts a raw QPC value to seconds since session start.</summary>
    public double TimeOf(long qpc) => (qpc - StartTicks) * TicksToSeconds;

    /// <summary>Seconds since session start, right now.</summary>
    public double Now() => (Stopwatch.GetTimestamp() - StartTicks) * TicksToSeconds;

    public readonly Ring<CounterSample> Counters = new(60 * 60 * 4);      // 60 min @ 4 Hz
    public readonly Ring<FrameSample> Frames = new(900_000);              // ~30 min @ 500 fps
    public readonly Ring<GpuSample> Gpu = new(60 * 60 * 4);
    public readonly SpscLog<StallSample> Stalls = new(262_144);           // lock-free: probe must never block
    public readonly Ring<ProcSnapshot> Procs = new(60 * 60);              // 60 min @ 1 Hz

    public readonly List<StutterEvent> Events = new();
    private int _nextEventId = 1;

    public int RefreshHz = 60;
    public bool FrametimeCaptureActive;
    public string FrametimeStatus = "not started";
    public string GpuStatus = "not started";
    public long FramesSeen;
    public double SessionSeconds => Clock.Elapsed.TotalSeconds;

    // live-ish readouts for the UI
    public volatile bool ValorantForeground;
    public volatile bool ValorantRunning;

    /// <summary>This monitor's own CPU cost, as a share of the whole CPU. Displayed in the UI so
    /// the overhead of measuring is visible rather than assumed.</summary>
    public float SelfCpuPct;
    public float SelfPeakCpuPct;

    public void Start()
    {
        StartedWall = DateTime.Now;
        StartTicks = Stopwatch.GetTimestamp();
        Clock.Restart();
        lock (Events) { Events.Clear(); _nextEventId = 1; }
        Stalls.Clear();
        FramesSeen = 0;
    }

    public StutterEvent AddEvent(StutterEvent e)
    {
        lock (Events)
        {
            e.Id = _nextEventId++;
            e.Wall = StartedWall.AddSeconds(e.T);
            Events.Add(e);
            return e;
        }
    }

    public StutterEvent[] EventsSnapshot()
    {
        lock (Events) return Events.ToArray();
    }
}

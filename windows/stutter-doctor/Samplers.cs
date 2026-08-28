using System.Diagnostics;
using System.Net.NetworkInformation;

namespace StutterDoctor;

/// <summary>
/// Measures how long the OS fails to run a thread that is *entitled* to run right now.
///
/// A thread at TIME_CRITICAL priority asking to sleep 1 ms should come back in ~1-2 ms. If it comes
/// back in 40 ms, nothing this process did caused that — the machine as a whole was wedged: a driver
/// sitting in a DPC/ISR, an SMI, a hypervisor exit, a storage stall. That is exactly the "everything
/// freezes for a moment" symptom, and it needs no external tooling to detect.
/// </summary>
public sealed class StallProbe : IDisposable
{
    private readonly SessionStore _store;
    private Thread _thread;
    private volatile bool _run;

    /// <summary>Wake-interval histogram: &lt;2, 2-4, 4-8, 8-16, 16-33, 33-100, 100-250, 250+ ms.</summary>
    public static readonly double[] Buckets = { 2, 4, 8, 16, 33, 100, 250, double.MaxValue };
    public readonly long[] Histogram = new long[8];

    public double BaselineMs { get; private set; } = 1.5;
    public double WorstMs { get; private set; }
    public long Wakeups { get; private set; }

    /// <summary>Timer resolution the probe is actually getting. If this is not ~1 ms the stall
    /// numbers are measuring Windows' tick rate rather than anything wrong with the machine.</summary>
    public double TimerResolutionMs { get; private set; } = double.NaN;

    /// <summary>Floor for what counts as a stall. 4 ms is one whole frame at 240 Hz.</summary>
    public double FloorMs = 4.0;

    public StallProbe(SessionStore store) => _store = store;

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "StutterDoctor.StallProbe",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    private void Loop()
    {
        // Ask for a 1 ms timer so Sleep(1) really means 1 ms. Since Windows 10 2004 this is scoped
        // to our own process, so it cannot change the game's timer behaviour.
        Native.TimeBeginPeriod(1);

        // Base priority 15: above any normal-class game thread, and it survives us dropping the
        // process priority class while Valorant is in focus.
        Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_PRIORITY_TIME_CRITICAL);
        TimerResolutionMs = Native.CurrentTimerResolutionMs();

        try
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            int resCheck = 0;
            long last = Stopwatch.GetTimestamp();
            double ema = 1.5;

            while (_run)
            {
                Thread.Sleep(1);

                long now = Stopwatch.GetTimestamp();
                double ms = (now - last) * toMs;
                last = now;
                Wakeups++;

                // Re-check once a second: throttling can be applied to us at any time.
                if (++resCheck >= 1000) { resCheck = 0; TimerResolutionMs = Native.CurrentTimerResolutionMs(); }

                int b = 0;
                while (b < Buckets.Length - 1 && ms >= Buckets[b]) b++;
                Histogram[b]++;

                // Track the quiet-period baseline only from clean wakeups, so a burst of stalls
                // cannot drag the threshold up and hide the next one.
                if (ms < 6.0) ema += 0.001 * (ms - ema);

                double threshold = Math.Max(FloorMs, ema * 3.0);
                if (ms > threshold)
                {
                    if (ms > WorstMs) WorstMs = ms;
                    _store.Stalls.Add(new StallSample
                    {
                        T = (now - _store.StartTicks) * toMs / 1000.0,
                        Ms = (float)ms,
                    });
                }

                BaselineMs = ema;
            }
        }
        finally { Native.TimeEndPeriod(1); }
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(500);
        _thread = null;
    }
}

// ---------------------------------------------------------------------------------------------

/// <summary>Sweeps kernel performance counters at 4 Hz: DPC/ISR time, per-core load, hard faults,
/// disk queue, CPU clock ratio. These are the numbers that tell you *what kind* of stall it was.</summary>
public sealed class CounterSampler : IDisposable
{
    private readonly SessionStore _store;
    private Thread _thread;
    private volatile bool _run;
    public string Status { get; private set; } = "not started";

    private Native.PdhQuery _q;
    private int _dpc, _isr, _cpu, _perf, _dpcq, _ints, _ctx, _pq, _avail, _pagesIn, _diskQ, _diskIdle;
    private int[] _cores = Array.Empty<int>();

    public CounterSampler(SessionStore store) => _store = store;

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "StutterDoctor.Counters" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            _q = new Native.PdhQuery();
            const string PI = @"\Processor Information(_Total)\";
            _dpc = _q.Add(PI + "% DPC Time");
            _isr = _q.Add(PI + "% Interrupt Time");
            _cpu = _q.Add(PI + "% Processor Time");
            _perf = _q.Add(PI + "% Processor Performance", noCap100: true);
            _dpcq = _q.Add(PI + "DPCs Queued/sec");
            _ints = _q.Add(PI + "Interrupts/sec");
            _ctx = _q.Add(@"\System\Context Switches/sec");
            _pq = _q.Add(@"\System\Processor Queue Length");
            _avail = _q.Add(@"\Memory\Available MBytes");
            _pagesIn = _q.Add(@"\Memory\Pages Input/sec");
            _diskQ = _q.Add(@"\PhysicalDisk(_Total)\Avg. Disk Queue Length");
            _diskIdle = _q.Add(@"\PhysicalDisk(_Total)\% Idle Time");

            // Per-logical-core load, so we can spot a single pegged core starving the render thread.
            var cores = new List<int>();
            int logical = Environment.ProcessorCount;
            for (int g = 0; g < 4 && cores.Count < logical; g++)
                for (int c = 0; c < 64 && cores.Count < logical; c++)
                {
                    int h = _q.Add($@"\Processor Information({g},{c})\% Processor Time");
                    if (h < 0) break;
                    cores.Add(h);
                }
            _cores = cores.ToArray();

            Status = $"ok ({_cores.Length} cores)";
        }
        catch (Exception ex) { Status = "failed: " + ex.Message; return; }

        long prevBytes = ReadNetBytes();
        var sw = Stopwatch.StartNew();
        double prevNetT = 0;

        while (_run)
        {
            Thread.Sleep(250);
            if (!_q.Collect()) continue;

            long bytes = ReadNetBytes();
            double t = sw.Elapsed.TotalSeconds;
            double dt = Math.Max(0.05, t - prevNetT);
            float netKBs = (float)((bytes - prevBytes) / 1024.0 / dt);
            prevBytes = bytes; prevNetT = t;

            float maxCore = 0; int maxIdx = -1;
            for (int i = 0; i < _cores.Length; i++)
            {
                double v = _q.Read(_cores[i]);
                if (!double.IsNaN(v) && v > maxCore) { maxCore = (float)v; maxIdx = i; }
            }

            _store.Counters.Add(new CounterSample
            {
                T = _store.Now(),
                DpcPct = F(_q.Read(_dpc)),
                IsrPct = F(_q.Read(_isr)),
                CpuPct = F(_q.Read(_cpu)),
                CpuPerfPct = F(_q.Read(_perf)),
                DpcsQueuedSec = F(_q.Read(_dpcq)),
                InterruptsSec = F(_q.Read(_ints)),
                ContextSwitchesSec = F(_q.Read(_ctx)),
                ProcQueueLen = F(_q.Read(_pq)),
                AvailMB = F(_q.Read(_avail)),
                PagesInSec = F(_q.Read(_pagesIn)),
                DiskQueueLen = F(_q.Read(_diskQ)),
                DiskIdlePct = F(_q.Read(_diskIdle)),
                NetKBs = netKBs < 0 ? 0 : netKBs,
                MaxCorePct = maxCore,
                MaxCoreIdx = maxIdx,
            });
        }
    }

    private static float F(double d) => double.IsNaN(d) ? 0f : (float)d;

    private static long ReadNetBytes()
    {
        long total = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                total += s.BytesReceived + s.BytesSent;
            }
        }
        catch { }
        return total;
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(700);
        _thread = null;
        _q?.Dispose();
    }
}

// ---------------------------------------------------------------------------------------------

/// <summary>Snapshots the whole process table once a second via NtQuerySystemInformation, so a
/// background process that spikes for 300 ms can be named rather than guessed at.</summary>
public sealed class ProcessSampler : IDisposable
{
    private readonly SessionStore _store;
    private Thread _thread;
    private volatile bool _run;

    private readonly Dictionary<int, (long cpu, uint hard)> _prev = new();
    private static readonly double CoreCount = Environment.ProcessorCount;

    /// <summary>Processes we always keep in the snapshot even if idle, because their behaviour matters.</summary>
    private static readonly string[] AlwaysKeep =
    {
        "VALORANT-Win64-Shipping.exe", "RiotClientServices.exe", "vgc.exe", "vgtray.exe",
    };

    public ProcessSampler(SessionStore store) => _store = store;

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "StutterDoctor.Processes" };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        double prevT = 0;

        while (_run)
        {
            Thread.Sleep(1000);
            double t = sw.Elapsed.TotalSeconds;
            double dt = t - prevT;
            prevT = t;
            if (dt <= 0.1) continue;

            List<Native.RawProc> procs;
            try { procs = Native.SnapshotProcesses(); }
            catch { continue; }

            var deltas = new List<ProcDelta>(procs.Count);
            float totalHard = 0;
            bool valorantSeen = false;
            int selfPid = Environment.ProcessId;

            foreach (var p in procs)
            {
                if (p.Pid == 0) continue;
                long cpu = p.KernelTime + p.UserTime;

                if (_prev.TryGetValue(p.Pid, out var old))
                {
                    double busyMs = (cpu - old.cpu) / 10_000.0;              // 100 ns -> ms
                    float pct = (float)(busyMs / (dt * 1000.0 * CoreCount) * 100.0);

                    if (p.Pid == selfPid)
                    {
                        _store.SelfCpuPct = pct < 0 ? 0 : pct;
                        if (pct > _store.SelfPeakCpuPct) _store.SelfPeakCpuPct = pct;
                    }
                    float hardSec = (float)(unchecked(p.HardFaults - old.hard) / dt);
                    if (hardSec < 0 || hardSec > 1e7) hardSec = 0;           // counter reset / pid reuse
                    totalHard += hardSec;

                    if (p.Name.Equals("VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                        valorantSeen = true;

                    if (pct > 0.4f || hardSec > 5f || AlwaysKeep.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        deltas.Add(new ProcDelta
                        {
                            Pid = p.Pid,
                            Name = p.Name,
                            CpuPct = pct < 0 ? 0 : pct,
                            HardFaultsSec = hardSec,
                            WorkingSetMB = p.WorkingSet / (1024 * 1024),
                        });
                    }
                }
                _prev[p.Pid] = (cpu, p.HardFaults);
            }

            // Drop entries for processes that have exited so the dictionary cannot grow forever.
            if (_prev.Count > procs.Count * 2)
            {
                var live = procs.Select(p => p.Pid).ToHashSet();
                foreach (var dead in _prev.Keys.Where(k => !live.Contains(k)).ToList()) _prev.Remove(dead);
            }

            _store.ValorantRunning = valorantSeen;

            deltas.Sort((a, b) => b.CpuPct.CompareTo(a.CpuPct));
            _store.Procs.Add(new ProcSnapshot
            {
                T = _store.Now(),
                Top = deltas.Take(14).ToArray(),
                TotalHardFaultsSec = totalHard,
            });
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(1500);
        _thread = null;
    }
}

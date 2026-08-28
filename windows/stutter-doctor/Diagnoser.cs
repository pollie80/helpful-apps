namespace StutterDoctor;

/// <summary>
/// Takes one stutter and everything the samplers recorded around it, and works out what was
/// actually happening. Each rule that fires contributes a scored suspect with its evidence and a
/// concrete fix. If nothing correlates, it says so rather than inventing a cause.
/// </summary>
public static class Diagnoser
{
    private const double LookBehind = 1.5;   // seconds of history to inspect
    private const double LookAhead = 0.4;
    private const double BaseFrom = 25.0;    // baseline window starts this far back
    private const double BaseTo = 3.0;       // ...and ends here, clear of the event

    public static void Analyze(SessionStore store, StutterEvent e, int refreshHz)
    {
        double lo = e.T - LookBehind, hi = e.T + LookAhead;

        var win = store.Counters.WindowByTime(c => c.T, lo, hi);
        var baseline = store.Counters.WindowByTime(c => c.T, e.T - BaseFrom, e.T - BaseTo);
        var frames = store.Frames.WindowByTime(f => f.T, lo, hi);
        var gpu = store.Gpu.WindowByTime(g => g.T, lo, hi);
        var gpuBase = store.Gpu.WindowByTime(g => g.T, e.T - BaseFrom, e.T - BaseTo);
        var stalls = store.Stalls.WindowByTime(s => s.T, lo, hi);
        var procs = store.Procs.WindowByTime(p => p.T, e.T - 2.5, hi);

        var S = e.Suspects;
        var ev = e.Evidence;

        // ---------------------------------------------------------------- system-level stall
        double worstStall = stalls.Length > 0 ? stalls.Max(s => s.Ms) : 0;
        double stallTotal = stalls.Sum(s => (double)s.Ms);
        if (worstStall >= 8)
        {
            ev.Add($"OS failed to schedule a TIME_CRITICAL thread for {worstStall:F1} ms " +
                   $"({stalls.Length} stall(s), {stallTotal:F0} ms total in window)");
            int score = worstStall >= 100 ? 92 : worstStall >= 33 ? 82 : worstStall >= 16 ? 70 : 58;
            S.Add(new Suspect
            {
                Name = "System-wide stall (kernel / driver)",
                Score = score,
                Why = $"For {worstStall:F1} ms the whole machine stopped running threads. This is not the game " +
                      "and not GPU load — something in kernel mode held the CPU (a driver's DPC/ISR, an SMI, " +
                      "or a storage/hypervisor stall).",
                Fix = "Look at the next suspect for which subsystem was busy at the same moment. If nothing else " +
                      "correlates, run LatencyMon or a WPR trace to name the offending .sys file.",
            });
        }

        // ---------------------------------------------------------------- DPC / ISR
        float dpcPeak = Max(win, c => c.DpcPct), dpcBase = Median(baseline, c => c.DpcPct);
        float isrPeak = Max(win, c => c.IsrPct), isrBase = Median(baseline, c => c.IsrPct);
        float intPeak = Max(win, c => c.InterruptsSec), intBase = Median(baseline, c => c.InterruptsSec);

        if (win.Length > 0 && dpcPeak > Math.Max(dpcBase * 3f, 3.5f))
        {
            ev.Add($"DPC time peaked at {dpcPeak:F2}% (baseline {dpcBase:F2}%)");
            string hint = DriverHint(win, baseline);
            S.Add(new Suspect
            {
                Name = "Driver DPC latency spike" + (hint != null ? $" — likely {hint}" : ""),
                Score = dpcPeak > 12 ? 85 : dpcPeak > 7 ? 72 : 60,
                Why = $"Deferred procedure calls jumped to {dpcPeak:F2}% of CPU time against a {dpcBase:F2}% baseline. " +
                      "A driver ran too long at elevated IRQL and blocked everything else, including the render thread." +
                      (hint != null ? $" Traffic on the {hint} subsystem spiked at the same instant." : ""),
                Fix = hint switch
                {
                    "network" => "Update or roll back the NIC driver; disable Interrupt Moderation / Energy-Efficient Ethernet / " +
                                 "Green Ethernet in the adapter's advanced properties. Wi-Fi adapters are a frequent offender — try wired.",
                    "storage" => "Update NVMe/chipset drivers and check SSD health and free space. A drive doing background " +
                                 "garbage collection can stall the whole system.",
                    _ => "Run LatencyMon for a few minutes to name the driver. Usual suspects: audio (Realtek/Nahimic), " +
                         "Wi-Fi/Bluetooth, USB polling devices, RGB software, and virtual display adapters.",
                },
            });
        }

        if (win.Length > 0 && (isrPeak > Math.Max(isrBase * 3f, 2.0f) || intPeak > Math.Max(intBase * 2f, 60000f)))
        {
            ev.Add($"ISR time {isrPeak:F2}% (baseline {isrBase:F2}%), interrupts {intPeak:F0}/s (baseline {intBase:F0}/s)");
            S.Add(new Suspect
            {
                Name = "Interrupt storm",
                Score = 62,
                Why = $"Hardware interrupt servicing spiked to {isrPeak:F2}% with {intPeak:F0} interrupts/sec. " +
                      "A device is flooding the CPU with interrupts.",
                Fix = "Common causes: a high-polling-rate mouse combined with a saturated USB controller, a failing " +
                      "USB device, or a NIC without MSI-X. Move peripherals to different USB controllers and unplug " +
                      "anything you are not using to bisect it.",
            });
        }

        // ---------------------------------------------------------------- memory / paging
        float pagesPeak = Max(win, c => c.PagesInSec), pagesBase = Median(baseline, c => c.PagesInSec);
        float availMin = win.Length > 0 ? win.Min(c => c.AvailMB) : float.MaxValue;
        var faultingProc = procs.SelectMany(p => p.Top ?? Array.Empty<ProcDelta>())
                                .Where(p => p.HardFaultsSec > 150)
                                .OrderByDescending(p => p.HardFaultsSec).FirstOrDefault();

        if (pagesPeak > Math.Max(pagesBase * 5f, 400f) || faultingProc.HardFaultsSec > 150)
        {
            ev.Add($"Hard page faults {pagesPeak:F0}/s (baseline {pagesBase:F0}/s), {availMin:F0} MB free" +
                   (faultingProc.Name != null ? $", worst process {faultingProc.Name} @ {faultingProc.HardFaultsSec:F0}/s" : ""));
            S.Add(new Suspect
            {
                Name = "Hard page faults (paging to disk)",
                Score = availMin < 2000 ? 84 : 68,
                Why = $"The system had to fetch {pagesPeak:F0} pages/sec from disk" +
                      (availMin < 4000 ? $" with only {availMin:F0} MB of RAM free" : "") +
                      ". Execution blocks on storage latency, which shows up as a freeze." +
                      (faultingProc.Name != null ? $" {faultingProc.Name} was the biggest faulter." : ""),
                Fix = availMin < 3000
                    ? "You are running out of RAM. Close browser tabs and background apps before playing, and make sure " +
                      "the page file is system-managed rather than disabled."
                    : "Something is being paged in mid-match. Check for a background app touching lots of memory, and " +
                      "confirm the page file is system-managed on a fast drive.",
            });
        }

        // ---------------------------------------------------------------- storage
        float dqPeak = Max(win, c => c.DiskQueueLen), dqBase = Median(baseline, c => c.DiskQueueLen);
        float idleMin = win.Length > 0 ? win.Min(c => c.DiskIdlePct) : 100;
        float idleBase = Median(baseline, c => c.DiskIdlePct);
        if (dqPeak > Math.Max(dqBase * 4f, 2.5f) || (idleBase > 80 && idleMin < 25))
        {
            ev.Add($"Disk queue peaked at {dqPeak:F2} (baseline {dqBase:F2}); idle dropped to {idleMin:F0}% (baseline {idleBase:F0}%)");
            S.Add(new Suspect
            {
                Name = "Storage stall",
                Score = 64,
                Why = $"The disk queue backed up to {dqPeak:F2} outstanding requests while idle time fell to {idleMin:F0}%. " +
                      "Anything waiting on that I/O — including asset streaming and anti-cheat scans — stops.",
                Fix = "Find the writer: Windows Update, Defender scan, a game launcher patching, a cloud-sync client, " +
                      "or shader cache generation. Pause them while you play. Also confirm the SSD has >15% free.",
            });
        }

        // ---------------------------------------------------------------- CPU contention
        float coreMax = Max(win, c => c.MaxCorePct);
        float cpuPeak = Max(win, c => c.CpuPct);
        float perfMin = win.Length > 0 ? win.Where(c => c.CpuPerfPct > 0).Select(c => c.CpuPerfPct).DefaultIfEmpty(100f).Min() : 100f;
        float perfBase = Median(baseline.Where(c => c.CpuPerfPct > 0).ToArray(), c => c.CpuPerfPct);

        var noisy = procs.SelectMany(p => p.Top ?? Array.Empty<ProcDelta>())
                         .Where(p => !IsGameOrSelf(p.Name) && p.CpuPct > 7f)
                         .GroupBy(p => p.Name)
                         .Select(g => (Name: g.Key, Cpu: g.Max(x => x.CpuPct)))
                         .OrderByDescending(x => x.Cpu)
                         .FirstOrDefault();

        if (noisy.Name != null)
        {
            ev.Add($"Background process {noisy.Name} used {noisy.Cpu:F1}% of total CPU during the window");
            S.Add(new Suspect
            {
                Name = $"Background CPU spike — {noisy.Name}",
                Score = noisy.Cpu > 20 ? 74 : noisy.Cpu > 12 ? 62 : 48,
                Why = $"{noisy.Name} consumed {noisy.Cpu:F1}% of the whole CPU right as the stutter happened. " +
                      "On an 8-core part that is enough to preempt a render or audio thread.",
                Fix = $"Close {noisy.Name} before playing, or if it must run, drop its priority. Browsers, Discord " +
                      "(hardware acceleration), OBS, launchers and RGB/monitoring suites are the usual culprits.",
            });
        }

        if (coreMax >= 97 && cpuPeak < 70)
        {
            ev.Add($"One logical core saturated at {coreMax:F0}% while overall CPU was only {cpuPeak:F0}%");
            S.Add(new Suspect
            {
                Name = "Single-thread saturation",
                Score = 55,
                Why = $"A single core hit {coreMax:F0}% while the package sat at {cpuPeak:F0}%. One thread is the " +
                      "bottleneck — classic for a game's main/render thread being starved or pinned.",
                Fix = "Make sure Valorant is not restricted by an affinity mask, disable any 'CPU optimiser' utilities, " +
                      "and check that Core Isolation/Memory Integrity is off (it taxes exactly this kind of workload).",
            });
        }

        if (perfBase > 90 && perfMin < perfBase * 0.8f)
        {
            ev.Add($"CPU performance ratio dropped to {perfMin:F0}% (baseline {perfBase:F0}%)");
            S.Add(new Suspect
            {
                Name = "CPU downclock",
                Score = 58,
                Why = $"Effective CPU clock fell to {perfMin:F0}% of nominal (from {perfBase:F0}%). The chip stopped " +
                      "boosting mid-match — thermal, power or a power-plan transition.",
                Fix = "Check cooling and the VRM/PPT limits in BIOS. On a 7800X3D also verify the power plan's minimum " +
                      "processor state and that no vendor utility is forcing an eco preset.",
            });
        }

        // ---------------------------------------------------------------- GPU
        if (gpu.Length > 0)
        {
            uint mask = 0;
            foreach (var g in gpu) mask |= g.ThrottleMask;
            string throttle = GpuSampler.DescribeThrottle(mask);
            if (throttle != null)
            {
                float tMax = gpu.Max(g => g.TempC);
                ev.Add($"GPU throttle reasons active: {throttle} (peak {tMax:F0} °C)");
                S.Add(new Suspect
                {
                    Name = $"GPU throttling — {throttle}",
                    Score = 76,
                    Why = $"The GPU driver reported it was actively limiting clocks ({throttle}) at {tMax:F0} °C.",
                    Fix = throttle.Contains("thermal")
                        ? "Improve case airflow, clean the heatsink, or raise the fan curve. A 4070 SUPER should not be " +
                          "thermal throttling in Valorant at all."
                        : "The card is hitting its power limit. Check for an aggressive undervolt/overclock profile in " +
                          "MSI Afterburner and confirm PCIe power cabling.",
                });
            }

            float smMin = gpu.Min(g => g.SmClockMhz);
            float smBase = gpuBase.Length > 0 ? Median(gpuBase, g => g.SmClockMhz) : 0;
            if (smBase > 500 && smMin < smBase * 0.82f)
            {
                ev.Add($"GPU SM clock dipped to {smMin:F0} MHz (baseline {smBase:F0} MHz)");
                S.Add(new Suspect
                {
                    Name = "GPU downclock / P-state drop",
                    Score = 60,
                    Why = $"Core clock fell from ~{smBase:F0} MHz to {smMin:F0} MHz. The driver dropped the card into a " +
                          "lower performance state, usually because it thought the load had ended.",
                    Fix = "In NVIDIA Control Panel set Power Management Mode to 'Prefer maximum performance' for Valorant. " +
                          "Very high frame rates on a light game can otherwise cause the driver to oscillate P-states.",
                });
            }
        }

        // ---------------------------------------------------------------- per-frame attribution
        if (frames.Length > 0)
        {
            var worst = frames.OrderByDescending(f => f.FrameMs).First();
            ev.Add($"Worst frame in window: {worst.FrameMs:F1} ms (CPU busy {worst.CpuBusyMs:F1}, GPU busy {worst.GpuBusyMs:F1}, " +
                   $"displayed {worst.DisplayedMs:F1}, mode id {worst.PresentModeId})");

            if (worst.GpuBusyMs > 0 && worst.GpuBusyMs >= worst.FrameMs * 0.75f)
            {
                S.Add(new Suspect
                {
                    Name = "GPU-bound frame",
                    Score = 70,
                    Why = $"The GPU was busy for {worst.GpuBusyMs:F1} ms of a {worst.FrameMs:F1} ms frame — the card " +
                          "genuinely could not finish in time.",
                    Fix = "At 1440p on a 4070 SUPER this should not happen in Valorant unless something else is using the " +
                          "GPU. Check for a browser with hardware acceleration, a stream/recording encoder, or another " +
                          "app rendering on the same card.",
                });
            }
            else if (worst.CpuBusyMs > 0 && worst.CpuBusyMs >= worst.FrameMs * 0.75f)
            {
                S.Add(new Suspect
                {
                    Name = "CPU-bound frame",
                    Score = 68,
                    Why = $"The CPU spent {worst.CpuBusyMs:F1} ms building a {worst.FrameMs:F1} ms frame while the GPU waited.",
                    Fix = "Look at the background-process suspects above; also consider Memory Integrity (HVCI), which adds " +
                          "measurable CPU-side overhead to every game.",
                });
            }

            int dropped = frames.Count(f => f.Dropped);
            if (dropped > 0)
            {
                ev.Add($"{dropped} frame(s) rendered but never shown");
                S.Add(new Suspect
                {
                    Name = "Dropped presents",
                    Score = 55,
                    Why = $"{dropped} frame(s) were rendered and then thrown away without ever reaching the screen. " +
                          "Work was wasted and the image on screen froze.",
                    Fix = "Almost always a present-path problem: V-Sync fighting the frame rate, or the compositor taking " +
                          "over from direct scanout. See the V-Sync and present-mode findings.",
                });
            }

            var composed = frames.FirstOrDefault(f => !PresentMonSource.IsDirectScanout(f.PresentModeId) && f.PresentModeId != 0);
            if (composed.PresentModeId != 0)
            {
                S.Add(new Suspect
                {
                    Name = "Composed present (DWM in the path)",
                    Score = 66,
                    Why = "The game was not scanning out directly to the display; the desktop compositor was copying its " +
                          "frames. That adds a frame of latency and makes the game vulnerable to anything else on the desktop.",
                    Fix = "Switch Valorant to true Fullscreen (not Windowed Fullscreen), close overlays, and avoid moving " +
                          "windows or playing video on the other monitors while in a match.",
                });
            }

            // V-Sync miss: the frame lands on an exact multiple of the refresh interval.
            // Only meaningful when scanout is actually locked to the refresh grid — under VRR the
            // panel refreshes whenever the frame is ready, so "about 2 refresh intervals long" is
            // just an ordinary slow frame and firing on it buries the real causes.
            if (refreshHz > 0 && ScanoutIsQuantised(store, 1000.0 / refreshHz, e.T))
            {
                double refreshMs = 1000.0 / refreshHz;
                double ratio = worst.FrameMs / refreshMs;
                int k = (int)Math.Round(ratio);
                if (k >= 2 && k <= 8 && Math.Abs(ratio - k) * refreshMs < 0.25 && worst.FrameMs < 250)
                {
                    ev.Add($"Frame time {worst.FrameMs:F2} ms = {k}x the {refreshMs:F2} ms refresh interval");
                    S.Add(new Suspect
                    {
                        Name = $"V-Sync frame miss (dropped to 1/{k} refresh rate)",
                        Score = 88,
                        Why = $"The stutter is exactly {k} refresh intervals long ({worst.FrameMs:F2} ms at {refreshHz} Hz). " +
                              "That is the signature of V-Sync: miss the scanout deadline by a microsecond and the frame is " +
                              $"held for a whole extra refresh, so frame rate halves instantly to {refreshHz / k} fps.",
                        Fix = "Turn V-Sync OFF in Valorant, and cap the frame rate instead (Max FPS Always ≈ refresh − 3). " +
                              "If you want tearing-free output, use G-Sync/FreeSync with V-Sync on in the driver, " +
                              "off in the game, plus a cap below refresh.",
                    });
                }
            }
        }

        // ---------------------------------------------------------------- nothing found
        if (S.Count == 0)
        {
            S.Add(new Suspect
            {
                Name = "Unattributed",
                Score = 0,
                Why = "None of the monitored subsystems moved in the window around this stutter. Either it was shorter " +
                      "than the 250 ms counter interval, or it happened somewhere this tool cannot see — inside the " +
                      "GPU driver, in anti-cheat, or below the OS (SMI / firmware).",
                Fix = "If most of your stutters land here, capture a WPR/xperf trace, or enable frametime capture so " +
                      "per-frame CPU/GPU attribution is available.",
            });
        }

        S.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (S.Count > 5) S.RemoveRange(5, S.Count - 5);
    }

    /// <summary>Was the DPC spike accompanied by network or disk traffic? Weak evidence on its own,
    /// but it usually points at the right driver family.</summary>
    private static string DriverHint(CounterSample[] win, CounterSample[] baseline)
    {
        float netPeak = Max(win, c => c.NetKBs), netBase = Median(baseline, c => c.NetKBs);
        float dqPeak = Max(win, c => c.DiskQueueLen), dqBase = Median(baseline, c => c.DiskQueueLen);
        bool net = netPeak > Math.Max(netBase * 3f, 400f);
        bool disk = dqPeak > Math.Max(dqBase * 4f, 2f);
        if (net && !disk) return "network";
        if (disk && !net) return "storage";
        return null;
    }

    private static bool IsGameOrSelf(string name) =>
        name != null && (name.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("StutterDoctor.exe", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("System", StringComparison.OrdinalIgnoreCase));

    private static float Max<T>(T[] a, Func<T, float> sel) => a.Length == 0 ? 0f : a.Max(sel);

    private static float Median<T>(T[] a, Func<T, float> sel)
    {
        if (a.Length == 0) return 0f;
        var v = a.Select(sel).OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2f;
    }

    // ------------------------------------------------------------------ session roll-up

    public sealed class CauseTally
    {
        public string Name = "";
        public int Count;
        public int BestScore;
        public double WorstMs;
        public string Why = "";
        public string Fix = "";
    }

    /// <summary>Aggregates every analysed stutter into a ranked list of root causes — the thing
    /// you actually read after a session.</summary>
    /// <summary>
    /// Drops frames the game rendered while deliberately running slow. Valorant has separate caps
    /// for the background and for menus — both 60 fps by default — so alt-tabbing or sitting in
    /// the lobby fills the capture with 16.7 ms frames that are working exactly as configured.
    /// Left in, they dominate the 1% and 0.1% lows and pile onto one frame duration convincingly
    /// enough to look like a periodic blocker. Detected from the frametimes themselves rather than
    /// from the config, so a cap at any value is caught, and so is a stretch the config does not
    /// explain.
    /// </summary>
    public static FrameSample[] GameplayFrames(FrameSample[] frames)
    {
        if (frames.Length < 60) return frames;

        var sorted = frames.Select(f => f.FrameMs).ToArray();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        if (median <= 0) return frames;

        // A real hitch is one or two frames long. A cap holds for as long as the window is in the
        // background, so judge each frame by its neighbourhood, not by itself.
        const int Half = 7;
        double ceiling = median * 2.5;
        var keep = new List<FrameSample>(frames.Length);
        var scratch = new double[2 * Half + 1];

        for (int i = 0; i < frames.Length; i++)
        {
            int a = Math.Max(0, i - Half), b = Math.Min(frames.Length - 1, i + Half);
            int n = 0;
            for (int j = a; j <= b; j++) scratch[n++] = frames[j].FrameMs;
            Array.Sort(scratch, 0, n);
            if (scratch[n / 2] <= ceiling) keep.Add(frames[i]);
        }

        // If almost everything looks capped the baseline itself is suspect; report it all rather
        // than silently throwing the session away.
        return keep.Count < frames.Length / 4 ? frames : keep.ToArray();
    }

    /// <summary>A recurring frame delay whose length is always the same, which means something
    /// running at a fixed rate is holding the game's frames back.</summary>
    public sealed class PeriodicBlocker
    {
        public double PeriodMs;
        public double Hz;
        public int Count;
        public double ShareOfLateFrames;   // 0..1, how much of the late-frame mass sits in the spike
        public double Concentration;       // times denser than a flat distribution would be
        public string Match;               // the display or subsystem running at this rate, if known
    }

    /// <summary>
    /// Looks for one delay length that keeps recurring. Random hitches spread themselves out; a
    /// fixed-rate blocker piles every one of its victims onto the same millisecond, because the
    /// frame always waits for that source's next tick. Finding the spike gives its frequency, and
    /// the frequency usually names the culprit outright.
    /// </summary>
    public static PeriodicBlocker FindPeriodicBlocker(SessionStore store)
    {
        const double Lo = 6.0, Hi = 100.0, Bin = 0.05;
        int nb = (int)((Hi - Lo) / Bin);
        var hist = new int[nb];
        int total = 0;

        foreach (var f in GameplayFrames(store.Frames.Tail(400_000)))
        {
            if (f.FrameMs < Lo || f.FrameMs >= Hi) continue;
            hist[(int)((f.FrameMs - Lo) / Bin)]++;
            total++;
        }
        if (total < 400) return null;

        // Skip the first and last bins: the low edge is just the tail of normal frames bleeding
        // in, and calling that a spike would flag every healthy session.
        int peak = 1;
        for (int i = 2; i < nb - 1; i++) if (hist[i] > hist[peak]) peak = i;
        if (peak <= (int)(0.5 / Bin)) return null;
        double peakMs = Lo + (peak + 0.5) * Bin;

        // Everything within ±0.20 ms of the peak counts as the same spike: a real one has that
        // much natural width from scheduling jitter.
        int band = (int)(0.20 / Bin);
        int inBand = 0;
        for (int i = Math.Max(0, peak - band); i <= Math.Min(nb - 1, peak + band); i++) inBand += hist[i];

        double share = (double)inBand / total;
        double flat = (double)(2 * band + 1) / nb;      // share a uniform spread would put here
        double concentration = share / flat;

        // 6x denser than flat is far beyond what an ordinary long tail produces, and 150 frames
        // keeps a handful of coincidences from being announced as a finding.
        if (inBand < 150 || concentration < 6.0) return null;

        return new PeriodicBlocker
        {
            PeriodMs = peakMs,
            Hz = 1000.0 / peakMs,
            Count = inBand,
            ShareOfLateFrames = share,
            Concentration = concentration,
            Match = NameTheRate(1000.0 / peakMs, store),
        };
    }

    /// <summary>Ties a measured frequency back to something on the machine that runs at that rate.</summary>
    private static string NameTheRate(double hz, SessionStore store)
    {
        var hits = new List<string>();

        try
        {
            foreach (var d in Native.GetDisplays())
            {
                if (d.Hz <= 0 || d.Primary) continue;
                if (Math.Abs(d.Hz - hz) <= 1.5)
                    hits.Add($"secondary display {d.Width}x{d.Height} @ {d.Hz} Hz");
            }
        }
        catch { /* display enumeration is best-effort here */ }

        if (store.RefreshHz > 0)
        {
            double k = store.RefreshHz / hz;
            if (k >= 1.5 && Math.Abs(k - Math.Round(k)) < 0.05)
                hits.Add($"1/{Math.Round(k):F0} of the {store.RefreshHz} Hz primary");
        }

        if (Math.Abs(hz - 60) <= 1.5) hits.Add("a 60 Hz subsystem (Game DVR capture, an overlay, or DWM)");

        return hits.Count == 0 ? null : string.Join("; or ", hits);
    }

    private static double _quantCheckedAt = double.NegativeInfinity;
    private static bool _quantResult;

    /// <summary>
    /// True when the display is scanning out on a fixed refresh grid rather than tracking the
    /// game. Under fixed-refresh V-Sync every displayed frame lasts a whole number of refresh
    /// intervals; under an active VRR link the displayed time follows the present interval and
    /// the distribution is continuous. Recomputed at most every few seconds — VRR can drop out
    /// mid-session, so this is not a one-shot answer.
    /// </summary>
    public static bool ScanoutIsQuantised(SessionStore store, double refreshMs, double now)
    {
        if (now - _quantCheckedAt < 5.0) return _quantResult;
        _quantCheckedAt = now;

        int onGrid = 0, total = 0;
        foreach (var f in store.Frames.Tail(4000))
        {
            if (f.DisplayedMs <= 0.5f || f.DisplayedMs > 60f) continue;
            total++;
            double k = f.DisplayedMs / refreshMs;
            if (Math.Abs(k - Math.Round(k)) * refreshMs < 0.10) onGrid++;
        }

        // Continuous timings land inside a ±0.10 ms window by chance about 5% of the time; a
        // panel genuinely locked to the grid lands there nearly always. 60% is comfortably clear
        // of both, so neither a noisy VRR link nor an occasional dropped frame flips the answer.
        _quantResult = total >= 500 && onGrid >= total * 0.60;
        return _quantResult;
    }

    /// <summary>Fraction of displayed frames sitting on an exact refresh boundary, for the report.</summary>
    public static (double OnGridFraction, int Sampled) ScanoutGridFit(SessionStore store, double refreshMs)
    {
        int onGrid = 0, total = 0;
        foreach (var f in store.Frames.Tail(200_000))
        {
            if (f.DisplayedMs <= 0.5f || f.DisplayedMs > 60f) continue;
            total++;
            double k = f.DisplayedMs / refreshMs;
            if (Math.Abs(k - Math.Round(k)) * refreshMs < 0.10) onGrid++;
        }
        return (total == 0 ? 0 : (double)onGrid / total, total);
    }

    public static List<CauseTally> Summarise(IEnumerable<StutterEvent> events)
    {
        var map = new Dictionary<string, CauseTally>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (e.Suspects.Count == 0) continue;
            var top = e.Suspects[0];
            // Collapse "Background CPU spike — chrome.exe" style names onto one bucket per process.
            var key = top.Name;
            if (!map.TryGetValue(key, out var t))
                map[key] = t = new CauseTally { Name = key, Why = top.Why, Fix = top.Fix };
            t.Count++;
            t.BestScore = Math.Max(t.BestScore, top.Score);
            t.WorstMs = Math.Max(t.WorstMs, e.DurationMs);
        }
        return map.Values
                  .OrderByDescending(t => t.Count * 1000 + t.BestScore)
                  .ToList();
    }
}

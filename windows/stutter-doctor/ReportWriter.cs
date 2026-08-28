using System.Globalization;
using System.Text;

namespace StutterDoctor;

/// <summary>Writes a session out as a readable markdown report plus raw CSVs.</summary>
public static class ReportWriter
{
    public static string Write(Engine eng, List<Finding> audit)
    {
        var store = eng.Store;
        string dir = Path.Combine(AppContext.BaseDirectory, "reports",
            "session-" + store.StartedWall.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        var events = store.EventsSnapshot();
        var frames = store.Frames.Snapshot();
        var counters = store.Counters.Snapshot();
        var gpu = store.Gpu.Snapshot();
        var stalls = store.Stalls.Snapshot();

        WriteCsvs(dir, events, frames, counters, gpu, stalls);
        File.WriteAllText(Path.Combine(dir, "report.md"),
            BuildMarkdown(eng, audit, events, frames, stalls), Utf8Bom);

        return dir;
    }

    /// <summary>With a BOM: without one, Windows PowerShell and Excel decode these as ANSI and
    /// every em dash and degree sign turns to mojibake.</summary>
    private static readonly UTF8Encoding Utf8Bom = new(true);

    // ---------------------------------------------------------------- markdown

    private static string BuildMarkdown(Engine eng, List<Finding> audit, StutterEvent[] events,
                                        FrameSample[] frames, StallSample[] stalls)
    {
        var store = eng.Store;
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine("# Stutter Doctor — session report");
        sb.AppendLine();
        sb.AppendLine($"- **Started:** {store.StartedWall:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Duration:** {TimeSpan.FromSeconds(store.SessionSeconds):hh\\:mm\\:ss}");
        sb.AppendLine($"- **Frametime capture:** {store.FrametimeStatus}");
        sb.AppendLine($"- **GPU telemetry:** {store.GpuStatus}");
        sb.AppendLine($"- **Stutter events recorded:** {events.Length}");
        sb.AppendLine();

        sb.AppendLine("## Machine");
        sb.AppendLine();
        foreach (var line in MachineSummary()) sb.AppendLine($"- {line}");
        sb.AppendLine();

        // ------------------------------------------------ verdict
        sb.AppendLine("## Verdict");
        sb.AppendLine();

        var blocker = Diagnoser.FindPeriodicBlocker(store);
        if (blocker != null)
        {
            sb.AppendLine($"### Periodic blocker at {blocker.Hz:F2} Hz");
            sb.AppendLine();
            sb.AppendLine($"**{blocker.Count:N0} slow frames all took almost exactly {blocker.PeriodMs:F2} ms** — " +
                          $"{blocker.ShareOfLateFrames * 100:F0}% of every slow frame this session, " +
                          $"{blocker.Concentration:F0}x denser than chance would put there.");
            sb.AppendLine();
            sb.AppendLine("Random stutters scatter across a range of durations. When they instead pile onto one exact " +
                          "value, the frame was not slow — it was *waiting*, and it waited for the next tick of " +
                          $"something running at {blocker.Hz:F2} Hz. That is a far stronger signal than anything in the " +
                          "table below, because it names a rate, and the rate names the culprit.");
            sb.AppendLine();
            if (blocker.Match != null)
            {
                sb.AppendLine($"**Running at that rate on this machine:** {blocker.Match}.");
                sb.AppendLine();
            }
            sb.AppendLine("**Fix:** Take the candidates away one at a time and re-record — the spike either survives " +
                          "or it does not, and that is a clean answer in one session each. Start with whichever is " +
                          "cheapest to undo.");
            sb.AppendLine();
        }

        var inGame = events.Where(e => e.GameRunning).ToArray();
        var pool = inGame.Length > 0 ? inGame : events;

        if (pool.Length == 0)
        {
            sb.AppendLine("No stutters were detected during this session. Either the problem did not reproduce, or it is");
            sb.AppendLine("shorter than the detection floor. Try a longer session and press the mark hotkey when you feel one.");
            sb.AppendLine();
        }
        else
        {
            var causes = Diagnoser.Summarise(pool);
            sb.AppendLine($"Ranked by how often each cause was the leading explanation across {pool.Length} stutters" +
                          (inGame.Length > 0 ? " recorded while Valorant was running." : "."));
            sb.AppendLine();
            sb.AppendLine("| # | Cause | Times | Confidence | Worst |");
            sb.AppendLine("|---|-------|-------|-----------|-------|");
            for (int i = 0; i < causes.Count; i++)
            {
                var c = causes[i];
                sb.AppendLine($"| {i + 1} | {Escape(c.Name)} | {c.Count} | {c.BestScore}% | {c.WorstMs:F0} ms |");
            }
            sb.AppendLine();
            foreach (var c in causes.Take(4))
            {
                sb.AppendLine($"### {Escape(c.Name)}");
                sb.AppendLine();
                sb.AppendLine(c.Why);
                sb.AppendLine();
                sb.AppendLine($"**Fix:** {c.Fix}");
                sb.AppendLine();
            }
        }

        // ------------------------------------------------ frame stats
        sb.AppendLine("## Frame pacing");
        sb.AppendLine();
        if (frames.Length > 200)
        {
            // Menu and alt-tab frames run at their own reduced cap and would otherwise set the
            // 1% and 0.1% lows all by themselves, hiding how the game actually plays.
            var play = Diagnoser.GameplayFrames(frames);
            int excluded = frames.Length - play.Length;

            var ms = play.Select(f => (double)f.FrameMs).OrderBy(x => x).ToArray();
            double P(double q) => ms[Math.Clamp((int)(q * (ms.Length - 1)), 0, ms.Length - 1)];
            double mean = ms.Average();

            sb.AppendLine($"- Frames captured: **{frames.Length:N0}**" +
                          (excluded > 0
                              ? $" — {excluded:N0} ({excluded * 100.0 / frames.Length:F1}%) excluded below as menu / "
                                + "background frames running at a reduced cap"
                              : ""));
            sb.AppendLine($"- Average: **{1000.0 / mean:F0} fps** ({mean:F2} ms)");
            sb.AppendLine($"- Median (p50): {P(0.50):F2} ms");
            sb.AppendLine($"- p95: {P(0.95):F2} ms");
            sb.AppendLine($"- **1% low: {1000.0 / P(0.99):F0} fps** ({P(0.99):F2} ms)");
            sb.AppendLine($"- **0.1% low: {1000.0 / P(0.999):F0} fps** ({P(0.999):F2} ms)");
            sb.AppendLine($"- Worst single frame: **{ms[^1]:F1} ms**");
            int dropped = play.Count(f => f.Dropped);
            sb.AppendLine($"- Frames rendered but never displayed: {dropped:N0} ({dropped * 100.0 / play.Length:F2}%)");

            var modes = play.GroupBy(f => f.PresentModeId)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{ModeName(g.Key)} {g.Count() * 100.0 / play.Length:F1}%");
            sb.AppendLine($"- Present modes seen: {string.Join(", ", modes)}");

            if (store.RefreshHz > 0)
            {
                var (onGrid, sampled) = Diagnoser.ScanoutGridFit(store, 1000.0 / store.RefreshHz);
                if (sampled >= 500)
                {
                    bool locked = onGrid >= 0.60;
                    sb.AppendLine($"- Scanout locked to the {store.RefreshHz} Hz refresh grid: " +
                                  $"**{onGrid * 100:F1}%** of displayed frames ({sampled:N0} sampled) — " +
                                  (locked
                                      ? "**fixed refresh**. VRR is not driving the panel, so every missed deadline "
                                        + "costs a whole refresh interval."
                                      : "**variable refresh is live**. The panel is tracking the game rather than a "
                                        + "fixed clock, so slow frames are not being rounded up to whole refreshes."));
                }
            }

            sb.AppendLine();
            sb.AppendLine("> A wide gap between the average and the 1% / 0.1% lows is the numeric form of \"it feels");
            sb.AppendLine("> stuttery even though the fps counter looks fine\".");
        }
        else
        {
            sb.AppendLine("No frametime data was captured this session — run as Administrator with Valorant open to enable it.");
        }
        sb.AppendLine();

        // ------------------------------------------------ responsiveness
        sb.AppendLine("## System responsiveness (stall probe)");
        sb.AppendLine();
        sb.AppendLine($"A thread at TIME_CRITICAL priority asked to sleep 1 ms, {eng.Stall.Wakeups:N0} times. " +
                      "Anything much over 2 ms means the OS could not run *anything* — not a game problem, a system problem.");
        sb.AppendLine();
        sb.AppendLine("| Wake interval | Count | Share |");
        sb.AppendLine("|---------------|-------|-------|");
        string[] labels = { "< 2 ms (healthy)", "2–4 ms", "4–8 ms", "8–16 ms", "16–33 ms", "33–100 ms", "100–250 ms", "> 250 ms" };
        long total = Math.Max(1, eng.Stall.Wakeups);
        for (int i = 0; i < labels.Length; i++)
        {
            long c = eng.Stall.Histogram[i];
            if (c == 0 && i > 3) continue;
            sb.AppendLine($"| {labels[i]} | {c:N0} | {c * 100.0 / total:F3}% |");
        }
        sb.AppendLine();
        sb.AppendLine($"- Baseline wake latency: **{eng.Stall.BaselineMs:F2} ms**");
        sb.AppendLine($"- Worst stall: **{eng.Stall.WorstMs:F1} ms**");
        sb.AppendLine($"- Stalls over threshold: **{stalls.Length:N0}**");
        sb.AppendLine($"- Timer resolution in effect: **{eng.Stall.TimerResolutionMs:F2} ms**");
        if (eng.Stall.TimerResolutionMs > 2.0)
        {
            sb.AppendLine();
            sb.AppendLine("> **These stall numbers are not trustworthy.** The timer resolution should be ~1 ms. A value");
            sb.AppendLine("> near 15.6 ms means Windows throttled this process and every \"stall\" above is just the OS");
            sb.AppendLine("> tick rate, not a fault on your machine. Ignore the stall section of this report.");
        }
        if (store.Stalls.Dropped > 0)
            sb.AppendLine($"- (log full: {store.Stalls.Dropped:N0} further stalls not individually recorded)");
        sb.AppendLine();

        // ------------------------------------------------ audit
        sb.AppendLine("## Configuration checkup");
        sb.AppendLine();
        foreach (var group in audit.GroupBy(x => x.Severity).OrderByDescending(g => g.Key))
        {
            foreach (var x in group)
            {
                sb.AppendLine($"### [{x.Severity.ToString().ToUpperInvariant()}] {Escape(x.Title)}");
                sb.AppendLine();
                sb.AppendLine(x.Detail);
                if (!string.IsNullOrWhiteSpace(x.Fix))
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Fix:** {x.Fix}");
                }
                sb.AppendLine();
            }
        }

        // ------------------------------------------------ event list
        sb.AppendLine("## Every stutter recorded");
        sb.AppendLine();
        if (events.Length == 0) sb.AppendLine("_none_");
        else
        {
            sb.AppendLine("| # | Time | Kind | Duration | Severity | Leading suspect | Conf |");
            sb.AppendLine("|---|------|------|----------|----------|-----------------|------|");
            foreach (var e in events.Take(400))
            {
                var top = e.Suspects.FirstOrDefault();
                sb.AppendLine($"| {e.Id} | {e.T.ToString("F2", inv)}s | {e.Kind} | {e.DurationMs.ToString("F1", inv)} ms | " +
                              $"{e.Severity} | {Escape(top?.Name ?? "-")} | {top?.Score ?? 0}% |");
            }
            if (events.Length > 400) sb.AppendLine($"\n_({events.Length - 400} more in stutters.csv)_");
        }
        sb.AppendLine();

        // ------------------------------------------------ next steps
        sb.AppendLine("## Suggested bisect order");
        sb.AppendLine();
        sb.AppendLine("Change **one thing at a time** and record a fresh session after each, so the numbers above stay");
        sb.AppendLine("comparable. Compare the 0.1% low and the stall histogram between runs — those move first.");
        sb.AppendLine();
        int step = 1;
        foreach (var a in audit.Where(x => x.Severity >= Sev.Medium && !string.IsNullOrWhiteSpace(x.Fix)))
            sb.AppendLine($"{step++}. **{Escape(a.Title)}** — {a.Fix}");
        if (step == 1) sb.AppendLine("_No configuration problems were found; the cause is likely dynamic. See the verdict above._");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Raw data: `stutters.csv`, `counters.csv`, `gpu.csv`, `stalls.csv`, `frames_around_stutters.csv`.");

        return sb.ToString();
    }

    private static string ModeName(byte id) => id switch
    {
        1 => "Hardware legacy flip",
        2 => "Hardware legacy copy",
        3 => "Hardware: Independent Flip",
        4 => "Composed: Flip",
        5 => "Hardware Composed: Independent Flip",
        6 => "Composed: Copy (GPU GDI)",
        7 => "Composed: Copy (CPU GDI)",
        _ => "Unknown",
    };

    private static string Escape(string s) => s?.Replace("|", "\\|") ?? "";

    public static IEnumerable<string> MachineSummary()
    {
        yield return "CPU: " + (Native.ReadRegString(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim()
            ?? "unknown") + $" ({Environment.ProcessorCount} logical)";

        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m))
            yield return $"RAM: {m.ullTotalPhys / (1024.0 * 1024 * 1024):F0} GB ({m.dwMemoryLoad}% in use at report time)";

        foreach (var d in Native.GetDisplays())
            yield return $"Display: {d.Width}x{d.Height} @ {d.Hz} Hz — {d.MonitorName} on {d.Adapter}" + (d.Primary ? " (primary)" : "");

        yield return $"OS: {Environment.OSVersion.VersionString}";
    }

    // ---------------------------------------------------------------- csv

    private static void WriteCsvs(string dir, StutterEvent[] events, FrameSample[] frames,
                                  CounterSample[] counters, GpuSample[] gpu, StallSample[] stalls)
    {
        var inv = CultureInfo.InvariantCulture;

        using (var w = new StreamWriter(Path.Combine(dir, "stutters.csv"), false, Utf8Bom))
        {
            w.WriteLine("id,time_s,wall_clock,kind,duration_ms,severity,game_running,suspect_1,score_1,suspect_2,score_2,evidence");
            foreach (var e in events)
            {
                var s1 = e.Suspects.ElementAtOrDefault(0);
                var s2 = e.Suspects.ElementAtOrDefault(1);
                w.WriteLine($"{e.Id},{e.T.ToString("F4", inv)},{e.Wall:HH:mm:ss.fff},{e.Kind}," +
                            $"{e.DurationMs.ToString("F2", inv)},{e.Severity},{e.GameRunning}," +
                            $"{Q(s1?.Name)},{s1?.Score ?? 0},{Q(s2?.Name)},{s2?.Score ?? 0}," +
                            $"{Q(string.Join(" | ", e.Evidence))}");
            }
        }

        using (var w = new StreamWriter(Path.Combine(dir, "counters.csv"), false, Utf8Bom))
        {
            w.WriteLine("time_s,cpu_pct,dpc_pct,isr_pct,cpu_perf_pct,dpcs_queued_s,interrupts_s,ctx_switches_s," +
                        "proc_queue,avail_mb,pages_in_s,disk_queue,disk_idle_pct,net_kbs,max_core_pct,max_core_idx");
            foreach (var c in counters)
                w.WriteLine(string.Join(",", new[]
                {
                    c.T.ToString("F3", inv), N(c.CpuPct), N(c.DpcPct), N(c.IsrPct), N(c.CpuPerfPct),
                    N(c.DpcsQueuedSec), N(c.InterruptsSec), N(c.ContextSwitchesSec), N(c.ProcQueueLen),
                    N(c.AvailMB), N(c.PagesInSec), N(c.DiskQueueLen), N(c.DiskIdlePct), N(c.NetKBs),
                    N(c.MaxCorePct), c.MaxCoreIdx.ToString(inv),
                }));
        }

        using (var w = new StreamWriter(Path.Combine(dir, "gpu.csv"), false, Utf8Bom))
        {
            w.WriteLine("time_s,util_pct,mem_util_pct,sm_mhz,mem_mhz,temp_c,power_w,pstate,throttle_mask,throttle_reasons");
            foreach (var g in gpu)
                w.WriteLine($"{g.T.ToString("F3", inv)},{N(g.UtilPct)},{N(g.MemUtilPct)},{N(g.SmClockMhz)}," +
                            $"{N(g.MemClockMhz)},{N(g.TempC)},{N(g.PowerW)},P{g.PState},0x{g.ThrottleMask:X}," +
                            $"{Q(GpuSampler.DescribeThrottle(g.ThrottleMask))}");
        }

        using (var w = new StreamWriter(Path.Combine(dir, "stalls.csv"), false, Utf8Bom))
        {
            w.WriteLine("time_s,stall_ms");
            foreach (var s in stalls) w.WriteLine($"{s.T.ToString("F4", inv)},{N(s.Ms)}");
        }

        // Full frametime logs run to hundreds of megabytes; the useful part is what surrounds each
        // stutter, so only those windows are exported.
        if (frames.Length > 0 && events.Length > 0)
        {
            using var w = new StreamWriter(Path.Combine(dir, "frames_around_stutters.csv"), false, Utf8Bom);
            w.WriteLine("event_id,time_s,frame_ms,cpu_busy_ms,cpu_wait_ms,gpu_busy_ms,gpu_wait_ms,displayed_ms,present_mode_id,dropped");
            foreach (var e in events)
            {
                foreach (var f in frames.Where(f => f.T >= e.T - 2.0 && f.T <= e.T + 1.0))
                    w.WriteLine($"{e.Id},{f.T.ToString("F5", inv)},{N(f.FrameMs)},{N(f.CpuBusyMs)},{N(f.CpuWaitMs)}," +
                                $"{N(f.GpuBusyMs)},{N(f.GpuWaitMs)},{N(f.DisplayedMs)},{f.PresentModeId},{(f.Dropped ? 1 : 0)}");
            }
        }
    }

    private static string N(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string Q(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

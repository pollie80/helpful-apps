using System.Diagnostics;
using System.Globalization;

namespace StutterDoctor;

/// <summary>Streams GPU clocks, temperature, power and — crucially — the driver's own throttle
/// reason bitmask out of nvidia-smi. If the GPU downclocks mid-match, this says why.</summary>
public sealed class GpuSampler : IDisposable
{
    private readonly SessionStore _store;
    private Process _proc;
    private volatile bool _run;
    public string Status { get; private set; } = "not started";
    public bool Available { get; private set; }

    // nvidia-smi renamed this field in recent drivers; try the new name first.
    private static readonly string[] QueryCandidates =
    {
        "utilization.gpu,utilization.memory,clocks.current.sm,clocks.current.memory,temperature.gpu,power.draw,pstate,clocks_event_reasons.active",
        "utilization.gpu,utilization.memory,clocks.current.sm,clocks.current.memory,temperature.gpu,power.draw,pstate,clocks_throttle_reasons.active",
        "utilization.gpu,utilization.memory,clocks.current.sm,clocks.current.memory,temperature.gpu,power.draw,pstate",
    };

    public GpuSampler(SessionStore store) => _store = store;

    /// <summary>Probing nvidia-smi costs a few hundred milliseconds per candidate query, so it runs
    /// off the caller's thread — otherwise pressing Start would visibly freeze the window.</summary>
    public void Start()
    {
        if (_proc != null || _run) return;
        _run = true;
        Status = "starting…";
        _store.GpuStatus = Status;
        new Thread(StartWorker) { IsBackground = true, Name = "StutterDoctor.GpuStart" }.Start();
    }

    private void StartWorker()
    {
        string chosen = null;
        foreach (var q in QueryCandidates)
        {
            if (!_run) return;
            if (Probe(q)) { chosen = q; break; }
        }
        if (chosen == null)
        {
            Status = "nvidia-smi unavailable — GPU clock/throttle data disabled";
            _store.GpuStatus = Status;
            return;
        }

        bool hasReasons = chosen.Contains("reasons");
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = $"--query-gpu={chosen} --format=csv,noheader,nounits -lms 250",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) ParseLine(e.Data, hasReasons); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            Available = true;
            Status = hasReasons ? "streaming (with throttle reasons)" : "streaming (no throttle reasons)";
        }
        catch (Exception ex) { Status = "failed: " + ex.Message; }
        _store.GpuStatus = Status;
    }

    private static bool Probe(string query)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = $"--query-gpu={query} --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            return p.HasExited && p.ExitCode == 0 && outp.Contains(',');
        }
        catch { return false; }
    }

    private void ParseLine(string line, bool hasReasons)
    {
        if (!_run) return;
        var f = line.Split(',');
        if (f.Length < 7) return;
        try
        {
            var s = new GpuSample
            {
                T = _store.Now(),
                UtilPct = P(f[0]),
                MemUtilPct = P(f[1]),
                SmClockMhz = P(f[2]),
                MemClockMhz = P(f[3]),
                TempC = P(f[4]),
                PowerW = P(f[5]),
                PState = ParsePState(f[6]),
                ThrottleMask = hasReasons && f.Length > 7 ? ParseHex(f[7]) : 0u,
            };
            _store.Gpu.Add(s);
        }
        catch { }
    }

    private static float P(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static byte ParsePState(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == 'P' || s[0] == 'p') && byte.TryParse(s[1..], out var n)) return n;
        return 255;
    }

    private static uint ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? (uint)v : 0u;
    }

    public void Dispose()
    {
        _run = false;
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
        Available = false;
    }

    /// <summary>Decodes the NVML throttle bitmask into something a human can act on.
    /// GpuIdle and ApplicationsClocksSetting are deliberately ignored — they are not faults.</summary>
    public static string DescribeThrottle(uint mask)
    {
        if (mask == 0) return null;
        var parts = new List<string>();
        if ((mask & 0x004) != 0) parts.Add("SW power cap");
        if ((mask & 0x008) != 0) parts.Add("HW slowdown");
        if ((mask & 0x010) != 0) parts.Add("sync boost");
        if ((mask & 0x020) != 0) parts.Add("SW thermal");
        if ((mask & 0x040) != 0) parts.Add("HW thermal");
        if ((mask & 0x080) != 0) parts.Add("HW power brake");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}

// ---------------------------------------------------------------------------------------------

/// <summary>
/// Consumes real per-frame timings from PresentMon. This is the difference between "something
/// stuttered" and "the GPU took 31 ms on this frame while the CPU sat waiting".
///
/// PresentMon reads ETW — it does not attach to, inject into, or read the memory of the game.
/// </summary>
public sealed class PresentMonSource : IDisposable
{
    private readonly SessionStore _store;
    private Process _proc;
    private volatile bool _run;
    private bool _useQpc = true;

    public string Status { get; private set; } = "not started";
    public bool Running => _proc is { HasExited: false };
    public string ExePath { get; private set; }
    public string CurrentPresentMode { get; private set; } = "-";

    /// <summary>Raised for each frame whose wall time is a clear outlier against its neighbours.</summary>
    public event Action<double, FrameSample> Hitch;

    private const string TargetExe = "VALORANT-Win64-Shipping.exe";

    // resolved column indices
    private int _cTime = -1, _cFrame = -1, _cCpuBusy = -1, _cCpuWait = -1,
                _cGpuBusy = -1, _cGpuWait = -1, _cDisplayed = -1, _cDropped = -1, _cMode = -1;
    private bool _haveHeader;

    private double _emaFrameMs;
    private int _framesSinceStart;

    /// <summary>Consecutive frames well above baseline. Past <see cref="SustainedRun"/> this is a
    /// different steady state rather than a stutter.</summary>
    private int _elevatedRun;
    private const int SustainedRun = 10;

    public PresentMonSource(SessionStore store) => _store = store;

    /// <summary>NVIDIA's FrameView SDK ships a PresentMon_x64.exe that cannot run standalone: it
    /// parses its command line, then exits 1 with no output because it expects to be driven by
    /// FvSDK_x64.dll and the FrameViewKMD driver. Detect it by path so the failure can be explained
    /// rather than misreported as a permissions problem.</summary>
    public static bool IsFrameViewSdkBuild(string path) =>
        path != null && path.Contains("FrameViewSDK", StringComparison.OrdinalIgnoreCase);

    public static string Locate()
    {
        var candidates = new List<string>
        {
            // A standalone Intel/Intel-Google PresentMon dropped next to this app always wins.
            Path.Combine(AppContext.BaseDirectory, "PresentMon.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon_x64.exe"),
            Path.Combine(AppContext.BaseDirectory, "presentmon", "PresentMon.exe"),
            @"C:\Program Files\NVIDIA Corporation\FrameView\PresentMon_x64.exe",
            // Last resort: the SDK build, which is expected to fail. Kept so the app can name it.
            @"C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe",
            @"C:\Program Files (x86)\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;

        // anything named PresentMon* on PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir.Trim(), "PresentMon*.exe"))
                    return f;
            }
            catch { }
        }
        return null;
    }

    public void Start()
    {
        if (_proc != null) return;
        ExePath = Locate();
        if (ExePath == null)
        {
            Status = "PresentMon not found — frametime capture off (stall probe still active)";
            _store.FrametimeStatus = Status;
            return;
        }
        if (!Native.IsElevated())
        {
            Status = "needs Administrator (ETW) — click 'Restart as Admin' for frametime capture";
            _store.FrametimeStatus = Status;
            return;
        }

        _run = true;
        Status = "starting PresentMon…";
        _store.FrametimeStatus = Status;

        // Each rejected argument set costs a 2.5 s probe. Four of them on the UI thread would be a
        // ten-second freeze in the app whose whole job is finding freezes.
        new Thread(StartWorker) { IsBackground = true, Name = "StutterDoctor.PresentMonStart" }.Start();
    }

    private void StartWorker()
    {
        // --v2_metrics is what actually matters here: without it PresentMon 2.x emits the 1.x
        // column set, which has no CPUBusy/CPUWait/GPUBusy/GPUWait at all. Those four are the
        // whole basis for deciding whether a slow frame was the CPU's fault or the GPU's, so a
        // capture without them looks like it worked while quietly answering "unattributed" every
        // time. Sets below it drop flags one at a time for older builds that reject them.
        string[][] argSets =
        {
            new[] { "--process_name", TargetExe, "--output_stdout", "--no_console_stats", "--v2_metrics", "--qpc_time", "--stop_existing_session", "--session_name", "StutterDoctor" },
            new[] { "--process_name", TargetExe, "--output_stdout", "--no_console_stats", "--v2_metrics", "--qpc_time", "--stop_existing_session" },
            new[] { "--process_name", TargetExe, "--output_stdout", "--no_console_stats", "--v2_metrics", "--qpc_time" },
            new[] { "--process_name", TargetExe, "--output_stdout", "--no_console_stats", "--qpc_time", "--stop_existing_session", "--session_name", "StutterDoctor" },
            new[] { "--process_name", TargetExe, "--output_stdout", "--no_console_stats", "--qpc_time" },
            new[] { "--process_name", TargetExe, "--output_stdout", "--no_console_stats" },
        };

        foreach (var set in argSets)
        {
            if (!_run) return;
            _useQpc = set.Contains("--qpc_time");
            if (TryLaunch(set))
            {
                Status = _useQpc ? "capturing (QPC-aligned)" : "capturing (approximate timeline)";
                _store.FrametimeCaptureActive = true;
                _store.FrametimeStatus = Status;
                return;
            }
        }

        // PresentMon prints nothing at all when it fails, so there is no error text to surface.
        Status = IsFrameViewSdkBuild(ExePath)
            ? "unavailable — the only PresentMon here is NVIDIA's FrameView SDK build, which cannot run " +
              "standalone. Drop a standalone PresentMon.exe next to StutterDoctor.exe to enable frametimes."
            : "PresentMon could not open an ETW session (exit 1). Close other capture tools " +
              "(FrameView, CapFrameX, OCAT, GeForce Experience overlay) and try again.";
        _store.FrametimeStatus = Status;
    }

    private readonly List<string> _stderr = new();
    public IReadOnlyList<string> StdErr => _stderr;

    private bool TryLaunch(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            if (p == null) return false;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderr) { if (_stderr.Count < 200) _stderr.Add(e.Data); }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // A bad argument makes PresentMon exit almost immediately; a good one blocks waiting
            // for the game, which is exactly what we want.
            if (p.WaitForExit(2500)) { p.Dispose(); return false; }

            _proc = p;
            return true;
        }
        catch { return false; }
    }

    private static string Norm(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }

    private bool _timeModeKnown;
    private bool _timeIsQpc;
    private double _timeOffset;

    /// <summary>
    /// Timestamp for one frame. The time column is a QPC tick count when --qpc_time is honoured,
    /// but builds differ on whether it is written as an integer or a decimal, and without the flag
    /// it is seconds since the trace began. Handle all three: falling back to arrival time stamps
    /// every frame in a read batch with the same instant, which silently destroys the alignment
    /// between frames and everything else on the timeline.
    /// </summary>
    private double ResolveFrameTime(string[] f)
    {
        if (_cTime < 0 || _cTime >= f.Length) return _store.Now();

        var raw = f[_cTime].Trim();
        if (raw.Length == 0 ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
            double.IsNaN(v) || v <= 0)
            return _store.Now();

        if (!_timeModeKnown)
        {
            // A QPC tick count is enormous even a fraction of a second in; trace-relative
            // seconds start near zero. One sample is enough to tell them apart.
            _timeIsQpc = v > 1_000_000;
            if (!_timeIsQpc) _timeOffset = _store.Now() - v;
            _timeModeKnown = true;
            Status = _timeIsQpc ? "capturing (QPC-aligned)" : "capturing (trace-clock aligned)";
        }

        return _timeIsQpc ? _store.TimeOf((long)v) : v + _timeOffset;
    }

    private void OnLine(string line)
    {
        if (!_run || line.Length == 0) return;

        // PresentMon emits its header once up front, and again if the schema changes.
        if (line.StartsWith("Application", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ProcessID", StringComparison.OrdinalIgnoreCase) && !_haveHeader)
        {
            ResolveColumns(line.Split(','));
            return;
        }
        if (!_haveHeader) return;

        var f = line.Split(',');
        if (f.Length <= _cFrame || _cFrame < 0) return;

        double frameMs = D(f, _cFrame);
        if (frameMs <= 0 || frameMs > 20000) return;

        double t = ResolveFrameTime(f);

        var s = new FrameSample
        {
            T = t,
            FrameMs = (float)frameMs,
            CpuBusyMs = (float)D(f, _cCpuBusy),
            CpuWaitMs = (float)D(f, _cCpuWait),
            GpuBusyMs = (float)D(f, _cGpuBusy),
            GpuWaitMs = (float)D(f, _cGpuWait),
            DisplayedMs = (float)D(f, _cDisplayed),
            Dropped = _cDropped >= 0 && _cDropped < f.Length &&
                      (f[_cDropped].Trim() is "1" or "true" or "True"),
            PresentModeId = _cMode >= 0 && _cMode < f.Length ? PresentModeId(f[_cMode]) : (byte)0,
        };

        if (_cMode >= 0 && _cMode < f.Length)
        {
            var m = f[_cMode].Trim();
            if (m.Length > 0 && m != CurrentPresentMode) CurrentPresentMode = m;
        }

        _store.Frames.Add(s);
        Interlocked.Increment(ref _store.FramesSeen);

        // Online outlier detection against a slow-moving baseline of recent frames.
        if (_framesSinceStart < 120) { _framesSinceStart++; _emaFrameMs = _emaFrameMs <= 0 ? frameMs : _emaFrameMs + 0.05 * (frameMs - _emaFrameMs); return; }

        if (frameMs < _emaFrameMs * 1.8)
        {
            _emaFrameMs += 0.02 * (frameMs - _emaFrameMs);
            _elevatedRun = 0;
        }
        else if (++_elevatedRun >= SustainedRun)
        {
            // Frame times that stay high are not a stutter — the game has changed gear, which is
            // what Valorant's separate menu and background caps do. Adopt the new level as the
            // baseline instead of reporting every frame of it as a fresh hitch.
            _emaFrameMs += 0.10 * (frameMs - _emaFrameMs);
        }

        double trigger = Math.Max(_emaFrameMs * 2.0, _emaFrameMs + 4.0);
        if (frameMs > trigger && _elevatedRun < SustainedRun) Hitch?.Invoke(t, s);
    }

    private void ResolveColumns(string[] header)
    {
        int Find(params string[] aliases)
        {
            for (int i = 0; i < header.Length; i++)
            {
                var h = Norm(header[i]);
                foreach (var a in aliases) if (h == a) return i;
            }
            return -1;
        }

        _cTime = Find("cpustarttime", "cpustartqpctime", "timeinseconds", "cpustarttimeinseconds");
        _cFrame = Find("frametime", "msbetweenpresents");
        _cCpuBusy = Find("cpubusy", "msinpresentapi");
        _cCpuWait = Find("cpuwait");
        _cGpuBusy = Find("gpubusy", "msgpuactive");
        _cGpuWait = Find("gpuwait");
        _cDisplayed = Find("displayedtime", "msbetweendisplaychange", "msuntildisplayed");
        _cDropped = Find("dropped");
        _cMode = Find("presentmode");
        _haveHeader = _cFrame >= 0;

        // MsInPresentAPI is only the time inside the Present() call, not the CPU cost of the
        // frame — treating it as CPUBusy makes every frame look CPU-cheap. Say so rather than
        // reporting attribution built on a column that does not mean what the label says.
        bool haveV2 = _cGpuBusy >= 0 && _cCpuWait >= 0;
        HasCpuGpuAttribution = haveV2;
        if (_haveHeader && !haveV2)
        {
            AttributionNote = "PresentMon returned 1.x metrics, which carry no CPUBusy/CPUWait/GPUBusy/GPUWait. " +
                              "Frame timings and present modes are accurate, but no stutter can be attributed to " +
                              "the CPU or the GPU this session.";
            _store.FrametimeStatus = Status + " — no CPU/GPU attribution (1.x metrics)";
        }
    }

    /// <summary>False when the capture lacks the columns that CPU-vs-GPU attribution depends on.</summary>
    public bool HasCpuGpuAttribution { get; private set; } = true;

    public string AttributionNote { get; private set; }

    private static double D(string[] f, int i)
    {
        if (i < 0 || i >= f.Length) return 0;
        var s = f[i].Trim();
        if (s.Length == 0 || s == "NA") return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Independent Flip means the game scans out directly. Anything "Composed" means DWM
    /// is in the path — a common and very fixable source of hitching.</summary>
    public static byte PresentModeId(string mode)
    {
        var m = Norm(mode);
        if (m.Contains("hardwarecomposedindependentflip")) return 5;
        if (m.Contains("hardwareindependentflip") || m.Contains("independentflip")) return 3;
        if (m.Contains("hardwarelegacyflip")) return 1;
        if (m.Contains("hardwarelegacycopytofrontbuffer")) return 2;
        if (m.Contains("composedflip")) return 4;
        if (m.Contains("composedcopywithgpugdi")) return 6;
        if (m.Contains("composedcopywithcpugdi")) return 7;
        return 0;
    }

    public static bool IsDirectScanout(byte id) => id is 3 or 5 or 1 or 2;

    public void Dispose()
    {
        _run = false;
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
    }
}

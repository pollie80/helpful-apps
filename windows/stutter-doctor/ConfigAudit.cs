using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StutterDoctor;

public enum Sev { Good = 0, Info = 1, Low = 2, Medium = 3, High = 4, Critical = 5 }

public sealed class Finding
{
    public Sev Severity;
    public string Title = "";
    public string Detail = "";
    public string Fix = "";
    public string Area = "";
}

/// <summary>
/// A one-shot inspection of everything that is known to cause hitching in Valorant specifically.
/// Runs in well under a second and needs no session — it is the "before you play" checklist.
/// </summary>
public static class ConfigAudit
{
    /// <summary>Apps that habitually cost frames: overlays, capture, RGB suites, monitoring tools.
    /// Each carries its own remedy — "just close it" is wrong advice for some of these.</summary>
    private static readonly (string Proc, string Label, Sev Sev, string Note, string Fix)[] KnownOffenders =
    {
        ("obs64",             "OBS Studio",              Sev.Medium, "capturing/encoding on the same GPU",
            "Close OBS while testing. If you need to record, use NVENC and cap the game's frame rate so the encoder has headroom."),
        ("obs32",             "OBS Studio",              Sev.Medium, "capturing/encoding on the same GPU",
            "Close OBS while testing."),
        ("Discord",           "Discord",                 Sev.Low,    "hardware acceleration and overlay both cost frames",
            "Discord → Settings → Advanced → turn off Hardware Acceleration, and Game Overlay → off. No need to quit it."),
        ("chrome",            "Chrome",                  Sev.Low,    "GPU-accelerated tabs share the card with the game",
            "Close video/animation-heavy tabs before playing — a background YouTube tab alone can cost frames."),
        ("msedge",            "Edge",                    Sev.Low,    "GPU-accelerated tabs share the card with the game",
            "Close video/animation-heavy tabs before playing — a background YouTube tab alone can cost frames."),
        ("firefox",           "Firefox",                 Sev.Low,    "GPU-accelerated tabs share the card with the game",
            "Close video/animation-heavy tabs before playing."),
        ("iCUE",              "Corsair iCUE",            Sev.Medium, "RGB suites are a classic source of DPC spikes",
            "Exit iCUE completely (tray icon → Quit) and play a session. Lighting settings persist in device memory."),
        ("LightingService",   "Armoury Crate / Aura",    Sev.Medium, "RGB suites are a classic source of DPC spikes",
            "Stop the Aura/Armoury Crate services and play a session. These are among the worst offenders for DPC latency."),
        ("MSIAfterburner",    "MSI Afterburner",         Sev.Low,    "polling + RTSS overlay hook the present path",
            "Raise the polling interval to 1000 ms, or close it while testing."),
        ("RTSS",              "RivaTuner Statistics",    Sev.Medium, "injects into the present path; can conflict with Vanguard",
            "Close RTSS while testing. Its overlay hooks the same present path this tool is measuring."),
        ("NahimicService",    "Nahimic audio",           Sev.High,   "well-documented cause of audio-driver DPC latency",
            "Disable the Nahimic service (services.msc → Nahimic service → Stop + Disable). This is a repeat offender for exactly your symptom."),
        ("nahimicSvc32",      "Nahimic audio",           Sev.High,   "well-documented cause of audio-driver DPC latency",
            "Disable the Nahimic service in services.msc."),
        ("nahimicSvc64",      "Nahimic audio",           Sev.High,   "well-documented cause of audio-driver DPC latency",
            "Disable the Nahimic service in services.msc."),
        ("MsMpEng",           "Defender real-time scan", Sev.Low,    "bursty disk + CPU during matches",
            "Do not turn Defender off. Instead add an exclusion for the Valorant install folder and Riot Client: " +
            "Windows Security → Virus & threat protection → Manage settings → Exclusions → Add folder."),
        ("SearchIndexer",     "Windows Search indexer",  Sev.Low,    "bursty disk during matches",
            "Exclude the game drive from indexing, or leave it — it usually idles during play."),
        ("parsecd",           "Parsec",                  Sev.Medium, "virtual display + capture pipeline stays active",
            "Quit Parsec from the tray while playing, and see the separate virtual display adapter finding."),
        ("OneDrive",          "OneDrive sync",           Sev.Low,    "background file I/O",
            "Pause syncing for an hour before a session (tray icon → Pause syncing)."),
        ("Dropbox",           "Dropbox sync",            Sev.Low,    "background file I/O",
            "Pause syncing before a session."),
        ("EpicGamesLauncher", "Epic Games Launcher",     Sev.Low,    "background updater",
            "Quit it from the tray while playing."),
        ("Steam",             "Steam",                   Sev.Low,    "background downloads/shader pre-caching",
            "Make sure no downloads or shader pre-caching are running mid-match."),
        ("WallpaperEngine",   "Wallpaper Engine",        Sev.Medium, "renders continuously on the same GPU",
            "Set it to pause on fullscreen applications, or quit it while playing."),
        ("wallpaper64",       "Wallpaper Engine",        Sev.Medium, "renders continuously on the same GPU",
            "Set it to pause on fullscreen applications."),
    };

    public static List<Finding> Run(SessionStore store)
    {
        var f = new List<Finding>();
        var displays = Native.GetDisplays();
        var val = ValorantSettings.Load();

        Displays(f, displays, val, store);
        ValorantConfig(f, val, displays, store);
        WindowsPlatform(f);
        GpuAndDrivers(f, displays);
        Storage(f);
        Memory(f);
        RunningApps(f, store);

        f.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return f;
    }

    // ---------------------------------------------------------------- displays

    private static void Displays(List<Finding> f, List<Native.DisplayInfo> d, ValorantSettings v, SessionStore store)
    {
        if (d.Count == 0) return;

        var primary = d.FirstOrDefault(x => x.Primary) ?? d[0];
        store.RefreshHz = primary.Hz > 0 ? primary.Hz : 60;

        // If Valorant targets a specific monitor, that one's refresh is what matters.
        if (!string.IsNullOrEmpty(v.MonitorDeviceId))
        {
            var key = ExtractMonitorKey(v.MonitorDeviceId);
            var match = d.FirstOrDefault(x => key != null && x.MonitorId != null &&
                                              x.MonitorId.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (match != null) store.RefreshHz = match.Hz;
        }

        var rates = d.Select(x => x.Hz).Distinct().OrderByDescending(x => x).ToList();
        string list = string.Join(", ", d.Select(x => $"{x.Width}x{x.Height}@{x.Hz}Hz{(x.Primary ? " (primary)" : "")}"));

        if (d.Count > 1 && rates.Count > 1)
        {
            f.Add(new Finding
            {
                Area = "Display",
                Severity = Sev.High,
                Title = $"{d.Count} monitors running at {rates.Count} different refresh rates",
                Detail = $"{list}. Windows drives all outputs from one compositor clock. When refresh rates do not " +
                         "divide evenly, DWM periodically has to resynchronise, and the highest-refresh display gets a " +
                         "hitch. This is one of the most common causes of random sub-second freezes in Valorant on a " +
                         "multi-monitor setup, and it matches your symptom exactly.",
                Fix = $"Test it first: disable the {string.Join("/", d.Where(x => x.Hz != rates[0]).Select(x => x.Hz).Distinct())} Hz " +
                      "monitors in Windows display settings (you do not need to unplug anything) and play a few rounds. " +
                      "If the freezes stop, you have your answer. The permanent fix is to set every monitor to a rate that " +
                      $"divides evenly into {rates[0]} Hz — {rates[0] / 2} or {rates[0] / 4} Hz exactly. Note that a monitor " +
                      $"reporting {rates.Last()} Hz is really running 59.94 Hz, which does NOT divide into {rates[0]}; " +
                      $"force it to exactly {rates[0] / 4} Hz in the NVIDIA Control Panel (Change resolution → Refresh rate), " +
                      "or leave the secondaries disabled while you play.",
            });
        }
        else if (d.Count > 1)
        {
            f.Add(new Finding
            {
                Area = "Display",
                Severity = Sev.Info,
                Title = $"{d.Count} monitors, all at {rates[0]} Hz",
                Detail = list + ". Matched refresh rates — this is the good configuration.",
                Fix = "Nothing to do.",
            });
        }
    }

    private static string ExtractMonitorKey(string deviceId)
    {
        // "MONITOR\\SAM7778\\{4d36e96e-...}\\0002" -> "SAM7778"
        var m = Regex.Match(deviceId, @"MONITOR\\+([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------------------------------------------------------------- Valorant

    /// <summary>What the present path actually did this session, rather than what it ought to do.
    /// A "composition episode" is a contiguous run of frames that were not scanned out directly;
    /// in exclusive fullscreen those are mode switches, and they are expensive.</summary>
    private readonly record struct PresentPath(
        int Sampled, double DirectFraction, int Episodes, double WorstMs, double TotalCostMs)
    {
        public bool Measured => Sampled >= 20_000;
    }

    private static PresentPath MeasurePresentPath(SessionStore store)
    {
        var frames = store.Frames.Tail(400_000);
        if (frames.Length == 0) return default;

        int direct = 0, episodes = 0;
        double worst = 0, cost = 0, lastOffT = double.NegativeInfinity;

        foreach (var fr in frames)
        {
            if (PresentMonSource.IsDirectScanout(fr.PresentModeId)) { direct++; continue; }
            // frames more than a second apart belong to separate episodes
            if (fr.T - lastOffT > 1.0) episodes++;
            lastOffT = fr.T;
            cost += fr.FrameMs;
            if (fr.FrameMs > worst) worst = fr.FrameMs;
        }

        return new PresentPath(frames.Length, (double)direct / frames.Length, episodes, worst, cost);
    }

    private static void ValorantConfig(List<Finding> f, ValorantSettings v, List<Native.DisplayInfo> d, SessionStore store)
    {
        var path = MeasurePresentPath(store);

        if (!v.Found)
        {
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Info,
                Title = "Valorant settings file not found",
                Detail = "Could not read GameUserSettings.ini, so in-game graphics settings were not checked.",
                Fix = "Launch Valorant once, then re-run the checkup.",
            });
            return;
        }

        int hz = d.FirstOrDefault(x => x.Primary)?.Hz ?? 60;

        if (v.VSync && v.FrameRateLimit <= 0)
        {
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Critical,
                Title = "V-Sync is ON with no frame rate cap",
                Detail = $"bUseVSync=True and FrameRateLimit=0 in {v.Path}. This is the single most likely explanation " +
                         $"for your symptom. Your rig can push far more than {hz} fps in Valorant, so the game is being " +
                         "held at the scanout deadline with zero headroom. The instant a frame is even fractionally late — " +
                         "a background thread, a DPC, anything — V-Sync holds it for a whole extra refresh and the frame " +
                         $"rate halves to {hz / 2} fps for that moment. That reads as a random sub-second freeze, and it " +
                         "will feel worse than the numbers suggest because your mouse input stalls with it.",
                Fix = "In Valorant → Settings → Video → General: set V-Sync OFF, and set 'Limit FPS Always' ON with " +
                      $"Max FPS ≈ {Math.Max(60, hz - 3)}. If your monitor supports G-Sync, instead enable G-Sync in the " +
                      "NVIDIA Control Panel, leave V-Sync ON in the driver, OFF in the game, and keep the in-game cap " +
                      $"at {Math.Max(60, hz - 3)}.",
            });
        }
        else if (v.VSync)
        {
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Medium,
                Title = "V-Sync is ON",
                Detail = $"bUseVSync=True with a {v.FrameRateLimit:F0} fps cap. V-Sync still couples your frame pacing to " +
                         "the scanout deadline; a missed deadline costs a full refresh interval.",
                Fix = $"Turn V-Sync off and rely on the frame cap, or use G-Sync/FreeSync with a cap below {hz} fps.",
            });
        }
        else if (v.FrameRateLimit <= 0)
        {
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Medium,
                Title = "No frame rate cap",
                Detail = "V-Sync is off and no cap is set, so this rig will run several hundred fps above the display's " +
                         $"{hz} Hz. If the monitor has Adaptive Sync / VRR enabled, exceeding the refresh rate pushes the " +
                         "frame rate out of the VRR window and G-Sync simply stops working — you get tearing and uneven " +
                         "pacing despite VRR being 'on'. Uncapped rates also make the GPU oscillate between power states.",
                Fix = $"Set 'Limit FPS Always' ON with Max FPS ≈ {Math.Max(60, hz - 3)}.",
            });
        }
        else
        {
            // V-Sync off with a cap: the configuration we want. Whether it is *correct* depends on
            // where the cap sits relative to refresh, which is what keeps VRR engaged.
            bool below = v.FrameRateLimit <= hz - 2;
            f.Add(new Finding
            {
                Area = "Valorant",
                Severity = below ? Sev.Good : Sev.Medium,
                Title = below
                    ? $"V-Sync off with a {v.FrameRateLimit:F0} fps cap on a {hz} Hz display — correct"
                    : $"Cap of {v.FrameRateLimit:F0} fps is not safely below the {hz} Hz refresh rate",
                Detail = below
                    ? $"Read from {v.FrameRateLimitSource}. This is the right shape for a VRR display: the cap sits " +
                      $"{hz - v.FrameRateLimit:F0} fps under refresh, which keeps every frame inside the Adaptive Sync " +
                      "window so the panel matches the game rather than the other way round."
                    : $"Read from {v.FrameRateLimitSource}. A cap at or above the refresh rate lets frames reach the top " +
                      "of the VRR range, where Adaptive Sync hands back over to fixed-refresh behaviour and pacing gets " +
                      "uneven.",
                Fix = below
                    ? "Leave it. If the monitor has VRR, confirm G-Sync Compatible is actually enabled in the NVIDIA " +
                      "Control Panel — enabling Adaptive Sync on the monitor's own OSD does not enable it driver-side."
                    : $"Lower Max FPS Always to about {Math.Max(60, hz - 3)}.",
            });
        }

        if (v.FullscreenMode == 1)
        {
            // Borderless is only a problem if independent flip is measurably being lost. Assuming it
            // is — which this check used to do — invents a high-severity finding on a clean system.
            if (path.Measured && path.DirectFraction >= 0.99)
            {
                f.Add(new Finding
                {
                    Area = "Valorant", Severity = Sev.Good,
                    Title = "Windowed Fullscreen is getting a direct scanout",
                    Detail = $"FullscreenMode=1, and {path.DirectFraction * 100:F2}% of {path.Sampled:N0} captured frames " +
                             "went straight to the display without the desktop compositor in the path. Borderless is " +
                             "costing you nothing here.",
                    Fix = "Leave it. Borderless also avoids the multi-second mode switches that exclusive fullscreen pays " +
                          "on every alt-tab or overlay.",
                });
            }
            else if (path.Measured)
            {
                f.Add(new Finding
                {
                    Area = "Valorant", Severity = Sev.High,
                    Title = "Windowed Fullscreen is losing its independent flip",
                    Detail = $"FullscreenMode=1 in {v.Path}. Only {path.DirectFraction * 100:F1}% of {path.Sampled:N0} " +
                             $"captured frames scanned out directly; the rest went through DWM across {path.Episodes} " +
                             "episode(s). Composition adds a frame of latency and its own pacing, which is exactly the " +
                             "hitching you are chasing.",
                    Fix = "Valorant → Settings → Video → General → Display Mode: Fullscreen. Alt-tabbing gets slower, but " +
                          "the present path stops depending on what the rest of the desktop is doing.",
                });
            }
        }
        else if (v.FullscreenMode == 0 && path.Measured && path.Episodes > 0 && path.WorstMs >= 1000)
        {
            // The mirror case, and the one that actually shows up on this machine: exclusive fullscreen
            // hands the display back to DWM on every alt-tab or overlay, and each handover is a freeze.
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Medium,
                Title = "Exclusive fullscreen is paying for display mode switches",
                Detail = $"FullscreenMode=0, and the present path left direct scanout {path.Episodes} time(s) this " +
                         $"session. Those handovers cost {path.TotalCostMs / 1000.0:F1} s in total, the worst single " +
                         $"one {path.WorstMs / 1000.0:F1} s. Exclusive fullscreen owns the display outright, so anything " +
                         "that needs the desktop back — an alt-tab, an overlay, a notification — forces a full mode " +
                         "switch out and back. Borderless has no exclusive mode to surrender, so these stop happening.",
                Fix = "Worth testing: Valorant → Settings → Video → General → Display Mode: Windowed Fullscreen. Set " +
                      "NVIDIA Control Panel → Manage 3D Settings → Monitor Technology to G-SYNC Compatible and Set up " +
                      "G-SYNC → 'Enable for windowed and full screen mode', or you lose VRR. Then record one session " +
                      "and check this same line: if direct scanout stays above 99%, keep it.",
            });
        }
        else if (v.FullscreenMode == 2)
        {
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Medium,
                Title = "Running in Windowed mode",
                Detail = "FullscreenMode=2. Always composited by DWM — highest latency and most stutter-prone.",
                Fix = "Switch Display Mode to Fullscreen.",
            });
        }

        if (v.Letterbox)
        {
            f.Add(new Finding
            {
                Area = "Valorant", Severity = Sev.Info,
                Title = "Letterboxing is enabled",
                Detail = "bShouldLetterbox=True. Harmless if your render resolution matches the display's aspect ratio, " +
                         "but it adds a scaling/composition step when it does not.",
                Fix = "Only worth changing if you are also seeing black bars you did not expect.",
            });
        }

        if (v.ResolutionX > 0)
        {
            var mon = d.FirstOrDefault(x => x.Primary);
            if (mon != null && (v.ResolutionX != mon.Width || v.ResolutionY != mon.Height))
            {
                f.Add(new Finding
                {
                    Area = "Valorant", Severity = Sev.Low,
                    Title = $"Render resolution {v.ResolutionX}x{v.ResolutionY} does not match the display ({mon.Width}x{mon.Height})",
                    Detail = "A mismatch forces a scaling pass and can prevent direct scanout.",
                    Fix = "Set the in-game resolution to the display's native resolution.",
                });
            }
        }
    }

    // ---------------------------------------------------------------- Windows

    private static void WindowsPlatform(List<Finding> f)
    {
        const string DG = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard";
        int? hvci = Native.ReadRegInt(DG + @"\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        int? vbs = Native.ReadRegInt(DG, "EnableVirtualizationBasedSecurity");

        // These are two different things and must never be OR'd together. VBS runs on most modern
        // Windows installs for reasons unrelated to Memory Integrity, so treating VBS=1 as "HVCI on"
        // reports Memory Integrity as enabled on machines where it is switched off.
        if (hvci == 1)
        {
            f.Add(new Finding
            {
                Area = "Windows", Severity = Sev.Medium,
                Title = "Memory Integrity (HVCI / Core Isolation) is enabled",
                Detail = "Kernel transitions go through the hypervisor. The CPU-side cost in games is typically a few " +
                         "percent, and it can lengthen worst-case DPC servicing. Worth one A/B test if the stall probe " +
                         "shows real kernel stalls — but if stalls are at zero and DPC time is low, this is not your " +
                         "cause and turning it off buys nothing except reduced security.",
                Fix = "Only if the stall data justifies it: Windows Security → Device Security → Core Isolation → " +
                      "Memory Integrity → Off, then reboot. Record a session before and after and compare the stall " +
                      "histogram. Turn it back on if the numbers do not move.",
            });
        }
        else
        {
            f.Add(new Finding
            {
                Area = "Windows", Severity = Sev.Good,
                Title = "Memory Integrity (HVCI) is off",
                Detail = "HypervisorEnforcedCodeIntegrity\\Enabled = " + (hvci?.ToString() ?? "not set") +
                         ". Nothing to gain here performance-wise. If a previous test turned it off and the stutters " +
                         "did not change, turn it back on — you are carrying the security cost for no benefit.",
                Fix = "Windows Security → Device Security → Core Isolation → Memory Integrity.",
            });
        }

        if (vbs == 1 && hvci != 1)
        {
            f.Add(new Finding
            {
                Area = "Windows", Severity = Sev.Info,
                Title = "Virtualisation-Based Security is on, but Memory Integrity is not",
                Detail = "EnableVirtualizationBasedSecurity=1 with HVCI off. VBS alone underpins features like " +
                         "Credential Guard and the hypervisor itself; without HVCI its per-kernel-call overhead is far " +
                         "smaller and it is not a realistic cause of discrete sub-second freezes.",
                Fix = "",
            });
        }

        int? dvr = Native.ReadRegInt(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled");
        int? dvrPolicy = Native.ReadRegInt(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
        if (dvr == 1 && dvrPolicy != 0)
        {
            f.Add(new Finding
            {
                Area = "Windows", Severity = Sev.Medium,
                Title = "Game DVR / background recording is enabled",
                Detail = "GameDVR_Enabled=1. The Xbox capture pipeline hooks the present path of whatever game is in " +
                         "focus, even when you are not recording. It costs frames and occasionally produces periodic hitches.",
                Fix = "Settings → Gaming → Captures → turn off 'Record what happened'. Also set " +
                      @"HKCU\System\GameConfigStore\GameDVR_Enabled to 0.",
            });
        }

        int? hags = Native.ReadRegInt(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        f.Add(new Finding
        {
            Area = "Windows", Severity = Sev.Info,
            Title = $"Hardware-accelerated GPU scheduling is {(hags == 2 ? "ON" : "OFF")}",
            Detail = hags == 2
                ? "HAGS is on. Required for NVIDIA Reflex to work at its best, and generally fine on Ada cards."
                : "HAGS is off. On a 40-series card, turning it on usually improves frame pacing and is required for " +
                  "the lowest-latency Reflex modes — but it is worth testing both ways, as a minority of systems hitch with it on.",
            Fix = "Settings → System → Display → Graphics → Change default graphics settings → Hardware-accelerated GPU " +
                  "scheduling. Reboot after changing. Test one session each way.",
        });

        var scheme = Native.ReadRegString(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme");
        string schemeName = scheme?.ToLowerInvariant() switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate performance",
            _ => scheme == null ? "unknown" : "custom (" + scheme + ")",
        };
        f.Add(new Finding
        {
            Area = "Windows", Severity = Sev.Info,
            Title = $"Power plan: {schemeName}",
            Detail = schemeName == "Balanced"
                ? "Balanced is the correct choice on a Ryzen 7000 X3D part — the AMD chipset driver's CPPC preferred-core " +
                  "logic is tuned for it, and High Performance can actually park boost behaviour less effectively."
                : "Note that on Ryzen 7000, Balanced with the AMD chipset driver installed is usually the best option.",
            Fix = "Leave as is unless you are testing.",
        });

        f.Add(new Finding
        {
            Area = "Windows", Severity = Sev.Info,
            Title = $"Windows build {Environment.OSVersion.Version.Build}",
            Detail = $"{Environment.OSVersion.VersionString}, {Environment.ProcessorCount} logical processors.",
            Fix = "",
        });
    }

    // ---------------------------------------------------------------- GPU

    private static void GpuAndDrivers(List<Finding> f, List<Native.DisplayInfo> d)
    {
        var adapters = d.Select(x => x.Adapter).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var virtualAdapters = new[] { "Parsec", "Virtual Display", "IDD", "Sunshine", "Moonlight", "Spacedesk", "DisplayLink" };
        foreach (var name in EnumerateAllAdapters())
        {
            if (virtualAdapters.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            {
                f.Add(new Finding
                {
                    Area = "GPU", Severity = Sev.Medium,
                    Title = $"Virtual display adapter installed: {name}",
                    Detail = "Indirect Display Driver adapters register themselves with the graphics stack and take part in " +
                             "mode-set and composition decisions even when nothing is connected to them. They are a known " +
                             "cause of intermittent present-path hitching and can block independent flip.",
                    Fix = $"If you are not actively streaming, disable '{name}' in Device Manager → Display adapters " +
                          "(right-click → Disable device). Re-enable it when you need it. Test a session with it disabled.",
                });
            }
            else if (name.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase) &&
                     adapters.Any(a => a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
            {
                f.Add(new Finding
                {
                    Area = "GPU", Severity = Sev.Low,
                    Title = "Integrated Radeon graphics active alongside the RTX 4070 SUPER",
                    Detail = "The 7800X3D's iGPU is enabled. Harmless on its own, but if any monitor is plugged into a " +
                             "motherboard video output, frames have to be copied across the PCIe bus to reach it — which " +
                             "causes exactly this kind of intermittent hitch.",
                    Fix = "Confirm every monitor cable goes into the graphics card, not the motherboard. If they all do, " +
                          "leave the iGPU alone; otherwise move them, or disable the iGPU in BIOS.",
                });
            }
        }

        string ver = NvidiaDriverVersion();
        if (ver != null)
        {
            f.Add(new Finding
            {
                Area = "GPU", Severity = Sev.Info,
                Title = $"NVIDIA driver {ver}",
                Detail = "If the stutters started after a driver update, that is your prime suspect regardless of anything " +
                         "else on this list.",
                Fix = "If timing lines up with an update, do a clean install of the previous branch with DDU.",
            });
        }
    }

    private static IEnumerable<string> EnumerateAllAdapters()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < 32; i++)
        {
            var dd = new Native.DISPLAY_DEVICE();
            dd.cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>();
            if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
            var n = dd.DeviceString?.Trim();
            if (!string.IsNullOrEmpty(n) && seen.Add(n)) yield return n;
        }
    }

    private static string NvidiaDriverVersion()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = "--query-gpu=driver_version --format=csv,noheader",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var s = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- storage & memory

    private static void Storage(List<Finding> f)
    {
        string install = ValorantSettings.FindInstallPath();
        if (install != null)
        {
            try
            {
                var di = new DriveInfo(Path.GetPathRoot(install));
                double freePct = di.TotalFreeSpace * 100.0 / di.TotalSize;
                f.Add(new Finding
                {
                    Area = "Storage",
                    Severity = freePct < 10 ? Sev.High : freePct < 15 ? Sev.Medium : Sev.Info,
                    Title = $"Valorant is on {di.Name} — {di.TotalFreeSpace / (1024.0 * 1024 * 1024):F0} GB free ({freePct:F0}%)",
                    Detail = freePct < 15
                        ? "An SSD below ~15% free has much less spare area for garbage collection, and write latency spikes " +
                          "become common. Those spikes stall anything waiting on I/O."
                        : $"Install path: {install}. Plenty of headroom.",
                    Fix = freePct < 15 ? "Free up space until the drive is at least 20% empty." : "",
                });
            }
            catch { }
        }

        var paging = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "PagingFiles", null) as string[];

        if (paging == null || paging.Length == 0 || paging.All(string.IsNullOrWhiteSpace))
        {
            f.Add(new Finding
            {
                Area = "Storage", Severity = Sev.High,
                Title = "Page file appears to be disabled",
                Detail = "With no page file, Windows cannot trim standby memory under pressure. When RAM fills, allocation " +
                         "stalls and hard faults spike — which presents as a freeze.",
                Fix = "System → About → Advanced system settings → Performance → Advanced → Virtual memory → " +
                      "'Automatically manage paging file size'.",
            });
        }
        else
        {
            bool systemManaged = paging.Any(p => p.TrimEnd().EndsWith("0 0") || p.Split(' ').Length < 2);
            f.Add(new Finding
            {
                Area = "Storage", Severity = Sev.Info,
                Title = $"Page file: {string.Join("; ", paging.Where(p => !string.IsNullOrWhiteSpace(p)))}",
                Detail = systemManaged ? "System-managed — the recommended setting." : "Fixed size. Usually fine.",
                Fix = "",
            });
        }
    }

    private static void Memory(List<Finding> f)
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;

        double totalGB = m.ullTotalPhys / (1024.0 * 1024 * 1024);
        double availGB = m.ullAvailPhys / (1024.0 * 1024 * 1024);

        f.Add(new Finding
        {
            Area = "Memory",
            Severity = m.dwMemoryLoad > 85 ? Sev.High : m.dwMemoryLoad > 70 ? Sev.Medium : Sev.Info,
            Title = $"RAM {totalGB:F0} GB total, {availGB:F1} GB free ({m.dwMemoryLoad}% in use) right now",
            Detail = m.dwMemoryLoad > 70
                ? "Memory is already this full before Valorant is even running. Valorant wants roughly 4–5 GB; if free " +
                  "memory runs low mid-match, Windows starts paging and you get freezes."
                : "Comfortable headroom.",
            Fix = m.dwMemoryLoad > 70 ? "Close background apps before playing and re-check." : "",
        });
    }

    private static void RunningApps(List<Finding> f, SessionStore store)
    {
        List<Native.RawProc> procs;
        try { procs = Native.SnapshotProcesses(); } catch { return; }

        var names = procs.Select(p => Path.GetFileNameWithoutExtension(p.Name ?? ""))
                         .Where(n => !string.IsNullOrEmpty(n))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Anything that ran at any point during the session counts, even if it has since been closed.
        int liveOnly = names.Count;
        names.UnionWith(store.ProcessesSeen);
        bool fromCensus = names.Count > liveOnly;

        var hits = KnownOffenders.Where(k => names.Contains(k.Proc))
                                 .GroupBy(k => k.Label)
                                 .Select(g => g.First())
                                 .ToList();

        foreach (var h in hits)
        {
            f.Add(new Finding
            {
                Area = "Background",
                Severity = h.Sev,
                Title = $"{h.Label} is running",
                Detail = $"Detected {h.Proc}.exe — {h.Note}. If it spikes during a freeze, the Stutters tab " +
                         "will name it directly rather than leaving you to guess." +
                         (store.ProcessesSeen.Contains(h.Proc) ? " Seen running during the session." : ""),
                Fix = h.Fix,
            });
        }

        if (hits.Count == 0)
        {
            f.Add(new Finding
            {
                Area = "Background", Severity = Sev.Good,
                Title = "No well-known problem apps detected",
                Detail = $"Scanned {names.Count} process name(s) against the built-in offender list and found none" +
                         (fromCensus ? $", including everything sampled during the {store.ProcessesSeen.Count} " +
                                       "name census taken while recording." : "."),
                Fix = "",
            });
        }
    }
}

// -------------------------------------------------------------------------------------------

/// <summary>Reads Valorant's own INI so the audit can talk about the settings you actually have.</summary>
public sealed class ValorantSettings
{
    public bool Found;
    public string Path = "";
    public bool VSync;

    /// <summary>Effective frame cap in fps, 0 for uncapped. Valorant keeps this in
    /// RiotUserSettings.ini, NOT in the UE-level FrameRateLimit field of GameUserSettings.ini —
    /// that one stays 0.000000 even when a cap is active.</summary>
    public double FrameRateLimit;
    public string FrameRateLimitSource = "";
    public int FullscreenMode = -1;
    public bool Letterbox;
    public int ResolutionX, ResolutionY;
    public string MonitorDeviceId = "";

    public static ValorantSettings Load()
    {
        var s = new ValorantSettings();
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VALORANT", "Saved", "Config");
        if (!Directory.Exists(root)) return s;

        // Prefer the per-account file (it carries the scalability groups too), newest wins.
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        try
        {
            foreach (var p in Directory.EnumerateFiles(root, "GameUserSettings.ini", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(p);
                if (t > bestTime) { bestTime = t; best = p; }
            }
        }
        catch { }
        if (best == null) return s;

        s.Found = true;
        s.Path = best;
        try
        {
            foreach (var raw in File.ReadAllLines(best))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();

                switch (k)
                {
                    case "bUseVSync": s.VSync = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                    case "bShouldLetterbox": s.Letterbox = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                    case "FrameRateLimit":
                        double.TryParse(v, out var fr);
                        if (fr > 0) { s.FrameRateLimit = fr; s.FrameRateLimitSource = "GameUserSettings.ini"; }
                        break;
                    case "FullscreenMode": int.TryParse(v, out var fm); s.FullscreenMode = fm; break;
                    case "ResolutionSizeX": int.TryParse(v, out var rx); s.ResolutionX = rx; break;
                    case "ResolutionSizeY": int.TryParse(v, out var ry); s.ResolutionY = ry; break;
                    case "DefaultMonitorDeviceID": s.MonitorDeviceId = v.Trim('"'); break;
                }
            }
        }
        catch { }

        LoadRiotSettings(s, root);
        return s;
    }

    /// <summary>The FPS cap the user actually set in the video menu lives here, as
    /// <c>EAresBoolSettingName::LimitFramerateAlways</c> plus
    /// <c>EAresFloatSettingName::MaxFramerateAlways</c>. Reading only GameUserSettings.ini reports
    /// "no frame rate cap" on a machine that is very much capped.</summary>
    private static void LoadRiotSettings(ValorantSettings s, string root)
    {
        try
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var p in Directory.EnumerateFiles(root, "RiotUserSettings.ini", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(p);
                if (t > bestTime) { bestTime = t; best = p; }
            }
            if (best == null) return;

            bool limit = false;
            double max = 0;
            foreach (var raw in File.ReadAllLines(best))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                var k = raw[..eq].Trim();
                var v = raw[(eq + 1)..].Trim();
                if (k.EndsWith("LimitFramerateAlways", StringComparison.OrdinalIgnoreCase))
                    limit = v.Equals("True", StringComparison.OrdinalIgnoreCase);
                else if (k.EndsWith("MaxFramerateAlways", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(v, out max);
            }

            if (limit && max > 0)
            {
                s.FrameRateLimit = max;
                s.FrameRateLimitSource = System.IO.Path.GetFileName(best);
            }
        }
        catch { }
    }

    public static string FindInstallPath()
    {
        var yaml = @"C:\ProgramData\Riot Games\Metadata\valorant.live\valorant.live.product_settings.yaml";
        try
        {
            if (File.Exists(yaml))
            {
                var m = Regex.Match(File.ReadAllText(yaml), @"product_install_full_path:\s*""?([^""\r\n]+)");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        catch { }

        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            var p = System.IO.Path.Combine(d.Name, "Riot Games", "VALORANT");
            try { if (Directory.Exists(p)) return p; } catch { }
        }
        return null;
    }
}

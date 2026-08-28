using System.Diagnostics;
using System.Runtime;

namespace StutterDoctor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A blocking gen2 collection inside the monitor would register as a stall in its own probe.
        // SustainedLowLatency keeps the GC out of the way for the life of the session.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Must happen before any measurement: a throttled background process cannot hold a 1 ms
        // timer, and every reading from the stall probe would be fiction.
        Native.DisablePowerThrottling();

        int idx = Array.FindIndex(args, a => a.Equals("--capture", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            int seconds = 60;
            if (idx + 1 < args.Length) int.TryParse(args[idx + 1], out seconds);
            bool lowOverhead = args.Any(a => a.Equals("--low-overhead", StringComparison.OrdinalIgnoreCase));
            return Capture(Math.Clamp(seconds, 5, 3 * 60 * 60), lowOverhead);
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        int tab = 0;
        int t = Array.FindIndex(args, a => a.Equals("--tab", StringComparison.OrdinalIgnoreCase));
        if (t >= 0 && t + 1 < args.Length) int.TryParse(args[t + 1], out tab);

        bool autoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(tab, autoStart));
        return 0;
    }

    /// <summary>Headless timed capture: no window, writes the same report the GUI does.
    /// Handy for "record my next match and tell me what happened" without alt-tabbing.</summary>
    private static int Capture(int seconds, bool lowOverhead)
    {
        Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        try
        {
            using var eng = new Engine { LowOverhead = lowOverhead };
            Console.WriteLine($"Stutter Doctor — capturing for {seconds}s" + (lowOverhead ? " (low-overhead)" : ""));

            var audit = ConfigAudit.Run(eng.Store);
            int high = audit.Count(a => a.Severity >= Sev.High);
            Console.WriteLine($"Checkup: {audit.Count} findings, {high} high-severity.");

            eng.Start();
            Console.WriteLine($"Frametimes: {eng.Store.FrametimeStatus}");
            Console.WriteLine($"GPU:        {eng.Store.GpuStatus}");

            var sw = Stopwatch.StartNew();
            int lastReported = -1;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                eng.Tick();
                Thread.Sleep(250);
                int elapsed = (int)sw.Elapsed.TotalSeconds;
                if (elapsed % 10 == 0 && elapsed != lastReported)
                {
                    lastReported = elapsed;
                    Console.WriteLine($"  {elapsed,4}s   stutters={eng.Store.EventsSnapshot().Length,-4} " +
                                      $"frames={eng.Store.FramesSeen,-8} worst stall={eng.Stall.WorstMs:F1}ms " +
                                      $"overhead={eng.Store.SelfCpuPct:F2}%");
                }
            }

            // Let the pending queue settle so the last detections still get diagnosed.
            for (int i = 0; i < 10; i++) { eng.Tick(); Thread.Sleep(250); }
            eng.Stop();

            string dir = ReportWriter.Write(eng, audit);
            var events = eng.Store.EventsSnapshot();
            Console.WriteLine();
            Console.WriteLine($"Done. {events.Length} stutter(s) recorded, worst stall {eng.Stall.WorstMs:F1} ms, " +
                              $"monitor overhead peaked at {eng.Store.SelfPeakCpuPct:F2}% of total CPU.");
            foreach (var c in Diagnoser.Summarise(events).Take(5))
                Console.WriteLine($"  {c.Count,4}x  {c.Name}  ({c.BestScore}%)");
            Console.WriteLine();
            Console.WriteLine(dir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex);
            return 1;
        }
    }

    private static void Report(Exception ex)
    {
        if (ex == null) return;
        try
        {
            var log = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(log, $"[{DateTime.Now:u}] {ex}\n\n");
            MessageBox.Show(ex.Message + "\n\nWritten to " + log, "Stutter Doctor",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}

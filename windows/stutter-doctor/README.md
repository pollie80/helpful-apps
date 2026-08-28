# Stutter Doctor

Finds out *why* Valorant freezes for a fraction of a second, instead of leaving you to guess.

It records what the machine was doing in the moment around each hitch, then names the cause and
tells you how to fix it. It also runs a configuration checkup that catches the usual Valorant
stutter traps before you even play.

---

## Quick start

Double-click **`Stutter Doctor.cmd`**. It builds on first run and launches elevated.

1. Open the **Checkup** tab and read anything marked CRITICAL or HIGH. Those are found without
   recording anything and are usually where the answer is.
2. Click **Start session**, alt-tab into Valorant, play.
3. **Press F8 the moment you feel a freeze.** This is the single most valuable thing you can do —
   it timestamps your perception so the app can align it against what the hardware was doing.
4. Alt-tab back, click **Stop session**, then **Export report**.

The report lands in `bin\Release\net8.0-windows\reports\session-<timestamp>\`.

### Command line

```bash
StutterDoctor.exe --capture 600
```

| Flag | Effect |
|------|--------|
| `--capture <seconds>` | Headless timed recording, writes the report, exits. No window. |
| `--low-overhead` | Stall probe + kernel counters only. No process table walk, no nvidia-smi. |
| `--autostart` | Open the window and begin recording immediately. |
| `--tab <0-2>` | Open on Live (0), Stutters (1) or Checkup (2). |

---

## What it measures

**Stall probe** — a thread at priority 15 asks to sleep 1 ms, forever. If it comes back in 40 ms,
nothing this app did caused that: the whole machine was wedged. This catches driver DPC storms, SMIs
and storage stalls, and it needs no external tooling.

**Frame timings** — real per-frame CPU and GPU times via PresentMon, which reads ETW out-of-process.
This is what separates "the GPU took 31 ms" from "the CPU took 31 ms" from "the frame was ready on
time and V-Sync held it anyway". Needs Administrator.

**Kernel counters** at 4 Hz — DPC and interrupt time, per-core load, effective CPU clock, hard page
faults, disk queue, network throughput.

**GPU telemetry** at 4 Hz — clocks, temperature, power, and the driver's own throttle-reason bitmask,
so "GPU downclocked" comes with a reason rather than a shrug.

**Process table** at 1 Hz, in one syscall — so a background app that spikes for 300 ms gets *named*.

Each detection is held for ~1.3 s before analysis so the surrounding telemetry has actually arrived,
then scored against every rule. If nothing correlates, it reports **Unattributed** rather than
inventing a cause.

---

## Does the monitor itself cost frames?

It reports its own CPU usage in the **Monitor overhead** tile — verify rather than trust. Measured
here: **0.2% of total CPU, peaking near 1%.**

- Nothing touches the game: no overlay, no injection, no hooks, no reading Valorant's memory. This
  is also why Vanguard has no reason to object.
- One syscall per second for the whole process table, not 300 `Process` objects.
- nvidia-smi runs once as a streaming child process, not re-spawned per sample.
- The stall log is lock-free, so the probe can never block behind a reader and manufacture the
  stalls it is trying to measure.
- While Valorant has focus the app drops its own priority class and stops painting the graph
  entirely. The probe is unaffected — priority 15 survives the demotion.
- `--low-overhead` cuts it further if you want a control run.

### One gotcha worth knowing about

Windows 11 ignores a *background* process's 1 ms timer request and clamps it to the stock ~15.6 ms
tick. Because this tool is always in the background while you play, that would have produced a
continuous stream of phantom 16 ms "freezes". The app opts out of power throttling
(`PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`) to prevent it, and displays the timer
resolution actually in effect next to the worst-stall reading. If that ever shows ~15.6 ms instead
of ~1 ms, the stall numbers are measuring Windows' tick rate and the report says so explicitly.

---

## Reading the output

`report.md` is the human-readable summary: ranked verdict, frame-pacing percentiles, the stall
histogram, the full checkup, every event, and a suggested bisect order.

Raw data sits alongside it as CSV: `stutters.csv`, `counters.csv`, `gpu.csv`, `stalls.csv`, and
`frames_around_stutters.csv` (windows around each event only — a full frametime log would run to
hundreds of megabytes).

**Change one thing at a time** and record a fresh session after each. Compare the **0.1% low** and
the **stall histogram** between runs; those move first, well before the average frame rate does.

---

## Requirements

.NET 8 SDK (installed). Administrator only for frametime capture — everything else works without it.
PresentMon is optional and auto-detected; NVIDIA's FrameView SDK bundles a copy at
`C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe`.

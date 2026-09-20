---
name: windows-game-telemetry
description: How to measure what a Windows game is actually doing - frametimes, CPU/GPU attribution, DPC and interrupt time, paging, timer resolution, display mode - from outside the process, with no injection, hooks or overlay. Use when building or reviewing a performance monitor, diagnosing frame pacing, stutter or hitching, attributing a stall to CPU vs GPU, reading PDH or ETW counters from code, or auditing Windows settings that cost frames (GameDVR, HAGS, VBS/HVCI, power plan, refresh-rate mismatch). Includes the measurement traps that silently produce confident wrong answers.
---

# Windows game telemetry

Distilled from a working frametime diagnostic. The techniques matter more than the tool: most
of the value is in *which* source answers a question, and in the traps that make a monitor
report phantom problems.

## Principle: stay outside the process

Never inject, hook, overlay, or read the game's memory. Anti-cheat treats all of it as hostile,
and the act of measuring changes what you measure. Everything below is out-of-process, reading
data Windows already publishes.

Then **measure your own overhead and publish it**. A monitor that cannot state its own cost is
asking to be trusted rather than verified. Reporting "0.2% of one core, peaking near 1%" is a
claim a reader can check.

## Sources, by question

| Question | Source |
| --- | --- |
| How long did each frame take, CPU vs GPU? | **PresentMon** via ETW, out-of-process |
| Was the CPU busy, and on which core? | PDH `\Processor Information(<group>,<core>)\% Processor Time` |
| Was the kernel eating time in drivers? | DPC and interrupt time counters |
| Was the scheduler oversubscribed? | `\System\Processor Queue Length`, `\System\Context Switches/sec` |
| Did it touch the disk to satisfy RAM? | `\Memory\Pages Input/sec` (hard faults), `\Memory\Available MBytes` |
| Was the disk the stall? | `\PhysicalDisk(_Total)\% Idle Time`, `Avg. Disk Queue Length` |
| What is every process doing, cheaply? | `NtQuerySystemInformation(SystemProcessInformation)` |
| What is actually in focus? | `GetForegroundWindow` + `GetWindowThreadProcessId` |
| Refresh rate and monitor identity | `EnumDisplayDevices` + `EnumDisplaySettings` |
| Total RAM pressure | `GlobalMemoryStatusEx` |

### PDH: always use the English counter API

Call **`PdhAddEnglishCounterW`**, never `PdhAddCounterW`. Performance counter names are
localised - on a German or Japanese Windows, `\Processor Information(_Total)\% Processor Time`
does not exist under that name and the query fails at runtime on a machine you do not have.
The English variant takes the canonical name on every locale. This is the single most common
portability bug in homegrown Windows monitors.

### Process table in one call

`NtQuerySystemInformation` with `SystemProcessInformation` snapshots every process in a single
buffer. Enumerating processes individually costs more than the thing being measured, which
matters when sampling at a few hertz while a game runs.

### PresentMon

Frame data comes from ETW, so it needs administrator rights but no game cooperation. NVIDIA's
FrameView SDK bundles a copy at
`C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe`, which is worth
auto-detecting before asking the user to install anything.

## Traps that produce confident wrong answers

These are the expensive ones. Each produced a real, plausible, completely wrong diagnosis.

**1. Windows 11 clamps a background process's timer resolution.**
`timeBeginPeriod(1)` is ignored for a background process and clamped to the stock ~15.6 ms tick.
A monitor is *always* in the background while the game has focus, so any timing probe reports a
continuous stream of phantom ~16 ms stalls. Opt out with
`SetProcessInformation(..., ProcessPowerThrottling, PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION)`,
then **display the resolution actually in effect** via `NtQueryTimerResolution`. If it reads
15.6 ms rather than ~1 ms, the stall numbers are measuring Windows' tick rate and the report
must say so rather than quietly reporting freezes.

**2. PresentMon 2.x defaults to 1.x metrics.**
The 1.x metric set has no `CPUBusy`/`CPUWait`/`GPUBusy`/`GPUWait`, so every frame attributes to
"unattributed" while the capture looks perfectly healthy. Pass `--v2_metrics`. Related: do not
alias `MsInPresentAPI` onto CPU time - that is only the duration inside `Present()`, not the
frame's CPU cost.

**3. Parse frame timestamps as decimal.**
A `long.TryParse` on a decimal timestamp fails silently, and every frame falls back to batch
arrival time - hundreds of frames sharing one instant, which then looks like a massive stall.

**4. Strip capped frames before analysing pacing.**
Menu and background frame caps (commonly 60 fps) produce a dense spike at ~16.67 ms that is
trivially mistaken for a periodic 60 Hz blocker. The tell is run length: capped frames arrive in
long consecutive runs, while a real hitch is 1-2 frames. Filter sustained cap-rate stretches out
before computing percentiles, or the "1% low" is just the cap.

**5. Do not infer V-Sync misses from arithmetic.**
Flagging any frame near a multiple of the refresh interval invents hundreds of findings on a
machine where VRR is working fine. Gate it on *measured* scanout quantisation - under working
G-Sync/FreeSync, displayed-frame times are continuous rather than snapped to the refresh grid.

## Reading periodicity

A periodic cause announces itself as a spike at one exact frame duration, because frames are
waiting for that source's next tick - and the frequency then names the culprit. So **histogram
the late frames before trusting any per-event rule**. A smooth exponential decay with no
periodicity means there is no single blocker to find, and per-event "causes" will be noise.

## Settings audit worth running first

Cheaper than any capture, and often the whole answer:

| Setting | Where |
| --- | --- |
| GameDVR / background recording | `HKCU\System\GameConfigStore\GameDVR_Enabled`, `HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR` |
| Hardware-accelerated GPU scheduling | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` |
| VBS / HVCI | `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard` |
| Active power plan | `ActivePowerScheme` |
| Refresh-rate mismatch across monitors | `EnumDisplaySettings` per display |

Also worth flagging: RGB/vendor services (Armoury Crate, Aura, Nahimic), an open Epic or Steam
launcher, GPU-accelerated browser tabs, Defender real-time scanning the game directory, a drive
under 20% free, and integrated graphics active alongside the discrete card.

Refresh-rate mismatch deserves particular attention - a mixed set of monitors can force a
compositor scaling pass and prevent direct scanout, which reads as a hardware limit but is not.

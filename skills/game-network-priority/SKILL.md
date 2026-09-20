---
name: game-network-priority
description: Diagnose and fix packet loss, lag spikes or rubber-banding in a competitive game (Valorant, CS2, Apex, League, Overwatch) that appears while something else is downloading - Steam, Windows Update, Epic, a browser. Use when someone reports in-game packet loss during a download, asks to "prioritise" a game's traffic, asks whether they can download and play at the same time, or asks about QoS for gaming. Covers bufferbloat diagnosis, why Windows QoS policies cannot fix inbound traffic, and the fixes that actually work.
---

# Game network priority

## The one idea that matters

When a game shows packet loss while a download runs, the player is almost never **out of
bandwidth**. A competitive shooter uses 1-3 Mbps. On a gigabit line there is plenty spare.

The problem is **queueing delay**, commonly called bufferbloat:

1. A bulk download (TCP) probes for more speed until it completely fills the narrowest link.
2. That link's buffer stays permanently full - typically hundreds of milliseconds deep.
3. The game's small, time-critical UDP packets arrive and sit *behind* that queue.
4. Packets that arrive too late are useless. The game reports them as **loss**.

So the goal is never "give the game more bandwidth". It is **keep the bottleneck queue short**,
which means making the bulk download ask for less than the line can carry.

Corollary worth stating plainly: a download at 100% of line rate will always hurt. A download
at 50-60% of line rate usually costs a few milliseconds and nothing more.

## Diagnose (Windows)

Find the real number first - do not trust Task Manager's per-process view, it misses
Delivery Optimization entirely.

```powershell
# Total inbound over 5s, in Mbps
$a=(Get-NetAdapterStatistics -Name Ethernet).ReceivedBytes; Start-Sleep 5
$b=(Get-NetAdapterStatistics -Name Ethernet).ReceivedBytes
"Inbound: $([math]::Round((($b-$a)*8)/5/1MB,2)) Mbps"

# Negotiated link speed, to compare against
Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object Name, LinkSpeed
```

If inbound is close to link speed, you have found the cause. Now name the culprit.

### Culprit 1: Steam

Steam stages partial downloads on disk, so an active or queued download is visible as a folder
even when Steam is closed:

```powershell
# Library roots are listed in <steam>\steamapps\libraryfolders.vdf
# A download in progress appears as:
#   <library>\steamapps\downloading\<appid>\
# and its human name is the "name" key in:
#   <library>\steamapps\appmanifest_<appid>.acf
```

To pause without losing progress, ask Steam to close itself. Staged bytes are kept and the
download resumes on next launch:

```powershell
& "$env:ProgramFiles(x86)\Steam\steam.exe" -shutdown
```

### Culprit 2: Windows Update / Delivery Optimization

This is the one people miss. It does not appear as a normal process download:

```powershell
Get-DeliveryOptimizationStatus
```

Any row with `Status = Downloading` is pulling at full speed. The services behind it are
`wuauserv`, `DoSvc` and `UsoSvc`. Stopping them requires elevation.

## Fixes, best first

### 1. Router SQM - the only real fix

Enable **SQM / QoS** on the router using `CAKE` or `fq_codel`, with ingress shaping set to
**85-90% of measured line rate**. This deliberately gives up ~10% of peak throughput so the
router, not the ISP's buffer, controls the queue. Downloads then run at full tilt with no
measurable effect on game latency.

This works because downstream buffering happens in *the ISP's* equipment, which you cannot
reach - unless you shape ingress slightly below what they send, so their buffer never fills.

Available on OpenWrt, most Ubiquiti/UniFi, pfSense/OPNsense, eero, and many ASUS/Netgear
gaming models. If the router does not support it, use fix 2 or 3.

### 2. Cap the downloader (no router support needed)

If the bulk flow is the only heavy traffic, having *it* self-limit achieves the same thing.

Steam: **Settings > Downloads > Limit bandwidth to**. Set it to roughly **half** the measured
line rate, then tune upward until latency starts to move. Steam ships unlimited by default,
which is why it saturates.

Verified sibling settings in the same panel:
- **Allow downloads during gameplay** - uncheck this to have Steam pause automatically.
- *Throttle downloads while streaming* - Remote Play only, irrelevant here.

Caveat: "Allow downloads during gameplay" only fires for games Steam knows are running. For a
non-Steam title such as Valorant, add it as a non-Steam shortcut and launch it through Steam so
Steam sees a game running. Worth verifying on the specific setup before relying on it.

### 3. Pause everything for the session

Blunt but certain. See `scripts/valorant-focus.ps1` in this folder - it closes Steam, pauses
Windows Update, caps Delivery Optimization to 5% via policy, and lowers background process
priority. Run with `-Off` to restore.

## What does NOT work

**Windows QoS policies do not fix this.** `New-NetQosPolicy`, DSCP tagging, and
`ThrottleRateActionBitsPerSecond` all act on **outbound** traffic only. A download is
**inbound**. An endpoint cannot shape traffic that has already been sent to it - the queueing
damage happened upstream before the packets arrived. Any guide recommending a Windows QoS
policy to fix download-induced lag is wrong about the mechanism.

DSCP marking also only matters if every hop honours it. Consumer ISPs generally do not.

**Raising game process priority does not fix this either.** It is a CPU scheduling control and
has no effect on a network queue. On a machine that is not CPU-bound it changes nothing
measurable. Harmless, but do not present it as the fix.

## Reporting back

State the measured numbers, not impressions: inbound Mbps before and after, link speed, and
which culprit was responsible. "933 Mbps of a 1 Gbps line, from a Steam download" is a
diagnosis. "Your internet was busy" is not.

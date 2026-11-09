# Sets every RGB device (except the ones you skip) to one colour, then exits.
# A run-once replacement for SignalRGB-style apps that keep re-sending colours all day.
# Runs at logon from the elevated scheduled task "RGB Colour Once" (see install.ps1).
#
# How: start the OpenRGB service just long enough to detect hardware (it owns the SMBus/PawnIO
# access RAM needs), push the colour through the OpenRGB CLI, stop the service.
# The colour lives in each device's controller afterwards, so nothing keeps running.
#
# OpenRGB.exe is a GUI-subsystem program that prints to the parent console, so its output is
# read back from this console's screen buffer.
#
# Settings come from rgb-config.json next to this script (copy rgb-config.example.json).

param([string]$Color)

$ErrorActionPreference = 'Stop'
$orgb = 'C:\Program Files\OpenRGB\OpenRGB.exe'
$logFile = Join-Path $PSScriptRoot 'last-run.log'
$configFile = Join-Path $PSScriptRoot 'rgb-config.json'

function Log($m) { "$(Get-Date -f 'yyyy-MM-dd HH:mm:ss') $m" | Add-Content $logFile -Encoding UTF8 }
Set-Content $logFile "" -Encoding UTF8

if (-not (Test-Path $configFile)) { Log "no rgb-config.json - copy rgb-config.example.json and edit it"; exit 1 }
$cfg = Get-Content $configFile -Raw | ConvertFrom-Json
if (-not $Color) { $Color = "$($cfg.color)" }
$Color = $Color.Trim().TrimStart('#')
$skip = @($cfg.skip | % { [regex]::Escape($_) }) -join '|'
$expected = [ordered]@{}
if ($cfg.expected) { $cfg.expected.PSObject.Properties | % { $expected[$_.Name] = [int]$_.Value } }
$defaultMode = if ($cfg.defaultMode) { "$($cfg.defaultMode)" } else { 'Direct' }
$waitMinutes = if ($cfg.waitMinutes) { [double]$cfg.waitMinutes } else { 2 }

$raw = $Host.UI.RawUI
try { $raw.BufferSize = New-Object Management.Automation.Host.Size(300, 3000) } catch {}

function Invoke-Orgb([string[]]$argv) {
    Clear-Host
    Start-Process $orgb -ArgumentList $argv -NoNewWindow -Wait
    $y = $raw.CursorPosition.Y
    if ($y -lt 1) { return @() }
    $cells = $raw.GetBufferContents((New-Object Management.Automation.Host.Rectangle(0, 0, 299, $y)))
    for ($r = 0; $r -le $cells.GetUpperBound(0); $r++) {
        (-join (0..$cells.GetUpperBound(1) | % { $cells[$r, $_].Character })).TrimEnd()
    }
}

# Devices from "expected" that have not shown up yet (name substring = how many must appear).
function Get-Missing($devs) {
    @($expected.Keys | ? { @($devs -match [regex]::Escape($_)).Count -lt $expected[$_] })
}

Log "start, colour #$Color"

# SignalRGB must not hold the SMBus. Harmless if it is not installed.
Get-Process SignalRgb, SignalRgbLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service 'SignalRgb.Service' -Force -ErrorAction SilentlyContinue

# The task runs right at logon, so some devices may not be ready yet. OpenRGB only detects when
# its service starts, so if any expected device is missing the service is restarted to rescan,
# until waitMinutes. Then whatever was found gets coloured.
$deadline = (Get-Date).AddMinutes($waitMinutes)
$devices = @(); $missing = @()
try {
    do {
        Start-Service 'OpenRGB'
        # Poll until the list stops growing (detection finished) or everything is there.
        $prev = -1
        for ($i = 0; $i -lt 15; $i++) {
            Start-Sleep 2
            $devices = @(Invoke-Orgb @('--list-devices') | ? { $_ -match '^\d+: ' })
            if ($expected.Count -and -not (Get-Missing $devices)) { break }
            if ($devices.Count -gt 0 -and $devices.Count -eq $prev) { break }
            $prev = $devices.Count
        }
        $missing = Get-Missing $devices
        if (-not $missing) { break }
        Log ("missing: " + ($missing -join ', ') + " - rescanning")
        Stop-Service 'OpenRGB' -Force
        Start-Sleep 5
    } while ((Get-Date) -lt $deadline)
    if ($missing) { Log ("gave up waiting for: " + ($missing -join ', ')) }
    Log ("detected: " + ($devices -join ' | '))

    foreach ($line in $devices) {
        $idx, $name = $line -split ': ', 2
        if ($skip -and $name -match $skip) { Log "skip $name"; continue }

        # First "devices" rule whose match is in the name decides mode and zone sizes.
        $rule = $cfg.devices | ? { $name -match [regex]::Escape($_.match) } | select -First 1
        $mode = if ($rule -and $rule.mode) { "$($rule.mode)" } else { $defaultMode }
        $argv = @()
        if ($rule -and $rule.zoneSizes) {
            $rule.zoneSizes.PSObject.Properties | % { $argv += @('-d', $idx, '-z', $_.Name, '-s', "$($_.Value)") }
        }
        $argv += @('-d', $idx, '-m', $mode, '-c', $Color)

        # A fresh CLI client sometimes validates -d before the server has sent every controller,
        # giving "Empty device ID". Retry a few times.
        for ($try = 1; $try -le 6; $try++) {
            $res = @(Invoke-Orgb $argv | ? { $_ })
            Log "set $name (try $try) :: $($argv -join ' ') :: $($res -join ' / ')"
            if (-not ($res -match 'Error')) { break }
            Start-Sleep 3
        }
    }
}
finally {
    Start-Sleep 2
    Stop-Service 'OpenRGB' -Force -ErrorAction SilentlyContinue
    Log "done, OpenRGB service stopped"
}

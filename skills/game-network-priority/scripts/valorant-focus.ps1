<#
  valorant-focus.ps1 - give Valorant the network and the CPU.

    .\valorant-focus.ps1          # focus mode ON
    .\valorant-focus.ps1 -Off     # restore everything
    .\valorant-focus.ps1 -Status  # just report what is eating the line

  Self-elevates for the Windows Update parts. The Steam and priority
  parts work fine unelevated.
#>
[CmdletBinding()]
param(
    [switch]$Off,
    [switch]$Status
)

$ErrorActionPreference = 'Continue'
$DOPolicy   = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization'
$UpdSvcs    = @('wuauserv','DoSvc','UsoSvc')
$Background = @('msedge','msedgewebview2','chrome','firefox','Discord','OneDrive','steamwebhelper')

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-InboundMbps([int]$Seconds = 4) {
    $nic = Get-NetAdapter | Where-Object Status -eq 'Up' |
           Sort-Object { (Get-NetAdapterStatistics -Name $_.Name).ReceivedBytes } -Descending |
           Select-Object -First 1
    if (-not $nic) { return 0 }
    $a = (Get-NetAdapterStatistics -Name $nic.Name).ReceivedBytes
    Start-Sleep -Seconds $Seconds
    $b = (Get-NetAdapterStatistics -Name $nic.Name).ReceivedBytes
    [math]::Round((($b - $a) * 8) / $Seconds / 1MB, 2)
}

function Get-SteamPath {
    $p = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $p) {
        $p = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath
    }
    if ($p) { return $p.Replace('/','\') }
    return $null
}

function Show-Hogs {
    Write-Host "`n  Inbound right now: $(Get-InboundMbps) Mbps" -ForegroundColor Cyan

    $steamPath = Get-SteamPath
    if ($steamPath) {
        $libs = @($steamPath)
        $vdf  = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            Get-Content $vdf | Select-String '"path"' | ForEach-Object {
                $libs += (($_ -replace '.*"path"\s*"([^"]+)".*','$1') -replace '\\','\')
            }
        }
        foreach ($l in ($libs | Where-Object { $_ } | Select-Object -Unique)) {
            $dl = Join-Path $l 'steamapps\downloading'
            if (Test-Path $dl) {
                Get-ChildItem $dl -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    $man  = Join-Path $l "steamapps\appmanifest_$($_.Name).acf"
                    $name = $_.Name
                    if (Test-Path $man) {
                        $hit = Get-Content $man | Select-String '"name"' | Select-Object -First 1
                        if ($hit) { $name = ($hit -replace '.*"name"\s*"([^"]+)".*','$1') }
                    }
                    Write-Host "  Steam has a download queued/active: $name" -ForegroundColor Yellow
                }
            }
        }
    }

    try {
        $do = Get-DeliveryOptimizationStatus -ErrorAction Stop
        foreach ($j in $do) {
            Write-Host ("  Windows Update downloading: {0} MB of {1} MB" -f
                [math]::Round($j.BytesFromHttp/1MB,1),
                [math]::Round($j.TotalBytesToTransfer/1MB,1)) -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  Windows Update: idle" -ForegroundColor DarkGray
    }
}

if ($Status) { Show-Hogs; return }

# Re-launch elevated so the Windows Update half can run too.
if (-not (Test-Admin)) {
    Write-Host "Elevating for the Windows Update controls..." -ForegroundColor DarkGray
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    if ($Off) { $argList += '-Off' }
    try {
        Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait
        return
    } catch {
        Write-Host "Declined - continuing without the update controls.`n" -ForegroundColor Yellow
    }
}

if (-not $Off) {
    Write-Host "`n=== VALORANT FOCUS: ON ===" -ForegroundColor Green
    $before = Get-InboundMbps 3
    Write-Host "  Inbound before: $before Mbps"

    # 1. Stop Steam - this pauses any in-flight download. It resumes on next launch.
    $steamPath = Get-SteamPath
    $steamExe  = if ($steamPath) { Join-Path $steamPath 'steam.exe' } else { $null }
    if (Get-Process steam -ErrorAction SilentlyContinue) {
        if ($steamExe -and (Test-Path $steamExe)) { & $steamExe -shutdown }
        $waited = 0
        while ((Get-Process steam -ErrorAction SilentlyContinue) -and $waited -lt 15) {
            Start-Sleep -Seconds 1; $waited++
        }
        Get-Process steam,steamwebhelper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Write-Host "  Steam closed (download paused, progress kept)" -ForegroundColor Green
    } else {
        Write-Host "  Steam already closed" -ForegroundColor DarkGray
    }

    # 2. Park Windows Update / Delivery Optimization.
    if (Test-Admin) {
        New-Item -Path $DOPolicy -Force | Out-Null
        Set-ItemProperty $DOPolicy -Name 'DOPercentageMaxForegroundBandwidth' -Value 5  -Type DWord
        Set-ItemProperty $DOPolicy -Name 'DOPercentageMaxBackgroundBandwidth' -Value 5  -Type DWord
        foreach ($s in $UpdSvcs) {
            try { Stop-Service $s -Force -ErrorAction Stop } catch {}
        }
        Write-Host "  Windows Update paused, Delivery Optimization capped at 5%" -ForegroundColor Green
    }

    # 3. Priorities.
    $v = Get-Process VALORANT-Win64-Shipping -ErrorAction SilentlyContinue
    if ($v) {
        try { $v.PriorityClass = 'High'; Write-Host "  Valorant -> High priority" -ForegroundColor Green } catch {}
    } else {
        Write-Host "  Valorant not running yet - re-run this once it is up" -ForegroundColor DarkGray
    }
    $n = 0
    Get-Process $Background -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.PriorityClass = 'BelowNormal'; $n++ } catch {}
    }
    Write-Host "  $n background processes -> BelowNormal" -ForegroundColor Green

    Write-Host "  Inbound after:  $(Get-InboundMbps 4) Mbps`n" -ForegroundColor Cyan
}
else {
    Write-Host "`n=== VALORANT FOCUS: OFF ===" -ForegroundColor Yellow
    if (Test-Admin) {
        foreach ($k in 'DOPercentageMaxForegroundBandwidth','DOPercentageMaxBackgroundBandwidth') {
            Remove-ItemProperty $DOPolicy -Name $k -ErrorAction SilentlyContinue
        }
        foreach ($s in $UpdSvcs) {
            try { Set-Service $s -StartupType Automatic -ErrorAction SilentlyContinue } catch {}
            try { Start-Service $s -ErrorAction Stop } catch {}
        }
        Write-Host "  Windows Update restored" -ForegroundColor Green
    }
    $n = 0
    Get-Process $Background -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.PriorityClass = 'Normal'; $n++ } catch {}
    }
    Write-Host "  $n background processes -> Normal" -ForegroundColor Green
    Write-Host "  Relaunch Steam when you want the download to resume.`n" -ForegroundColor DarkGray
}

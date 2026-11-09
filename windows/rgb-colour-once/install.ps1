# Run elevated once. Turns off SignalRGB autostart (if present) and registers the one-shot
# logon task. Needs OpenRGB 1.1+ (which pulls in the PawnIO driver), e.g.
#   winget install OpenRGB.OpenRGB
$dir = $PSScriptRoot
$user = "$env:USERDOMAIN\$env:USERNAME"

$config = Join-Path $dir 'rgb-config.json'
if (-not (Test-Path $config)) { Copy-Item (Join-Path $dir 'rgb-config.example.json') $config }

# SignalRGB autostart: back up and remove the Run entry; service to Manual.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$v = (Get-ItemProperty $run -ErrorAction SilentlyContinue).SignalRgb
if ($v) { "SignalRgb=$v" | Set-Content (Join-Path $dir 'signalrgb-run-backup.txt'); Remove-ItemProperty $run -Name SignalRgb }
Set-Service 'SignalRgb.Service' -StartupType Manual -ErrorAction SilentlyContinue
# The OpenRGB installer sets its service to Automatic; set-rgb.ps1 starts it only while needed.
Set-Service 'OpenRGB' -StartupType Manual -ErrorAction SilentlyContinue

# conhost --headless gives the script a console buffer without a visible window.
# No delay: set-rgb.ps1 itself waits for every expected device and rescans if any are missing.
$ps = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\set-rgb.ps1`""
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless $ps"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName 'RGB Colour Once' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force |
    Out-Null

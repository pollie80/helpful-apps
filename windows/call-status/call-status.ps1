# Call Status popup - always-on-top indicator for mic/camera use.
# Detection:
#  - Mic/camera in use: CapabilityAccessManager ConsentStore registry (LastUsedTimeStop = 0)
#  - Device-level mic mute: Core Audio (default communications capture device)
#  - Discord in-app mute/deafen: Discord local RPC (requires one-time discord-setup.ps1)
# States: green = free, red = in a call (hot mic), amber = in a call but muted,
#         blue = camera only. Separate MIC / CAM badges show each device explicitly.
# Controls: drag to move, drag any edge or corner to resize (the badge scales with the
#           window), double-click to force "IN A CALL" on/off, right-click to quit.
#           Those are listed in a tooltip on hover; nothing is drawn on the badge itself.

Add-Type -AssemblyName PresentationFramework
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072  # TLS 1.2
Add-Type -Path (Join-Path $PSScriptRoot 'DiscordRpc.cs')

# Core Audio interop for device-level mic mute (Sound settings / Fn key / headset mute)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int NotImpl1();
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
}

[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IntPtr pNotify);
    int UnregisterControlChangeNotify(IntPtr pNotify);
    int GetChannelCount(out uint channelCount);
    int SetMasterVolumeLevel(float level, ref Guid eventContext);
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channelNumber, float level, ref Guid eventContext);
    int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
    int GetChannelVolumeLevel(uint channelNumber, out float level);
    int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
    int SetMute(bool mute, ref Guid eventContext);
    int GetMute(out bool mute);
}

public static class MicState {
    public static bool? GetDefaultMicMute() {
        try {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice dev;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(1, 2, out dev));
            var iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 1, IntPtr.Zero, out o));
            var vol = (IAudioEndpointVolume)o;
            bool mute;
            Marshal.ThrowExceptionForHR(vol.GetMute(out mute));
            return mute;
        } catch { return null; }
    }
}
'@

# Build emoji from codepoints so the file stays plain ASCII
$emoRed   = [char]::ConvertFromUtf32(0x1F534)   # red circle
$emoMic   = [char]::ConvertFromUtf32(0x1F3A4)   # microphone
$emoGreen = [char]::ConvertFromUtf32(0x1F7E2)   # green circle
$emoCam   = [char]::ConvertFromUtf32(0x1F3A5)   # movie camera
$emoMuted = [char]::ConvertFromUtf32(0x1F507)   # muted speaker
$emoGame  = [char]::ConvertFromUtf32(0x1F3AE)   # video game controller

# Background utilities that hold the mic/camera open 24/7 - not real calls
$script:IgnoreApps = @('nvcontainer', 'nvbroadcast', 'nvidia broadcast', 'steelseriessonar',
                       'sonarhost', 'wavelink', 'voicemod', 'audiorepeater', 'rtkauduservice64')

function Get-CapabilityAppsInUse {
    param([string]$Capability)   # 'microphone' or 'webcam'
    $inUse = @()
    $base = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\$Capability"

    Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -ne 'NonPackaged' } |
        ForEach-Object {
            $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $p -and $p.PSObject.Properties['LastUsedTimeStop'] -and $p.LastUsedTimeStop -eq 0) {
                $inUse += ($_.PSChildName -split '_')[0]
            }
        }

    Get-ChildItem (Join-Path $base 'NonPackaged') -ErrorAction SilentlyContinue |
        ForEach-Object {
            $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $p -and $p.PSObject.Properties['LastUsedTimeStop'] -and $p.LastUsedTimeStop -eq 0) {
                $exe = ($_.PSChildName -split '#')[-1] -replace '\.exe$',''
                $inUse += $exe
            }
        }

    $inUse | Where-Object { $script:IgnoreApps -notcontains $_.ToLower() } | Sort-Object -Unique
}

function Get-FriendlyNames {
    param([string[]]$Apps)
    ($Apps | ForEach-Object {
        switch -Wildcard ($_.ToLower()) {
            'ms-teams*'  { 'Teams' }
            '*teams*'    { 'Teams' }
            'zoom*'      { 'Zoom' }
            'discord*'   { 'Discord' }
            'slack*'     { 'Slack' }
            'chrome*'    { 'Chrome' }
            'msedge*'    { 'Edge' }
            'firefox*'   { 'Firefox' }
            'whatsapp*'  { 'WhatsApp' }
            'valorant*'  { 'Valorant' }
            default      { $_ }
        }
    } | Sort-Object -Unique) -join ', '
}

# ---------- Discord RPC (in-app mute/deafen) ----------
$script:ConfigPath   = Join-Path $PSScriptRoot 'call-status-config.json'
$script:DiscordCfg   = $null
$script:Rpc          = $null
$script:RpcNextTry   = 0

function Load-DiscordConfig {
    if (Test-Path $script:ConfigPath) {
        try { $script:DiscordCfg = Get-Content $script:ConfigPath -Raw | ConvertFrom-Json } catch { $script:DiscordCfg = $null }
    }
}

function Save-DiscordConfig {
    try { $script:DiscordCfg | ConvertTo-Json | Set-Content $script:ConfigPath -Encoding UTF8 } catch {}
}

function Invoke-DiscordAuthenticate {
    param($Rpc)
    $req  = '{"cmd":"AUTHENTICATE","args":{"access_token":"' + $script:DiscordCfg.access_token + '"},"nonce":"' + [guid]::NewGuid() + '"}'
    $resp = $Rpc.Request($req, 3000)
    if ($resp -and $resp -notmatch '"evt"\s*:\s*"ERROR"') { return $true }
    # Token stale - try a refresh
    try {
        $tok = Invoke-RestMethod -Method Post -Uri 'https://discord.com/api/oauth2/token' -TimeoutSec 8 `
            -ContentType 'application/x-www-form-urlencoded' -Body @{
                client_id     = $script:DiscordCfg.client_id
                client_secret = $script:DiscordCfg.client_secret
                grant_type    = 'refresh_token'
                refresh_token = $script:DiscordCfg.refresh_token
            }
        $script:DiscordCfg.access_token  = $tok.access_token
        $script:DiscordCfg.refresh_token = $tok.refresh_token
        Save-DiscordConfig
        $req  = '{"cmd":"AUTHENTICATE","args":{"access_token":"' + $script:DiscordCfg.access_token + '"},"nonce":"' + [guid]::NewGuid() + '"}'
        $resp = $Rpc.Request($req, 3000)
        return ($resp -and $resp -notmatch '"evt"\s*:\s*"ERROR"')
    } catch { return $false }
}

# Returns $true (muted or deafened), $false (live), or $null (unavailable / not set up)
function Get-DiscordMute {
    if ($null -eq $script:DiscordCfg) {
        Load-DiscordConfig
        if ($null -eq $script:DiscordCfg) { return $null }
    }
    if ($null -eq $script:Rpc -or -not $script:Rpc.Connected) {
        $now = [Environment]::TickCount
        if (($now - $script:RpcNextTry) -lt 0) { return $null }
        $script:RpcNextTry = $now + 20000   # retry at most every 20s
        $r = New-Object DiscordRpc
        if (-not $r.Connect([string]$script:DiscordCfg.client_id)) { return $null }
        if (-not (Invoke-DiscordAuthenticate $r)) { $r.Close(); return $null }
        $script:Rpc = $r
    }
    $resp = $script:Rpc.Request('{"cmd":"GET_VOICE_SETTINGS","args":{},"nonce":"' + [guid]::NewGuid() + '"}', 1500)
    if (-not $resp) { return $null }
    try { $j = $resp | ConvertFrom-Json } catch { return $null }
    if ($j.evt -eq 'ERROR') { return $null }
    return [bool]($j.data.mute -or $j.data.deaf)
}

# ---------- UI ----------
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Call Status" Width="640" Height="220" MinWidth="220" MinHeight="80"
        WindowStyle="None" ResizeMode="NoResize" AllowsTransparency="True"
        Background="Transparent" Topmost="True" ShowInTaskbar="False" ShowActivated="False"
        ToolTipService.InitialShowDelay="500" ToolTipService.ShowDuration="30000">
  <Grid>
    <Border x:Name="Root" CornerRadius="28" Background="#2E7D32">
      <Border.Effect>
        <DropShadowEffect BlurRadius="14" ShadowDepth="2" Opacity="0.5"/>
      </Border.Effect>
      <!-- Viewbox scales the whole badge with the window. The inner width is fixed so the
           layout (and the detail line's ellipsis) stays proportional at any window size. -->
      <Viewbox Stretch="Uniform" StretchDirection="Both">
        <StackPanel x:Name="ContentPanel" Margin="20,10,20,10" VerticalAlignment="Center">
          <TextBlock x:Name="StatusText" FontFamily="Segoe UI Emoji" FontSize="44" FontWeight="Bold"
                     Foreground="White" HorizontalAlignment="Center" Text="..."/>
          <StackPanel x:Name="BadgeRow" Orientation="Horizontal" HorizontalAlignment="Center"
                      Margin="0,10,0,0" Visibility="Collapsed">
            <Border x:Name="MicBadge" CornerRadius="14" Padding="18,8" Margin="0,0,12,0" Background="#B71C1C">
              <TextBlock x:Name="MicBadgeText" FontFamily="Segoe UI Emoji" FontSize="26" FontWeight="Bold"
                         Foreground="White" Text=""/>
            </Border>
            <Border x:Name="CamBadge" CornerRadius="14" Padding="18,8" Background="#1565C0">
              <TextBlock x:Name="CamBadgeText" FontFamily="Segoe UI Emoji" FontSize="26" FontWeight="Bold"
                         Foreground="White" Text=""/>
            </Border>
          </StackPanel>
          <TextBlock x:Name="DetailText" FontFamily="Segoe UI" FontSize="20"
                     Foreground="#CCFFFFFF" HorizontalAlignment="Center" Margin="0,8,0,0" Text=""
                     TextWrapping="Wrap" TextAlignment="Center" Visibility="Collapsed"/>
        </StackPanel>
      </Viewbox>
    </Border>
    <!-- Invisible grab strips along every edge / corner: drag to resize. They sit on top of
         the badge, so the outer 8px resize and everything inside still drags the window. -->
    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="8"/><ColumnDefinition Width="*"/><ColumnDefinition Width="8"/>
      </Grid.ColumnDefinitions>
      <Grid.RowDefinitions>
        <RowDefinition Height="8"/><RowDefinition Height="*"/><RowDefinition Height="8"/>
      </Grid.RowDefinitions>
      <Thumb x:Name="GripNW" Grid.Row="0" Grid.Column="0" Cursor="SizeNWSE" Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripN"  Grid.Row="0" Grid.Column="1" Cursor="SizeNS"   Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripNE" Grid.Row="0" Grid.Column="2" Cursor="SizeNESW" Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripW"  Grid.Row="1" Grid.Column="0" Cursor="SizeWE"   Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripE"  Grid.Row="1" Grid.Column="2" Cursor="SizeWE"   Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripSW" Grid.Row="2" Grid.Column="0" Cursor="SizeNESW" Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripS"  Grid.Row="2" Grid.Column="1" Cursor="SizeNS"   Background="Transparent" Opacity="0"/>
      <Thumb x:Name="GripSE" Grid.Row="2" Grid.Column="2" Cursor="SizeNWSE" Background="Transparent" Opacity="0"/>
    </Grid>
  </Grid>
</Window>
'@

$window = [Windows.Markup.XamlReader]::Parse($xaml)
$script:Root         = $window.FindName('Root')
$script:StatusText   = $window.FindName('StatusText')
$script:BadgeRow     = $window.FindName('BadgeRow')
$script:MicBadge     = $window.FindName('MicBadge')
$script:MicBadgeText = $window.FindName('MicBadgeText')
$script:CamBadge     = $window.FindName('CamBadge')
$script:CamBadgeText = $window.FindName('CamBadgeText')
$script:DetailText   = $window.FindName('DetailText')
$script:ContentPanel = $window.FindName('ContentPanel')

# The controls reminder lives in a hover tooltip, so nothing is drawn on the badge itself.
# Set here rather than in the XAML attribute: one line per control reads better than a long row.
$window.ToolTip = @(
    'drag to move'
    'drag edges to resize'
    'double-click to force IN A CALL'
    'right-click to quit'
) -join "`n"

$script:manualOverride = $false
$script:Brush = [Windows.Media.BrushConverter]::new()

function Update-Status {
    $micApps    = @(Get-CapabilityAppsInUse 'microphone')
    $camApps    = @(Get-CapabilityAppsInUse 'webcam')
    $deviceMute = [MicState]::GetDefaultMicMute()

    $discordOnMic  = @($micApps | Where-Object { $_ -match '(?i)discord' }).Count -gt 0
    $othersOnMic   = @($micApps | Where-Object { $_ -notmatch '(?i)discord' }).Count -gt 0
    # Game-only mic: Valorant is the sole mic user (yellow, not red)
    $gameOnlyMic   = $micApps.Count -gt 0 -and
                     @($micApps | Where-Object { $_ -notmatch '(?i)^valorant' }).Count -eq 0
    $discordMute   = if ($discordOnMic) { Get-DiscordMute } else { $null }

    $micInUse = $micApps.Count -gt 0
    $camOn    = $camApps.Count -gt 0
    # Muted if the device itself is muted, or Discord is the only mic user and is muted/deafened in-app
    $micMuted = ($deviceMute -eq $true) -or (($discordMute -eq $true) -and -not $othersOnMic)
    $micLive  = $micInUse -and -not $micMuted
    $inCall   = $micInUse -or $camOn -or $script:manualOverride

    if ($inCall) {
        Set-BadgeRowVisible $true

        # Overall colour + headline: worst-case state
        if ($micLive -and $gameOnlyMic) {
            $script:Root.Background = $script:Brush.ConvertFromString('#F9A825')   # yellow - game voice only
            $script:StatusText.Text = "$emoGame  GAME MIC"
        } elseif ($micLive) {
            $script:Root.Background = $script:Brush.ConvertFromString('#D32F2F')   # red - hot mic
            $script:StatusText.Text = "$emoRed  IN A CALL"
        } elseif ($micInUse) {
            $script:Root.Background = $script:Brush.ConvertFromString('#E65100')   # amber - muted
            $script:StatusText.Text = "$emoMuted  ON MUTE"
        } elseif ($camOn) {
            $script:Root.Background = $script:Brush.ConvertFromString('#0D47A1')   # blue - camera only
            $script:StatusText.Text = "$emoCam  ON CAMERA"
        } else {
            $script:Root.Background = $script:Brush.ConvertFromString('#D32F2F')   # manual override
            $script:StatusText.Text = "$emoRed  IN A CALL"
        }

        # MIC badge
        if ($micLive) {
            $script:MicBadge.Background = $script:Brush.ConvertFromString('#7F0000')
            $script:MicBadge.Opacity = 1.0
            $script:MicBadgeText.Text = "$emoMic MIC ON"
        } elseif ($micInUse) {
            $script:MicBadge.Background = $script:Brush.ConvertFromString('#37474F')
            $script:MicBadge.Opacity = 1.0
            $script:MicBadgeText.Text = "$emoMuted MIC MUTED"
        } else {
            $script:MicBadge.Background = $script:Brush.ConvertFromString('#263238')
            $script:MicBadge.Opacity = 0.55
            $script:MicBadgeText.Text = "$emoMic MIC OFF"
        }

        # CAM badge
        if ($camOn) {
            $script:CamBadge.Background = $script:Brush.ConvertFromString('#1565C0')
            $script:CamBadge.Opacity = 1.0
            $script:CamBadgeText.Text = "$emoCam CAM ON"
        } else {
            $script:CamBadge.Background = $script:Brush.ConvertFromString('#263238')
            $script:CamBadge.Opacity = 0.55
            $script:CamBadgeText.Text = "$emoCam CAM OFF"
        }

        # Detail line
        $parts = @()
        if ($micInUse) {
            $micLabel = "mic: $(Get-FriendlyNames $micApps)"
            if ($micMuted) {
                if ($discordMute -eq $true -and -not ($deviceMute -eq $true)) { $micLabel += ' (muted in Discord)' }
                else { $micLabel += ' (device muted)' }
            }
            $parts += $micLabel
        }
        if ($camOn) { $parts += "cam: $(Get-FriendlyNames $camApps)" }
        if ($script:manualOverride -and $parts.Count -eq 0) { $parts += 'manual - double-click to clear' }
        $script:DetailInfo = $parts -join '   |   '
    } else {
        Set-BadgeRowVisible $false
        $script:Root.Background = $script:Brush.ConvertFromString('#2E7D32')
        $script:StatusText.Text = "$emoGreen  FREE"
        $script:DetailInfo = ''
    }
    Set-DetailLine
    # The block is shrink-wrapped around its content, so any change of text or badge state needs
    # a fresh spacing pass - including the badge labels, which are set after the row is shown.
    $sig = @($script:StatusText.Text, $script:DetailText.Text, $script:MicBadgeText.Text,
             $script:CamBadgeText.Text, $script:BadgeRow.Visibility,
             $script:DetailText.Visibility) -join '|'
    if ($sig -ne $script:LastTextSig) {
        $script:LastTextSig = $sig
        Update-Spacing
    }
    $window.Topmost = $true   # reassert in case another window grabbed it
}

# ---------- position persistence (remembers screen + spot across restarts) ----------
$script:PosPath = Join-Path $PSScriptRoot 'call-status-position.json'

function Save-Position {
    try {
        [pscustomobject]@{ Left = $window.Left; Top = $window.Top
                           Width = $window.Width; Height = $window.Height } |
            ConvertTo-Json | Set-Content $script:PosPath -Encoding UTF8
    } catch {}
}

function Restore-Position {
    $wa = [System.Windows.SystemParameters]::WorkArea
    $default = $true
    if (Test-Path $script:PosPath) {
        try {
            $pos = Get-Content $script:PosPath -Raw | ConvertFrom-Json
            $vL = [System.Windows.SystemParameters]::VirtualScreenLeft
            $vT = [System.Windows.SystemParameters]::VirtualScreenTop
            $vW = [System.Windows.SystemParameters]::VirtualScreenWidth
            $vH = [System.Windows.SystemParameters]::VirtualScreenHeight
            # Size first, so the default top-right placement below uses the restored width
            if ($null -ne $pos.Width -and $pos.Width -ge $window.MinWidth -and $pos.Width -le $vW) {
                $window.Width = [double]$pos.Width
            }
            if ($null -ne $pos.Height -and $pos.Height -ge $window.MinHeight -and $pos.Height -le $vH) {
                $window.Height = [double]$pos.Height
            }
            # Only restore if the saved spot is still on a connected screen (virtual desktop)
            if ($pos.Left -ge ($vL - 50) -and $pos.Left -le ($vL + $vW - 100) -and
                $pos.Top  -ge ($vT - 50) -and $pos.Top  -le ($vT + $vH - 100)) {
                $window.Left = $pos.Left
                $window.Top  = $pos.Top
                $default = $false
            }
        } catch {}
    }
    if ($default) {
        $window.Left = $wa.Right - $window.Width - 16
        $window.Top  = $wa.Top + 16
    }
}

$window.Add_MouseLeftButtonDown({
    try {
        $window.DragMove()   # blocks until the mouse button is released
        Save-Position
    } catch {}
})
# ---------- adaptive spacing ----------
# The badge sits in a Viewbox, so it scales uniformly - which strands dead space whenever the
# window's aspect ratio differs from the content's (a squarish window left a small block floating
# in the middle). Growing the padding and the row gaps until the content's ratio matches the
# window's turns that dead space into even spacing, and lets the text scale up to fill.
$script:BaseMargins = @{
    Panel  = [Windows.Thickness]::new(20, 10, 20, 10)
    Badges = [Windows.Thickness]::new(0, 10, 0, 0)
    Detail = [Windows.Thickness]::new(0, 8, 0, 0)
}

# The controls reminder is worth a line of space only while the pointer is on the badge; the
# rest of the time the detail line carries call info, or collapses out of the layout entirely.
$script:DetailInfo = ''

function Set-DetailLine {
    # Call info only - the controls hint is an overlay now, so it never moves this line around.
    $t = $script:DetailInfo
    $script:DetailText.Text = $t
    $script:DetailText.Visibility = if ([string]::IsNullOrWhiteSpace($t)) { 'Collapsed' } else { 'Visible' }
}

function Set-BadgeRowVisible {
    param([bool]$Visible)
    # Collapsed, not Hidden - a hidden row still reserves its full height, which showed up as a
    # dead band in the middle of the badge whenever there was no call in progress.
    $v = if ($Visible) { 'Visible' } else { 'Collapsed' }
    # Update-Status re-runs Update-Spacing once the badge labels are set, so no pass here:
    # measuring now would size the block around the previous state's labels.
    $script:BadgeRow.Visibility = $v
}

function Update-Spacing {
    if ($window.ActualWidth -le 0 -or $window.ActualHeight -le 0) { return }

    $inf  = [double]::PositiveInfinity
    $huge = [Windows.Size]::new($inf, $inf)

    # Shrink-wrap the block to its widest real row. The Viewbox's scale comes off the natural
    # width, so slack in there (the old fixed 568px bar, sized for a 640-wide strip) was coming
    # straight off the font size - the headline only ever filled about 60% of it.
    $badgesShown = $script:BadgeRow.Visibility -eq 'Visible'
    $script:StatusText.Measure($huge)
    $script:BadgeRow.Measure($huge)      # zero-wide while collapsed
    $contentW = [Math]::Max($script:StatusText.DesiredSize.Width, $script:BadgeRow.DesiredSize.Width)
    $contentW = [Math]::Max($contentW, 200.0)
    # A long detail line may widen the block, but only so far - past that it wraps, rather than
    # dragging the whole badge's scale down with it.
    $detailShown = $script:DetailText.Visibility -eq 'Visible'
    if ($detailShown) {
        $script:DetailText.Width = [double]::NaN   # Auto, so the measure below is its natural width
        $script:DetailText.Measure($huge)
        $dw = $script:DetailText.DesiredSize.Width
        if ($dw -gt $contentW) { $contentW = [Math]::Min($dw, $contentW * 1.8) }
    }
    $script:ContentPanel.Width = $contentW
    $script:DetailText.Width   = $contentW

    # Measure at the stock margins: that natural size is what the surplus is measured against,
    # and it differs between the in-call and idle layouts.
    $script:ContentPanel.Margin = $script:BaseMargins.Panel
    $script:BadgeRow.Margin     = $script:BaseMargins.Badges
    $script:DetailText.Margin   = $script:BaseMargins.Detail
    $script:ContentPanel.InvalidateMeasure()
    $script:ContentPanel.Measure($huge)
    $bw = $script:ContentPanel.DesiredSize.Width    # DesiredSize includes the margins
    $bh = $script:ContentPanel.DesiredSize.Height
    if ($bw -le 0 -or $bh -le 0) { return }

    $aspect = $window.ActualWidth / $window.ActualHeight
    $mx        = $script:BaseMargins.Panel.Left
    $top       = $script:BaseMargins.Panel.Top
    $bottom    = $script:BaseMargins.Panel.Bottom
    $gapBadges = $script:BaseMargins.Badges.Top
    $gapDetail = $script:BaseMargins.Detail.Top

    if ($aspect -gt ($bw / $bh)) {
        # Window is wider than the content: pad the sides, so the text grows to the full height
        $extra = [Math]::Min(($bh * $aspect) - $bw, $bw * 3)
        $mx = $mx + ($extra / 2)
    } else {
        # Window is taller: share the surplus height evenly over the gaps that actually exist -
        # a collapsed badge row or detail line has no gap of its own - so the rows sit at even
        # intervals instead of leaving one big void in the middle.
        $extra = [Math]::Min(($bw / $aspect) - $bh, $bh * 3)
        $gaps = 2                                    # top and bottom padding are always there
        if ($badgesShown) { $gaps = $gaps + 1 }
        if ($detailShown) { $gaps = $gaps + 1 }
        $each = $extra / $gaps
        $top = $top + $each; $bottom = $bottom + $each
        if ($badgesShown) { $gapBadges = $gapBadges + $each }
        if ($detailShown) { $gapDetail = $gapDetail + $each }
    }
    $script:ContentPanel.Margin = [Windows.Thickness]::new($mx, $top, $mx, $bottom)
    $script:BadgeRow.Margin     = [Windows.Thickness]::new(0, $gapBadges, 0, 0)
    $script:DetailText.Margin   = [Windows.Thickness]::new(0, $gapDetail, 0, 0)

    # Corners live outside the Viewbox, so scale them by hand (0.127 keeps the stock 28 at 640x220)
    $r = 0.127 * [Math]::Min($window.ActualWidth, $window.ActualHeight)
    $r = [Math]::Max(10.0, [Math]::Min(48.0, $r))
    $script:Root.CornerRadius = [Windows.CornerRadius]::new($r)
}

$window.Add_SizeChanged({ Update-Spacing })

# ---------- resizing (drag any edge or corner) ----------
function Set-WindowEdge {
    param([string]$Edge, [double]$dx, [double]$dy)
    if ($Edge -match 'W') {
        $w = $window.Width - $dx
        if ($w -ge $window.MinWidth) { $window.Width = $w; $window.Left = $window.Left + $dx }
    } elseif ($Edge -match 'E') {
        $w = $window.Width + $dx
        if ($w -ge $window.MinWidth) { $window.Width = $w }
    }
    if ($Edge -match 'N') {
        $h = $window.Height - $dy
        if ($h -ge $window.MinHeight) { $window.Height = $h; $window.Top = $window.Top + $dy }
    } elseif ($Edge -match 'S') {
        $h = $window.Height + $dy
        if ($h -ge $window.MinHeight) { $window.Height = $h }
    }
}

foreach ($edge in 'NW','N','NE','W','E','SW','S','SE') {
    $grip = $window.FindName("Grip$edge")
    if ($null -eq $grip) { continue }
    $grip.Tag = $edge   # read back off the sender, so each handler knows its own edge
    $grip.Add_DragDelta({ param($src, $ev) Set-WindowEdge -Edge $src.Tag -dx $ev.HorizontalChange -dy $ev.VerticalChange })
    $grip.Add_DragCompleted({ Save-Position })
}

$window.Add_MouseRightButtonUp({ $window.Close() })
$window.Add_MouseDoubleClick({
    $script:manualOverride = -not $script:manualOverride
    Update-Status
})

$window.Add_Loaded({
    Restore-Position
    Update-Spacing
    Update-Status
})

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(2)
$timer.Add_Tick({ Update-Status })
$timer.Start()

$null = $window.ShowDialog()

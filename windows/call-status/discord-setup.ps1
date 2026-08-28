# One-time setup: authorize the call-status popup to read your Discord mute state.
# Prerequisite (2 minutes, free): create a Discord application at
#   https://discord.com/developers/applications
#   1. New Application -> name it anything (e.g. "Call Status") -> Create
#   2. OAuth2 page (left sidebar): copy the Client ID; click Reset Secret and copy the Client Secret
#   3. OAuth2 -> Redirects: add  http://127.0.0.1  and Save Changes
# Then run this script (Discord must be running), enter both values,
# and click Authorize in the popup that appears inside Discord.

param([string]$ClientId, [string]$ClientSecret)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072  # TLS 1.2
Add-Type -Path (Join-Path $PSScriptRoot 'DiscordRpc.cs')

Write-Host ''
Write-Host '=== Discord mute detection setup ===' -ForegroundColor Cyan
Write-Host 'If you have not created a Discord application yet, see the notes at the top of this script.'
Write-Host ''
if (-not $ClientId)     { $ClientId     = (Read-Host 'Client ID').Trim() }
if (-not $ClientSecret) { $ClientSecret = (Read-Host 'Client Secret').Trim() }

$rpc = New-Object DiscordRpc
if (-not $rpc.Connect($ClientId)) {
    Write-Host 'Could not reach the Discord desktop app. Is Discord running?' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Connected. Now switch to Discord and click AUTHORIZE in the popup it is showing...' -ForegroundColor Yellow
$req  = '{"cmd":"AUTHORIZE","args":{"client_id":"' + $ClientId + '","scopes":["rpc","rpc.voice.read"]},"nonce":"' + [guid]::NewGuid() + '"}'
$resp = $rpc.Request($req, 300000)   # wait up to 5 minutes for the click
if (-not $resp) { Write-Host 'No response - timed out or Discord closed.' -ForegroundColor Red; exit 1 }
$j = $resp | ConvertFrom-Json
if ($j.evt -eq 'ERROR') { Write-Host "Discord said: $($j.data.message)" -ForegroundColor Red; exit 1 }
$code = $j.data.code

Write-Host 'Authorized. Exchanging the code for a token...'
$tok = Invoke-RestMethod -Method Post -Uri 'https://discord.com/api/oauth2/token' -TimeoutSec 15 `
    -ContentType 'application/x-www-form-urlencoded' -Body @{
        client_id     = $ClientId
        client_secret = $ClientSecret
        grant_type    = 'authorization_code'
        code          = $code
        redirect_uri  = 'http://127.0.0.1'
    }

# Verify the token works and read the current voice state
$auth = $rpc.Request('{"cmd":"AUTHENTICATE","args":{"access_token":"' + $tok.access_token + '"},"nonce":"' + [guid]::NewGuid() + '"}', 5000)
if (-not $auth -or ($auth | ConvertFrom-Json).evt -eq 'ERROR') {
    Write-Host 'Token verification failed.' -ForegroundColor Red
    exit 1
}
$vs = $rpc.Request('{"cmd":"GET_VOICE_SETTINGS","args":{},"nonce":"' + [guid]::NewGuid() + '"}', 5000) | ConvertFrom-Json
$rpc.Close()

[pscustomobject]@{
    client_id     = $ClientId
    client_secret = $ClientSecret
    access_token  = $tok.access_token
    refresh_token = $tok.refresh_token
} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'call-status-config.json') -Encoding UTF8

Write-Host ''
Write-Host "Success! Discord currently reports: muted = $($vs.data.mute), deafened = $($vs.data.deaf)" -ForegroundColor Green
Write-Host 'The popup will pick this up automatically within about 20 seconds of your next Discord call.'

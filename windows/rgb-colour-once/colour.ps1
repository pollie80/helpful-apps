# Change the RGB colour without a UAC prompt: saves it to rgb-config.json, then runs the
# already-elevated "RGB Colour Once" task. Usage: colour.ps1 FF0000  (no argument = re-apply)
param([string]$Color)

$file = Join-Path $PSScriptRoot 'rgb-config.json'
if ($Color) {
    $Color = $Color.Trim().TrimStart('#').ToUpper()
    if ($Color -notmatch '^[0-9A-F]{6}$') { throw "Colour must be 6 hex digits, e.g. 4917FF" }
    # Edit just the colour line so the rest of the file keeps its formatting.
    $json = Get-Content $file -Raw
    $json = [regex]::Replace($json, '("color"\s*:\s*")[^"]*(")', "`${1}$Color`${2}")
    [IO.File]::WriteAllText($file, $json)
}
Start-ScheduledTask 'RGB Colour Once'

# rgb-colour-once

Set all your PC's RGB to one colour at logon, then get out of the way.

Apps like SignalRGB run all day and re-send colours to every device many times a second, even when
the "effect" is a single solid colour. If all you want is one colour, that is wasted CPU, GPU and
USB traffic. This sets the colour once through [OpenRGB](https://openrgb.org/) and exits. Each
device's controller keeps the colour until power-off, and the task sets it again at the next logon.

## What happens at logon

1. The elevated scheduled task `RGB Colour Once` runs `set-rgb.ps1` in a hidden console
   (`conhost --headless`), so there is no window and no UAC prompt.
2. It stops SignalRGB if it is running, so it releases the SMBus.
3. It starts the OpenRGB service, which detects the hardware. RAM needs the PawnIO driver for this.
4. It waits for every device listed in `expected`. If one is missing (USB devices can still be
   loading right after logon), it restarts the service to rescan, up to `waitMinutes`.
5. It sets each device to the colour, except devices matching `skip`.
6. It stops the OpenRGB service. Nothing keeps running.

Each run is logged to `last-run.log`.

## Setup

1. Install OpenRGB 1.1 or later. It pulls in the signed PawnIO driver it needs for RAM/SMBus:
   ```
   winget install OpenRGB.OpenRGB
   ```
2. Copy `rgb-config.example.json` to `rgb-config.json` and edit it for your hardware (see below).
   `install.ps1` copies the example for you if you skip this.
3. Run `install.ps1` once from an **elevated** PowerShell. It:
   - registers the logon task
   - sets the OpenRGB service to Manual (the installer makes it Automatic)
   - removes SignalRGB's autostart entry, backing it up to `signalrgb-run-backup.txt`, and sets its
     service to Manual. SignalRGB stays installed and still works if you open it by hand.
4. Test it without logging out:
   ```
   powershell -ExecutionPolicy Bypass -File colour.ps1
   ```

## Changing the colour

```
powershell -ExecutionPolicy Bypass -File colour.ps1 FF0000
```

This saves the colour to `rgb-config.json` and runs the already-elevated task, so there is no UAC
prompt. The saved colour is the one used at the next logon.

## rgb-config.json

| Key | Meaning |
| --- | --- |
| `color` | Hex colour, no `#`. |
| `skip` | Device-name substrings to leave alone, e.g. a mouse another app (G HUB) controls. |
| `expected` | Device-name substring → how many must be detected before colouring. Leave `{}` to colour whatever shows up first. |
| `devices` | Per-device rules. The first rule whose `match` is in the name wins. `mode` is the OpenRGB mode; `zoneSizes` resizes addressable zones (zone index → LED count). |
| `defaultMode` | Mode for devices with no rule. `Direct` works on almost everything. |
| `waitMinutes` | How long to keep rescanning for missing `expected` devices. |

To find device names, modes and zones, stop the task's service if it is running, then:

```
net start OpenRGB
"C:\Program Files\OpenRGB\OpenRGB.exe" --list-detailed
net stop OpenRGB
```

Run this from an elevated `cmd` window. OpenRGB is a GUI program and prints to the console it was
started from, so a redirected or piped output comes back empty.

Prefer `Static` where a device offers it: it is a hardware effect, so it survives better than
`Direct`. Some GPUs and keyboards only offer `Direct`, which still holds until power-off.

The example config is for 2× ENE RAM, an MSI RTX 4070 SUPER, an MSI B650 board with two 60-LED
strips on `JRAINBOW1/2` (zones 2 and 3), and a SteelSeries Apex 3 TKL, with a Logitech mouse skipped.

## Undo

```
schtasks /Delete /TN "RGB Colour Once" /F
```

Then put SignalRGB back in startup from its own settings (or re-add the line in
`signalrgb-run-backup.txt` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).

# PS4 Controller Battery Tray

Puts the DualShock 4's battery percentage in the Windows system tray as a plain number, so you
find out the controller is dying before it dies. Windows does not surface this anywhere for a
DS4 - the Settings battery reading is for Xbox pads.

## Why it reads the controller directly

It talks to the pad over HID and pulls the battery byte out of the input report, over Bluetooth
or USB. That means it coexists with DS4Windows rather than fighting it - it only reads, and it
does not claim the device exclusively.

## Setup

```
pip install -r requirements.txt
```

Run it with `pythonw.exe` rather than `python.exe` so you do not get a console window:

```
pythonw ps4_battery_tray.py
```

To start it with Windows, put a shortcut to that command in `shell:startup`.

## Configuring

- `CONTROLLER_NAMES` maps a controller's Bluetooth MAC to a friendly label, for telling two pads
  apart. Unknown controllers show their raw ID, so run it once to find the MAC you want to name.
- `POLL_SECONDS` (default 30) - how often to re-read the battery.
- `PIDS` covers DS4 v1, DS4 v2, and the Sony wireless adapter.

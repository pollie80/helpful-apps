# helpful-apps

Small desktop utilities that each fix one specific annoyance. Nothing here needs a server, an
account, or a subscription - they are single files or single projects you run locally.

One folder per platform, one folder per app inside it. Every app has its own README.

## windows

| App | What it is for |
| --- | --- |
| [call-status](windows/call-status) | An always-on-top badge showing whether your mic or camera is live, so people can see when you are in a call. Detects device mute and Discord's in-app mute too. |
| [ps4-controller-battery-tray](windows/ps4-controller-battery-tray) | The DualShock 4's battery percentage in the system tray, which Windows otherwise will not show you. Reads the pad directly over HID. |
| [stutter-doctor](windows/stutter-doctor) | Works out *why* a game hitches, instead of leaving you guessing. Records what the machine was doing around each freeze and names the cause. |

## apple

| App | What it is for |
| --- | --- |
| [claude-usage-bar](apple/claude-usage-bar) | Your Claude subscription usage in the macOS menu bar as a live percentage. Reads the real numbers from the same endpoint Claude Code's `/usage` panel uses, rather than estimating from local logs, and lets you pick which limit window the number tracks. |

## skills

Procedures rather than programs - see [skills/](skills).

| Skill | What it is for |
| --- | --- |
| [game-network-priority](skills/game-network-priority) | Why a competitive game loses packets while something is downloading, and the fixes that actually work. |

## Conventions

- Anything holding a credential is gitignored and ships as a `.example` file instead.
- Build output (`bin/`, `obj/`, `__pycache__/`) stays out of the repo.

Licensed under the [MIT License](LICENSE).

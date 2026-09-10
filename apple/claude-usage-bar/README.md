# Claude Usage Bar

Your Claude subscription usage in the macOS menu bar, as a live percentage. The numbers are the
real ones the Claude Code `/usage` panel shows - not an estimate reconstructed from local
transcripts, which is what most usage monitors do.

```
◔ 29%   <- click ->  Your usage limits - Team
                     5-hour limit          Resets in 4 hr 50 min    9%
                     Weekly - all models   Resets in 17 hr         29%
                     Weekly - Fable        Resets in 17 hr         35%

                     Menu bar shows  [Weekly - all models  v]
                     Refresh every   [1 minute             v]
```

One percentage sits in the menu bar. Which one is up to you, because the interesting window
changes depending on how you work - the 5-hour session cap if you burst, the weekly cap if you
grind, or a single model's weekly cap if that is the one you keep hitting.

## Why not estimate from transcripts

Claude Code writes every turn's token counts to `~/.claude/projects/**/*.jsonl`, and tools like
`ccusage` add those up to infer cost and a rolling session window. That is a good estimate, but
it is still an estimate, and it cannot see usage from any other client, machine, or teammate on
a shared plan.

Your actual subscription limits live server side, so this app asks the server:

```
GET https://api.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=1
    Authorization: Bearer <oauth access token>
    anthropic-beta: oauth-2025-04-20
```

The response has per-window objects at the top level (`five_hour`, `seven_day`, and a set of
server-side model buckets), plus a `limits[]` array which is the display list the `/usage` panel
itself renders - one entry per bar:

```json
{"kind":"session",       "group":"session", "percent":9,  "resets_at":"...", "scope":null}
{"kind":"weekly_all",    "group":"weekly",  "percent":29, "resets_at":"...", "scope":null}
{"kind":"weekly_scoped", "group":"weekly",  "percent":35, "resets_at":"...",
 "scope":{"model":{"display_name":"Fable"}}, "is_active":true}
```

The UI is built from `limits[]` rather than from the fixed top-level keys. That way a new model
bucket appears on its own, and a `kind` this build has never seen still gets a row with a
readable label instead of silently vanishing. The top-level `five_hour` / `seven_day` pair is a
fallback for if `limits` ever stops being sent.

## Credentials

The OAuth token is read from the login keychain - service `Claude Code-credentials`, the same
item Claude Code itself writes. When it has expired, the app trades the stored refresh token at
`https://console.anthropic.com/v1/oauth/token` and writes the rotated pair back to the keychain,
which is exactly what Claude Code does, so the two stay in step.

Nothing is sent anywhere except those two Anthropic endpoints, and no local transcripts are read.
There is no config file and no token of its own to leak.

## Build and install

```bash
./build.sh
cp -R build/ClaudeUsageBar.app /Applications/
open /Applications/ClaudeUsageBar.app
```

Needs the Xcode Command Line Tools (`xcode-select --install`) and nothing else. `build.sh` calls
`swiftc` directly rather than going through SwiftPM, because SwiftPM wraps manifest evaluation in
its own `sandbox-exec`, which cannot nest inside an already-sandboxed shell.

**On first launch macOS asks** *"ClaudeUsageBar wants to use your confidential information stored
in Claude Code-credentials"*. Enter your macOS login password and choose **Always Allow**, or you
will be asked again on every poll. The bundle is ad-hoc signed with a stable identifier
(`local.claude-usage-bar`) so the grant survives rebuilds. Until you allow it, the popover shows
a keychain error where the bars should be.

## Launch at login

Tick **Launch at login** in the popover. It is backed by `SMAppService`, and the state is read
back from the system each time the popover opens, so the checkbox stays truthful if you turn the
login item off in System Settings instead of here.

Registration only works from a stable location, so copy the app to `/Applications` first -
otherwise the toggle tells you that rather than failing quietly. macOS may also hold a new login
item behind approval, and the popover then offers a button through to the Login Items pane.

## Using it

- **Menu bar shows** picks which window drives the menu bar number. Clicking a row in the popover
  selects it too. The choice is remembered.
- **Refresh every** sets the poll interval: 30 seconds, 1, 5 or 15 minutes. It also polls once at
  launch, and **Refresh now** forces one.
- The number turns amber past 75% and red past 90%. If the server raises a severity of its own on
  a window, that wins over the thresholds.
- A poll that fails after an earlier success leaves the last known numbers on screen with a
  warning line, rather than quietly going stale.

## Known limits

- **No context-window row.** The `Context window 65.6k / 1M` line in Claude Code's own panel is
  per-session state inside a running session, not account state, so there is nothing for an
  outside app to read.
- **`/api/oauth/usage` is not a documented public API.** It is what the official client uses, and
  it can change without notice. If it does, `UsageAPI.swift` is the only file that needs
  adjusting - the response decoding is all in one place.
- **Subscription accounts only.** On an API key or Bedrock/Vertex setup the endpoint reports no
  windows at all, and the popover says so instead of showing a misleading 0%.
- **Apple silicon only** as written. `build.sh` targets `arm64-apple-macos14.0`; change the
  target for an Intel build.

## Layout

| File | Role |
| --- | --- |
| `Sources/ClaudeUsageBar/Keychain.swift` | Read and update the `Claude Code-credentials` keychain item |
| `Sources/ClaudeUsageBar/UsageAPI.swift` | Wire types, token refresh, flattening the response into display rows |
| `Sources/ClaudeUsageBar/AppState.swift` | Poll timer, remembered preferences, the menu bar title |
| `Sources/ClaudeUsageBar/UsagePopover.swift` | The popover UI |
| `Sources/ClaudeUsageBar/ClaudeUsageBarApp.swift` | `MenuBarExtra` entry point |
| `build.sh` | Compile, assemble the `.app`, ad-hoc sign it |

# GameRamCap

Tray app for Windows 10/11. While a game is in the foreground, it puts a **hard ceiling on the resident RAM (working set)** of background apps you list (Discord, Claude, Steam, Spotify by default). When you leave the game, every original limit is restored.

It does not clean memory periodically. It uses the documented `SetProcessWorkingSetSizeEx` API with the hard-max flag, per process.

## Modes

**Smart (default)** - no hard caps, so nothing can be starved or crash:

- By default (`LimitWhenRamFree: false`) it acts only when free RAM drops below `PressureFreePercent` (25%). Set it to `true` to keep background apps in budget all the time (more aggressive, more page faults).
- Fullscreen/borderless apps seen in front are remembered as games and are never trimmed or capped while they run, even after Alt+Tab.
- Apps that refused a trim are retried every 5 minutes instead of being ignored for the whole session.
- `explorer` and recording tools (OBS, NVIDIA Share) are protected by default.
- Under pressure, trims background apps softly (pages move to the standby list; the app takes them back instantly if needed, no disk).
- The budget is per **app** (all its processes together). It is `BudgetMB`, or `MaxCeilingMB` when 0.
- Never touches the focused app, an app you left in the last `GraceAfterFocusSeconds`, busy processes, or `Games`.
- Learns: if an app pulls its pages back right away, its budget grows and the cooldown doubles; when it stays quiet, the budget returns to normal.
- `HoldBackground` (default `true`): after a background app is trimmed it also gets a hard ceiling (its `CeilingMB` per process) so it cannot grow back; focusing the app lifts the ceiling at once. Set `false` for pure soft trimming.
- Below `CriticalFreePercent` (10%) trimming is stronger (budget halved, down to `MinCeilingMB`).

**Hard** - the old behaviour: fixed hard per-process ceilings. Set `"Mode": "Hard"`.

## Choosing the mode (tray menu)

Right-click the tray icon:

- **Mode** > Smart / Hard - switch the whole behaviour live.
- **Intensity** > Ultra Performance ... Ultra Safe, or **Custom** (uses the numbers in config.json).
- **Options** > Focus mode, Limit all apps, Hold background apps, Only limit when RAM is low, Auto-detect fullscreen games.

**Intensity > Edit levels...** opens a table with the five levels so you can change each one's check interval, ceiling per app, cooldown, grace, budget scale, minimum process size and critical factor. Only the values you change are stored (`PresetOverrides` in config.json); "Reset all to defaults" clears them.

Every choice is saved to `config.json` and applied immediately (limits are released and re-applied with the new mode).

## Build

Requires the .NET 8 SDK.

    dotnet build -c Release
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

## Use

1. Run `GameRamCap.exe`. A tray icon appears (right-click: Enabled, Open config file, Reload config, Exit).
2. Edit `%APPDATA%\GameRamCap\config.json`. Saving the file reloads it automatically.

| Setting | Meaning |
|---|---|
| `LimitEverything` | **On by default.** Every app and game gets a ceiling (values from `Default*`; apps in `Targets` keep their own numbers), except the one in focus, protected system processes and `Exceptions`. |
| `Exceptions` | Process names that are never limited. Easiest: tray > Choose apps... > **Exceptions...** (OBS, VMs, anything that must keep all its RAM). |
| `FocusMode` | **On by default.** Works for any app in `Targets`, no game needed: the app you are using (foreground window) is unlimited; every other target is capped. Click elsewhere and its cap returns. |
| `RelimitDelaySeconds` | Delay before the cap returns after you leave an app (default 1.5s, avoids flicker on Alt+Tab). |
| `Targets[].FocusAlso` | Extra process names that count as focusing that app (steamwebhelper already maps to `steam`). |
| `Games` | Process names (no `.exe`) that always count as a game. Find them in Task Manager > Details. |
| `AutoDetectFullscreen` | Any borderless/fullscreen foreground window counts as a game. |
| `FullscreenIgnore` | Fullscreen apps that are not games (browsers, video players). |
| `DelaySeconds` | How long the game must stay in front before limits apply. |
| `Targets[].CeilingMB` | Starting ceiling for that app. |
| `Targets[].Adaptive` | Ceiling grows when the app is busy at its limit and shrinks when idle, between `MinCeilingMB` and `MaxCeilingMB`. |

## Safety

- System processes, security services and common anti-cheat services are never touched (hard-coded list in `Engine.cs`).
- Do not list your game as a target.
- Hard limits live in the target process. Exit from the tray restores them. **End Task** cannot be caught by the app, so a hidden watchdog copy (`GameRamCap.exe --watchdog <pid>`, started detached) waits for the main app to die and then lifts every leftover ceiling. A crashed run is also cleaned up at the next start.
- Too low a ceiling makes apps page heavily (Discord voice can crackle). Keep `Adaptive` on and start with the defaults.
- Some elevated processes can only be limited if GameRamCap runs as Administrator.

## Upload to GitHub (button in Visual Studio)

In the Start dropdown (next to the green arrow) choose **Upload to GitHub**, then press **Ctrl+F5**.
It checks the git state, asks for a commit message, commits and pushes. On the first run it can create the GitHub repo for you.

For fully automatic auth set a token once: `setx GH_TOKEN "ghp_xxx"` and restart Visual Studio
(fine-grained token, Contents: Read and write; also Administration: write if it should create the repo).
Without a token, git's normal sign-in (Git Credential Manager) is used instead.

## Intensity levels (Smart mode + `HoldBackground`)

All levels act at all times. Check interval = 1s (Ultra Performance) ... 5s (Ultra Safe). The hard RAM ceiling is **per process** (the app total is this x its number of processes, split by what each process uses). No process is squeezed below `HoldMinProcessMB` (64) or `HoldKeepPercent` (60%) of its current size, so apps do not stutter:

| Level | Interval | Ceiling / process |
|---|---|---|
| Ultra Performance | 1 s | 20 MB |
| Performance | 2 s | 40 MB |
| Balanced | 3 s | 100 MB |
| Safe | 4 s | 250 MB |
| Ultra Safe | 5 s | 400 MB |

`Custom` profile keeps using each target's own `CeilingMB` per process.

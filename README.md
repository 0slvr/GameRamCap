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

**Hard** - fixed hard ceilings. Set `"Mode": "Hard"`. `HardScope` chooses how the limit applies:

- `"Process"` (**recommended**, default): every process of an app gets the limit separately.
- `"App"`: the limit is the total for the app and is split equally between its running processes (min 16 MB each, fixed, not adapted).

## Choosing the mode (tray menu)

Right-click the tray icon:

- **Enabled**, **Mode** (Smart with levels Ultra Performance ... Ultra Safe, or Hard per process / per app), **Settings...** (opens the window), **Start with Windows**, **Open log file**, **Exit**. The Custom level, the editors for levels and per-app limits live in the window.

Starting a second copy of the app opens the window of the running one.
- Mode (Smart / Hard) and all the option toggles are in the window, tab **Settings**.

**Intensity > Edit levels...** opens a table with the five levels so you can change each one's check interval, ceiling per app, cooldown, grace, budget scale, minimum process size and critical factor. Only the values you change are stored (`PresetOverrides` in config.json); "Reset all to defaults" clears them.

Every choice is saved to `config.json` and applied immediately (limits are released and re-applied with the new mode).

## Build

Requires the .NET 8 SDK.

    dotnet build -c Release
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

## Use

1. Run `GameRamCap.exe`. A tray icon appears (right-click for the menu above).
2. Edit `%APPDATA%\GameRamCap\config.json` (created on first run; Settings > advanced options opens it). Saving the file reloads it automatically. If the file is broken, a copy is kept as `config.broken-<date>.json` and the last good config (`config.lastgood.json`) is used.

| Setting | Meaning |
|---|---|
| `LimitEverything` | **On by default.** Every app and game gets a ceiling (values from `Default*`; apps in `Targets` keep their own numbers), except the one in focus, protected system processes and `Exceptions`. |
| `Exceptions` | Process names that are never limited. Easiest: Settings > **Games & lists** > Exceptions (OBS, VMs, anything that must keep all its RAM). |
| `FocusMode` | **Always on (not shown in the UI).** Works for any app in `Targets`, no game needed: the app you are using (foreground window) is unlimited; every other target is capped. Click elsewhere and its cap returns. |
| `RelimitDelaySeconds` | Delay before the cap returns after you leave an app (default 1.5s, avoids flicker on Alt+Tab). |
| `Targets[].FocusAlso` | Extra process names that count as focusing that app (steamwebhelper already maps to `steam`). |
| `Games` | Process names (no `.exe`) that always count as a game. Find them in Task Manager > Details. |
| `AutoDetectFullscreen` | Any borderless/fullscreen foreground window counts as a game. |
| `FullscreenIgnore` | Fullscreen apps that are not games (browsers, video players). |
| `DelaySeconds` | How long the game must stay in front before limits apply. |
| `Targets[].CeilingMB` | Starting ceiling for that app, applied to **each process** of the app separately (in Hard mode and Custom). You choose apps; the limit is per process. |
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

All levels act at all times. Check interval = 1s (Ultra Performance) ... 5s (Ultra Safe). The hard RAM ceiling is **per process** (the app total is this x its number of processes, split by what each process uses). No process is squeezed below `HoldMinProcessMB` (64) or `HoldKeepPercent` (60%) of its current size, so apps do not stutter. The floor depends on the level (Ultra Performance 8 MB / 1% ... Ultra Safe 48 MB / 50%; Custom uses config.json). Held processes also **adapt**: one that sits at its ceiling and keeps page-faulting gets +25% (up to `MaxCeilingMB`), and slides back after 30 quiet seconds:

| Level | Interval | Ceiling / process | Meaning |
|---|---|---|---|
| Ultra Performance | 1 s | 8 MB | lowest possible |
| Performance | 2 s | 20 MB | very low |
| Balanced | 3 s | 30 MB | low, still tuned by auto-squeeze |
| Safe | 4 s | 40 MB | more room |
| Ultra Safe | 5 s | 90 MB | most room |

`Custom` (tray > Intensity, or Settings > Intensity) keeps using each target's own `CeilingMB` per process, plus the "apps not in the list" limit (`Default*`). **Those per-app numbers (Apps tab) are used only with Custom (and Hard mode)**; with the five levels they are greyed out and ignored.

All columns of the level table work in hold mode: *Cooldown* = minimum seconds between two cap passes of an app, *Budget scale* multiplies the app target (ceiling x process count), *Min process* leaves smaller processes alone, *Critical factor* multiplies the target and lowers existing ceilings once when free RAM is below the level's critical %. **Only limit when RAM is low** (`LimitWhenRamFree: false`) makes every level wait until free RAM is below its pressure % (Ultra Performance 60% ... Ultra Safe 12%).

## Auto-squeeze (default on)

Goal: the lowest RAM each background process can live on **without stutter**. On top of the level's starting ceiling,
every held process is probed: quiet for `AutoProbeSeconds` (10 s) -> ceiling lowered one step (10%, or straight to
its real use + 25% if the ceiling is not binding), down to `AutoMinProcessMB` (16). At its ceiling and page-faulting
more than `AutoFaultRate` (150/s) -> ceiling raised 25% and that level is kept as a floor for 5 minutes, then probed again.
It never grows past what the process used before it was held (+20%). Toggle: Settings > Auto-squeeze.
Focusing an app lifts its ceiling at once (the first moments after returning to it reload pages, that is unavoidable).

## Smarter limiting (Smart mode)

- **Audio protection** (`AudioProtect`, default on; Settings > Basics): only the single **process** that owns an active audio stream (the audio service of Discord/Chrome, the player of Spotify) is left alone, plus `AudioProtectGraceSeconds` (8) after the sound stops. The other processes of the same app are limited as usual, so apps that play sound are not exempt as a whole. Detected through the Windows audio sessions.
- **Low memory priority** (`LowerMemoryPriority`, default on; `BackgroundMemoryPriority` 1-4, default 2): background apps get a lower Windows memory priority. Nothing is capped; when RAM is short Windows takes their pages first. Focusing the app gives its original priority back at once. An app that already set an equal or lower priority itself is left alone. After End Task on GameRamCap the low priority stays on those apps until they restart; it only matters when RAM is short.
- **Biggest first**: for each background app the target is `HoldMB x process count`. The biggest processes are squeezed first, each only as far as needed, and squeezing stops as soon as the target is reached. CPU use plays no part; stutter is detected from page faults (auto-squeeze).
- **Remembered safe limits** (`LearnFloors`, default on): when auto-squeeze finds that a process starts page-faulting at a level, that level is saved per app in `%APPDATA%\GameRamCap\learned.json` and used as the floor next time (entries expire after 14 days). It is a separate file because saving `config.json` makes the app reload. Settings > **Forget learned limits** clears it.

## One window

Double-click the tray icon: tabs **Main** and **Advanced** (Advanced holds Apps, Settings pages and Intensity levels). Closing the window only hides it.

- **Thread memory priority** (`ThreadMemoryPriority`, default off; Settings > advanced options): besides the process, the threads of background apps also get the low memory priority (`BackgroundMemoryPriority`). Processes that own an active audio stream are never touched. Experimental; needs `LowerMemoryPriority`.
- **Windows tools** (Settings > advanced options): Toggle MemCompression (note: turning it off raises RAM pressure), Toggle PageCombining (reboot needed), iGPU route. Results go to a message box and to the log.
- The separate SmartRam Lab (second engine, baseline/metrics tab) was removed; its useful parts are options of the main engine now.
- The app runs as Administrator (`app.manifest`) because the tools and some processes need it.


## Notes on some settings (see CHANGELOG.md for release history)

- **Limit all the time** (`AlwaysLimit`): turn it off and limits apply only while a game runs. A game must stay in front for `DelaySeconds`; **Keep limits while the game runs** keeps them after Alt+Tab until the game exits. Games = your `Games` list or auto-detected fullscreen apps.
- **Gentle trim before capping** (`SoftTrim`, five levels): an app over its budget first gets its idle processes trimmed, then the hard cap follows. Cooldown, the budget feedback loop and the Busy CPU limits of the level table now take effect. Busy apps are still capped when Hold is on.
- **Own limit** (Apps tab, `UseOwnLimit`): with the five levels, a ticked app uses its own Limit/Min/Max/Adaptive instead of the level. Custom always uses them.
- **Adaptive** per app now switches auto-squeeze off for that app (Custom and own-limit rows).

## Logs and privacy

`%APPDATA%\GameRamCap\log.txt` records limits applied/released, detected games and errors. It rotates to `log.txt.old` at 512 KB. The app does not log which windows you switch between.

# GameRamCap

**Give your game more RAM. Automatically.**

GameRamCap is a small tray app for Windows 10/11. While you play, it keeps background apps (Discord, Chrome, Steam, Spotify...) from hogging your memory, so your game has more RAM and runs smoother. When you go back to an app, its limit is lifted at once.

Nothing is closed, nothing is deleted. The limits only affect how much RAM background apps may keep in memory, and every one of them is released when you leave the game or exit GameRamCap.

## Quick start

1. Download `GameRamCap.exe` from the [Releases](../../releases) page and run it. (Windows asks for administrator rights; it needs them to limit other apps.)
2. A tray icon appears. That's it: start a game and GameRamCap does the rest.
3. Double-click the tray icon any time to open the window.

Optional: right-click the tray icon > **Start with Windows**.

## What you see in the window

| Tab | What it is for |
|---|---|
| **Main** | On/off switch, the **intensity slider**, and the few settings that matter. |
| **Apps** | Every running app with its RAM, CPU, GPU and disk use. Pick which apps to limit, set exceptions, and use the right-click tools (below). |
| **Games and lists** | Tell GameRamCap which programs are games, and which apps must never be limited. |
| **Settings** | Mode, when limits apply, game detection, and maintenance. |

### Intensity: how hard should it squeeze?

Move the slider on the Main tab.

| Level | What to expect |
|---|---|
| **Ultra Safe** | Very gentle. Nothing should feel different. |
| **Safe** | Gentle. Background apps keep a comfortable amount of RAM. |
| **Balanced** | A good middle, and a good start. |
| **Performance** | Strong. Frees a lot of RAM; an app you switch back to may need a moment to wake up. |
| **Ultra Performance** | Frees the most RAM. Background apps are kept very small and may feel slow when you return to them. |
| **Custom** | Your own numbers (ceiling per process, check interval, grace, cooldown). Use *Start from a level* to begin from a preset. |

Not sure? Use **Balanced**.

### Smart or Hard mode

- **Smart (default):** adapts to your PC. It acts when free RAM gets low, learns which apps struggle when squeezed and gives them room back, and never starves anything.
- **Hard:** fixed RAM ceilings you set yourself per app (Apps tab), applied per process or per app.

Switch in Settings or from the tray menu (**Mode**).

### Right-click tools on the Apps tab

- **Priority** and **CPU cores**: for this run only, or remembered every time the app starts.
- **Freeze / Resume**: pause an app completely (no CPU, nothing lost) and bring it back later.
- **Process tree**: see parent and child processes (for example Chrome and all its helpers) with RAM per branch.
- **History**: RAM and CPU of every app over the last hour, to spot an app that keeps growing.
- **Connections**: the network connections an app has open.
- **Check on VirusTotal**: opens the VirusTotal page for the app's file. Only the file's SHA-256 hash is used; the file is never uploaded.
- **End task**, **Start new task**, **Open file location**.

### Extras while a game runs (off by default, Settings > Game detection)

- **Pause Windows Update** so it does not download or install in the middle of a game. It is put back exactly as it was afterwards, even after a crash.
- **Clear the standby list** when free RAM is low (at most once every couple of minutes). It only drops cached data; no app is changed.

## Is it safe?

- **The app you are using, your game, Windows system processes, security services and common anti-cheat services are never touched.**
- Apps that are playing sound are protected, and recording tools (OBS, NVIDIA Share) are protected by default.
- Add anything that must keep all its RAM (virtual machines, editors...) to **Exceptions**.
- Exiting from the tray restores everything. If GameRamCap is killed (End Task) or crashes, a hidden watchdog copy lifts leftover limits, resumes frozen apps and restores Windows Update. The next start also cleans up.
- A very low ceiling makes apps slower to come back (Discord voice can crackle). Start with **Balanced**.
- Do not add your game as a limited app.

## Files and privacy

Everything is stored in `%APPDATA%\GameRamCap\`:

- `config.json`: your settings (saving the file reloads it; a broken file is backed up and the last good one is restored).
- `log.txt`: limits applied/released, detected games and errors (rotates at 512 KB). It does not log which windows you switch between.
- `learned.json`, `suspended.json`, `winupdate.json`: remembered safe limits and recovery information.

Nothing is sent anywhere. The only network feature is the VirusTotal button, which opens a web page in your browser.

## Build from source

Requires the .NET 8 SDK.

    dotnet build -c Release
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

See [CHANGELOG.md](CHANGELOG.md) for release history.

---

# Reference (advanced)

Everything below is for people who edit `config.json` or want to know exactly what the engine does.

## How it works

GameRamCap uses the documented `SetProcessWorkingSetSizeEx` API (with the hard-max flag in Hard mode) to limit the resident RAM (working set) of background processes. It does not clean memory periodically.

### Smart mode

No blanket hard caps, so nothing can be starved or crash:

- With `LimitWhenRamFree: true` it acts only when free RAM drops below the level's pressure % (Ultra Performance 60% ... Ultra Safe 12%). With `false` it keeps background apps in budget all the time.
- Fullscreen/borderless apps seen in front are remembered as games and are never trimmed or capped while they run, even after Alt+Tab.
- Apps that refused a trim are retried every 5 minutes.
- Under pressure it trims background apps softly (pages move to the standby list; the app takes them back instantly if needed, no disk).
- The budget is per **app** (all its processes together): `BudgetMB`, or `MaxCeilingMB` when 0.
- It never touches the focused app (including its whole process tree; UWP apps resolve to the real app), an app you left in the last `GraceAfterFocusSeconds`, busy processes, or `Games`.
- Learning: if an app pulls its pages back right away, its budget grows and the cooldown doubles; when it stays quiet, the budget returns to normal.
- `HoldBackground` (default `true`): after a background app is trimmed it also gets a hard ceiling per process so it cannot grow back; focusing the app lifts the ceiling at once. `false` = pure soft trimming.
- Below `CriticalFreePercent` trimming is stronger.

### Hard mode

Fixed hard ceilings. Set `"Mode": "Hard"`. `HardScope`:

- `"Process"` (recommended, default): every process of an app gets the limit separately.
- `"App"`: the limit is the total for the app, split equally between its running processes (min 16 MB each, fixed, not adapted).

### Intensity levels

All levels act at all times (unless `LimitWhenRamFree` is on). The hard ceiling is **per process** (app total = ceiling x number of processes, split by what each process uses). No process is squeezed below its floor or keep %, so apps do not stutter. Held processes also **adapt**: one that sits at its ceiling and keeps page-faulting gets +25% (up to `MaxCeilingMB`) and slides back after 30 quiet seconds.

| Level | Interval | Ceiling / process | Floor | Keep % | Pressure % |
|---|---|---|---|---|---|
| Ultra Performance | 1 s | 8 MB | 8 MB | 1% | 60 |
| Performance | 2 s | 20 MB | 12 MB | 8% | 45 |
| Balanced | 3 s | 30 MB | 16 MB | 15% | 30 |
| Safe | 4 s | 40 MB | 24 MB | 25% | 20 |
| Ultra Safe | 5 s | 90 MB | 48 MB | 50% | 12 |

Per-level values can be overridden by hand in `config.json` (`PresetOverrides`); only the values you change are stored.

**Custom** (last stop of the slider) is a level with your own numbers (`CustomLevel` in `config.json`): ceiling per process (down to 2 MB; below 8 MB some apps may stutter), check interval, grace, cooldown, plus budget scale / min process / critical factor / floor / keep % under "More numbers". It starts as a copy of Balanced and runs through the same engine path as the five levels.

Level fields: *Cooldown* = minimum seconds between two cap passes of an app; *Budget scale* multiplies the app target (ceiling x process count); *Min process* leaves smaller processes alone; *Critical factor* multiplies the target and lowers existing ceilings once when free RAM is below the critical %.

### Auto-squeeze (default on)

Finds the lowest RAM each background process can live on **without stutter**. Every held process is probed: quiet for `AutoProbeSeconds` (10 s) -> ceiling lowered one step (10%, or straight to its real use + 25% if the ceiling is not binding), down to `AutoMinProcessMB` (16). At its ceiling and page-faulting more than `AutoFaultRate` (150/s) -> ceiling raised 25% and that level is kept as a floor for 5 minutes, then probed again. It never grows past what the process used before it was held (+20%). Focusing an app lifts its ceiling at once.

### Smarter limiting (Smart mode)

- **Audio protection** (`AudioProtect`, default on): only the **process** that owns an active audio stream is left alone, plus `AudioProtectGraceSeconds` (8) after the sound stops. Other processes of the same app are limited as usual.
- **Low memory priority** (`LowerMemoryPriority`, default on; `BackgroundMemoryPriority` 1-4, default 2): background apps get a lower Windows memory priority; when RAM is short Windows takes their pages first. Focusing the app restores it at once. After End Task on GameRamCap the low priority stays until those apps restart.
- **Thread memory priority** (`ThreadMemoryPriority`, default off, experimental): also applies the low priority to threads of background apps; needs `LowerMemoryPriority`.
- **Biggest first**: for each background app the target is `ceiling x process count`; the biggest processes are squeezed first, only as far as needed. Stutter is detected from page faults, not CPU use.
- **Remembered safe limits** (`LearnFloors`, default on): when auto-squeeze finds a process starts page-faulting at some level, it is saved per app in `learned.json` and used as the floor next time (expires after 14 days). Settings > **Forget learned limits** clears it.

## Settings (`config.json`)

Edit `%APPDATA%\GameRamCap\config.json` (created on first run).

| Setting | Meaning |
|---|---|
| `Mode`, `Profile` | `Smart` / `Hard`; the intensity level (`UltraSafe`, `Safe`, `Balanced`, `Performance`, `UltraPerformance`, `Custom`). |
| `LimitEverything` | **On by default.** Every app and game gets a ceiling (values from `Default*`; apps in `Targets` keep their own numbers), except the one in focus, protected system processes and `Exceptions`. |
| `Exceptions` | Process names that are never limited. Easiest: Games and lists tab > Exceptions. |
| `FocusMode` | Always on. The app you are using is unlimited; every other target is capped. Click elsewhere and its cap returns. |
| `RelimitDelaySeconds` | Delay before the cap returns after you leave an app (default 1.5 s, avoids flicker on Alt+Tab). |
| `Targets[].FocusAlso` | Extra process names that count as focusing that app (`steamwebhelper` already maps to `steam`). |
| `Games` | Process names (no `.exe`) that always count as a game. Find them in Task Manager > Details. |
| `AutoDetectFullscreen` | Any borderless/fullscreen foreground window counts as a game. |
| `FullscreenIgnore` | Fullscreen apps that are not games (browsers, video players). |
| `DelaySeconds` | How long the game must stay in front before limits apply. |
| `AlwaysLimit` | Off = limits apply only while a game runs. |
| `KeepLimitsWhileGameRuns` | Keep limits after Alt+Tab until the game exits. |
| `SoftTrim` | An app over its budget first gets idle processes trimmed, then the hard cap follows. |
| `Targets[].CeilingMB` | Starting ceiling for that app, applied to **each process** (Hard mode and rows ticked *Own limit*). |
| `Targets[].Adaptive` | Ceiling grows when the app is busy at its limit and shrinks when idle, between `MinCeilingMB` and `MaxCeilingMB`. |
| `Targets[].UseOwnLimit` | *Own limit* (Apps tab): this row uses its own Limit/Min/Max/Adaptive instead of the level. In Smart mode the per-app numbers are ignored otherwise. |
| `Rules` | Saved priority / CPU-core rules (Apps tab, right-click). |
| `CustomLevel` | The numbers of the Custom level. |
| `PauseWindowsUpdate` | Pause Windows Update while a game runs (restored afterwards). |
| `StandbyClean`, `StandbyFreeBelowMB` (1024), `StandbyMinMB` (1024), `StandbyMinIntervalSeconds` (120) | Standby list cleaner: acts only while a game runs, when FREE RAM (not counting the standby cache) is below `StandbyFreeBelowMB` and the standby list is at least `StandbyMinMB`; at most once per interval. |

## Notes

- The app runs as Administrator (`app.manifest`); some processes can only be limited that way, and the standby cleaner and Windows Update pause need it.
- The window is hidden, not closed, when you press X. A second copy of the app opens the window of the running one.
- A hidden watchdog (`GameRamCap.exe --watchdog <pid>`, started detached) waits for the main app to die and then lifts every leftover ceiling, resumes frozen apps and restores Windows Update.

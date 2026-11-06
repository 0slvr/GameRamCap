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
| **Settings** | Mode, when limits apply, game detection, and maintenance. |

### Intensity: how hard should it squeeze?

Move the slider on the Main tab.

| Level | What to expect |
|---|---|
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

Select several apps (Ctrl or Shift) and every command applies to all of them.

- **Priority** and **CPU cores**: for this run only, or remembered every time the app starts.
- **Freeze / Resume**: pause an app completely (no CPU, nothing lost) and bring it back later.
- **History**: RAM and CPU of every app over the last hour, to spot an app that keeps growing.
- **Check on VirusTotal**: opens the VirusTotal page for the app's file. Only the file's SHA-256 hash is used; the file is never uploaded.
- **Protect (never limit)** / **Unprotect**: choose which apps GameRamCap may touch. Protected apps are never limited, frozen or ended from here. Only the Windows core processes that can crash the system are protected by default (System, smss, csrss, wininit, winlogon, services, lsass, lsm, svchost, dwm, fontdrvhost, Registry, Secure System, Memory Compression). You can unprotect those too, at your own risk.
- **End task** (also the Delete key), **Start new task**, **Open file location**.

### Extras while a game runs (off by default, Settings > Game detection)

- **Clear the standby list** when free RAM is low (at most once every couple of minutes). It only drops cached data; no app is changed.

## Is it safe?

- **The app you are using, your game, Windows system processes, security services and common anti-cheat services are never touched.**
- Apps that are playing sound are protected, and recording tools (OBS, NVIDIA Share) are protected by default.
- Add anything that must keep all its RAM (virtual machines, editors...) to **Exceptions**.
- Exiting from the tray restores everything. If GameRamCap is killed (End Task) or crashes, a hidden watchdog copy lifts leftover limits and resumes frozen apps. The next start also cleans up.
- A very low ceiling makes apps slower to come back (Discord voice can crackle). Start with **Balanced**.
- Do not add your game as a limited app.

## Files and privacy

Everything is stored in `%APPDATA%\GameRamCap\`:

- `config.json`: your settings (saving the file reloads it; a broken file is backed up and the last good one is restored).
- `log.txt`: limits applied/released, detected games and errors (rotates at 512 KB). It does not log which windows you switch between.
- `suspended.json`, `winupdate.json`: recovery information (an older `learned.json` can be deleted).

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

- With `LimitWhenRamFree: true` it acts only when free RAM drops below the level's pressure % (Ultra Performance 60% ... Balanced 30%). With `false` it keeps background apps in budget all the time.
- Fullscreen/borderless apps seen in front are remembered as games and are never trimmed or capped while they run, even after Alt+Tab.
- Apps that refused a trim are retried every 5 minutes.
- Under pressure it trims background apps softly (pages move to the standby list; the app takes them back instantly if needed, no disk).
- The budget is per **app** (all its processes together): `BudgetMB`, or `MaxCeilingMB` when 0.
- It never touches the focused app (including its whole process tree; UWP apps resolve to the real app), an app you left in the last `GraceAfterFocusSeconds`, busy processes, or `Games`.
- Learning: if an app pulls its pages back right away, its budget grows and the cooldown doubles; when it stays quiet, the budget returns to normal.
- Hold is always on: after a background app is trimmed it also gets a hard ceiling per process so it cannot grow back; focusing the app lifts the ceiling at once.
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

Per-level values can be overridden by hand in `config.json` (`PresetOverrides`); only the values you change are stored.

**Custom** (last stop of the slider) is a level with your own numbers (`CustomLevel` in `config.json`): ceiling per process (down to 2 MB; below 8 MB some apps may stutter), check interval, grace, cooldown, plus budget scale / min process / critical factor / floor / keep % under "More numbers". It starts as a copy of Balanced and runs through the same engine path as the five levels.

Level fields: *Cooldown* = minimum seconds between two cap passes of an app; *Budget scale* multiplies the app target (ceiling x process count); *Min process* leaves smaller processes alone; *Critical factor* multiplies the target and lowers existing ceilings once when free RAM is below the critical %.

### Smarter limiting (Smart mode)

- **Audio protection** (`AudioProtect`, default on): only the **process** that owns an active audio stream is left alone, plus `AudioProtectGraceSeconds` (8) after the sound stops. Other processes of the same app are limited as usual.
- **Low memory priority** (always on, no setting, in Smart and Hard mode): background apps get the lowest Windows memory priority; when RAM is short Windows takes their pages first. Focusing the app restores it at once. After End Task on GameRamCap the low priority stays until those apps restart.
- **Thread memory priority** (always on, no setting): the same lowest priority is also applied to the threads of background apps.
- **Biggest first**: for each background app the target is `ceiling x process count`; the biggest processes are squeezed first, only as far as needed. Stutter is detected from page faults, not CPU use.
- **Measured need** (`MeasureNeed`, default on): a background process is trimmed once and, `NeedProbeSeconds` (4) later, the memory it pulled straight back is taken as what it really needs. It is capped at that x `NeedMargin` (1.3) and that value is its floor, so critical mode never goes below it. Growth is bounded: a ceiling can only be raised up to twice the measured need (at least 64 MB), by page-fault thrash or by CPU starvation (a process that burns more than 50 % CPU while using under a quarter of its ceiling gets +8 MB steps; idea from Memory Sentry), and nothing is written to `learned.json`. A measured need is never above `NeedMaxMB` (128) and raises never go past twice that, however much the process thrashes. Soft page faults no longer hide a starved process: above `ThrashFaultsPerMB` (60) faults per second per resident MB the ceiling is raised even when the disk is idle. The process that plays audio gets a generous ceiling (`AudioCapMB`, 160) under pressure instead of none, so a player like Spotify cannot grow to 800 MB+ while it plays; 0 restores the old behaviour. This replaces the guessed floors (`HoldKeepPercent`, `BigProcKeepPercent`, learned floors) while it is on.

## Settings (`config.json`)

Edit `%APPDATA%\GameRamCap\config.json` (created on first run).

| Setting | Meaning |
|---|---|
| `Mode`, `Profile` | `Smart` / `Hard`; the intensity level (`Balanced`, `Performance`, `UltraPerformance`, `Custom`). |
| `LimitEverything` | **On by default.** Every app and game gets a ceiling (values from `Default*`; apps in `Targets` keep their own numbers), except the one in focus, protected system processes and `Exceptions`. |
| `Exceptions` | Process names that are never limited. Easiest: Main tab or Apps tab > Exceptions. |
| `FocusMode` | Always on. The app you are using is unlimited; every other target is capped. Click elsewhere and its cap returns. |
| `RelimitDelaySeconds` | Delay before the cap returns after you leave an app (default 1.5 s, avoids flicker on Alt+Tab). |
| `Targets[].FocusAlso` | Extra process names that count as focusing that app (`steamwebhelper` already maps to `steam`). |
| `Games` | Process names (no `.exe`) that always count as a game. Find them in Task Manager > Details. |
| `AutoDetectFullscreen` | Always on (not a setting any more): any borderless/fullscreen foreground window counts as a game. |
| `FullscreenIgnore` | Fullscreen apps that are not games (browsers, video players). |
| `DelaySeconds` | How long the game must stay in front before limits apply. |
| `AlwaysLimit` | Off = limits apply only while a game runs. |
| `KeepLimitsWhileGameRuns` | Keep limits after Alt+Tab until the game exits. |
| `Targets[].CeilingMB` | Starting ceiling for that app, applied to **each process** (Hard mode and rows ticked *Own limit*). |
| `Targets[].Adaptive` | Ceiling grows when the app is busy at its limit and shrinks when idle, between `MinCeilingMB` and `MaxCeilingMB`. |
| `Targets[].UseOwnLimit` | *Own limit* (Apps tab): this row uses its own Limit/Min/Max/Adaptive instead of the level. In Smart mode the per-app numbers are ignored otherwise. |
| `Rules` | Saved priority / CPU-core rules (Apps tab, right-click). |
| `CustomLevel` | The numbers of the Custom level. |
| `StandbyClean`, `StandbyFreeBelowMB` (1024), `StandbyMinMB` (1024), `StandbyMinIntervalSeconds` (120) | Standby list cleaner: acts only while a game runs, when FREE RAM (not counting the standby cache) is below `StandbyFreeBelowMB` and the standby list is at least `StandbyMinMB`; at most once per interval. |

## Notes

- The app runs as Administrator (`app.manifest`); some processes can only be limited that way, and the standby cleaner needs it.
- The window is hidden, not closed, when you press X. A second copy of the app opens the window of the running one.
- A hidden watchdog (`GameRamCap.exe --watchdog <pid>`, started detached) waits for the main app to die and then lifts every leftover ceiling, resumes frozen apps.

### Smarter limiting (wake-up awareness)

- **`WakeBoostPerUse`** (default 0.1, 0 = off): the app list of the last 30 minutes is remembered. Every time you bring an app to the front it is spared a bit more (bigger margin on its measured need, bigger keep share), up to double. Apps you never come back to keep the normal, strong limits. This is what keeps the Start menu, search and other apps you summon often from feeling slow.

- **Hot-set wake-up** (`PrefetchApps`, default on): just before a process is trimmed or capped for the first time, GameRamCap saves which pages it had resident. When you come back to the app, exactly those pages are read back (not a guess by address), within `PrefetchMaxMB` per app. Apps you come back to often get up to `1 + PrefetchFrequentBoost` times that (default 2 = up to 3x). Without a saved hot set it falls back to the old address-order read.
- **`WakeStartOnKey`** (default on): pressing `Win`, `Win+S` or `Win+Q` lifts the limit of the Start / Search hosts (`StartMenuExperienceHost`, `SearchHost`, `ShellExperienceHost`) and reads their trimmed pages back into RAM before the menu shows, the same way the Win+V wake-up does for `TextInputHost`. Skipped while free RAM is below `PrefetchMinFreePercent`.
- **`WakeAltTabOnKey`** (default on): pressing `Alt+Tab` lifts the limit of the app you are about to switch to (the previous one on the first `Tab`, the next one back on each further `Tab`) and reads its trimmed pages back into RAM before the window comes up. Skipped while free RAM is below `PrefetchMinFreePercent`.
- **`WakeClickOnMouse`** (default on): when the left mouse button goes down on a window of another app, that app's limit is lifted and its trimmed pages are read back into RAM before the window comes to the front. Clicks inside the app already in front, and on the desktop / taskbar / Start (shell), do nothing. Skipped while free RAM is below `PrefetchMinFreePercent`.

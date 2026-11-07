<div align="center">

# GameRamCap

### Give your game more RAM. Automatically.

A tiny tray app that keeps background apps from hogging your memory while you play.
No closing apps. No manual cleaning. Just smoother games.

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

[**Download**](../../releases) · [How it works](#-how-it-works) · [Is it safe?](#-is-it-safe) · [FAQ](#-faq)

</div>

<!-- Add a screenshot or GIF of the window here:  ![GameRamCap](docs/screenshot.png) -->

---

## ✨ Why GameRamCap?

Discord, Chrome, Steam and Spotify quietly eat gigabytes of RAM while your game is running.
GameRamCap squeezes those background apps, leaves the RAM to your game, and gives every app its memory back **the moment you switch to it**.

- 🎮 **Detects your game automatically**, with nothing to set up
- ⚡ **Instant wake-up:** `Alt+Tab`, `Win`, or a click restores the app you're heading to
- 🧠 **Smart mode** learns which apps struggle when squeezed and gives them room
- 🔊 **Never cuts your music or voice chat:** apps playing audio are protected
- 🛡️ **Nothing is closed or deleted**, and everything is restored when you exit
- 🔒 **100% offline:** nothing is sent anywhere

---

## 🚀 Quick start

1. Download `GameRamCap.exe` from the [**Releases**](../../releases) page and run it.
   Windows will ask for administrator rights, which are needed to limit other apps.
2. A tray icon appears. **That's it.** Start a game and GameRamCap does the rest.
3. Double-click the tray icon any time to open the window.

> 💡 Right-click the tray icon → **Start with Windows** to have it ready on every boot.

---

## 🎚️ Pick your intensity

Move the slider on the **Main** tab.

| Level | Best for | What to expect |
|---|---|---|
| **Balanced** ⭐ | Most people | A good middle. **Start here.** |
| **Performance** | Low-RAM PCs | Frees a lot of RAM. An app may need a moment to wake up. |
| **Ultra Performance** | Maximum RAM | Background apps are kept very small and may feel slow when you return. |
| **Custom** | Tinkerers | Your own numbers. Start from any level and adjust. |

---

## 🧭 How it works

GameRamCap uses the documented Windows `SetProcessWorkingSetSizeEx` API to limit how much RAM background processes may keep resident. It doesn't clean memory on a timer. It trims only what your game needs back.

**Smart mode (default)**
Acts when free RAM gets low, measures what each app really needs, and raises the limit of any app that struggles. Nothing gets starved.

**Hard mode**
Fixed RAM ceilings that you set per app on the **Apps** tab.

Switch between them in **Settings** or from the tray menu.

### The window

| Tab | What it does |
|---|---|
| **Main** | On/off switch, the intensity slider, and the few settings that matter. |
| **Apps** | Every running app with its RAM, CPU, GPU and disk use. Choose which apps to limit. |
| **Settings** | Mode, when limits apply, game detection, and maintenance. |

### Right-click tools (Apps tab)

Select several apps with `Ctrl` or `Shift` and every command applies to all of them.

| Tool | What it does |
|---|---|
| **Priority / CPU cores** | Set for this run only, or remember it every time the app starts. |
| **Freeze / Resume** | Pause an app completely (no CPU, nothing lost) and bring it back later. |
| **History** | RAM and CPU of every app over the last hour. Spot the one that keeps growing. |
| **Check on VirusTotal** | Opens the VirusTotal page for the file. Only its SHA-256 hash is used; **the file is never uploaded.** |
| **Protect / Unprotect** | Choose which apps GameRamCap may touch. |
| **End task · Start new task · Open file location** | Handy shortcuts, so you don't need Task Manager. |

### Optional extra (off by default)

**Clear the standby list** while a game runs and free RAM is low. It only drops cached data, so no app is changed. Find it in *Settings → Game detection*.

---

## 🛡️ Is it safe?

**Yes. These are never touched:**

- the app you're using right now and your game
- Windows system processes and security services
- common anti-cheat services
- apps that are playing sound
- recording tools (OBS, NVIDIA Share)

**And if something goes wrong:**

- Exiting from the tray restores everything.
- If GameRamCap is killed or crashes, a hidden **watchdog** lifts every leftover limit and resumes frozen apps. The next start cleans up too.

**Good to know**

- A very low limit makes apps slower to come back (Discord voice can crackle). Start with **Balanced**.
- Never add your game as a limited app.
- Put anything that must keep all its RAM (virtual machines, editors) in **Exceptions**.

---

## 🔐 Privacy

Everything lives in `%APPDATA%\GameRamCap\`:

| File | Purpose |
|---|---|
| `config.json` | Your settings. Saving reloads them, and a broken file is backed up and the last good one restored. |
| `log.txt` | Limits applied or released, detected games, errors. Rotates at 512 KB. It never logs which windows you switch between. |
| `suspended.json`, `winupdate.json` | Recovery information. |

**Nothing is sent anywhere.** The only network-related feature is the VirusTotal button, which just opens a web page in your browser.

---

## ❓ FAQ

<details>
<summary><b>Will it lower my FPS or crash my game?</b></summary>

No. Your game is never limited. It's detected automatically, even after you `Alt+Tab`. GameRamCap only works on *other* apps.
</details>

<details>
<summary><b>Why does it ask for administrator rights?</b></summary>

Some processes can only be limited with admin rights, and the standby cleaner needs them too.
</details>

<details>
<summary><b>An app feels slow when I switch back to it.</b></summary>

Lower the intensity (try **Balanced**), or add the app to **Exceptions**. Smart mode also learns and relaxes limits for apps that struggle.
</details>

<details>
<summary><b>What happens when I press X on the window?</b></summary>

It hides to the tray. GameRamCap keeps working. Use the tray menu → Exit to quit and restore everything.
</details>

<details>
<summary><b>Does it work with any game?</b></summary>

Any game that runs fullscreen or borderless is detected automatically. For others, add the process name (without `.exe`) to `Games` in `config.json`. You'll find it in Task Manager → Details.
</details>

---

## 🔧 Advanced reference

<details>
<summary><b>Intensity levels in detail</b></summary>

| Level | Check every | Ceiling / process | Floor | Keep % | Pressure % |
|---|---|---|---|---|---|
| Ultra Performance | 1 s | 8 MB | 8 MB | 1% | 60 |
| Performance | 2 s | 20 MB | 12 MB | 8% | 45 |
| Balanced | 3 s | 30 MB | 16 MB | 15% | 30 |

- The hard ceiling is **per process**. An app's total is ceiling × its number of processes.
- No process is squeezed below its floor, so apps don't stutter.
- Held processes **adapt**: one that sits at its ceiling and keeps page-faulting gets +25% (up to `MaxCeilingMB`), then slides back after 30 quiet seconds.
- Override per-level values with `PresetOverrides` in `config.json`. Only the values you change are stored.
- **Custom** can go down to 2 MB per process (below 8 MB some apps may stutter).
</details>

<details>
<summary><b>Smart mode behaviour</b></summary>

- With `LimitWhenRamFree: true` it acts only when free RAM drops below the level's pressure %. With `false` it keeps background apps in budget all the time.
- Budget is per **app** (all its processes together).
- It never touches the focused app (including its process tree), an app you left in the last `GraceAfterFocusSeconds`, busy processes, or games.
- Under pressure it trims softly: pages go to the standby list and come back instantly if needed, with no disk access.
- After a trim, the app also gets a hard ceiling per process so it can't grow back. Focusing it lifts the ceiling at once.
- Below `CriticalFreePercent`, trimming gets stronger.
- Background apps get the lowest Windows memory priority (processes and threads), so Windows takes their pages first. Focusing the app restores it.
- **Audio protection:** only the process that owns an active audio stream is spared (`AudioProtect`, plus `AudioProtectGraceSeconds`). The process playing audio gets a generous ceiling under pressure (`AudioCapMB`, 160) instead of none.
- **Measured need** (`MeasureNeed`): a process is trimmed once and, `NeedProbeSeconds` (4) later, what it pulled back is taken as its real need. It is capped at that × `NeedMargin` (1.3), never above `NeedMaxMB` (128).
</details>

<details>
<summary><b>Wake-up features</b></summary>

| Setting | Default | What it does |
|---|---|---|
| `WakeBoostPerUse` | 0.1 | Apps you bring to the front often are spared more (up to double). 0 = off. |
| `PrefetchApps` | on | Saves each process's resident pages before the first trim and reads exactly those back when you return (within `PrefetchMaxMB`). |
| `WakeStartOnKey` | on | `Win`, `Win+S`, `Win+Q` pre-wake the Start and Search hosts. Needs `GlobalHooks` = true. |
| `WakeAltTabOnKey` | on | `Alt+Tab` pre-wakes the app you're switching to (works without `GlobalHooks`, through the switcher window event). |
| `WakeClickOnMouse` | on | Clicking a window of another app wakes it before it comes to the front. Needs `GlobalHooks` = true. |

All wake-ups are skipped while free RAM is below `PrefetchMinFreePercent`.
</details>

<details>
<summary><b>config.json settings</b></summary>

Edit `%APPDATA%\GameRamCap\config.json`. It's created on first run.

| Setting | Meaning |
|---|---|
| `Mode`, `Profile` | `Smart` / `Hard`, and the level (`Balanced`, `Performance`, `UltraPerformance`, `Custom`). |
| `LimitEverything` | On by default. Every app gets a ceiling except the focused one, protected processes and `Exceptions`. |
| `Exceptions` | Process names that are never limited. |
| `Games` | Process names (no `.exe`) that always count as a game. |
| `FullscreenIgnore` | Fullscreen apps that aren't games (browsers, video players). |
| `RelimitDelaySeconds` | Delay before the cap returns after you leave an app (default 1.5). |
| `DelaySeconds` | How long a game must stay in front before limits apply. |
| `AlwaysLimit` | Off = limits apply only while a game runs. |
| `KeepLimitsWhileGameRuns` | Keep limits after `Alt+Tab` until the game exits. |
| `Targets[]` | Per-app rows: `CeilingMB`, `Adaptive`, `MinCeilingMB`, `MaxCeilingMB`, `UseOwnLimit`, `FocusAlso`. |
| `Rules` | Saved priority and CPU-core rules. |
| `CustomLevel` | The numbers of the Custom level. |
| `StandbyClean`, `StandbyFreeBelowMB`, `StandbyMinMB`, `StandbyMinIntervalSeconds` | Standby cleaner: only while a game runs, when free RAM is below `StandbyFreeBelowMB` and the standby list is at least `StandbyMinMB`; at most once per interval. |
</details>

<details>
<summary><b>Protected processes (default)</b></summary>

Only the Windows core processes that can crash the system: `System`, `smss`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `lsm`, `svchost`, `dwm`, `fontdrvhost`, `Registry`, `Secure System`, `Memory Compression`.

You can unprotect these too, at your own risk.
</details>

---

## 🛠️ Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

A GitHub Action builds every push and pull request.

## 🤝 Contributing

Issues and pull requests are welcome. If an app misbehaves while limited, please open an issue with the app name and your intensity level.

## 📄 License

[MIT](LICENSE)

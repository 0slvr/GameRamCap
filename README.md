# GameRamCap

Tray app for Windows 10/11. While a game is in the foreground, it puts a **hard ceiling on the resident RAM (working set)** of background apps you list (Discord, Claude, Steam, Spotify by default). When you leave the game, every original limit is restored.

It does not clean memory periodically. It uses the documented `SetProcessWorkingSetSizeEx` API with the hard-max flag, per process.

## Build

Requires the .NET 8 SDK.

    dotnet build -c Release
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

## Use

1. Run `GameRamCap.exe`. A tray icon appears (right-click: Enabled, Open config file, Reload config, Exit).
2. Edit `%APPDATA%\GameRamCap\config.json`. Saving the file reloads it automatically.

| Setting | Meaning |
|---|---|
| `Games` | Process names (no `.exe`) that always count as a game. Find them in Task Manager > Details. |
| `AutoDetectFullscreen` | Any borderless/fullscreen foreground window counts as a game. |
| `FullscreenIgnore` | Fullscreen apps that are not games (browsers, video players). |
| `DelaySeconds` | How long the game must stay in front before limits apply. |
| `Targets[].CeilingMB` | Starting ceiling for that app. |
| `Targets[].Adaptive` | Ceiling grows when the app is busy at its limit and shrinks when idle, between `MinCeilingMB` and `MaxCeilingMB`. |

## Safety

- System processes, security services and common anti-cheat services are never touched (hard-coded list in `Engine.cs`).
- Do not list your game as a target.
- Limits live in the target process, so closing GameRamCap or alt-tabbing out restores them; a crashed run is cleaned up at the next start.
- Too low a ceiling makes apps page heavily (Discord voice can crackle). Keep `Adaptive` on and start with the defaults.
- Some elevated processes can only be limited if GameRamCap runs as Administrator.

## Upload to GitHub (button in Visual Studio)

In the Start dropdown (next to the green arrow) choose **Upload to GitHub**, then press **Ctrl+F5**.
It checks the git state, asks for a commit message, commits and pushes. On the first run it can create the GitHub repo for you.

For fully automatic auth set a token once: `setx GH_TOKEN "ghp_xxx"` and restart Visual Studio
(fine-grained token, Contents: Read and write; also Administration: write if it should create the repo).
Without a token, git's normal sign-in (Git Credential Manager) is used instead.

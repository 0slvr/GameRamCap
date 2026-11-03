# GameRamCap Lab

Tray app for Windows 10/11 for **experiments only**: it reduces the memory of background apps while they are NOT in focus, restores them the moment they get focus back, and logs everything to CSV. See `LAB-GUIDE-AR.md` for the full test plan.

## Window

- **Experiment** tab: pick E0...E8, Delay (5 / 30 / 120 s), visible-but-unfocused mode, then **Start / Stop**. Changes apply at once while running. Live status + latest log lines + shortcuts to the CSV folder, log and config.
- **Apps** tab: choose the apps in the test (or all apps; audio/mic apps are protected automatically), then Save.
- Closing the window hides it to the tray (the experiment keeps running). Tray: Open window / Exit.

## Build

Requires the .NET 8 SDK.

    dotnet build -c Release

## Safety

- System processes, anti-cheat services and OBS / recording tools are never touched (list in `Engine.cs`). The app in focus is never touched. No game detection: any app not in focus is treated the same.
- End Task is covered: a hidden watchdog copy (`GameRamCap.exe --watchdog <pid>`) lifts leftover hard limits and resumes suspended processes.
- Results are in `%APPDATA%\GameRamCap\lab\`.

## Upload to GitHub (Visual Studio button)

In the Start dropdown choose **Upload to GitHub**, then Ctrl+F5 (`upload-github.ps1`).

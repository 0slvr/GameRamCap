# Changelog

## Unreleased
- UI: one wording for modes, scope and limits on the Main tab and the Settings pages; all windows share the same font, padding, button style and button order; Hard scope is also in Settings.
- Hard mode scope: per process (recommended) or per app (`HardScope`), in the window and the tray.
- Tray: **Mode** menu: Smart (intensity levels inside it) or Hard.
- The foreground app's process tree (parents and children) is never limited; UWP apps (ApplicationFrameHost) resolve to the real app.
- A newly focused app is released immediately, before the next scan.
- Fullscreen detection also asks `SHQueryUserNotificationState` (fewer false positives).
- Preset is resolved once per tick; Toggle MemCompression shows the current state and asks first.
- Process scan runs on a background thread (no more UI stutter).
- Global exception handlers; errors are logged instead of swallowed.
- Group budget follows the app's process count; `SetProfile` no longer causes an extra config reload.
- Broken `config.json` is backed up (`config.broken-<date>.json`) and the last good config is restored.
- Watchdog does not release ceilings if a new instance is already running.
- "Start with Windows" (Scheduled Task, highest privileges) in the tray menu.
- Starting a second copy opens the running window.
- Log rotates to `log.txt.old`; window switching is no longer logged.
- Removed the unused `NAudio.Wasapi` package; window title shows the version.
- `upload-github.ps1`: token no longer appears on the git command line, csproj `<Version>` is respected, file list shown before commit.

## 0.2.0
- Single window (Main + Advanced), intensity levels, audio protection, memory priority, learned floors.

# Audit fixes (on top of GameRamCap-secured)

Not compiled here (no .NET SDK in the sandbox): run `dotnet build -c Release` once before using.

## Fixed
- Autostart: the protected copy in Program Files now gets ONLY the program's own files (GameRamCap.exe/.dll/.deps.json/.runtimeconfig.json
  and the libraries its deps.json lists). It used to copy the whole folder (Downloads/Desktop...) up to 6 levels deep.
- Suspender: a process that is alive but could not be resumed stays on the frozen list (and in suspended.json); it is no longer forgotten.
- Built-in protection extended: audiodg, Defender, GPU driver services, VM hosts, sihost, ctfmon... (OBS / Streamlabs / NVIDIA Share are NOT in the list)
  (right-click > Unprotect still works).
- Saving a setting / reloading config.json no longer takes the engine lock on the UI thread (no more freezes while a scan runs).
- A list edited in the window while the engine reads it no longer fills the log with "Collection was modified".
- SecureStore: an untrusted data folder is moved aside (GameRamCap.untrusted-<time>) instead of deleted.
- SingleInstance: 6 short retries, so the watchdog's brief look at the lock file cannot make a real start exit.
- CeilingLedger: written after 150 ms (was 500 ms); gone processes are pruned every 30 s instead of on every write.
- Startup switch: when it cannot be turned on you now get the reason (tray balloon and Home page message).
- Config: Critical <= Pressure enforced; intensity-level overrides are range-checked; int overflow in the Smart budget fixed (long math).
- New option GlobalHooks (config.json, default true): false turns off the global keyboard/mouse hooks (needs a restart).
- Win+V poll (TextInputHost thread walk) throttled to once a second; bookkeeping dictionaries are trimmed every 10 minutes.
- PowerPlan: powercfg output read asynchronously, so the 8 s timeout really works.
- Efficiency mode OFF restores the app's original priority (was always Normal).
- ProcessRules: deleting a rule puts Normal priority / all cores back on that app.
- Restart: every distinct program path of the app is restarted (not only the first); Store apps are reported instead of failing; the
  confirmation says arguments and admin rights are not kept.
- ProcessIdentity: copies placed in user-writable folders under C:\Windows (Temp, Tasks, ...) no longer count as Windows.
- Removed dead code: RecycleInputHost, TrimBurstLater, Engine.Profile.cs; wrong "no hard caps" comment corrected. GameRamCap.csproj.user removed.

## Not changed on purpose / still open
- A windowed (not fullscreen) game is only safe if it is in the Games list.
- If a different copy is already running (autostarted one), a newer exe started by hand exits; exit the tray app first to update the copy.
- Caps left by a version older than the ledger are not lifted automatically (the ledger only knows its own).
- UsageMonitor still opens every process every 5 s; Standby cleaner / Boost can still stutter a game (off by default / one-shot).

## Native / system-tools review (this round)
Not compiled here (no .NET SDK in the sandbox): run `dotnet build -c Release` once before using.
- Native.cs split into Native.cs (process / memory), Native.Hooks.cs (hooks, window events) and Native.Windows.cs (window under a point, Win+V panel).
- Low-level keyboard/mouse hooks now run on their own thread with their own message loop (a busy UI thread can no longer delay system-wide input).
  Both hooks are installed or neither; the old "one hook failed, second install replaced a live delegate" crash path is gone.
- GlobalHooks is now OFF by default (some anti-cheats / antivirus dislike global hooks). Alt+Tab is still detected through the
  EVENT_SYSTEM_SWITCHSTART window event; Start key and mouse-click wake-ups need GlobalHooks=true.
- WinEvent hooks for window activity skip this program's own windows (WINEVENT_SKIPOWNPROCESS); the foreground event is hooked once.
- Process.GetProcessById(pid).StartTime replaced by Native.GetStartTime (one handle, one call) in Hold / memory priority / wake-up / CeilingLedger.
- Thread memory priority reads thread ids from one cached Toolhelp snapshot instead of a Process object per app (the Win+V panel check keeps using the thread list that GetProcessesByName already has).
- Least privilege: reading limits uses PROCESS_QUERY_LIMITED_INFORMATION; setting / lifting a ceiling needs only PROCESS_SET_QUOTA (+ limited query);
  prefetch no longer asks for PROCESS_VM_OPERATION.
- Release() reads the working set from the handle it already has. Image path supports paths longer than 1024 characters.
- Suspender: the freeze list is written to suspended.json BEFORE processes are frozen; a process that cannot be frozen is taken off it again.
- One shared Privilege helper (DebugPrivilege and the standby cleaner used two copies of the token code); OpenProcess/CloseHandle shared with Suspender.
- Removed dead code: InputHostProbe.cs, DebugInputHost option (it was forced off on every load), DescribeWindows, HasVisibleWindow, LastActivityTicks.
- GameRamCap.csproj: PlatformTarget x64 (the MEMORY_BASIC_INFORMATION layout is the 64-bit one).
- Not changed: UsageMonitor / the Apps tab still open every process every 5 s (cheap; a single NtQuerySystemInformation snapshot would avoid it).

## Engine review (races, slow leaks, logic) - this round
Not compiled here (no .NET SDK in the sandbox): run `dotnet build -c Release` once before using.

### Fixed
- Focus: a shell process in front (Explorer: desktop, taskbar, the Alt+Tab switcher; Start; Search) is no longer the root of the
  "in focus" process tree. Explorer is the parent of nearly every app the user started, so every Alt+Tab used to lift the ceilings of
  almost everything. A tree with more than 60 descendants is ignored as well (TreeNames).
- Tick: UsageMonitor.Update (opens every process) and ProcessRules.Apply now run OUTSIDE the engine lock, so the foreground / Alt+Tab /
  click / key handlers are no longer held up behind a scan. The audio-session COM poll runs on the thread pool, not under the lock.
- Smart + audio: a process that was squeezed while idle and then starts playing sound gets its ceiling raised to AudioCapMB
  (TuneHeld never looks at audio processes, so it stayed at the small ceiling before).
- GameRamCap never limits itself (Smart, Everything and Hard modes).
- Slow leaks: one sweep every 10 minutes (CleanCaches, at the top of TickFocus, so it runs in every mode and also while the game gate is
  closed) forgets _groups, _synth, _neutral, _statusAt, _loggedNames, _audioLogged, _cpuPrev, _tuneLogAt, _softLogAt, _hot, _resident,
  _wokenAt, _prioFail of processes / apps that are gone. Learned "popup apps" expire after 2 hours. Frozen entries of dead processes are
  pruned (Suspender.PruneDead).
- Hard mode (ApplyAndAdapt): an entry whose process is no longer handled is released instead of silently forgotten while it still carried
  our ceiling.
- Exempt apps: released by the pids the scan already has (Smart) / only when the exempt set changes or on the 1 s scan (Everything),
  instead of one full process enumeration per exempt name per tick. IsException uses a set.
- Event handlers: key-repeat / click / prefetch throttles are taken under a small lock (they ran concurrently on thread-pool tasks);
  only one wake-up reads pages at a time (a burst of Alt+Tab no longer piles up disk reads next to the game).
- ProbeNeed: does not place a cap after Dispose, after the settings object changed, or when the PID now belongs to another process.
- IsGaming() is evaluated once per tick (it was called by TickFocus and again by TickStandby, with side effects).
- Fullscreen: an app that was taken for a game only because it was fullscreen (not in the Games list) is a normal app again once it is
  in front and NOT fullscreen for 10 s. Games in the Games list stay protected.
- CeilingLedger: the snapshot is taken inside the write lock, so an older snapshot can never overwrite a newer one.
- Cleanup: unused `Relaxed` list and glued doc comments removed.

### Not changed on purpose / still open
- SaveCfg / SetModeAndLevel still change the live Config object in place (Mode, Profile...). A scan can see a mix of the old and new
  value for one tick; the next tick is consistent. Swapping in a new Config object would fix it but touches the whole UI.
- The lists the windows edit (Exceptions, Targets...) are fresh ConfigStore.Load() copies, never the engine's live lists, so no
  copy-on-write was needed.
- Alt+Tab: Shift+Tab (backwards) still counts as a forward step.

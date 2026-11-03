# E7: Memory Compression / Page Combining test. ADMIN PowerShell. A REBOOT is needed after each change.
# Usage:  .\Lab-MMAgent.ps1 -Action Status
#         .\Lab-MMAgent.ps1 -Action DisableCompression      (then reboot, run the same game session, compare CSVs)
#         .\Lab-MMAgent.ps1 -Action EnableCompression
#         .\Lab-MMAgent.ps1 -Action DisablePageCombining | EnablePageCombining
# Change ONE setting per test. Community results conflict: only your own numbers count.
param([Parameter(Mandatory)][ValidateSet('Status','DisableCompression','EnableCompression','DisablePageCombining','EnablePageCombining')][string]$Action)
$log = Join-Path $env:APPDATA 'GameRamCap\lab\mmagent-log.txt'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
$before = Get-MMAgent | Out-String
switch ($Action) {
  'DisableCompression'    { Disable-MMAgent -MemoryCompression }
  'EnableCompression'     { Enable-MMAgent  -MemoryCompression }
  'DisablePageCombining'  { Disable-MMAgent -PageCombining }
  'EnablePageCombining'   { Enable-MMAgent  -PageCombining }
}
$after = Get-MMAgent | Out-String
"$(Get-Date -Format s)  $Action`nBEFORE:`n$before`nAFTER (takes effect after reboot):`n$after" | Tee-Object -FilePath $log -Append
Write-Host "Reboot now, then run your normal session with Lab experiment E0 and compare lab\system.csv." -ForegroundColor Yellow

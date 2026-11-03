# E4: route an app to the integrated GPU (or back). Same registry value that Settings > System > Display > Graphics writes.
# (Registry location is widely used but not an officially documented API - labelled "documented by behaviour".)
# Usage:  .\Lab-IGPU.ps1 -Show
#         .\Lab-IGPU.ps1 -Exe "C:\Users\you\AppData\Local\Discord\app-1.0.9xxx\Discord.exe" -Pref PowerSaving
#         .\Lab-IGPU.ps1 -Exe "<same path>" -Pref Default
# PowerSaving = iGPU (if you have one), HighPerformance = dGPU. Restart the app afterwards.
param([string]$Exe, [ValidateSet('Default','PowerSaving','HighPerformance')][string]$Pref = 'PowerSaving', [switch]$Show)
$key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
if ($Show) { if (Test-Path $key) { Get-ItemProperty $key | Format-List } else { "No per-app GPU preferences set." }; return }
if (-not $Exe -or -not (Test-Path $Exe)) { throw "Give -Exe with the full path of an existing .exe" }
New-Item -Path $key -Force | Out-Null
switch ($Pref) {
  'Default'         { Remove-ItemProperty -Path $key -Name $Exe -ErrorAction SilentlyContinue; "Removed preference for $Exe" }
  'PowerSaving'     { Set-ItemProperty -Path $key -Name $Exe -Value 'GpuPreference=1;'; "Set $Exe -> iGPU (power saving)" }
  'HighPerformance' { Set-ItemProperty -Path $key -Name $Exe -Value 'GpuPreference=2;'; "Set $Exe -> dGPU (high performance)" }
}
Write-Host "Restart the app, then compare apps.csv column gpu_ded_mb (dedicated VRAM of the dGPU) before/after." -ForegroundColor Yellow

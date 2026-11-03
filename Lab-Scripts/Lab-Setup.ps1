# Run in an ADMIN PowerShell. Creates a System Restore point and prints the current memory settings.
# (Windows allows one automatic restore point per 24h by default; System Protection must be ON for C:.)
$ErrorActionPreference = 'Stop'
Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue
Checkpoint-Computer -Description "Before GameRamCap Lab" -RestorePointType MODIFY_SETTINGS
Write-Host "Restore point created." -ForegroundColor Green
Write-Host "`n--- Memory management agent (compression / page combining) ---"
Get-MMAgent | Format-List
Write-Host "--- Pagefile ---"
Get-CimInstance Win32_PageFileUsage | Format-Table Name, AllocatedBaseSize, CurrentUsage, PeakUsage
Get-CimInstance Win32_ComputerSystem | Select-Object AutomaticManagedPagefile | Format-List
Write-Host "--- RAM / GPU ---"
Get-CimInstance Win32_PhysicalMemory | Measure-Object Capacity -Sum | ForEach-Object { "RAM GB: " + [math]::Round($_.Sum/1GB,1) }
Get-CimInstance Win32_VideoController | Format-Table Name, AdapterRAM, DriverVersion
(Get-CimInstance Win32_OperatingSystem).Caption
(Get-CimInstance Win32_Processor).Name

# Runs LinkWatch with a pretend headset and saves window captures, for testing without the hardware.
# Usage: .\tools\simulate.ps1 -Name "Oculus Quest 2" -Speed "USB 2.0 (High Speed, 480 Mbps) (device is USB 3 capable, port/cable is at USB 2)" -PcPort USB-C -Hubs "Generic USB Hub" -Out quest2
param([string]$Name = "Oculus Quest 2", [string]$Speed = "USB 3.x Gen 1 (5 Gbps, SuperSpeed)",
      [string]$PcPort = "USB-A", [string]$Hubs = "", [string]$Out = "sim")
$exe = Join-Path (Split-Path $PSScriptRoot) "LinkWatch.exe"
# the real instance must not be running (single instance)
Stop-ScheduledTask -TaskName "LinkWatch" -ErrorAction SilentlyContinue
Get-Process LinkWatch -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1
"main","history","settings" | ForEach-Object { Remove-Item "$env:TEMP\LinkWatch_$_.png" -ErrorAction SilentlyContinue }
$env:LINKWATCH_SIMULATE = "$Name;$Speed;$PcPort;$Hubs"
# a plain (non-admin) start would hand over to the scheduled task, so run it directly through the CLR path:
$psi = New-Object Diagnostics.ProcessStartInfo $exe; $psi.UseShellExecute = $false
$psi.EnvironmentVariables["LINKWATCH_SIMULATE"] = $env:LINKWATCH_SIMULATE
$psi.EnvironmentVariables["LINKWATCH_NO_TASK"] = "1"
$p = [Diagnostics.Process]::Start($psi)
Start-Sleep 4
& $exe --screenshot | Out-Null
$t = 0; while (-not (Test-Path "$env:TEMP\LinkWatch_settings.png") -and $t -lt 20) { Start-Sleep 1; $t++ }
Copy-Item "$env:TEMP\LinkWatch_main.png" (Join-Path $env:TEMP "LinkWatch_$Out.png") -Force
Stop-Process $p -Force
Join-Path $env:TEMP "LinkWatch_$Out.png"

# Dev tool (run as admin, LinkWatch stopped): runs the EtwUsb counter against the connected Quest for a few seconds.
param([int]$Seconds = 8)
$log = Join-Path $env:TEMP "lw_counter.txt"
try {
    $src = Join-Path (Split-Path $PSScriptRoot) "src"
    Add-Type -Path "$src\EtwUsb.cs", "$src\UsbInfo.cs" -ReferencedAssemblies System.dll, System.Core.dll
    $link = [UsbInfo]::Get()
    "quest: connected=$($link.Connected) $($link.Device) $($link.Speed) where=$($link.Where)" | Set-Content $log
    $e = New-Object EtwUsb
    "start: $($e.Start()) $($e.LastError)" | Add-Content $log
    $e.SetTarget($link.RootHubId, $link.Ports)
    1..$Seconds | ForEach-Object {
        Start-Sleep 1; $v = $e.Take()
        "s$_ resolved=$($e.TargetResolved) out=$($v[0]) in=$($v[1]) events=$($e.EventsSeen) lost=$($e.Lost())" | Add-Content $log
    }
    $e.Describe() | Add-Content $log
    $e.Stop(); "stopped" | Add-Content $log
} catch { "ERROR: $_" | Add-Content $log }

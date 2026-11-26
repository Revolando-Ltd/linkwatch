# Dev tool (run as admin): records 4 s of xHCI ETW events including the rundown, as XML, to study the event fields.
$out = Join-Path $env:TEMP "lw_schema"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out | Remove-Item -Force -ErrorAction SilentlyContinue
logman stop LwSchema -ets 2>$null | Out-Null; if ($args -contains "-stopLinkWatch") { logman stop LinkWatchUsb -ets 2>$null | Out-Null; logman stop OvrMonitorUsb -ets 2>$null | Out-Null }
logman start LwSchema -ets -o "$out\x.etl" -bs 1024 -nb 64 256 -p "{36DA592D-E43A-4E28-AF6F-4BC57C5A11E8}" 0xFFFFFFFFFFFFFFFF 0xFF | Set-Content "$out\log.txt"
logman update LwSchema -ets -p "{30E1D284-5D88-459C-83FD-6345B39B19EC}" 0xFFFFFFFFFFFFFFFF 0xFF | Add-Content "$out\log.txt"
Start-Sleep 4
logman query LwSchema -ets | Add-Content "$out\log.txt"; logman stop LwSchema -ets | Add-Content "$out\log.txt"
tracerpt "$out\x.etl" -o "$out\x.xml" -of XML -y | Add-Content "$out\log.txt"
"done" | Set-Content "$out\done.txt"





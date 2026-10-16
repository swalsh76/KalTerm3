<#
  S-1a: one labelled run of all three flush-path measurements, so cache-on / cache-off / cache-on
  (A-B-A) runs are comparable. Throwaway spike script. Output also saved to <Dir>\runs\<Label>.txt.
  Usage: run-all.ps1 -Label A1-cache-on -DiskSpd <diskspd.exe> [-Dir C:\KalTermSpikes\s1]
#>
param(
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)][string]$DiskSpd,
    [string]$Dir = 'C:\KalTermSpikes\s1'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sqlcmd = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'
New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'runs') | Out-Null
$log = Join-Path $Dir "runs\$Label.txt"

& {
    "=== $Label  $(Get-Date -Format s) ==="
    Get-PhysicalDisk | Get-StorageAdvancedProperty | ForEach-Object {
        "device cache enabled: $($_.IsDeviceCacheEnabled)   power protected: $($_.IsPowerProtected)" }
    ""
    "--- diskspd (10 s per run)"
    & "$here\run-diskspd.ps1" -DiskSpd $DiskSpd -Dir "$Dir\diskspd" -Seconds 10 | Out-String
    "--- explicit flush"
    & "$here\flush-latency.ps1" -Dir "$Dir\flush" -Iterations 2000 | Out-String
    "--- SQL commit latency"
    & $sqlcmd -S . -E -C -W -b -i "$here\commit-latency.sql" | Select-Object -Skip 4
} 2>&1 | Tee-Object -FilePath $log

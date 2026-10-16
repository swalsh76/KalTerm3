<#
  S-1a step 1: diskspd write latency by caching mode. Throwaway spike script.
  Usage: run-diskspd.ps1 -DiskSpd <path to diskspd.exe> -Dir <scratch dir> [-Seconds 15]
  Writes raw output to <Dir>\results\ and prints a summary table.
#>
param(
    [Parameter(Mandatory)][string]$DiskSpd,
    [Parameter(Mandatory)][string]$Dir,
    [int]$Seconds = 15
)
$ErrorActionPreference = 'Stop'
$results = Join-Path $Dir 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$target = Join-Path $Dir 'dsp.dat'

# mode name, diskspd caching switch. Write-through (-Sw) asks the stack for FUA / flush-through.
$modes = @(
    @{ Name = 'buffered';          Switch = '-Sb' },
    @{ Name = 'nobuffer';          Switch = '-Su' },
    @{ Name = 'writethrough';      Switch = '-Sw' },
    @{ Name = 'nobuffer+through';  Switch = '-Sh' }
)
# pattern name, block size, random switch. Sequential 4K ~ log commits; random 8K ~ data pages.
$patterns = @(
    @{ Name = 'seq4K';  Block = '4K'; Random = @() },
    @{ Name = 'rand8K'; Block = '8K'; Random = @('-r8K') }
)

$summary = foreach ($p in $patterns) {
    foreach ($m in $modes) {
        $tag = "$($p.Name)-$($m.Name)" -replace '[^A-Za-z0-9+_-]', '_'
        $out = Join-Path $results "$tag.txt"
        $args = @("-b$($p.Block)", '-w100', '-t1', '-o1', '-L', '-Z1M', "-d$Seconds", '-c2G', $m.Switch) + $p.Random + @($target)
        & $DiskSpd @args | Out-File -Encoding utf8 $out
        $text = Get-Content $out
        # Total row of the "Total IO" table: thread | bytes | I/Os | MiB/s | I/O per s | AvgLat | LatStdDev | file
        $row = $text | Select-String -Pattern '^total:\s+\d+' | Select-Object -First 1
        $cols = ($row.Line -replace '^total:\s*', '') -split '\s*\|\s*'
        [pscustomobject]@{
            Pattern = $p.Name; Mode = $m.Name
            IOPS = [double]$cols[3]; AvgLatMs = [double]$cols[4]; StdDevMs = [double]$cols[5]
        }
    }
}
Remove-Item -Force $target -ErrorAction SilentlyContinue
$summary | Format-Table -AutoSize
$summary | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $results 'summary.json')

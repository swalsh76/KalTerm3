<#
  S-1a step 1b: latency of a 4K write followed by an explicit flush (FlushFileBuffers), versus a
  write-through write with no explicit flush. Throwaway spike script.
  The reference found a plain fsync ~70x faster than the drive's real flush on macOS; this is the
  Windows analogue. Usage: flush-latency.ps1 -Dir <scratch dir> [-Iterations 3000]
#>
param(
    [Parameter(Mandatory)][string]$Dir,
    [int]$Iterations = 3000
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Dir | Out-Null

function Measure-Writes([string]$Name, [IO.FileOptions]$Options, [bool]$FlushEach) {
    $path = Join-Path $Dir "flush-$Name.dat"
    $buf = New-Object byte[] 4096
    [Random]::new(42).NextBytes($buf)
    $fs = [IO.FileStream]::new($path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, $Options)
    try {
        $fs.SetLength(64MB)
        for ($i = 0; $i -lt 300; $i++) {                       # warm up
            $fs.Position = ($i % 1000) * 4096; $fs.Write($buf, 0, 4096); if ($FlushEach) { $fs.Flush($true) }
        }
        $lat = New-Object double[] $Iterations
        $sw = [Diagnostics.Stopwatch]::new()
        for ($i = 0; $i -lt $Iterations; $i++) {
            $fs.Position = ($i % 1000) * 4096
            $sw.Restart()
            $fs.Write($buf, 0, 4096)
            if ($FlushEach) { $fs.Flush($true) }               # FlushFileBuffers
            $lat[$i] = $sw.Elapsed.TotalMilliseconds
        }
    } finally { $fs.Dispose(); Remove-Item -Force $path -ErrorAction SilentlyContinue }
    $sorted = $lat | Sort-Object
    [pscustomobject]@{
        Mode = $Name; Iterations = $Iterations
        MeanMs = [math]::Round(($lat | Measure-Object -Average).Average, 4)
        P50Ms = [math]::Round($sorted[[int]($Iterations * 0.50)], 4)
        P99Ms = [math]::Round($sorted[[int]($Iterations * 0.99)], 4)
        PerSec = [math]::Round($Iterations / (($lat | Measure-Object -Sum).Sum / 1000))
    }
}

$r = @(
    Measure-Writes 'write-through-no-flush' ([IO.FileOptions]::WriteThrough) $false
    Measure-Writes 'write-through+flush'    ([IO.FileOptions]::WriteThrough) $true
    Measure-Writes 'buffered+flush'         ([IO.FileOptions]::None)         $true
    Measure-Writes 'buffered-no-flush'      ([IO.FileOptions]::None)         $false
)
$r | Format-Table -AutoSize

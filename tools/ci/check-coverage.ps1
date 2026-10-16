<#
  Fails (exit 1) if line coverage in any Cobertura report under -Path is below -MinLinePercent.
  coverlet.MTP's own threshold option is not yet supported, so CI uses this instead. One threshold, one place.

    ./tools/ci/check-coverage.ps1 -Path TestResults -MinLinePercent 90
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateRange(0, 100)][double]$MinLinePercent = 90
)

$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -Path $Path -Recurse -Filter 'coverage.cobertura*.xml' -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    Write-Error "No Cobertura coverage reports found under '$Path'. Did the tests run with --coverlet?"
    exit 1
}

$failed = $false
foreach ($report in $reports) {
    $xml = [xml](Get-Content -LiteralPath $report.FullName -Raw)
    $line = [double]::Parse($xml.coverage.'line-rate', [Globalization.CultureInfo]::InvariantCulture) * 100
    $branch = [double]::Parse($xml.coverage.'branch-rate', [Globalization.CultureInfo]::InvariantCulture) * 100
    $status = if ($line -ge $MinLinePercent) { 'ok  ' } else { 'FAIL' }
    '{0} {1:N1}% lines (minimum {2:N1}%), {3:N1}% branches: {4}' -f $status, $line, $MinLinePercent, $branch, $report.Name
    if ($line -lt $MinLinePercent) { $failed = $true }
}

if ($failed) {
    Write-Error "Line coverage is below the $MinLinePercent% gate."
    exit 1
}

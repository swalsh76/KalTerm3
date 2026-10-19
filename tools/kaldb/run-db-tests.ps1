<#
.SYNOPSIS
Runs the database-backed test suites on the target server (kaldb) against its real SQL Server instance.

.DESCRIPTION
Publishes the Data, Ingest and Api test projects self-contained with coverage off (coverlet.MTP is not in a publish's deps.json), copies only
the files whose SHA-256 differs to the server over WinRM, then runs each suite there as SYSTEM through a temporary scheduled task
(non-interactive, shared memory) and prints each summary. Run from the repo root. See docs/BUILD-LOG.md 5.1.

For each suite it writes a small runner (C:\KalTermSpikes\m3\run-<suite>.ps1) on the server that points KALTERM_TEST_SQL_SERVER at the instance, changes
into the suite's folder and runs its .exe. Nothing about the server (address, credential file) is stored in the repository: pass them in, or set
KALTERM_TEST_HOST and KALTERM_TEST_CRED in your own environment.

.PARAMETER ComputerName
The server's address or name. Defaults to $env:KALTERM_TEST_HOST.

.PARAMETER CredentialPath
An Export-Clixml credential file for a local administrator on the server. Defaults to $env:KALTERM_TEST_CRED.

.PARAMETER Suites
Which suites to run: data, ingest, api. Default all three.

.PARAMETER Instance
The SQL Server instance the tests use on the server. Default lpc:.\KALTERM.
#>
param(
    [string]$ComputerName = $env:KALTERM_TEST_HOST,
    [string]$CredentialPath = $env:KALTERM_TEST_CRED,
    [ValidateSet('data', 'ingest', 'api')][string[]]$Suites = @('data', 'ingest', 'api'),
    [string]$Instance = 'lpc:.\KALTERM'
)
$ErrorActionPreference = 'Stop'
if (-not $ComputerName -or -not $CredentialPath) {
    throw 'Pass -ComputerName and -CredentialPath, or set KALTERM_TEST_HOST and KALTERM_TEST_CRED.'
}
if (-not (Test-Path $CredentialPath)) {
    throw "Credential file not found: $CredentialPath"
}

$map = @{
    data   = @('tests\KalTerm.Data.Tests\KalTerm.Data.Tests.csproj', 'data-tests-nocov', 'KalTerm.Data.Tests.exe')
    ingest = @('tests\KalTerm.Ingest.Tests\KalTerm.Ingest.Tests.csproj', 'ingest-tests-nocov', 'KalTerm.Ingest.Tests.exe')
    api    = @('tests\KalTerm.Api.Tests\KalTerm.Api.Tests.csproj', 'api-tests-nocov', 'KalTerm.Api.Tests.exe')
}
$work = 'C:\KalTermSpikes\m3'   # the same folder on this machine (publish output) and on the server

foreach ($n in $Suites) {
    dotnet publish $map[$n][0] -c Release -r win-x64 --self-contained -p:NoCoverage=true -o "$work\$($map[$n][1])" 2>&1 |
        Select-String 'error' | Select-Object -First 5
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $n" }
}
dotnet restore KalTerm.sln | Out-Null   # a win-x64 publish rewrites project.assets.json; later builds need it back

$session = New-PSSession -ComputerName $ComputerName -Credential (Import-Clixml $CredentialPath)
try {
    $hash = { param($r) $h = @{}; Get-ChildItem $r -Recurse -File | ForEach-Object { $h[$_.FullName.Substring($r.Length + 1)] = (Get-FileHash $_.FullName).Hash }; $h }
    $bad = 0
    foreach ($n in $Suites) {
        $root = "$work\$($map[$n][1])"
        $local = & $hash $root
        $remote = Invoke-Command -Session $session -ArgumentList $root -ScriptBlock $hash
        $todo = @($local.Keys | Where-Object { $remote[$_] -ne $local[$_] })
        foreach ($f in $todo) {
            $dest = Join-Path $root $f
            Invoke-Command -Session $session -ArgumentList (Split-Path $dest) -ScriptBlock { param($p) New-Item -ItemType Directory -Force $p | Out-Null }
            Copy-Item (Join-Path $root $f) -Destination $dest -ToSession $session -Force
        }

        $after = Invoke-Command -Session $session -ArgumentList $root -ScriptBlock $hash
        $bad += @($local.Keys | Where-Object { $after[$_] -ne $local[$_] }).Count
        "$n : copied $($todo.Count) of $($local.Count) files"
    }

    if ($bad -ne 0) { throw "$bad files differ after the copy; not running" }

    $runners = @{}
    foreach ($n in $Suites) {
        $runners[$n] = "`$env:KALTERM_TEST_SQL_SERVER = '$Instance'`r`nSet-Location '$work\$($map[$n][1])'`r`n& .\$($map[$n][2])`r`nexit `$LASTEXITCODE`r`n"
    }

    Invoke-Command -Session $session -ArgumentList $Suites, $work, $runners -ScriptBlock {
        param($suites, $dir, $runners)
        foreach ($n in $suites) {
            Set-Content "$dir\run-$n.ps1" $runners[$n] -Encoding ASCII
            Remove-Item "$dir\last-$n.txt" -ErrorAction SilentlyContinue
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command `"& '$dir\run-$n.ps1' *> '$dir\last-$n.txt'`""
            Register-ScheduledTask -TaskName "kt-run-$n" -Action $action -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest) -Force | Out-Null
            try {
                Start-ScheduledTask -TaskName "kt-run-$n"
                $deadline = (Get-Date).AddMinutes(8)   # bounded: an unwatched run must not hang
                do { Start-Sleep 3 } while ((Get-ScheduledTask -TaskName "kt-run-$n").State -eq 'Running' -and (Get-Date) -lt $deadline)
                $result = (Get-ScheduledTaskInfo -TaskName "kt-run-$n").LastTaskResult
            }
            finally {
                Unregister-ScheduledTask -TaskName "kt-run-$n" -Confirm:$false
            }

            "=== $n : exit $result"
            $text = Get-Content "$dir\last-$n.txt"
            $text | Where-Object { $_ -match '^\s*failed ' } | ForEach-Object { $_.Substring(0, [Math]::Min(230, $_.Length)) }
            $text | Select-Object -Last 8
        }
    }
}
finally {
    Remove-PSSession $session
}

<#
.SYNOPSIS
Deploys a published ingest host to the server and runs it there as a scheduled task, from a release folder that stays pinned until you change it.

.DESCRIPTION
Syncs a local `dotnet publish` folder (self-contained win-x64 of src/KalTerm.Ingest.Host) by SHA-256 into C:\KalTerm\ingest\releases\<name> on the server,
installs the run wrapper and the status script, registers the task `KalTerm-Ingest` (SYSTEM, at boot, restarts on failure, no time limit) when absent, and points
release.txt at the release. A running ingest keeps running from the release it started from; -Restart stops it (a hard stop: the next start logs the downtime as
`ingest_down`) and starts it from the new release. Development and the soak do not block each other: builds never touch a release folder.

Secrets are not handled here. The Kalshi key file, KALSHI_ENV, KALSHI_KEY_ID and KALSHI_PRIVATE_KEY_PATH are set on the server separately (machine variables,
key file readable only by SYSTEM and Administrators), and no host, address or credential is stored in the repository.

.PARAMETER ReleaseDir
The published folder on this machine.

.PARAMETER Name
The release name on the server; defaults to the folder name.

.PARAMETER Restart
Stop the running task and start it from this release.
#>
param(
    [Parameter(Mandatory)][string]$ReleaseDir,
    [string]$ComputerName = $env:KALTERM_TEST_HOST,
    [string]$CredentialPath = $env:KALTERM_TEST_CRED,
    [string]$Name,
    [string]$Instance = 'lpc:.\KALTERM',
    [string]$Database = 'KalTerm',
    [int]$ProvisionMaxSizeGb = 100,
    [switch]$Restart
)
$ErrorActionPreference = 'Stop'
if (-not $ComputerName -or -not $CredentialPath) { throw 'Pass -ComputerName and -CredentialPath, or set KALTERM_TEST_HOST and KALTERM_TEST_CRED.' }
if (-not (Test-Path (Join-Path $ReleaseDir 'KalTerm.Ingest.Host.exe'))) { throw "No KalTerm.Ingest.Host.exe in $ReleaseDir" }
if (-not $Name) { $Name = Split-Path $ReleaseDir -Leaf }
$root = 'C:\KalTerm\ingest'

$wrapper = @"
`$ErrorActionPreference = 'Stop'
`$root = '$root'
`$release = (Get-Content "`$root\release.txt").Trim()
`$logDir = "`$root\logs"
New-Item -ItemType Directory -Force `$logDir | Out-Null
`$log = "`$logDir\ingest-`$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
Get-ChildItem `$logDir -Filter 'ingest-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 30 | Remove-Item -Force
`$env:KALTERM_Ingest__Server = '$Instance'
`$env:KALTERM_Ingest__Database = '$Database'
`$env:KALTERM_Ingest__ProvisionMaxSizeGb = '$ProvisionMaxSizeGb'
Set-Location "`$root\releases\`$release"
"`$(Get-Date -Format o) starting release `$release" | Out-File `$log -Append -Encoding ascii
cmd.exe /c "KalTerm.Ingest.Host.exe >> ""`$log"" 2>&1"
exit `$LASTEXITCODE
"@

$session = New-PSSession -ComputerName $ComputerName -Credential (Import-Clixml $CredentialPath)
try {
    Invoke-Command -Session $session -ArgumentList $root -ScriptBlock { param($r) New-Item -ItemType Directory -Force "$r\releases\", "$r\logs" | Out-Null }

    $hash = { param($r) $h = @{}; if (Test-Path $r) { Get-ChildItem $r -Recurse -File | ForEach-Object { $h[$_.FullName.Substring($r.Length + 1)] = (Get-FileHash $_.FullName).Hash } }; $h }
    $remoteDir = "$root\releases\$Name"
    $local = & $hash $ReleaseDir
    $remote = Invoke-Command -Session $session -ArgumentList $remoteDir -ScriptBlock $hash
    $todo = @($local.Keys | Where-Object { $remote[$_] -ne $local[$_] })
    foreach ($f in $todo) {
        $dest = Join-Path $remoteDir $f
        Invoke-Command -Session $session -ArgumentList (Split-Path $dest) -ScriptBlock { param($p) New-Item -ItemType Directory -Force $p | Out-Null }
        Copy-Item (Join-Path $ReleaseDir $f) -Destination $dest -ToSession $session -Force
    }

    $after = Invoke-Command -Session $session -ArgumentList $remoteDir -ScriptBlock $hash
    $bad = @($local.Keys | Where-Object { $after[$_] -ne $local[$_] }).Count
    if ($bad -ne 0) { throw "$bad files differ after the copy; not switching releases" }
    "release $Name : copied $($todo.Count) of $($local.Count) files"

    Copy-Item (Join-Path $PSScriptRoot 'ingest-status.ps1') -Destination "$root\status.ps1" -ToSession $session -Force
    Invoke-Command -Session $session -ArgumentList $root, $wrapper, $Name, $Restart.IsPresent -ScriptBlock {
        param($root, $wrapper, $name, $restart)
        Set-Content "$root\run.ps1" $wrapper -Encoding ASCII
        Set-Content "$root\release.txt" $name -Encoding ASCII
        if (-not (Get-ScheduledTask -TaskName 'KalTerm-Ingest' -ErrorAction SilentlyContinue)) {
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$root\run.ps1`""
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName 'KalTerm-Ingest' -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest) | Out-Null
            'task KalTerm-Ingest registered'
        }

        if ($restart) {
            Stop-ScheduledTask -TaskName 'KalTerm-Ingest' -ErrorAction SilentlyContinue
            Get-Process -Name 'KalTerm.Ingest.Host' -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-ScheduledTask -TaskName 'KalTerm-Ingest'
            'task started'
        }
    }
}
finally {
    Remove-PSSession $session
}

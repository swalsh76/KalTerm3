<#
.SYNOPSIS
Deploys a published KalTerm service (the ingest host or the read-only API) to the server and runs it there as a scheduled task, from a release folder that stays pinned until you change it.

.DESCRIPTION
Syncs a local `dotnet publish` folder (self-contained win-x64 of src/KalTerm.Ingest.Host or src/KalTerm.Api.Host) by SHA-256 into C:\KalTerm\<service>\releases\<name>
on the server, installs the run wrapper, registers the task (SYSTEM, at boot, no time limit) when absent, and points release.txt at the release. The wrapper waits
(bounded) for the SQL Server service, then supervises the host: any exit other than code 0 is retried after a capped, growing wait. A running service keeps running from
the release it started from; -Restart stops it (a hard stop) and starts it from the new release. Builds never touch a release folder, so development and a running
service do not block each other.

  ingest   C:\KalTerm\ingest   task KalTerm-Ingest   also installs the status script (ingest-status.ps1) as status.ps1
  api      C:\KalTerm\api      task KalTerm-Api       listens on loopback only (the process refuses anything else)

Secrets are not handled here. For the ingest, the Kalshi key file and KALSHI_* variables are set on the server separately (machine variables, key file readable only
by SYSTEM and Administrators). No host, address or credential is stored in the repository.

.PARAMETER Service
ingest or api.

.PARAMETER ReleaseDir
The published folder on this machine.

.PARAMETER Name
The release name on the server; defaults to the folder name.

.PARAMETER WatchList
Ingest only: the tickers whose order books are built and published (Ingest:WatchList, at most 100). Omitted means no order book subscription.

.PARAMETER BackupTarget
Ingest only: the folder the daily backups go to as the SERVER sees it (Ingest:BackupTarget), a folder or a \host\shareolder path. Omitted means no backups. The folder
must carry the marker written by `tools\kaldbdmin.ps1 -- backup init-target`, and for a share the credential must be stored (`admin.ps1 -SetShareCredential`).

.PARAMETER Restart
Stop the running task and start it from this release.

.PARAMETER PrintWrapper
Print the run wrapper that would be installed and exit; touches nothing.
#>
param(
    [Parameter(Mandatory)][ValidateSet('ingest', 'api')][string]$Service,
    [Parameter(Mandatory)][string]$ReleaseDir,
    [string]$ComputerName = $env:KALTERM_TEST_HOST,
    [string]$CredentialPath = $env:KALTERM_TEST_CRED,
    [string]$Name,
    [string]$Instance = 'lpc:.\KALTERM',
    [string]$Database = 'KalTerm',
    [int]$ProvisionMaxSizeGb = 500,
    [string]$ApiUrl = 'http://127.0.0.1:5280',
    [string[]]$WatchList = @(),
    [string]$BackupTarget,
    [switch]$Restart,
    [switch]$PrintWrapper
)
$ErrorActionPreference = 'Stop'

$services = @{
    ingest = @{
        Root = 'C:\KalTerm\ingest'; Task = 'KalTerm-Ingest'; Exe = 'KalTerm.Ingest.Host.exe'; Prefix = 'ingest'; Status = 'ingest-status.ps1'
        Env  = @("`$env:KALTERM_Ingest__Server = '$Instance'", "`$env:KALTERM_Ingest__Database = '$Database'", "`$env:KALTERM_Ingest__ProvisionMaxSizeGb = '$ProvisionMaxSizeGb'")
    }
    api    = @{
        Root = 'C:\KalTerm\api'; Task = 'KalTerm-Api'; Exe = 'KalTerm.Api.Host.exe'; Prefix = 'api'; Status = $null
        Env  = @("`$env:KALTERM_Api__Server = '$Instance'", "`$env:KALTERM_Api__Database = '$Database'", "`$env:KALTERM_Api__Url = '$ApiUrl'")
    }
}
if ($WatchList.Count -gt 0) {
    if ($Service -ne 'ingest') { throw '-WatchList applies to the ingest service only.' }
    for ($i = 0; $i -lt $WatchList.Count; $i++) {
        if ($WatchList[$i] -notmatch '^[A-Za-z0-9._-]+$') { throw "Not a ticker: entry $i" }
        $services.ingest.Env += "`$env:KALTERM_Ingest__WatchList__$i = '$($WatchList[$i])'"
    }
}
if ($BackupTarget) {
    if ($Service -ne 'ingest') { throw '-BackupTarget applies to the ingest service only.' }
    if ($BackupTarget -notmatch "^[^'`"`r`n]+$") { throw 'The backup target must not contain quotes or line breaks.' }
    $services.ingest.Env += "`$env:KALTERM_Ingest__BackupTarget = '$BackupTarget'"
}
$spec = $services[$Service]

# lpc:.\NAME is the named instance NAME, whose service is MSSQL$NAME; anything else is taken to be the default instance.
$sqlService = if ($Instance -match '\\([^\\]+)$') { 'MSSQL$' + $Matches[1] } else { 'MSSQLSERVER' }

$template = @'
$ErrorActionPreference = 'Stop'
$root = '@@ROOT@@'
$release = (Get-Content "$root\release.txt").Trim()
$logDir = "$root\logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = "$logDir\@@PREFIX@@-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
Get-ChildItem $logDir -Filter '@@PREFIX@@-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 30 | Remove-Item -Force
@@ENV@@
function Write-Log($text) { "$(Get-Date -Format o) $text" | Out-File $log -Append -Encoding ascii }

# After a boot SQL Server may not be up yet (it is a delayed-start service): wait for it, bounded, then go on and let the host report if it is still down.
$sql = Get-Service '@@SQLSERVICE@@' -ErrorAction SilentlyContinue
if ($sql) {
    try { $sql.WaitForStatus('Running', [TimeSpan]::FromMinutes(10)); Write-Log "SQL Server service is $($sql.Status)" }
    catch { Write-Log "SQL Server service is still $($sql.Status) after 10 minutes; starting the host anyway" }
}

# Supervise the host: a clean exit (code 0, an orderly stop) ends this; any other exit is retried after a capped, growing wait.
$wait = 15
while ($true) {
    Set-Location "$root\releases\$release"
    Write-Log "starting release $release"
    cmd.exe /c "@@EXE@@ >> ""$log"" 2>&1"
    $code = $LASTEXITCODE
    Write-Log "host exited with code $code"
    if ($code -eq 0) { break }
    Write-Log "retrying in $wait s"
    Start-Sleep -Seconds $wait
    $wait = [Math]::Min(120, $wait * 2)
}
'@
$wrapper = $template.Replace('@@ROOT@@', $spec.Root).Replace('@@PREFIX@@', $spec.Prefix).Replace('@@ENV@@', ($spec.Env -join "`r`n")).Replace('@@SQLSERVICE@@', $sqlService).Replace('@@EXE@@', $spec.Exe)

if ($PrintWrapper) {
    $wrapper
    return
}

if (-not $ComputerName -or -not $CredentialPath) { throw 'Pass -ComputerName and -CredentialPath, or set KALTERM_TEST_HOST and KALTERM_TEST_CRED.' }
if (-not (Test-Path (Join-Path $ReleaseDir $spec.Exe))) { throw "No $($spec.Exe) in $ReleaseDir" }
if (-not $Name) { $Name = Split-Path $ReleaseDir -Leaf }
$root = $spec.Root

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

    if ($spec.Status) {
        Copy-Item (Join-Path $PSScriptRoot $spec.Status) -Destination "$root\status.ps1" -ToSession $session -Force
    }

    Invoke-Command -Session $session -ArgumentList $root, $wrapper, $Name, $Restart.IsPresent, $spec.Task -ScriptBlock {
        param($root, $wrapper, $name, $restart, $task)
        Set-Content "$root\run.ps1" $wrapper -Encoding ASCII
        Set-Content "$root\release.txt" $name -Encoding ASCII
        if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) {
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$root\run.ps1`""
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName $task -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest) | Out-Null
            "task $task registered"
        }

        if ($restart) {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            Get-Process | Where-Object { $_.Path -like "$root\releases\*" } | Stop-Process -Force
            Start-ScheduledTask -TaskName $task
            'task started'
        }
    }
}
finally {
    Remove-PSSession $session
}

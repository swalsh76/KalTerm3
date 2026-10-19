<#
.SYNOPSIS
Runs kalterm-admin (backup and restore) on the server (kaldb) through remoting.

.DESCRIPTION
Publishes src/KalTerm.Admin self-contained when asked (or when there is no published copy yet), syncs it by SHA-256 to C:\KalTerm\admin on the server, and runs
`kalterm-admin <arguments>` there with the same backup settings the ingest host uses. Output comes back as text; the exit code of kalterm-admin becomes this script's
exit code. Run from the repo root. Nothing about the server (address, credential file, share) is stored in the repository: pass them in, or set KALTERM_TEST_HOST and
KALTERM_TEST_CRED in your own environment.

Everything after the options below goes to kalterm-admin unchanged; put `--` first if its own options would be mistaken for this script's:

    .\tools\kaldb\admin.ps1 -- backup list
    .\tools\kaldb\admin.ps1 -- backup run --full
    .\tools\kaldb\admin.ps1 -- restore --database KalTerm_check

To store the credential for the backup share (you type the password here, on your own computer; it travels to the server over the remoting session and is
stored there protected for that machine, never in a file you can read and never in the repository):

    .\tools\kaldb\admin.ps1 -SetShareCredential

.PARAMETER Publish
Publish kalterm-admin again before syncing.

.PARAMETER BackupTarget
The backup folder as the SERVER sees it, for example \\nas\backups\KalTerm. Must match the ingest host's Ingest:BackupTarget.

.PARAMETER SetShareCredential
Ask for the share's user name and password and store them on the server (checks them against the share first).

.PARAMETER Arguments
The kalterm-admin command line.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$ComputerName = $env:KALTERM_TEST_HOST,
    [string]$CredentialPath = $env:KALTERM_TEST_CRED,
    [string]$BackupTarget = $env:KALTERM_BACKUP_TARGET,
    [string]$Instance = 'lpc:.\KALTERM',
    [string]$Database = 'KalTerm',
    [switch]$Publish,
    [switch]$SetShareCredential,
    [Parameter(Position = 0, ValueFromRemainingArguments)][string[]]$Arguments
)
$ErrorActionPreference = 'Stop'
if (-not $ComputerName -or -not $CredentialPath) { throw 'Pass -ComputerName and -CredentialPath, or set KALTERM_TEST_HOST and KALTERM_TEST_CRED.' }
if (-not (Test-Path $CredentialPath)) { throw "Credential file not found: $CredentialPath" }
if (-not $BackupTarget) { throw 'Pass -BackupTarget (the folder as the server sees it, for example \\host\share\KalTerm) or set KALTERM_BACKUP_TARGET.' }

$local = 'C:\KalTermSpikes\m3\admin'      # the published tool on this machine
$remote = 'C:\KalTerm\admin'               # and on the server

if ($Publish -or -not (Test-Path (Join-Path $local 'kalterm-admin.exe'))) {
    dotnet publish src\KalTerm.Admin\KalTerm.Admin.csproj -c Release -r win-x64 --self-contained -o $local 2>&1 | Select-String ' error ' | Select-Object -First 5
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
    dotnet restore KalTerm.sln | Out-Null   # a win-x64 publish rewrites project.assets.json; later builds need it back
}

$session = New-PSSession -ComputerName $ComputerName -Credential (Import-Clixml $CredentialPath)
try {
    $hash = { param($r) $h = @{}; if (Test-Path $r) { Get-ChildItem $r -Recurse -File | ForEach-Object { $h[$_.FullName.Substring($r.Length + 1)] = (Get-FileHash $_.FullName).Hash } }; $h }
    $have = & $hash $local
    $there = Invoke-Command -Session $session -ArgumentList $remote -ScriptBlock $hash
    foreach ($f in @($have.Keys | Where-Object { $there[$_] -ne $have[$_] })) {
        $dest = Join-Path $remote $f
        Invoke-Command -Session $session -ArgumentList (Split-Path $dest) -ScriptBlock { param($p) New-Item -ItemType Directory -Force $p | Out-Null }
        Copy-Item (Join-Path $local $f) -Destination $dest -ToSession $session -Force
    }

    $stdin = $null
    if ($SetShareCredential) {
        $share = Get-Credential -Message "Account on the backup share ($BackupTarget)"
        if (-not $share) { throw 'no credential given' }
        $Arguments = @('backup', 'set-credential', '--user', $share.UserName, '--password-stdin')
        $stdin = $share.GetNetworkCredential().Password
    }

    $result = Invoke-Command -Session $session -ArgumentList $remote, (, @($Arguments)), $BackupTarget, $Instance, $Database, $stdin -ScriptBlock {
        param($dir, $arguments, $target, $instance, $database, $stdin)
        $env:KALTERM_Ingest__BackupTarget = $target
        $env:KALTERM_Ingest__Server = $instance
        $env:KALTERM_Ingest__Database = $database
        # kalterm-admin's own error lines (stderr) are its output, not a failure of this script: carry them back tagged, then the exit code.
        $lines = if ($null -ne $stdin) { $stdin | & "$dir\kalterm-admin.exe" @arguments 2>&1 } else { & "$dir\kalterm-admin.exe" @arguments 2>&1 }
        $lines | ForEach-Object { [pscustomobject]@{ IsError = ($_ -is [System.Management.Automation.ErrorRecord]); Text = "$_" } }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE }
    }

    $code = 1
    foreach ($item in @($result)) {
        if ($null -ne $item.ExitCode) { $code = [int]$item.ExitCode }
        elseif ($item.IsError) { [Console]::Error.WriteLine($item.Text) }
        else { $item.Text }
    }

    exit $code
}
finally {
    Remove-PSSession $session
}

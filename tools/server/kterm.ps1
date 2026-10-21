<#
.SYNOPSIS
Runs the kterm CLI on the server (srv01) through remoting, where the API listens on loopback only (decision D5).

.DESCRIPTION
Publishes src/KalTerm.Cli self-contained when asked (or when there is no published copy yet), syncs it by SHA-256 to C:\KalTerm\cli on the server, and runs
`kterm <arguments>` there. The output comes back as text; the exit code of kterm becomes this script's exit code (0 fine, 1 failed, 2 status found problems).
Run from the repo root. Nothing about the server (address, credential file) is stored in the repository: pass them in, or set KALTERM_TEST_HOST and
KALTERM_TEST_CRED in your own environment.

Everything after the options below goes to kterm unchanged, so put `--` first if kterm's own options (for example --json) would be mistaken for this script's:

    .\tools\server\kterm.ps1 -ComputerName <host> -CredentialPath <file> -- status --json

.PARAMETER Publish
Publish the CLI again before syncing (otherwise a published copy from an earlier run is reused).

.PARAMETER Arguments
The kterm command line.
#>
param(
    [string]$ComputerName = $env:KALTERM_TEST_HOST,
    [string]$CredentialPath = $env:KALTERM_TEST_CRED,
    [switch]$Publish,
    [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
)
$ErrorActionPreference = 'Stop'
if (-not $ComputerName -or -not $CredentialPath) { throw 'Pass -ComputerName and -CredentialPath, or set KALTERM_TEST_HOST and KALTERM_TEST_CRED.' }
if (-not (Test-Path $CredentialPath)) { throw "Credential file not found: $CredentialPath" }

$local = 'C:\KalTermSpikes\m3\kterm'      # the published CLI on this machine
$remote = 'C:\KalTerm\cli'                 # and on the server

if ($Publish -or -not (Test-Path (Join-Path $local 'kterm.exe'))) {
    dotnet publish src\KalTerm.Cli\KalTerm.Cli.csproj -c Release -r win-x64 --self-contained -o $local 2>&1 | Select-String ' error ' | Select-Object -First 5
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

    $result = Invoke-Command -Session $session -ArgumentList $remote, (, @($Arguments)) -ScriptBlock {
        param($dir, $arguments)
        # kterm's own error lines (stderr) are its output, not a failure of this script: carry them back tagged, then the exit code.
        & "$dir\kterm.exe" @arguments 2>&1 | ForEach-Object { [pscustomobject]@{ IsError = ($_ -is [System.Management.Automation.ErrorRecord]); Text = "$_" } }
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

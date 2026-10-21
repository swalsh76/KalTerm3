<#
.SYNOPSIS
Deploys a published ingest host to the server. A thin wrapper over deploy-service.ps1 -Service ingest; see that script for everything it does.

.PARAMETER ReleaseDir
The published folder of src/KalTerm.Ingest.Host on this machine.

.PARAMETER Restart
Stop the running task and start it from this release (a hard stop).
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
& "$PSScriptRoot\deploy-service.ps1" -Service ingest @PSBoundParameters

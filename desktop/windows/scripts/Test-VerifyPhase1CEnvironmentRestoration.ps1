[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ProcessEnvironmentSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    $snapshots = @{}
    foreach ($name in $Names) {
        $snapshots[$name] = [pscustomobject]@{
            Exists = Test-Path -LiteralPath "Env:\$name"
            Value = [Environment]::GetEnvironmentVariable(
                $name,
                [EnvironmentVariableTarget]::Process)
        }
    }

    return $snapshots
}

function Restore-ProcessEnvironmentSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Snapshots
    )

    $failures = [System.Collections.Generic.List[System.Exception]]::new()
    foreach ($name in $Snapshots.Keys) {
        try {
            $snapshot = $Snapshots[$name]
            $value = if ($snapshot.Exists) { $snapshot.Value } else { $null }
            [Environment]::SetEnvironmentVariable(
                $name,
                $value,
                [EnvironmentVariableTarget]::Process)
        }
        catch {
            $failures.Add($_.Exception)
        }
    }

    if ($failures.Count -ne 0) {
        throw [AggregateException]::new(
            'The regression test could not restore its caller environment.',
            $failures.ToArray())
    }
}

function Assert-ProcessEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Expected
    )

    if (-not (Test-Path -LiteralPath "Env:\$Name")) {
        throw "Expected process environment variable to exist after verification: $Name"
    }

    $actual = [Environment]::GetEnvironmentVariable(
        $Name,
        [EnvironmentVariableTarget]::Process)
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "Process environment variable was not restored exactly: $Name"
    }
}

function Assert-ProcessEnvironmentAbsent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (Test-Path -LiteralPath "Env:\$Name") {
        throw "Process environment variable leaked after failed verification: $Name"
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$verifyScript = Join-Path $PSScriptRoot 'Verify-Phase1C.ps1'
$postgresBin = Join-Path $repoRoot '.tools\postgresql\16\bin'
$postgresExe = Join-Path $postgresBin 'postgres.exe'
$nodeCommand = Get-Command -Name 'node.exe' -CommandType Application -ErrorAction Stop
$nodeExe = [System.IO.Path]::GetFullPath($nodeCommand.Source)

if (-not (Test-Path -LiteralPath $postgresExe -PathType Leaf)) {
    throw "The regression test requires the repository PostgreSQL executable: $postgresExe"
}
if (-not (Test-Path -LiteralPath $nodeExe -PathType Leaf)) {
    throw "The regression test requires a valid Node executable: $nodeExe"
}

$verificationVariables = @(
    'EBAYCRM_POSTGRES_BIN',
    'EBAYCRM_RELEASE_ACCEPTANCE',
    'EBAYCRM_NODE_EXE'
)
$callerSnapshot = Get-ProcessEnvironmentSnapshot -Names @(
    $verificationVariables + 'Path'
)

try {
    $sentinels = @{
        EBAYCRM_POSTGRES_BIN = 'phase1c-sentinel-postgres'
        EBAYCRM_RELEASE_ACCEPTANCE = 'phase1c-sentinel-acceptance'
        EBAYCRM_NODE_EXE = 'phase1c-sentinel-node'
    }
    foreach ($name in $verificationVariables) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $sentinels[$name],
            [EnvironmentVariableTarget]::Process)
    }

    & $verifyScript `
        -PublishedNodeProbeOnly `
        -PostgresBin $postgresBin `
        -NodeExe $nodeExe

    foreach ($name in $verificationVariables) {
        Assert-ProcessEnvironmentValue -Name $name -Expected $sentinels[$name]
    }

    foreach ($name in $verificationVariables) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }

    $env:Path = [System.IO.Path]::GetDirectoryName($nodeExe)
    $verificationFailure = $null
    try {
        & $verifyScript `
            -PublishedNodeProbeOnly `
            -PostgresBin $postgresBin `
            -NodeExe $nodeExe
    }
    catch {
        $verificationFailure = $_
    }

    if ($null -eq $verificationFailure) {
        throw 'Expected verification to fail after PATH was restricted.'
    }
    if ($verificationFailure.Exception.Message.IndexOf(
            'dotnet',
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'Restricted PATH did not fail at the expected post-mutation dotnet command.'
    }

    foreach ($name in $verificationVariables) {
        Assert-ProcessEnvironmentAbsent -Name $name
    }

    Write-Host 'Phase 1C process environment restoration regression passed.'
}
finally {
    Restore-ProcessEnvironmentSnapshot -Snapshots $callerSnapshot
}

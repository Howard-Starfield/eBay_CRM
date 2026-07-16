[CmdletBinding()]
param(
    [datetime]$RunStartUtc = [datetime]::UtcNow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$publishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'desktop\windows\artifacts\win-x64'))
$runStart = $RunStartUtc.ToUniversalTime()
$findings = [System.Collections.Generic.List[string]]::new()

$requiredPublishFiles = @(
    'HowardLab.EbayCrm.AppHost.exe',
    'HowardLab.EbayCrm.AppHost.Fixture.exe',
    'HowardLab.EbayCrm.AppHost.Fixture.dll',
    'HowardLab.EbayCrm.AppHost.Fixture.deps.json',
    'HowardLab.EbayCrm.AppHost.Fixture.runtimeconfig.json',
    'migrations\0001_apphost_control.sql',
    'hostfxr.dll',
    'coreclr.dll',
    'System.Private.CoreLib.dll',
    'node-probe\node.exe',
    'node-probe\package.json',
    'node-probe\node-payload-manifest-v1.json',
    'node-probe\app\control\apphost-control-client.js',
    'node-probe\app\control\identity-health-server.js',
    'node-probe\app\probes\probe-orchestrator.js',
    'node-probe\app\probes\server-probe.js',
    'node-probe\app\probes\worker-probe.js',
    'node-probe\app\protocol\control-protocol.js'
)

foreach ($fileName in $requiredPublishFiles) {
    $requiredPath = Join-Path $publishRoot $fileName
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        $findings.Add("Required publish file is missing: $requiredPath")
    }
}

$rootForms = @(
    $repoRoot.TrimEnd('\'),
    $repoRoot.TrimEnd('\').Replace('\', '/')
) | Select-Object -Unique

$candidateProcesses = Get-CimInstance -ClassName Win32_Process | Where-Object {
    $name = [string]$_.Name
    $name.Equals('postgres.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
    $name.Equals('pg_ctl.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
    $name.Equals('initdb.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
    $name.Equals('node.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
    $name.Equals('cmd.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
    $name.Equals('conhost.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
    $name.StartsWith('HowardLab.EbayCrm.AppHost', [System.StringComparison]::OrdinalIgnoreCase)
}

foreach ($process in $candidateProcesses) {
    $commandLine = [string]$process.CommandLine
    if ([string]::IsNullOrWhiteSpace($commandLine)) {
        continue
    }

    $containsRoot = $false
    foreach ($rootForm in $rootForms) {
        if ($commandLine.IndexOf($rootForm, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $containsRoot = $true
            break
        }
    }

    if ($containsRoot) {
        $findings.Add(
            "Matching-process scan found the repository/worktree root in an accessible command line: " +
            "name=$($process.Name), pid=$($process.ProcessId), commandLine=$commandLine")
    }
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$recentTempDirectories = Get-ChildItem -LiteralPath $tempRoot -Directory -Force -Filter 'ebaycrm-*' |
    Where-Object { $_.LastWriteTimeUtc -ge $runStart }

foreach ($directory in $recentTempDirectories) {
    $findings.Add(
        "Temporary directory modified since the verification run started: " +
        "$($directory.FullName) (lastWriteUtc=$($directory.LastWriteTimeUtc.ToString('O')))")
}

if ($findings.Count -ne 0) {
    foreach ($finding in $findings) {
        Write-Error $finding -ErrorAction Continue
    }

    throw "Phase 1C cleanup audit failed with $($findings.Count) finding(s)."
}

Write-Host (
    "Phase 1C cleanup audit passed: required publish files are present; " +
    "the matching-process scan found no accessible command line containing '$repoRoot'; " +
    "no ebaycrm-* temp directory " +
    "was modified on or after $($runStart.ToString('O')).")
Write-Warning (
    'The matching-process scan is non-exhaustive: inaccessible or null command lines are not ' +
    'observable, and descendant command lines that omit the repository/worktree root do not match.')

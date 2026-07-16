[CmdletBinding()]
param(
    [string]$PostgresBin,
    [string]$NodeExe,
    [switch]$PublishedNodeProbeOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Native command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

function Assert-NoReparsePointBelowRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TrustedRoot,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $root = [System.IO.Path]::GetFullPath($TrustedRoot).TrimEnd('\')
    $target = [System.IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "The trusted repository root is missing or is not a directory: $root"
    }

    $volumeRoot = [System.IO.Path]::GetPathRoot($root)
    if ([string]::IsNullOrWhiteSpace($volumeRoot)) {
        throw "The trusted repository root has no local volume root: $root"
    }

    $currentPath = $volumeRoot
    $rootRemainder = $root.Substring($volumeRoot.Length)
    foreach ($component in ($rootRemainder -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($component)) {
            continue
        }

        $currentPath = Join-Path $currentPath $component
        if (-not (Test-Path -LiteralPath $currentPath)) {
            throw "An existing trusted-root ancestor disappeared during cleanup validation: $currentPath"
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing recursive cleanup through reparse-point trusted-root ancestor: $currentPath"
        }
    }

    $rootPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to inspect a cleanup path outside the trusted repository root: $target"
    }

    $relativePath = $target.Substring($rootPrefix.Length)
    $currentPath = $root
    foreach ($component in ($relativePath -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($component)) {
            continue
        }

        $currentPath = Join-Path $currentPath $component
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing recursive cleanup through reparse-point component: $currentPath"
        }
    }
}

function Get-RelativePathUnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $canonicalRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $canonicalPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected root: root=$canonicalRoot path=$canonicalPath"
    }

    return $canonicalPath.Substring($prefix.Length).Replace('\', '/')
}

function Get-TreeFilesWithoutReparsePoints {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $canonicalRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $canonicalRoot -PathType Container)) {
        throw "Required tree root is missing: $canonicalRoot"
    }

    $rootItem = Get-Item -LiteralPath $canonicalRoot -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse-point tree root is not allowed: $canonicalRoot"
    }

    $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    $pending = [System.Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($canonicalRoot)
    while ($pending.Count -ne 0) {
        $directory = $pending.Dequeue()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse-point payload or source entry is not allowed: $($entry.FullName)"
            }

            if ($entry.PSIsContainer) {
                $pending.Enqueue($entry.FullName)
            }
            else {
                $files.Add([System.IO.FileInfo]$entry)
            }
        }
    }

    return $files.ToArray()
}

function Sort-OrdinalStrings {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Values
    )

    [Array]::Sort($Values, [System.StringComparer]::Ordinal)
    return $Values
}

function Stage-PublishedNodeProbePayload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$PublishedRoot,

        [Parameter(Mandatory = $true)]
        [string]$ResolvedNodeExecutable,

        [Parameter(Mandatory = $true)]
        [string]$CorepackExecutable
    )

    $sourceRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $RepositoryRoot 'desktop\windows\node\src'))
    $publishConfig = Join-Path $RepositoryRoot 'desktop\windows\node\tsconfig.publish.json'
    $payloadRoot = [System.IO.Path]::GetFullPath((Join-Path $PublishedRoot 'node-probe'))
    $appRoot = Join-Path $payloadRoot 'app'

    Assert-NoReparsePointBelowRoot -TrustedRoot $RepositoryRoot -TargetPath $payloadRoot
    if (Test-Path -LiteralPath $payloadRoot) {
        $null = Get-TreeFilesWithoutReparsePoints -Root $payloadRoot
        Remove-Item -LiteralPath $payloadRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
    Invoke-NativeCommand $CorepackExecutable @(
        'yarn', 'exec', 'tsc',
        '--project', $publishConfig,
        '--outDir', $appRoot
    )

    $sourceFiles = @(Get-TreeFilesWithoutReparsePoints -Root $sourceRoot)
    if ($sourceFiles.Count -eq 0) {
        throw 'The Node probe source tree is empty.'
    }

    $expectedJavaScript = [System.Collections.Generic.List[string]]::new()
    foreach ($sourceFile in $sourceFiles) {
        if (-not $sourceFile.Extension.Equals('.ts', [System.StringComparison]::Ordinal)) {
            throw "Only TypeScript source files are allowed in the probe source tree: $($sourceFile.FullName)"
        }

        $sourceRelative = Get-RelativePathUnderRoot -Root $sourceRoot -Path $sourceFile.FullName
        $expectedJavaScript.Add('app/' + $sourceRelative.Substring(0, $sourceRelative.Length - 3) + '.js')
    }

    $emittedFiles = @(Get-TreeFilesWithoutReparsePoints -Root $appRoot)
    $actualJavaScript = [System.Collections.Generic.List[string]]::new()
    foreach ($emittedFile in $emittedFiles) {
        if (-not $emittedFile.Extension.Equals('.js', [System.StringComparison]::Ordinal)) {
            throw "The publish compiler emitted a non-JavaScript artifact: $($emittedFile.FullName)"
        }

        $actualJavaScript.Add(
            'app/' + (Get-RelativePathUnderRoot -Root $appRoot -Path $emittedFile.FullName))
    }

    $expectedSorted = Sort-OrdinalStrings -Values $expectedJavaScript.ToArray()
    $actualSorted = Sort-OrdinalStrings -Values $actualJavaScript.ToArray()
    if ($expectedSorted.Count -ne $actualSorted.Count) {
        throw 'The emitted JavaScript closure does not match the TypeScript source closure.'
    }
    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        if (-not [System.StringComparer]::Ordinal.Equals(
                $expectedSorted[$index],
                $actualSorted[$index])) {
            throw 'The emitted JavaScript closure does not match the TypeScript source closure.'
        }
    }

    foreach ($entrypoint in @(
        'app/probes/server-probe.js',
        'app/probes/worker-probe.js')) {
        if (-not $actualJavaScript.Contains($entrypoint)) {
            throw "Required published Node entrypoint is missing: $entrypoint"
        }
    }

    $nodeDestination = Join-Path $payloadRoot 'node.exe'
    [System.IO.File]::Copy($ResolvedNodeExecutable, $nodeDestination, $false)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $payloadRoot 'package.json'),
        '{"type":"module"}',
        $utf8NoBom)

    $artifactPaths = [System.Collections.Generic.List[string]]::new()
    $artifactPaths.Add('node.exe')
    $artifactPaths.Add('package.json')
    foreach ($relative in $actualJavaScript) {
        $artifactPaths.Add($relative)
    }
    $sortedArtifactPaths = Sort-OrdinalStrings -Values $artifactPaths.ToArray()

    $artifacts = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $sortedArtifactPaths) {
        if (-not (
                $relative.Equals('node.exe', [System.StringComparison]::Ordinal) -or
                $relative.Equals('package.json', [System.StringComparison]::Ordinal) -or
                ($relative.StartsWith('app/', [System.StringComparison]::Ordinal) -and
                    $relative.EndsWith('.js', [System.StringComparison]::Ordinal)))) {
            throw "Invalid generated payload artifact shape: $relative"
        }

        $fullPath = Join-Path $payloadRoot $relative.Replace('/', '\')
        $file = Get-Item -LiteralPath $fullPath -Force
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse-point payload file is not allowed: $fullPath"
        }

        $artifacts.Add([ordered]@{
            path = $relative
            length = [long]$file.Length
            sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToUpperInvariant()
        })
    }

    $manifest = [ordered]@{
        version = 1
        buildIdentity = 'published-node-probe/1'
        nodeExecutable = 'node.exe'
        serverEntrypoint = 'app/probes/server-probe.js'
        workerEntrypoint = 'app/probes/worker-probe.js'
        artifacts = $artifacts.ToArray()
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 6 -Compress
    $manifestPath = Join-Path $payloadRoot 'node-payload-manifest-v1.json'
    $temporaryManifest = Join-Path $payloadRoot (
        '.node-payload-manifest-v1.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [System.IO.File]::WriteAllText($temporaryManifest, $manifestJson, $utf8NoBom)
        if (Test-Path -LiteralPath $manifestPath) {
            throw "Refusing to replace an existing Node payload manifest: $manifestPath"
        }
        [System.IO.File]::Move($temporaryManifest, $manifestPath)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryManifest) {
            Remove-Item -LiteralPath $temporaryManifest -Force
        }
    }

    $declaredFiles = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($relative in $sortedArtifactPaths) {
        if (-not $declaredFiles.Add($relative)) {
            throw "Duplicate generated payload artifact: $relative"
        }
    }
    $null = $declaredFiles.Add('node-payload-manifest-v1.json')
    $actualPayloadFiles = @(Get-TreeFilesWithoutReparsePoints -Root $payloadRoot)
    if ($actualPayloadFiles.Count -ne $declaredFiles.Count) {
        throw 'The generated Node payload contains an undeclared file.'
    }
    foreach ($file in $actualPayloadFiles) {
        $relative = Get-RelativePathUnderRoot -Root $payloadRoot -Path $file.FullName
        if (-not $declaredFiles.Contains($relative)) {
            throw "The generated Node payload contains an undeclared file: $relative"
        }
    }

    return $payloadRoot
}

$runStartUtc = [datetime]::UtcNow
$cleanupScript = Join-Path $PSScriptRoot 'Test-Phase1CCleanup.ps1'
$verificationFailure = $null
$locationPushed = $false

try {
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    $solutionPath = Join-Path $repoRoot 'desktop\windows\EbayCrm.Desktop.sln'
    $appHostProject = Join-Path $repoRoot 'desktop\windows\src\HowardLab.EbayCrm.AppHost\HowardLab.EbayCrm.AppHost.csproj'
    $coreTests = Join-Path $repoRoot 'desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests\HowardLab.EbayCrm.AppHost.Core.Tests.csproj'
    $windowsTests = Join-Path $repoRoot 'desktop\windows\tests\HowardLab.EbayCrm.AppHost.Windows.Tests\HowardLab.EbayCrm.AppHost.Windows.Tests.csproj'
    $integrationTests = Join-Path $repoRoot 'desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj'
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'desktop\windows\artifacts'))
    $publishRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'win-x64'))

    if ([string]::IsNullOrWhiteSpace($PostgresBin)) {
        if (-not [string]::IsNullOrWhiteSpace($env:EBAYCRM_POSTGRES_BIN)) {
            $PostgresBin = $env:EBAYCRM_POSTGRES_BIN
        }
        else {
            $PostgresBin = Join-Path $repoRoot '.tools\postgresql\16\bin'
        }
    }

    $PostgresBin = [System.IO.Path]::GetFullPath($PostgresBin)
    $postgresExe = Join-Path $PostgresBin 'postgres.exe'
    if (-not (Test-Path -LiteralPath $postgresExe -PathType Leaf)) {
        throw "PostgreSQL 16 postgres.exe was not found at: $postgresExe"
    }

    if ([string]::IsNullOrWhiteSpace($NodeExe)) {
        $NodeExe = if (-not [string]::IsNullOrWhiteSpace($env:EBAYCRM_NODE_EXE)) {
            $env:EBAYCRM_NODE_EXE
        }
        else {
            'node'
        }
    }

    $nodeCommand = Get-Command -Name $NodeExe -CommandType Application -ErrorAction Stop
    $resolvedNodeExe = [System.IO.Path]::GetFullPath($nodeCommand.Source)
    $siblingCorepack = Join-Path ([System.IO.Path]::GetDirectoryName($resolvedNodeExe)) 'corepack.cmd'
    $corepackExe = if (Test-Path -LiteralPath $siblingCorepack -PathType Leaf) {
        $siblingCorepack
    }
    else {
        (Get-Command -Name 'corepack' -CommandType Application -ErrorAction Stop).Source
    }

    $env:EBAYCRM_POSTGRES_BIN = $PostgresBin
    $env:EBAYCRM_RELEASE_ACCEPTANCE = '1'
    $env:EBAYCRM_NODE_EXE = $resolvedNodeExe

    Push-Location $repoRoot
    $locationPushed = $true

    Invoke-NativeCommand $postgresExe @('--version')
    Invoke-NativeCommand 'dotnet' @(
        'restore', $solutionPath,
        '--locked-mode'
    )
    Invoke-NativeCommand 'dotnet' @(
        'build', $solutionPath,
        '--configuration', 'Release',
        '--no-restore',
        '--nologo'
    )

    $expectedParent = [System.IO.Path]::GetDirectoryName($publishRoot)
    if (-not $expectedParent.Equals($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($publishRoot).Equals(
            'win-x64',
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected publish path: $publishRoot"
    }

    Assert-NoReparsePointBelowRoot -TrustedRoot $repoRoot -TargetPath $publishRoot
    if (Test-Path -LiteralPath $publishRoot) {
        $null = Get-TreeFilesWithoutReparsePoints -Root $publishRoot
        $publishItem = Get-Item -LiteralPath $publishRoot -Force
        if (-not $publishItem.FullName.TrimEnd('\').Equals(
                $publishRoot.TrimEnd('\'),
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a publish path whose resolved identity changed: $($publishItem.FullName)"
        }

        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }

    Invoke-NativeCommand 'dotnet' @(
        'publish', $appHostProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $publishRoot,
        '--no-restore',
        '--nologo',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishAot=false'
    )

    Invoke-NativeCommand $corepackExe @(
        'yarn', 'exec', 'tsc',
        '--project', 'desktop/windows/node/tsconfig.json',
        '--noEmit'
    )
    Invoke-NativeCommand $corepackExe @(
        'yarn', 'exec', 'tsx', '--test',
        'desktop/windows/node/test/**/*.test.ts'
    )

    $null = Stage-PublishedNodeProbePayload `
        -RepositoryRoot $repoRoot `
        -PublishedRoot $publishRoot `
        -ResolvedNodeExecutable $resolvedNodeExe `
        -CorepackExecutable $corepackExe

    Invoke-NativeCommand 'dotnet' @(
        'test', $integrationTests,
        '--configuration', 'Release',
        '--no-restore', '--no-build',
        '--filter', 'FullyQualifiedName~PublishedNodeProbeAppHostSmokeTests',
        '--nologo'
    )

    if (-not $PublishedNodeProbeOnly) {
        Invoke-NativeCommand 'dotnet' @(
            'test', $coreTests,
            '--configuration', 'Release',
            '--no-restore', '--no-build', '--nologo'
        )
        Invoke-NativeCommand 'dotnet' @(
            'test', $windowsTests,
            '--configuration', 'Release',
            '--no-restore', '--no-build', '--nologo'
        )
        Invoke-NativeCommand 'dotnet' @(
            'test', $integrationTests,
            '--configuration', 'Release',
            '--no-restore', '--no-build',
            '--filter', 'Category!=DestructiveContainment&FullyQualifiedName!~PublishedNodeProbeAppHostSmokeTests',
            '--nologo'
        )
        Invoke-NativeCommand 'dotnet' @(
            'test', $integrationTests,
            '--configuration', 'Release',
            '--no-restore', '--no-build',
            '--filter', 'Category=DestructiveContainment',
            '--nologo', '--',
            'RunConfiguration.DisableAppDomain=true'
        )
    }
}
catch {
    $verificationFailure = $_
}
finally {
    if ($locationPushed) {
        Pop-Location
    }
}

$cleanupFailure = $null
try {
    & $cleanupScript -RunStartUtc $runStartUtc
}
catch {
    $cleanupFailure = $_
}

if ($null -ne $verificationFailure -and $null -ne $cleanupFailure) {
    throw [System.AggregateException]::new(
        'Phase 1C verification and cleanup audit both failed.',
        [System.Exception[]]@($verificationFailure.Exception, $cleanupFailure.Exception))
}

if ($null -ne $verificationFailure) {
    throw $verificationFailure
}

if ($null -ne $cleanupFailure) {
    throw $cleanupFailure
}

Write-Host 'Phase 1C verification passed.'

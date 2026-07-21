[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $OutputRoot,
    [string] $NodeArchivePath,
    [string] $GitArchivePath,
    [string] $PayloadToolExePath,
    [string] $PayloadToolSha256,
    [string] $CandidateCatalogPath,
    [switch] $Offline,
    [switch] $ClosureOnly
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepositoryRoot 'desktop\windows\artifacts\phase-1c-b'
}
$nodeVersion = '24.18.0'
$yarnVersion = '4.13.0'
$nodeArchiveName = 'node-v24.18.0-win-x64.zip'
$nodeArchiveUrl = "https://nodejs.org/dist/v24.18.0/$nodeArchiveName"
$nodeArchiveSha256 = '0AE68406B42D7725661DA979B1403EC9926DA205C6770827F33AAC9D8F26E821'
$nodeExecutableSha256 = '9A4EB5F1C29C6A2E93852EAD46B999E284A6A5CA8BAB4D4E241D587D025A52DE'
$nodeArchiveLength = 37176245
$nodeExecutableLength = 92534088
$nodeSubject = 'CN=OpenJS Foundation, O=OpenJS Foundation, L=San Francisco, S=California, C=US'
$gitVersion = '2.55.0.windows.2'
$gitArchiveName = 'MinGit-2.55.0.2-64-bit.zip'
$gitArchiveUrl = 'https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.2/MinGit-2.55.0.2-64-bit.zip'
$gitArchiveSha256 = 'E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31'
$gitExecutableSha256 = '22FEAD8244EF3A7225FB800099A4E43ECA8BCEC0466774917669599C2F19A05A'
$gitArchiveLength = 38839825
$gitExecutableLength = 46936
$gitSubject = 'CN=Johannes Schindelin, O=Johannes Schindelin, L=Bruehl, C=DE'
$originalLocation = Get-Location
$originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
$originalNxDaemon = [Environment]::GetEnvironmentVariable('NX_DAEMON', 'Process')
$stagingRoot = $null
$stagingParent = $null
$stagingParentCreated = $false
$buildInjectionExactNames = @('NODE_OPTIONS', 'NODE_PATH')
$buildInjectionPrefixes = @('npm_', 'YARN_', 'COREPACK_')
$originalBuildInjectionEnvironment = @{}
foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    $name = [string]$entry.Key
    if (($buildInjectionExactNames -contains $name) -or
        ($buildInjectionPrefixes | Where-Object { $name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
        $originalBuildInjectionEnvironment[$name] = [string]$entry.Value
    }
}

function Clear-BuildInjectionEnvironment {
    foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
        $name = [string]$entry.Key
        if (($buildInjectionExactNames -contains $name) -or
            ($buildInjectionPrefixes | Where-Object { $name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
    }
}

function Restore-BuildInjectionEnvironment {
    param([hashtable] $Original)
    Clear-BuildInjectionEnvironment
    foreach ($entry in $Original.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable([string]$entry.Key, [string]$entry.Value, 'Process')
    }
}

function Get-CanonicalPath {
    param([Parameter(Mandatory = $true)][string] $Path, [switch] $MustExist)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) {
        throw 'phase1cb-path-invalid'
    }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ($full.IndexOf(':', $root.Length) -ge 0) {
        throw 'phase1cb-path-invalid'
    }
    if ($MustExist -and -not (Test-Path -LiteralPath $full)) {
        throw 'phase1cb-path-missing'
    }
    if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $root
    }
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-IsSameOrUnder {
    param([string] $Candidate, [string] $Root)
    return $Candidate.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Candidate.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparseChain {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $StopRoot)
    $current = Get-CanonicalPath -Path $Path
    $stop = Get-CanonicalPath -Path $StopRoot -MustExist
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'phase1cb-reparse-point'
            }
        }
        if ($current.Equals($stop, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent -or -not (Test-IsSameOrUnder -Candidate $parent.FullName -Root $stop)) {
            throw 'phase1cb-path-outside-root'
        }
        $current = $parent.FullName
    }
}

function Get-ExactPinnedSha256 {
    param(
        [string] $Path,
        [long] $ExpectedLength,
        [string] $Reason
    )
    try {
        $file = Get-CanonicalPath -Path $Path -MustExist
        Assert-NoReparseChain -Path $file -StopRoot (Split-Path -Parent $file)
        $metadata = Get-Item -LiteralPath $file -Force
        if ($metadata.PSIsContainer -or
            ($metadata.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            [long]$metadata.Length -ne $ExpectedLength) {
            throw $Reason
        }
        $streams = @(Get-Item -LiteralPath $file -Stream * -ErrorAction Stop)
        if ($streams.Count -ne 1 -or
            @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$streams[0].Stream)) {
            throw $Reason
        }
        return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    } catch {
        throw $Reason
    }
}

function Test-WindowsReservedArchiveComponent {
    param([string] $Component)
    if ([string]::IsNullOrEmpty($Component) -or
        $Component.Equals('.', [StringComparison]::Ordinal) -or
        $Component.Equals('..', [StringComparison]::Ordinal) -or
        $Component.EndsWith('.', [StringComparison]::Ordinal) -or
        $Component.EndsWith(' ', [StringComparison]::Ordinal) -or
        $Component.IndexOfAny([char[]]('<>:"|?*')) -ge 0) {
        return $true
    }
    foreach ($character in $Component.ToCharArray()) {
        if ([int]$character -lt 32) { return $true }
    }
    $stem = $Component.Split('.')[0]
    return $stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'
}

function Assert-ExactOrdinaryTreeFromZip {
    param(
        [string] $Root,
        [hashtable] $ExpectedFiles,
        [hashtable] $ExpectedDirectories
    )
    $canonical = Get-CanonicalPath -Path $Root -MustExist
    Assert-NoReparseChain -Path $canonical -StopRoot (Split-Path -Parent $canonical)
    $actualFiles = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $actualDirectories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$entries = 0
    [long]$bytes = 0
    foreach ($entry in Get-ChildItem -LiteralPath $canonical -Recurse -Force) {
        $entries++
        if ($entries -gt 1000) { throw 'phase1cb-mingit-tree-invalid' }
        $path = Get-CanonicalPath -Path $entry.FullName -MustExist
        Assert-NoReparseChain -Path $path -StopRoot $canonical
        if (-not $entry.FullName.Equals($path, [StringComparison]::Ordinal) -or
            ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'phase1cb-mingit-tree-invalid'
        }
        $relative = $path.Substring($canonical.Length).TrimStart('\').Replace('\', '/')
        $streams = @(Get-Item -LiteralPath $path -Stream * -ErrorAction Stop)
        if (@($streams | Where-Object {
            @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$_.Stream)
        }).Count -ne 0) {
            throw 'phase1cb-mingit-tree-invalid'
        }
        if ($entry.PSIsContainer) {
            if (-not $ExpectedDirectories.ContainsKey($relative) -or
                -not $actualDirectories.Add($relative)) {
                throw 'phase1cb-mingit-tree-invalid'
            }
        } else {
            if ($streams.Count -ne 1 -or -not $ExpectedFiles.ContainsKey($relative) -or
                [long]$ExpectedFiles[$relative] -ne [long]$entry.Length -or
                -not $actualFiles.Add($relative)) {
                throw 'phase1cb-mingit-tree-invalid'
            }
            $bytes += [long]$entry.Length
            if ($bytes -gt 100000000) { throw 'phase1cb-mingit-tree-invalid' }
        }
    }
    if ($actualFiles.Count -ne $ExpectedFiles.Count -or
        $actualDirectories.Count -ne $ExpectedDirectories.Count -or
        $actualFiles.Count -ne 364 -or $bytes -ne 94007844) {
        throw 'phase1cb-mingit-tree-invalid'
    }
}

function Expand-PinnedMinGitArchive {
    param([string] $ArchivePath, [string] $DestinationRoot)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Get-CanonicalPath -Path $ArchivePath -MustExist
    $destination = Get-CanonicalPath -Path $DestinationRoot
    $parent = Get-CanonicalPath -Path (Split-Path -Parent $destination) -MustExist
    Assert-NoReparseChain -Path $archive -StopRoot (Split-Path -Parent $archive)
    Assert-NoReparseChain -Path $parent -StopRoot ([IO.Path]::GetPathRoot($parent))
    if (Test-Path -LiteralPath $destination) { throw 'phase1cb-mingit-extract-not-clean' }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $destination = Get-CanonicalPath -Path $destination -MustExist
    Assert-NoReparseChain -Path $destination -StopRoot $parent
    $expectedFiles = @{}
    $expectedDirectories = @{}
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $componentIdentity = @{}
    [long]$entryCount = 0
    [long]$fileCount = 0
    [long]$uncompressedBytes = 0
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $entryCount++
            if ($entryCount -gt 367 -or [string]::IsNullOrEmpty($entry.FullName) -or
                $entry.FullName.Length -gt 512 -or
                $entry.FullName.StartsWith('/', [StringComparison]::Ordinal) -or
                $entry.FullName.Contains('\') -or $entry.FullName.Contains(':') -or
                $entry.FullName.Contains('//') -or -not $seen.Add($entry.FullName) -or
                (($entry.ExternalAttributes -band 0x400) -ne 0) -or
                ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000)) {
                throw 'phase1cb-mingit-archive-entry-invalid'
            }
            $directoryEntry = $entry.FullName.EndsWith('/', [StringComparison]::Ordinal)
            $relative = $entry.FullName.TrimEnd('/')
            $segments = @($relative.Split('/') | Where-Object { $_.Length -ne 0 })
            if ($segments.Count -eq 0 -or $segments.Count -gt 64 -or
                ($segments | Where-Object { Test-WindowsReservedArchiveComponent $_ }).Count -ne 0) {
                throw 'phase1cb-mingit-archive-entry-invalid'
            }
            $currentRelative = ''
            for ($index = 0; $index -lt $segments.Count; $index++) {
                $parentIdentity = $currentRelative.ToLowerInvariant()
                $componentKey = $parentIdentity + "`0" + $segments[$index].ToLowerInvariant()
                if ($componentIdentity.ContainsKey($componentKey) -and
                    -not ([string]$componentIdentity[$componentKey]).Equals(
                        $segments[$index], [StringComparison]::Ordinal)) {
                    throw 'phase1cb-mingit-archive-entry-invalid'
                }
                $componentIdentity[$componentKey] = $segments[$index]
                $currentRelative = if ($currentRelative.Length -eq 0) {
                    $segments[$index]
                } else { $currentRelative + '/' + $segments[$index] }
                if ($index -eq ($segments.Count - 1)) { continue }
                if ($expectedFiles.ContainsKey($currentRelative)) {
                    throw 'phase1cb-mingit-archive-entry-invalid'
                }
                $expectedDirectories[$currentRelative] = $true
            }
            if ($directoryEntry) {
                if ($entry.Length -ne 0 -or $expectedFiles.ContainsKey($relative)) {
                    throw 'phase1cb-mingit-archive-entry-invalid'
                }
                $expectedDirectories[$relative] = $true
            } else {
                if ($expectedDirectories.ContainsKey($relative) -or
                    $entry.Length -lt 0 -or
                    $uncompressedBytes -gt (94007844 - [long]$entry.Length)) {
                    throw 'phase1cb-mingit-archive-entry-invalid'
                }
                $fileCount++
                $uncompressedBytes += [long]$entry.Length
                $expectedFiles[$relative] = [long]$entry.Length
            }
            $target = Get-CanonicalPath -Path (Join-Path $destination $relative.Replace('/', '\'))
            if (-not (Test-IsSameOrUnder -Candidate $target -Root $destination) -or
                $target.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'phase1cb-mingit-archive-entry-invalid'
            }
        }
        if ($entryCount -ne 367 -or $fileCount -ne 364 -or
            $uncompressedBytes -ne 94007844) {
            throw 'phase1cb-mingit-archive-entry-invalid'
        }
        foreach ($directory in @($expectedDirectories.Keys | Sort-Object {
            @($_ -split '/').Count
        }, { $_ })) {
            New-ValidatedDestinationDirectory `
                -DestinationRoot $destination `
                -DirectoryPath (Join-Path $destination ([string]$directory).Replace('/', '\'))
        }
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) { continue }
            $relative = $entry.FullName
            $target = Join-Path $destination $relative.Replace('/', '\')
            $input = $entry.Open()
            $output = [IO.File]::Open(
                $target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $input.CopyTo($output)
                $output.Flush($true)
            } finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    } finally {
        $zip.Dispose()
    }
    Assert-ExactOrdinaryTreeFromZip `
        -Root $destination `
        -ExpectedFiles $expectedFiles `
        -ExpectedDirectories $expectedDirectories
    return $destination
}

function Copy-ExactOrdinaryTree {
    param([string] $SourceRoot, [string] $DestinationRoot)
    $source = Get-CanonicalPath -Path $SourceRoot -MustExist
    $destination = Get-CanonicalPath -Path $DestinationRoot
    $parent = Get-CanonicalPath -Path (Split-Path -Parent $destination) -MustExist
    Assert-NoReparseChain -Path $source -StopRoot (Split-Path -Parent $source)
    Assert-NoReparseChain -Path $parent -StopRoot ([IO.Path]::GetPathRoot($parent))
    if (Test-Path -LiteralPath $destination) { throw 'phase1cb-mingit-copy-not-clean' }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $destination = Get-CanonicalPath -Path $destination -MustExist
    [long]$entries = 0
    [long]$bytes = 0
    foreach ($entry in Get-ChildItem -LiteralPath $source -Recurse -Force) {
        $entries++
        if ($entries -gt 1000) { throw 'phase1cb-mingit-tree-invalid' }
        $entryPath = Get-CanonicalPath -Path $entry.FullName -MustExist
        Assert-NoReparseChain -Path $entryPath -StopRoot $source
        $relative = $entryPath.Substring($source.Length).TrimStart('\')
        $target = Join-Path $destination $relative
        if ($entry.PSIsContainer) {
            New-ValidatedDestinationDirectory -DestinationRoot $destination -DirectoryPath $target
        } else {
            $bytes += [long]$entry.Length
            if ($bytes -gt 100000000) { throw 'phase1cb-mingit-tree-invalid' }
            New-ValidatedDestinationDirectory `
                -DestinationRoot $destination `
                -DirectoryPath (Split-Path -Parent $target)
            [IO.File]::Copy($entryPath, $target, $false)
            if ((Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
                throw 'phase1cb-mingit-copy-invalid'
            }
            $targetStreams = @(Get-Item -LiteralPath $target -Stream * -ErrorAction Stop)
            if ($targetStreams.Count -ne 1 -or
                @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$targetStreams[0].Stream)) {
                throw 'phase1cb-mingit-copy-invalid'
            }
        }
    }
    if ($entries -ne 434 -or $bytes -ne 94007844) {
        throw 'phase1cb-mingit-tree-invalid'
    }
    return $destination
}

function Assert-MinGitAbsentFromPayload {
    param([string] $PayloadRoot)
    $payload = Get-CanonicalPath -Path $PayloadRoot -MustExist
    if ((Test-Path -LiteralPath (Join-Path $payload '.phase1cb-toolchain')) -or
        (Test-Path -LiteralPath (Join-Path $payload 'mingit')) -or
        @(Get-ChildItem -LiteralPath $payload -Recurse -Force -Filter git.exe).Count -ne 0) {
        throw 'phase1cb-payload-mingit-present'
    }
}

function Remove-OwnedOutputRoot {
    param([string] $Path, [string] $AllowedRoot)
    [int] $maximumCleanupDepth = 128
    [long] $maximumCleanupEntries = 1000000
    [long] $maximumCleanupBytes = 68719476736
    $canonical = Get-CanonicalPath -Path $Path
    $allowed = Get-CanonicalPath -Path $AllowedRoot -MustExist
    if (-not (Test-IsSameOrUnder -Candidate $canonical -Root $allowed) -or
        $canonical.Equals($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'phase1cb-cleanup-root-invalid'
    }
    Assert-NoReparseChain -Path $allowed -StopRoot ([IO.Path]::GetPathRoot($allowed))
    Assert-NoReparseChain -Path $canonical -StopRoot $allowed
    if (-not (Test-Path -LiteralPath $canonical)) {
        return
    }
    $allowedItem = Get-Item -LiteralPath $allowed -Force
    if (-not $allowedItem.PSIsContainer -or
        ($allowedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not $allowedItem.FullName.Equals($allowed, [StringComparison]::Ordinal)) {
        throw 'phase1cb-cleanup-root-invalid'
    }
    $rootItem = Get-Item -LiteralPath $canonical -Force
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not $rootItem.FullName.Equals($canonical, [StringComparison]::Ordinal)) {
        throw 'phase1cb-cleanup-root-invalid'
    }
    foreach ($cleanupRoot in @($allowedItem, $rootItem)) {
        $rootStreams = @(Get-Item -LiteralPath $cleanupRoot.FullName -Stream * -ErrorAction Stop)
        $unexpectedRootStreams = @($rootStreams | Where-Object {
            @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$_.Stream)
        })
        if ($unexpectedRootStreams.Count -ne 0) {
            throw 'phase1cb-cleanup-root-invalid'
        }
    }

    function Assert-CleanupEntry {
        param([string] $EntryPath, [string] $OwnedRoot)
        $entryCanonical = Get-CanonicalPath -Path $EntryPath -MustExist
        if (-not (Test-IsSameOrUnder -Candidate $entryCanonical -Root $OwnedRoot) -or
            $entryCanonical.Equals($OwnedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'phase1cb-cleanup-entry-invalid'
        }
        $item = Get-Item -LiteralPath $entryCanonical -Force
        if (-not $item.FullName.Equals($entryCanonical, [StringComparison]::Ordinal) -or
            -not $item.Name.Equals((Split-Path -Leaf $entryCanonical), [StringComparison]::Ordinal)) {
            throw 'phase1cb-cleanup-entry-invalid'
        }
        $streams = @(Get-Item -LiteralPath $entryCanonical -Stream * -ErrorAction Stop)
        $unexpectedStreams = @($streams | Where-Object {
            @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$_.Stream)
        })
        if ($unexpectedStreams.Count -ne 0 -or
            ((-not $item.PSIsContainer) -and $streams.Count -ne 1)) {
            throw 'phase1cb-cleanup-entry-invalid'
        }
        return $item
    }

    function Remove-CleanupDirectoryNoFollow {
        param(
            [string] $DirectoryPath,
            [string] $OwnedRoot,
            [hashtable] $Budget,
            [int] $Depth
        )
        foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($DirectoryPath)) {
            $item = Assert-CleanupEntry -EntryPath $entryPath -OwnedRoot $OwnedRoot
            $bytes = if ($item.PSIsContainer) { [long]0 } else { [long]$item.Length }
            if (($Depth + 1) -gt $maximumCleanupDepth -or
                [long]$Budget.Entries -ge $maximumCleanupEntries -or
                $bytes -lt 0 -or
                [long]$Budget.Bytes -gt ($maximumCleanupBytes - $bytes)) {
                throw 'phase1cb-cleanup-traversal-budget'
            }
            $Budget.Entries = [long]$Budget.Entries + 1
            $Budget.Bytes = [long]$Budget.Bytes + $bytes
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                if ($item.PSIsContainer) {
                    [IO.Directory]::Delete($item.FullName)
                } else {
                    [IO.File]::Delete($item.FullName)
                }
            } elseif ($item.PSIsContainer) {
                Remove-CleanupDirectoryNoFollow `
                    -DirectoryPath $item.FullName `
                    -OwnedRoot $OwnedRoot `
                    -Budget $Budget `
                    -Depth ($Depth + 1)
                if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
                    [IO.File]::SetAttributes(
                        $item.FullName,
                        $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
                }
                [IO.Directory]::Delete($item.FullName)
            } else {
                if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
                    [IO.File]::SetAttributes(
                        $item.FullName,
                        $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
                }
                [IO.File]::Delete($item.FullName)
            }
        }
    }

    $cleanupBudget = @{ Entries = [long]1; Bytes = [long]0 }
    Remove-CleanupDirectoryNoFollow `
        -DirectoryPath $canonical `
        -OwnedRoot $canonical `
        -Budget $cleanupBudget `
        -Depth 0
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
        [IO.File]::SetAttributes(
            $rootItem.FullName,
            $rootItem.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
    }
    [IO.Directory]::Delete($canonical)
}

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string] $Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ([regex]::Replace($Value, '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

if ($null -eq ('Phase1CBJob' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class Phase1CBJob
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectBasicAccountingInformation = 1;
    private const uint KillOnJobClose = 0x00002000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr ProcThreadAttributeHandleList = new IntPtr(0x00020002);
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation { public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct StartupInfo { public int Size; public string Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2Length; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    public sealed class Launched : IDisposable
    {
        public Process Process { get; private set; }
        public FileStream StandardOutput { get; private set; }
        public FileStream StandardError { get; private set; }
        internal Launched(Process process, FileStream stdout, FileStream stderr) { Process=process; StandardOutput=stdout; StandardError=stderr; }
        public void Dispose() { StandardOutput.Dispose(); StandardError.Dispose(); Process.Dispose(); }
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, uint type, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job, uint type, IntPtr info, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    public static IntPtr CreateKillOnClose()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = KillOnJobClose;
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(info));
        try {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)Marshal.SizeOf(info))) throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        } catch { CloseHandle(job); throw; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void AssignAndVerify(IntPtr job, IntPtr process)
    {
        if (!AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
        bool assigned;
        if (!IsProcessInJob(process, job, out assigned) || !assigned) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static Launched LaunchSuspendedAssigned(
        IntPtr job,
        string application,
        string argumentLine,
        string workingDirectory,
        string[] environmentPairs)
    {
        var security = new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle=true };
        IntPtr stdoutRead=IntPtr.Zero, stdoutWrite=IntPtr.Zero, stderrRead=IntPtr.Zero, stderrWrite=IntPtr.Zero, input=IntPtr.Zero;
        IntPtr environment=IntPtr.Zero, attributeList=IntPtr.Zero, handleList=IntPtr.Zero;
        ProcessInformation pi=new ProcessInformation();
        try {
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0) || !SetHandleInformation(stdoutRead, HandleFlagInherit, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreatePipe(out stderrRead, out stderrWrite, ref security, 0) || !SetHandleInformation(stderrRead, HandleFlagInherit, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            input=CreateFileW("NUL", GenericRead, FileShareRead|FileShareWrite, ref security, OpenExisting, 0, IntPtr.Zero);
            if (input == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Array.Sort(environmentPairs, StringComparer.OrdinalIgnoreCase);
            string environmentBlock=string.Join("\0", environmentPairs)+"\0\0";
            environment=Marshal.StringToHGlobalUni(environmentBlock);
            IntPtr attributeBytes=IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            if (attributeBytes == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributeList=Marshal.AllocHGlobal(attributeBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            handleList=Marshal.AllocHGlobal(IntPtr.Size*3);
            Marshal.WriteIntPtr(handleList, 0, input);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite);
            Marshal.WriteIntPtr(handleList, IntPtr.Size*2, stderrWrite);
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handleList, new IntPtr(IntPtr.Size*3), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new StartupInfoEx {
                StartupInfo=new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfoEx)), Flags=StartfUseStdHandles, StandardInput=input, StandardOutput=stdoutWrite, StandardError=stderrWrite },
                AttributeList=attributeList
            };
            var commandLine=new StringBuilder("\""+application+"\""+(argumentLine.Length==0 ? "" : " "+argumentLine));
            if (!CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended|CreateNoWindow|CreateUnicodeEnvironment|ExtendedStartupInfoPresent, environment, workingDirectory, ref startup, out pi)) throw new Win32Exception(Marshal.GetLastWin32Error());
            AssignAndVerify(job, pi.Process);
            var process=Process.GetProcessById((int)pi.ProcessId);
            var ignored=process.Handle;
            if (ResumeThread(pi.Thread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(stdoutWrite); stdoutWrite=IntPtr.Zero;
            CloseHandle(stderrWrite); stderrWrite=IntPtr.Zero;
            CloseHandle(input); input=IntPtr.Zero;
            CloseHandle(pi.Thread); pi.Thread=IntPtr.Zero;
            CloseHandle(pi.Process); pi.Process=IntPtr.Zero;
            var stdout=new FileStream(new SafeFileHandle(stdoutRead, true), FileAccess.Read, 8192, false); stdoutRead=IntPtr.Zero;
            var stderr=new FileStream(new SafeFileHandle(stderrRead, true), FileAccess.Read, 8192, false); stderrRead=IntPtr.Zero;
            return new Launched(process, stdout, stderr);
        } catch {
            if (pi.Process != IntPtr.Zero) TerminateProcess(pi.Process, 1);
            throw;
        } finally {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            foreach (var handle in new[]{stdoutRead,stdoutWrite,stderrRead,stderrWrite,input,pi.Thread,pi.Process}) if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
        }
    }

    public static uint ActiveProcessCount(IntPtr job)
    {
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BasicAccountingInformation)));
        try {
            if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, buffer, (uint)Marshal.SizeOf(typeof(BasicAccountingInformation)), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return ((BasicAccountingInformation)Marshal.PtrToStructure(buffer, typeof(BasicAccountingInformation))).ActiveProcesses;
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void TerminateAndVerifyEmpty(IntPtr job)
    {
        if (job == IntPtr.Zero) return;
        if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var watch = Stopwatch.StartNew();
        while (ActiveProcessCount(job) != 0 && watch.Elapsed < TimeSpan.FromSeconds(5)) System.Threading.Thread.Sleep(10);
        if (ActiveProcessCount(job) != 0) throw new InvalidOperationException("phase1cb-dotnet-job-not-empty");
        if (!CloseHandle(job)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void CloseAndVerifyEmpty(IntPtr job)
    {
        if (job == IntPtr.Zero) return;
        if (ActiveProcessCount(job) != 0) throw new InvalidOperationException("phase1cb-dotnet-job-not-empty");
        if (!CloseHandle(job)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@
}

function Invoke-BoundedDotnet {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $ProfileRoot,
        [int] $TimeoutSeconds = 1800,
        [int] $MaximumOutputBytes = 16777216
    )
    foreach ($directory in @(
        $ProfileRoot,
        (Join-Path $ProfileRoot 'AppData\Roaming'),
        (Join-Path $ProfileRoot 'AppData\Local'),
        (Join-Path $ProfileRoot 'Temp'),
        (Join-Path $ProfileRoot 'NuGet'))) {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
    }
    $argumentLine = (($Arguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Value ([string]$_)
    }) -join ' ')
    $windows = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $environment = @{
        SystemRoot = $windows; WINDIR = $windows
        COMSPEC = (Join-Path $windows 'System32\cmd.exe'); PATHEXT = '.COM;.EXE;.BAT;.CMD'
        HOME = $ProfileRoot; USERPROFILE = $ProfileRoot
        APPDATA = (Join-Path $ProfileRoot 'AppData\Roaming')
        LOCALAPPDATA = (Join-Path $ProfileRoot 'AppData\Local')
        TEMP = (Join-Path $ProfileRoot 'Temp'); TMP = (Join-Path $ProfileRoot 'Temp')
        DOTNET_CLI_HOME = $ProfileRoot; NUGET_PACKAGES = (Join-Path $ProfileRoot 'NuGet')
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'
        MSBUILDDISABLENODEREUSE = '1'
        PATH = ((Split-Path -Parent $FilePath) + [IO.Path]::PathSeparator + (Join-Path $windows 'System32'))
    }
    $environmentPairs = @($environment.GetEnumerator() | ForEach-Object {
        if ([string]$_.Key -match '[=\x00]' -or [string]$_.Value -match '\x00') {
            throw 'phase1cb-dotnet-environment-invalid'
        }
        ([string]$_.Key) + '=' + ([string]$_.Value)
    })
    $job = [IntPtr]::Zero
    $launched = $null
    $completed = $false
    try {
        $job = [Phase1CBJob]::CreateKillOnClose()
        $launched = [Phase1CBJob]::LaunchSuspendedAssigned(
            $job,
            $FilePath,
            $argumentLine,
            (Get-Location).Path,
            $environmentPairs)
    } catch {
        if ($job -ne [IntPtr]::Zero) { [Phase1CBJob]::TerminateAndVerifyEmpty($job) }
        throw 'phase1cb-dotnet-job-assignment-failed'
    }
    $process = $launched.Process
    $stdoutBytes = [IO.MemoryStream]::new()
    $stderrBytes = [IO.MemoryStream]::new()
    $stdoutBuffer = New-Object byte[] 8192
    $stderrBuffer = New-Object byte[] 8192
    $stdoutRead = $launched.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
    $stderrRead = $launched.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
    $stdoutEof = $false
    $stderrEof = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $exitObservedAt = $null
    try {
        while (-not ($process.HasExited -and $stdoutEof -and $stderrEof -and
            [Phase1CBJob]::ActiveProcessCount($job) -eq 0)) {
            if (-not $stdoutEof -and $stdoutRead.IsCompleted) {
                $count = $stdoutRead.GetAwaiter().GetResult()
                if ($count -eq 0) { $stdoutEof = $true } else {
                    $stdoutBytes.Write($stdoutBuffer, 0, $count)
                    $stdoutRead = $launched.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
                }
            }
            if (-not $stderrEof -and $stderrRead.IsCompleted) {
                $count = $stderrRead.GetAwaiter().GetResult()
                if ($count -eq 0) { $stderrEof = $true } else {
                    $stderrBytes.Write($stderrBuffer, 0, $count)
                    $stderrRead = $launched.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
                }
            }
            if (($stdoutBytes.Length + $stderrBytes.Length) -gt $MaximumOutputBytes) {
                throw 'phase1cb-dotnet-output-limit'
            }
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                throw 'phase1cb-dotnet-timeout'
            }
            if ($process.HasExited -and $null -eq $exitObservedAt) {
                $exitObservedAt = $watch.Elapsed
            }
            if ($null -ne $exitObservedAt -and
                ($watch.Elapsed - $exitObservedAt).TotalSeconds -ge 5) {
                throw 'phase1cb-dotnet-inherited-pipe'
            }
            Start-Sleep -Milliseconds 5
        }
        $stdoutText = [Text.Encoding]::UTF8.GetString($stdoutBytes.ToArray())
        $stderrText = [Text.Encoding]::UTF8.GetString($stderrBytes.ToArray())
        if (-not [string]::IsNullOrEmpty($stdoutText)) { [Console]::Out.Write($stdoutText) }
        if (-not [string]::IsNullOrEmpty($stderrText)) { [Console]::Error.Write($stderrText) }
        if ($process.ExitCode -ne 0) {
            throw "phase1cb-command-failed:$($process.ExitCode)"
        }
        [Phase1CBJob]::CloseAndVerifyEmpty($job)
        $job = [IntPtr]::Zero
        $completed = $true
    } finally {
        if (-not $completed -and $job -ne [IntPtr]::Zero) {
            [Phase1CBJob]::TerminateAndVerifyEmpty($job)
            $job = [IntPtr]::Zero
        }
        $stdoutBytes.Dispose()
        $stderrBytes.Dispose()
        if ($null -ne $launched) { $launched.Dispose() }
    }
}

function Invoke-BoundedPayloadTool {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $ProfileRoot,
        [int] $TimeoutSeconds = 1800,
        [int] $MaximumOutputBytes = 16777216
    )
    Invoke-BoundedDotnet `
        -FilePath $FilePath `
        -Arguments $Arguments `
        -ProfileRoot $ProfileRoot `
        -TimeoutSeconds $TimeoutSeconds `
        -MaximumOutputBytes $MaximumOutputBytes
}

function Get-SourceHead {
    param(
        [Parameter(Mandatory = $true)][string] $SourceRoot,
        [Parameter(Mandatory = $true)][string] $GitExecutable
    )
    $result = @{ Count = 0; Value = $null }
    Invoke-BoundedGitRecords `
        -SourceRoot $SourceRoot `
        -GitExecutable $GitExecutable `
        -GitArguments @('rev-parse', '--verify', 'HEAD') `
        -RecordDelimiter 10 `
        -MaximumRecords 1 `
        -MaximumRecordBytes 128 `
        -OnRecord {
            param($record)
            $result.Count = [int]$result.Count + 1
            $result.Value = ([string]$record).TrimEnd("`r")
        }
    if ([int]$result.Count -ne 1) { throw 'phase1cb-source-head-invalid' }
    $identity = [string]$result.Value
    if ($identity -notmatch '^[0-9a-f]{40}$') {
        throw 'phase1cb-source-head-invalid'
    }
    return $identity
}

function Assert-TrustedSourceCheckout {
    param(
        [Parameter(Mandatory = $true)][string] $SourceRoot,
        [Parameter(Mandatory = $true)][string] $ExpectedCommit,
        [Parameter(Mandatory = $true)][string] $GitExecutable
    )
    if (-not (Get-SourceHead -SourceRoot $SourceRoot -GitExecutable $GitExecutable).Equals(
            $ExpectedCommit,
            [StringComparison]::Ordinal)) {
        throw 'production-source-head-changed'
    }
    Invoke-StreamingGitInventory -SourceRoot $SourceRoot -GitExecutable $GitExecutable -StatusPorcelain -OnPath {
        throw 'production-source-checkout-not-clean'
    }
}

function Invoke-StreamingGitInventory {
    param(
        [string] $SourceRoot,
        [Parameter(Mandatory = $true)][string] $GitExecutable,
        [switch] $IncludeUntracked,
        [switch] $StatusPorcelain,
        [scriptblock] $OnPath
    )
    if ($StatusPorcelain) {
        if ($IncludeUntracked) { throw 'phase1cb-source-inventory-failed' }
        Invoke-BoundedGitRecords `
            -SourceRoot $SourceRoot `
            -GitExecutable $GitExecutable `
            -GitArguments @('status', '--porcelain=v2', '-z', '--untracked-files=all') `
            -OnRecord $OnPath
        return
    }
    if ($IncludeUntracked) {
        Invoke-BoundedGitRecords `
            -SourceRoot $SourceRoot `
            -GitExecutable $GitExecutable `
            -GitArguments @('ls-files', '-z', '--cached', '--others', '--exclude-standard') `
            -OnRecord $OnPath
        return
    }
    Invoke-BoundedGitRecords `
        -SourceRoot $SourceRoot `
        -GitExecutable $GitExecutable `
        -GitArguments @('ls-files', '--stage', '-z') `
        -OnRecord $OnPath
}

function Invoke-BoundedGitRecords {
    param(
        [string] $SourceRoot,
        [string[]] $GitArguments,
        [scriptblock] $OnRecord,
        [int] $RecordDelimiter = 0,
        [int] $MaximumRecords = 250000,
        [int] $MaximumRecordBytes = 32768,
        [int] $TimeoutSeconds = 300,
        [string] $GitExecutable,
        [switch] $RawArguments
    )
    $root = Get-CanonicalPath -Path $SourceRoot -MustExist
    Assert-NoReparseChain -Path $root -StopRoot ([IO.Path]::GetPathRoot($root))
    if ([string]::IsNullOrWhiteSpace($GitExecutable)) {
        throw 'phase1cb-source-inventory-failed'
    }
    $git = Get-CanonicalPath -Path $GitExecutable -MustExist
    Assert-NoReparseChain -Path $git -StopRoot ([IO.Path]::GetPathRoot($git))
    if ($RecordDelimiter -lt 0 -or $RecordDelimiter -gt 255 -or
        $MaximumRecords -le 0 -or $MaximumRecords -gt 250000 -or
        $MaximumRecordBytes -le 0 -or $MaximumRecordBytes -gt 32768 -or
        $TimeoutSeconds -le 0 -or $TimeoutSeconds -gt 300) {
        throw 'phase1cb-source-inventory-failed'
    }
    $arguments = if ($RawArguments) { @($GitArguments) } else { @('-C', $root) + @($GitArguments) }
    $argumentLine = (($arguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Value ([string]$_)
    }) -join ' ')
    $windows = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $environmentPairs = @(
        'GIT_CONFIG_NOSYSTEM=1',
        'GIT_CONFIG_SYSTEM=NUL',
        'GIT_CONFIG_GLOBAL=NUL',
        'GIT_CONFIG_COUNT=0',
        'GIT_ATTR_NOSYSTEM=1',
        'GIT_TERMINAL_PROMPT=0',
        'GIT_OPTIONAL_LOCKS=0',
        'GIT_FLUSH=1',
        'GCM_INTERACTIVE=Never',
        'LC_ALL=C',
        'LANG=C',
        ('SystemRoot=' + $windows),
        ('WINDIR=' + $windows),
        ('COMSPEC=' + (Join-Path $windows 'System32\cmd.exe')),
        'PATHEXT=.COM;.EXE;.BAT;.CMD',
        ('PATH=' + (Split-Path -Parent $git) + [IO.Path]::PathSeparator + (Join-Path $windows 'System32'))
    )
    $job = [IntPtr]::Zero
    $launched = $null
    $completed = $false
    $recordBytes = [IO.MemoryStream]::new()
    $stderrBytes = [IO.MemoryStream]::new()
    $stdoutBuffer = New-Object byte[] 8192
    $stderrBuffer = New-Object byte[] 8192
    [long]$records = 0
    try {
        $job = [Phase1CBJob]::CreateKillOnClose()
        $launched = [Phase1CBJob]::LaunchSuspendedAssigned(
            $job,
            $git,
            $argumentLine,
            $root,
            $environmentPairs)
        $stdoutRead = $launched.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderrRead = $launched.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        $stdoutEof = $false
        $stderrEof = $false
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $decoder = [Text.UTF8Encoding]::new($false, $true)
        while (-not ($launched.Process.HasExited -and $stdoutEof -and $stderrEof -and
            [Phase1CBJob]::ActiveProcessCount($job) -eq 0)) {
            if (-not $stdoutEof -and $stdoutRead.IsCompleted) {
                $count = $stdoutRead.GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $stdoutEof = $true
                } else {
                    for ($index = 0; $index -lt $count; $index++) {
                        if ($stdoutBuffer[$index] -eq $RecordDelimiter) {
                            if ($recordBytes.Length -eq 0 -or $records -ge $MaximumRecords) {
                                throw 'phase1cb-source-traversal-budget'
                            }
                            try {
                                $record = $decoder.GetString($recordBytes.ToArray())
                            } catch {
                                throw 'phase1cb-source-inventory-invalid-utf8'
                            }
                            $recordBytes.SetLength(0)
                            $records++
                            & $OnRecord $record
                        } else {
                            if ($recordBytes.Length -ge $MaximumRecordBytes) {
                                throw 'phase1cb-source-traversal-budget'
                            }
                            $recordBytes.WriteByte($stdoutBuffer[$index])
                        }
                    }
                    $stdoutRead = $launched.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
                }
            }
            if (-not $stderrEof -and $stderrRead.IsCompleted) {
                $count = $stderrRead.GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $stderrEof = $true
                } else {
                    if ($stderrBytes.Length -gt (65536 - $count)) {
                        throw 'phase1cb-git-stderr-limit'
                    }
                    $stderrBytes.Write($stderrBuffer, 0, $count)
                    $stderrRead = $launched.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
                }
            }
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                throw 'phase1cb-source-inventory-timeout'
            }
            Start-Sleep -Milliseconds 5
        }
        if ($recordBytes.Length -ne 0) {
            throw 'phase1cb-source-inventory-invalid-termination'
        }
        if ($launched.Process.ExitCode -ne 0) {
            throw 'phase1cb-source-inventory-failed'
        }
        [Phase1CBJob]::CloseAndVerifyEmpty($job)
        $job = [IntPtr]::Zero
        $completed = $true
    } finally {
        if (-not $completed -and $job -ne [IntPtr]::Zero) {
            [Phase1CBJob]::TerminateAndVerifyEmpty($job)
            $job = [IntPtr]::Zero
        }
        $recordBytes.Dispose()
        $stderrBytes.Dispose()
        if ($null -ne $launched) { $launched.Dispose() }
    }
}

function Get-GitBlobSha1 {
    param([string] $Path)
    $file = Get-ExactOrdinaryFile -Path $Path
    $metadata = Get-Item -LiteralPath $file -Force
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($metadata.Length)`0")
    $sha1 = [Security.Cryptography.SHA1]::Create()
    $buffer = New-Object byte[] 131072
    try {
        $null = $sha1.TransformBlock($header, 0, $header.Length, $header, 0)
        $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $null = $sha1.TransformBlock($buffer, 0, $count, $buffer, 0)
            }
        } finally {
            $stream.Dispose()
        }
        $null = $sha1.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        return ([BitConverter]::ToString($sha1.Hash).Replace('-', '')).ToLowerInvariant()
    } finally {
        $sha1.Dispose()
    }
}

function New-ValidatedDestinationDirectory {
    param([string] $DestinationRoot, [string] $DirectoryPath)
    $root = Get-CanonicalPath -Path $DestinationRoot -MustExist
    $target = Get-CanonicalPath -Path $DirectoryPath
    if (-not (Test-IsSameOrUnder -Candidate $target -Root $root)) {
        throw 'phase1cb-source-path-escape'
    }
    $current = $root
    $relative = $target.Substring($root.Length).TrimStart('\')
    foreach ($segment in @($relative -split '\\' | Where-Object { $_.Length -ne 0 })) {
        $next = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $next)) {
            [IO.Directory]::CreateDirectory($next) | Out-Null
        }
        $item = Get-Item -LiteralPath $next -Force
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $item.Name.Equals($segment, [StringComparison]::Ordinal) -or
            -not $item.FullName.Equals($next, [StringComparison]::Ordinal)) {
            throw 'phase1cb-source-destination-invalid'
        }
        $streams = @(Get-Item -LiteralPath $next -Stream * -ErrorAction Stop)
        if (@($streams | Where-Object { @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$_.Stream) }).Count -ne 0) {
            throw 'phase1cb-source-destination-invalid'
        }
        $current = $next
    }
}

function Test-StagingParentWritableAndIsolated {
    param([string] $Candidate)
    $created = $false
    $accepted = $false
    $probe = $null
    try {
        $canonical = Get-CanonicalPath -Path $Candidate
        if (-not (Test-Path -LiteralPath $canonical)) {
            [IO.Directory]::CreateDirectory($canonical) | Out-Null
            $created = $true
        }
        $canonical = Get-CanonicalPath -Path $canonical -MustExist
        Assert-NoReparseChain -Path $canonical -StopRoot ([IO.Path]::GetPathRoot($canonical))
        for ($ancestor = [IO.Directory]::GetParent($canonical);
             $null -ne $ancestor;
             $ancestor = $ancestor.Parent) {
            $nodeModules = Join-Path $ancestor.FullName 'node_modules'
            if (Test-Path -LiteralPath $nodeModules) { return $false }
        }
        $probe = Join-Path $canonical ('.phase1cb-write-probe-' + [Guid]::NewGuid().ToString('N'))
        $bytes = [Text.Encoding]::UTF8.GetBytes('phase1cb-write-delete-proof')
        $stream = [IO.File]::Open(
            $probe,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        $probeItem = Get-Item -LiteralPath $probe -Force
        if ($probeItem.Length -ne $bytes.Length -or
            ($probeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }
        [IO.File]::Delete($probe)
        $probe = $null
        $accepted = -not (Test-Path -LiteralPath $probeItem.FullName)
        return $accepted
    } catch {
        return $false
    } finally {
        if ($null -ne $probe -and (Test-Path -LiteralPath $probe)) {
            [IO.File]::Delete($probe)
        }
        if ($created -and -not $accepted -and (Test-Path -LiteralPath $Candidate) -and
            [IO.Directory]::GetFileSystemEntries($Candidate).Length -eq 0) {
            [IO.Directory]::Delete($Candidate)
        }
    }
}

function Copy-TrackedSource {
    param(
        [string] $SourceRoot,
        [string] $DestinationRoot,
        [string] $ExpectedCommit,
        [Parameter(Mandatory = $true)][string] $GitExecutable,
        [switch] $AllowUntrustedSource
    )
    if (-not $AllowUntrustedSource) {
        Assert-TrustedSourceCheckout -SourceRoot $SourceRoot -ExpectedCommit $ExpectedCommit -GitExecutable $GitExecutable
    }
    $budget = @{ Entries = [long]0; Bytes = [long]0 }
    $excluded = @(
        '.git/', '.worktrees/', '.superpowers/', '.tools/',
        'desktop/windows/artifacts/'
    )
    Invoke-StreamingGitInventory -SourceRoot $SourceRoot -GitExecutable $GitExecutable -IncludeUntracked:$AllowUntrustedSource -OnPath {
        param($relativeValue)
        $inventoryRecord = [string]$relativeValue
        $expectedBlob = $null
        if (-not $AllowUntrustedSource) {
            $tab = $inventoryRecord.IndexOf("`t")
            if ($tab -le 0) { throw 'phase1cb-source-inventory-failed' }
            $prefix = $inventoryRecord.Substring(0, $tab)
            if ($prefix -notmatch '^[0-7]{6} ([0-9a-f]{40}) 0$') {
                throw 'phase1cb-source-inventory-failed'
            }
            $expectedBlob = [string]$Matches[1]
            $inventoryRecord = $inventoryRecord.Substring($tab + 1)
        }
        $relative = $inventoryRecord.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative -match '(^|/)node_modules(/|$)' -or
            $relative -match '(^|/)(bin|obj)(/|$)' -or
            ($excluded | Where-Object { $relative.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
            continue
        }
        $relativeDepth = @(($relative -split '[/\\]') | Where-Object { $_.Length -ne 0 }).Count
        if ($relativeDepth -gt 64 -or [long]$budget.Entries -ge 250000) {
            throw 'phase1cb-source-traversal-budget'
        }
        $source = Get-CanonicalPath -Path (Join-Path $SourceRoot $relative) -MustExist
        Assert-NoReparseChain -Path $source -StopRoot $SourceRoot
        $sourceItem = Get-Item -LiteralPath $source -Force
        if ($sourceItem.PSIsContainer -or $sourceItem.Length -lt 0 -or
            [long]$budget.Bytes -gt ([long]17179869184 - [long]$sourceItem.Length)) {
            throw 'phase1cb-source-traversal-budget'
        }
        $budget.Entries = [long]$budget.Entries + 1
        $budget.Bytes = [long]$budget.Bytes + [long]$sourceItem.Length
        if (-not $AllowUntrustedSource) {
            $sourceBlob = Get-GitBlobSha1 -Path $source
            if (-not $sourceBlob.Equals($expectedBlob, [StringComparison]::Ordinal)) {
                throw 'production-source-byte-mismatch'
            }
        }
        $destination = Get-CanonicalPath -Path (Join-Path $DestinationRoot $relative)
        if (-not (Test-IsSameOrUnder -Candidate $destination -Root $DestinationRoot)) {
            throw 'phase1cb-source-path-escape'
        }
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) {
            New-ValidatedDestinationDirectory -DestinationRoot $DestinationRoot -DirectoryPath $parent
        }
        Assert-NoReparseChain -Path $parent -StopRoot $DestinationRoot
        Copy-Item -LiteralPath $source -Destination $destination
        if (-not $AllowUntrustedSource) {
            $destinationBlob = Get-GitBlobSha1 -Path $destination
            if (-not $destinationBlob.Equals($expectedBlob, [StringComparison]::Ordinal)) {
                throw 'production-source-byte-mismatch'
            }
        }
    }
    if (-not $AllowUntrustedSource) {
        Assert-TrustedSourceCheckout -SourceRoot $SourceRoot -ExpectedCommit $ExpectedCommit -GitExecutable $GitExecutable
    }
}

function Get-ExactOrdinaryFile {
    param([string] $Path)
    $absolute = Get-CanonicalPath -Path $Path -MustExist
    $parent = Get-CanonicalPath -Path (Split-Path -Parent $absolute) -MustExist
    Assert-NoReparseChain -Path $absolute -StopRoot $parent
    $leaf = Split-Path -Leaf $absolute
    $matches = @(Get-ChildItem -LiteralPath $parent -Force | Where-Object {
        $_.Name.Equals($leaf, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1 -or $matches[0].PSIsContainer -or
        -not $matches[0].Name.Equals($leaf, [StringComparison]::Ordinal) -or
        -not $matches[0].FullName.Equals($absolute, [StringComparison]::Ordinal) -or
        ($matches[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'phase1cb-payload-tool-artifact-invalid'
    }
    $streams = @(Get-Item -LiteralPath $absolute -Stream * -ErrorAction Stop)
    if (@($streams | Where-Object { @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$_.Stream) }).Count -ne 0) {
        throw 'phase1cb-payload-tool-artifact-invalid'
    }
    return $absolute
}

function Assert-PayloadToolArtifact {
    param([string] $Path, [string] $ExpectedSha256)
    try {
        if ($ExpectedSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        $file = Get-ExactOrdinaryFile -Path $Path
        if (-not (Split-Path -Leaf $file).Equals(
                'HowardLab.EbayCrm.PayloadTool.exe',
                [StringComparison]::Ordinal)) {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        $parent = Get-CanonicalPath -Path (Split-Path -Parent $file) -MustExist
        $entries = @([IO.Directory]::GetFileSystemEntries($parent))
        if ($entries.Count -ne 1 -or
            -not $entries[0].Equals($file, [StringComparison]::Ordinal)) {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        $metadata = Get-Item -LiteralPath $file -Force
        if ($metadata.Length -le 0 -or
            -not (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.Equals(
                $ExpectedSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
    } catch {
        throw 'phase1cb-payload-tool-artifact-invalid'
    }
}

function New-PayloadToolArtifact {
    param(
        [string] $SourceExe,
        [string] $ExpectedExeSha256,
        [string] $DestinationRoot
    )
    try {
        if ($ExpectedExeSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        $sourceExe = Get-ExactOrdinaryFile -Path $SourceExe
        if (-not (Split-Path -Leaf $sourceExe).Equals(
                'HowardLab.EbayCrm.PayloadTool.exe',
                [StringComparison]::Ordinal)) {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        $sourceRoot = Get-CanonicalPath -Path (Split-Path -Parent $sourceExe) -MustExist
        Assert-NoReparseChain -Path $sourceRoot -StopRoot ([IO.Path]::GetPathRoot($sourceRoot))
        if (-not (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.Equals(
                $ExpectedExeSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        $destination = Get-CanonicalPath -Path $DestinationRoot
        $parent = Get-CanonicalPath -Path (Split-Path -Parent $destination) -MustExist
        Assert-NoReparseChain -Path $parent -StopRoot ([IO.Path]::GetPathRoot($parent))
        if (Test-Path -LiteralPath $destination) {
            throw 'phase1cb-payload-tool-artifact-invalid'
        }
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        Assert-NoReparseChain -Path $destination -StopRoot $parent
        $target = Join-Path $destination 'HowardLab.EbayCrm.PayloadTool.exe'
        [IO.File]::Copy($sourceExe, $target, $false)
        Assert-PayloadToolArtifact -Path $target -ExpectedSha256 $ExpectedExeSha256
        return $target
    } catch {
        throw 'phase1cb-payload-tool-artifact-invalid'
    }
}

try {
    Clear-BuildInjectionEnvironment
    $repository = Get-CanonicalPath -Path $RepositoryRoot -MustExist
    Set-Location $repository
    Assert-NoReparseChain -Path $repository -StopRoot ([IO.Path]::GetPathRoot($repository))
    if (-not $ClosureOnly) {
        throw 'phase1cb-candidate-validator-task6a-required'
    }
    $artifactsRoot = Get-CanonicalPath -Path (Join-Path $repository 'desktop\windows\artifacts')
    if (-not (Test-Path -LiteralPath $artifactsRoot)) {
        New-Item -ItemType Directory -Path $artifactsRoot | Out-Null
    }
    $output = Get-CanonicalPath -Path $OutputRoot
    if (-not (Test-IsSameOrUnder -Candidate $output -Root $artifactsRoot) -or
        $output.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'phase1cb-output-root-invalid'
    }
    Remove-OwnedOutputRoot -Path $output -AllowedRoot $artifactsRoot
    New-Item -ItemType Directory -Path $output | Out-Null
    $finalBuildRoot = Join-Path $output 'build'
    $payloadRoot = Join-Path $output 'payload'
    $catalogRoot = Join-Path $output 'catalog'
    $appHostRoot = Join-Path $output 'apphost'
    $toolchainRoot = Join-Path $output 'toolchain'
    New-Item -ItemType Directory -Path $toolchainRoot | Out-Null
    New-Item -ItemType Directory -Path $catalogRoot | Out-Null
    New-Item -ItemType Directory -Path $appHostRoot | Out-Null

    if ([string]::IsNullOrWhiteSpace($PayloadToolExePath) -or
        $PayloadToolSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'phase1cb-payload-tool-identity-required'
    }
    $payloadToolExe = New-PayloadToolArtifact `
        -SourceExe $PayloadToolExePath `
        -ExpectedExeSha256 $PayloadToolSha256 `
        -DestinationRoot (Join-Path $toolchainRoot 'payload-tool')

    if ([string]::IsNullOrWhiteSpace($NodeArchivePath)) {
        if ($Offline) { throw 'phase1cb-offline-node-archive-required' }
        $archive = Join-Path $toolchainRoot $nodeArchiveName
        Invoke-WebRequest -UseBasicParsing -Uri $nodeArchiveUrl -OutFile $archive
    } else {
        $archive = Get-CanonicalPath -Path $NodeArchivePath -MustExist
    }
    $archiveHash = Get-ExactPinnedSha256 `
        -Path $archive -ExpectedLength $nodeArchiveLength `
        -Reason 'phase1cb-node-archive-hash-invalid'
    if (-not $archiveHash.Equals($nodeArchiveSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-node-archive-hash-invalid'
    }
    $extractRoot = Join-Path $toolchainRoot 'node'
    Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
    $extractedNode = Get-CanonicalPath -Path (Join-Path $extractRoot "$($nodeArchiveName.Substring(0, $nodeArchiveName.Length - 4))\node.exe") -MustExist
    $executableHash = Get-ExactPinnedSha256 `
        -Path $extractedNode -ExpectedLength $nodeExecutableLength `
        -Reason 'phase1cb-node-executable-hash-invalid'
    if (-not $executableHash.Equals($nodeExecutableSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-node-executable-hash-invalid'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $extractedNode
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Subject.Equals($nodeSubject, [StringComparison]::Ordinal)) {
        throw 'phase1cb-node-authenticode-invalid'
    }

    if ([string]::IsNullOrWhiteSpace($GitArchivePath)) {
        if ($Offline) { throw 'phase1cb-offline-git-archive-required' }
        $gitArchive = Join-Path $toolchainRoot $gitArchiveName
        Invoke-WebRequest -UseBasicParsing -Uri $gitArchiveUrl -OutFile $gitArchive
    } else {
        $gitArchive = Get-CanonicalPath -Path $GitArchivePath -MustExist
    }
    $gitArchiveHash = Get-ExactPinnedSha256 `
        -Path $gitArchive -ExpectedLength $gitArchiveLength `
        -Reason 'phase1cb-git-archive-hash-invalid'
    if (-not $gitArchiveHash.Equals($gitArchiveSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-git-archive-hash-invalid'
    }
    $extractedMinGit = Expand-PinnedMinGitArchive `
        -ArchivePath $gitArchive `
        -DestinationRoot (Join-Path $toolchainRoot 'mingit')
    $extractedGit = Get-CanonicalPath -Path (Join-Path $extractedMinGit 'cmd\git.exe') -MustExist
    $gitExecutableHash = Get-ExactPinnedSha256 `
        -Path $extractedGit -ExpectedLength $gitExecutableLength `
        -Reason 'phase1cb-git-executable-hash-invalid'
    if (-not $gitExecutableHash.Equals($gitExecutableSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-git-executable-hash-invalid'
    }
    $gitSignature = Get-AuthenticodeSignature -LiteralPath $extractedGit
    if ($gitSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $gitSignature.SignerCertificate -or
        -not $gitSignature.SignerCertificate.Subject.Equals($gitSubject, [StringComparison]::Ordinal)) {
        throw 'phase1cb-git-authenticode-invalid'
    }
    $observedGitVersion = @{ Count = 0; Value = $null }
    Invoke-BoundedGitRecords `
        -SourceRoot $extractedMinGit `
        -GitExecutable $extractedGit `
        -RawArguments `
        -GitArguments @('--version') `
        -RecordDelimiter 10 `
        -MaximumRecords 1 `
        -MaximumRecordBytes 128 `
        -OnRecord {
            param($record)
            $observedGitVersion.Count = [int]$observedGitVersion.Count + 1
            $observedGitVersion.Value = ([string]$record).TrimEnd("`r")
        }
    if ([int]$observedGitVersion.Count -ne 1 -or
        -not ([string]$observedGitVersion.Value).Equals(
            "git version $gitVersion", [StringComparison]::Ordinal)) {
        throw 'phase1cb-git-version-invalid'
    }

    $sourceCommit = Get-SourceHead -SourceRoot $repository -GitExecutable $extractedGit
    $stagingCandidates = @(
        (Join-Path ([IO.Path]::GetPathRoot($repository)) 'HowardLabPhase1CBStaging'),
        (Join-Path ([IO.Directory]::GetParent($repository).FullName) '.HowardLabPhase1CBStaging')
    )
    foreach ($candidate in $stagingCandidates) {
        $candidateExisted = Test-Path -LiteralPath $candidate
        if (Test-StagingParentWritableAndIsolated -Candidate $candidate) {
            $stagingParent = Get-CanonicalPath -Path $candidate -MustExist
            $stagingParentCreated = -not $candidateExisted
            break
        }
    }
    if ($null -eq $stagingParent) { throw 'phase1cb-staging-parent-unavailable' }
    Assert-NoReparseChain -Path $stagingParent -StopRoot ([IO.Path]::GetPathRoot($stagingParent))
    $stagingRoot = Join-Path $stagingParent $sourceCommit
    Remove-OwnedOutputRoot -Path $stagingRoot -AllowedRoot $stagingParent
    [IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
    $buildRoot = Get-CanonicalPath -Path $stagingRoot -MustExist
    Copy-TrackedSource -SourceRoot $repository -DestinationRoot $buildRoot `
        -ExpectedCommit $sourceCommit -GitExecutable $extractedGit `
        -AllowUntrustedSource:$ClosureOnly
    $dotnetProfile = Join-Path $toolchainRoot 'dotnet-profile'
    $buildToolchain = Join-Path $buildRoot '.phase1cb-toolchain'
    New-Item -ItemType Directory -Path $buildToolchain | Out-Null
    Copy-Item -Path (Join-Path (Split-Path -Parent $extractedNode) '*') -Destination $buildToolchain -Recurse
    $pinnedNode = Join-Path $buildToolchain 'node.exe'
    $pinnedNodeHash = Get-ExactPinnedSha256 `
        -Path $pinnedNode -ExpectedLength $nodeExecutableLength `
        -Reason 'phase1cb-copied-node-hash-invalid'
    if (-not $pinnedNodeHash.Equals($nodeExecutableSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-copied-node-hash-invalid'
    }
    $pinnedMinGit = Copy-ExactOrdinaryTree `
        -SourceRoot $extractedMinGit `
        -DestinationRoot (Join-Path $buildToolchain 'mingit')
    $pinnedGit = Get-CanonicalPath -Path (Join-Path $pinnedMinGit 'cmd\git.exe') -MustExist
    $pinnedGitHash = Get-ExactPinnedSha256 `
        -Path $pinnedGit -ExpectedLength $gitExecutableLength `
        -Reason 'phase1cb-copied-git-hash-invalid'
    if (-not $pinnedGitHash.Equals($gitExecutableSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-copied-git-hash-invalid'
    }
    $pinnedGitSignature = Get-AuthenticodeSignature -LiteralPath $pinnedGit
    if ($pinnedGitSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $pinnedGitSignature.SignerCertificate -or
        -not $pinnedGitSignature.SignerCertificate.Subject.Equals(
            $gitSubject, [StringComparison]::Ordinal)) {
        throw 'phase1cb-copied-git-authenticode-invalid'
    }
    $observedPinnedGitVersion = @{ Count = 0; Value = $null }
    Invoke-BoundedGitRecords `
        -SourceRoot $pinnedMinGit `
        -GitExecutable $pinnedGit `
        -RawArguments `
        -GitArguments @('--version') `
        -RecordDelimiter 10 `
        -MaximumRecords 1 `
        -MaximumRecordBytes 128 `
        -OnRecord {
            param($record)
            $observedPinnedGitVersion.Count = [int]$observedPinnedGitVersion.Count + 1
            $observedPinnedGitVersion.Value = ([string]$record).TrimEnd("`r")
        }
    if ([int]$observedPinnedGitVersion.Count -ne 1 -or
        -not ([string]$observedPinnedGitVersion.Value).Equals(
            "git version $gitVersion", [StringComparison]::Ordinal)) {
        throw 'phase1cb-copied-git-version-invalid'
    }

    Assert-PayloadToolArtifact -Path $payloadToolExe -ExpectedSha256 $PayloadToolSha256
    Invoke-BoundedPayloadTool $payloadToolExe @(
        'staging', 'normalize-sdk',
        '--build-root', $buildRoot,
        '--source-project', (Join-Path $repository 'packages/twenty-sdk/project.json')
    ) $dotnetProfile

    $compiledDesktopNode = Join-Path $buildRoot 'desktop\windows\node\publish'
    Assert-PayloadToolArtifact -Path $payloadToolExe -ExpectedSha256 $PayloadToolSha256
    Invoke-BoundedPayloadTool $payloadToolExe @(
        'build', 'execute',
        '--node', $pinnedNode,
        '--git-root', $pinnedMinGit,
        '--build-root', $buildRoot,
        '--compiled-desktop-node-root', $compiledDesktopNode
    ) $dotnetProfile

    Assert-NoReparseChain -Path $buildRoot -StopRoot $stagingParent
    if (Test-Path -LiteralPath $finalBuildRoot) { throw 'phase1cb-final-build-not-clean' }
    [IO.Directory]::Move($buildRoot, $finalBuildRoot)
    $stagingRoot = $null
    $buildRoot = Get-CanonicalPath -Path $finalBuildRoot -MustExist
    Assert-NoReparseChain -Path $buildRoot -StopRoot $output
    $pinnedNode = Get-CanonicalPath -Path (Join-Path $buildRoot '.phase1cb-toolchain\node.exe') -MustExist
    $compiledDesktopNode = Get-CanonicalPath -Path (Join-Path $buildRoot 'desktop\windows\node\publish') -MustExist

    Set-Location $repository
    [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
    Assert-PayloadToolArtifact -Path $payloadToolExe -ExpectedSha256 $PayloadToolSha256
    Invoke-BoundedPayloadTool $payloadToolExe @(
        'closure', 'materialize',
        '--build-root', $buildRoot, '--payload-root', $payloadRoot,
        '--node', $pinnedNode,
        '--node-archive', $archive,
        '--inventory', (Join-Path $buildRoot 'desktop\windows\runtime\production\production-entrypoints-v1.json'),
        '--compiled-desktop-node-root', $compiledDesktopNode,
        '--closure-only'
    ) $dotnetProfile
    $payloadNodeHash = Get-ExactPinnedSha256 `
        -Path (Join-Path $payloadRoot 'node.exe') -ExpectedLength $nodeExecutableLength `
        -Reason 'phase1cb-payload-node-hash-invalid'
    if (-not $payloadNodeHash.Equals($nodeExecutableSha256, [StringComparison]::Ordinal)) {
        throw 'phase1cb-payload-node-hash-invalid'
    }
    Assert-MinGitAbsentFromPayload -PayloadRoot $payloadRoot

    Write-Output "classification=untrusted-build-closure"
    Write-Output "node=$nodeVersion archiveSha256=$archiveHash executableSha256=$payloadNodeHash yarn=$yarnVersion git=$gitVersion gitArchiveSha256=$gitArchiveHash gitExecutableSha256=$pinnedGitHash"
    Write-Output "sourceCommit=$sourceCommit finalReparsePoints=0"
    exit 0
} finally {
    if ($null -ne $stagingRoot -and $null -ne $stagingParent -and
        (Test-Path -LiteralPath $stagingRoot)) {
        Remove-OwnedOutputRoot -Path $stagingRoot -AllowedRoot $stagingParent
    }
    if ($stagingParentCreated -and $null -ne $stagingParent -and
        (Test-Path -LiteralPath $stagingParent) -and
        [IO.Directory]::GetFileSystemEntries($stagingParent).Length -eq 0) {
        Assert-NoReparseChain -Path $stagingParent -StopRoot ([IO.Path]::GetPathRoot($stagingParent))
        [IO.Directory]::Delete($stagingParent)
    }
    Restore-BuildInjectionEnvironment -Original $originalBuildInjectionEnvironment
    [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
    [Environment]::SetEnvironmentVariable('NX_DAEMON', $originalNxDaemon, 'Process')
    Set-Location $originalLocation
}

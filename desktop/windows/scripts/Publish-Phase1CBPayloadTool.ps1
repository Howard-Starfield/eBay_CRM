[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [Parameter(Mandatory = $true)][string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
}
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$project = Join-Path $repository 'desktop\windows\tools\HowardLab.EbayCrm.PayloadTool\HowardLab.EbayCrm.PayloadTool.csproj'
$output = [IO.Path]::GetFullPath($OutputRoot)
if (-not [IO.Path]::IsPathRooted($OutputRoot) -or
    -not (Test-Path -LiteralPath $project -PathType Leaf) -or
    (Test-Path -LiteralPath $output)) {
    throw 'phase1cb-payload-tool-publish-path-invalid'
}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $output
if ($LASTEXITCODE -ne 0) {
    throw 'phase1cb-payload-tool-publish-failed'
}
$entries = @([IO.Directory]::GetFileSystemEntries($output))
if ($entries.Count -ne 1 -or
    -not (Split-Path -Leaf $entries[0]).Equals(
        'HowardLab.EbayCrm.PayloadTool.exe',
        [StringComparison]::Ordinal)) {
    throw 'phase1cb-payload-tool-publish-artifacts-invalid'
}
$artifact = Get-Item -LiteralPath $entries[0] -Force
if ($artifact.PSIsContainer -or $artifact.Length -le 0 -or
    ($artifact.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'phase1cb-payload-tool-publish-artifacts-invalid'
}
$streams = @(Get-Item -LiteralPath $artifact.FullName -Stream * -ErrorAction Stop)
if (@($streams | Where-Object { @('$DATA', ':$DATA', '::$DATA') -notcontains ([string]$_.Stream) }).Count -ne 0) {
    throw 'phase1cb-payload-tool-publish-artifacts-invalid'
}
$sha256 = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
Write-Output "artifact=$($artifact.FullName)"
Write-Output "length=$($artifact.Length)"
Write-Output "sha256=$sha256"

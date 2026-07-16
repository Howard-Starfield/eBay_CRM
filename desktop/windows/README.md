# eBay CRM Windows AppHost

This directory contains the Windows-only .NET 10 AppHost solution. Run the
commands below from the repository root.

## Prerequisites

- Install a .NET 10 SDK. The repository `global.json` selects SDK 10.0.100 and
  permits roll-forward to the latest installed .NET 10 feature band.
- Extract PostgreSQL 16 locally and set `EBAYCRM_POSTGRES_BIN` to the directory
  containing `postgres.exe`. Tests must not depend on a system PostgreSQL
  service.

```powershell
$env:EBAYCRM_POSTGRES_BIN = "$PWD\.tools\postgresql\16\bin"
& "$env:EBAYCRM_POSTGRES_BIN\postgres.exe" --version
```

## Phase 1B authoritative acceptance sequence

Regenerate lock files only when package inputs intentionally change. Normal
verification pins the repository PostgreSQL 16 binaries, uses locked restore,
publishes before any test that consumes the published folder, requires the S4U
test to fail instead of skip when Windows policy is unavailable, and runs
host-kill tests in their own nonparallel command.

```powershell
$env:EBAYCRM_POSTGRES_BIN = "$PWD\.tools\postgresql\16\bin"
$env:EBAYCRM_RELEASE_ACCEPTANCE = "1"
& "$env:EBAYCRM_POSTGRES_BIN\postgres.exe" --version

dotnet restore desktop\windows\EbayCrm.Desktop.sln --locked-mode
dotnet build desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-restore --nologo

Remove-Item -Recurse -Force desktop\windows\artifacts\win-x64 -ErrorAction SilentlyContinue
dotnet publish desktop\windows\src\HowardLab.EbayCrm.AppHost\HowardLab.EbayCrm.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true --output desktop\windows\artifacts\win-x64 --no-restore --nologo -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false

dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~TimeoutReconciliationAcceptanceTests|FullyQualifiedName~DiagnosticCanaryAcceptanceTests|FullyQualifiedName~CrossSessionOwnershipAcceptanceTests" --logger "console;verbosity=detailed" --nologo

# AppHostShutdownTests contains the late server/worker stop boundaries, so this
# supplement is required to execute all four real role-boundary variants.
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --no-build --filter "FullyQualifiedName~TimeoutReconciliationAcceptanceTests|FullyQualifiedName~AppHostShutdownTests" --logger "console;verbosity=detailed" --nologo

dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests\HowardLab.EbayCrm.AppHost.Core.Tests.csproj --configuration Release --no-restore --no-build --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Windows.Tests\HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --no-restore --no-build --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --no-build --filter "Category!=DestructiveContainment" --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --no-build --filter "Category=DestructiveContainment" --nologo -- RunConfiguration.DisableAppDomain=true
```

The focused Phase 1B filter plus the role-boundary supplement are the release
gates for the four real server/worker start/stop timeout boundaries, bounded
production diagnostics, and same-user cross-session ownership. Both commands
must report zero failures and zero skips. The first two partition commands cover
the Core and Windows suites. The third command is the non-destructive integration
partition; the final command is the destructive containment partition and must
remain isolated and nonparallel.

The publish folder is generated evidence and must not be committed. Required
acceptance tests expect `desktop\windows\artifacts\win-x64` to contain the
AppHost, the attested Fixture closure, migrations, `hostfxr.dll`, `coreclr.dll`,
and `System.Private.CoreLib.dll`.

When package inputs intentionally change, run `dotnet restore
desktop\windows\EbayCrm.Desktop.sln --force-evaluate`, review every lock-file
change, then repeat the locked sequence above.

## Phase 1C scripted verification

Phase 1C packages the locked restore, Release build, clean self-contained
publish, Node protocol/probe gates, isolated .NET test partitions, publish-file
checks, and final matching-process/temp cleanup scan into one command:

```powershell
& .\desktop\windows\scripts\Verify-Phase1C.ps1
```

That default command remains the full Phase 1C gate. For a focused rebuild and
review of only the published Node probe boundary, use:

```powershell
& .\desktop\windows\scripts\Verify-Phase1C.ps1 -PublishedNodeProbeOnly
```

The focused switch still performs the locked restore, zero-warning Release
build, clean self-contained `win-x64` publish, Node typecheck and 61-test
protocol/probe suite, generated payload staging and validation, published
external AppHost smoke test, publish inventory checks, and final cleanup audit.
It skips the Core, Windows, ordinary integration, and destructive containment
partitions. Those partitions remain part of the default full gate.

The generated payload is rooted at
`desktop\windows\artifacts\win-x64\node-probe`. It contains a copied
`node.exe`, a minimal `package.json`, six compiled JavaScript files under
`app`, and `node-payload-manifest-v1.json`. The manifest declares the eight
runtime artifacts in ordinal path order with byte length and SHA-256, identifies
the server and worker entrypoints, and binds them to build identity
`published-node-probe/1`. The manifest is the ninth payload file. The entire
`desktop\windows\artifacts\win-x64` tree is generated evidence and must never
be committed.

The script defaults PostgreSQL to `.tools\postgresql\16\bin` and Node to the
`node` command. An explicit parameter takes precedence over the corresponding
environment variable:

```powershell
& .\desktop\windows\scripts\Verify-Phase1C.ps1 `
  -PostgresBin 'C:\tools\postgresql-16\bin' `
  -NodeExe 'C:\Program Files\nodejs\node.exe'
```

`EBAYCRM_POSTGRES_BIN` and `EBAYCRM_NODE_EXE` remain supported when parameters
are omitted. The script sets `EBAYCRM_RELEASE_ACCEPTANCE=1`, deletes only the
validated non-reparse-point `desktop\windows\artifacts\win-x64` directory,
publishes with the Phase 1B settings above, and always performs the Phase 1C
cleanup audit after the verification attempt. The cleanup command can also be
run independently; pass the UTC start of the run whose temporary artifacts
should be audited:

```powershell
& .\desktop\windows\scripts\Test-Phase1CCleanup.ps1 `
  -RunStartUtc ([datetime]'2026-07-15T12:00:00Z')
```

The standalone cleanup script's process portion is a non-exhaustive matching
scan: it reports relevant processes only when an accessible command line
contains this repository/worktree path. Inaccessible or null command lines and
descendant command lines that omit the path are not observable matches, and the
script does not delete temporary directories. This scan alone is not exact
ownership or exhaustive descendant-cleanup evidence. The published external
smoke separately provides exact completion-port ledger and cumulative Job
accounting closure for every process in that smoke run; see the Phase 1C
production launch boundary report.

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

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

## Authoritative acceptance sequence

Regenerate lock files only when package inputs intentionally change. Normal
verification uses locked restore, publishes before any test that consumes the
published folder, and runs host-kill tests in their own nonparallel command.

```powershell
dotnet restore desktop\windows\EbayCrm.Desktop.sln --locked-mode
dotnet build desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-restore
Remove-Item -Recurse -Force desktop\windows\artifacts\win-x64 -ErrorAction SilentlyContinue
dotnet publish desktop\windows\src\HowardLab.EbayCrm.AppHost\HowardLab.EbayCrm.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true --output desktop\windows\artifacts\win-x64 -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests --configuration Release --no-restore --no-build
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Windows.Tests --configuration Release --no-restore --no-build
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests --configuration Release --no-restore --no-build --filter "Category!=DestructiveContainment"
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests --configuration Release --no-restore --no-build --filter "Category=DestructiveContainment" -- RunConfiguration.DisableAppDomain=true
```

The publish folder is generated evidence and must not be committed. Required
acceptance tests expect `desktop\windows\artifacts\win-x64` to contain the
AppHost, the attested Fixture closure, migrations, `hostfxr.dll`, `coreclr.dll`,
and `System.Private.CoreLib.dll`.

When package inputs intentionally change, run `dotnet restore
desktop\windows\EbayCrm.Desktop.sln --force-evaluate`, review every lock-file
change, then repeat the locked sequence above.

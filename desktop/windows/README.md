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

## Restore

Regenerate lock files only when package inputs intentionally change, then
verify that the solution restores from those lock files:

```powershell
dotnet restore desktop\windows\EbayCrm.Desktop.sln --force-evaluate
dotnet restore desktop\windows\EbayCrm.Desktop.sln --locked-mode
```

## Build and test

```powershell
dotnet build desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-restore
dotnet test desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-build
```

## Publish

```powershell
dotnet publish desktop\windows\src\HowardLab.EbayCrm.AppHost\HowardLab.EbayCrm.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true --output desktop\windows\artifacts\win-x64 -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false
```

# Phase 1C production launch boundary report

## Decision

ADOPT_PRODUCTION_LAUNCH_BOUNDARY

This Phase 1C-A decision is supported by the partitioned evidence below. It
adopts the tested production-shaped Node launch boundary; it does not claim a
full Twenty boot or combine results from different source identities into a new
global test total.

The reviewed boundary replaces fixture-specific launch construction with a
validated, leased Node payload and role-specific launch plans while retaining
the existing AppHost lifecycle, control protocol, Job containment, PostgreSQL
supervision, and explicit `RedisCompatibility` selection.

## Evidence identity

- Tested code commit: `4740e5274b748983acd44375e67e5cfb26baa93c`
- Branch: `codex/phase-1c-production-launch-boundary`
- Upstream Twenty `main` reference: `5f8baa9761d658dd5de57059b10cbaab5510c936`
- Host: Microsoft Windows NT 10.0.26200.0, x64
- .NET SDK: 10.0.302
- PostgreSQL: 16.14
- Node: 24.18.0
- Yarn: 4.13.0

Commit `4740e527` corrects the destructive acceptance inventory to the reviewed
213-file publish and is the current tested code identity. The focused
published-Node, Core, and Windows results were recorded at its direct
predecessor `c72c7693`; the only intervening change is the reviewed acceptance
inventory correction. Each row retains its actual execution identity. The
ordinary integration baseline is older and labeled separately; no global
unique test total is reported.

## Recorded partition evidence

| Gate | Commit | Result |
| --- | --- | --- |
| Focused published-Node command | `c72c7693` | Exit 0 in 31.3 seconds |
| Release build within focused command | `c72c7693` | 0 warnings, 0 errors |
| Clean self-contained `win-x64` publish | `c72c7693` | Generated successfully |
| Node typecheck and tests | `c72c7693` | Typecheck passed; 61/61 tests |
| Published external AppHost smoke | `c72c7693` | 1/1 in 12 seconds |
| Focused cleanup audit | `c72c7693` | Clean |
| AppHost startup/provider partition | `887da2af` | 111/111, 0 skipped; independent review clean |
| Core suite | `c72c7693` | 190/190, 0 failed, 0 skipped |
| Windows suite | `c72c7693` | 284/284, 0 failed, 0 skipped |
| Corrected inventory focused test | `4740e527` | 1/1; independent review passed |
| Destructive containment, acceptance enabled | `4740e527` | 3/3, 0 failed, 0 skipped; primary fresh rerun completed in 43.5 seconds |
| Generated publish inventory inspection | `4740e527` working tree | Publish: 213 files, 173,998,102 bytes; Node payload: 9 files, 92,578,940 bytes; manifest: 8 artifacts |

The primary focused command was:

```powershell
& .\desktop\windows\scripts\Verify-Phase1C.ps1 -PublishedNodeProbeOnly
```

It performed locked restore, the Release build, a clean untrimmed,
non-single-file, non-AOT self-contained publish, Node typecheck/tests, payload
generation, the published smoke, inventory checks, and cleanup audit. It
intentionally skipped Core, Windows, ordinary integration, and destructive
containment; the default command remains the full gate.

## Published Node payload inventory

The generated publish root is
`desktop\windows\artifacts\win-x64` and must never be committed. Its reviewed
inventory is 213 files totaling 173,998,102 bytes. The `node-probe` subtree is
9 files totaling 92,578,940 bytes: 8 runtime artifacts declared by
`node-payload-manifest-v1.json`, plus the manifest itself.

The manifest is version 1, uses build identity `published-node-probe/1`, names
`node.exe`, `app/probes/server-probe.js`, and
`app/probes/worker-probe.js`, and records the length and SHA-256 of each
declared artifact. Its declared closure contains `node.exe`, `package.json`,
and these six compiled modules:

- `app/control/apphost-control-client.js`
- `app/control/identity-health-server.js`
- `app/probes/probe-orchestrator.js`
- `app/probes/server-probe.js`
- `app/probes/worker-probe.js`
- `app/protocol/control-protocol.js`

The reviewed payload had no undeclared file. A case-insensitive scan of all
payload text and relative filenames found 0 `secret` or `canary` matches.

## External lifecycle and process evidence

The external published smoke launched
`HowardLab.EbayCrm.AppHost.exe` from the publish directory with exact
`EBAYCRM_RELEASE_ACCEPTANCE=1`, backend `redis`, and target
`controlled-node-probe`. The process exited 0; stdout contained `Ready` before
`Stopped`; the profile tree was removed; and no PostgreSQL `postmaster.pid`
remained. This acceptance mode is a production-shaped protocol and lifecycle
probe, not a real Twenty server or worker boot.

Exact process closure comes from a Job completion-port ledger, not executable
name inference. It records PID plus creation time, image, parent PID plus parent
creation time, and start/exit observations for every Job process. The smoke
proved one AppHost identity, exactly two `node.exe` identities, PostgreSQL
identity presence, unique identity keys, a complete in-ledger parent chain for
every child, an exit observation for every recorded process, zero active Job
processes, and cumulative Job `TotalProcesses` exactly equal to the ledger
count.

That exact completion-port and Job-accounting closure is separate from the
cleanup script's non-exhaustive command-line substring scan. The latter can
report only accessible relevant processes whose command line contains this
repository/worktree path; inaccessible or null command lines and descendant
commands that omit the path are not observable matches. It is useful cleanup
evidence but is not exact ownership proof.

## Reused baseline

The ordinary `Category!=DestructiveContainment` integration result is reused as
an explicitly unchanged baseline: 394/394 at source commit
`f4a24581097aceaef0de274dc96c9e656e3a71bf`. It was not executed at current
HEAD, is not combined into a new total, and is not evidence that the partition
passes at `4740e527`. The prior baseline included both real PostgreSQL late-stop
cases.

## Current remaining-partition commands

Run from the repository root in Windows PowerShell 5.1:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests\HowardLab.EbayCrm.AppHost.Core.Tests.csproj `
  --configuration Release --no-restore --no-build --nologo

dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Windows.Tests\HowardLab.EbayCrm.AppHost.Windows.Tests.csproj `
  --configuration Release --no-restore --no-build --nologo

$env:EBAYCRM_RELEASE_ACCEPTANCE = '1'
$env:EBAYCRM_POSTGRES_BIN = "$PWD\.tools\postgresql\16\bin"
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj `
  --configuration Release --no-restore --no-build `
  --filter 'Category=DestructiveContainment' --nologo -- `
  RunConfiguration.DisableAppDomain=true
```

At tested code commit `4740e527`, the corrected focused inventory test passed
1/1, an isolated destructive run passed 3/3 with 0 skipped, and the primary
fresh destructive rerun passed 3/3 with 0 skipped in 43.5 seconds. The exact
`EBAYCRM_RELEASE_ACCEPTANCE=1` value is required; runs without it are gated
skips and are not acceptance evidence.

## Read-only evidence inspection commands

Run these Windows PowerShell 5.1-compatible commands from the repository root.
They inspect the existing generated payload and documentation without modifying
files:

```powershell
$publishRoot = (Resolve-Path 'desktop\windows\artifacts\win-x64').Path
$publishFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$publishBytes = ($publishFiles | Measure-Object -Property Length -Sum).Sum
[pscustomobject]@{
  PublishFileCount = $publishFiles.Count
  PublishBytes = $publishBytes
}

$nodeRoot = Join-Path $publishRoot 'node-probe'
$nodeFiles = @(Get-ChildItem -LiteralPath $nodeRoot -Recurse -File)
$nodeBytes = ($nodeFiles | Measure-Object -Property Length -Sum).Sum
$manifestPath = Join-Path $nodeRoot 'node-payload-manifest-v1.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
[pscustomobject]@{
  NodePayloadFileCount = $nodeFiles.Count
  NodePayloadBytes = $nodeBytes
  ManifestArtifactCount = @($manifest.artifacts).Count
}

$textFiles = @($nodeFiles | Where-Object { $_.Name -ne 'node.exe' })
$textMatches = @(
  $textFiles | Select-String -Pattern 'secret|canary' -CaseSensitive:$false
)
$relativeNames = @(
  $nodeFiles | ForEach-Object { $_.FullName.Substring($nodeRoot.Length + 1) }
)
$relativeNameMatches = @(
  $relativeNames | Where-Object { $_ -match '(?i)secret|canary' }
)
[pscustomobject]@{
  TextSecretOrCanaryMatches = $textMatches.Count
  RelativeNameSecretOrCanaryMatches = $relativeNameMatches.Count
}

$reportPath = 'docs\architecture\phase-1c-production-launch-boundary-report.md'
$reportText = Get-Content -LiteralPath $reportPath -Raw
$decisionToken = 'ADOPT_PRODUCTION_' + 'LAUNCH_BOUNDARY'
[regex]::Matches($reportText, [regex]::Escape($decisionToken)).Count

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
```

The recorded results are 213 publish files and 173,998,102 bytes; 9 Node
payload files and 92,578,940 bytes; 8 manifest artifacts; 0 text matches; 0
relative-filename matches; one adoption token; and `git diff --check` exit 0.

## Scope limits and deferred product work

PostgreSQL remains part of the architecture. `RedisCompatibility` remains the
explicit selected backend, and `postgres-desktop` remains fail-closed and
incomplete. This phase neither removes Redis nor bundles a Redis runtime.

The published Node probe does not launch the full Twenty server or worker. The
eBay UI, bundled Redis, installer, tray, updater, backup/restore workflow, and
local LLM runtime are outside this phase. The acceptance environment and target
are enabled only by exact `EBAYCRM_RELEASE_ACCEPTANCE=1`; they are evidence
fixtures and not a general production launch mode.

Full Twenty server/worker, bundled Redis, eBay UI, installer, tray, updater,
backup, and local LLM delivery remain separate phases.

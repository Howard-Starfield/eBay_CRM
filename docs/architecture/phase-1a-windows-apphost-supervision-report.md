# Phase 1A Windows AppHost supervision evidence

Date: 2026-07-14–15 (America/Los_Angeles)

Branch: `codex/phase-1a-apphost`

Evidence parent: `7c58da37`

Tested staged tree before the final evidence-hash update:
`03be8b5c46422355974c7b3dcdfd52bedb306112`. This Git tree object is the
stable, non-self-referential implementation/evidence snapshot; the final commit
also contains this evidence-hash correction.
Scope: Windows AppHost foundation only. No full Twenty server, Redis-free boot,
eBay UI, tray, installer, updater, backup, or local-model runtime was launched or
claimed by this work.

## Environment and inputs

- Windows 11 Home 10.0.26200, build 26200, x64.
- .NET SDK 10.0.302; host/runtime 10.0.10.
- PostgreSQL 16.14, EnterpriseDB Windows x64 installer 16.14-2.
- Installer source recorded by the local package manifest:
  `https://get.enterprisedb.com/postgresql/postgresql-16.14-2-windows-x64.exe`.
- Installer SHA-256:
  `6D3919BC23CFB45E79C6E391DE8B689C32101F2C1B73377AA26E4CE593C0EF28`.
- Installer Authenticode: valid; signer `CN=EnterpriseDB Corporation,
  O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US`; certificate
  thumbprint `7BEDD1269FCCF7A5D95F18274750B79893C06C70`.
- Extracted `postgres.exe` SHA-256:
  `CB0A0F1F2859E76023CCE19280222F976488A1AD6EE9348814D328E3AC030A15`.
  The extracted binary itself is not Authenticode-signed; trust evidence is the
  valid signed installer plus the recorded installer hash.

## Reproducible command evidence

The sequence is authoritative in `desktop/windows/README.md`. The publish step
ran before acceptance tests that consume its folder.

| Command | Observed result |
|---|---|
| `dotnet restore desktop\windows\EbayCrm.Desktop.sln --locked-mode` | passed |
| `dotnet build desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-restore` | passed, 0 warnings, 0 errors |
| self-contained `dotnet publish ... --runtime win-x64 ... PublishSingleFile=false PublishTrimmed=false PublishAot=false` | passed |
| Core tests | 132 passed, 0 failed, 0 skipped |
| Windows tests | 131 passed, 0 failed, 0 skipped |
| Integration, `Category!=DestructiveContainment` | 115 passed, 0 failed, 0 skipped |
| Integration, `Category=DestructiveContainment` | 3 passed, 0 failed, 0 skipped |

Total: 381 passed, 0 failed, 0 skipped. PostgreSQL-backed tests had zero skips.

Every recorded clean `win-x64` publish contained 204 files, including AppHost
EXE/DLL, Fixture EXE/DLL and its attested dependency closure, migration
`0001_apphost_control.sql`, `hostfxr.dll`, `coreclr.dll`, and
`System.Private.CoreLib.dll`. The generated folder is ignored and not committed.

### 2026-07-15 evidence-cleanup replay

The follow-up replay based on commit `65039dabf54b0e42cd4247c993030b8f502f4729`
removed the publish directory before publishing. Two consecutive clean publishes
both produced 204 files totaling 81,233,630 bytes, with zero differences across
relative path, length, and SHA-256. An earlier run recorded 81,233,626 bytes,
and an independent clean controller replay of commit `c8ca33da` recorded
81,233,622 bytes. No cause is attributed to these small cross-rebuild byte-total
differences because the earlier per-file inventories were not retained.
The tested follow-up staged tree before the final metadata additions was
`3f2088f95c474f960b1fc91d63f5c907b4b11b55`.

For the implementer run, the inventory remained 204 files and 81,233,630 bytes
before and after the complete destructive partition. The independent controller
run remained at 204 files, contained zero evidence files, and totaled
81,233,622 bytes after its destructive partition. The acceptance invariant is
per-run immutability: the containment test emits current-run identity JSON
through xUnit output and asserts that every published artifact's relative path,
length, and SHA-256 remains unchanged from that run's clean pre-test snapshot.
It does not require byte-for-byte equality across independent clean rebuilds and
does not write test evidence into the application publish directory.

## Acceptance-gate observations

1. **Published external termination — pass.** The destructive test sampled the
   AppHost tree while startup ran and retained process handles plus PID and UTC
   creation time for the AppHost, PostgreSQL helpers/postmaster/backends, both
   Fixture roles, and immediate descendants. External AppHost termination was
   performed through the retained AppHost identity. Every retained identity
   signaled inside ten seconds, identity-safe reopen checks passed, and the same
   profile cold-started to `Ready` again.
2. **Leaked Job-handle negative control — pass.** Existing real integration
   coverage duplicates the worker Job handle into the child, demonstrates the
   held boundary, then executes the shared escalation and proves the worker,
   server, and postmaster are gone. Cleanup is in `finally`/disposal paths.
3. **Pipe/generation/impostor rejection — pass.** Existing Windows and AppHost
   tests reject stale generation, old operation, wrong nonce/build/time, and
   wrong-process identity before control is accepted.
4. **Postmaster independence and SQL readiness — pass.** Real PostgreSQL tests
   retain and image-verify the postmaster after `pg_ctl` exits and require an
   authenticated `SELECT 1` readiness result tied to the expected data folder.
5. **Late timeout reconciliation — partial, not passed.** Task 10 acceptance tests force
   one-millisecond start and stop deadlines. A second operation is rejected
   while indeterminate; retained-identity/SQL reconciliation produces one
   running or stopped database role with no duplicate. The current production
   composition does not expose a safe fault-injection seam for genuinely late
   server/worker start and stop, so their no-duplicate outcome was not proven by
   the real-timing acceptance test and is not claimed.
6. **Simultaneous failure coalescing — pass.** A real three-role crash initially
   exposed a race where recovery drained an already-disconnected worker. The
   corrected path waits on the retained contained process identity. The focused
   gate passed three consecutive runs and the full non-destructive suite passed;
   exactly one new `Ready` transition is asserted.
7. **Abrupt death and cold restart — pass.** Covered by the published external
   termination test described in gate 1.
8. **Twenty same-profile contenders — pass.** Twenty published AppHost processes
   were launched against one disposable profile. Exactly one reached `Ready`;
   all 19 losers exited with code 2 and exactly `profile-already-owned`. Profile
   ownership is now acquired before shared-port validation.
9. **Bounded output flood — pass.** Task 10 drives the real published Fixture
   flood mode, drains stdout/stderr without deadlock, and explicitly asserts the
   64 KiB retained-byte bound, 4 KiB line bound, 32 MiB managed-memory delta,
   and zero emitted files. File-count and file-size rejection are also exercised
   by the Task 10 artifact scanner.
10. **Production diagnostic artifact scan — not fully present.** The scanner
    itself proves content detection across 4,096-byte boundaries, relative
    filename checks, ZIP-entry and manifest name/content checks, and file
    count/size bounds including ZIP entries.
    A real Fixture prints an exact registered canary; the retained child output
    is `[REDACTED]` and a subsequent scan of the emitted file reports zero
    findings (one emitted file scanned). However, production composition still
    uses `NoopDiagnosticSink`; there is no production JSONL/diagnostic ZIP set to
    scan. This category is absent, not represented as synthetic production
    evidence.
11. **Known and interrupted migrations — pass.** Real migration tests prove the
    known catalog outcome, host-kill interruption marker, exact cluster/catalog
    validation, and successful cold classification/recovery.
12. **One bounded shutdown/escalation — pass.** Existing integration coverage
    asserts one monotonic budget, one escalation, idempotent second stop, no
    restart after shutdown, and zero retained descendants.
13. **Restart-budget exhaustion — pass.** Task 10 now exhausts the production
    restart budget through four real worker Job/process crashes in a real
    composition. It asserts exactly one `Faulted` transition and one durable
    fault record, waits for ownership release, proves the retained database,
    server, and worker identities are gone, and proves the history does not grow
    into a restart loop after cleanup.
14. **Process allowlist/no Twenty assertion — pass.** The published-tree test
    admits AppHost, Fixture, PostgreSQL binaries, and only `cmd.exe`/`conhost.exe`
    helpers whose retained parent/grandparent is an admitted PostgreSQL binary.
    Creation-time ordering rejects stale parent-PID associations. No Node,
    Redis, or full Twenty process was launched or globally inspected/killed.

The passing external-death run retained each native handle and PID/creation-time
pair during the run and emitted the current-run JSON through xUnit output rather
than writing into the publish folder. Every listed retained identity was
signaled after external AppHost termination. The exact evidence below remains
committed for the recorded run, while the destructive regression asserts that
the publish folder's relative paths, lengths, and SHA-256 values are unchanged.

| PID | Parent PID | UTC creation time | Image | Signaled |
|---:|---:|---|---|---|
| 35420 | 0 | `2026-07-15T06:32:39.5455845Z` | `HowardLab.EbayCrm.AppHost.exe` | true |
| 11916 | 35420 | `2026-07-15T06:32:39.8233489Z` | `initdb.exe` | true |
| 2816 | 11916 | `2026-07-15T06:32:39.8393699Z` | `initdb.exe` | true |
| 15476 | 2816 | `2026-07-15T06:32:39.8534552Z` | `cmd.exe` | true |
| 27872 | 2816 | `2026-07-15T06:32:39.9137722Z` | `cmd.exe` | true |
| 33888 | 27872 | `2026-07-15T06:32:39.9361435Z` | `postgres.exe` | true |
| 30164 | 2816 | `2026-07-15T06:32:39.9679616Z` | `cmd.exe` | true |
| 34664 | 30164 | `2026-07-15T06:32:39.99074Z` | `postgres.exe` | true |
| 16684 | 2816 | `2026-07-15T06:32:40.0319468Z` | `cmd.exe` | true |
| 34496 | 16684 | `2026-07-15T06:32:40.0501766Z` | `postgres.exe` | true |
| 29320 | 2816 | `2026-07-15T06:32:40.8940192Z` | `cmd.exe` | true |
| 33140 | 29320 | `2026-07-15T06:32:40.9137295Z` | `postgres.exe` | true |
| 31708 | 35420 | `2026-07-15T06:32:47.2765651Z` | `pg_ctl.exe` | true |
| 16784 | 31708 | `2026-07-15T06:32:47.2901868Z` | `cmd.exe` | true |
| 33576 | 16784 | `2026-07-15T06:32:47.3112088Z` | `postgres.exe` | true |
| 35956 | 31708 | `2026-07-15T06:32:47.3320713Z` | `cmd.exe` | true |
| 18536 | 35956 | `2026-07-15T06:32:47.3513454Z` | `postgres.exe` | true |
| 17264 | 18536 | `2026-07-15T06:32:47.4754513Z` | `cmd.exe` | true |
| 23176 | 18536 | `2026-07-15T06:32:47.5505549Z` | `postgres.exe` | true |
| 26060 | 18536 | `2026-07-15T06:32:47.5823759Z` | `postgres.exe` | true |
| 30828 | 18536 | `2026-07-15T06:32:47.5914421Z` | `postgres.exe` | true |
| 17840 | 18536 | `2026-07-15T06:32:47.6012583Z` | `postgres.exe` | true |
| 16460 | 18536 | `2026-07-15T06:32:47.6376244Z` | `postgres.exe` | true |
| 13828 | 18536 | `2026-07-15T06:32:47.6460729Z` | `postgres.exe` | true |
| 27916 | 18536 | `2026-07-15T06:32:47.655402Z` | `postgres.exe` | true |
| 7180 | 35420 | `2026-07-15T06:32:49.8117324Z` | `psql.exe` | true |
| 36480 | 35420 | `2026-07-15T06:32:49.8899321Z` | `psql.exe` | true |
| 34148 | 35420 | `2026-07-15T06:32:49.9715483Z` | `psql.exe` | true |
| 10132 | 35420 | `2026-07-15T06:32:50.0668923Z` | `psql.exe` | true |
| 33384 | 35420 | `2026-07-15T06:32:50.1279312Z` | `psql.exe` | true |
| 35760 | 35420 | `2026-07-15T06:32:50.2191808Z` | `psql.exe` | true |
| 8616 | 35420 | `2026-07-15T06:32:50.4191763Z` | `HowardLab.EbayCrm.AppHost.Fixture.exe` | true |
| 35504 | 35420 | `2026-07-15T06:32:50.7277307Z` | `HowardLab.EbayCrm.AppHost.Fixture.exe` | true |

An earlier negative run exposed a stale historical parent PID belonging to a
long-lived `git.exe`, leading to the creation-time ancestry fence now tested.

## Manual cross-session ownership gate

Not performed. Safe inspection found one genuine interactive desktop:
`explorer.exe` PID 3732, session ID 3, user `DESKTOP-0KKR3CV\sdokd`. Numerous
type-2 logon records were created by tests, but they are not two independently
interactive Windows sessions and were not treated as such. No second genuine
same-user interactive desktop was available, so the required two-session check
cannot honestly be claimed.

## Cleanup and deferred work

After the destructive command, the exact-name audit found zero `postgres`,
`pg_ctl`, `initdb`, `psql`, AppHost, or Fixture processes and zero canonical
`ebaycrm-task10-*`, `ebaycrm-task9-*`, or `ebaycrm-pg-*` disposable roots.
Unrelated global Node, Redis, Git, and other user processes were neither killed
nor used as product evidence.

Before adoption, add safe real-timing injection for server/worker start and stop
and prove their no-duplicate reconciliation. Also wire the bounded/redacting
production diagnostic sink and scan its actual JSONL/log/ZIP output, then repeat
the same-profile ownership test from two genuine interactive Windows sessions
for the same user. Phase 1A does not decide the later Redis/PostgreSQL product
policy.

REVISE_APPHOST_FOUNDATION

---

# Phase 1B Windows AppHost hardening evidence addendum

Date: 2026-07-15 (America/Los_Angeles)

Branch: `codex/phase-1b-apphost-hardening`

Phase 1B base commit: `2507d8c92f3d2771ee803fb18feb757396a740c0`.
Tested implementation commit: `3057bb076facfe40a74432615f39fa183674fe1c`.
Tested implementation tree: `b3f45b6c251008d73db73a9e3d08956a6e3207f0`.
The later documentation-only evidence commit does not change the tested
implementation. Scope remains the Windows AppHost foundation. No Redis-free
Twenty boot, full Twenty server, eBay UI, tray, installer, updater, backup, or
local-model runtime was launched or claimed.

## Environment and clean build

- Microsoft Windows 11 Home 10.0.26200, build 26200, 64-bit.
- .NET SDK 10.0.302, MSBuild 18.6.11, host/runtime 10.0.10, `win-x64`.
- Pinned PostgreSQL `postgres (PostgreSQL) 16.14` from
  `C:\Users\sdokd\Downloads\eBayCRM\.worktrees\phase-1a-apphost\.tools\postgresql\16\bin`.
- `EBAYCRM_POSTGRES_BIN` was set to that exact directory for every PostgreSQL
  gate. `EBAYCRM_RELEASE_ACCEPTANCE=1` made S4U policy unavailability a failure,
  not a skip.

The final post-fix sequence ran:

```powershell
dotnet restore desktop\windows\EbayCrm.Desktop.sln --locked-mode
dotnet build desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-restore --nologo
Remove-Item -Recurse -Force desktop\windows\artifacts\win-x64 -ErrorAction SilentlyContinue
dotnet publish desktop\windows\src\HowardLab.EbayCrm.AppHost\HowardLab.EbayCrm.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true --output desktop\windows\artifacts\win-x64 --no-restore --nologo -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false
```

Locked restore and publish exited zero. The Release build reported 0 warnings
and 0 errors. The clean self-contained publish contained 204 files totaling
81,270,270 bytes, including AppHost, Fixture, `hostfxr.dll`, `coreclr.dll`, and
`System.Private.CoreLib.dll`.

## Focused Phase 1B gates

The exact aggregate gate was:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~TimeoutReconciliationAcceptanceTests|FullyQualifiedName~DiagnosticCanaryAcceptanceTests|FullyQualifiedName~CrossSessionOwnershipAcceptanceTests" --logger "console;verbosity=detailed" --nologo
```

It passed 37 of 37 tests with 0 failures and 0 skips. Because that literal
filter does not select the late-stop tests in `AppHostShutdownTests`, the four
role boundaries were also run with this required supplement:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --no-build --filter "FullyQualifiedName~TimeoutReconciliationAcceptanceTests|FullyQualifiedName~AppHostShutdownTests" --logger "console;verbosity=detailed" --nologo
```

The supplement passed 18 of 18 tests with 0 failures and 0 skips. Its detailed
output included late start and late stop for both Server and Worker. A separate
six-test output-only evidence replay passed 6 of 6; its temporary serialization
statements were reverted and are not part of the tested or committed tree.

### Retained role identity evidence

This detailed table was captured by the pre-review evidence replay at
`f432ece2`. The final `3057bb07` gate reran the strengthened assertions but did
not emit new process identifiers. Each test asserted one role launch, one
launch for the exact generation, one live exact Fixture identity before
release, and no duplicate.

| Boundary | Role | Generation | Operation ID | PID | UTC creation time |
|---|---|---:|---|---:|---|
| `StartIdentityRetained` | Server | 1 | `75503489-a731-4d4d-ad23-3948fdbdfa29` | 38520 | `2026-07-15T11:57:23.2200753Z` |
| `StartIdentityRetained` | Worker | 1 | `1cef24fd-f922-4d4a-ac04-ec47c99c5e6f` | 33392 | `2026-07-15T11:57:35.6132792Z` |
| `StopAccepted` | Server | 1 | `de08626f-b1a2-4a71-90d0-09a729a509ed` | 6120 | `2026-07-15T11:57:35.8019767Z` |
| `StopAccepted` | Worker | 1 | `742ab65d-7d69-49f2-98e1-be7ff10ed530` | 13224 | `2026-07-15T11:57:23.1251886Z` |

Both start cases reconciled to `Ready` with the same generation and identity.
Both stop cases reconciled to `Stopped`, left zero live exact Fixture identities,
released profile ownership exactly once, and left the retained database identity
signaled.

### Production diagnostic evidence

`ProductionCompositionRedactsGeneratedSecretsBoundsSegmentsAndLoserTouchesNothing`
recorded 4 retained JSONL segments totaling 3,406,726 bytes. Six distinct
registered canaries were exercised. The subsequent artifact scan examined 7
artifacts (four JSONL segments, manifest, ZIP, and ZIP entry) and returned 0
findings. The retained text contained `[REDACTED]`, contained no raw canary, and
the losing composition made zero segment-factory calls and did not alter the
owner's segment snapshots.

### Same-user S4U evidence

The pre-review detailed release-mode S4U gate at `f432ece2` recorded:

- User SID `S-1-5-21-3841990347-2960067561-1741789597-1001`.
- Owner PID 20632 in interactive session 3.
- Broker PID 14044 in S4U session 0.
- Contender PID 19800 in the same S4U session 0, created at
  `2026-07-15T11:57:50.8517446Z`.
- Cumulative broker Job process count 1; contender exit code 2 with exact
  `profile-already-owned` stderr.

The final gate at `3057bb07` additionally proved the owner remained alive over
an explicit 1,250 ms observation window; it does not claim that a successful
health-monitor cycle was observed. The contender could not mutate the owned
profile, and the final focused gate reported no S4U skip.

## Full documented partitions

The final post-fix partition commands and observed counts were:

| Partition | Command discriminator | Passed | Failed | Skipped |
|---|---|---:|---:|---:|
| Core | Core test project | 156 | 0 | 0 |
| Windows | Windows test project | 144 | 0 | 0 |
| Non-destructive integration | `Category!=DestructiveContainment` | 146 | 0 | 0 |
| Destructive containment | `Category=DestructiveContainment` with `RunConfiguration.DisableAppDomain=true` | 3 | 0 | 0 |

Total across the non-overlapping partitions: 449 passed, 0 failed, 0 skipped.
The destructive partition's pre/post inventory remained 204 files and
81,270,270 bytes with zero relative-path, length, or SHA-256 differences.

## Independent-review remediation

The final whole-branch review found no Critical issues and initially blocked
adoption on lifecycle, diagnostic-ACL, and S4U-cleanup findings. Commit
`3057bb076facfe40a74432615f39fa183674fe1c` closed those findings with RED/GREEN
coverage:

- caller cancellation and faulted authenticated accepts preserve or dispose the
  exact retained role resource and reconcile to a bounded outcome;
- recovery-stop reconciliation is keyed independently from the mutable global
  operation, reaches a terminal outcome on a second timeout, rejects stale
  cross-role results, and prunes terminal tracking;
- the internal seam receives the actual pending accept/read task, so acceptance
  proves the real operation is in flight;
- diagnostic DACL validation uses `GetKernelObjectSecurity` on the exact opened
  handles, rejects callback ACEs, and remains reparse-safe and fail-closed; and
- S4U setup and teardown preserve primary plus cleanup failures, apply the
  current-user-only ACL at directory creation, preserve pre-existing scheduler
  folders, and remove runner-owned artifacts.

Fresh focused re-reviews approved all three scopes with no remaining Critical
or Important findings before the authoritative gates above were run.

## Destructive cleanup regression found during adoption

The first Task 5 destructive run passed 2 tests and failed
`TwentyPublishedContenders_ProduceOneOwnerAndStableLosers` during profile
deletion because `apphost-0.jsonl` was still non-delete-shareable. The isolated
test reproduced the failure. Diagnostic RED evidence identified winning AppHost
PID 24700, created `2026-07-15T11:38:30.6617056Z`; all 20 retained AppHost
identities had signaled, but external termination/Job/file teardown had not yet
made the diagnostic file exclusive before deletion began.

Commit `f432ece2a41601e48fec36528a80d3b0587d7064` added the same bounded
condition-based exclusive-file wait already used by the cross-session test.
The isolated test then passed 1 of 1, and two complete destructive replays each
passed 3 of 3. The final replay is the count recorded above. The initial failure
is retained here rather than hidden.

## Exact-scope cleanup audit and limitation

The final audit queried eight exact executable paths: the published AppHost and
Fixture, Release Fixture and acceptance broker, and pinned `postgres.exe`,
`pg_ctl.exe`, `initdb.exe`, and `psql.exe`. It found 0 surviving exact
identities. It did not enumerate, inspect, or kill unrelated user processes by
image name.

Task Scheduler's `\HowardLab` folder was absent, so there were 0
`eBayCRM-Acceptance-*` tasks. The post-review audit queried the scoped
`ebaycrm-phase1b-role-executor-*`, `ebaycrm-s4u-*`, `ebaycrm-task*-*`, and
`ebaycrm-pg-*` disposable roots and found 0. The earlier Task 5 audit also found
0 across its sixteen exact disposable-prefix patterns. The final post-review
audit returned zero for processes, tasks, and scoped roots without deleting or
inspecting unrelated processes by image name.

The remaining limitation is deliberate scope: these gates use the attested
Fixture plus real PostgreSQL and do not prove a Redis-free or full Twenty boot.
They support adoption of the hardened .NET AppHost foundation only.

ADOPT_DOTNET_APPHOST_FOUNDATION

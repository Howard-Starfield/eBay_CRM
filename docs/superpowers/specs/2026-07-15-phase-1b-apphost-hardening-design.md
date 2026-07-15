# Phase 1B Windows AppHost Hardening Design

Date: 2026-07-15

Status: Approved for planning after fresh architecture review

Base commit: `2507d8c92f3d2771ee803fb18feb757396a740c0`

Related documents:

- [`2026-07-14-windows-apphost-supervision-spike-design.md`](2026-07-14-windows-apphost-supervision-spike-design.md)
- [`../../architecture/phase-1a-windows-apphost-supervision-report.md`](../../architecture/phase-1a-windows-apphost-supervision-report.md)

## Decision

Retain the .NET 10 AppHost foundation and close the three evidence gaps that
produced `REVISE_APPHOST_FOUNDATION` in Phase 1A:

1. real server and worker late-start/late-stop reconciliation;
2. bounded, redacting diagnostics in the production composition; and
3. same-user, different-Windows-session profile ownership.

The implementation extends the existing lifecycle and diagnostics seams. It
does not introduce a second supervisor, a permanent Windows service, a new
logging framework, or a replacement process model.

The Phase 1B outcome is:

- `ADOPT_DOTNET_APPHOST_FOUNDATION` only when every acceptance gate in this
  document passes;
- `REVISE_APPHOST_FOUNDATION` when the architecture remains viable but any
  gate is incomplete; or
- `REJECT_DOTNET_APPHOST_FOUNDATION` only if deterministic reconciliation,
  safe production diagnostics, or cross-session ownership cannot be made
  reliable without changing the foundation.

## Goals

- Prove that a real server or worker can finish starting or stopping after the
  caller's deadline without creating a duplicate process generation.
- Preserve enough retained identity to reconcile every indeterminate role
  operation.
- Wire the existing bounded JSONL diagnostic sink into the production AppHost
  composition without allowing logging failure to block lifecycle progress.
- Store diagnostic segments under the canonical profile with current-user-only
  access, fixed names, bounded size, and bounded count.
- Prove the existing global named mutex prevents two same-user processes in
  different Windows sessions from owning one canonical profile.
- Produce reproducible evidence and one honest final decision token.

## Non-goals

- Booting the complete Twenty server or worker without Redis.
- Adding the eBay dashboard, tray UI, installer, updater, backup, or local LLM.
- Installing or testing a permanent Windows service.
- Logging eBay messages, buyer data, OAuth material, database passwords, or
  full command lines.
- Replacing the existing typed `IDiagnosticSink` with
  `Microsoft.Extensions.Logging` or an external telemetry service.
- Claiming that S4U is the eventual product launch model. It is an acceptance
  harness mechanism only.

## Fresh-review conclusions

The existing global mutex name is correct. Windows provides a per-session
kernel-object namespace and the `Global\\` prefix is the supported way for a
named mutex to span sessions. The existing current-user security descriptor
must remain in place so another user cannot acquire or manipulate that mutex.

The cross-session harness will use a temporary Task Scheduler task with the
current user's SID and `TASK_LOGON_S4U`. S4U stores no password and launches on
a non-interactive desktop. It does not provide network access or access to
encrypted files. That limitation is useful for this test: the second AppHost
must lose profile ownership before reading DPAPI credentials, opening the
database, running migrations, or launching children.

The harness will use Task Scheduler COM APIs directly. It will not shell out to
`schtasks.exe`, install a service, call `WTSQueryUserToken`, or require
`LocalSystem`/`SE_TCB` privileges.

The existing `JsonLinesDiagnosticSink` already has the right bounded-channel,
redaction, truncation, and rotation model. Phase 1B will wire and harden this
boundary rather than add a parallel logging abstraction.

## Workstream A: role-operation reconciliation

### State and command model

Server and worker operations use the same indeterminate-outcome vocabulary as
PostgreSQL:

```text
Requested
Acknowledged
Completed
TimedOutIndeterminate
ReconciledRunning
ReconciledStopped
Escalated
```

Add role-generic reconciliation commands for server and worker start and stop.
Every command carries role, generation value, and operation ID. Process
generation identity is immutable after launch: role plus generation value
identifies the retained process, while the startup or shutdown operation ID
identifies the action being reconciled. The coordinator currently retargets
its generation record to the shutdown operation; the retained `RoleResource`
does not. Start reconciliation therefore matches the complete launch
generation. Stop reconciliation matches role plus generation value and
separately requires the active shutdown operation ID. A result event uses the
coordinator's current generation record so its operation fence remains valid.
Stale and duplicate results are ignored.

### Late start

Once a child has been atomically assigned to the Job and its retained process
identity exists, cancellation or deadline expiry cannot dispose that identity
and claim that start failed. The executor starts one `AcceptAsync` task using a
role-lifetime token that is independent of the command/caller token. The
command awaits that task through its own deadline. If the command deadline
expires, the authenticated accept continues against the retained process and
pipe; it is never canceled and restarted. The executor records the operation
as indeterminate and returns control to the coordinator. The orchestrator
dispatches that timeout with `CancellationToken.None`, then gives only the
resulting reconciliation command a fresh bounded role-reconciliation token.
It does not run startup rollback while a retained role operation is
indeterminate. Reconciliation then:

1. inspects the retained process handle;
2. awaits the original, single authenticated control-channel accept task;
3. verifies generation-bound readiness; and
4. reports exactly one running or stopped outcome.

No second generation may launch while the prior start is indeterminate. If the
retained child exited, reconciliation disposes its role resources once and
reports stopped; the coordinator may create one replacement generation only
through the existing bounded restart policy after stop is conclusive. If it
authenticated and became ready, reconciliation reports running and startup
continues from that same generation. Recovery paths must use the same
`Start -> RoleStarted -> Wait` sequence as initial startup; they may not queue a
wait command before start completion is known.

### Late stop

After shutdown is accepted, cancellation or deadline expiry preserves the
retained role resource. `RoleResource` stores the immutable launch generation
and the separate shutdown operation ID. Reconciliation inspects the retained
handle and those two fences. A signaled handle reports stopped and performs
idempotent cleanup. A live handle reports running/indeterminate so the existing
shutdown budget may perform its one final escalation.

No late timeout or acknowledgement may stop a newer generation.

### Deterministic acceptance seam

Add internal-only lifecycle test support with exact pause points:

- after atomic Job assignment and retained identity creation while the one
  independent `AcceptAsync` task is genuinely pending; and
- after shutdown acceptance, before process-exit observation is returned.

The seam is injected only through internal composition constructors visible to
the integration-test assembly. It is not selectable through CLI arguments,
environment variables, profile files, or production configuration. Production
uses a no-op implementation.

Acceptance tests publish and run the real AppHost/Fixture composition, delay
the Fixture handshake or exit at each boundary, force the caller/command
deadline while the independent operation remains live, release the boundary,
and assert:

- one retained process generation for the affected role;
- no duplicate process or control identity;
- correct generation and operation fencing;
- eventual `Ready`, `Stopped`, or one stable `Faulted` state; and
- exactly-once resource cleanup.

## Workstream B: production diagnostics

### Storage and access

Production diagnostics live under:

```text
<canonical-profile>\\logs\\apphost-0.jsonl
<canonical-profile>\\logs\\apphost-1.jsonl
<canonical-profile>\\logs\\apphost-2.jsonl
<canonical-profile>\\logs\\apphost-3.jsonl
```

The diagnostic writer remains inactive until `AcquireInstanceAsync` has
successfully acquired both profile-ownership boundaries. A losing contender
must dispose an unopened sink and create, truncate, or modify no log artifact.

After activation, the directory and files must:

- reject reparse points in their resolved path;
- grant access only to the current user SID;
- use fixed application-controlled names;
- truncate a slot before reuse rather than append without bound;
- cap each segment at 1 MiB and retain at most four segments; and
- never derive a path or filename from diagnostic content.

### Data contract

Only typed, allowlisted lifecycle fields and stable reason codes may enter the
sink. A thread-safe secret registry is shared with the sink. The executor
registers the PostgreSQL password immediately after profile identity creation
and every control capability immediately after creation, before either value is
used or any related event can be emitted. Field values are redacted against an
immutable registry snapshot and truncated by the existing sink. Acceptance
scans for the actual generated password and capability nonces in addition to
static test canaries.

The following are prohibited from diagnostic fields and filenames:

- database passwords and connection strings;
- control-channel capabilities and nonces;
- OAuth/API tokens and refresh tokens;
- raw environment blocks or full command lines;
- raw exception text when it could contain user-controlled data;
- eBay messages, buyer identifiers, order details, and knowledge files.

Exceptions are mapped to stable reason codes at the lifecycle boundary. A
bounded developer-only exception type or name may be recorded only when it is
on an explicit allowlist and contains no message or stack trace.

### Failure behavior and lifetime

The sink remains a bounded, non-blocking producer with one background writer.
Full queues, access denial, disk-full simulation, malformed segment state, and
writer failure increment non-secret counters and do not prevent startup,
shutdown, containment, or ownership release.

`AppHostComposition` owns one gated production sink and injects it into the
lifecycle executor and every `WindowsProcessLauncher`. The executor activates
it only after profile acquisition. Disposal happens only after role monitors
and process-output drains have settled, and remains bounded by final host
teardown. No component creates its own uncoordinated production sink.

### Acceptance evidence

Production-composition tests will:

- force segment rotation and prove the 4 MiB total bound;
- emit registered canaries split across buffer and 4,096-byte boundaries;
- scan every JSONL file and any generated support ZIP/manifest artifact;
- simulate an unavailable or unwritable log path;
- abruptly terminate the AppHost and scan the remaining artifacts; and
- prove diagnostics failure does not alter lifecycle outcome.

## Workstream C: same-user cross-session acceptance harness

### Topology

The integration test starts the published AppHost interactively and waits until
it owns a disposable canonical profile. It then registers a one-shot Task
Scheduler task under the current user's SID with `TASK_LOGON_S4U`. The task
launches a small acceptance broker on a non-interactive desktop.

The broker records its SID and Windows session ID and atomically launches the
same published AppHost inside a broker-owned Job Object against the owned
profile. It captures exact exit code/stdout/stderr and the Job's cumulative
process accounting, then writes a nonce-bound result atomically to a
current-user-only local temporary directory. It never receives or stores a
password. Cumulative Job accounting is the race-free child-creation ledger: a
total process count of one proves the contender never created even a transient
database, migration, server, or worker child.

### Required assertions

The test accepts evidence only when:

- owner, broker, and contender have the same user SID;
- the broker/contender session ID differs from the interactive owner's session
  ID;
- the contender exits with code 2 and exactly `profile-already-owned`;
- the broker Job's cumulative total process count is exactly one, proving no
  PostgreSQL, migration, server, or worker child was launched by the contender;
- the losing contender creates, truncates, or modifies no diagnostic file;
- the original owner remains healthy and retains the profile; and
- the task, task folder, broker result, and disposable profile are removed in
  `finally` blocks.

The result file includes an unpredictable nonce supplied through a
current-user-only file, exact process identities, timestamps, and session IDs.
The harness rejects stale or mismatched results. It does not use the network or
DPAPI-protected profile secrets.

### Environment limitations

If the Windows edition or policy disables Task Scheduler S4U, the test reports
an explicit environmental skip reason during ordinary development runs. The
release acceptance command must run on a supported Windows 11 environment and
must pass; a skip cannot produce `ADOPT_DOTNET_APPHOST_FOUNDATION`.

## Error handling

| Failure | Required result |
|---|---|
| Role start deadline after retained identity exists | Preserve the original accept task, use a fresh bounded reconciliation token, and block duplicate launch. |
| Role stop deadline after shutdown accepted | Preserve identity; reconcile before retry or escalation. |
| Stale role result | Ignore by role/generation/operation fencing. |
| Diagnostic path rejected or unwritable | Continue lifecycle; increment bounded failure counter. |
| Diagnostic queue full | Drop event, increment counter, never block coordinator. |
| Canary registration or redaction invariant fails | Fail acceptance; never publish adoption token. |
| S4U task cannot register in developer run | Explicit environment skip with cleanup. |
| S4U acceptance skipped in release run | `REVISE_APPHOST_FOUNDATION`. |
| Contender touches diagnostics or reaches profile/database preparation | Fail closed; `REVISE_APPHOST_FOUNDATION`. |
| Broker Job cumulative process count exceeds one | Fail closed; `REVISE_APPHOST_FOUNDATION`. |
| Harness cleanup fails | Fail test and record the exact non-secret cleanup reason. |

## Necessary test strategy

Fast feedback remains proportional to risk:

1. core coordinator unit tests for role reconciliation and stale-event fencing;
2. Windows unit tests for ACL-safe rotating segments and Task Scheduler data
   contracts;
3. real Fixture integration tests at the two deterministic timeout boundaries;
4. published production-composition diagnostic and cross-session acceptance;
5. one complete AppHost solution test run plus the existing destructive
   containment partition before final evidence.

No full Twenty frontend/server suite is added to this phase. This keeps the
work focused while retaining tests for the exact OS, lifecycle, and security
boundaries being changed.

## Acceptance gates

Phase 1B passes only when:

1. real late server start reconciles without a duplicate generation;
2. real late worker start reconciles without a duplicate generation;
3. real late server stop reconciles or escalates once without touching a newer
   generation;
4. real late worker stop reconciles or escalates once without touching a newer
   generation;
5. production composition activates diagnostics only after ownership and emits
   bounded current-user-only JSONL segments;
6. registered canaries do not appear in JSONL, support ZIPs, manifests,
   filenames, stdout, or stderr;
7. diagnostic failure cannot block or change lifecycle cleanup;
8. a same-SID contender in a different Windows session loses ownership before
   touching diagnostics or starting any database or role child, proven by a
   broker Job cumulative process count of one;
9. all temporary scheduled-task and acceptance artifacts are removed; and
10. existing Phase 1A containment, PostgreSQL, protocol, migration, and restart
    tests remain green.

## Evidence update

Update `docs/architecture/phase-1a-windows-apphost-supervision-report.md` with a
Phase 1B addendum containing:

- branch, base commit, Windows build, .NET version, and test commands;
- late-start/late-stop process identities and no-duplicate evidence;
- diagnostic segment counts, byte bounds, canary scan totals, and failure-mode
  results;
- owner and contender SID/session evidence;
- cleanup audit; and
- exactly one final decision token.

The original Phase 1A observations remain intact. The addendum explains which
three gaps were closed and does not claim Redis-free Twenty boot.

## Official references

- Windows kernel object namespaces:
  https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
- Task Scheduler logon types:
  https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type
- `WTSQueryUserToken` privilege boundary:
  https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken
- .NET data redaction:
  https://learn.microsoft.com/en-us/dotnet/core/extensions/data-redaction
- Compile-time logging source generation and classification:
  https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/source-generation

## Approved decisions summary

- Keep .NET 10, PostgreSQL, the existing AppHost, Job Object, and protocol.
- Extend the existing lifecycle coordinator with role-generic reconciliation.
- Use internal-only deterministic pause points for real-timing acceptance.
- Wire and harden the existing typed JSONL sink; do not add a second logging
  framework.
- Keep diagnostics local, current-user-only, redacted, fixed-name, and bounded
  to four 1 MiB segments.
- Prove cross-session ownership with a temporary same-user S4U scheduled task.
- Do not install a service or use privileged token APIs.
- Keep Phase 1B limited to the three adoption blockers.

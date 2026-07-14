# Phase 1A Windows AppHost Supervision Spike Design

Date: 2026-07-14

Status: Approved; implementation plan complete

Base commit: `c5633340`

Related specifications:

- [`2026-07-13-ebay-crm-master-prd-design.md`](2026-07-13-ebay-crm-master-prd-design.md)
- [`2026-07-13-twenty-runtime-modes-design.md`](2026-07-13-twenty-runtime-modes-design.md)
- [`2026-07-13-phase-0b-logical-queue-ledger-design.md`](2026-07-13-phase-0b-logical-queue-ledger-design.md)

This specification refines the earlier runtime-mode design in two places:
server failure pauses or stops the dependent worker before server recovery,
and Phase 1A proves only the narrower host-generated secret-safety guarantee
defined below. It does not weaken the master PRD's final-product security goal.

## Decision being tested

Phase 1A tests whether a .NET 10 Windows AppHost can safely own the local
runtime lifecycle required by the eBay CRM desktop product:

```text
.NET 10 AppHost
|- app-owned PostgreSQL
|- server fixture
`- worker fixture
```

The AppHost must provide deterministic startup, cooperative shutdown, crash
containment, process-generation fencing, bounded recovery, and diagnostics
without requiring Docker or manual server setup.

The Phase 1A outcome is:

- `ADOPT_DOTNET_APPHOST_FOUNDATION` when every acceptance gate in this
  document passes; or
- `REVISE_APPHOST_FOUNDATION` when the architecture remains viable but a
  required invariant fails; or
- `REJECT_DOTNET_APPHOST_FOUNDATION` when atomic containment, PostgreSQL
  ownership, or deterministic reconciliation cannot be achieved.

Passing Phase 1A does not claim that the complete Twenty server can boot
without Redis. Current Redis-free work remains a separate backend project.

## Goals

- Establish .NET 10 as the authoritative Windows runtime host.
- Atomically place supervised child processes in a Windows Job Object.
- Prove that AppHost termination cannot leave supervised descendants running.
- Initialize, start, identify, monitor, stop, and recover a real PostgreSQL
  cluster owned by the application.
- Prove a versioned cooperative control protocol with fixture server and
  worker processes.
- Serialize lifecycle transitions and fence stale events by process
  generation.
- Treat timeouts as indeterminate until the runtime state is reconciled.
- Bound log memory, disk use, restart attempts, and total shutdown time.
- Demonstrate that AppHost-generated standard diagnostics do not expose
  fixture secrets.
- Produce focused evidence suitable for implementation planning.

## Non-goals

- Booting the full Twenty server or worker without Redis.
- Integrating the Phase 0B queue overlay into the complete application.
- Building the tray, WebView2 window, installer, updater, backup, restore, or
  local-model manager.
- Implementing production DPAPI credential persistence.
- Running the full Twenty frontend or server test suites.
- Selecting production restart-budget values or every production timeout.
- Defending against malicious code already running as the same Windows user,
  an administrator, a debugger, process injection, or memory inspection.
- Certifying the complete desktop application for release.

## Authoritative host decision

The supervisor uses .NET 10 LTS.

- Platform-neutral lifecycle abstractions target `net10.0`.
- Windows process, Job Object, named-pipe, and single-instance code targets
  `net10.0-windows`.
- The spike publishes as an ordinary self-contained folder.
- NativeAOT, trimming, and single-file publishing are deferred.
- Production code never invokes PowerShell, `cmd.exe`, `npm`, or `npx`.
- Tauri and Electron are not added. The same AppHost will later own the tray
  and WebView2 window so there is only one desktop-lifecycle authority.

Phase 1A may expose a console harness for test visibility. That harness is not
a second supervisor and will be removed or hidden when the tray UI is added.

## Runtime scope and topology

The spike uses fixture processes for the server and worker control protocol and
a real bundled-compatible PostgreSQL distribution for database lifecycle
evidence.

```text
AppHost process
|
|- single lifecycle coordinator
|- one unnamed Windows Job Object
|- per-generation named-pipe servers
|- bounded diagnostic sinks
|
|- initdb.exe / pg_ctl.exe / postgres.exe
|  `- PostgreSQL backend descendants
|- server fixture
|  `- optional immediate grandchild fixture
`- worker fixture
   `- optional immediate grandchild fixture
```

The production data-profile root is supplied as an explicit absolute path.
The default remains `%LOCALAPPDATA%\HowardLab\eBayCRM`. Tests use disposable
profile roots and never touch the seller's real profile.

## Component boundaries

The lifecycle core depends on interfaces rather than directly depending on
Windows APIs or PostgreSQL commands:

- `IProcessLauncher` creates a process from an immutable launch specification.
- `ISupervisedProcess` exposes retained identity and liveness for one process
  generation.
- `IProcessGroup` owns the Job Object lifetime and membership checks.
- `IPostgreSqlRuntime` owns cluster initialization, verified identity,
  readiness, shutdown, and reconciliation.
- `IOneShotCommand` runs migrations and other bounded commands.
- `IHealthProbe` reports generation-bound readiness evidence.
- `IControlChannel` implements the versioned cooperative protocol.
- `IInstanceLock` owns the user and data-profile single-instance boundary.
- `IRestartPolicy` makes restart decisions without performing transitions.
- `IDiagnosticSink` accepts allowlisted lifecycle records without blocking the
  coordinator.
- `IClock` makes deadlines and restart windows deterministic in tests.

Implementations may change without changing the coordinator contract. The
Windows launcher and PostgreSQL runtime are integration-tested independently
from the state machine.

## Lifecycle coordinator and state model

Exactly one serialized coordinator may mutate runtime lifecycle state.
Process exits, pipe disconnects, health failures, timeouts, Job notifications,
operator shutdown, and migration completion are converted to events and
submitted to that coordinator.

```text
Stopped
  -> AcquiringInstance
  -> ValidatingPayload
  -> PreparingRuntime
  -> StartingDatabase
  -> WaitingForDatabase
  -> Migrating
  -> StartingServer
  -> WaitingForServer
  -> StartingWorker
  -> WaitingForWorker
  -> Ready
```

Any active state may enter `Stopping` or `Faulted`. A PostgreSQL start or stop
deadline may enter `ReconcilingDatabaseStart` or
`ReconcilingDatabaseStop` before reaching a terminal state.

Every process instance contains:

```text
role
generation
retained process handle
PID
process creation time
verified executable path
pipe identity
startup operation ID
```

Every delayed probe, pipe message, timeout, and exit event carries the role,
generation, and operation ID it belongs to. Events from an older generation
are discarded. Repeated failure signals for the same generation coalesce into
one transition.

The coordinator guarantees:

- one transition at a time;
- no restart after shutdown begins;
- no restart from a stale event;
- partial-start rollback only for resources owned by that startup operation;
- requested clean exits do not consume restart budget;
- App-tier failures do not restart a healthy PostgreSQL instance; and
- budget exhaustion enters one visible `Faulted` state rather than looping.

Production restart numbers are not selected by this spike. Acceptance tests
inject a deterministic policy that allows two retries and faults on the next
failure, proving that the policy boundary is enforced.

## Atomic process launch and containment

The Windows launcher uses `CreateProcessW` with `STARTUPINFOEX` and
`PROC_THREAD_ATTRIBUTE_JOB_LIST` so Job membership is established during
process creation. It does not use a start-then-assign sequence.

The launch contract requires:

- an absolute, non-null application path;
- an explicit working directory;
- a correctly quoted argument vector;
- `CREATE_UNICODE_ENVIRONMENT` and a child-specific environment allowlist;
- `EXTENDED_STARTUPINFO_PRESENT`;
- an explicit `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`;
- valid standard handles when output is redirected;
- no shell execution;
- no breakaway flags; and
- continuous asynchronous stdout and stderr draining from process creation.

The AppHost owns exactly one unnamed, non-inheritable Job handle for the
supervised tree. The Job uses `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and does not
enable `BREAKAWAY_OK` or `SILENT_BREAKAWAY_OK`. The Job handle is never placed
in a child's inherited-handle list or duplicated into a supervised process.

Retained process handles are the authoritative source for exit detection. Job
notifications may supplement diagnostics but are not the sole source of
truth.

Normal transient child failures must not dispose the Job lifetime object.
Only final AppHost teardown closes the controlling Job handle. External
AppHost termination therefore closes that handle and terminates the supervised
tree, including immediate grandchildren and PostgreSQL descendants.

## Cooperative control protocol

Job containment is the crash boundary. Cooperative control is a separate,
versioned protocol used for clean operation.

Each server and worker generation receives an unpredictable pipe name and
single-use capability nonce through its allowlisted environment. The pipe
server is created before the child starts and requires:

- an explicit access-control list for the expected user/logon boundary;
- `FILE_FLAG_FIRST_PIPE_INSTANCE`;
- `PIPE_REJECT_REMOTE_CLIENTS`;
- one expected client;
- bounded frame sizes and bounded message counts;
- protocol-version negotiation;
- a live retained process handle;
- matching PID, process creation time, generation, and Job membership; and
- a `Hello` message containing the per-generation capability nonce.

Any identity mismatch destroys the pipe instance and faults that process
generation. PID alone is never accepted as identity.

Messages include a protocol version, operation ID, role, generation, message
type, and bounded payload. Drain and shutdown commands are idempotent. Late
acknowledgements for an earlier operation or generation are ignored.

The worker fixture implements:

```text
DrainAccepted
NoNewWorkAcquisition
ActiveWorkRemaining(count)
Drained
ShutdownAccepted
Stopped
```

The server fixture implements `ShutdownAccepted` and `Stopped`. Business queue
lease semantics are outside Phase 1A, but duplicate, out-of-order, truncated,
oversized, disconnected, and late protocol messages are in scope.

This protocol prevents accidental cross-process connections and access from
other user/session boundaries. It does not claim protection from malicious
same-user code with memory inspection or process-injection capability.

## PostgreSQL ownership and identity

`pg_ctl.exe` is a lifecycle command, not the PostgreSQL process being
supervised. It may exit while `postgres.exe` continues. The AppHost therefore
creates and retains a separate identity for the actual postmaster:

```text
PostgresInstanceIdentity
|- generation
|- canonical data-directory path
|- postmaster PID
|- retained postmaster process handle
|- postmaster creation time
|- verified bundled postgres.exe path
|- verified Job membership
|- configured loopback port
`- application cluster identity
```

After `pg_ctl start`, the runtime reads `postmaster.pid` only from the expected
data directory, opens a retained process handle, verifies the image path and
creation time, verifies Job membership, and associates the result with the
current generation. No stop, restart, or force-termination action operates
from a PID alone.

Initial readiness requires:

- the retained postmaster handle is alive;
- the verified process remains in the AppHost Job;
- an authenticated SQL connection succeeds with the expected local account;
- `SELECT 1` succeeds; and
- PostgreSQL reports the expected canonical data directory.

After runtime migrations, readiness also verifies the application cluster
identity and expected runtime schema version in `desktop_runtime`.
`pg_isready` may be used as an early connectivity hint but is not the final
readiness gate.

On first cluster initialization, the AppHost generates a random application
cluster ID and atomically stores it in
`runtime\cluster-identity.json` under the canonical data profile. The
controlled runtime migration stores the same value in a single
`desktop_runtime` control row. After migration, both values must match. A
missing or mismatched value is a wrong-cluster or repair-required failure, not
an instruction to overwrite either copy.

Stale, corrupt, malformed, foreign, and unrelated-process PID files are
explicit failure states. The AppHost never deletes or replaces one until it
has reconciled whether an owned postmaster is still running.

## Timeout reconciliation

A deadline does not prove that an operation failed. PostgreSQL may finish a
start or stop after `pg_ctl` reports a timeout, and a fixture child may stop
just after a pipe deadline.

Long-running operations use these outcomes:

```text
Requested
Acknowledged
Completed
TimedOutIndeterminate
ReconciledRunning
ReconciledStopped
Escalated
```

After a timeout, the coordinator reconciles retained process handles, pipe
state, operation IDs, and generation. PostgreSQL reconciliation also checks
the expected PID file, verified postmaster handle, authenticated SQL state,
and application cluster identity when available.

No new generation starts until the prior generation is conclusively stopped.
No process is force-terminated from a late timeout after a newer generation
has started.

PostgreSQL gets a normal 60-second startup deadline. When that expires, the
runtime enters `ReconcilingDatabaseStart` for up to five minutes so legitimate
crash recovery can finish. The AppHost never starts a second postmaster during
this interval. Failure to reconcile within the outer bound enters `Faulted`
and requires a visible repair or retry action.

## Startup and rollback

Startup order is:

1. Acquire the user/data-profile single-instance lock.
2. Validate all payload paths, versions, checksums, and required files.
3. Prepare disposable runtime directories and bounded log sinks.
4. Initialize PostgreSQL only when the expected data directory is genuinely
   uninitialized.
5. Start and identify the postmaster.
6. Establish authenticated database readiness.
7. Inspect migration state and run the controlled migration command.
8. Verify schema and application cluster identity.
9. Start and authenticate the server fixture.
10. Verify generation-bound server readiness.
11. Start and authenticate the worker fixture.
12. Verify generation-bound worker readiness and enter `Ready`.

Fixture readiness requires the retained process handle to remain alive, a
successful authenticated pipe `Hello`, the child's actual loopback endpoint
reported over that pipe, and a health response containing the expected
protocol version, fixture build identity, generation, and non-secret
generation nonce. A response from a reused port or prior generation is
rejected.

Failure rolls back only components started by the current startup operation,
in reverse dependency order. Migration failure and interrupted/unknown
migration outcome do not auto-retry in the same run.

## Shutdown

The explicit shutdown sequence is:

1. Reject new startup and restart transitions.
2. Send `Drain` to the worker and wait for `Drained`.
3. Send `Shutdown` to the worker and confirm its retained handle is signaled.
4. Send `Shutdown` to the server and confirm its retained handle is signaled.
5. Run `pg_ctl stop -m fast` against the verified data directory.
6. Reconcile the postmaster handle and PID file.
7. Close remaining control channels and release the instance lock.
8. Close the Job handle during final AppHost teardown.

One 45-second total deadline bounds the entire sequence:

- worker drain and stop allocation: 15 seconds;
- server stop allocation: 10 seconds;
- PostgreSQL fast-stop allocation: 20 seconds.

Unused time from an earlier stage remains available to later stages, but the
total never exceeds 45 seconds. Each timeout first enters reconciliation.
Forced Job termination is the final escalation only after the total deadline
and generation-bound reconciliation. An externally killed AppHost relies on
Job closure immediately and performs PostgreSQL crash recovery on next launch.

## Failure handling

| Failure | Required behavior |
|---|---|
| Validation failure | Start nothing; enter `Faulted` with an actionable allowlisted diagnostic. |
| PostgreSQL start timeout | Enter indeterminate reconciliation; never launch a duplicate postmaster. |
| PostgreSQL unexpected exit | Stop worker, then server; reconcile database identity; apply the injected database restart policy. |
| Server fixture failure | Stop or pause the worker, restart only the server generation, verify readiness, then restart the worker. |
| Worker fixture failure | Restart only the worker within the injected budget. |
| Simultaneous failure signals | Coalesce by role and generation into one coordinator transition. |
| Pipe identity or protocol failure | Reject the connection and fault only the affected generation. |
| Migration known failure | Stop the database cleanly and enter `Faulted`; no same-run retry. |
| Migration interrupted/unknown | Preserve the durable marker, inspect schema on next launch, and require repair when outcome cannot be classified. |
| Output sink slow or disk full | Preserve lifecycle progress; drop or truncate bounded child output and emit a non-secret counter/diagnostic. |
| Restart budget exhausted | Enter one stable `Faulted` state; do not loop. |
| Shutdown during startup or backoff | Cancel pending work through the coordinator and unwind owned resources once. |

App-tier failures never restart a healthy PostgreSQL instance. Database
failure stops the dependent app tier before database recovery.

## Migration recovery shell

Phase 1A proves migration lifecycle safety with a controlled test migration;
it does not run the complete Twenty migration set.

Before invoking the migration command, the AppHost atomically writes
`runtime\migration-attempt.json` using a temporary file and atomic replace.
The durable record
contains the operation ID, application version, expected starting schema
version, target schema version, and state:

```text
Running
Succeeded
Failed
InterruptedOrUnknown
```

The migration command also takes a PostgreSQL single-migrator advisory lock.
After the command exits, the AppHost verifies the actual schema version before
recording success. A success exit code without the target schema is a failure.

If the AppHost or PostgreSQL dies while the record is `Running`, the next
launch marks it `InterruptedOrUnknown`, inspects the actual schema, and either:

- recognizes the target schema and records success;
- recognizes the unchanged starting schema and permits an explicit retry; or
- enters repair-required state.

The AppHost does not guess whether arbitrary partially applied migration code
is safe to repeat.

## Single-instance and data-profile ownership

The invariant is one AppHost per Windows user and canonical data-profile path
across interactive sessions.

- The lock name contains a stable hash of the canonical data-profile path.
- The wait-handle restriction is user-scoped but not session-scoped.
- A second data-profile ownership lock guards alternative entry points. It is
  `runtime\profile.lock`, opened for the AppHost lifetime with exclusive file
  sharing. Its diagnostic content contains only the AppHost PID, creation
  time, and profile hash; the operating-system file handle, not that content,
  is the ownership authority.
- A secondary launch in another session reports that the profile is already
  active; cross-session UI activation is not required.
- Different users and different canonical profile paths have independent
  locks.

PostgreSQL's own data-directory lock is defense in depth, not the AppHost's
single-instance mechanism.

## Secrets and diagnostic boundary

Phase 1A uses fixture secrets and does not persist production credentials.
Secrets may be present in a child's explicit environment because some child
programs require them. They are never placed in command-line arguments,
manifests, instance-lock names, lifecycle event fields, or filenames.

The defensible guarantee is:

> AppHost-generated logs, lifecycle events, exceptions, and standard
> diagnostic bundles are constructed from allowlisted non-secret fields and
> never intentionally serialize child environment values or secret-bearing
> configuration. Child-originated output and crash dumps are potentially
> sensitive.

Required controls are:

- `SecretValue` string conversion always returns a redacted value;
- launch specifications and environment dictionaries are never generically
  serialized;
- diagnostics are assembled from allowlists rather than serialized and then
  redacted;
- unmanaged environment buffers are zeroed after process creation;
- stdout and stderr are asynchronously drained into bounded queues;
- exact registered fixture secret values are redacted from standard child-log
  output;
- raw child output and crash dumps are excluded from standard diagnostic
  bundles; and
- every generated artifact is scanned for fixture canary values in tests.

The spike does not claim that transformed child self-disclosure, memory dumps,
same-user inspection, or administrator access can be prevented.

## Output and resource bounds

Child output must not block process progress or exhaust memory or disk.

- stdout and stderr have independent asynchronous readers;
- line/frame sizes are bounded;
- in-memory queues are bounded;
- log files rotate by size and count;
- binary or invalidly encoded output is safely escaped or discarded;
- log-sink failure never blocks lifecycle transitions; and
- standard exceptions contain bounded redacted tails, never entire streams.

The exact production rotation sizes are deferred. Phase 1A tests inject small
limits so overflow, truncation, counter emission, and non-blocking behavior are
deterministic.

## Testing strategy

### Fast unit suite

Run on every Phase 1A change:

- state transitions and reverse-order rollback;
- generation and operation-ID fencing;
- coalescing simultaneous failure signals;
- shutdown during startup and restart backoff;
- injected restart-budget exhaustion and reset behavior;
- timeout state transitions and no duplicate startup;
- migration-state classification;
- secret-safe diagnostic serialization; and
- Windows argument and environment-block construction.

### Windows fixture integration suite

Run on every Phase 1A pull request:

- atomic Job assignment with a fixture that creates an immediate grandchild;
- normal shutdown, unhandled exception, `Environment.FailFast`, Task Manager
  equivalent termination, and external `TerminateProcess` cleanup;
- a negative test that deliberately leaks a duplicate Job handle and proves
  the containment check detects survivors;
- explicit and silent breakaway attempts;
- exact inherited-handle and environment-name allowlists;
- paths containing spaces and Unicode;
- child exit after `CreateProcessW` succeeds but before `Hello`;
- same-user pipe impostor, stale-generation connection, wrong nonce, protocol
  mismatch, oversized/truncated frame, duplicate operation, and disconnect;
- output flooding, invalid encoding, slow sink, and disk-full simulation; and
- multiple simultaneous launches against one test profile.

### Real PostgreSQL integration suite

Required before Phase 1A merges:

- initialize a disposable cluster;
- clean start and fast stop;
- identify and retain the real postmaster independently of `pg_ctl`;
- verify Job membership for PostgreSQL descendants;
- authenticated SQL readiness and expected data directory;
- AppHost termination followed by successful PostgreSQL crash recovery;
- start timeout followed by late successful start reconciliation;
- stop timeout followed by late successful stop reconciliation;
- delayed readiness beyond an injected short test deadline while a real
  postmaster remains alive, proving reconciliation without a duplicate start;
- occupied port and wrong cluster responding on the configured port;
- stale, corrupt, malformed, and unrelated-process `postmaster.pid` cases;
- read-only data directory and unavailable log path;
- PostgreSQL exit while fixtures are ready; and
- shutdown requested while startup or recovery is in progress.

Timeout-path tests inject short deadlines or a fake clock. They do not sleep
for the production 60-second and five-minute bounds.

### Manual release check

Before the AppHost foundation is used in a distributable build, run two
interactive Windows sessions for the same user and prove that only one can own
the same canonical data profile. This check may be manual in Phase 1A because
multi-session automation is environment-specific.

### Explicitly excluded test suites

The full Twenty frontend suite, full Twenty server suite, Redis-free boot,
installer testing, updater testing, backup/restore, and long soak tests do not
gate this spike.

## Acceptance gates

Phase 1A passes only when all of these are demonstrated:

1. No supervised descendant survives external AppHost termination.
2. The negative handle-leak test reliably detects a surviving descendant.
3. Every accepted control connection is tied to one live process generation,
   not merely a PID.
4. The real PostgreSQL postmaster is identified and monitored independently of
   `pg_ctl`.
5. A timeout never causes duplicate PostgreSQL, server, or worker startup.
6. Concurrent failure signals produce one serialized recovery transition.
7. PostgreSQL recovers successfully after abrupt AppHost termination.
8. Multiple AppHost launches cannot own the same canonical data profile.
9. Output flooding cannot deadlock the child or exhaust AppHost memory/disk.
10. AppHost-generated standard artifacts contain none of the registered
    fixture canary secrets.
11. Migration known failure and interrupted/unknown outcome enter the correct
    durable terminal state without same-run retry.
12. Clean shutdown completes within the 45-second total budget, or the
    coordinator performs one generation-bound final escalation and records one
    stable `Faulted` diagnostic without extending that budget.
13. Restart-budget exhaustion enters `Faulted` exactly once and does not loop.
14. Test evidence makes no claim that the complete Twenty server is Redis-free.

## Evidence artifact

Implementation must produce
`docs/architecture/phase-1a-windows-apphost-supervision-report.md` containing:

- pinned .NET, Windows, and PostgreSQL versions;
- exact test commands and results;
- the pass/revise/reject token;
- failed or deferred cases;
- containment and process-identity evidence;
- PostgreSQL crash-recovery evidence;
- canary-scan evidence; and
- the exact follow-on scope before tray/UI integration.

## Follow-on work after adoption

Adoption proves the lifecycle foundation, not the desktop product. Later
specifications and plans must still cover:

- real Twenty server and worker control-channel integration;
- completion of the Redis-free local backend;
- tray and WebView2 UI in the same AppHost;
- Windows sleep, resume, logout, and shutdown integration;
- DPAPI-backed credential storage;
- PostgreSQL upgrade and rollback policy;
- installer, code signing, updater, repair, and uninstall behavior;
- backup and restoration;
- diagnostic consent and support-bundle UX;
- local llama.cpp lifecycle; and
- Twenty source-license and bundled-dependency review.

## Official technical references

- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Windows Job Objects: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
- `UpdateProcThreadAttribute`: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
- `CreateProcessW`: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
- Windows named-pipe security: https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights
- PostgreSQL `pg_ctl`: https://www.postgresql.org/docs/current/app-pg-ctl.html
- PostgreSQL `pg_isready`: https://www.postgresql.org/docs/current/app-pg-isready.html

## Approved decisions summary

- Use .NET 10 as the authoritative Windows AppHost.
- Keep PostgreSQL and preserve Twenty's PostgreSQL schema architecture.
- Keep server and worker as separately supervised processes.
- Use atomic Windows Job Object assignment and a separate cooperative protocol.
- Use fixture server/worker processes and real PostgreSQL in Phase 1A.
- Treat timeouts as indeterminate until reconciled.
- Fence lifecycle events and control messages by process generation.
- Identify the real PostgreSQL postmaster independently of `pg_ctl`.
- Default the eventual product to app-owned Local Desktop mode.
- Keep Redis/Twenty Compatibility mode as a future advanced option.
- Do not claim Redis-free Twenty boot from this spike.

# Phase 1C-B checkpoint handoff

Date: 2026-07-21

## Project goal

HowardLab eBay CRM is a local-first Windows desktop application built around a
reviewed Windows AppHost and a pinned Twenty application payload. The current
Phase 1C-B goal is to boot the real Twenty server and worker through that
AppHost with explicit `RedisCompatibility`, app-owned PostgreSQL, supervised
migrations, real readiness, frontend/workspace CRUD, and a BullMQ worker
canary. It must preserve the Phase 1C-A trust, process-containment, secret, and
cleanup boundaries and must not claim that Twenty is Redis-free.

The complete Phase 1C-B acceptance matrix also requires fresh/repeat installed-
like startup, migration reconciliation and drift rejection, independent
server/worker restart, Redis/PostgreSQL failure evidence, immutable-install
verification, zero retained process/database/profile residue, and exactly one
new Phase 1C-B decision-token family. Phase 2+ product work is out of scope.

## Why this is a checkpoint

The user explicitly requested this reviewed checkpoint be merged and pushed to
`main` before the Phase 1C-B cold closure gate was resolved. Do not treat the
merge as Phase 1C-B completion and do not emit an adoption decision token from
this state.

## Accepted foundation already on main

- App-owned PostgreSQL lifecycle and profile isolation.
- Exact Windows Job completion-port process accounting and bounded shutdown.
- Reviewed production role-launch boundary with `RedisCompatibility` explicit.
- Acceptance-only `controlled-node-probe` and published external AppHost smoke.
- Deterministic trusted Node payload, manifest authenticity, ACL validation,
  environment restoration, and fail-closed `postgres-desktop` incompleteness.
- Phase 1C-A evidence recorded as `ADOPT_PRODUCTION_LAUNCH_BOUNDARY` with Node
  61/61, published smoke 1/1, Core 190/190, Windows 284/284, and destructive
  containment 3/3 at that accepted checkpoint.
- Five fork-CI repairs after the Phase 1C-B branch point are already on `main`
  (`a743ca55` through `77ef8cb7`) and must be preserved during integration.

## Phase 1C-B work completed in this checkpoint

### Design and implementation plan

- Design amendment: `docs/superpowers/specs/2026-07-16-phase-1c-b-compatibility-boot-design-amendment.md`.
- Implementation plan: `docs/superpowers/plans/2026-07-16-phase-1c-b-compatibility-boot.md`.
- Prior reviewed commits on the branch:
  - `5d44e250` design approval.
  - `7fe622ca` implementation-plan approval.
  - `d404d910` trusted production payload manifests.

### Installed-like production payload builder

- Added `ProductionPayloadBuilder`, its PayloadTool CLI, publish/build scripts,
  and extensive Windows tests.
- Pinned Node 24.18.0, Yarn 4.13.0, and MinGit 2.55.0.windows.2 identities.
- Stages tracked source into a dedicated canonical no-reparse tree and uses
  private toolchain/cache/profile roots.
- Uses exact focused development workspaces (`twenty`, `twenty-server`,
  `twenty-front`, `twenty-emails`) before a final production dependency prune.
- Keeps Git, package-manager, environment, path, archive, Authenticode,
  alternate-data-stream, reparse, traversal-budget, and cleanup checks
  fail-closed.
- Added the Twenty server asset copier and tests.
- Added production payload canonicalization and compiled-entrypoint closure
  checks. Generated payloads, caches, `node_modules`, and `.superpowers` remain
  ignored and are not committed.

### Cold-build diagnostic boundary

The cold build repeatedly failed at command 2,
`twenty-server:lingui:extract`, while an exact warm rerun passed. That Nx target
declares `dependsOn: ["^build"]`, so command 2 drives a dependency-build graph.

The checkpoint adds only these command-2 diagnostic arguments:

- `--outputStyle=stream`
- `--verbose`
- `--parallel=1`

Non-zero child output now flows through the existing reason-code mapping as a
bounded diagnostic. The sanitizer incrementally consumes the already-bounded
stdout/stderr snapshots, limits the body to 32 KiB UTF-8 and 200 logical lines,
normalizes Unicode line separators, strips ANSI/C1/control sequences, replaces
the canonical build root, redacts credential-like fields including YAML block
scalars, prevents marker collisions, and never emits an environment block or
reconstructed command line. PayloadTool emits stable begin/end markers and
preserves the exact byte/line caps after framing.

Independent final review reported PASS with zero Critical, Important, or Minor
findings. Final local evidence before this checkpoint merge:

- Diagnostic partition: 13/13 passed, zero skipped.
- Complete Windows test project: 563/563 passed, zero skipped.
- PowerShell relay/default cleanup contracts: passed within that project.
- `git diff --check`: clean except the known lock-file line-ending warning.

## Unresolved cold closure

Three earlier cold attempts failed at production build command 2; the latest
captured pre-diagnostic failure took about 1,029.6 seconds. A freshly published
diagnostic PayloadTool had SHA-256
`CB69113096290E5E1691CFFDD6DDBFBAEC6C6E44E55D9E97F7766A77C6FC3134`.

The single approved diagnostic cycle entered serialized command 2 at 12:20:46
on 2026-07-21 and held exactly one CPU-active Nx worker until the PayloadTool/Nx
tree exited near 12:45:12. It exceeded the prior failure duration, but the
controller's outer 2,400-second wrapper timed out while the supervising
PowerShell was cleaning up. The terminal exit and sanitized diagnostic were
therefore lost. Classify that cycle as **INCONCLUSIVE**, not pass or fail.

The interrupted commit staging tree and failed diagnostic output root were
removed with the builder's validated `Remove-OwnedOutputRoot`; no owned build
process or retained staging remained at handoff. The user then ordered this
checkpoint merged and pushed rather than approving another cold cycle.

## Exact next task

1. Start from the pushed `main`; read this handoff, the design amendment, the
   implementation plan, and the durable progress ledger if it is available.
2. Confirm `main` and the working tree are clean. Do not recreate or reset to
   the retired feature branch.
3. Republish the PayloadTool from current `main` and record its fresh exact
   SHA-256. Reacquire the pinned Node/MinGit archives under ignored artifacts if
   branch-worktree cleanup removed them.
4. Only with user authority for another cold cycle, rerun
   `Build-Phase1CBPayload.ps1 -Offline -ClosureOnly` with an outer wrapper of at
   least 75 minutes. The internal reviewed per-command timeout remains 30
   minutes; do not weaken it. Capture the terminal reason and bounded diagnostic
   before cleanup.
5. If serialized command 2 passes, continue the remaining payload commands and
   closure validation. If it fails, name the exact Nx dependency/task from the
   sanitized evidence and allow at most one evidence-based TDD repair inside
   the existing Phase 1C-B boundary. Stop for architecture changes.
6. Re-run focused tests and independent review for any repair. Do not run the
   complete Phase 1C-B acceptance matrix until all task-level workstreams pass.
7. Continue Tasks 3+ from the plan: migrations, Redis-compatible acceptance
   endpoint/licensing, real role launch/readiness/drain, CRUD/BullMQ canaries,
   failure/restart/containment, final evidence report, and the single Phase 1C-B
   decision-token family.

## Non-negotiable boundaries

- Never weaken Phase 1C-A trust, ACL, manifest, Job, process-identity, or secret
  controls to make a test pass.
- Never log connection strings, credentials, environment blocks, buyer data,
  or full child command lines.
- Never kill by executable name; terminate only exact owned PID/creation-time
  identities and Job members.
- Never touch the real `%LOCALAPPDATA%\HowardLab\eBayCRM` profile in tests.
- Never run destructive cleanup without canonical containment and reparse
  validation.
- Keep `RedisCompatibility` explicit; do not enable `postgres-desktop` or claim
  Redis-free readiness.
- Do not add eBay dashboard/OAuth/sync, knowledge, tray, installer, updater,
  backup, or local-LLM features in Phase 1C-B.

# eBay CRM Desktop Master Product Requirements and Design

**Status:** Approved design

**Date:** 2026-07-13

**Product working name:** HowardLab eBay CRM Desktop

**Foundation:** Twenty fork

**Primary platform:** Windows 11

**Companion architecture specification:** [Twenty Runtime Modes and Windows Desktop Foundation](./2026-07-13-twenty-runtime-modes-design.md)

## 1. Executive summary

HowardLab eBay CRM Desktop is a personal, local-first Windows edition of Twenty for one Windows user operating two or more eBay seller accounts. It adds an eBay Operations area, a unified buyer-message inbox, account-aware order and listing context, seller-maintained Markdown knowledge, and a guarded LLM reply agent.

The application is installed and launched like a normal Windows program. In its default Local Desktop mode, an app-owned supervisor starts a bundled local PostgreSQL server, the Twenty server, the Twenty worker, PostgreSQL-backed runtime services, and an optional llama.cpp server. The user does not install Docker, PostgreSQL, Redis, Node.js, or a separate web server.

The product is designed for automatic replies to narrowly defined, low-risk post-purchase questions. Every automatic send must be grounded in an identifiable order or item and an unambiguous seller-authored Markdown source. Consequential, ambiguous, old, pre-sale, unsupported-language, or attachment-bearing messages are routed to human review. The application, not the model, makes the final send decision.

## 2. Product goals

1. Provide a one-installer Windows experience for a local Twenty fork.
2. Let one seller manage multiple eBay accounts in a single unified inbox.
3. Synchronize eBay conversations durably, resumably, and without duplicates.
4. Show the order, listing, account, and knowledge context needed to answer a buyer.
5. Let sellers maintain the reply knowledge base as ordinary Markdown files.
6. Support a local llama.cpp model by default and explicitly configured cloud models when desired.
7. Automatically answer only well-grounded, low-risk post-purchase questions.
8. Make every agent decision, knowledge version, approval, and outbound send auditable.
9. Continue synchronizing and replying from the Windows tray after the main window closes.
10. Provide encrypted backup and verified restoration on a clean Windows PC.

## 3. V1 non-goals

V1 does not include:

- A multi-tenant SaaS deployment.
- Multiple Windows users collaborating in the same workspace.
- A mobile application.
- Automatic replies to pre-sale questions.
- Automatic refunds, returns, cancellations, discounts, replacements, address changes, dispute handling, or delivery promises.
- Automatic handling of messages containing attachments.
- Automatic modification of seller knowledge from model output or corrected drafts.
- Model training or fine-tuning on eBay buyer content.
- Semantic embeddings over eBay buyer-message content without adequate eBay permission or legal clarification.
- Automatic cloud-model fallback without a separate future opt-in feature.
- Reliance on `auth.howardlab.dev`; a public authentication, licensing, or recovery service is explicitly deferred.

## 4. Target user and capacity

The V1 user is one seller using one Windows 11 account and operating two or more eBay seller accounts. The application must be tested against, but must not artificially enforce, these capacity targets:

- Five connected eBay accounts.
- 250,000 stored messages total.
- 500 incoming messages per day.
- 10,000 locally indexed listing and order records per account.
- Ten pending agent jobs without freezing the interface.
- Complete restart recovery without duplicate imports or replies.

## 5. Product principles

### 5.1 Local-first

Customer records, messages, knowledge, attachments, queue state, and audit history remain on the user's PC unless a configured feature must transmit specific data. Cloud LLM use, Gmail alerts, and GitHub backup require explicit configuration and clear disclosure of the data leaving the PC.

### 5.2 PostgreSQL is canonical

PostgreSQL remains Twenty's canonical database. Redis is not replaced by SQLite. Local Desktop mode replaces Redis responsibilities with PostgreSQL-backed durable coordination and discardable in-process caches while preserving Twenty's data model and server/worker separation.

### 5.3 Evidence before automation

The agent may draft freely, but it may send automatically only when deterministic gates prove the message is eligible and the proposed reply is grounded in approved seller knowledge and factual order context.

### 5.4 One account identity per conversation

Every conversation is permanently bound to the receiving eBay account. Reads, knowledge selection, model context, and outbound sends must preserve that identity. A conversation cannot silently move between accounts.

### 5.5 Recoverable operations

Synchronization, indexing, notifications, and outbound replies use checkpoints, durable jobs, leases, idempotency keys, and receipts. A crash must cause reconciliation, not duplicated work.

## 6. Runtime architecture

### 6.1 Default process topology

```text
HowardLab eBay CRM desktop supervisor
├── Twenty desktop window and tray
├── Local PostgreSQL
├── Twenty server
├── Twenty worker
├── PostgreSQL-backed runtime services
├── eBay synchronization and reply outbox
└── Optional local llama.cpp server
```

The server and worker remain separate supervised child processes. The desktop supervisor does not merge them into one process.

### 6.2 Runtime modes

#### Local Desktop mode

Local Desktop mode is the default and recommended mode. It starts bundled PostgreSQL and uses PostgreSQL-backed queue, schedule, coordination, and runtime-state adapters. It requires no Docker or Redis installation.

#### Twenty Compatibility mode

Compatibility mode is an advanced option that retains Redis and BullMQ behavior for development, upstream comparison, or unsupported workloads. Only one runtime mode may own a data directory and port set at a time. Switching modes requires a clean shutdown and a compatibility health check.

### 6.3 Startup and shutdown

Startup order is:

```text
single-instance lock
→ data-directory validation
→ PostgreSQL
→ migrations
→ Twenty server
→ Twenty worker
→ optional local model
→ synchronization
```

Automation stays paused until required health checks pass. Closing the main window minimizes to the tray. An explicit Exit command stops polling, drains or checkpoints jobs, stops the worker and server, and cleanly stops PostgreSQL. Forced PostgreSQL termination is not a normal shutdown path.

### 6.4 Redis responsibility replacement

| Existing responsibility | Local Desktop implementation |
|---|---|
| BullMQ jobs | PostgreSQL-backed queue behind Twenty's queue contract |
| Delays and schedules | Durable PostgreSQL schedules |
| Job retry and result state | Durable job rows and attempt receipts |
| Distributed locks | PostgreSQL advisory locks or leased ownership rows |
| Pub/sub wakeups | PostgreSQL `LISTEN/NOTIFY` with durable state in tables |
| Cache | Bounded in-process cache that is safe to discard |
| Rate-limit counters | PostgreSQL counters by account, API resource, and window |
| Runtime health | `desktop_runtime` records and supervisor health probes |

`pg-boss` is the first queue implementation candidate. Phase 0 must verify waiting-only deduplication, priorities, delayed jobs, dynamic schedules, per-queue concurrency, stalled-job recovery, retry semantics, and bounded shutdown. If it cannot satisfy the contract cleanly, the project will implement a focused PostgreSQL driver behind the same interface. This is a gated implementation selection, not an unresolved product requirement.

## 7. Local storage layout

Program binaries belong under `Program Files`. Mutable data belongs under the current Windows user's local application-data directory:

```text
%LOCALAPPDATA%\HowardLab\eBayCRM\
├── postgres\data\
├── files\
├── knowledge\
├── models\
├── backups\
├── logs\
└── runtime\
```

The live PostgreSQL directory must never be edited, copied for backup while running, placed in Git, or synchronized by a generic file-sync utility. The application UI is the normal database-access surface. An Advanced Database page shows status, size, location, integrity results, backup controls, and developer-only local connection information while the runtime is active.

PostgreSQL listens only on `127.0.0.1` with generated credentials. The database password is stored in the Windows-protected secret vault.

## 8. Database ownership and schemas

Twenty's existing core, metadata, and workspace schemas remain intact.

### 8.1 `desktop_runtime`

This schema contains local runtime concerns:

- Durable jobs and attempts.
- Schedules.
- Worker leases.
- Coordination state.
- Rate-limit windows.
- Process health and recovery metadata.
- Mode-selection metadata.

### 8.2 `ebay_crm`

This schema contains canonical eBay-domain records:

```text
accounts
buyers
conversations
messages
message_attachments
orders
order_items
listings
sync_runs
sync_checkpoints
sync_failures
reply_outbox
reply_attempts
agent_decisions
knowledge_assignments
```

All applicable records carry `workspace_id` and `ebay_account_id`. Account-scoped external IDs receive unique constraints. eBay records link to Twenty contacts and workspace records by UUID without modifying Twenty's internal schema contracts.

Message and decision records preserve the evidence needed for audit without storing secrets or hidden model reasoning.

### 8.3 Files outside PostgreSQL

- Markdown files are canonical in the managed `knowledge` folder.
- Message attachments are canonical in the managed `files` folder after quarantine processing.
- PostgreSQL stores file metadata, content hashes, paths, assignment state, and index state.
- GGUF models remain in the managed `models` folder and are excluded from normal backups.

## 9. eBay accounts and synchronization

### 9.1 Account connection

Each eBay account is connected separately through eBay OAuth. The application stores the account identifier and masked authorization metadata in PostgreSQL; refresh and access credentials remain in the Windows vault. Each account has a color from Twenty's design-token palette, marketplace labels, sync policy, language policy, automation mode, model policy, and notification status.

### 9.2 Sync presets

The onboarding and account settings UI provides:

#### Smart / message-linked — default

- Import all conversation history made available by the API by default, while allowing the seller to reduce the history range before sync.
- Prioritize unread and recent messages so the inbox becomes usable before the historical backfill finishes.
- Fetch only orders and listings referenced by synchronized conversations.
- Fetch missing older order/listing context on demand.

#### Seller Dashboard

- Import selected message history.
- Import active listings.
- Import orders from a seller-selected date range.

#### Custom

- Select message-history range.
- Select order-history range.
- Select active and ended listing scope.
- Select attachment-download behavior.
- Select refresh frequency within safe bounds.

Before import, the UI shows discoverable record counts, approximate API pages, current rate-limit information when available, selected data categories, and estimated local storage. Estimates remain explicitly approximate because discovery requests themselves consume calls and not every endpoint exposes a complete total.

### 9.3 Initial synchronization

Initial synchronization proceeds in this order:

1. Authenticate and validate account identity.
2. Fetch unread and recent conversations.
3. Normalize and persist messages.
4. Make the recent inbox usable.
5. Backfill the selected older conversation history.
6. Fetch selected or message-linked orders and listings.
7. Complete indexes and reconciliation.

The progress UI is per account and reports stage, discovered, imported, skipped, failed, and remaining counts. When a reliable total is unavailable, it uses an indeterminate stage instead of inventing a percentage. The seller may pause, resume, retry, or minimize the process to the background. Checkpoints allow restart without repeating completed pages.

Historical imports never play sounds and never trigger automatic replies.

### 9.4 Incremental polling

Because a local desktop application cannot reliably receive public eBay webhooks, V1 uses adaptive polling while the runtime is active:

- Approximately every 60 seconds under normal activity.
- Approximately every 30 seconds for ten minutes after recent activity.
- Approximately every two minutes after extended idle time.
- Immediately on startup, wake, reconnect, foreground, and manual refresh.
- Staggered across accounts to avoid bursts.

The scheduler reads eBay rate-limit data where available, respects `429` and reset times, and prioritizes live message import and outbound replies over historical work. Low quota pauses optional backfill work without blocking essential operations.

### 9.5 Sync targets

- Normal new-message detection target: at most 60 seconds.
- P95 detection target: at most two minutes under healthy network and quota conditions.
- Sound notification: immediately after durable import.
- Eligible reply delivery: immediately after the model and send gate complete; no artificial human-like delay.
- Messages not processed within two minutes receive a delayed status visible in the dashboard.

## 10. eBay Operations user experience

Twenty's left sidebar receives an **eBay** entry. The eBay area uses Twenty's existing typography, spacing, radius, elevation, borders, semantic colors, themes, responsive primitives, and component library. It must not create a parallel design system or hard-code account colors.

### 10.1 Navigation

The eBay area contains:

- Operations Dashboard.
- Unified Inbox.
- Orders and Listings.
- Knowledge.
- Human Review.
- Agent Activity and Audit.
- Account, Sync, Model, Automation, and Notification Settings.

### 10.2 Default dashboard

The approved default is the Operations Dashboard layout. It prioritizes:

- Unread messages.
- Messages requiring review.
- Failed or delayed replies.
- Automatic replies sent today.
- Average response time.
- Account and OAuth health.
- Synchronization and backfill progress.
- Missing SKU, listing, or knowledge mappings.

The dashboard uses combined metrics by default and supports account filtering.

### 10.3 Unified inbox

Every message row and conversation header displays:

- Account name and color badge.
- Marketplace.
- Buyer.
- Item thumbnail and title when available.
- Unread state.
- Automation or review state.
- Message age.

The inbox supports per-account filters, combined and per-account unread counts, saved views, and a visible “Sending as” identity. Buyers with similar identifiers across accounts are not automatically merged, though the UI may show possible matches.

### 10.4 Review workspace

The review workspace shows together:

- Original buyer message.
- Sanitized attachment previews.
- Account and marketplace.
- Buyer and conversation timeline.
- Order and listing context.
- Detected language and English translation.
- Matched knowledge passages and revision identifiers.
- Risk and eligibility reasons.
- Editable proposed reply.
- Correct outbound account identity.

## 11. Markdown knowledge system

### 11.1 Canonical source

Markdown files in `%LOCALAPPDATA%\HowardLab\eBayCRM\knowledge\` are canonical. The in-app editor edits the same files that an external editor edits. PostgreSQL must not maintain a competing canonical copy.

Supported V1 files are UTF-8 `.md` files, optionally with a BOM that is normalized during parsing. The default maximum is 5 MiB per file.

### 11.2 Metadata and assignment

Optional YAML frontmatter supports fields such as:

```yaml
title: TV Remote X2 Setup
accounts:
  - star-shop
skus:
  - RM-X2
listingIds:
  - "1234567890"
keywords:
  - pair remote
  - setup
priority: 50
```

The UI lets sellers import, create, edit, and assign knowledge by account, SKU, listing ID, manual product mapping, title pattern, variation attributes, keywords, account-wide scope, global scope, and priority. It also provides unmatched-product and unmatched-message queues, manual repair, test questions, and passage previews.

### 11.3 Retrieval hierarchy

The shared retrieval service uses this order:

1. Account and listing ID.
2. Account and SKU.
3. Saved manual product mapping.
4. Listing-title pattern and variation attributes.
5. Seller-defined keywords.
6. Account-wide knowledge.
7. Global knowledge.
8. No confident match, which blocks automatic reply.

The test screen and production responder must call the same retrieval service.

### 11.4 File watcher and reconciliation

The knowledge subsystem uses:

- Recursive OS filesystem events.
- A 750 ms per-path debounce.
- Two stable-file checks 250 ms apart.
- Parse and index within approximately one to two seconds after a stable save.
- SHA-256 hashes to distinguish actual changes from duplicate events.
- Full reconciliation on startup and wake.
- Full reconciliation every five minutes.
- Manual refresh.
- Atomic writes from the in-app editor.

Before an automatic reply, the system waits up to two seconds for a relevant pending reindex. If it cannot obtain a valid stable revision, it routes the message to review.

The watcher must handle editor temporary files, atomic rename, file locks, partial writes, offline edits, rename/move, deletion, invalid frontmatter, duplicate identifiers, optimistic editing conflicts, BOMs, Windows case-insensitive paths, and retryable indexing failures. It rejects symlinks or junctions that escape the managed knowledge root.

An invalid replacement is not silently served from an old revision for automatic reply. The previous revision remains available for audit and recovery, while the affected source is marked invalid and excluded until repaired. Each agent run records an immutable knowledge-revision snapshot.

## 12. Message and automation scope

### 12.1 Ingestion scope

Every incoming conversation made available by the selected sync policy is imported and displayed.

### 12.2 Eligible automatic-reply scope

V1 automatic replies are limited to low-risk post-purchase messages linked to an identifiable order or listing, including:

- Greetings associated with a support question.
- Setup and installation.
- Product usage.
- Basic troubleshooting.
- Package contents and product specifications.
- Factual order or purchased-item details.
- Other questions directly and unambiguously answered by approved Markdown.

### 12.3 Mandatory human-review scope

The following always require review:

- Refunds, returns, cancellations, disputes, and payment issues.
- Discounts, offers, replacements, and compensation.
- Address changes.
- Delivery promises or commitments not already factual in order data.
- Safety, legal, regulatory, or marketplace-policy topics.
- Missing or conflicting order, listing, buyer, or knowledge context.
- Low-confidence retrieval or model output.
- Unsupported, disabled, mixed, or low-confidence language.
- Abusive, suspicious, or prompt-injection content.
- Any image or other attachment.
- Pre-sale questions in V1.
- Messages older than 24 hours.
- Messages imported as historical backfill.

## 13. Reply-agent pipeline

```text
durably imported message
→ account and age validation
→ order/listing resolution
→ deterministic risk precheck
→ knowledge retrieval
→ minimal context assembly
→ structured model generation
→ deterministic eligibility gate
→ durable outbox or human-review draft
```

Buyer content is untrusted data. It cannot redefine the system prompt, select credentials, change the outbound account, enable tools, change automation mode, or bypass policy.

The model returns a structured result containing:

- Detected language.
- Intent.
- Risk category.
- Referenced order and item.
- Knowledge references.
- Proposed reply.
- Confidence and uncertainty indicators.
- Structured validation status.

The application, not model self-confidence alone, decides whether the reply may be sent.

### 13.1 Strict all-checks gate

Automatic sending requires every condition to pass:

- Account is connected and in Automatic mode.
- Global automation is not paused.
- Message is newly received after account connection.
- Message is no more than 24 hours old.
- Message has not been processed or replied to already.
- Order or item identity is unambiguous.
- Intent is in the eligible allowlist.
- No attachment exists.
- No deterministic risk rule fires.
- Language is enabled and confidently detected.
- A valid, stable Markdown revision is matched.
- Evidence has no unresolved conflict.
- Model output conforms to the required structure.
- Reply is grounded in the selected evidence.
- Correct outbound account identity is verified.
- Durable idempotency key is reserved.

V1 does not expose a raw confidence slider. Failing any condition creates a review draft and alerts the seller.

### 13.2 Account modes and emergency stop

Each account supports:

- **Automatic:** eligible messages may be sent.
- **Human Review:** the agent may draft but never send automatically.
- **Sync Only:** import and notify without running the reply agent.

The tray provides **Pause All Automation**, which blocks new sends without stopping synchronization. New accounts remain setup-locked during import and configuration. After required checks pass, the onboarding flow presents Automatic as the intended operating mode but requires explicit seller confirmation before enabling it.

### 13.3 Outbound delivery

An eligible reply enters a durable outbox with account ID, conversation ID, source message ID, idempotency key, expected state, knowledge revision, model identity, policy version, and payload hash. Delivery attempts produce immutable receipts. A retry must reconcile remote and local state before sending again.

There is no artificial delay after generation. An eligible reply is submitted as soon as the outbox worker can safely deliver it.

## 14. Model routing and language

### 14.1 Local-first routing

The default model profile points to an app-managed OpenAI-compatible llama.cpp server. The local runtime owns GGUF discovery, start/stop, hardware parameters, health, logs, and endpoint availability. Twenty consumes the endpoint through its OpenAI-compatible provider boundary.

Cloud providers are disabled until configured. API keys remain in the Windows vault. The UI discloses what message, order, and knowledge content can leave the PC before enabling a cloud provider.

Profiles support:

- Primary local model.
- Optional configured cloud model.
- Manual fallback choice.
- Testing model.
- Per-account override.

Local timeout, malformed output, insufficient grounding, or model unavailability blocks automatic reply and creates a review item. Automatic cloud fallback is off in V1.

### 14.2 Model qualification

A model may be enabled for automatic replies only after passing evaluation cases for:

- Structured output.
- Tool and context adherence.
- Knowledge grounding.
- No invented facts or commitments.
- Enabled-language quality.
- Prompt-injection resistance.
- Long-context behavior.
- Cancellation and timeout behavior.
- Required latency on the user's hardware.

### 14.3 Language behavior

The agent uses English seller knowledge and replies in the buyer's detected language when that language is enabled for the account. English is enabled by default.

The system stores:

- Original buyer message.
- Detected language and confidence.
- Generated reply.
- English translation of non-English messages and replies.

Low-confidence, mixed, unsupported, or disabled languages require review. Account settings provide preview tests before enabling automatic replies for another language.

## 15. Controlled knowledge improvement

When a seller edits and sends a draft, the application stores the final sent reply and edit delta for audit. It never silently changes Markdown, fine-tunes a model, or treats the correction as trusted policy.

The UI may offer **Save this correction to knowledge**. That action must show the target file, proposed Markdown diff, assignment consequences, and resulting retrieval preview. The seller must explicitly approve the file edit.

## 16. Notifications and escalation email

### 16.1 Local notifications

Every genuinely new incoming message plays a configurable local sound immediately after durable import. Imported history remains silent. Messages needing review also create a tray/desktop warning. Notification deduplication keys prevent repeated sounds or alerts for the same event after retry.

### 16.2 Gmail escalation

High-risk, unanswered, or failed messages generate a Gmail alert after the seller configures a Gmail app password, destination address, and successful test. The Gmail app password is never the seller's normal Google password and remains in the Windows vault. SMTP connections require authenticated TLS with certificate validation; plaintext SMTP and invalid certificates are rejected.

The email is a responsive multipart message with HTML and plain-text alternatives. It contains:

- Account and marketplace.
- Buyer.
- Received time.
- Item and order context.
- Complete buyer message.
- Human-review reason.
- Explicit “No automatic reply sent” state.
- Open Conversation desktop link and a fallback conversation identifier.
- Sanitized inline previews of attached images when safe.

Buyer text is escaped and cannot inject HTML, scripts, tracking pixels, or executable content. The setup UI clearly warns that full buyer content and safe image previews will leave the local PC and be stored in Gmail.

### 16.3 Attachment safety

Any attachment forces human review. Image processing uses:

1. Quarantine download.
2. Magic-byte and MIME validation independent of filename.
3. Safe raster allowlist such as JPEG, PNG, and WebP.
4. Rejection of SVG, HTML, executables, and disguised files.
5. Windows Security scanning when available.
6. Creation of a metadata-stripped preview copy.
7. Per-image and total-email size limits.

The sanitized preview may be included in Gmail. The unmodified original remains local. If validation, scanning, or size checks fail, the email includes the buyer text and omission reason but not the unsafe file. Non-image attachments are not emailed in V1.

## 17. Secret and local-service security

### 17.1 Secret vault

The desktop supervisor owns a Windows DPAPI-protected vault scoped to the current Windows user. It stores:

- eBay OAuth tokens.
- Gmail app password.
- Cloud LLM keys.
- GitHub authorization.
- PostgreSQL credentials.
- Local control-channel secrets.

PostgreSQL stores only provider, account, masked suffix, state, created time, and last-used time. After save, the UI never returns the complete secret and provides only Test, Replace, and Delete.

Secrets must not appear in:

- PostgreSQL application records.
- Plaintext configuration.
- Environment-variable diagnostics.
- Process command lines.
- Logs or telemetry.
- Crash reports.
- LLM prompts.
- Backup archives.
- Source control.

### 17.2 Local network boundary

All internal services bind to loopback. The supervisor generates an authenticated local control channel. Cross-origin rules reject unrelated browser origins. A second application instance cannot start another runtime against the same data directory.

### 17.3 Credential failure

An expired or invalid credential pauses only the affected account or provider. It never switches to another account or cloud provider silently. The dashboard and tray show the failure and required action.

## 18. Retention and deletion

Disconnecting an eBay account immediately removes its active credentials and stops synchronization and automation. Its local history remains read-only by default.

The seller may explicitly delete an account's local data. Per-account and per-buyer deletion removes messages, attachments, indexes, drafts, notification payloads, and derived records. Audit records retain only non-content evidence required to prove deletion and must not preserve deleted buyer text.

Legally or contractually required marketplace-account deletion requests override read-only retention. OAuth revocation and application uninstall provide clear choices for deleting local data.

## 19. Backup, GitHub transfer, and restore

### 19.1 Backup contents

The application creates a logical, encrypted `.ebaycrm-backup` archive:

```text
HowardLab-eBayCRM-YYYY-MM-DD.ebaycrm-backup
├── database.dump
├── files\
├── knowledge\
├── settings.json
└── manifest.json
```

The archive contains PostgreSQL records, attachments, Markdown, assignments, and non-secret settings. It excludes credentials, logs, temporary files, and local GGUF models.

Backup uses `pg_dump` in a consistent logical format. File collection is coordinated with attachment writes, the manifest records schema and application versions plus content hashes, and the final archive is written atomically.

The versioned backup format derives an encryption key from the seller's recovery password using Argon2id with a unique random salt and recorded work parameters, then encrypts and authenticates the payload with XChaCha20-Poly1305 using a unique random nonce. No recovery password or derived key is stored in the archive. Losing the recovery password makes the encrypted archive unrecoverable.

### 19.2 Destinations

Local-file backup is the default and supports any user-selected folder, including a separately managed OneDrive or external-drive folder.

Manual GitHub backup is an optional V1 destination with these constraints:

- It uses a separate private repository, never the public source repository.
- Repository visibility is checked before each upload.
- The archive is encrypted locally before upload.
- The archive is uploaded as a GitHub Release asset, not committed to Git history.
- GitHub authorization remains in the Windows vault.
- Assets of 2 GiB or more are refused because GitHub Release assets must be under 2 GiB; the UI directs the seller to local or file-sync storage.

### 19.3 Restore

On a clean PC, the seller installs the application, chooses Restore Existing Backup, selects or downloads the archive, enters the recovery password, and restores into a new local PostgreSQL cluster. Restore validates the manifest and hashes, imports files, runs required migrations, verifies record counts, and keeps automation paused.

Because backups exclude credentials, the seller must reconnect eBay, Gmail, GitHub, and cloud providers. Local models must be selected or downloaded again. Automation requires a fresh health and eligibility confirmation after restore.

## 20. Failure handling

| Failure | Required behavior |
|---|---|
| PostgreSQL unavailable or corrupt | Enter recovery mode; do not sync or send |
| Twenty server unavailable | Keep supervisor and diagnostics active; restart with bounded backoff |
| Worker crash | Reclaim expired leases and restart without duplicating work |
| eBay authorization failure | Pause affected account and request reconnection |
| eBay `429` or quota exhaustion | Honor reset; preserve essential live work; defer backfill |
| Transient network or eBay `5xx` | Retry with bounded exponential backoff and jitter |
| LLM timeout or malformed result | Do not reply; create review item |
| Gmail failure | Keep durable notification job; warn in tray; retry safely |
| Knowledge parse/index failure | Exclude invalid file; show repair status; block affected automation |
| Attachment validation failure | Quarantine; omit from email; require review |
| Abnormal shutdown | Reconcile cursors, jobs, receipts, and outbox before resuming |
| Backup validation failure | Preserve existing installation; reject restore atomically |

All retries are bounded, observable, and idempotent. Repeated failure creates a visible terminal state instead of an infinite silent loop.

## 21. Audit requirements

The system records:

- Account and workspace identity.
- Source conversation and message.
- Sync run and checkpoint.
- Order/listing resolution.
- Knowledge files, sections, hashes, and revision IDs.
- Model and provider identity.
- Policy version and eligibility results.
- Draft, seller edit, and approval.
- Outbox idempotency key and payload hash.
- Delivery attempts and eBay receipts.
- Notification attempts.
- Automation-mode and settings changes.

The audit UI does not expose hidden chain-of-thought or secrets. It exposes concise policy reasons and evidence sufficient to understand why the system sent, drafted, blocked, retried, or failed.

## 22. Testing strategy

### 22.1 Unit tests

- Risk and eligibility rules.
- Account routing.
- Language policy.
- Markdown parsing and assignment precedence.
- File stability and conflict logic.
- Idempotency-key generation.
- Secret redaction.
- Retention and deletion selection.

### 22.2 Runtime contract tests

The same queue, schedule, cache, lock, session, and coordination contract suite runs against Local Desktop and Compatibility modes. The suite tests priorities, retry, deduplication, delays, recurring schedules, concurrency, worker crash, lease expiry, and shutdown.

### 22.3 Integration and contract tests

- PostgreSQL integration tests without Redis.
- Compatibility-mode Redis/BullMQ tests.
- Mock eBay OAuth, Message, Order, Listing, and rate-limit APIs.
- Pagination, quota reset, partial response, retry, and schema-drift fixtures.
- Local and cloud OpenAI-compatible provider fixtures.
- Gmail formatting, deduplication, and attachment-safety fixtures.

### 22.4 End-to-end tests

- Clean Windows installation and first launch.
- Two eBay accounts with overlapping buyer identities.
- Smart sync and resumable backfill.
- Window close to tray and Windows wake.
- Human-review and automatic reply paths.
- Emergency pause during queued work.
- Crash between remote send and local receipt.
- Backup on one clean environment and restore on another.
- Upgrade across supported application and schema versions.

### 22.5 Model evaluation

The approved evaluation set includes eligible and ineligible examples across supported languages, missing SKUs, ambiguous listings, conflicting knowledge, prompt injection, refunds, delivery promises, attachments, old messages, and unsupported questions. A model cannot be marked automation-capable until it meets the defined pass threshold for every safety-critical class.

### 22.6 Security tests

- Secret scans across PostgreSQL, logs, crash output, backups, and command lines.
- Loopback binding and origin enforcement.
- Malicious attachment fixtures.
- Malicious Markdown and frontmatter fixtures.
- Buyer prompt-injection fixtures.
- Backup tampering and wrong-password tests.
- Public-repository GitHub backup refusal.

## 23. V1 acceptance criteria

V1 is complete when all of the following are demonstrated:

1. A clean Windows 11 PC installs and launches the application without Docker or manual service setup.
2. Local Desktop mode operates Twenty server and worker without Redis and passes the runtime contract suite.
3. Compatibility mode remains available and passes its equivalent contract suite.
4. At least two eBay accounts connect and synchronize simultaneously.
5. The recent inbox becomes usable before historical backfill completes.
6. Backfill pauses, resumes, restarts, and finishes without duplicate messages.
7. The approved Operations Dashboard and unified inbox preserve account identity.
8. Markdown changes are detected, indexed, versioned, tested, and safely excluded when invalid.
9. An eligible post-purchase message can be answered from cited Markdown and correct order context.
10. Every mandatory-review class is blocked from automatic sending.
11. A crash around outbound delivery does not cause a duplicate reply.
12. Sounds, tray warnings, and formatted Gmail alerts behave according to policy.
13. Pause All Automation prevents new sends immediately without stopping sync.
14. Secrets are absent from PostgreSQL records, logs, process arguments, and backups.
15. An encrypted backup restores successfully on a clean PC and requires provider reconnection.
16. Capacity and responsiveness targets in Section 4 pass on representative Windows hardware.

## 24. Delivery roadmap

### Phase 0 — Foundation spike

- Pin and record a Twenty upstream revision.
- Inventory Redis, BullMQ, session, cache, lock, and realtime dependencies.
- Define runtime contracts.
- Execute the `pg-boss` semantic spike.
- Establish dual-backend CI and upstream guardrails.

### Phase 1 — Windows runtime

- Desktop supervisor.
- Bundled PostgreSQL lifecycle.
- Server/worker supervision.
- Local data directories.
- Tray and explicit shutdown.
- Migration and recovery shell.
- Installer prototype.

### Phase 2 — eBay synchronization

- OAuth and secret references.
- Multi-account records.
- `ebay_crm` schema.
- Smart sync and adaptive polling.
- Durable checkpoints, quota handling, and progress UI.

### Phase 3 — Dashboard and inbox

- Twenty sidebar integration.
- Operations Dashboard.
- Unified multi-account inbox.
- Order/listing context.
- Account-aware status and audit surfaces.

### Phase 4 — Markdown knowledge

- Managed folder and editor.
- Watcher and reconciliation.
- Assignment hierarchy.
- Missing-mapping repair.
- Full-text retrieval and test screen.

### Phase 5 — Human-review agent

- Local and cloud provider routing.
- Structured generation.
- Language handling.
- Risk policy.
- Review editor.
- Sounds, Gmail, and attachment previews.

### Phase 6 — Strict automatic replies

- Eligibility gate.
- Durable reply outbox.
- Account modes and emergency pause.
- Delivery receipts and crash reconciliation.
- Automation evaluation and release gate.

### Phase 7 — Production hardening

- Encrypted backup and restore.
- Private GitHub Release upload.
- Upgrade and rollback.
- Retention and deletion.
- Performance and long-duration tests.
- Installer and release hardening.

Each phase receives a separate detailed implementation plan immediately before execution. Passing a phase's verification gate is required before later phases may depend on it.

## 25. Deferred follow-on work

The following work is intentionally deferred beyond V1:

- `auth.howardlab.dev` integration for public OAuth callbacks, licensing, or recovery.
- Semantic embeddings over eBay buyer messages pending permission and legal review.
- Automatic pre-sale replies.
- Automatic cloud failover.
- Team/multi-user operation.
- Mobile client.
- Generic non-image attachment delivery in Gmail.
- Model fine-tuning.
- Automatic knowledge learning.
- Additional marketplace channels.

## 26. External references

- eBay Message API: https://developer.ebay.com/develop/api/sell/message_api
- eBay Developer Analytics rate-limit types: https://developer.ebay.com/api-docs/developer/analytics/types/api%3ARate
- eBay inventory pagination: https://developer.ebay.com/api-docs/sell/inventory/types/slr%3AInventoryItems
- eBay line-item identity: https://developer.ebay.com/api-docs/sell/fulfillment/types/sel%3ALineItem
- Windows DPAPI `CryptProtectData`: https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata
- Windows DPAPI `CryptUnprotectData`: https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata
- GitHub large-file guidance: https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github
- GitHub Releases: https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
- GitHub repository visibility: https://docs.github.com/en/repositories/creating-and-managing-repositories/about-repositories

## 27. Approved decisions summary

- Use the safety-first vertical-slice roadmap.
- Default to Local Desktop mode with PostgreSQL and no Redis.
- Preserve Twenty's database schemas and server/worker separation.
- Use PostgreSQL for canonical CRM data and durable runtime coordination.
- Use `pg-boss` as the first queue compatibility spike, not as an assumption.
- Provide Compatibility mode for Redis/BullMQ.
- Support one Windows user and multiple eBay accounts.
- Default to Smart/message-linked sync with seller-selectable alternatives.
- Use the Operations Dashboard layout with Twenty design tokens.
- Keep Markdown files canonical and externally editable.
- Use a local model by default and require explicit cloud configuration.
- Make automatic replies strict, evidence-grounded, and post-purchase only.
- Provide Automatic, Human Review, Sync Only, and global Pause All modes.
- Sound every genuinely new message.
- Email full buyer messages and safe image previews for escalation.
- Store secrets only in the Windows-protected vault.
- Keep disconnected account history read-only by default.
- Exclude credentials from backups.
- Encrypt every portable backup.
- Permit manual GitHub backup only as an encrypted private Release asset.
- Defer the public HowardLab authentication website and buyer-message embeddings.

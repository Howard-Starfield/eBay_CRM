import { type Pool, type PoolClient } from 'pg';
import { type Db as PgBossDatabase } from 'pg-boss';
import { v4, v5 } from 'uuid';

import { type QueueJobOptions } from 'src/engine/core-modules/message-queue/drivers/interfaces/job-options.interface';
import { type MessageQueueJobRecord } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-record.type';
import { type MessageQueueJobState } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-state.type';
import { type MessageQueueStats } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-stats.type';
import { type MessageQueueJobData } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueueWorkerOptions } from 'src/engine/core-modules/message-queue/interfaces/message-queue-worker-options.interface';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

import {
  type LogicalDeadLetterArgs,
  type LogicalJobStart,
  type LogicalPhysicalArgs,
  type LogicalQueuePolicy,
  type LogicalSettlementArgs,
  type LogicalStartArgs,
  type LogicalTransport,
} from './pg-boss-logical-ledger.types';

type PgBossLogicalLedgerOptions = {
  pool: Pool;
  schema: 'desktop_runtime';
  transport: LogicalTransport;
};

type PersistedQueuePolicy = {
  stall_recovery_limit: number;
  heartbeat_seconds: number | null;
  worker_ready: boolean;
};

type StartableQueueJob = {
  id: string;
  generation: number;
  current_physical_job_id: string;
  status: string;
  stall_count: number;
  stall_recovery_limit: number;
  transport_retry_count: number;
  job_name?: string;
  payload?: MessageQueueJobData;
};

type SettlementQueueJob = {
  id: string;
  generation: number;
  current_physical_job_id: string;
  current_execution_token: string | null;
  status: string;
  handler_failure_count: number;
  handler_retry_limit: number;
  stall_count: number;
  stall_recovery_limit: number;
  priority: number;
};

type QueueJobInspectionRow = {
  id: string;
  job_name: string;
  payload: MessageQueueJobData;
  status: string;
  started_count: number;
  created_at: Date;
  updated_at: Date;
  completed_at: Date | null;
  failed_at: Date | null;
  last_error: { name?: string; message?: string } | null;
};

type PostgreSqlError = Error & {
  code?: string;
  constraint?: string;
};

const PHYSICAL_JOB_NAMESPACE = '47c50f4e-5f71-5f5d-a93c-8040c7f65296';

export class PgBossLogicalLedger {
  private initializePromise?: Promise<void>;

  constructor(private readonly options: PgBossLogicalLedgerOptions) {}

  async initialize(): Promise<void> {
    if (!this.initializePromise) {
      this.initializePromise = this.initializeSchema().catch((error) => {
        this.initializePromise = undefined;
        throw error;
      });
    }

    await this.initializePromise;
  }

  async registerQueuePolicy(
    queueName: MessageQueue,
    options: MessageQueueWorkerOptions,
  ): Promise<LogicalQueuePolicy> {
    const stallRecoveryLimit = options.maxStalledCount ?? 1;

    if (!Number.isInteger(stallRecoveryLimit) || stallRecoveryLimit < 0) {
      throw new Error('stall recovery limit must be a non-negative integer');
    }

    const heartbeatSeconds =
      options.lockDuration === undefined
        ? undefined
        : Math.max(10, Math.ceil(options.lockDuration / 1_000));

    await this.initialize();
    await this.options.pool.query({
      text: `
        INSERT INTO ${this.options.schema}.queue_policy
          (queue_name, stall_recovery_limit, heartbeat_seconds, worker_ready_at)
        VALUES ($1, $2, $3, '-infinity'::timestamptz)
        ON CONFLICT (queue_name) DO UPDATE SET
          stall_recovery_limit = EXCLUDED.stall_recovery_limit,
          heartbeat_seconds = EXCLUDED.heartbeat_seconds,
          worker_ready_at = '-infinity'::timestamptz,
          updated_at = now()
      `,
      values: [queueName, stallRecoveryLimit, heartbeatSeconds ?? null],
    });

    return { stallRecoveryLimit, heartbeatSeconds };
  }

  async markWorkerReady(queueName: MessageQueue): Promise<void> {
    await this.initialize();
    await this.options.pool.query({
      text: `
        UPDATE ${this.options.schema}.queue_policy
        SET worker_ready_at = now(), updated_at = now()
        WHERE queue_name = $1
      `,
      values: [queueName],
    });
  }

  async createJob<T extends MessageQueueJobData>(args: {
    queueName: MessageQueue;
    jobName: string;
    data: T;
    options?: QueueJobOptions;
  }): Promise<string | null> {
    const handlerRetryLimit = args.options?.retryLimit ?? 0;

    if (!Number.isInteger(handlerRetryLimit) || handlerRetryLimit < 0) {
      throw new Error('handler retry limit must be a non-negative integer');
    }

    await this.initialize();

    const client = await this.options.pool.connect();
    const logicalJobId = v4();
    const physicalJobId = this.physicalJobId(logicalJobId, 0);
    const priority = args.options?.priority ?? 0;
    const availableAt = new Date(Date.now() + (args.options?.delay ?? 0));

    try {
      await client.query('BEGIN');

      const policy = await this.loadReadyQueuePolicy(client, args.queueName);

      await client.query({
        text: `
          INSERT INTO ${this.options.schema}.queue_job (
            id,
            queue_name,
            job_name,
            payload,
            payload_version,
            status,
            generation,
            handler_failure_count,
            handler_retry_limit,
            stall_count,
            stall_recovery_limit,
            started_count,
            transport_retry_count,
            priority,
            available_at,
            dedup_key,
            current_physical_job_id
          ) VALUES (
            $1, $2, $3, $4::jsonb, 2, 'queued', 0, 0, $5, 0, $6, 0, 0,
            $7, $8, $9, $10
          )
        `,
        values: [
          logicalJobId,
          args.queueName,
          args.jobName,
          args.data,
          handlerRetryLimit,
          policy.stall_recovery_limit,
          priority,
          availableAt,
          args.options?.id ?? null,
          physicalJobId,
        ],
      });

      await this.options.transport.send({
        queueName: args.queueName,
        envelope: { version: 2, logicalJobId, generation: 0 },
        physicalJobId,
        stallRecoveryLimit: policy.stall_recovery_limit,
        priority,
        availableAt,
        db: this.toPgBossDatabase(client),
      });

      await client.query('COMMIT');

      return logicalJobId;
    } catch (error) {
      await client.query('ROLLBACK');

      if (
        args.options?.id !== undefined &&
        this.isWaitingDeduplicationConflict(error)
      ) {
        return null;
      }

      throw error;
    } finally {
      client.release();
    }
  }

  async startAttempt(args: LogicalStartArgs): Promise<LogicalJobStart> {
    await this.initialize();

    const client = await this.options.pool.connect();

    try {
      await client.query('BEGIN');

      const result = await client.query<StartableQueueJob>({
        text: `
          SELECT id, generation, current_physical_job_id, status, stall_count,
            stall_recovery_limit, transport_retry_count
          FROM ${this.options.schema}.queue_job
          WHERE id = $1
          FOR UPDATE
        `,
        values: [args.envelope.logicalJobId],
      });
      const job = result.rows[0];

      if (
        !job ||
        job.id !== args.envelope.logicalJobId ||
        job.generation !== args.envelope.generation ||
        job.current_physical_job_id !== args.physicalJobId ||
        !['queued', 'retry_wait', 'active'].includes(job.status)
      ) {
        await this.completeFencedEnvelopeWithClient(client, args);
        await client.query('COMMIT');

        return { kind: 'fenced' };
      }

      const retryDelta = Math.max(
        0,
        args.transportRetryCount - job.transport_retry_count,
      );
      const stallCount = job.stall_count + retryDelta;
      const executionToken = v4();

      if (stallCount > job.stall_recovery_limit) {
        const transitioned = await client.query({
          text: `
            UPDATE ${this.options.schema}.queue_job
            SET status = 'failed',
              stall_count = $2,
              transport_retry_count = $3,
              current_execution_token = NULL,
              failure_kind = 'stall_exhausted',
              failed_at = now(),
              updated_at = now()
            WHERE id = $1
              AND generation = $4
              AND current_physical_job_id = $5
              AND status IN ('queued', 'retry_wait', 'active')
          `,
          values: [
            job.id,
            stallCount,
            Math.max(job.transport_retry_count, args.transportRetryCount),
            job.generation,
            args.physicalJobId,
          ],
        });

        if (transitioned.rowCount === 0) {
          await this.completeFencedEnvelopeWithClient(client, args);
          await client.query('COMMIT');

          return { kind: 'fenced' };
        }

        await client.query({
          text: `
            INSERT INTO ${this.options.schema}.queue_job_attempt (
              id, job_id, generation, physical_job_id, worker_instance_id,
              execution_token, transport_retry_count, started_at, finished_at,
              outcome
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, now(), now(), 'stalled')
          `,
          values: [
            v4(),
            job.id,
            job.generation,
            args.physicalJobId,
            args.workerInstanceId,
            executionToken,
            args.transportRetryCount,
          ],
        });
        await this.completeFencedEnvelopeWithClient(client, args);
        await client.query('COMMIT');

        return { kind: 'stall-exhausted' };
      }

      const details =
        job.job_name === undefined || job.payload === undefined
          ? await client.query<{
              job_name: string;
              payload: MessageQueueJobData;
            }>({
              text: `
                SELECT job_name, payload
                FROM ${this.options.schema}.queue_job
                WHERE id = $1
              `,
              values: [job.id],
            })
          : { rows: [{ job_name: job.job_name, payload: job.payload }] };
      const jobDetails = details.rows[0];

      if (!jobDetails) {
        throw new Error(`logical queue job ${job.id} has no handler payload`);
      }

      const transitioned = await client.query({
        text: `
          UPDATE ${this.options.schema}.queue_job
          SET status = 'active',
            stall_count = $2,
            transport_retry_count = $3,
            started_count = started_count + 1,
            current_execution_token = $4,
            updated_at = now()
          WHERE id = $1
            AND generation = $5
            AND current_physical_job_id = $6
            AND status IN ('queued', 'retry_wait', 'active')
          RETURNING id
        `,
        values: [
          job.id,
          stallCount,
          Math.max(job.transport_retry_count, args.transportRetryCount),
          executionToken,
          job.generation,
          args.physicalJobId,
        ],
      });

      if (transitioned.rowCount === 0) {
        await this.completeFencedEnvelopeWithClient(client, args);
        await client.query('COMMIT');

        return { kind: 'fenced' };
      }

      await client.query({
        text: `
          INSERT INTO ${this.options.schema}.queue_job_attempt (
            id, job_id, generation, physical_job_id, worker_instance_id,
            execution_token, transport_retry_count, started_at, outcome
          ) VALUES ($1, $2, $3, $4, $5, $6, $7, now(), 'running')
        `,
        values: [
          v4(),
          job.id,
          job.generation,
          args.physicalJobId,
          args.workerInstanceId,
          executionToken,
          args.transportRetryCount,
        ],
      });
      await client.query('COMMIT');

      return {
        kind: 'execute',
        logicalJobId: job.id,
        jobName: jobDetails.job_name,
        data: jobDetails.payload,
        executionToken,
      };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async completeFencedEnvelope(args: LogicalPhysicalArgs): Promise<void> {
    await this.initialize();

    const client = await this.options.pool.connect();

    try {
      await client.query('BEGIN');
      await this.completeFencedEnvelopeWithClient(client, args);
      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async settleSuccess(
    args: LogicalSettlementArgs,
  ): Promise<'settled' | 'fenced'> {
    await this.initialize();

    const client = await this.options.pool.connect();

    try {
      await client.query('BEGIN');
      const locked = await client.query<
        Pick<
          SettlementQueueJob,
          | 'id'
          | 'generation'
          | 'current_physical_job_id'
          | 'current_execution_token'
          | 'status'
        >
      >({
        text: `
          SELECT id, generation, current_physical_job_id,
            current_execution_token, status
          FROM ${this.options.schema}.queue_job
          WHERE id = $1
          FOR UPDATE
        `,
        values: [args.logicalJobId],
      });
      const job = locked.rows[0];

      if (
        !job ||
        job.id !== args.logicalJobId ||
        job.generation !== args.generation ||
        job.current_physical_job_id !== args.physicalJobId ||
        job.current_execution_token !== args.executionToken ||
        job.status !== 'active'
      ) {
        await client.query('COMMIT');

        return 'fenced';
      }

      const attempt = await client.query<{ id: string }>({
        text: `
          UPDATE ${this.options.schema}.queue_job_attempt
          SET outcome = 'completed', finished_at = now()
          WHERE job_id = $1
            AND execution_token = $3
            AND outcome = 'running'
            AND EXISTS (
              SELECT 1
              FROM ${this.options.schema}.queue_job
              WHERE id = $1
                AND generation = $2
                AND current_execution_token = $3
                AND status = 'active'
                AND current_physical_job_id = $4
            )
          RETURNING id
        `,
        values: [
          args.logicalJobId,
          args.generation,
          args.executionToken,
          args.physicalJobId,
        ],
      });

      if (!attempt.rows[0]) {
        await client.query('ROLLBACK');

        return 'fenced';
      }

      const settled = await client.query<{ id: string }>({
        text: `
          UPDATE ${this.options.schema}.queue_job
          SET status = 'completed',
            current_execution_token = NULL,
            failure_kind = NULL,
            last_error = NULL,
            completed_at = now(),
            updated_at = now()
          WHERE id = $1
            AND generation = $2
            AND current_execution_token = $3
            AND status = 'active'
            AND current_physical_job_id = $4
          RETURNING id
        `,
        values: [
          args.logicalJobId,
          args.generation,
          args.executionToken,
          args.physicalJobId,
        ],
      });

      if (!settled.rows[0]) {
        await client.query('ROLLBACK');

        return 'fenced';
      }

      await this.completeFencedEnvelopeWithClient(client, args);
      await client.query('COMMIT');

      return 'settled';
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async settleFailure(
    args: LogicalSettlementArgs,
    error: unknown,
  ): Promise<'retried' | 'failed' | 'fenced'> {
    await this.initialize();

    const client = await this.options.pool.connect();

    try {
      await client.query('BEGIN');
      const locked = await client.query<SettlementQueueJob>({
        text: `
          SELECT id, generation, current_physical_job_id,
            current_execution_token, status, handler_failure_count,
            handler_retry_limit, stall_count, stall_recovery_limit, priority
          FROM ${this.options.schema}.queue_job
          WHERE id = $1
          FOR UPDATE
        `,
        values: [args.logicalJobId],
      });
      const job = locked.rows[0];

      if (!this.matchesSettlementFence(job, args)) {
        await client.query('COMMIT');

        return 'fenced';
      }

      const sanitizedError = this.sanitizeError(error);
      const failureCount = job.handler_failure_count + 1;

      const attempt = await client.query<{ id: string }>({
        text: `
          UPDATE ${this.options.schema}.queue_job_attempt
          SET outcome = 'handler_failed', finished_at = now(), error = $3::jsonb
          WHERE job_id = $1
            AND execution_token = $2
            AND outcome = 'running'
            AND EXISTS (
              SELECT 1
              FROM ${this.options.schema}.queue_job
              WHERE id = $1
                AND generation = $4
                AND current_execution_token = $2
                AND status = 'active'
            )
          RETURNING id
        `,
        values: [
          args.logicalJobId,
          args.executionToken,
          sanitizedError,
          args.generation,
        ],
      });

      if (!attempt.rows[0]) {
        await client.query('ROLLBACK');

        return 'fenced';
      }

      if (failureCount <= job.handler_retry_limit) {
        const nextGeneration = job.generation + 1;
        const nextPhysicalJobId = this.physicalJobId(
          args.logicalJobId,
          nextGeneration,
        );
        const remainingStallAllowance = Math.max(
          0,
          job.stall_recovery_limit - job.stall_count,
        );
        const availableAt = new Date();
        const transitioned = await client.query<{ id: string }>({
          text: `
            UPDATE ${this.options.schema}.queue_job
            SET status = 'retry_wait',
              generation = $4,
              handler_failure_count = $5,
              stall_count = $6,
              transport_retry_count = 0,
              current_physical_job_id = $7,
              current_execution_token = NULL,
              last_error = $8::jsonb,
              available_at = $9,
              updated_at = now()
            WHERE id = $1
              AND generation = $2
              AND current_execution_token = $3
              AND status = 'active'
            RETURNING id
          `,
          values: [
            args.logicalJobId,
            args.generation,
            args.executionToken,
            nextGeneration,
            failureCount,
            job.stall_count,
            nextPhysicalJobId,
            sanitizedError,
            availableAt,
          ],
        });

        if (!transitioned.rows[0]) {
          await client.query('ROLLBACK');

          return 'fenced';
        }

        await this.options.transport.send({
          queueName: args.queueName,
          envelope: {
            version: 2,
            logicalJobId: args.logicalJobId,
            generation: nextGeneration,
          },
          physicalJobId: nextPhysicalJobId,
          stallRecoveryLimit: remainingStallAllowance,
          priority: job.priority,
          availableAt,
          db: this.toPgBossDatabase(client),
        });
        await this.completeFencedEnvelopeWithClient(client, args);
        await client.query('COMMIT');

        return 'retried';
      }

      const transitioned = await client.query<{ id: string }>({
        text: `
          UPDATE ${this.options.schema}.queue_job
          SET status = 'failed',
            handler_failure_count = $4,
            current_execution_token = NULL,
            failure_kind = 'handler_exhausted',
            last_error = $5::jsonb,
            failed_at = now(),
            updated_at = now()
          WHERE id = $1
            AND generation = $2
            AND current_execution_token = $3
            AND status = 'active'
          RETURNING id
        `,
        values: [
          args.logicalJobId,
          args.generation,
          args.executionToken,
          failureCount,
          sanitizedError,
        ],
      });

      if (!transitioned.rows[0]) {
        await client.query('ROLLBACK');

        return 'fenced';
      }

      await this.completeFencedEnvelopeWithClient(client, args);
      await client.query('COMMIT');

      return 'failed';
    } catch (caughtError) {
      await client.query('ROLLBACK');
      throw caughtError;
    } finally {
      client.release();
    }
  }

  async getStats(queueName: MessageQueue): Promise<MessageQueueStats> {
    await this.initialize();

    const result = await this.options.pool.query<{
      status: string;
      count: string;
    }>({
      text: `
        SELECT status, count(*)::text AS count
        FROM ${this.options.schema}.queue_job
        WHERE queue_name = $1
        GROUP BY status
      `,
      values: [queueName],
    });
    const counts = {
      queued: 0,
      active: 0,
      retry_wait: 0,
      completed: 0,
      failed: 0,
      cancelled: 0,
    };

    for (const row of result.rows) {
      if (row.status in counts) {
        counts[row.status as keyof typeof counts] = Number(row.count);
      }
    }

    return {
      queueName,
      created: counts.queued,
      active: counts.active,
      retry: counts.retry_wait,
      completed: counts.completed,
      failed: counts.failed + counts.cancelled,
      healthy: true,
    };
  }

  async findJobs(
    queueName: MessageQueue,
    states: MessageQueueJobState[],
  ): Promise<MessageQueueJobRecord[]> {
    await this.initialize();

    const canonicalStates = [
      ...new Set(states.flatMap((state) => this.toCanonicalStates(state))),
    ];

    if (canonicalStates.length === 0) {
      return [];
    }

    const result = await this.options.pool.query<QueueJobInspectionRow>({
      text: `
        SELECT id, job_name, payload, status, started_count, created_at,
          updated_at, completed_at, failed_at, last_error
        FROM ${this.options.schema}.queue_job
        WHERE queue_name = $1
          AND status = ANY($2::text[])
        ORDER BY created_at DESC
      `,
      values: [queueName, canonicalStates],
    });

    return result.rows.map((row) => {
      const state = this.toNeutralState(row.status);
      const finishedAt = row.completed_at ?? row.failed_at;
      const record: MessageQueueJobRecord = {
        id: row.id,
        name: row.job_name,
        data: row.payload,
        state,
        attemptsMade: row.started_count,
        createdAt: row.created_at.getTime(),
      };

      if (row.started_count > 0) {
        record.processedAt = row.updated_at.getTime();
      }
      if (finishedAt) {
        record.finishedAt = finishedAt.getTime();
      }
      if (state === 'failed' && row.last_error?.message) {
        record.failedReason = row.last_error.message;
      }

      return record;
    });
  }

  async reconcileDeadLetter(
    args: LogicalDeadLetterArgs,
  ): Promise<'failed' | 'fenced'> {
    await this.initialize();

    const client = await this.options.pool.connect();

    try {
      await client.query('BEGIN');
      const locked = await client.query<
        Pick<
          SettlementQueueJob,
          | 'id'
          | 'generation'
          | 'current_physical_job_id'
          | 'current_execution_token'
          | 'status'
        >
      >({
        text: `
          SELECT id, generation, current_physical_job_id,
            current_execution_token, status
          FROM ${this.options.schema}.queue_job
          WHERE id = $1
          FOR UPDATE
        `,
        values: [args.envelope.logicalJobId],
      });
      const job = locked.rows[0];

      if (
        !job ||
        job.id !== args.envelope.logicalJobId ||
        job.generation !== args.envelope.generation ||
        job.current_physical_job_id !== args.physicalJobId
      ) {
        await client.query('COMMIT');

        return 'fenced';
      }

      if (
        ['queued', 'retry_wait'].includes(job.status) &&
        job.current_execution_token === null
      ) {
        const failed = await client.query<{ id: string }>({
          text: `
            UPDATE ${this.options.schema}.queue_job
            SET status = 'failed',
              current_execution_token = NULL,
              failure_kind = 'stall_exhausted',
              failed_at = now(),
              updated_at = now()
            WHERE id = $1
              AND generation = $2
              AND current_physical_job_id = $3
              AND current_execution_token IS NULL
              AND status IN ('queued', 'retry_wait')
            RETURNING id
          `,
          values: [job.id, job.generation, args.physicalJobId],
        });

        if (!failed.rows[0]) {
          await client.query('ROLLBACK');

          return 'fenced';
        }

        const syntheticExecutionToken = v5(
          `${args.physicalJobId}:dead-letter-execution`,
          PHYSICAL_JOB_NAMESPACE,
        );

        await client.query({
          text: `
            INSERT INTO ${this.options.schema}.queue_job_attempt (
              id, job_id, generation, physical_job_id, worker_instance_id,
              execution_token, transport_retry_count, started_at, finished_at,
              outcome
            ) VALUES ($1, $2, $3, $4, $5, $6, 0, now(), now(), 'stalled')
            ON CONFLICT (job_id, execution_token) DO NOTHING
          `,
          values: [
            v5(
              `${args.physicalJobId}:dead-letter-attempt`,
              PHYSICAL_JOB_NAMESPACE,
            ),
            job.id,
            job.generation,
            args.physicalJobId,
            v5('dead-letter-reconciler', PHYSICAL_JOB_NAMESPACE),
            syntheticExecutionToken,
          ],
        });
        await client.query('COMMIT');

        return 'failed';
      }

      if (job.current_execution_token === null || job.status !== 'active') {
        await client.query('COMMIT');

        return 'fenced';
      }

      const attempt = await client.query<{ id: string }>({
        text: `
          UPDATE ${this.options.schema}.queue_job_attempt
          SET outcome = 'stalled', finished_at = now()
          WHERE job_id = $1
            AND generation = $2
            AND physical_job_id = $3
            AND execution_token = $4
            AND outcome = 'running'
            AND EXISTS (
              SELECT 1
              FROM ${this.options.schema}.queue_job
              WHERE id = $1
                AND generation = $2
                AND current_execution_token = $4
                AND status = 'active'
            )
          RETURNING id
        `,
        values: [
          job.id,
          job.generation,
          args.physicalJobId,
          job.current_execution_token,
        ],
      });

      if (!attempt.rows[0]) {
        await client.query('ROLLBACK');

        return 'fenced';
      }

      const failed = await client.query<{ id: string }>({
        text: `
          UPDATE ${this.options.schema}.queue_job
          SET status = 'failed',
            current_execution_token = NULL,
            failure_kind = 'stall_exhausted',
            failed_at = now(),
            updated_at = now()
          WHERE id = $1
            AND generation = $2
            AND current_execution_token = $3
            AND status = 'active'
            AND current_physical_job_id = $4
          RETURNING id
        `,
        values: [
          job.id,
          job.generation,
          job.current_execution_token,
          args.physicalJobId,
        ],
      });

      if (!failed.rows[0]) {
        await client.query('ROLLBACK');

        return 'fenced';
      }

      await client.query('COMMIT');

      return 'failed';
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  physicalJobId(logicalJobId: string, generation: number): string {
    return v5(`${logicalJobId}:${generation}`, PHYSICAL_JOB_NAMESPACE);
  }

  private async loadReadyQueuePolicy(
    client: PoolClient,
    queueName: MessageQueue,
  ): Promise<PersistedQueuePolicy> {
    const result = await client.query<PersistedQueuePolicy>({
      text: `
        SELECT stall_recovery_limit, heartbeat_seconds,
          worker_ready_at > '-infinity'::timestamptz AS worker_ready
        FROM ${this.options.schema}.queue_policy
        WHERE queue_name = $1
        FOR UPDATE
      `,
      values: [queueName],
    });

    if (!result.rows[0]?.worker_ready) {
      throw new Error(
        'logical queue worker is not ready; register workers before producing jobs',
      );
    }

    return result.rows[0];
  }

  private toPgBossDatabase(client: PoolClient): PgBossDatabase {
    return {
      executeSql: async (text, values) => client.query(text, values),
    };
  }

  private async completeFencedEnvelopeWithClient(
    client: PoolClient,
    args: LogicalPhysicalArgs,
  ): Promise<void> {
    await this.options.transport.complete({
      queueName: args.queueName,
      physicalJobId: args.physicalJobId,
      db: this.toPgBossDatabase(client),
    });
  }

  private matchesSettlementFence(
    job: SettlementQueueJob | undefined,
    args: LogicalSettlementArgs,
  ): job is SettlementQueueJob {
    return (
      job !== undefined &&
      job.id === args.logicalJobId &&
      job.generation === args.generation &&
      job.current_physical_job_id === args.physicalJobId &&
      job.current_execution_token === args.executionToken &&
      job.status === 'active'
    );
  }

  private sanitizeError(error: unknown): { name: string; message: string } {
    if (error instanceof Error) {
      return { name: error.name, message: error.message };
    }

    return {
      name: 'Error',
      message: typeof error === 'string' ? error : 'Unknown error',
    };
  }

  private toCanonicalStates(state: MessageQueueJobState): string[] {
    switch (state) {
      case 'created':
        return ['queued'];
      case 'active':
        return ['active'];
      case 'retry':
        return ['retry_wait'];
      case 'completed':
        return ['completed'];
      case 'failed':
        return ['failed', 'cancelled'];
    }
  }

  private toNeutralState(status: string): MessageQueueJobState {
    switch (status) {
      case 'queued':
        return 'created';
      case 'active':
        return 'active';
      case 'retry_wait':
        return 'retry';
      case 'completed':
        return 'completed';
      case 'failed':
      case 'cancelled':
        return 'failed';
      default:
        throw new Error(`unknown logical queue job state: ${status}`);
    }
  }

  private isWaitingDeduplicationConflict(
    error: unknown,
  ): error is PostgreSqlError {
    const postgresError = error as PostgreSqlError;

    return (
      postgresError?.code === '23505' &&
      postgresError.constraint === 'queue_job_waiting_dedup_key_idx'
    );
  }

  private async initializeSchema(): Promise<void> {
    const client = await this.options.pool.connect();

    try {
      await client.query('BEGIN');
      await client.query(`
        CREATE TABLE IF NOT EXISTS ${this.options.schema}.queue_policy (
          queue_name text PRIMARY KEY,
          stall_recovery_limit integer NOT NULL CHECK (stall_recovery_limit >= 0),
          heartbeat_seconds integer CHECK (heartbeat_seconds IS NULL OR heartbeat_seconds >= 0),
          worker_ready_at timestamptz NOT NULL DEFAULT '-infinity'::timestamptz,
          updated_at timestamptz NOT NULL DEFAULT now()
        )
      `);
      await client.query(`
        ALTER TABLE ${this.options.schema}.queue_policy
        ADD COLUMN IF NOT EXISTS worker_ready_at timestamptz
          NOT NULL DEFAULT '-infinity'::timestamptz
      `);
      await client.query(`
        CREATE TABLE IF NOT EXISTS ${this.options.schema}.queue_job (
          id uuid PRIMARY KEY,
          queue_name text NOT NULL,
          job_name text NOT NULL,
          payload jsonb NOT NULL,
          payload_version integer NOT NULL,
          status text NOT NULL CHECK (
            status IN ('queued', 'active', 'retry_wait', 'completed', 'failed', 'cancelled')
          ),
          generation integer NOT NULL CHECK (generation >= 0),
          handler_failure_count integer NOT NULL CHECK (handler_failure_count >= 0),
          handler_retry_limit integer NOT NULL CHECK (handler_retry_limit >= 0),
          stall_count integer NOT NULL CHECK (stall_count >= 0),
          stall_recovery_limit integer NOT NULL CHECK (stall_recovery_limit >= 0),
          started_count integer NOT NULL CHECK (started_count >= 0),
          transport_retry_count integer NOT NULL CHECK (transport_retry_count >= 0),
          priority integer NOT NULL,
          available_at timestamptz NOT NULL,
          dedup_key text,
          current_physical_job_id uuid NOT NULL,
          current_execution_token uuid,
          failure_kind text,
          last_error jsonb,
          created_at timestamptz NOT NULL DEFAULT now(),
          updated_at timestamptz NOT NULL DEFAULT now(),
          completed_at timestamptz,
          failed_at timestamptz
        )
      `);
      await client.query(`
        CREATE TABLE IF NOT EXISTS ${this.options.schema}.queue_job_attempt (
          id uuid PRIMARY KEY,
          job_id uuid NOT NULL REFERENCES ${this.options.schema}.queue_job(id) ON DELETE CASCADE,
          generation integer NOT NULL CHECK (generation >= 0),
          physical_job_id uuid NOT NULL,
          worker_instance_id uuid NOT NULL,
          execution_token uuid NOT NULL,
          transport_retry_count integer NOT NULL CHECK (transport_retry_count >= 0),
          started_at timestamptz NOT NULL,
          finished_at timestamptz,
          outcome text NOT NULL CHECK (
            outcome IN ('running', 'completed', 'handler_failed', 'stalled', 'fenced', 'cancelled')
          ),
          error jsonb,
          UNIQUE (job_id, execution_token)
        )
      `);
      await client.query(`
        CREATE UNIQUE INDEX IF NOT EXISTS queue_job_waiting_dedup_key_idx
        ON ${this.options.schema}.queue_job (queue_name, dedup_key)
        WHERE dedup_key IS NOT NULL
          AND status IN ('queued', 'retry_wait')
      `);
      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }
}

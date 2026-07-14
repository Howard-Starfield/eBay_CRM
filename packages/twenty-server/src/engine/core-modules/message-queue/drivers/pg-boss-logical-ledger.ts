import { type Pool, type PoolClient } from 'pg';
import { type Db as PgBossDatabase } from 'pg-boss';
import { v4, v5 } from 'uuid';

import { type QueueJobOptions } from 'src/engine/core-modules/message-queue/drivers/interfaces/job-options.interface';
import { type MessageQueueJobData } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueueWorkerOptions } from 'src/engine/core-modules/message-queue/interfaces/message-queue-worker-options.interface';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

import {
  type LogicalQueuePolicy,
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
          (queue_name, stall_recovery_limit, heartbeat_seconds)
        VALUES ($1, $2, $3)
        ON CONFLICT (queue_name) DO UPDATE SET
          stall_recovery_limit = EXCLUDED.stall_recovery_limit,
          heartbeat_seconds = EXCLUDED.heartbeat_seconds,
          updated_at = now()
      `,
      values: [queueName, stallRecoveryLimit, heartbeatSeconds ?? null],
    });

    return { stallRecoveryLimit, heartbeatSeconds };
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

      const policy = await this.loadOrCreateQueuePolicy(client, args.queueName);

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

  physicalJobId(logicalJobId: string, generation: number): string {
    return v5(`${logicalJobId}:${generation}`, PHYSICAL_JOB_NAMESPACE);
  }

  private async loadOrCreateQueuePolicy(
    client: PoolClient,
    queueName: MessageQueue,
  ): Promise<PersistedQueuePolicy> {
    const result = await client.query<PersistedQueuePolicy>({
      text: `
        SELECT stall_recovery_limit, heartbeat_seconds
        FROM ${this.options.schema}.queue_policy
        WHERE queue_name = $1
        FOR UPDATE
      `,
      values: [queueName],
    });

    if (result.rows[0]) {
      return result.rows[0];
    }

    const inserted = await client.query<PersistedQueuePolicy>({
      text: `
        INSERT INTO ${this.options.schema}.queue_policy
          (queue_name, stall_recovery_limit, heartbeat_seconds)
        VALUES ($1, $2, $3)
        ON CONFLICT (queue_name) DO UPDATE SET
          queue_name = EXCLUDED.queue_name
        RETURNING stall_recovery_limit, heartbeat_seconds
      `,
      values: [queueName, 1, null],
    });

    return (
      inserted.rows[0] ?? {
        stall_recovery_limit: 1,
        heartbeat_seconds: null,
      }
    );
  }

  private toPgBossDatabase(client: PoolClient): PgBossDatabase {
    return {
      executeSql: async (text, values) => client.query(text, values),
    };
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
          updated_at timestamptz NOT NULL DEFAULT now()
        )
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

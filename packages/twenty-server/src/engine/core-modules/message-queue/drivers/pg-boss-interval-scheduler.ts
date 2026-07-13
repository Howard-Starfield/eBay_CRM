import { CronExpressionParser } from 'cron-parser';
import { type Pool, type PoolClient } from 'pg';
import { type Db as PgBossDatabase } from 'pg-boss';

import { type MessageQueueJobData } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

export type PgBossIntervalSchedule = {
  scheduleKey: string;
  queueName: MessageQueue;
  jobName: string;
  payload: {
    version: 1;
    jobName: string;
    data: MessageQueueJobData | undefined;
    intervalOptions?: {
      priority?: number;
      retryLimit?: number;
    };
  };
  everyMs: number;
  remainingRuns?: number;
};

export type PgBossCronSchedule = {
  scheduleKey: string;
  queueName: MessageQueue;
  jobName: string;
  payload: PgBossIntervalSchedule['payload'];
  pattern: string;
  remainingRuns?: number;
};

type PersistedIntervalSchedule = {
  schedule_key: string;
  queue_name: MessageQueue;
  job_name: string;
  payload: PgBossIntervalSchedule['payload'];
  every_ms: number;
  remaining_runs: number | null;
  next_run_at: Date;
};

type PersistedCronSchedule = {
  schedule_key: string;
  queue_name: MessageQueue;
  job_name: string;
  payload: PgBossCronSchedule['payload'];
  cron_pattern: string;
  remaining_runs: number | null;
  next_run_at: Date;
};

type PgBossIntervalSchedulerOptions = {
  pool: Pool;
  schema: 'desktop_runtime';
  pollIntervalMs?: number;
  shutdownTimeoutMs?: number;
  enqueue: (
    schedule: {
      queueName: MessageQueue;
      payload: PgBossIntervalSchedule['payload'];
    },
    database: PgBossDatabase,
  ) => Promise<void>;
  onError?: (error: unknown) => void;
};

const DEFAULT_POLL_INTERVAL_MS = 1_000;
const DEFAULT_SHUTDOWN_TIMEOUT_MS = 1_000;
const MAX_CLAIMS_PER_POLL = 100;

export class PgBossIntervalScheduler {
  private readonly tableName: string;
  private readonly pollIntervalMs: number;
  private readonly shutdownTimeoutMs: number;
  private initializePromise?: Promise<void>;
  private pollTimer?: ReturnType<typeof setTimeout>;
  private pollPromise?: Promise<void>;
  private activeClient?: PoolClient;
  private activeClientReleased = false;
  private running = false;

  constructor(private readonly options: PgBossIntervalSchedulerOptions) {
    this.tableName = `${options.schema}.interval_schedule`;
    this.pollIntervalMs = options.pollIntervalMs ?? DEFAULT_POLL_INTERVAL_MS;
    this.shutdownTimeoutMs =
      options.shutdownTimeoutMs ?? DEFAULT_SHUTDOWN_TIMEOUT_MS;
  }

  async start(): Promise<void> {
    await this.initialize();

    if (this.running) {
      return;
    }

    this.running = true;
    this.scheduleNextPoll(0);
  }

  async stop(): Promise<void> {
    this.running = false;

    if (this.pollTimer) {
      clearTimeout(this.pollTimer);
      this.pollTimer = undefined;
    }

    if (!this.pollPromise) {
      return;
    }

    const completedGracefully = await this.waitForPoll(this.shutdownTimeoutMs);

    if (completedGracefully) {
      return;
    }

    if (this.activeClient) {
      this.releaseClient(this.activeClient, true);
    }

    await this.waitForPoll(this.shutdownTimeoutMs);
  }

  async upsert(schedule: PgBossIntervalSchedule): Promise<void> {
    if (!Number.isInteger(schedule.everyMs) || schedule.everyMs <= 0) {
      throw new Error('Interval schedule everyMs must be a positive integer');
    }

    if (
      schedule.remainingRuns !== undefined &&
      (!Number.isInteger(schedule.remainingRuns) || schedule.remainingRuns <= 0)
    ) {
      throw new Error(
        'Interval schedule remainingRuns must be a positive integer',
      );
    }

    await this.initialize();
    await this.options.pool.query(
      `
        INSERT INTO ${this.tableName}
          (schedule_key, queue_name, job_name, payload, every_ms, remaining_runs, next_run_at)
        VALUES
          ($1, $2, $3, $4::jsonb, $5::integer, $6::integer, now() + ($5::integer * interval '1 millisecond'))
        ON CONFLICT (schedule_key) DO UPDATE SET
          queue_name = EXCLUDED.queue_name,
          job_name = EXCLUDED.job_name,
          payload = EXCLUDED.payload,
          every_ms = EXCLUDED.every_ms,
          remaining_runs = EXCLUDED.remaining_runs,
          next_run_at = now() + (EXCLUDED.every_ms * interval '1 millisecond'),
          updated_at = now()
        WHERE ${this.tableName}.queue_name IS DISTINCT FROM EXCLUDED.queue_name
           OR ${this.tableName}.job_name IS DISTINCT FROM EXCLUDED.job_name
           OR ${this.tableName}.payload IS DISTINCT FROM EXCLUDED.payload
           OR ${this.tableName}.every_ms IS DISTINCT FROM EXCLUDED.every_ms
           OR ${this.tableName}.remaining_runs IS DISTINCT FROM EXCLUDED.remaining_runs
      `,
      [
        schedule.scheduleKey,
        schedule.queueName,
        schedule.jobName,
        schedule.payload,
        schedule.everyMs,
        schedule.remainingRuns ?? null,
      ],
    );
  }

  async remove(scheduleKey: string): Promise<void> {
    await this.initialize();
    await this.options.pool.query(
      `DELETE FROM ${this.tableName} WHERE schedule_key = $1`,
      [scheduleKey],
    );
  }

  async upsertCron(schedule: PgBossCronSchedule): Promise<void> {
    if (
      schedule.remainingRuns !== undefined &&
      (!Number.isInteger(schedule.remainingRuns) || schedule.remainingRuns <= 0)
    ) {
      throw new Error('Cron schedule remainingRuns must be a positive integer');
    }

    const nextRunAt = this.nextCronRun(schedule.pattern, new Date());

    await this.initialize();
    await this.options.pool.query(
      `
        INSERT INTO ${this.options.schema}.cron_schedule
          (schedule_key, queue_name, job_name, payload, cron_pattern, remaining_runs, next_run_at)
        VALUES
          ($1, $2, $3, $4::jsonb, $5, $6::integer, $7)
        ON CONFLICT (schedule_key) DO UPDATE SET
          queue_name = EXCLUDED.queue_name,
          job_name = EXCLUDED.job_name,
          payload = EXCLUDED.payload,
          cron_pattern = EXCLUDED.cron_pattern,
          remaining_runs = EXCLUDED.remaining_runs,
          next_run_at = EXCLUDED.next_run_at,
          updated_at = now()
        WHERE ${this.options.schema}.cron_schedule.queue_name IS DISTINCT FROM EXCLUDED.queue_name
           OR ${this.options.schema}.cron_schedule.job_name IS DISTINCT FROM EXCLUDED.job_name
           OR ${this.options.schema}.cron_schedule.payload IS DISTINCT FROM EXCLUDED.payload
           OR ${this.options.schema}.cron_schedule.cron_pattern IS DISTINCT FROM EXCLUDED.cron_pattern
           OR ${this.options.schema}.cron_schedule.remaining_runs IS DISTINCT FROM EXCLUDED.remaining_runs
      `,
      [
        schedule.scheduleKey,
        schedule.queueName,
        schedule.jobName,
        schedule.payload,
        schedule.pattern,
        schedule.remainingRuns ?? null,
        nextRunAt,
      ],
    );
  }

  async removeCron(scheduleKey: string): Promise<void> {
    await this.initialize();
    await this.options.pool.query(
      `DELETE FROM ${this.options.schema}.cron_schedule WHERE schedule_key = $1`,
      [scheduleKey],
    );
  }

  private async initialize(): Promise<void> {
    if (!this.initializePromise) {
      this.initializePromise = this.options.pool
        .query(
          `
            CREATE TABLE IF NOT EXISTS ${this.tableName} (
              schedule_key text PRIMARY KEY,
              queue_name text NOT NULL,
              job_name text NOT NULL,
              payload jsonb,
              every_ms integer NOT NULL CHECK (every_ms > 0),
              remaining_runs integer,
              next_run_at timestamptz NOT NULL,
              updated_at timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS ${this.options.schema}.cron_schedule (
              schedule_key text PRIMARY KEY,
              queue_name text NOT NULL,
              job_name text NOT NULL,
              payload jsonb NOT NULL,
              cron_pattern text NOT NULL,
              remaining_runs integer,
              next_run_at timestamptz NOT NULL,
              updated_at timestamptz NOT NULL DEFAULT now()
            );
          `,
        )
        .then(() => undefined)
        .catch((error) => {
          this.initializePromise = undefined;
          throw error;
        });
    }

    await this.initializePromise;
  }

  private scheduleNextPoll(delayMs: number): void {
    if (!this.running) {
      return;
    }

    this.pollTimer = setTimeout(() => {
      this.pollTimer = undefined;
      this.pollPromise = this.pollDueSchedules()
        .catch((error) => this.options.onError?.(error))
        .finally(() => {
          this.pollPromise = undefined;
          this.scheduleNextPoll(this.pollIntervalMs);
        });
    }, delayMs);
    this.pollTimer.unref?.();
  }

  private async pollDueSchedules(): Promise<void> {
    const client = await this.options.pool.connect();

    this.activeClient = client;
    this.activeClientReleased = false;

    try {
      await client.query('BEGIN');
      const result = await client.query<PersistedIntervalSchedule>(`
        SELECT
          schedule_key,
          queue_name,
          job_name,
          payload,
          every_ms,
          remaining_runs,
          next_run_at
        FROM ${this.tableName} schedule
        INNER JOIN ${this.options.schema}.queue queue
          ON queue.name = schedule.queue_name
        WHERE schedule.next_run_at <= now()
        ORDER BY schedule.next_run_at, schedule.schedule_key
        LIMIT ${MAX_CLAIMS_PER_POLL}
        FOR UPDATE SKIP LOCKED
      `);
      const cronResult = await client.query<PersistedCronSchedule>(`
        SELECT
          schedule_key,
          queue_name,
          job_name,
          payload,
          cron_pattern,
          remaining_runs,
          next_run_at
        FROM ${this.options.schema}.cron_schedule schedule
        INNER JOIN ${this.options.schema}.queue queue
          ON queue.name = schedule.queue_name
        WHERE schedule.next_run_at <= now()
        ORDER BY schedule.next_run_at, schedule.schedule_key
        LIMIT ${MAX_CLAIMS_PER_POLL}
        FOR UPDATE SKIP LOCKED
      `);

      const database = this.toPgBossDatabase(client);

      for (const schedule of result.rows) {
        await this.options.enqueue(
          {
            queueName: schedule.queue_name,
            payload: schedule.payload,
          },
          database,
        );

        await client.query(
          `
            UPDATE ${this.tableName}
            SET
              next_run_at = next_run_at + (every_ms * interval '1 millisecond'),
              remaining_runs = CASE
                WHEN remaining_runs IS NULL THEN NULL
                ELSE remaining_runs - 1
              END,
              updated_at = now()
            WHERE schedule_key = $1
          `,
          [schedule.schedule_key],
        );

        if (schedule.remaining_runs === 1) {
          await client.query(
            `DELETE FROM ${this.tableName} WHERE schedule_key = $1`,
            [schedule.schedule_key],
          );
        }
      }

      for (const schedule of cronResult.rows) {
        await this.options.enqueue(
          {
            queueName: schedule.queue_name,
            payload: schedule.payload,
          },
          database,
        );

        const nextRunAt = this.nextCronRun(
          schedule.cron_pattern,
          schedule.next_run_at,
        );

        await client.query(
          `
            UPDATE ${this.options.schema}.cron_schedule
            SET
              next_run_at = $2,
              remaining_runs = CASE
                WHEN remaining_runs IS NULL THEN NULL
                ELSE remaining_runs - 1
              END,
              updated_at = now()
            WHERE schedule_key = $1
          `,
          [schedule.schedule_key, nextRunAt],
        );

        if (schedule.remaining_runs === 1) {
          await client.query(
            `DELETE FROM ${this.options.schema}.cron_schedule WHERE schedule_key = $1`,
            [schedule.schedule_key],
          );
        }
      }

      await client.query('COMMIT');
    } catch (error) {
      if (this.activeClient === client && !this.activeClientReleased) {
        try {
          await client.query('ROLLBACK');
        } finally {
          this.releaseClient(client);
        }
      }

      throw error;
    }

    this.releaseClient(client);
    if (this.activeClient === client) {
      this.activeClient = undefined;
    }
  }

  private nextCronRun(pattern: string, currentDate: Date): Date {
    return CronExpressionParser.parse(pattern, { currentDate }).next().toDate();
  }

  private async waitForPoll(timeoutMs: number): Promise<boolean> {
    if (!this.pollPromise) {
      return true;
    }

    return Promise.race([
      this.pollPromise.then(() => true),
      new Promise<false>((resolve) => {
        setTimeout(() => resolve(false), timeoutMs);
      }),
    ]);
  }

  private releaseClient(client: PoolClient, destroy = false): void {
    if (this.activeClient !== client || this.activeClientReleased) {
      return;
    }

    this.activeClientReleased = true;
    client.release(destroy);
    if (this.activeClient === client) {
      this.activeClient = undefined;
    }
  }

  private toPgBossDatabase(client: PoolClient): PgBossDatabase {
    return {
      executeSql: async (text, values) => client.query(text, values),
    };
  }
}

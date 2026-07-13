import {
  Logger,
  type OnModuleDestroy,
  type OnModuleInit,
} from '@nestjs/common';

import { Pool, type PoolClient } from 'pg';
import {
  type Db as PgBossDatabase,
  PgBoss,
  type JobWithMetadata as PgBossJobWithMetadata,
  type SendOptions as PgBossSendOptions,
} from 'pg-boss';

import { QUEUE_RETENTION } from 'src/engine/core-modules/message-queue/constants/queue-retention.constants';
import {
  type QueueCronJobOptions,
  type QueueJobOptions,
} from 'src/engine/core-modules/message-queue/drivers/interfaces/job-options.interface';
import { type MessageQueueDriver } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface';
import { PgBossIntervalScheduler } from 'src/engine/core-modules/message-queue/drivers/pg-boss-interval-scheduler';
import { type MessageQueueJobRecord } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-record.type';
import { type MessageQueueJobState } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-state.type';
import { type MessageQueueStats } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-stats.type';
import {
  type MessageQueueJob,
  type MessageQueueJobData,
} from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueueWorkerOptions } from 'src/engine/core-modules/message-queue/interfaces/message-queue-worker-options.interface';
import { MESSAGE_QUEUE_PRIORITY } from 'src/engine/core-modules/message-queue/message-queue-priority.constant';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';
import { getJobKey } from 'src/engine/core-modules/message-queue/utils/get-job-key.util';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

export type PgBossDriverOptions = {
  connectionString: string;
  schema: 'desktop_runtime';
  applicationName: string;
  intervalPollMs?: number;
};

type PgBossJobEnvelope<T extends MessageQueueJobData = MessageQueueJobData> = {
  version: 1;
  jobName: string;
  logicalId?: string;
  cronScheduleKey?: string;
  intervalOptions?: {
    priority: number;
    retryLimit: number;
  };
  data: T;
};

type InspectablePgBossJob = PgBossJobWithMetadata<PgBossJobEnvelope>;

const RETENTION_CLEANUP_INTERVAL_MS = 60_000;

export class PgBossDriver
  implements MessageQueueDriver, OnModuleInit, OnModuleDestroy
{
  private readonly logger = new Logger(PgBossDriver.name);
  private readonly boss: PgBoss;
  private readonly coordinationPool: Pool;
  private readonly intervalScheduler: PgBossIntervalScheduler;
  private readonly registeredQueues = new Set<MessageQueue>();
  private readonly queueCreationPromises = new Map<
    MessageQueue,
    Promise<void>
  >();
  private readonly workerOptions = new Map<
    MessageQueue,
    MessageQueueWorkerOptions
  >();
  private startPromise?: Promise<void>;
  private cronLimitInitializePromise?: Promise<void>;
  private stopPromise?: Promise<void>;
  private started = false;
  private boundedShutdownDrain = false;
  private retentionCleanupTimer?: ReturnType<typeof setInterval>;
  private retentionCleanupPromise?: Promise<void>;
  private coordinationPoolClosed = false;
  private destroying = false;

  constructor(
    private readonly options: PgBossDriverOptions,
    _metricsService: MetricsService,
    private readonly twentyConfigService: TwentyConfigService,
  ) {
    this.boss = new PgBoss({
      connectionString: options.connectionString,
      schema: options.schema,
      application_name: options.applicationName,
      cronMonitorIntervalSeconds: 1,
      cronWorkerIntervalSeconds: 1,
      superviseIntervalSeconds: 1,
    });
    this.coordinationPool = new Pool({
      connectionString: options.connectionString,
      application_name: `${options.applicationName}-coordination`,
    });
    this.intervalScheduler = new PgBossIntervalScheduler({
      pool: this.coordinationPool,
      schema: options.schema,
      ...(options.intervalPollMs === undefined
        ? {}
        : { pollIntervalMs: options.intervalPollMs }),
      enqueue: async ({ queueName, payload }, database) => {
        const { intervalOptions, ...envelope } = payload;

        await this.boss.send(queueName, envelope, {
          db: database,
          priority: intervalOptions?.priority ?? 0,
          retryLimit: intervalOptions?.retryLimit ?? 0,
          deleteAfterSeconds: QUEUE_RETENTION.failedMaxAge,
        });
      },
      onError: (error) => {
        this.logger.error('Failed to enqueue due interval schedules', error);
      },
    });
  }

  register(queueName: MessageQueue): void {
    this.registeredQueues.add(queueName);
  }

  async onModuleInit(): Promise<void> {
    if (this.started) {
      return;
    }

    if (!this.startPromise) {
      this.startPromise = (async () => {
        await this.boss.start();
        this.started = true;
        await Promise.all([
          this.intervalScheduler.start(),
          this.initializeCronLimitTable(),
        ]);
        await Promise.all(
          [...this.registeredQueues].map((queueName) =>
            this.ensureQueue(queueName),
          ),
        );
        if (!this.destroying) {
          this.startRetentionCleanup();
        }
      })().finally(() => {
        this.startPromise = undefined;
      });
    }

    await this.startPromise;
  }

  async onModuleDestroy(): Promise<void> {
    this.destroying = true;

    if (this.startPromise) {
      await this.startPromise;
    }

    this.stopRetentionCleanup();
    await this.intervalScheduler.stop();

    if (this.retentionCleanupPromise) {
      await this.retentionCleanupPromise;
    }

    if (!this.stopPromise) {
      this.stopPromise = (async () => {
        try {
          if (this.started) {
            const timeout = this.boundedShutdownDrain
              ? this.twentyConfigService.get('AI_STREAM_SHUTDOWN_DRAIN_MS')
              : undefined;

            await this.boss.stop({
              close: true,
              graceful: true,
              ...(timeout === undefined ? {} : { timeout }),
            });
            this.started = false;
          }
        } finally {
          await this.closeCoordinationPool();
        }
      })().finally(() => {
        this.stopPromise = undefined;
      });
    }

    await this.stopPromise;
  }

  async add<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    jobName: string,
    data: T,
    options?: QueueJobOptions,
  ): Promise<void> {
    await this.ensureStartedQueue(queueName);

    const envelope: PgBossJobEnvelope<T> = {
      version: 1,
      jobName,
      ...(options?.id ? { logicalId: options.id } : {}),
      data,
    };
    const priority =
      options?.priority ?? MESSAGE_QUEUE_PRIORITY[queueName] ?? 0;
    const sendOptions: PgBossSendOptions = {
      priority: -priority,
      retryLimit:
        options?.retryLimit ??
        this.workerOptions.get(queueName)?.maxStalledCount ??
        0,
      ...(options?.delay === undefined
        ? {}
        : { startAfter: new Date(Date.now() + options.delay) }),
      deleteAfterSeconds: QUEUE_RETENTION.failedMaxAge,
    };

    if (options?.id) {
      await this.sendWaitingJobOnce(
        queueName,
        options.id,
        envelope,
        sendOptions,
      );

      return;
    }

    await this.boss.send(queueName, envelope, sendOptions);
  }

  async work<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    handler: (job: MessageQueueJob<T>) => Promise<void> | void,
    options?: MessageQueueWorkerOptions,
  ): Promise<void> {
    await this.ensureStartedQueue(queueName);
    this.boundedShutdownDrain ||= options?.boundedShutdownDrain === true;
    this.workerOptions.set(queueName, options ?? {});
    const lockDurationSeconds =
      options?.lockDuration === undefined
        ? undefined
        : Math.ceil(options.lockDuration / 1_000);

    const queueRecoveryOptions = {
      ...(lockDurationSeconds === undefined
        ? {}
        : {
            // pg-boss expiration is second-granularity. Rounding upward avoids
            // expiring a job earlier than Twenty's millisecond lock duration.
            expireInSeconds: lockDurationSeconds,
          }),
      // pg-boss rejects heartbeat periods below ten seconds. Shorter Twenty
      // locks therefore rely on expiration; longer locks also get renewal.
      ...(lockDurationSeconds !== undefined && lockDurationSeconds >= 10
        ? { heartbeatSeconds: lockDurationSeconds }
        : {}),
      ...(options?.maxStalledCount === undefined
        ? {}
        : { retryLimit: options.maxStalledCount }),
    };

    if (Object.keys(queueRecoveryOptions).length > 0) {
      await this.boss.updateQueue(queueName, queueRecoveryOptions);
      await this.boss.supervise(queueName);
    }

    await this.boss.work<PgBossJobEnvelope<T>>(
      queueName,
      {
        ...(options?.concurrency === undefined
          ? {}
          : { localConcurrency: options.concurrency }),
        ...(lockDurationSeconds !== undefined && lockDurationSeconds >= 10
          ? { heartbeatRefreshSeconds: lockDurationSeconds / 2 }
          : {}),
      },
      async ([job]) => {
        if (!job) {
          return;
        }

        const envelope = this.readEnvelope(job.data);

        if (
          envelope.cronScheduleKey !== undefined &&
          !(await this.claimCronRun(queueName, envelope.cronScheduleKey))
        ) {
          return;
        }

        await handler({
          id: job.id,
          name: envelope.jobName,
          data: envelope.data,
          abortSignal: job.signal,
        });
      },
    );
  }

  async addCron<T extends MessageQueueJobData | undefined>({
    queueName,
    jobName,
    data,
    options,
    jobId,
  }: {
    queueName: MessageQueue;
    jobName: string;
    data: T;
    options: QueueCronJobOptions;
    jobId?: string;
  }): Promise<void> {
    const { every, pattern, limit } = options.repeat;

    if ((every === undefined) === (pattern === undefined)) {
      throw new Error(
        'Recurring jobs require exactly one of repeat.every or repeat.pattern',
      );
    }

    if (limit !== undefined && (!Number.isInteger(limit) || limit <= 0)) {
      throw new Error('Recurring job repeat.limit must be a positive integer');
    }

    await this.ensureStartedQueue(queueName);
    const cronKey = getJobKey({ jobName, jobId });
    const scheduleKey = this.persistedScheduleKey(queueName, cronKey);
    const envelope: PgBossJobEnvelope = {
      version: 1,
      jobName,
      data: data ?? {},
    };

    if (pattern !== undefined) {
      const priority =
        options.priority ?? MESSAGE_QUEUE_PRIORITY[queueName] ?? 0;

      await this.intervalScheduler.remove(scheduleKey);

      if (limit === undefined) {
        await this.removeCronLimit(scheduleKey);
      } else {
        envelope.cronScheduleKey = scheduleKey;
        await this.upsertCronLimit({
          scheduleKey,
          queueName,
          cronKey,
          remainingRuns: limit,
          definition: {
            pattern,
            data: data ?? {},
            priority,
            retryLimit: options.retryLimit ?? 0,
            limit,
          },
        });
      }

      await this.boss.schedule(queueName, pattern, envelope, {
        key: cronKey,
        priority: -priority,
        retryLimit: options.retryLimit ?? 0,
        deleteAfterSeconds: QUEUE_RETENTION.failedMaxAge,
      });

      return;
    }

    await Promise.all([
      this.boss.unschedule(queueName, cronKey),
      this.removeCronLimit(scheduleKey),
    ]);

    if (every === undefined) {
      throw new Error('Interval schedule is missing repeat.every');
    }

    await this.intervalScheduler.upsert({
      scheduleKey,
      queueName,
      jobName,
      payload: {
        ...envelope,
        intervalOptions: {
          priority: -(
            options.priority ??
            MESSAGE_QUEUE_PRIORITY[queueName] ??
            0
          ),
          retryLimit: options.retryLimit ?? 0,
        },
      },
      everyMs: every,
      ...(limit === undefined ? {} : { remainingRuns: limit }),
    });
  }

  async removeCron({
    queueName,
    jobName,
    jobId,
  }: {
    queueName: MessageQueue;
    jobName: string;
    jobId?: string;
  }): Promise<void> {
    await this.ensureStartedQueue(queueName);
    const cronKey = getJobKey({ jobName, jobId });
    const scheduleKey = this.persistedScheduleKey(queueName, cronKey);

    await Promise.all([
      this.boss.unschedule(queueName, cronKey),
      this.intervalScheduler.remove(scheduleKey),
      this.removeCronLimit(scheduleKey),
    ]);
  }

  async getStats(queueName: MessageQueue): Promise<MessageQueueStats> {
    const counts: Omit<MessageQueueStats, 'queueName' | 'healthy'> = {
      created: 0,
      active: 0,
      completed: 0,
      failed: 0,
      retry: 0,
    };

    try {
      await this.ensureStartedQueue(queueName);
      await this.boss.getQueueStats(queueName, { force: true });
      const jobs = await this.boss.findJobs(queueName);

      for (const job of jobs) {
        counts[this.toNeutralState(job.state)] += 1;
      }

      return { queueName, healthy: true, ...counts };
    } catch {
      return { queueName, healthy: false, ...counts };
    }
  }

  async findJobs(
    queueName: MessageQueue,
    states: MessageQueueJobState[],
  ): Promise<MessageQueueJobRecord[]> {
    await this.ensureStartedQueue(queueName);
    const jobs = await this.boss.findJobs<PgBossJobEnvelope>(queueName);

    return jobs
      .filter((job) => states.includes(this.toNeutralState(job.state)))
      .map((job) => this.toJobRecord(job));
  }

  async retryJob(queueName: MessageQueue, jobId: string): Promise<void> {
    await this.ensureStartedQueue(queueName);
    await this.boss.retry(queueName, jobId);
  }

  async deleteJob(queueName: MessageQueue, jobId: string): Promise<void> {
    await this.ensureStartedQueue(queueName);
    await this.boss.deleteJob(queueName, jobId);
  }

  private async ensureStartedQueue(queueName: MessageQueue): Promise<void> {
    this.register(queueName);
    await this.onModuleInit();
    await this.ensureQueue(queueName);
  }

  private async sendWaitingJobOnce<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    logicalId: string,
    envelope: PgBossJobEnvelope<T>,
    sendOptions: PgBossSendOptions,
  ): Promise<void> {
    const client = await this.coordinationPool.connect();

    try {
      await client.query('BEGIN');
      await client.query(
        'SELECT pg_advisory_xact_lock(hashtextextended($1, 0))',
        [JSON.stringify([queueName, logicalId])],
      );

      const db = this.toPgBossDatabase(client);
      const duplicates = await this.boss.findJobs(queueName, {
        queued: true,
        data: { logicalId },
        db,
      });

      if (duplicates.length === 0) {
        await this.boss.send(queueName, envelope, { ...sendOptions, db });
      }

      await client.query('COMMIT');
    } catch (error) {
      try {
        await client.query('ROLLBACK');
      } catch (rollbackError) {
        this.logger.error(
          'Failed to roll back pg-boss logical-id transaction',
          rollbackError,
        );
      }

      throw error;
    } finally {
      client.release();
    }
  }

  private toPgBossDatabase(client: PoolClient): PgBossDatabase {
    return {
      executeSql: async (text, values) => client.query(text, values),
    };
  }

  private startRetentionCleanup(): void {
    if (this.retentionCleanupTimer) {
      return;
    }

    this.retentionCleanupTimer = setInterval(() => {
      if (this.retentionCleanupPromise) {
        return;
      }

      this.retentionCleanupPromise = this.cleanupTerminalJobs()
        .catch((error) => {
          this.logger.error('Failed to clean retained pg-boss jobs', error);
        })
        .finally(() => {
          this.retentionCleanupPromise = undefined;
        });
    }, RETENTION_CLEANUP_INTERVAL_MS);
    this.retentionCleanupTimer.unref?.();
  }

  private stopRetentionCleanup(): void {
    if (!this.retentionCleanupTimer) {
      return;
    }

    clearInterval(this.retentionCleanupTimer);
    this.retentionCleanupTimer = undefined;
  }

  private async cleanupTerminalJobs(): Promise<void> {
    await Promise.all(
      [...this.registeredQueues].map(async (queueName) => {
        const jobs = await this.boss.findJobs<PgBossJobEnvelope>(queueName);
        const now = Date.now();
        const expiredIds = new Set<string>();

        for (const retention of [
          {
            state: 'completed' as const,
            maxAgeSeconds: QUEUE_RETENTION.completedMaxAge,
            maxCount: QUEUE_RETENTION.completedMaxCount,
          },
          {
            state: 'failed' as const,
            maxAgeSeconds: QUEUE_RETENTION.failedMaxAge,
            maxCount: QUEUE_RETENTION.failedMaxCount,
          },
        ]) {
          const terminalJobs = jobs
            .filter((job) => this.toNeutralState(job.state) === retention.state)
            .sort(
              (left, right) =>
                this.terminalTimestamp(right) - this.terminalTimestamp(left),
            );

          terminalJobs.forEach((job, index) => {
            const ageMs = now - this.terminalTimestamp(job);

            if (
              ageMs > retention.maxAgeSeconds * 1_000 ||
              index >= retention.maxCount
            ) {
              expiredIds.add(job.id);
            }
          });
        }

        await Promise.all(
          [...expiredIds].map((jobId) => this.boss.deleteJob(queueName, jobId)),
        );
      }),
    );
  }

  private terminalTimestamp(job: InspectablePgBossJob): number {
    return (job.completedOn ?? job.createdOn).getTime();
  }

  private async closeCoordinationPool(): Promise<void> {
    if (this.coordinationPoolClosed) {
      return;
    }

    this.coordinationPoolClosed = true;
    await this.coordinationPool.end();
  }

  private persistedScheduleKey(
    queueName: MessageQueue,
    cronKey: string,
  ): string {
    return `${queueName}:${cronKey}`;
  }

  private async initializeCronLimitTable(): Promise<void> {
    if (!this.cronLimitInitializePromise) {
      this.cronLimitInitializePromise = this.coordinationPool
        .query(`
          CREATE TABLE IF NOT EXISTS ${this.options.schema}.cron_schedule_limit (
            schedule_key text PRIMARY KEY,
            queue_name text NOT NULL,
            cron_key text NOT NULL,
            definition jsonb NOT NULL,
            remaining_runs integer NOT NULL CHECK (remaining_runs >= 0),
            updated_at timestamptz NOT NULL DEFAULT now()
          )
        `)
        .then(() => undefined)
        .catch((error) => {
          this.cronLimitInitializePromise = undefined;
          throw error;
        });
    }

    await this.cronLimitInitializePromise;
  }

  private async upsertCronLimit(input: {
    scheduleKey: string;
    queueName: MessageQueue;
    cronKey: string;
    remainingRuns: number;
    definition: MessageQueueJobData;
  }): Promise<void> {
    await this.initializeCronLimitTable();
    await this.coordinationPool.query(
      `
        INSERT INTO ${this.options.schema}.cron_schedule_limit
          (schedule_key, queue_name, cron_key, definition, remaining_runs)
        VALUES ($1, $2, $3, $4::jsonb, $5)
        ON CONFLICT (schedule_key) DO UPDATE SET
          queue_name = EXCLUDED.queue_name,
          cron_key = EXCLUDED.cron_key,
          definition = EXCLUDED.definition,
          remaining_runs = EXCLUDED.remaining_runs,
          updated_at = now()
        WHERE ${this.options.schema}.cron_schedule_limit.queue_name IS DISTINCT FROM EXCLUDED.queue_name
           OR ${this.options.schema}.cron_schedule_limit.cron_key IS DISTINCT FROM EXCLUDED.cron_key
           OR ${this.options.schema}.cron_schedule_limit.definition IS DISTINCT FROM EXCLUDED.definition
      `,
      [
        input.scheduleKey,
        input.queueName,
        input.cronKey,
        input.definition,
        input.remainingRuns,
      ],
    );
  }

  private async removeCronLimit(scheduleKey: string): Promise<void> {
    await this.initializeCronLimitTable();
    await this.coordinationPool.query(
      `DELETE FROM ${this.options.schema}.cron_schedule_limit WHERE schedule_key = $1`,
      [scheduleKey],
    );
  }

  private async claimCronRun(
    queueName: MessageQueue,
    scheduleKey: string,
  ): Promise<boolean> {
    const client = await this.coordinationPool.connect();
    let remainingRuns: number | undefined;
    let cronKey: string | undefined;

    try {
      await client.query('BEGIN');
      const result = await client.query<{
        remaining_runs: number;
        cron_key: string;
      }>(
        `
          UPDATE ${this.options.schema}.cron_schedule_limit
          SET remaining_runs = remaining_runs - 1, updated_at = now()
          WHERE schedule_key = $1
            AND queue_name = $2
            AND remaining_runs > 0
          RETURNING remaining_runs, cron_key
        `,
        [scheduleKey, queueName],
      );

      remainingRuns = result.rows[0]?.remaining_runs;
      cronKey = result.rows[0]?.cron_key;
      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }

    if (remainingRuns === undefined) {
      return false;
    }

    if (remainingRuns === 0 && cronKey !== undefined) {
      await this.boss.unschedule(queueName, cronKey);
    }

    return true;
  }

  private async ensureQueue(queueName: MessageQueue): Promise<void> {
    let creation = this.queueCreationPromises.get(queueName);

    if (!creation) {
      const pendingCreation = this.boss
        .createQueue(queueName)
        .catch((error) => {
          if (this.queueCreationPromises.get(queueName) === pendingCreation) {
            this.queueCreationPromises.delete(queueName);
          }

          throw error;
        });

      creation = pendingCreation;
      this.queueCreationPromises.set(queueName, creation);
    }

    await creation;
  }

  private readEnvelope<T extends MessageQueueJobData>(
    value: PgBossJobEnvelope<T>,
  ): PgBossJobEnvelope<T> {
    if (value.version !== 1) {
      throw new Error(
        `Unsupported pg-boss job envelope version: ${value.version}`,
      );
    }

    return value;
  }

  private toNeutralState(
    state: InspectablePgBossJob['state'],
  ): MessageQueueJobState {
    return state === 'cancelled' ? 'failed' : state;
  }

  private toJobRecord(job: InspectablePgBossJob): MessageQueueJobRecord {
    const envelope = this.readEnvelope(job.data);
    const state = this.toNeutralState(job.state);
    const output = job.output as { message?: unknown } | null;
    const failedReason =
      state === 'failed' && output?.message !== undefined
        ? String(output.message)
        : undefined;

    return {
      id: job.id,
      name: envelope.jobName,
      data: envelope.data,
      state,
      attemptsMade: job.retryCount + (state === 'created' ? 0 : 1),
      createdAt: job.createdOn.getTime(),
      ...(job.startedOn ? { processedAt: job.startedOn.getTime() } : {}),
      ...(job.completedOn ? { finishedAt: job.completedOn.getTime() } : {}),
      ...(failedReason ? { failedReason } : {}),
    };
  }
}

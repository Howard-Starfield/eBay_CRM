import { type OnModuleDestroy, type OnModuleInit } from '@nestjs/common';

import { PgBoss, type JobWithMetadata as PgBossJobWithMetadata } from 'pg-boss';

import { QUEUE_RETENTION } from 'src/engine/core-modules/message-queue/constants/queue-retention.constants';
import {
  type QueueCronJobOptions,
  type QueueJobOptions,
} from 'src/engine/core-modules/message-queue/drivers/interfaces/job-options.interface';
import { type MessageQueueDriver } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface';
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
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

export type PgBossDriverOptions = {
  connectionString: string;
  schema: 'desktop_runtime';
  applicationName: string;
};

type PgBossJobEnvelope<T extends MessageQueueJobData = MessageQueueJobData> = {
  version: 1;
  jobName: string;
  logicalId?: string;
  data: T;
};

type InspectablePgBossJob = PgBossJobWithMetadata<PgBossJobEnvelope>;

export class PgBossDriver
  implements MessageQueueDriver, OnModuleInit, OnModuleDestroy
{
  private readonly boss: PgBoss;
  private readonly registeredQueues = new Set<MessageQueue>();
  private readonly queueCreationPromises = new Map<
    MessageQueue,
    Promise<void>
  >();
  private startPromise?: Promise<void>;
  private stopPromise?: Promise<void>;
  private started = false;
  private boundedShutdownDrain = false;

  constructor(
    private readonly options: PgBossDriverOptions,
    _metricsService: MetricsService,
    private readonly twentyConfigService: TwentyConfigService,
  ) {
    this.boss = new PgBoss({
      connectionString: options.connectionString,
      schema: options.schema,
      application_name: options.applicationName,
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
        await Promise.all(
          [...this.registeredQueues].map((queueName) =>
            this.ensureQueue(queueName),
          ),
        );
      })().finally(() => {
        this.startPromise = undefined;
      });
    }

    await this.startPromise;
  }

  async onModuleDestroy(): Promise<void> {
    if (this.startPromise) {
      await this.startPromise;
    }

    if (!this.started) {
      return;
    }

    if (!this.stopPromise) {
      this.stopPromise = (async () => {
        const timeout = this.boundedShutdownDrain
          ? this.twentyConfigService.get('AI_STREAM_SHUTDOWN_DRAIN_MS')
          : undefined;

        await this.boss.stop({
          close: true,
          graceful: true,
          ...(timeout === undefined ? {} : { timeout }),
        });
        this.started = false;
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

    if (options?.id) {
      const duplicates = await this.boss.findJobs(queueName, {
        queued: true,
        data: { logicalId: options.id },
      });

      if (duplicates.length > 0) {
        return;
      }
    }

    const envelope: PgBossJobEnvelope<T> = {
      version: 1,
      jobName,
      ...(options?.id ? { logicalId: options.id } : {}),
      data,
    };
    const priority =
      options?.priority ?? MESSAGE_QUEUE_PRIORITY[queueName] ?? 0;

    await this.boss.send(queueName, envelope, {
      priority: -priority,
      retryLimit: options?.retryLimit ?? 0,
      ...(options?.delay === undefined
        ? {}
        : { startAfter: new Date(Date.now() + options.delay) }),
      deleteAfterSeconds: QUEUE_RETENTION.completedMaxAge,
    });
  }

  async work<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    handler: (job: MessageQueueJob<T>) => Promise<void> | void,
    options?: MessageQueueWorkerOptions,
  ): Promise<void> {
    await this.ensureStartedQueue(queueName);
    this.boundedShutdownDrain ||= options?.boundedShutdownDrain === true;

    await this.boss.work<PgBossJobEnvelope<T>>(
      queueName,
      {
        ...(options?.concurrency === undefined
          ? {}
          : { localConcurrency: options.concurrency }),
      },
      async ([job]) => {
        if (!job) {
          return;
        }

        const envelope = this.readEnvelope(job.data);

        await handler({
          id: job.id,
          name: envelope.jobName,
          data: envelope.data,
          abortSignal: job.signal,
        });
      },
    );
  }

  async addCron<T extends MessageQueueJobData | undefined>(_input: {
    queueName: MessageQueue;
    jobName: string;
    data: T;
    options: QueueCronJobOptions;
    jobId?: string;
  }): Promise<void> {
    throw new Error(
      'pg-boss recurring schedules are implemented in Phase 0 Task 6',
    );
  }

  async removeCron(_input: {
    queueName: MessageQueue;
    jobName: string;
    jobId?: string;
  }): Promise<void> {
    throw new Error(
      'pg-boss recurring schedules are implemented in Phase 0 Task 6',
    );
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

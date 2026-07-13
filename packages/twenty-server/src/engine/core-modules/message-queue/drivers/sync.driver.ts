import { Logger } from '@nestjs/common';

import { isDefined } from 'twenty-shared/utils';

import { type MessageQueueDriver } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface';
import { type MessageQueueJobRecord } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-record.type';
import { type MessageQueueJobState } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-state.type';
import { type MessageQueueStats } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-stats.type';
import {
  type MessageQueueJob,
  type MessageQueueJobData,
} from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';

import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

// Synchronous driver for tests and local dev
export class SyncDriver implements MessageQueueDriver {
  private readonly logger = new Logger(SyncDriver.name);
  private workersMap: {
    [queueName: string]: (job: MessageQueueJob) => Promise<void> | void;
  } = {};
  private jobRecordsMap = new Map<string, MessageQueueJobRecord>();
  private jobQueueMap = new Map<string, MessageQueue>();
  private nextJobId = 0;

  constructor() {}

  async add<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    jobName: string,
    data: T,
  ): Promise<void> {
    const id = `sync-${this.nextJobId++}`;
    const record: MessageQueueJobRecord = {
      id,
      name: jobName,
      data,
      state: 'created',
      attemptsMade: 0,
      createdAt: Date.now(),
    };

    this.jobRecordsMap.set(id, record);
    this.jobQueueMap.set(id, queueName);

    try {
      record.state = 'active';
      record.processedAt = Date.now();
      await this.processJob(queueName, { id: '', name: jobName, data });
      record.state = 'completed';
      record.finishedAt = Date.now();
    } catch (error) {
      record.state = 'failed';
      record.attemptsMade += 1;
      record.failedReason =
        error instanceof Error ? error.message : String(error);
      record.finishedAt = Date.now();
      throw error;
    }
  }

  async addCron<T extends MessageQueueJobData | undefined>({
    queueName,
    jobName,
    data,
  }: {
    queueName: MessageQueue;
    jobName: string;
    data: T;
  }): Promise<void> {
    this.logger.log(`Running cron job with SyncDriver`);
    await this.processJob(queueName, {
      id: '',
      name: jobName,
      // TODO: Fix this type issue
      // oxlint-disable-next-line typescript/no-explicit-any
      data: data as any,
    });
  }

  async removeCron({ queueName }: { queueName: MessageQueue }) {
    this.logger.log(`Removing '${queueName}' cron job with SyncDriver`);
  }

  async work<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    handler: (job: MessageQueueJob<T>) => Promise<void> | void,
  ): Promise<void> {
    this.logger.log(`Registering handler for queue: ${queueName}`);
    this.workersMap[queueName] = handler;
  }

  async processJob<T extends MessageQueueJobData>(
    queueName: string,
    job: MessageQueueJob<T>,
  ) {
    const worker = this.workersMap[queueName];

    if (worker) {
      await worker(job);
    } else {
      if (process.env.NODE_ENV !== 'test') {
        this.logger.error(`No handler found for job: ${queueName}`);
      }
    }
  }

  async getStats(queueName: MessageQueue): Promise<MessageQueueStats> {
    const jobs = [...this.jobRecordsMap.values()].filter(
      ({ id }) => this.jobQueueMap.get(id) === queueName,
    );
    const count = (state: MessageQueueJobState) =>
      jobs.filter((job) => job.state === state).length;

    return {
      queueName,
      created: count('created'),
      active: count('active'),
      completed: count('completed'),
      failed: count('failed'),
      retry: count('retry'),
      healthy: true,
    };
  }

  async findJobs(
    queueName: MessageQueue,
    states: MessageQueueJobState[],
  ): Promise<MessageQueueJobRecord[]> {
    return [...this.jobRecordsMap.values()].filter(
      ({ id, state }) =>
        this.jobQueueMap.get(id) === queueName && states.includes(state),
    );
  }

  async retryJob(queueName: MessageQueue, jobId: string): Promise<void> {
    const job = this.jobRecordsMap.get(jobId);

    if (!isDefined(job) || this.jobQueueMap.get(jobId) !== queueName) {
      throw new Error(`Job ${jobId} was not found on queue ${queueName}`);
    }

    try {
      job.state = 'active';
      job.attemptsMade += 1;
      job.processedAt = Date.now();
      await this.processJob(queueName, {
        id: '',
        name: job.name,
        data: job.data,
      });
      job.state = 'completed';
      job.finishedAt = Date.now();
      job.failedReason = undefined;
    } catch (error) {
      job.state = 'failed';
      job.failedReason = error instanceof Error ? error.message : String(error);
      job.finishedAt = Date.now();
      throw error;
    }
  }

  async deleteJob(queueName: MessageQueue, jobId: string): Promise<void> {
    if (this.jobQueueMap.get(jobId) !== queueName) {
      return;
    }

    this.jobRecordsMap.delete(jobId);
    this.jobQueueMap.delete(jobId);
  }
}

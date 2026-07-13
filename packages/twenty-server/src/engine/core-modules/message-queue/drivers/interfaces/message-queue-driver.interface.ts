import {
  type QueueCronJobOptions,
  type QueueJobOptions,
} from 'src/engine/core-modules/message-queue/drivers/interfaces/job-options.interface';
import { type MessageQueueJobRecord } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-record.type';
import { type MessageQueueJobState } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-state.type';
import { type MessageQueueStats } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-stats.type';
import { type MessageQueueJobData } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueueWorkerOptions } from 'src/engine/core-modules/message-queue/interfaces/message-queue-worker-options.interface';

import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

export interface MessageQueueDriver {
  add<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    jobName: string,
    data: T,
    options?: QueueJobOptions,
  ): Promise<void>;
  work<T extends MessageQueueJobData>(
    queueName: MessageQueue,
    handler: ({ data, id }: { data: T; id: string }) => Promise<void> | void,
    options?: MessageQueueWorkerOptions,
  ): Promise<void>;
  addCron<T extends MessageQueueJobData | undefined>({
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
  }): Promise<void>;
  removeCron({
    queueName,
    jobName,
    jobId,
  }: {
    queueName: MessageQueue;
    jobName: string;
    jobId?: string;
  }): Promise<void>;
  getStats(queueName: MessageQueue): Promise<MessageQueueStats>;
  findJobs(
    queueName: MessageQueue,
    states: MessageQueueJobState[],
  ): Promise<MessageQueueJobRecord[]>;
  retryJob(queueName: MessageQueue, jobId: string): Promise<void>;
  deleteJob(queueName: MessageQueue, jobId: string): Promise<void>;
  register?(queueName: MessageQueue): void;
}

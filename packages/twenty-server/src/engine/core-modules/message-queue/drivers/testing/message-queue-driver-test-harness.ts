import { type MessageQueueDriver } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface';
import { type MessageQueueJob } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueueWorkerOptions } from 'src/engine/core-modules/message-queue/interfaces/message-queue-worker-options.interface';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

export type MessageQueueDriverTestHandler = (
  job: MessageQueueJob,
) => Promise<void> | void;

export type MessageQueueDriverTestHarnessOptions = {
  queueName: MessageQueue;
  handler: MessageQueueDriverTestHandler;
  workerOptions?: MessageQueueWorkerOptions;
  shutdownDrainMs?: number;
};

export type MessageQueueDriverTestHarness = {
  readonly driver: MessageQueueDriver;
  readonly queueName: MessageQueue;
  start(): Promise<void>;
  stop(): Promise<void>;
  clear(): Promise<void>;
  waitFor(
    predicate: () => boolean | Promise<boolean>,
    timeoutMs: number,
  ): Promise<void>;
  restartWorker(): Promise<void>;
  terminateWorker(): Promise<void>;
};

export type CreateMessageQueueDriverTestHarness = (
  options: MessageQueueDriverTestHarnessOptions,
) => Promise<MessageQueueDriverTestHarness>;

import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

export type MessageQueueStats = {
  queueName: MessageQueue;
  created: number;
  active: number;
  completed: number;
  failed: number;
  retry: number;
  healthy: boolean;
};

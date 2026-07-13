import { type MessageQueueJobData } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';

import { type MessageQueueJobState } from './message-queue-job-state.type';

export type MessageQueueJobRecord = {
  id: string;
  name: string;
  data: MessageQueueJobData;
  state: MessageQueueJobState;
  attemptsMade: number;
  createdAt: number;
  processedAt?: number;
  finishedAt?: number;
  failedReason?: string;
};

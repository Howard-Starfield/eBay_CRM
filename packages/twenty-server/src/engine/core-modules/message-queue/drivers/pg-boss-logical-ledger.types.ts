import { type Db as PgBossDatabase } from 'pg-boss';

import { type MessageQueueJobData } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

export type LogicalPgBossEnvelope = {
  version: 2;
  logicalJobId: string;
  generation: number;
};

export type LogicalQueuePolicy = {
  stallRecoveryLimit: number;
  heartbeatSeconds?: number;
};

export type LogicalJobStart =
  | {
      kind: 'execute';
      logicalJobId: string;
      jobName: string;
      data: MessageQueueJobData;
      executionToken: string;
    }
  | { kind: 'fenced' }
  | { kind: 'stall-exhausted' };

export type LogicalPhysicalArgs = {
  queueName: MessageQueue;
  physicalJobId: string;
};

export type LogicalStartArgs = LogicalPhysicalArgs & {
  envelope: LogicalPgBossEnvelope;
  transportRetryCount: number;
  workerInstanceId: string;
};

export type LogicalSettlementArgs = LogicalPhysicalArgs & {
  logicalJobId: string;
  generation: number;
  executionToken: string;
};

export type LogicalDeadLetterArgs = LogicalPhysicalArgs & {
  envelope: LogicalPgBossEnvelope;
};

export type LogicalTransport = {
  send: (args: {
    queueName: MessageQueue;
    envelope: LogicalPgBossEnvelope;
    physicalJobId: string;
    stallRecoveryLimit: number;
    priority: number;
    availableAt: Date;
    db: PgBossDatabase;
  }) => Promise<void>;
  complete: (args: {
    queueName: MessageQueue;
    physicalJobId: string;
    db: PgBossDatabase;
  }) => Promise<void>;
};

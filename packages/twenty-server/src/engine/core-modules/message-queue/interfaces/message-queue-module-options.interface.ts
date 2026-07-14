import { type BullMQDriverOptions } from 'src/engine/core-modules/message-queue/drivers/bullmq.driver';
import { type PgBossDriverOptions } from 'src/engine/core-modules/message-queue/drivers/pg-boss.driver';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

export enum MessageQueueDriverType {
  BullMQ = 'bull-mq',
  PgBoss = 'pg-boss',
  Sync = 'sync',
}

export interface BullMQDriverFactoryOptions {
  type: MessageQueueDriverType.BullMQ;
  options: BullMQDriverOptions;
  metricsService: MetricsService;
  twentyConfigService: TwentyConfigService;
}

export interface SyncDriverFactoryOptions {
  type: MessageQueueDriverType.Sync;
  // oxlint-disable-next-line typescript/no-explicit-any
  options: Record<string, any>;
}

export type PgBossDriverFactoryOptions = {
  type: MessageQueueDriverType.PgBoss;
  options: PgBossDriverOptions;
  metricsService: MetricsService;
  twentyConfigService: TwentyConfigService;
};

export type MessageQueueModuleOptions =
  | BullMQDriverFactoryOptions
  | PgBossDriverFactoryOptions
  | SyncDriverFactoryOptions;

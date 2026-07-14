import {
  type BullMQDriverFactoryOptions,
  MessageQueueDriverType,
  type MessageQueueModuleOptions,
  type PgBossDriverFactoryOptions,
} from 'src/engine/core-modules/message-queue/interfaces';
import { RUNTIME_BACKENDS } from 'src/engine/core-modules/runtime-backend/runtime-backend.constants';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type RedisClientService } from 'src/engine/core-modules/redis-client/redis-client.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

/**
 * MessageQueue Module factory
 * @returns MessageQueueModuleOptions
 * @param twentyConfigService
 * @param redisClientService
 * @param metricsService
 */
export const messageQueueModuleFactory = async (
  twentyConfigService: TwentyConfigService,
  redisClientService: RedisClientService,
  metricsService: MetricsService,
): Promise<MessageQueueModuleOptions> => {
  const runtimeBackend = twentyConfigService.get('RUNTIME_BACKEND');

  switch (runtimeBackend) {
    case RUNTIME_BACKENDS.POSTGRES_DESKTOP: {
      return {
        type: MessageQueueDriverType.PgBoss,
        options: {
          connectionString: twentyConfigService.get('PG_DATABASE_URL'),
          schema: 'desktop_runtime',
          applicationName: 'ebaycrm-message-queue',
        },
        metricsService,
        twentyConfigService,
      } satisfies PgBossDriverFactoryOptions;
    }
    case RUNTIME_BACKENDS.REDIS: {
      return {
        type: MessageQueueDriverType.BullMQ,
        options: {
          connection: redisClientService.getQueueClient(),
        },
        metricsService,
        twentyConfigService,
      } satisfies BullMQDriverFactoryOptions;
    }
    default:
      throw new Error(
        `Invalid runtime backend (${runtimeBackend}), check your .env file`,
      );
  }
};

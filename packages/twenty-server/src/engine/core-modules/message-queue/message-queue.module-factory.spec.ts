import { RUNTIME_BACKENDS } from 'src/engine/core-modules/runtime-backend/runtime-backend.constants';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type RedisClientService } from 'src/engine/core-modules/redis-client/redis-client.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

import { MessageQueueDriverType } from './interfaces';
import { messageQueueModuleFactory } from './message-queue.module-factory';

describe('messageQueueModuleFactory', () => {
  const metricsService = {} as MetricsService;
  const getQueueClient = jest.fn(() => ({ host: 'redis' }));
  const redisClientService = {
    getQueueClient,
  } as unknown as RedisClientService;

  beforeEach(() => {
    getQueueClient.mockClear();
  });

  it('selects pg-boss without constructing a Redis queue client in desktop mode', async () => {
    const get = jest.fn((key: string) => {
      if (key === 'RUNTIME_BACKEND') {
        return RUNTIME_BACKENDS.POSTGRES_DESKTOP;
      }
      if (key === 'PG_DATABASE_URL') {
        return 'postgresql://postgres:postgres@localhost/runtime';
      }

      throw new Error(`Unexpected config key: ${key}`);
    });
    const twentyConfigService = { get } as unknown as TwentyConfigService;

    await expect(
      messageQueueModuleFactory(
        twentyConfigService,
        redisClientService,
        metricsService,
      ),
    ).resolves.toEqual({
      type: MessageQueueDriverType.PgBoss,
      options: {
        connectionString: 'postgresql://postgres:postgres@localhost/runtime',
        schema: 'desktop_runtime',
        applicationName: 'ebaycrm-message-queue',
      },
      metricsService,
      twentyConfigService,
    });
    expect(getQueueClient).not.toHaveBeenCalled();
  });

  it('preserves the BullMQ queue client in compatibility mode', async () => {
    const get = jest.fn((key: string) => {
      if (key === 'RUNTIME_BACKEND') {
        return RUNTIME_BACKENDS.REDIS;
      }

      throw new Error(`Unexpected config key: ${key}`);
    });
    const twentyConfigService = { get } as unknown as TwentyConfigService;

    await expect(
      messageQueueModuleFactory(
        twentyConfigService,
        redisClientService,
        metricsService,
      ),
    ).resolves.toMatchObject({
      type: MessageQueueDriverType.BullMQ,
      options: { connection: { host: 'redis' } },
    });
    expect(getQueueClient).toHaveBeenCalledTimes(1);
  });

  it.each([undefined, null, 'Redis', ' redis', 'redis ', 'sqlite'])(
    'fails closed for runtime backend %j without inferring from Redis configuration',
    async (runtimeBackend) => {
      const get = jest.fn((key: string) => {
        if (key === 'RUNTIME_BACKEND') {
          return runtimeBackend;
        }
        if (key === 'REDIS_URL') {
          return 'redis://localhost:6379';
        }

        throw new Error(`Unexpected config key: ${key}`);
      });
      const twentyConfigService = { get } as unknown as TwentyConfigService;

      await expect(
        messageQueueModuleFactory(
          twentyConfigService,
          redisClientService,
          metricsService,
        ),
      ).rejects.toThrow('Invalid runtime backend');
      expect(get).toHaveBeenCalledTimes(1);
      expect(get).toHaveBeenCalledWith('RUNTIME_BACKEND');
      expect(getQueueClient).not.toHaveBeenCalled();
    },
  );
});

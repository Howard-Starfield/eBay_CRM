import { BullMQDriver } from 'src/engine/core-modules/message-queue/drivers/bullmq.driver';
import { type MessageQueueDriver } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';
import { MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

describe('BullMQDriver health', () => {
  beforeAll(() => {
    jest.useRealTimers();
  });

  it('returns bounded unhealthy zero-count stats when Redis is unavailable', async () => {
    const metricsService = {} as MetricsService;
    const twentyConfigService = {} as TwentyConfigService;
    const driver = new BullMQDriver(
      {
        connection: {
          host: '127.0.0.1',
          port: 1,
          connectTimeout: 50,
          maxRetriesPerRequest: null,
          retryStrategy: () => null,
        },
      },
      metricsService,
      twentyConfigService,
    );
    const neutralDriver: MessageQueueDriver = driver;

    driver.register(MessageQueue.cronQueue);

    let safetyTimeout: ReturnType<typeof setTimeout> | undefined;

    try {
      const startedAt = Date.now();
      const stats = await Promise.race([
        neutralDriver.getStats(MessageQueue.cronQueue),
        new Promise<never>((_resolve, reject) => {
          safetyTimeout = setTimeout(
            () => reject(new Error('getStats did not settle within 1500ms')),
            1_500,
          );
        }),
      ]);

      expect(Date.now() - startedAt).toBeLessThan(1_500);
      expect(stats).toEqual({
        queueName: MessageQueue.cronQueue,
        created: 0,
        active: 0,
        completed: 0,
        failed: 0,
        retry: 0,
        healthy: false,
      });
    } finally {
      clearTimeout(safetyTimeout);
      await driver.onModuleDestroy();
    }
  });
});

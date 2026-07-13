import { BullMQDriver } from 'src/engine/core-modules/message-queue/drivers/bullmq.driver';
import { type MessageQueueDriver } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface';
import { type MessageQueueJobRecord } from 'src/engine/core-modules/message-queue/drivers/interfaces/message-queue-job-record.type';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';
import { MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

const createDriver = (
  connection: ConstructorParameters<typeof BullMQDriver>[0]['connection'] = {
    host: '127.0.0.1',
    port: 1,
  },
) =>
  new BullMQDriver(
    { connection },
    {} as MetricsService,
    {} as TwentyConfigService,
  );

const injectQueue = (
  driver: BullMQDriver,
  queue: { waitUntilReady: () => Promise<unknown> },
) => {
  Object.defineProperty(driver, 'queueMap', {
    value: { [MessageQueue.cronQueue]: queue },
  });
};

const createJob = (
  state: MessageQueueJobRecord['state'],
): MessageQueueJobRecord => ({
  id: state,
  name: 'health-job',
  data: {},
  state,
  attemptsMade: 0,
  createdAt: Date.now(),
});

describe('BullMQDriver health', () => {
  beforeEach(() => {
    jest.useRealTimers();
  });

  it('shares one pending stats inspection through caller timeouts and recovery', async () => {
    jest.useFakeTimers();

    let resolveReadiness: (() => void) | undefined;
    const readiness = new Promise<void>((resolve) => {
      resolveReadiness = resolve;
    });
    const driver = createDriver();
    const queue = { waitUntilReady: jest.fn(() => readiness) };

    injectQueue(driver, queue);

    const findJobs = jest.spyOn(driver, 'findJobs').mockResolvedValue([]);

    const firstStats = driver.getStats(MessageQueue.cronQueue);

    await jest.advanceTimersByTimeAsync(1_000);
    await expect(firstStats).resolves.toMatchObject({ healthy: false });

    const secondStats = driver.getStats(MessageQueue.cronQueue);

    await jest.advanceTimersByTimeAsync(1_000);
    await expect(secondStats).resolves.toMatchObject({ healthy: false });

    const recoveredStats = driver.getStats(MessageQueue.cronQueue);

    resolveReadiness?.();

    await expect(recoveredStats).resolves.toMatchObject({ healthy: true });
    expect(queue.waitUntilReady).toHaveBeenCalledTimes(1);
    expect(findJobs).toHaveBeenCalledTimes(1);

    await expect(
      driver.getStats(MessageQueue.cronQueue),
    ).resolves.toMatchObject({ healthy: true });
    expect(queue.waitUntilReady).toHaveBeenCalledTimes(2);
    expect(findJobs).toHaveBeenCalledTimes(2);
  });

  it('cleans up a rejected inspection before the next health probe', async () => {
    const driver = createDriver();
    const queue = {
      waitUntilReady: jest
        .fn()
        .mockRejectedValueOnce(new Error('Redis unavailable'))
        .mockResolvedValue(undefined),
    };

    injectQueue(driver, queue);

    const findJobs = jest.spyOn(driver, 'findJobs').mockResolvedValue([]);

    await expect(
      driver.getStats(MessageQueue.cronQueue),
    ).resolves.toMatchObject({ healthy: false });
    await expect(
      driver.getStats(MessageQueue.cronQueue),
    ).resolves.toMatchObject({ healthy: true });

    expect(queue.waitUntilReady).toHaveBeenCalledTimes(2);
    expect(findJobs).toHaveBeenCalledTimes(1);
  });

  it('returns healthy stats after a successful inspection', async () => {
    const driver = createDriver();
    const queue = { waitUntilReady: jest.fn().mockResolvedValue(undefined) };

    injectQueue(driver, queue);

    jest
      .spyOn(driver, 'findJobs')
      .mockResolvedValue([createJob('created'), createJob('completed')]);

    await expect(driver.getStats(MessageQueue.cronQueue)).resolves.toEqual({
      queueName: MessageQueue.cronQueue,
      created: 1,
      active: 0,
      completed: 1,
      failed: 0,
      retry: 0,
      healthy: true,
    });
  });

  it('bounds repeated probes while Redis keeps reconnecting and destroys cleanly', async () => {
    const driver = createDriver({
      host: '127.0.0.1',
      port: 1,
      connectTimeout: 50,
      maxRetriesPerRequest: null,
      retryStrategy: () => 25,
    });
    const neutralDriver: MessageQueueDriver = driver;

    driver.register(MessageQueue.cronQueue);

    try {
      for (let probe = 0; probe < 3; probe += 1) {
        const startedAt = Date.now();
        const stats = await neutralDriver.getStats(MessageQueue.cronQueue);
        const elapsedMs = Date.now() - startedAt;

        expect(elapsedMs).toBeGreaterThanOrEqual(900);
        expect(elapsedMs).toBeLessThan(1_500);
        expect(stats).toEqual({
          queueName: MessageQueue.cronQueue,
          created: 0,
          active: 0,
          completed: 0,
          failed: 0,
          retry: 0,
          healthy: false,
        });
      }
    } finally {
      await driver.onModuleDestroy();
    }
  }, 7_500);
});

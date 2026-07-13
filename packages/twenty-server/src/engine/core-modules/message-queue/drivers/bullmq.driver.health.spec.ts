import { execFile } from 'node:child_process';
import { resolve } from 'node:path';
import { promisify } from 'node:util';

import { BullMQDriver } from 'src/engine/core-modules/message-queue/drivers/bullmq.driver';
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

const execFileAsync = promisify(execFile);
const serverRoot = resolve(__dirname, '../../../../..');

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

  it('exits cleanly after repeated reconnecting probes and driver destruction', async () => {
    const { stderr, stdout } = await execFileAsync(
      process.execPath,
      [
        resolve(serverRoot, '../../node_modules/tsx/dist/cli.mjs'),
        resolve(__dirname, 'testing/fixtures/bullmq-health-process.fixture.ts'),
      ],
      {
        cwd: serverRoot,
        timeout: 10_000,
        windowsHide: true,
      },
    );

    expect(stdout).toContain('BULLMQ_HEALTH_PROCESS_OK');
    expect(stderr).not.toContain('ECONNREFUSED');
  }, 12_000);
});

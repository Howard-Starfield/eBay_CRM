import { PgBoss } from 'pg-boss';

import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

import { PgBossDriver, type PgBossDriverOptions } from './pg-boss.driver';

jest.mock('pg-boss', () => ({
  PgBoss: jest.fn(),
}));

type BossMock = {
  start: jest.Mock;
  stop: jest.Mock;
  createQueue: jest.Mock;
  send: jest.Mock;
  work: jest.Mock;
  findJobs: jest.Mock;
  getQueueStats: jest.Mock;
  retry: jest.Mock;
  deleteJob: jest.Mock;
};

const queueName = MessageQueue.aiQueue;

const createBossMock = (): BossMock => ({
  start: jest.fn().mockResolvedValue(undefined),
  stop: jest.fn().mockResolvedValue(undefined),
  createQueue: jest.fn().mockResolvedValue(undefined),
  send: jest.fn().mockResolvedValue('job-id'),
  work: jest.fn().mockResolvedValue('worker-id'),
  findJobs: jest.fn().mockResolvedValue([]),
  getQueueStats: jest.fn().mockResolvedValue([]),
  retry: jest.fn().mockResolvedValue({}),
  deleteJob: jest.fn().mockResolvedValue({}),
});

const options: PgBossDriverOptions = {
  connectionString: 'postgresql://postgres:postgres@localhost/runtime',
  schema: 'desktop_runtime',
  applicationName: 'ebaycrm-message-queue',
};

const metricsService = {} as MetricsService;
const twentyConfigService = {
  get: jest.fn((key: string) => {
    if (key === 'AI_STREAM_SHUTDOWN_DRAIN_MS') {
      return 275;
    }

    throw new Error(`Unexpected config key: ${key}`);
  }),
} as unknown as TwentyConfigService;

describe('PgBossDriver', () => {
  let boss: BossMock;
  let driver: PgBossDriver;

  beforeEach(() => {
    boss = createBossMock();
    jest.mocked(PgBoss).mockImplementation(() => boss as never);
    driver = new PgBossDriver(options, metricsService, twentyConfigService);
  });

  it('uses the isolated schema and starts registered queues exactly once', async () => {
    driver.register(queueName);

    await Promise.all([driver.onModuleInit(), driver.onModuleInit()]);

    expect(PgBoss).toHaveBeenCalledWith({
      connectionString: options.connectionString,
      schema: 'desktop_runtime',
      application_name: options.applicationName,
    });
    expect(boss.start).toHaveBeenCalledTimes(1);
    expect(boss.createQueue).toHaveBeenCalledTimes(1);
    expect(boss.createQueue).toHaveBeenCalledWith(queueName);
  });

  it('maps logical id, lower numeric priority, and exact retry count into a versioned envelope', async () => {
    await driver.add(
      queueName,
      'reply-to-buyer',
      { messageId: 'message-1' },
      { id: 'buyer-message-1', priority: 2, retryLimit: 4 },
    );

    expect(boss.findJobs).toHaveBeenCalledWith(queueName, {
      queued: true,
      data: { logicalId: 'buyer-message-1' },
    });
    expect(boss.createQueue).toHaveBeenCalledWith(queueName);
    expect(boss.send).toHaveBeenCalledWith(
      queueName,
      {
        version: 1,
        jobName: 'reply-to-buyer',
        logicalId: 'buyer-message-1',
        data: { messageId: 'message-1' },
      },
      { priority: -2, retryLimit: 4, deleteAfterSeconds: 14_400 },
    );
  });

  it('deduplicates only matching queued logical ids', async () => {
    boss.findJobs.mockResolvedValue([{ id: 'existing' }]);

    await driver.add(queueName, 'reply-to-buyer', {}, { id: 'same-id' });

    expect(boss.send).not.toHaveBeenCalled();
  });

  it('retries queue creation after a transient failure', async () => {
    boss.createQueue
      .mockRejectedValueOnce(new Error('database unavailable'))
      .mockResolvedValueOnce(undefined);

    await expect(driver.add(queueName, 'first-attempt', {})).rejects.toThrow(
      'database unavailable',
    );
    await expect(
      driver.add(queueName, 'second-attempt', {}),
    ).resolves.toBeUndefined();

    expect(boss.createQueue).toHaveBeenCalledTimes(2);
    expect(boss.send).toHaveBeenCalledTimes(1);
  });

  it('maps millisecond delays to an absolute startAfter date', async () => {
    const before = Date.now();

    await driver.add(queueName, 'delayed-reply', {}, { delay: 325 });

    const sendOptions = boss.send.mock.calls[0][2] as { startAfter: Date };

    expect(sendOptions.startAfter).toBeInstanceOf(Date);
    expect(sendOptions.startAfter.getTime()).toBeGreaterThanOrEqual(
      before + 325,
    );
    expect(sendOptions.startAfter.getTime()).toBeLessThanOrEqual(
      Date.now() + 325,
    );
  });

  it('maps concurrency and unwraps the envelope while forwarding abort signal', async () => {
    const handler = jest.fn().mockResolvedValue(undefined);
    const signal = new AbortController().signal;

    await driver.work(queueName, handler, { concurrency: 3 });

    expect(boss.work).toHaveBeenCalledWith(
      queueName,
      { localConcurrency: 3 },
      expect.any(Function),
    );

    const workHandler = boss.work.mock.calls[0][2] as (
      jobs: Array<{
        id: string;
        signal: AbortSignal;
        data: {
          version: number;
          jobName: string;
          data: { messageId: string };
        };
      }>,
    ) => Promise<void>;

    await workHandler([
      {
        id: 'pg-job-id',
        signal,
        data: {
          version: 1,
          jobName: 'reply-to-buyer',
          data: { messageId: 'message-1' },
        },
      },
    ]);

    expect(handler).toHaveBeenCalledWith({
      id: 'pg-job-id',
      name: 'reply-to-buyer',
      data: { messageId: 'message-1' },
      abortSignal: signal,
    });
  });

  it('uses the configured graceful timeout when any worker requires bounded drain', async () => {
    await driver.onModuleInit();
    await driver.work(queueName, jest.fn(), { boundedShutdownDrain: true });

    await Promise.all([driver.onModuleDestroy(), driver.onModuleDestroy()]);

    expect(boss.stop).toHaveBeenCalledTimes(1);
    expect(boss.stop).toHaveBeenCalledWith({
      close: true,
      graceful: true,
      timeout: 275,
    });
  });

  it('does not stop a driver that was never started', async () => {
    await driver.onModuleDestroy();

    expect(boss.stop).not.toHaveBeenCalled();
  });

  it('maps pg-boss metadata into neutral records and inspection operations', async () => {
    const signal = new AbortController().signal;
    boss.findJobs.mockResolvedValue([
      {
        id: 'failed-id',
        name: queueName,
        data: {
          version: 1,
          jobName: 'reply-to-buyer',
          data: { messageId: 'message-1' },
        },
        signal,
        state: 'failed',
        retryCount: 2,
        createdOn: new Date('2026-07-13T01:00:00.000Z'),
        startedOn: new Date('2026-07-13T01:00:01.000Z'),
        completedOn: new Date('2026-07-13T01:00:02.000Z'),
        output: { message: 'delivery failed' },
      },
    ]);

    const records = await driver.findJobs(queueName, ['failed']);

    expect(records).toEqual([
      {
        id: 'failed-id',
        name: 'reply-to-buyer',
        data: { messageId: 'message-1' },
        state: 'failed',
        attemptsMade: 3,
        createdAt: Date.parse('2026-07-13T01:00:00.000Z'),
        processedAt: Date.parse('2026-07-13T01:00:01.000Z'),
        finishedAt: Date.parse('2026-07-13T01:00:02.000Z'),
        failedReason: 'delivery failed',
      },
    ]);

    await driver.retryJob(queueName, 'failed-id');
    await driver.deleteJob(queueName, 'failed-id');

    expect(boss.retry).toHaveBeenCalledWith(queueName, 'failed-id');
    expect(boss.deleteJob).toHaveBeenCalledWith(queueName, 'failed-id');
  });

  it('uses pg-boss queue stats as the health probe and neutral records for counts', async () => {
    boss.getQueueStats.mockResolvedValue([
      {
        name: queueName,
        deferredCount: 0,
        queuedCount: 2,
        readyCount: 2,
        activeCount: 1,
        failedCount: 1,
        totalCount: 5,
        capturedOn: new Date(),
      },
    ]);
    boss.findJobs.mockResolvedValue([
      { state: 'created' },
      { state: 'retry' },
      { state: 'active' },
      { state: 'completed' },
      { state: 'failed' },
    ]);

    await expect(driver.getStats(queueName)).resolves.toEqual({
      queueName,
      created: 1,
      retry: 1,
      active: 1,
      completed: 1,
      failed: 1,
      healthy: true,
    });
    expect(boss.getQueueStats).toHaveBeenCalledWith(queueName, { force: true });
  });
});

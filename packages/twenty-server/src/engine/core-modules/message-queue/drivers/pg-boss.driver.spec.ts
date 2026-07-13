import { Pool } from 'pg';
import { PgBoss } from 'pg-boss';

import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

import { PgBossDriver, type PgBossDriverOptions } from './pg-boss.driver';

jest.mock('pg-boss', () => ({
  PgBoss: jest.fn(),
}));
jest.mock('pg', () => ({
  Pool: jest.fn(),
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
  schedule: jest.Mock;
  unschedule: jest.Mock;
  updateQueue: jest.Mock;
  supervise: jest.Mock;
};

type RetentionJob = {
  id: string;
  state: 'completed' | 'failed';
  createdOn: Date;
  completedOn: Date;
};

type PoolClientMock = {
  query: jest.Mock;
  release: jest.Mock;
};

type PoolMock = {
  connect: jest.Mock;
  query: jest.Mock;
  end: jest.Mock;
};

type PgBossDriverRetentionInternals = {
  cleanupTerminalJobs(): Promise<void>;
  retentionCleanupTimer?: ReturnType<typeof setInterval>;
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
  schedule: jest.fn().mockResolvedValue(undefined),
  unschedule: jest.fn().mockResolvedValue(undefined),
  updateQueue: jest.fn().mockResolvedValue(undefined),
  supervise: jest.fn().mockResolvedValue(undefined),
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
  let pool: PoolMock;
  let poolClient: PoolClientMock;
  let driver: PgBossDriver;

  beforeEach(() => {
    jest.useRealTimers();
    boss = createBossMock();
    poolClient = {
      query: jest.fn().mockResolvedValue({ rows: [] }),
      release: jest.fn(),
    };
    pool = {
      connect: jest.fn().mockResolvedValue(poolClient),
      query: jest.fn().mockResolvedValue({ rows: [] }),
      end: jest.fn().mockResolvedValue(undefined),
    };
    jest.mocked(PgBoss).mockImplementation(() => boss as never);
    jest.mocked(Pool).mockImplementation(() => pool as never);
    driver = new PgBossDriver(options, metricsService, twentyConfigService);
  });

  afterEach(async () => {
    await driver.onModuleDestroy();
  });

  it('uses the isolated schema and starts registered queues exactly once', async () => {
    driver.register(queueName);

    await Promise.all([driver.onModuleInit(), driver.onModuleInit()]);

    expect(PgBoss).toHaveBeenCalledWith({
      connectionString: options.connectionString,
      schema: 'desktop_runtime',
      application_name: options.applicationName,
      superviseIntervalSeconds: 1,
    });
    expect(Pool).toHaveBeenCalledWith({
      connectionString: options.connectionString,
      application_name: `${options.applicationName}-coordination`,
      connectionTimeoutMillis: 1_000,
      query_timeout: 1_000,
      statement_timeout: 1_000,
    });
    expect(boss.start).toHaveBeenCalledTimes(1);
    expect(boss.createQueue).toHaveBeenCalledTimes(1);
    expect(boss.createQueue).toHaveBeenCalledWith(queueName, {
      retryLimit: 0,
    });
  });

  it('maps logical id, lower numeric priority, and exact retry count into a versioned envelope', async () => {
    await driver.add(
      queueName,
      'reply-to-buyer',
      { messageId: 'message-1' },
      { id: 'buyer-message-1', priority: 2, retryLimit: 4 },
    );

    expect(boss.findJobs).toHaveBeenCalledWith(
      queueName,
      expect.objectContaining({
        queued: true,
        data: { logicalId: 'buyer-message-1' },
        db: expect.objectContaining({ executeSql: expect.any(Function) }),
      }),
    );
    expect(boss.createQueue).toHaveBeenCalledWith(queueName, {
      retryLimit: 0,
    });
    expect(boss.send).toHaveBeenCalledWith(
      queueName,
      {
        version: 1,
        jobName: 'reply-to-buyer',
        logicalId: 'buyer-message-1',
        data: { messageId: 'message-1' },
      },
      expect.objectContaining({
        priority: -2,
        retryLimit: 4,
        deleteAfterSeconds: 604_800,
        db: expect.objectContaining({ executeSql: expect.any(Function) }),
      }),
    );
    expect(poolClient.query).toHaveBeenNthCalledWith(1, 'BEGIN');
    expect(poolClient.query).toHaveBeenNthCalledWith(
      2,
      'SELECT pg_advisory_xact_lock(hashtextextended($1, 0))',
      [JSON.stringify([queueName, 'buyer-message-1'])],
    );
    expect(poolClient.query).toHaveBeenNthCalledWith(3, 'COMMIT');
    expect(poolClient.release).toHaveBeenCalledTimes(1);
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

  it('upserts and removes a deterministic pg-boss cron schedule', async () => {
    await driver.addCron({
      queueName,
      jobName: 'reply-to-buyer',
      data: { version: 2 },
      jobId: 'buyer-1',
      options: {
        priority: 2,
        retryLimit: 3,
        repeat: { pattern: '*/1 * * * * *' },
      },
    });

    expect(boss.unschedule).toHaveBeenCalledWith(
      queueName,
      'reply-to-buyer.buyer-1',
    );
    expect(pool.query).toHaveBeenCalledWith(
      expect.stringContaining('INSERT INTO desktop_runtime.cron_schedule'),
      [
        `${queueName}:reply-to-buyer.buyer-1`,
        queueName,
        'reply-to-buyer',
        {
          version: 1,
          jobName: 'reply-to-buyer',
          data: { version: 2 },
          intervalOptions: { priority: -2, retryLimit: 3 },
        },
        '*/1 * * * * *',
        null,
        expect.any(Date),
      ],
    );
    expect(boss.schedule).not.toHaveBeenCalled();

    await driver.removeCron({
      queueName,
      jobName: 'reply-to-buyer',
      jobId: 'buyer-1',
    });

    expect(boss.unschedule).toHaveBeenCalledWith(
      queueName,
      'reply-to-buyer.buyer-1',
    );
    expect(pool.query).toHaveBeenCalledWith(
      expect.stringContaining('DELETE FROM desktop_runtime.interval_schedule'),
      [`${queueName}:reply-to-buyer.buyer-1`],
    );
    expect(pool.query).toHaveBeenCalledWith(
      expect.stringContaining('DELETE FROM desktop_runtime.cron_schedule'),
      [`${queueName}:reply-to-buyer.buyer-1`],
    );
  });

  it('persists interval schedules and rejects ambiguous repeat definitions', async () => {
    await driver.addCron({
      queueName,
      jobName: 'refresh-order',
      data: { version: 1 },
      jobId: 'order-1',
      options: {
        priority: 2,
        retryLimit: 3,
        repeat: { every: 250, limit: 2 },
      },
    });

    expect(pool.query).toHaveBeenCalledWith(
      expect.stringContaining('ON CONFLICT (schedule_key) DO UPDATE'),
      [
        `${queueName}:refresh-order.order-1`,
        queueName,
        'refresh-order',
        {
          version: 1,
          jobName: 'refresh-order',
          data: { version: 1 },
          intervalOptions: {
            priority: -2,
            retryLimit: 3,
          },
        },
        250,
        2,
      ],
    );

    await expect(
      driver.addCron({
        queueName,
        jobName: 'invalid-repeat',
        data: {},
        options: {
          repeat: { every: 250, pattern: '*/1 * * * * *' },
        },
      }),
    ).rejects.toThrow('exactly one of repeat.every or repeat.pattern');
  });

  it('persists a cron occurrence limit for enqueue-time consumption', async () => {
    await driver.addCron({
      queueName,
      jobName: 'limited-cron',
      data: { version: 1 },
      jobId: 'buyer-1',
      options: {
        repeat: { pattern: '*/1 * * * * *', limit: 2 },
      },
    });
    await driver.addCron({
      queueName,
      jobName: 'limited-cron',
      data: { version: 1 },
      jobId: 'buyer-1',
      options: {
        repeat: { pattern: '*/1 * * * * *', limit: 2 },
      },
    });

    const persistedScheduleKey = `${queueName}:limited-cron.buyer-1`;

    expect(pool.query).toHaveBeenCalledWith(
      expect.stringContaining('INSERT INTO desktop_runtime.cron_schedule'),
      expect.arrayContaining([persistedScheduleKey, queueName, 2]),
    );
    expect(boss.schedule).not.toHaveBeenCalled();
    expect(boss.unschedule).toHaveBeenCalledWith(
      queueName,
      'limited-cron.buyer-1',
    );
  });

  it('removes the prior recurring mechanism when changing schedule type', async () => {
    await driver.addCron({
      queueName,
      jobName: 'switching-schedule',
      data: {},
      jobId: 'one',
      options: { repeat: { every: 500 } },
    });

    expect(boss.unschedule).toHaveBeenCalledWith(
      queueName,
      'switching-schedule.one',
    );

    await driver.addCron({
      queueName,
      jobName: 'switching-schedule',
      data: {},
      jobId: 'one',
      options: { repeat: { pattern: '*/1 * * * * *' } },
    });

    expect(pool.query).toHaveBeenCalledWith(
      expect.stringContaining('DELETE FROM desktop_runtime.interval_schedule'),
      [`${queueName}:switching-schedule.one`],
    );
  });

  it('maps lock duration and stalled count to pg-boss queue recovery options', async () => {
    await driver.work(queueName, jest.fn(), {
      lockDuration: 500,
      maxStalledCount: 2,
    });

    expect(boss.updateQueue).toHaveBeenCalledWith(queueName, {
      heartbeatSeconds: 10,
      retryLimit: 2,
    });
    expect(boss.work).toHaveBeenCalledWith(
      queueName,
      expect.objectContaining({ heartbeatRefreshSeconds: 5 }),
      expect.any(Function),
    );

    await driver.add(queueName, 'recoverable-job', {});

    expect(boss.send).toHaveBeenCalledWith(
      queueName,
      expect.any(Object),
      expect.not.objectContaining({ retryLimit: expect.anything() }),
    );
  });

  it('enables pg-boss heartbeats when the rounded lock duration meets its ten-second minimum', async () => {
    await driver.work(queueName, jest.fn(), {
      lockDuration: 10_100,
      maxStalledCount: 1,
    });

    expect(boss.updateQueue).toHaveBeenCalledWith(queueName, {
      heartbeatSeconds: 11,
      retryLimit: 1,
    });
    expect(boss.work).toHaveBeenCalledWith(
      queueName,
      expect.objectContaining({ heartbeatRefreshSeconds: 5.5 }),
      expect.any(Function),
    );
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
    expect(pool.end).toHaveBeenCalledTimes(1);
  });

  it('does not leak maintenance when destruction races initialization', async () => {
    let releaseStart: () => void = () => undefined;
    const startGate = new Promise<void>((resolve) => {
      releaseStart = resolve;
    });

    boss.start.mockImplementation(async () => {
      await startGate;
    });
    driver.register(queueName);

    const initialize = driver.onModuleInit();
    const destroy = driver.onModuleDestroy();

    releaseStart();
    await Promise.all([initialize, destroy]);

    expect(boss.stop).toHaveBeenCalledTimes(1);
    expect(pool.end).toHaveBeenCalledTimes(1);
    expect(
      (
        driver as unknown as {
          retentionCleanupTimer?: ReturnType<typeof setInterval>;
        }
      ).retentionCleanupTimer,
    ).toBeUndefined();
  });

  it('starts and stops the retention maintenance timer with the driver', async () => {
    driver.register(queueName);

    await driver.onModuleInit();

    expect(
      (driver as unknown as PgBossDriverRetentionInternals)
        .retentionCleanupTimer,
    ).toBeDefined();

    await driver.onModuleDestroy();

    expect(
      (driver as unknown as PgBossDriverRetentionInternals)
        .retentionCleanupTimer,
    ).toBeUndefined();
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

  it('retains failures for seven days while cleaning completed jobs after four hours', async () => {
    const now = Date.now();
    const terminalJob = (
      id: string,
      state: RetentionJob['state'],
      ageMs: number,
    ): RetentionJob => ({
      id,
      state,
      createdOn: new Date(now - ageMs),
      completedOn: new Date(now - ageMs),
    });

    boss.findJobs.mockResolvedValue([
      terminalJob('completed-expired', 'completed', 4 * 60 * 60 * 1000 + 1),
      terminalJob('failed-still-retained', 'failed', 4 * 60 * 60 * 1000 + 1),
      terminalJob('failed-expired', 'failed', 7 * 24 * 60 * 60 * 1000 + 1),
    ]);
    driver.register(queueName);
    await driver.onModuleInit();

    await (
      driver as unknown as PgBossDriverRetentionInternals
    ).cleanupTerminalJobs();

    expect(boss.deleteJob).toHaveBeenCalledTimes(2);
    expect(boss.deleteJob).toHaveBeenCalledWith(queueName, 'completed-expired');
    expect(boss.deleteJob).toHaveBeenCalledWith(queueName, 'failed-expired');
    expect(boss.deleteJob).not.toHaveBeenCalledWith(
      queueName,
      'failed-still-retained',
    );
    await driver.onModuleDestroy();
  });

  it('caps completed and failed retention independently at one thousand jobs', async () => {
    const now = Date.now();
    const jobs: RetentionJob[] = ['completed', 'failed'].flatMap((state) =>
      Array.from({ length: 1_001 }, (_, index) => ({
        id: `${state}-${index}`,
        state: state as RetentionJob['state'],
        createdOn: new Date(now - index),
        completedOn: new Date(now - index),
      })),
    );

    boss.findJobs.mockResolvedValue(jobs);
    driver.register(queueName);
    await driver.onModuleInit();

    await (
      driver as unknown as PgBossDriverRetentionInternals
    ).cleanupTerminalJobs();

    expect(boss.deleteJob).toHaveBeenCalledTimes(2);
    expect(boss.deleteJob).toHaveBeenCalledWith(queueName, 'completed-1000');
    expect(boss.deleteJob).toHaveBeenCalledWith(queueName, 'failed-1000');
    await driver.onModuleDestroy();
  });
});

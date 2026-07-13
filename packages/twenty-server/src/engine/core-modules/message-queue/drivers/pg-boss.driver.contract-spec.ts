import { randomUUID } from 'node:crypto';

import { PgBoss } from 'pg-boss';

import {
  PgBossDriver,
  type PgBossDriverOptions,
} from 'src/engine/core-modules/message-queue/drivers/pg-boss.driver';
import { defineMessageQueueDriverContract } from 'src/engine/core-modules/message-queue/drivers/testing/message-queue-driver.contract';
import {
  type CreateMessageQueueDriverTestHarness,
  type MessageQueueDriverTestHarness,
} from 'src/engine/core-modules/message-queue/drivers/testing/message-queue-driver-test-harness';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

type PgBossDriverInternals = {
  boss: PgBoss;
  started: boolean;
};

const runtimeContractConnectionString = (): string => {
  const connectionString =
    process.env.RUNTIME_CONTRACT_DATABASE_URL ?? process.env.PG_DATABASE_URL;

  if (!connectionString) {
    throw new Error(
      'RUNTIME_CONTRACT_DATABASE_URL or PG_DATABASE_URL is required for the pg-boss runtime contract',
    );
  }

  return connectionString;
};

const pgBossOptions = () => ({
  connectionString: runtimeContractConnectionString(),
  schema: 'desktop_runtime',
  application_name: 'ebaycrm-runtime-contract-cleanup',
});

const deleteQueueContentsAndMetadata = async (
  boss: PgBoss,
  queueName: string,
): Promise<void> => {
  try {
    await boss.deleteAllJobs(queueName);
  } finally {
    await boss.deleteQueue(queueName);
  }
};

const deletePgBossQueue = async (queueName: string): Promise<void> => {
  const boss = new PgBoss(pgBossOptions());

  await boss.start();
  try {
    await deleteQueueContentsAndMetadata(boss, queueName);
  } finally {
    await boss.stop({ close: true, graceful: true });
  }
};

const deleteRuntimeContractQueues = async (): Promise<void> => {
  const boss = new PgBoss(pgBossOptions());

  await boss.start();
  try {
    const queues = await boss.getQueues();

    const results = await Promise.allSettled(
      queues
        .filter(({ name }) => name.startsWith('runtime-contract-'))
        .map(({ name }) => deleteQueueContentsAndMetadata(boss, name)),
    );
    const failures = results
      .filter((result) => result.status === 'rejected')
      .map((result) => result.reason);

    if (failures.length > 0) {
      throw new Error(
        `Failed to delete one or more runtime-contract pg-boss queues: ${failures.map(String).join('; ')}`,
      );
    }
  } finally {
    await boss.stop({ close: true, graceful: true });
  }
};

const within = async <T>(
  operation: Promise<T>,
  label: string,
  timeoutMs = 10_000,
): Promise<T> => {
  let timeout: ReturnType<typeof setTimeout> | undefined;

  try {
    return await Promise.race([
      operation,
      new Promise<never>((_resolve, reject) => {
        timeout = setTimeout(
          () => reject(new Error(`${label} exceeded ${timeoutMs}ms`)),
          timeoutMs,
        );
      }),
    ]);
  } finally {
    clearTimeout(timeout);
  }
};

const createHarness: CreateMessageQueueDriverTestHarness = async ({
  queueName,
  handler,
  workerOptions,
  shutdownDrainMs = 250,
}) => {
  const connectionString = runtimeContractConnectionString();

  const driverOptions: PgBossDriverOptions = {
    connectionString,
    schema: 'desktop_runtime',
    applicationName: 'ebaycrm-runtime-contract',
    intervalPollMs: 250,
  };
  const metricsService = {} as MetricsService;
  const twentyConfigService = {
    get: (key: string) => {
      if (key === 'AI_STREAM_SHUTDOWN_DRAIN_MS') {
        return shutdownDrainMs;
      }

      throw new Error(`Unexpected config key in queue contract: ${key}`);
    },
  } as TwentyConfigService;
  let driver: PgBossDriver;
  let workerStarted = false;
  let stopped = false;

  const buildDriver = async () => {
    const nextDriver = new PgBossDriver(
      driverOptions,
      metricsService,
      twentyConfigService,
    );

    nextDriver.register(queueName);
    await nextDriver.onModuleInit();

    return nextDriver;
  };

  driver = await buildDriver();

  const start = async () => {
    if (workerStarted) {
      return;
    }

    await driver.work(queueName, handler, workerOptions);
    workerStarted = true;
    stopped = false;
  };

  const stop = async () => {
    if (stopped) {
      return;
    }

    await driver.onModuleDestroy();
    workerStarted = false;
    stopped = true;
  };

  const terminateWorker = async () => {
    if (stopped) {
      return;
    }

    const internals = driver as unknown as PgBossDriverInternals;

    await internals.boss.stop({ close: true, graceful: false, timeout: 1_000 });
    internals.started = false;
    await driver.onModuleDestroy();
    workerStarted = false;
    stopped = true;
  };

  const harness: MessageQueueDriverTestHarness = {
    get driver() {
      return driver;
    },
    queueName,
    start,
    stop,
    async clear() {
      await stop();
      await deletePgBossQueue(queueName);
    },
    async waitFor(predicate, timeoutMs) {
      const deadline = Date.now() + timeoutMs;

      while (Date.now() < deadline) {
        if (await predicate()) {
          return;
        }

        await new Promise((resolve) => setTimeout(resolve, 25));
      }

      throw new Error(`Condition was not met within ${timeoutMs}ms`);
    },
    async restartWorker() {
      await terminateWorker();
      driver = await buildDriver();
      workerStarted = false;
      stopped = false;
      await start();
    },
    terminateWorker,
  };

  return harness;
};

if (process.env.RUNTIME_CONTRACT_DRIVER === 'pg-boss') {
  beforeAll(async () => {
    jest.useRealTimers();
    await deleteRuntimeContractQueues();
  });

  afterAll(async () => {
    await deleteRuntimeContractQueues();
  });

  describe('pg-boss adapter integration regressions', () => {
    it('atomically deduplicates concurrent waiting jobs across driver instances', async () => {
      const first = await createHarness({
        queueName: `runtime-contract-concurrent-dedup-${randomUUID()}` as never,
        handler: () => undefined,
      });
      const second = await createHarness({
        queueName: first.queueName,
        handler: () => undefined,
      });

      try {
        await within(
          Promise.all(
            Array.from({ length: 20 }, (_, index) =>
              (index % 2 === 0 ? first : second).driver.add(
                first.queueName,
                'concurrent-dedup',
                { index },
                { id: 'one-logical-job' },
              ),
            ),
          ),
          'concurrent add',
        );

        await expect(
          within(
            first.driver.findJobs(first.queueName, ['created']),
            'dedup inspection',
          ),
        ).resolves.toHaveLength(1);
      } finally {
        const cleanupFailures: unknown[] = [];

        try {
          const stopResults = await within(
            Promise.allSettled([first.stop(), second.stop()]),
            'driver stop',
          );

          cleanupFailures.push(
            ...stopResults
              .filter((result) => result.status === 'rejected')
              .map((result) => result.reason),
          );
        } catch (error) {
          cleanupFailures.push(error);
        }

        try {
          await within(first.clear(), 'first harness clear');
        } catch (error) {
          cleanupFailures.push(error);
        }

        if (cleanupFailures.length > 0) {
          throw new Error(
            `Failed to clean concurrent pg-boss contract harnesses: ${cleanupFailures.map(String).join('; ')}`,
          );
        }
      }
    });
  });

  defineMessageQueueDriverContract('pg-boss', createHarness);
} else {
  describe.skip('pg-boss message queue driver contract', () => {
    it('requires RUNTIME_CONTRACT_DRIVER=pg-boss', () => undefined);
  });
}

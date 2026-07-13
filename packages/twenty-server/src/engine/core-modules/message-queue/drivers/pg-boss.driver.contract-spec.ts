import { type PgBoss } from 'pg-boss';

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
};

const createHarness: CreateMessageQueueDriverTestHarness = async ({
  queueName,
  handler,
  workerOptions,
  shutdownDrainMs = 250,
}) => {
  const connectionString =
    process.env.RUNTIME_CONTRACT_DATABASE_URL ?? process.env.PG_DATABASE_URL;

  if (!connectionString) {
    throw new Error(
      'RUNTIME_CONTRACT_DATABASE_URL or PG_DATABASE_URL is required for the pg-boss runtime contract',
    );
  }

  const driverOptions: PgBossDriverOptions = {
    connectionString,
    schema: 'desktop_runtime',
    applicationName: 'ebaycrm-runtime-contract',
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

    const boss = (driver as unknown as PgBossDriverInternals).boss;

    await boss.stop({ close: true, graceful: false, timeout: 1_000 });
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
      if (stopped) {
        driver = await buildDriver();
        stopped = false;
      }

      const boss = (driver as unknown as PgBossDriverInternals).boss;

      await boss.deleteAllJobs(queueName);
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
  defineMessageQueueDriverContract('pg-boss', createHarness);
} else {
  describe.skip('pg-boss message queue driver contract', () => {
    it('requires RUNTIME_CONTRACT_DRIVER=pg-boss', () => undefined);
  });
}

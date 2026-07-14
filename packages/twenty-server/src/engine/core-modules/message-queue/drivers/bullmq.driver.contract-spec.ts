import { createConnection } from 'node:net';

import {
  BullMQDriver,
  type BullMQDriverOptions,
} from 'src/engine/core-modules/message-queue/drivers/bullmq.driver';
import { defineMessageQueueDriverContract } from 'src/engine/core-modules/message-queue/drivers/testing/message-queue-driver.contract';
import {
  type CreateMessageQueueDriverTestHarness,
  type MessageQueueDriverTestHarness,
} from 'src/engine/core-modules/message-queue/drivers/testing/message-queue-driver-test-harness';
import { type MetricsService } from 'src/engine/core-modules/metrics/metrics.service';
import { type TwentyConfigService } from 'src/engine/core-modules/twenty-config/twenty-config.service';

type ClosableWorker = {
  close(force?: boolean): Promise<void>;
};

type CleanableQueue = {
  obliterate(options: { force: boolean }): Promise<void>;
};

type BullMQDriverInternals = {
  workerMap: Record<string, ClosableWorker>;
  queueMap: Record<string, CleanableQueue>;
};

const reachableRedisUrls = new Map<string, Promise<void>>();

const verifyRedisIsReachable = async (redisUrl: string): Promise<void> => {
  const existingProbe = reachableRedisUrls.get(redisUrl);

  if (existingProbe) {
    return existingProbe;
  }

  const url = new URL(redisUrl);
  const probe = new Promise<void>((resolve, reject) => {
    const socket = createConnection({
      host: url.hostname,
      port: Number(url.port || 6379),
    });
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(
        new Error(`Redis at ${url.hostname}:${url.port || 6379} timed out`),
      );
    }, 1_000);

    socket.once('connect', () => {
      clearTimeout(timeout);
      socket.destroy();
      resolve();
    });
    socket.once('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
  });

  reachableRedisUrls.set(redisUrl, probe);

  return probe;
};

const connectionFromUrl = (redisUrl: string) => {
  const url = new URL(redisUrl);

  return {
    host: url.hostname,
    port: Number(url.port || 6379),
    username: url.username ? decodeURIComponent(url.username) : undefined,
    password: url.password ? decodeURIComponent(url.password) : undefined,
    db: url.pathname.length > 1 ? Number(url.pathname.slice(1)) : 0,
    maxRetriesPerRequest: null,
    ...(url.protocol === 'rediss:' ? { tls: {} } : {}),
  };
};

const createHarness: CreateMessageQueueDriverTestHarness = async ({
  queueName,
  handler,
  workerOptions,
  shutdownDrainMs = 250,
}) => {
  const redisUrl =
    process.env.RUNTIME_CONTRACT_REDIS_URL ??
    process.env.REDIS_QUEUE_URL ??
    process.env.REDIS_URL;

  if (!redisUrl) {
    throw new Error(
      'RUNTIME_CONTRACT_REDIS_URL, REDIS_QUEUE_URL, or REDIS_URL is required for the BullMQ runtime contract',
    );
  }

  await verifyRedisIsReachable(redisUrl);

  const connection = connectionFromUrl(redisUrl);
  const metricsService = {
    createMultiObservableGauge: () => undefined,
    recordHistogram: () => undefined,
    incrementCounterForEvent: async () => undefined,
  } as unknown as MetricsService;
  const twentyConfigService = {
    get: (key: string) => {
      if (key === 'AI_STREAM_SHUTDOWN_DRAIN_MS') {
        return shutdownDrainMs;
      }

      throw new Error(`Unexpected config key in queue contract: ${key}`);
    },
  } as TwentyConfigService;
  let driver: BullMQDriver;
  let workerStarted = false;
  let stopped = false;

  const buildDriver = () => {
    const driverOptions = {
      connection,
      // Keep stalled-job recovery bounded for the lease contract cases.
      stalledInterval: 500,
    } as unknown as BullMQDriverOptions;
    const nextDriver = new BullMQDriver(
      driverOptions,
      metricsService,
      twentyConfigService,
    );

    nextDriver.register(queueName);
    nextDriver.onModuleInit();

    return nextDriver;
  };

  driver = buildDriver();

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
    if (!workerStarted) {
      return;
    }

    const worker = (driver as unknown as BullMQDriverInternals).workerMap[
      queueName
    ];

    await worker.close(true);
    workerStarted = false;
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
        driver = buildDriver();
        stopped = false;
      }

      await terminateWorker();
      const queue = (driver as unknown as BullMQDriverInternals).queueMap[
        queueName
      ];

      if (queue) {
        await queue.obliterate({ force: true });
      }
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
      if (!stopped) {
        await terminateWorker();
        await driver.onModuleDestroy();
      }
      driver = buildDriver();
      workerStarted = false;
      stopped = false;
      await start();
    },
    terminateWorker,
  };

  return harness;
};

if (process.env.RUNTIME_CONTRACT_DRIVER === 'bullmq') {
  defineMessageQueueDriverContract('BullMQ', createHarness);
} else {
  describe.skip('BullMQ message queue driver contract', () => {
    it('requires RUNTIME_CONTRACT_DRIVER=bullmq', () => undefined);
  });
}

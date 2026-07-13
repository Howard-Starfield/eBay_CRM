import { setTimeout as delay } from 'node:timers/promises';

import { BullMQDriver } from 'src/engine/core-modules/message-queue/drivers/bullmq.driver';
import { MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

const run = async () => {
  const driver = new BullMQDriver(
    {
      connection: {
        host: '127.0.0.1',
        port: 1,
        connectTimeout: 50,
        maxRetriesPerRequest: null,
        retryStrategy: () => 25,
      },
    },
    {} as never,
    {} as never,
  );

  driver.register(MessageQueue.cronQueue);

  try {
    for (let probe = 0; probe < 3; probe += 1) {
      const startedAt = Date.now();
      const stats = await driver.getStats(MessageQueue.cronQueue);
      const elapsedMs = Date.now() - startedAt;

      if (elapsedMs < 900 || elapsedMs >= 1_500 || stats.healthy) {
        throw new Error(
          `Unexpected unavailable stats result after ${elapsedMs}ms: ${JSON.stringify(stats)}`,
        );
      }
    }
  } finally {
    let destroyTimeout: ReturnType<typeof setTimeout> | undefined;

    try {
      await Promise.race([
        driver.onModuleDestroy(),
        new Promise<never>((_resolve, reject) => {
          destroyTimeout = setTimeout(
            () => reject(new Error('Driver destruction timed out')),
            2_000,
          );
        }),
      ]);
    } finally {
      clearTimeout(destroyTimeout);
    }
  }

  await delay(500);
  process.stdout.write('BULLMQ_HEALTH_PROCESS_OK\n');
};

void run().catch((error) => {
  process.stderr.write(
    `${error instanceof Error ? (error.stack ?? error.message) : String(error)}\n`,
  );
  process.exitCode = 1;
});

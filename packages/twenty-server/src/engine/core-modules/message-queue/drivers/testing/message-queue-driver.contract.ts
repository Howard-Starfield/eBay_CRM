import { Pool } from 'pg';
import { v4 } from 'uuid';

import { type MessageQueueJob } from 'src/engine/core-modules/message-queue/interfaces/message-queue-job.interface';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

import { type CreateMessageQueueDriverTestHarness } from './message-queue-driver-test-harness';

const WAIT_TIMEOUT_MS = 15_000;
const RECOVERY_TIMEOUT_MS = 45_000;

const uniqueQueueName = (caseName: string): MessageQueue =>
  `runtime-contract-${caseName}-${v4()}` as MessageQueue;

const deferred = () => {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((promiseResolve) => {
    resolve = promiseResolve;
  });

  return { promise, resolve };
};

export const defineMessageQueueDriverContract = (
  name: string,
  createHarness: CreateMessageQueueDriverTestHarness,
) => {
  describe(`${name} message queue driver contract`, () => {
    let cleanup: (() => Promise<void>) | undefined;

    beforeAll(() => {
      jest.useRealTimers();
    });

    afterEach(async () => {
      await cleanup?.();
      cleanup = undefined;
    });

    const useHarness = async (
      caseName: string,
      handler: (job: MessageQueueJob) => Promise<void> | void,
      options?: Parameters<CreateMessageQueueDriverTestHarness>[0]['workerOptions'],
      shutdownDrainMs?: number,
    ) => {
      const harness = await createHarness({
        queueName: uniqueQueueName(caseName),
        handler,
        workerOptions: options,
        shutdownDrainMs,
      });

      cleanup = async () => {
        await harness.clear();
        await harness.stop();
      };

      return harness;
    };

    it('delivers an immediate job with its name and data preserved', async () => {
      const received: MessageQueueJob[] = [];
      const harness = await useHarness('immediate', (job) => {
        received.push(job);
      });

      await harness.start();
      await harness.driver.add(harness.queueName, 'immediate-job', {
        marker: 'preserved',
      });
      await harness.waitFor(() => received.length === 1, WAIT_TIMEOUT_MS);

      expect(received[0]).toMatchObject({
        name: 'immediate-job',
        data: { marker: 'preserved' },
      });
      expect(received[0].id).not.toBe('');
    });

    it('does not deliver a delayed job early and eventually delivers it', async () => {
      const deliveredAt: number[] = [];
      const harness = await useHarness('delay', () => {
        deliveredAt.push(Date.now());
      });
      const addedAt = Date.now();

      await harness.start();
      await harness.driver.add(
        harness.queueName,
        'delayed-job',
        {},
        { delay: 300 },
      );
      await new Promise((resolve) => setTimeout(resolve, 150));
      expect(deliveredAt).toHaveLength(0);
      await harness.waitFor(() => deliveredAt.length === 1, WAIT_TIMEOUT_MS);
      expect(deliveredAt[0] - addedAt).toBeGreaterThanOrEqual(250);
    });

    it('processes lower numeric priority before higher numeric priority', async () => {
      const received: string[] = [];
      const harness = await useHarness('priority', (job) => {
        received.push(job.name);
      });

      await harness.driver.add(
        harness.queueName,
        'lower-precedence',
        {},
        { priority: 7 },
      );
      await harness.driver.add(
        harness.queueName,
        'higher-precedence',
        {},
        { priority: 1 },
      );
      await harness.start();
      await harness.waitFor(() => received.length === 2, WAIT_TIMEOUT_MS);

      expect(received).toEqual(['higher-precedence', 'lower-precedence']);
    });

    it('attempts a job exactly three times when retryLimit is two', async () => {
      let attempts = 0;
      const harness = await useHarness('retries', () => {
        attempts += 1;
        throw new Error('contract failure');
      });

      await harness.start();
      await harness.driver.add(
        harness.queueName,
        'retry-job',
        {},
        { retryLimit: 2 },
      );
      await harness.waitFor(async () => {
        const stats = await harness.driver.getStats(harness.queueName);

        return stats.failed === 1;
      }, WAIT_TIMEOUT_MS);

      expect(attempts).toBe(3);
    });

    it('never exceeds configured concurrency', async () => {
      const gates = Array.from({ length: 6 }, () => deferred());
      let active = 0;
      let maximumActive = 0;
      let started = 0;
      const harness = await useHarness(
        'concurrency',
        async () => {
          const index = started;
          started += 1;
          active += 1;
          maximumActive = Math.max(maximumActive, active);
          await gates[index].promise;
          active -= 1;
        },
        { concurrency: 2 },
      );

      await harness.start();
      await Promise.all(
        gates.map((_, index) =>
          harness.driver.add(harness.queueName, `concurrent-${index}`, {}),
        ),
      );
      await harness.waitFor(() => started === 2, WAIT_TIMEOUT_MS);
      expect(maximumActive).toBe(2);
      gates.forEach(({ resolve }) => resolve());
      await harness.waitFor(
        () => started === 6 && active === 0,
        WAIT_TIMEOUT_MS,
      );
      expect(maximumActive).toBe(2);
    });

    it('deduplicates a matching ready job including prioritized jobs but permits another once active', async () => {
      const firstGate = deferred();
      let started = 0;
      const harness = await useHarness('deduplication', async () => {
        started += 1;
        if (started === 1) {
          await firstGate.promise;
        }
      });

      await harness.driver.add(
        harness.queueName,
        'deduplicated',
        {},
        { id: 'logical-id', priority: 1 },
      );
      await harness.driver.add(
        harness.queueName,
        'deduplicated',
        {},
        { id: 'logical-id', priority: 1 },
      );
      expect(
        await harness.driver.findJobs(harness.queueName, ['created']),
      ).toHaveLength(1);

      await harness.start();
      await harness.waitFor(() => started === 1, WAIT_TIMEOUT_MS);
      await harness.driver.add(
        harness.queueName,
        'deduplicated',
        {},
        { id: 'logical-id', priority: 1 },
      );
      firstGate.resolve();
      await harness.waitFor(() => started === 2, WAIT_TIMEOUT_MS);
    });

    it('upserts and removes a cron-pattern schedule', async () => {
      const received: Array<{ name: string; version: number }> = [];
      const harness = await useHarness('cron-pattern', (job) => {
        received.push({ name: job.name, version: job.data.version });
      });

      await harness.start();
      await harness.driver.addCron({
        queueName: harness.queueName,
        jobName: 'cron-pattern',
        data: { version: 1 },
        jobId: 'cron-pattern-id',
        options: { repeat: { pattern: '*/1 * * * * *' } },
      });
      await harness.driver.addCron({
        queueName: harness.queueName,
        jobName: 'cron-pattern',
        data: { version: 2 },
        jobId: 'cron-pattern-id',
        options: { repeat: { pattern: '*/1 * * * * *' } },
      });
      await harness.waitFor(() => received.length >= 1, WAIT_TIMEOUT_MS);
      expect(received.every(({ version }) => version === 2)).toBe(true);

      await harness.driver.removeCron({
        queueName: harness.queueName,
        jobName: 'cron-pattern',
        jobId: 'cron-pattern-id',
      });
      const countAfterRemoval = received.length;
      await new Promise((resolve) => setTimeout(resolve, 1_300));
      expect(received).toHaveLength(countAfterRemoval);
    });

    it('upserts an interval schedule, honors its limit, and removes it', async () => {
      let received = 0;
      const harness = await useHarness('interval', () => {
        received += 1;
      });

      await harness.start();
      await harness.driver.addCron({
        queueName: harness.queueName,
        jobName: 'interval',
        data: {},
        jobId: 'interval-id',
        options: { repeat: { every: 150, limit: 2 } },
      });
      await harness.driver.addCron({
        queueName: harness.queueName,
        jobName: 'interval',
        data: {},
        jobId: 'interval-id',
        options: { repeat: { every: 150, limit: 2 } },
      });
      await harness.waitFor(() => received === 2, WAIT_TIMEOUT_MS);
      await new Promise((resolve) => setTimeout(resolve, 400));
      expect(received).toBe(2);
      await harness.driver.removeCron({
        queueName: harness.queueName,
        jobName: 'interval',
        jobId: 'interval-id',
      });
    });

    it('reclaims a non-acknowledged job after a worker is killed and restarted', async () => {
      const firstAttempt = deferred();
      let entries = 0;
      const harness = await useHarness(
        'restart-reclaim',
        async () => {
          entries += 1;
          if (entries === 1) {
            await firstAttempt.promise;
          }
        },
        { lockDuration: 500, maxStalledCount: 2 },
      );

      await harness.start();
      await harness.driver.add(harness.queueName, 'reclaim', {});
      await harness.waitFor(() => entries === 1, WAIT_TIMEOUT_MS);
      await harness.restartWorker();
      await harness.waitFor(() => entries === 2, RECOVERY_TIMEOUT_MS);
      firstAttempt.resolve();
    });

    it('bounds shutdown and aborts a long-running handler', async () => {
      let started = false;
      let aborted = false;
      const harness = await useHarness(
        'bounded-shutdown',
        async ({ abortSignal }) => {
          started = true;
          await new Promise<void>((resolve) => {
            abortSignal?.addEventListener(
              'abort',
              () => {
                aborted = true;
                resolve();
              },
              { once: true },
            );
          });
        },
        { boundedShutdownDrain: true },
        200,
      );

      await harness.start();
      await harness.driver.add(harness.queueName, 'long-running', {});
      await harness.waitFor(() => started, WAIT_TIMEOUT_MS);
      const shutdownStartedAt = Date.now();
      await harness.stop();

      expect(aborted).toBe(true);
      expect(Date.now() - shutdownStartedAt).toBeLessThan(1_500);
    });

    it('retains completed and failed jobs for inspection', async () => {
      const harness = await useHarness('retention', (job) => {
        if (job.name === 'failed') {
          throw new Error('retained failure');
        }
      });

      await harness.start();
      await harness.driver.add(harness.queueName, 'completed', {});
      await harness.driver.add(harness.queueName, 'failed', {});
      await harness.waitFor(async () => {
        const completed = await harness.driver.findJobs(harness.queueName, [
          'completed',
        ]);
        const failed = await harness.driver.findJobs(harness.queueName, [
          'failed',
        ]);

        return completed.length === 1 && failed.length === 1;
      }, WAIT_TIMEOUT_MS);
    });

    it('reports healthy queue depth and active and failed counts', async () => {
      const gate = deferred();
      const harness = await useHarness(
        'stats',
        async (job) => {
          if (job.name === 'active') {
            await gate.promise;
          } else {
            throw new Error('counted failure');
          }
        },
        { concurrency: 1 },
      );

      await harness.start();
      await harness.driver.add(harness.queueName, 'active', {});
      await harness.waitFor(async () => {
        const stats = await harness.driver.getStats(harness.queueName);

        return stats.active === 1;
      }, WAIT_TIMEOUT_MS);
      await harness.driver.add(harness.queueName, 'waiting', {});
      const activeStats = await harness.driver.getStats(harness.queueName);
      expect(activeStats).toMatchObject({
        healthy: true,
        active: 1,
        created: 1,
      });
      gate.resolve();
      await harness.waitFor(async () => {
        const stats = await harness.driver.getStats(harness.queueName);

        return stats.failed === 1;
      }, WAIT_TIMEOUT_MS);
    });

    it('retries a failed job and deletes a waiting job', async () => {
      let shouldFail = true;
      const gate = deferred();
      const harness = await useHarness(
        'job-controls',
        async (job) => {
          if (job.name === 'retryable' && shouldFail) {
            throw new Error('retry me');
          }
          if (job.name === 'blocker') {
            await gate.promise;
          }
        },
        { concurrency: 1 },
      );

      await harness.start();
      await harness.driver.add(harness.queueName, 'retryable', {});
      await harness.waitFor(async () => {
        return (
          (await harness.driver.findJobs(harness.queueName, ['failed']))
            .length === 1
        );
      }, WAIT_TIMEOUT_MS);
      const [failedJob] = await harness.driver.findJobs(harness.queueName, [
        'failed',
      ]);
      shouldFail = false;
      await harness.driver.retryJob(harness.queueName, failedJob.id);
      await harness.waitFor(async () => {
        return (
          await harness.driver.findJobs(harness.queueName, ['completed'])
        ).some(({ id }) => id === failedJob.id);
      }, WAIT_TIMEOUT_MS);

      await harness.driver.add(harness.queueName, 'blocker', {});
      await harness.waitFor(async () => {
        const stats = await harness.driver.getStats(harness.queueName);

        return stats.active === 1;
      }, WAIT_TIMEOUT_MS);
      await harness.driver.add(harness.queueName, 'delete-me', {});
      const waitingJob = (
        await harness.driver.findJobs(harness.queueName, ['created'])
      ).find(({ name }) => name === 'delete-me');
      expect(waitingJob).toBeDefined();
      await harness.driver.deleteJob(harness.queueName, waitingJob?.id ?? '');
      expect(
        (await harness.driver.findJobs(harness.queueName, ['created'])).some(
          ({ name }) => name === 'delete-me',
        ),
      ).toBe(false);
      gate.resolve();
    });

    it('leaves a job available when the worker is stopped before claim', async () => {
      let received = 0;
      const harness = await useHarness('before-claim', () => {
        received += 1;
      });

      await harness.driver.add(harness.queueName, 'unclaimed', {});
      await harness.stop();
      await harness.restartWorker();
      await harness.waitFor(() => received === 1, WAIT_TIMEOUT_MS);
    });

    it('recovers a claimed mid-handler job after lease expiry without job loss', async () => {
      const firstAttempt = deferred();
      let entries = 0;
      const harness = await useHarness(
        'lease-recovery',
        async () => {
          entries += 1;
          if (entries === 1) {
            await firstAttempt.promise;
          }
        },
        { lockDuration: 500, maxStalledCount: 2 },
      );

      await harness.start();
      await harness.driver.add(harness.queueName, 'lease-recovery', {});
      await harness.waitFor(() => entries === 1, WAIT_TIMEOUT_MS);
      await harness.terminateWorker();
      await harness.restartWorker();
      await harness.waitFor(() => entries === 2, RECOVERY_TIMEOUT_MS);
      firstAttempt.resolve();
      await harness.waitFor(async () => {
        return (
          (await harness.driver.findJobs(harness.queueName, ['completed']))
            .length === 1
        );
      }, WAIT_TIMEOUT_MS);
    });

    it('re-enters after a PostgreSQL write while a unique receipt prevents duplicate durable state', async () => {
      const databaseUrl =
        process.env.RUNTIME_CONTRACT_DATABASE_URL ??
        process.env.PG_DATABASE_URL;

      if (!databaseUrl) {
        throw new Error(
          'RUNTIME_CONTRACT_DATABASE_URL or PG_DATABASE_URL is required for the PostgreSQL receipt contract',
        );
      }

      const pool = new Pool({ connectionString: databaseUrl });
      const tableName = `runtime_queue_receipt_${v4().replace(/-/g, '')}`;
      const firstAttempt = deferred();
      let entries = 0;
      const harness = await useHarness(
        'postgres-receipt',
        async () => {
          entries += 1;
          await pool.query(
            `INSERT INTO ${tableName} (receipt_key) VALUES ($1) ON CONFLICT DO NOTHING`,
            ['durable-effect'],
          );
          if (entries === 1) {
            await firstAttempt.promise;
          }
        },
        { lockDuration: 500, maxStalledCount: 2 },
      );
      const queueCleanup = cleanup;
      cleanup = async () => {
        try {
          await queueCleanup?.();
        } finally {
          await pool.query(`DROP TABLE IF EXISTS ${tableName}`);
          await pool.end();
        }
      };

      await pool.query(
        `CREATE TABLE ${tableName} (receipt_key text PRIMARY KEY)`,
      );
      await harness.start();
      await harness.driver.add(harness.queueName, 'postgres-receipt', {});
      await harness.waitFor(() => entries === 1, WAIT_TIMEOUT_MS);
      await harness.restartWorker();
      await harness.waitFor(() => entries === 2, RECOVERY_TIMEOUT_MS);
      firstAttempt.resolve();
      const result = await pool.query(
        `SELECT count(*)::int AS count FROM ${tableName}`,
      );
      expect(result.rows[0].count).toBe(1);
    });

    it('does not repeat an external side effect protected by an idempotency receipt after termination before acknowledgement', async () => {
      const receipts = new Set<string>();
      const firstAttempt = deferred();
      let entries = 0;
      let sideEffects = 0;
      const harness = await useHarness(
        'external-receipt',
        async () => {
          entries += 1;
          if (!receipts.has('external-effect')) {
            receipts.add('external-effect');
            sideEffects += 1;
          }
          if (entries === 1) {
            await firstAttempt.promise;
          }
        },
        { lockDuration: 500, maxStalledCount: 2 },
      );

      await harness.start();
      await harness.driver.add(harness.queueName, 'external-receipt', {});
      await harness.waitFor(() => entries === 1, WAIT_TIMEOUT_MS);
      await harness.restartWorker();
      await harness.waitFor(() => entries === 2, RECOVERY_TIMEOUT_MS);
      firstAttempt.resolve();
      expect(sideEffects).toBe(1);
    });
  });
};

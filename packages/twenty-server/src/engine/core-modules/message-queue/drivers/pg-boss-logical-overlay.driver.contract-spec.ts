import { randomUUID } from 'node:crypto';

import { Pool } from 'pg';
import { PgBoss } from 'pg-boss';

import { createPgBossContractHarness } from 'src/engine/core-modules/message-queue/drivers/pg-boss.driver.contract-spec';
import { PgBossLogicalLedger } from 'src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger';
import {
  type LogicalPgBossEnvelope,
  type LogicalTransport,
} from 'src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.types';
import { defineMessageQueueDriverContract } from 'src/engine/core-modules/message-queue/drivers/testing/message-queue-driver.contract';
import { type CreateMessageQueueDriverTestHarness } from 'src/engine/core-modules/message-queue/drivers/testing/message-queue-driver-test-harness';
import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

const createOverlayHarness: CreateMessageQueueDriverTestHarness = (args) =>
  createPgBossContractHarness(args, {
    logicalLedgerEnabled: true,
    applicationPrefix: 'ebaycrm-runtime-contract-overlay',
  });

const connectionString = () => {
  const value =
    process.env.RUNTIME_CONTRACT_DATABASE_URL ?? process.env.PG_DATABASE_URL;

  if (!value) {
    throw new Error('PG_DATABASE_URL is required for the overlay contract');
  }

  return value;
};

type OverlayDriverInternals = {
  boss: PgBoss;
  logicalLedger: PgBossLogicalLedger;
};

type LedgerInternals = {
  options: { transport: LogicalTransport };
};

const overlayInternals = (driver: unknown): OverlayDriverInternals =>
  driver as OverlayDriverInternals;

const ledgerTransport = (ledger: PgBossLogicalLedger): LogicalTransport =>
  (ledger as unknown as LedgerInternals).options.transport;

const fetchPhysicalJob = async (boss: PgBoss, queueName: MessageQueue) => {
  const jobs = await boss.fetch<LogicalPgBossEnvelope>(queueName, {
    includeMetadata: true,
  });
  const job = jobs[0];

  if (!job) {
    throw new Error(`No physical job was available for ${queueName}`);
  }

  return job;
};

const within = async <T>(operation: Promise<T>, label: string): Promise<T> => {
  let timeout: ReturnType<typeof setTimeout> | undefined;

  try {
    return await Promise.race([
      operation,
      new Promise<never>((_resolve, reject) => {
        timeout = setTimeout(
          () => reject(new Error(`${label} exceeded 10 seconds`)),
          10_000,
        );
      }),
    ]);
  } finally {
    clearTimeout(timeout);
  }
};

if (process.env.RUNTIME_CONTRACT_DRIVER === 'pg-boss-overlay') {
  defineMessageQueueDriverContract(
    'pg-boss logical overlay',
    createOverlayHarness,
  );

  describe('pg-boss logical overlay service gates', () => {
    beforeAll(() => {
      jest.useRealTimers();
    });

    it('blocks production until both logical workers are durably ready', async () => {
      const queueName =
        `runtime-contract-overlay-readiness-${randomUUID()}` as MessageQueue;
      const harness = await createOverlayHarness({
        queueName,
        handler: () => undefined,
      });
      const pool = new Pool({ connectionString: connectionString() });
      const { boss, logicalLedger } = overlayInternals(harness.driver);

      try {
        await logicalLedger.registerQueuePolicy(queueName, {});
        await expect(
          harness.driver.add(queueName, 'too-early', {}),
        ).rejects.toThrow(
          'logical queue worker is not ready; register workers before producing jobs',
        );
        await expect(boss.findJobs(queueName)).resolves.toHaveLength(0);

        const before = await pool.query<{ ready: boolean }>({
          text: `SELECT worker_ready_at > '-infinity'::timestamptz AS ready
            FROM desktop_runtime.queue_policy WHERE queue_name = $1`,
          values: [queueName],
        });

        expect(before.rows).toEqual([{ ready: false }]);
        await harness.start();

        const after = await pool.query<{ ready: boolean }>({
          text: `SELECT worker_ready_at > '-infinity'::timestamptz AS ready
            FROM desktop_runtime.queue_policy WHERE queue_name = $1`,
          values: [queueName],
        });

        expect(after.rows).toEqual([{ ready: true }]);
        await expect(
          harness.driver.add(queueName, 'after-readiness', {}),
        ).resolves.toBeUndefined();
        await expect(boss.findJobs(queueName)).resolves.toHaveLength(1);
      } finally {
        await pool.end();
        await harness.clear();
      }
    });

    it('terminally reconciles physical exhaustion before logical start', async () => {
      const queueName =
        `runtime-contract-overlay-prestart-dead-letter-${randomUUID()}` as MessageQueue;
      const handler = jest.fn();
      const harness = await createOverlayHarness({ queueName, handler });
      const pool = new Pool({ connectionString: connectionString() });
      const { boss } = overlayInternals(harness.driver);

      try {
        await harness.start();
        await boss.offWork(queueName);
        await harness.driver.add(queueName, 'prestart-exhaustion', {});

        const first = await fetchPhysicalJob(boss, queueName);

        await boss.fail(queueName, first.id, { message: 'prestart crash 1' });
        const retry = await fetchPhysicalJob(boss, queueName);

        await boss.fail(queueName, retry.id, { message: 'prestart crash 2' });
        await harness.waitFor(async () => {
          const jobs = await harness.driver.findJobs(queueName, ['failed']);

          return jobs.length === 1;
        }, 15_000);

        const logical = await pool.query<{
          id: string;
          status: string;
          failure_kind: string;
        }>({
          text: `SELECT id, status, failure_kind
            FROM desktop_runtime.queue_job WHERE queue_name = $1`,
          values: [queueName],
        });
        const attempts = await pool.query<{ outcome: string }>({
          text: `SELECT outcome FROM desktop_runtime.queue_job_attempt
            WHERE job_id = $1 ORDER BY started_at`,
          values: [logical.rows[0]?.id],
        });

        expect(handler).not.toHaveBeenCalled();
        expect(logical.rows).toEqual([
          expect.objectContaining({
            status: 'failed',
            failure_kind: 'stall_exhausted',
          }),
        ]);
        expect(attempts.rows).toEqual([{ outcome: 'stalled' }]);
        await expect(
          harness.driver.findJobs(queueName, ['created', 'retry', 'active']),
        ).resolves.toHaveLength(0);
        await expect(fetchPhysicalJob(boss, queueName)).rejects.toThrow(
          `No physical job was available for ${queueName}`,
        );
      } finally {
        await pool.end();
        await harness.clear();
      }
    });

    it('rolls back logical retry, replacement envelope, and current completion together', async () => {
      const queueName =
        `runtime-contract-overlay-rollback-${randomUUID()}` as MessageQueue;
      const sentinelQueue = `overlay-sentinel-${randomUUID()}`;
      const harness = await within(
        createOverlayHarness({
          queueName,
          handler: () => undefined,
        }),
        'rollback harness creation',
      );
      const pool = new Pool({ connectionString: connectionString() });
      const { boss, logicalLedger } = overlayInternals(harness.driver);
      const transport = ledgerTransport(logicalLedger);
      const originalSend = transport.send;

      try {
        await harness.start();
        await boss.offWork(queueName);
        await within(
          harness.driver.add(
            queueName,
            'retry-rollback',
            {},
            {
              retryLimit: 1,
            },
          ),
          'rollback add',
        );
        const physical = await within(
          fetchPhysicalJob(boss, queueName),
          'rollback physical fetch',
        );
        const envelope = physical.data;
        const start = await within(
          logicalLedger.startAttempt({
            queueName,
            envelope,
            physicalJobId: physical.id,
            transportRetryCount: physical.retryCount,
            workerInstanceId: randomUUID(),
          }),
          'rollback logical start',
        );

        expect(start.kind).toBe('execute');
        if (start.kind !== 'execute') {
          throw new Error(`Expected executable job, received ${start.kind}`);
        }

        transport.send = async (args) => {
          await originalSend(args);
          await args.db.executeSql(
            `INSERT INTO desktop_runtime.queue_policy
              (queue_name, stall_recovery_limit, heartbeat_seconds)
             VALUES ($1, 0, NULL)`,
            [sentinelQueue],
          );
          throw new Error('injected retry transaction interruption');
        };

        await expect(
          within(
            logicalLedger.settleFailure(
              {
                queueName,
                physicalJobId: physical.id,
                logicalJobId: start.logicalJobId,
                generation: envelope.generation,
                executionToken: start.executionToken,
              },
              new Error('ordinary handler failure'),
            ),
            'rollback interrupted settlement',
          ),
        ).rejects.toThrow('injected retry transaction interruption');

        const rolledBack = await pool.query({
          text: `SELECT status, generation, current_physical_job_id
            FROM desktop_runtime.queue_job WHERE id = $1`,
          values: [start.logicalJobId],
        });
        const sentinel = await pool.query(
          'SELECT 1 FROM desktop_runtime.queue_policy WHERE queue_name = $1',
          [sentinelQueue],
        );
        const physicalJobs = await boss.findJobs(queueName);

        expect(rolledBack.rows).toEqual([
          {
            status: 'active',
            generation: 0,
            current_physical_job_id: physical.id,
          },
        ]);
        expect(sentinel.rowCount).toBe(0);
        expect(physicalJobs.map(({ id }) => id)).toEqual([physical.id]);

        transport.send = originalSend;
        await expect(
          within(
            logicalLedger.settleFailure(
              {
                queueName,
                physicalJobId: physical.id,
                logicalJobId: start.logicalJobId,
                generation: envelope.generation,
                executionToken: start.executionToken,
              },
              new Error('ordinary handler failure'),
            ),
            'rollback healthy settlement',
          ),
        ).resolves.toBe('retried');
      } finally {
        transport.send = originalSend;
        await within(
          pool.query(
            'DELETE FROM desktop_runtime.queue_policy WHERE queue_name = $1',
            [sentinelQueue],
          ),
          'rollback sentinel cleanup',
        );
        await within(pool.end(), 'rollback inspection pool close');
        await within(harness.clear(), 'rollback harness cleanup');
      }
    });

    it('fences a stale generation before handler invocation and settlement', async () => {
      const queueName =
        `runtime-contract-overlay-fence-${randomUUID()}` as MessageQueue;
      const handler = jest.fn();
      const harness = await createOverlayHarness({ queueName, handler });
      const pool = new Pool({ connectionString: connectionString() });
      const { boss } = overlayInternals(harness.driver);

      try {
        await harness.start();
        await boss.offWork(queueName);
        await harness.driver.add(queueName, 'stale-generation', {});
        const logical = await pool.query<{ id: string }>({
          text: `SELECT id FROM desktop_runtime.queue_job
            WHERE queue_name = $1`,
          values: [queueName],
        });
        const logicalJobId = logical.rows[0]?.id;

        if (!logicalJobId) {
          throw new Error('Expected the logical job to exist');
        }

        await pool.query({
          text: `UPDATE desktop_runtime.queue_job
            SET generation = 1, current_physical_job_id = $2
            WHERE id = $1`,
          values: [logicalJobId, randomUUID()],
        });
        await harness.driver.work(queueName, handler);
        await harness.waitFor(async () => {
          const jobs = await boss.findJobs(queueName);

          return jobs.some(({ state }) => state === 'completed');
        }, 15_000);

        const attempts = await pool.query(
          'SELECT outcome FROM desktop_runtime.queue_job_attempt WHERE job_id = $1',
          [logicalJobId],
        );

        expect(handler).not.toHaveBeenCalled();
        expect(attempts.rowCount).toBe(0);
      } finally {
        await pool.end();
        await harness.clear();
      }
    });

    it('recovers settlement interruption without losing the job or duplicating a terminal receipt', async () => {
      const queueName =
        `runtime-contract-overlay-settlement-${randomUUID()}` as MessageQueue;
      const sentinelQueue = `overlay-sentinel-${randomUUID()}`;
      const harness = await createOverlayHarness({
        queueName,
        handler: () => undefined,
      });
      const pool = new Pool({ connectionString: connectionString() });
      const { boss, logicalLedger } = overlayInternals(harness.driver);
      const transport = ledgerTransport(logicalLedger);
      const originalComplete = transport.complete;

      try {
        await harness.start();
        await boss.offWork(queueName);
        await harness.driver.add(queueName, 'settlement-recovery', {});
        const physical = await fetchPhysicalJob(boss, queueName);
        const envelope = physical.data;
        const start = await logicalLedger.startAttempt({
          queueName,
          envelope,
          physicalJobId: physical.id,
          transportRetryCount: physical.retryCount,
          workerInstanceId: randomUUID(),
        });

        expect(start.kind).toBe('execute');
        if (start.kind !== 'execute') {
          throw new Error(`Expected executable job, received ${start.kind}`);
        }

        const settlement = {
          queueName,
          physicalJobId: physical.id,
          logicalJobId: start.logicalJobId,
          generation: envelope.generation,
          executionToken: start.executionToken,
        };

        transport.complete = async (args) => {
          await originalComplete(args);
          await args.db.executeSql(
            `INSERT INTO desktop_runtime.queue_policy
              (queue_name, stall_recovery_limit, heartbeat_seconds)
             VALUES ($1, 0, NULL)`,
            [sentinelQueue],
          );
          throw new Error('injected settlement interruption');
        };

        await expect(logicalLedger.settleSuccess(settlement)).rejects.toThrow(
          'injected settlement interruption',
        );
        transport.complete = originalComplete;
        await expect(logicalLedger.settleSuccess(settlement)).resolves.toBe(
          'settled',
        );
        await expect(logicalLedger.settleSuccess(settlement)).resolves.toBe(
          'fenced',
        );

        const job = await pool.query({
          text: 'SELECT status FROM desktop_runtime.queue_job WHERE id = $1',
          values: [start.logicalJobId],
        });
        const receipts = await pool.query({
          text: `SELECT outcome FROM desktop_runtime.queue_job_attempt
            WHERE job_id = $1 AND outcome IN ('completed', 'handler_failed', 'stalled')`,
          values: [start.logicalJobId],
        });
        const sentinel = await pool.query(
          'SELECT 1 FROM desktop_runtime.queue_policy WHERE queue_name = $1',
          [sentinelQueue],
        );

        expect(job.rows).toEqual([{ status: 'completed' }]);
        expect(receipts.rows).toEqual([{ outcome: 'completed' }]);
        expect(sentinel.rowCount).toBe(0);
      } finally {
        transport.complete = originalComplete;
        await pool.query(
          'DELETE FROM desktop_runtime.queue_policy WHERE queue_name = $1',
          [sentinelQueue],
        );
        await pool.end();
        await harness.clear();
      }
    });
  });
} else {
  describe.skip('pg-boss logical overlay driver contract', () => {
    it('requires RUNTIME_CONTRACT_DRIVER=pg-boss-overlay', () => undefined);
  });
}

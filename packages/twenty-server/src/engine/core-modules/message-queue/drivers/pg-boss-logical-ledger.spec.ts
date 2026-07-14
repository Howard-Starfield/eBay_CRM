import { type Pool, type PoolClient, type QueryConfig } from 'pg';

import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

import { PgBossLogicalLedger } from './pg-boss-logical-ledger';
import { type LogicalTransport } from './pg-boss-logical-ledger.types';

type ClientMock = Pick<PoolClient, 'query' | 'release'>;
type PoolMock = Pick<Pool, 'connect' | 'query'>;

const queueName = 'aiQueue' as MessageQueue;
const logicalId = '95ba9af8-0142-45b6-b8ca-d68a16c1e9ec';
const physicalJobId = '109f61bc-6df2-57e3-a21c-461f0bf8c803';
const stalePhysicalId = '29e45724-20de-51e6-85ce-3e991d929a51';
const workerInstanceId = 'ec5f6200-c7ad-40fe-83fc-46c2262f12ce';

describe('PgBossLogicalLedger', () => {
  let client: ClientMock;
  let pool: PoolMock;
  let transport: LogicalTransport;
  let ledger: PgBossLogicalLedger;

  beforeEach(() => {
    jest.useRealTimers();
    client = {
      query: jest.fn().mockResolvedValue({ rows: [] }),
      release: jest.fn(),
    };
    pool = {
      connect: jest.fn().mockResolvedValue(client),
      query: jest.fn().mockResolvedValue({ rows: [] }),
    };
    transport = {
      send: jest.fn().mockResolvedValue(undefined),
      complete: jest.fn().mockResolvedValue(undefined),
    };
    ledger = new PgBossLogicalLedger({
      pool: pool as Pool,
      schema: 'desktop_runtime',
      transport,
    });
  });

  it('creates the queue policy, logical job, and attempt tables', async () => {
    await ledger.initialize();

    const executedSql = (client.query as jest.Mock).mock.calls.map(
      ([query]: [string]) => query,
    );

    expect(executedSql).toEqual(
      expect.arrayContaining([
        expect.stringContaining(
          'CREATE TABLE IF NOT EXISTS desktop_runtime.queue_policy',
        ),
        expect.stringContaining(
          'CREATE TABLE IF NOT EXISTS desktop_runtime.queue_job',
        ),
        expect.stringContaining(
          'CREATE TABLE IF NOT EXISTS desktop_runtime.queue_job_attempt',
        ),
        expect.stringContaining('queue_job_waiting_dedup_key_idx'),
      ]),
    );
  });

  it('restricts ledger counters, statuses, outcomes, and attempt identity', async () => {
    await ledger.initialize();

    const schemaSql = (client.query as jest.Mock).mock.calls
      .map(([query]: [string]) => query)
      .join('\n');

    expect(schemaSql).toContain('CHECK (handler_failure_count >= 0)');
    expect(schemaSql).toContain('CHECK (stall_recovery_limit >= 0)');
    expect(schemaSql).toContain(
      "status IN ('queued', 'active', 'retry_wait', 'completed', 'failed', 'cancelled')",
    );
    expect(schemaSql).toContain('job_id uuid NOT NULL REFERENCES');
    expect(schemaSql).toContain('UNIQUE (job_id, execution_token)');
    expect(schemaSql).toContain('outcome text NOT NULL CHECK');
    expect(schemaSql).toContain(
      "outcome IN ('running', 'completed', 'handler_failed', 'stalled', 'fenced', 'cancelled')",
    );
  });

  it('materializes one stalled recovery when worker options omit it', async () => {
    await ledger.registerQueuePolicy(queueName, {});

    const calls = (pool.query as jest.Mock).mock.calls;
    const lastQuery = calls[calls.length - 1]?.[0] as QueryConfig | undefined;

    expect(lastQuery).toMatchObject({
      values: [queueName, 1, null],
    });
  });

  it('derives a stable unique physical UUID for each generation', () => {
    expect(ledger.physicalJobId(logicalId, 0)).toBe(
      ledger.physicalJobId(logicalId, 0),
    );
    expect(ledger.physicalJobId(logicalId, 1)).not.toBe(
      ledger.physicalJobId(logicalId, 0),
    );
  });

  it('persists explicit worker recovery policy before work starts', async () => {
    await ledger.registerQueuePolicy(queueName, {
      maxStalledCount: 2,
      lockDuration: 10_000,
    });

    const calls = (pool.query as jest.Mock).mock.calls;
    const lastQuery = calls[calls.length - 1]?.[0] as QueryConfig;

    expect(lastQuery.values).toEqual([queueName, 2, 10]);
  });

  it.each([-1, 1.5])(
    'rejects invalid stalled recovery limit %s before querying PostgreSQL',
    async (maxStalledCount) => {
      await expect(
        ledger.registerQueuePolicy(queueName, { maxStalledCount }),
      ).rejects.toThrow('stall recovery limit must be a non-negative integer');

      expect(pool.connect).not.toHaveBeenCalled();
      expect(pool.query).not.toHaveBeenCalled();
    },
  );

  it('creates and sends one logical job in the same transaction', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-07-13T12:00:00.000Z'));
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.release as jest.Mock).mockClear();
    (pool.connect as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_policy')) {
          return {
            rows: [{ stall_recovery_limit: 2, heartbeat_seconds: 10 }],
          };
        }

        return { rows: [] };
      },
    );
    (transport.send as jest.Mock).mockImplementation(async ({ db }) => {
      await db.executeSql('SELECT transport_sentinel', []);
    });

    const createdId = await ledger.createJob({
      queueName,
      jobName: 'reply-to-buyer',
      data: { orderId: 'order-1' },
      options: { id: 'order-1', retryLimit: 3, priority: 4, delay: 2_500 },
    });

    expect(createdId).toEqual(expect.any(String));
    expect(client.query).toHaveBeenNthCalledWith(1, 'BEGIN');
    expect(client.query).toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining('INSERT INTO desktop_runtime.queue_job'),
        values: expect.arrayContaining([
          createdId,
          queueName,
          'reply-to-buyer',
          { orderId: 'order-1' },
          3,
          2,
          4,
          'order-1',
        ]),
      }),
    );
    expect(transport.send).toHaveBeenCalledWith(
      expect.objectContaining({
        queueName,
        envelope: { version: 2, logicalJobId: createdId, generation: 0 },
        physicalJobId: ledger.physicalJobId(createdId as string, 0),
        stallRecoveryLimit: 2,
        priority: 4,
        availableAt: new Date('2026-07-13T12:00:02.500Z'),
        db: expect.objectContaining({ executeSql: expect.any(Function) }),
      }),
    );
    expect(client.query).toHaveBeenCalledWith('SELECT transport_sentinel', []);
    expect(client.query).toHaveBeenLastCalledWith('COMMIT');
    expect(client.release).toHaveBeenCalledTimes(1);
  });

  it('materializes the default policy inside a new job transaction', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_policy')) {
          return { rows: [] };
        }
        if (text.includes('INSERT INTO desktop_runtime.queue_policy')) {
          return {
            rows: [{ stall_recovery_limit: 1, heartbeat_seconds: null }],
          };
        }

        return { rows: [] };
      },
    );

    await ledger.createJob({
      queueName,
      jobName: 'reply-to-buyer',
      data: { orderId: 'order-1' },
    });

    expect(client.query).toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining(
          'INSERT INTO desktop_runtime.queue_policy',
        ),
        values: [queueName, 1, null],
      }),
    );
    expect(transport.send).toHaveBeenCalledWith(
      expect.objectContaining({ stallRecoveryLimit: 1 }),
    );
  });

  it('rolls back logical state and the envelope when sending fails', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.release as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_policy')) {
          return {
            rows: [{ stall_recovery_limit: 1, heartbeat_seconds: null }],
          };
        }

        return { rows: [] };
      },
    );
    (transport.send as jest.Mock).mockRejectedValueOnce(
      new Error('send failed'),
    );

    await expect(
      ledger.createJob({
        queueName,
        jobName: 'reply-to-buyer',
        data: { orderId: 'order-1' },
      }),
    ).rejects.toThrow('send failed');

    expect(client.query).toHaveBeenLastCalledWith('ROLLBACK');
    expect(client.query).not.toHaveBeenCalledWith('COMMIT');
    expect(client.release).toHaveBeenCalledTimes(1);
  });

  it('treats a waiting deduplication conflict as a successful no-op', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.release as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_policy')) {
          return {
            rows: [{ stall_recovery_limit: 1, heartbeat_seconds: null }],
          };
        }
        if (text.includes('INSERT INTO desktop_runtime.queue_job')) {
          throw Object.assign(new Error('duplicate waiting job'), {
            code: '23505',
            constraint: 'queue_job_waiting_dedup_key_idx',
          });
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.createJob({
        queueName,
        jobName: 'reply-to-buyer',
        data: { orderId: 'order-1' },
        options: { id: 'order-1' },
      }),
    ).resolves.toBeNull();

    expect(client.query).toHaveBeenLastCalledWith('ROLLBACK');
    expect(transport.send).not.toHaveBeenCalled();
    expect(client.release).toHaveBeenCalledTimes(1);
  });

  it.each([-1, 1.5])(
    'rejects invalid handler retry limit %s before querying PostgreSQL',
    async (retryLimit) => {
      await expect(
        ledger.createJob({
          queueName,
          jobName: 'reply-to-buyer',
          data: { orderId: 'order-1' },
          options: { retryLimit },
        }),
      ).rejects.toThrow('handler retry limit must be a non-negative integer');

      expect(pool.connect).not.toHaveBeenCalled();
    },
  );

  it('fences a physical generation that is no longer current', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 1,
                current_physical_job_id: physicalJobId,
                status: 'queued',
                stall_count: 0,
                stall_recovery_limit: 2,
                transport_retry_count: 0,
              },
            ],
          };
        }

        return { rows: [] };
      },
    );

    const result = await ledger.startAttempt({
      queueName,
      physicalJobId: stalePhysicalId,
      envelope: { version: 2, logicalJobId: logicalId, generation: 0 },
      transportRetryCount: 0,
      workerInstanceId,
    });

    expect(result).toEqual({ kind: 'fenced' });
    expect(transport.complete).toHaveBeenCalledWith(
      expect.objectContaining({
        queueName,
        physicalJobId: stalePhysicalId,
        db: expect.any(Object),
      }),
    );
    expect(
      (client.query as jest.Mock).mock.calls.filter(([query]) =>
        (typeof query === 'string' ? query : query.text).includes(
          'INSERT INTO desktop_runtime.queue_job_attempt',
        ),
      ),
    ).toHaveLength(0);
  });

  it('accounts each transport retry delta once and creates a fenced attempt', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    let updatedJobValues: unknown[] | undefined;
    let insertedAttemptValues: unknown[] | undefined;
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;
        const values = typeof query === 'string' ? undefined : query.values;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                status: 'queued',
                stall_count: 0,
                stall_recovery_limit: 2,
                transport_retry_count: 0,
                job_name: 'reply-to-buyer',
                payload: { orderId: 'order-1' },
              },
            ],
          };
        }
        if (text.includes('UPDATE desktop_runtime.queue_job')) {
          updatedJobValues = values;
          return { rows: [{ id: logicalId }] };
        }
        if (text.includes('INSERT INTO desktop_runtime.queue_job_attempt')) {
          insertedAttemptValues = values;
        }

        return { rows: [] };
      },
    );

    const result = await ledger.startAttempt({
      queueName,
      physicalJobId,
      envelope: { version: 2, logicalJobId: logicalId, generation: 0 },
      transportRetryCount: 1,
      workerInstanceId,
    });

    expect(result).toMatchObject({
      kind: 'execute',
      logicalJobId: logicalId,
      jobName: 'reply-to-buyer',
      data: { orderId: 'order-1' },
      executionToken: expect.any(String),
    });
    if (result.kind !== 'execute') {
      throw new Error('expected an executable logical attempt');
    }
    expect(updatedJobValues).toEqual(
      expect.arrayContaining([logicalId, 1, 1, result.executionToken]),
    );
    expect(insertedAttemptValues).toEqual(
      expect.arrayContaining([
        logicalId,
        physicalJobId,
        workerInstanceId,
        result.executionToken,
        1,
      ]),
    );
  });

  it('fences a start when the active canonical transition affects zero rows', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                status: 'queued',
                stall_count: 0,
                stall_recovery_limit: 2,
                transport_retry_count: 0,
                job_name: 'reply-to-buyer',
                payload: { orderId: 'order-1' },
              },
            ],
          };
        }
        if (text.includes('UPDATE desktop_runtime.queue_job')) {
          return { rows: [], rowCount: 0 };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.startAttempt({
        queueName,
        physicalJobId,
        envelope: { version: 2, logicalJobId: logicalId, generation: 0 },
        transportRetryCount: 0,
        workerInstanceId,
      }),
    ).resolves.toEqual({ kind: 'fenced' });

    expect(client.query).not.toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining(
          'INSERT INTO desktop_runtime.queue_job_attempt',
        ),
      }),
    );
    expect(transport.complete).toHaveBeenCalledTimes(1);
  });

  it('fences stall exhaustion when the canonical transition affects zero rows', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                status: 'active',
                stall_count: 0,
                stall_recovery_limit: 0,
                transport_retry_count: 0,
              },
            ],
          };
        }
        if (text.includes('UPDATE desktop_runtime.queue_job')) {
          return { rows: [], rowCount: 0 };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.startAttempt({
        queueName,
        physicalJobId,
        envelope: { version: 2, logicalJobId: logicalId, generation: 0 },
        transportRetryCount: 1,
        workerInstanceId,
      }),
    ).resolves.toEqual({ kind: 'fenced' });

    expect(client.query).not.toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining(
          'INSERT INTO desktop_runtime.queue_job_attempt',
        ),
      }),
    );
    expect(transport.complete).toHaveBeenCalledTimes(1);
  });

  it('settles logical success and physical completion in one transaction', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    const executionToken = 'ce3c741c-62b3-46d1-9f47-d313e27a8560';
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (
          text.includes('FROM desktop_runtime.queue_job') &&
          text.includes('FOR UPDATE')
        ) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                current_execution_token: executionToken,
                status: 'active',
              },
            ],
          };
        }

        if (
          text.includes('UPDATE desktop_runtime.queue_job') &&
          !text.includes('UPDATE desktop_runtime.queue_job_attempt') &&
          text.includes("status = 'completed'")
        ) {
          return { rows: [{ id: logicalId }] };
        }
        if (
          text.includes('UPDATE desktop_runtime.queue_job_attempt') &&
          text.includes("outcome = 'completed'")
        ) {
          return { rows: [{ id: 'attempt-id' }] };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.settleSuccess({
        queueName,
        logicalJobId: logicalId,
        generation: 0,
        physicalJobId,
        executionToken,
      }),
    ).resolves.toBe('settled');

    expect(client.query).toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining("status = 'completed'"),
        values: [logicalId, 0, executionToken, physicalJobId],
      }),
    );
    expect(client.query).toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining("outcome = 'completed'"),
        values: expect.arrayContaining([logicalId, executionToken]),
      }),
    );
    expect(transport.complete).toHaveBeenCalledWith(
      expect.objectContaining({
        queueName,
        physicalJobId,
        db: expect.any(Object),
      }),
    );
    expect(client.query).toHaveBeenLastCalledWith('COMMIT');
  });

  it('locks and fences the canonical job before mutating a success attempt', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    const executionToken = 'ce3c741c-62b3-46d1-9f47-d313e27a8560';
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (
          text.includes('FROM desktop_runtime.queue_job') &&
          text.includes('FOR UPDATE')
        ) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                current_execution_token: executionToken,
                status: 'active',
              },
            ],
          };
        }

        if (text.includes('UPDATE desktop_runtime.queue_job_attempt')) {
          return { rows: [{ id: 'attempt-id' }] };
        }
        if (
          text.includes('UPDATE desktop_runtime.queue_job') &&
          !text.includes('UPDATE desktop_runtime.queue_job_attempt')
        ) {
          return { rows: [{ id: logicalId }] };
        }

        return { rows: [] };
      },
    );

    await ledger.settleSuccess({
      queueName,
      logicalJobId: logicalId,
      generation: 0,
      physicalJobId,
      executionToken,
    });

    const sqlCalls = (client.query as jest.Mock).mock.calls.map(([query]) =>
      typeof query === 'string' ? query : query.text,
    );
    const lockIndex = sqlCalls.findIndex(
      (text) =>
        text.includes('FROM desktop_runtime.queue_job') &&
        text.includes('FOR UPDATE'),
    );
    const attemptIndex = sqlCalls.findIndex((text) =>
      text.includes('UPDATE desktop_runtime.queue_job_attempt'),
    );

    expect(lockIndex).toBeGreaterThan(-1);
    expect(lockIndex).toBeLessThan(attemptIndex);
  });

  it('fences success settlement when the physical job id is stale', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    const executionToken = 'ce3c741c-62b3-46d1-9f47-d313e27a8560';
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                current_execution_token: executionToken,
                status: 'active',
              },
            ],
          };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.settleSuccess({
        queueName,
        logicalJobId: logicalId,
        generation: 0,
        physicalJobId: stalePhysicalId,
        executionToken,
      }),
    ).resolves.toBe('fenced');

    expect(client.query).not.toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining(
          'UPDATE desktop_runtime.queue_job_attempt',
        ),
      }),
    );
    expect(transport.complete).not.toHaveBeenCalled();
  });

  it('creates the next generation without consuming stall allowance', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    const executionToken = 'ce3c741c-62b3-46d1-9f47-d313e27a8560';
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                current_execution_token: executionToken,
                status: 'active',
                handler_failure_count: 0,
                handler_retry_limit: 2,
                stall_count: 0,
                stall_recovery_limit: 2,
                priority: 4,
              },
            ],
          };
        }
        if (
          text.includes('UPDATE desktop_runtime.queue_job') &&
          text.includes("status = 'retry_wait'")
        ) {
          return { rows: [{ id: logicalId }] };
        }
        if (
          text.includes('UPDATE desktop_runtime.queue_job_attempt') &&
          text.includes("outcome = 'handler_failed'")
        ) {
          return { rows: [{ id: 'attempt-id' }] };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.settleFailure(
        {
          queueName,
          logicalJobId: logicalId,
          generation: 0,
          physicalJobId,
          executionToken,
        },
        new Error('handler failed'),
      ),
    ).resolves.toBe('retried');

    expect(client.query).toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining("status = 'retry_wait'"),
        values: expect.arrayContaining([logicalId, 1, 1, 0]),
      }),
    );
    expect(transport.send).toHaveBeenCalledWith(
      expect.objectContaining({
        queueName,
        envelope: { version: 2, logicalJobId: logicalId, generation: 1 },
        physicalJobId: ledger.physicalJobId(logicalId, 1),
        stallRecoveryLimit: 2,
        priority: 4,
        db: expect.any(Object),
      }),
    );
    expect(
      (transport.send as jest.Mock).mock.invocationCallOrder[0],
    ).toBeLessThan(
      (transport.complete as jest.Mock).mock.invocationCallOrder[0],
    );
  });

  it('rolls back a success transition when its running attempt is fenced', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    const executionToken = 'ce3c741c-62b3-46d1-9f47-d313e27a8560';
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (
          text.includes('FROM desktop_runtime.queue_job') &&
          text.includes('FOR UPDATE')
        ) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 0,
                current_physical_job_id: physicalJobId,
                current_execution_token: executionToken,
                status: 'active',
              },
            ],
          };
        }

        if (
          text.includes('UPDATE desktop_runtime.queue_job') &&
          !text.includes('UPDATE desktop_runtime.queue_job_attempt') &&
          text.includes("status = 'completed'")
        ) {
          return { rows: [{ id: logicalId }] };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.settleSuccess({
        queueName,
        logicalJobId: logicalId,
        generation: 0,
        physicalJobId,
        executionToken,
      }),
    ).resolves.toBe('fenced');

    expect(client.query).toHaveBeenLastCalledWith('ROLLBACK');
    expect(transport.complete).not.toHaveBeenCalled();
  });

  it('rolls back transport completion and records one terminal receipt on retry', async () => {
    type TransactionState = {
      logicalStatus: 'active' | 'completed';
      attemptOutcome: 'running' | 'completed';
      sentinelCount: number;
    };
    let committed: TransactionState = {
      logicalStatus: 'active',
      attemptOutcome: 'running',
      sentinelCount: 0,
    };
    let transaction: TransactionState | undefined;
    const transactionalClient = {
      query: jest.fn(
        async (query: string | QueryConfig): Promise<{ rows: unknown[] }> => {
          const text = typeof query === 'string' ? query : query.text;

          if (text === 'BEGIN') {
            transaction = { ...committed };
            return { rows: [] };
          }
          if (text === 'COMMIT') {
            committed = { ...(transaction ?? committed) };
            transaction = undefined;
            return { rows: [] };
          }
          if (text === 'ROLLBACK') {
            transaction = undefined;
            return { rows: [] };
          }

          const current = transaction ?? committed;

          if (
            text.includes('FROM desktop_runtime.queue_job') &&
            text.includes('FOR UPDATE')
          ) {
            return {
              rows: [
                {
                  id: logicalId,
                  generation: 0,
                  current_physical_job_id: physicalJobId,
                  current_execution_token:
                    'ce3c741c-62b3-46d1-9f47-d313e27a8560',
                  status: current.logicalStatus,
                },
              ],
            };
          }

          if (
            text.includes('UPDATE desktop_runtime.queue_job') &&
            !text.includes('UPDATE desktop_runtime.queue_job_attempt') &&
            text.includes("status = 'completed'")
          ) {
            if (current.logicalStatus !== 'active') {
              return { rows: [] };
            }
            current.logicalStatus = 'completed';
            return { rows: [{ id: logicalId }] };
          }
          if (
            text.includes('UPDATE desktop_runtime.queue_job_attempt') &&
            text.includes("outcome = 'completed'")
          ) {
            if (current.attemptOutcome !== 'running') {
              return { rows: [] };
            }
            current.attemptOutcome = 'completed';
            return { rows: [{ id: 'attempt-id' }] };
          }
          if (text === 'INSERT INTO settlement_sentinel DEFAULT VALUES') {
            current.sentinelCount += 1;
          }

          return { rows: [] };
        },
      ),
      release: jest.fn(),
    };
    const transactionalPool = {
      connect: jest.fn().mockResolvedValue(transactionalClient),
      query: jest.fn().mockResolvedValue({ rows: [] }),
    };
    const failingTransport: LogicalTransport = {
      send: jest.fn().mockResolvedValue(undefined),
      complete: jest.fn().mockImplementation(async ({ db }) => {
        await db.executeSql('INSERT INTO settlement_sentinel DEFAULT VALUES');
        throw new Error('completion failed');
      }),
    };
    const settlement = {
      queueName,
      logicalJobId: logicalId,
      generation: 0,
      physicalJobId,
      executionToken: 'ce3c741c-62b3-46d1-9f47-d313e27a8560',
    };
    const failingLedger = new PgBossLogicalLedger({
      pool: transactionalPool as unknown as Pool,
      schema: 'desktop_runtime',
      transport: failingTransport,
    });

    await expect(failingLedger.settleSuccess(settlement)).rejects.toThrow(
      'completion failed',
    );
    expect(committed.logicalStatus).toBe('active');
    expect(committed.attemptOutcome).toBe('running');
    expect(committed.sentinelCount).toBe(0);

    const healthyTransport: LogicalTransport = {
      send: jest.fn().mockResolvedValue(undefined),
      complete: jest.fn().mockResolvedValue(undefined),
    };
    const healthyLedger = new PgBossLogicalLedger({
      pool: transactionalPool as unknown as Pool,
      schema: 'desktop_runtime',
      transport: healthyTransport,
    });

    await expect(healthyLedger.settleSuccess(settlement)).resolves.toBe(
      'settled',
    );
    expect(committed.logicalStatus).toBe('completed');
    expect(committed.attemptOutcome).toBe('completed');
    expect(committed.attemptOutcome === 'completed' ? 1 : 0).toBe(1);
  });

  it('maps canonical ledger states into neutral queue stats', async () => {
    await ledger.initialize();
    (pool.query as jest.Mock).mockClear();
    (pool.query as jest.Mock).mockResolvedValue({
      rows: [
        { status: 'queued', count: '2' },
        { status: 'active', count: '1' },
        { status: 'retry_wait', count: '3' },
        { status: 'completed', count: '4' },
        { status: 'failed', count: '5' },
        { status: 'cancelled', count: '1' },
      ],
    });

    await expect(ledger.getStats(queueName)).resolves.toEqual({
      queueName,
      created: 2,
      active: 1,
      retry: 3,
      completed: 4,
      failed: 6,
      healthy: true,
    });
    expect(pool.query).toHaveBeenCalledWith(
      expect.objectContaining({
        text: expect.stringContaining('FROM desktop_runtime.queue_job'),
        values: [queueName],
      }),
    );
  });

  it('finds jobs from canonical ledger rows and maps cancelled as failed', async () => {
    await ledger.initialize();
    (pool.query as jest.Mock).mockClear();
    (pool.query as jest.Mock).mockResolvedValue({
      rows: [
        {
          id: logicalId,
          job_name: 'reply-to-buyer',
          payload: { orderId: 'order-1' },
          status: 'cancelled',
          started_count: 2,
          created_at: new Date('2026-07-13T01:00:00.000Z'),
          updated_at: new Date('2026-07-13T01:00:01.000Z'),
          failed_at: new Date('2026-07-13T01:00:02.000Z'),
          completed_at: null,
          last_error: { name: 'Error', message: 'cancelled by shutdown' },
        },
      ],
    });

    await expect(ledger.findJobs(queueName, ['failed'])).resolves.toEqual([
      {
        id: logicalId,
        name: 'reply-to-buyer',
        data: { orderId: 'order-1' },
        state: 'failed',
        attemptsMade: 2,
        createdAt: Date.parse('2026-07-13T01:00:00.000Z'),
        processedAt: Date.parse('2026-07-13T01:00:01.000Z'),
        finishedAt: Date.parse('2026-07-13T01:00:02.000Z'),
        failedReason: 'cancelled by shutdown',
      },
    ]);
    expect(pool.query).toHaveBeenCalledWith(
      expect.objectContaining({
        values: [queueName, ['failed', 'cancelled']],
      }),
    );
  });

  it('fences a dead letter for an old physical generation', async () => {
    await ledger.initialize();
    (client.query as jest.Mock).mockClear();
    (client.query as jest.Mock).mockImplementation(
      async (query: string | QueryConfig) => {
        const text = typeof query === 'string' ? query : query.text;

        if (text.includes('FROM desktop_runtime.queue_job')) {
          return {
            rows: [
              {
                id: logicalId,
                generation: 1,
                current_physical_job_id: physicalJobId,
                current_execution_token: 'ce3c741c-62b3-46d1-9f47-d313e27a8560',
                status: 'active',
              },
            ],
          };
        }

        return { rows: [] };
      },
    );

    await expect(
      ledger.reconcileDeadLetter({
        queueName,
        physicalJobId: stalePhysicalId,
        envelope: { version: 2, logicalJobId: logicalId, generation: 0 },
      }),
    ).resolves.toBe('fenced');

    expect(
      (client.query as jest.Mock).mock.calls.some(([query]) =>
        (typeof query === 'string' ? query : query.text).includes(
          'UPDATE desktop_runtime.queue_job',
        ),
      ),
    ).toBe(false);
    expect(client.query).toHaveBeenLastCalledWith('COMMIT');
  });
});

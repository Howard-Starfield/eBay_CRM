import { type Pool, type PoolClient, type QueryConfig } from 'pg';

import { type MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';

import { PgBossLogicalLedger } from './pg-boss-logical-ledger';
import { type LogicalTransport } from './pg-boss-logical-ledger.types';

type ClientMock = Pick<PoolClient, 'query' | 'release'>;
type PoolMock = Pick<Pool, 'connect' | 'query'>;

const queueName = 'aiQueue' as MessageQueue;
const logicalId = '95ba9af8-0142-45b6-b8ca-d68a16c1e9ec';

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
});

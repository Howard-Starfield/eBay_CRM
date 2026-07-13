import { type Pool, type PoolClient } from 'pg';

import {
  PgBossIntervalScheduler,
  type PgBossIntervalSchedule,
} from './pg-boss-interval-scheduler';

type ClientMock = Pick<PoolClient, 'query' | 'release'>;
type PoolMock = Pick<Pool, 'connect' | 'query'>;

const schedule: PgBossIntervalSchedule = {
  scheduleKey: 'reply-to-buyer.order-1',
  queueName: 'aiQueue' as never,
  jobName: 'reply-to-buyer',
  payload: {
    version: 1,
    jobName: 'reply-to-buyer',
    data: { orderId: 'order-1' },
  },
  everyMs: 250,
  remainingRuns: 2,
};

describe('PgBossIntervalScheduler', () => {
  let client: ClientMock;
  let pool: PoolMock;
  let enqueue: jest.Mock;
  let scheduler: PgBossIntervalScheduler;

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
    enqueue = jest.fn().mockResolvedValue(undefined);
    scheduler = new PgBossIntervalScheduler({
      pool: pool as Pool,
      schema: 'desktop_runtime',
      pollIntervalMs: 1_000,
      enqueue,
    });
  });

  afterEach(async () => {
    await scheduler.stop();
  });

  it('creates the isolated persisted schedule table and deterministically upserts a schedule', async () => {
    await scheduler.upsert(schedule);

    expect(pool.query).toHaveBeenNthCalledWith(
      1,
      expect.stringContaining(
        'CREATE TABLE IF NOT EXISTS desktop_runtime.interval_schedule',
      ),
    );
    expect(pool.query).toHaveBeenNthCalledWith(
      2,
      expect.stringContaining('ON CONFLICT (schedule_key) DO UPDATE'),
      [
        schedule.scheduleKey,
        schedule.queueName,
        schedule.jobName,
        schedule.payload,
        schedule.everyMs,
        schedule.remainingRuns,
      ],
    );
    expect((pool.query as jest.Mock).mock.calls[1][0]).toContain(
      'desktop_runtime.interval_schedule.payload IS DISTINCT FROM EXCLUDED.payload',
    );
  });

  it('claims due schedules transactionally, advances from the prior value, and deletes exhausted limits', async () => {
    (client.query as jest.Mock)
      .mockResolvedValueOnce({ rows: [] })
      .mockResolvedValueOnce({
        rows: [
          {
            schedule_key: schedule.scheduleKey,
            queue_name: schedule.queueName,
            job_name: schedule.jobName,
            payload: schedule.payload,
            every_ms: schedule.everyMs,
            remaining_runs: 1,
            next_run_at: new Date('2026-07-13T12:00:00.000Z'),
          },
        ],
      })
      .mockResolvedValue({ rows: [] });

    await (
      scheduler as unknown as { pollDueSchedules(): Promise<void> }
    ).pollDueSchedules();

    expect(client.query).toHaveBeenNthCalledWith(1, 'BEGIN');
    expect(client.query).toHaveBeenNthCalledWith(
      2,
      expect.stringMatching(/FOR UPDATE SKIP LOCKED/),
    );
    expect(enqueue).toHaveBeenCalledWith(
      expect.objectContaining({
        queueName: schedule.queueName,
        payload: schedule.payload,
      }),
      expect.objectContaining({ executeSql: expect.any(Function) }),
    );
    expect(client.query).toHaveBeenCalledWith(
      expect.stringContaining(
        "next_run_at = next_run_at + (every_ms * interval '1 millisecond')",
      ),
      [schedule.scheduleKey],
    );
    expect(client.query).toHaveBeenCalledWith(
      expect.stringContaining('DELETE FROM desktop_runtime.interval_schedule'),
      [schedule.scheduleKey],
    );
    expect(client.query).toHaveBeenLastCalledWith('COMMIT');
    expect(client.release).toHaveBeenCalledTimes(1);
  });

  it('rolls back a claim when enqueueing fails', async () => {
    (client.query as jest.Mock)
      .mockResolvedValueOnce({ rows: [] })
      .mockResolvedValueOnce({
        rows: [
          {
            schedule_key: schedule.scheduleKey,
            queue_name: schedule.queueName,
            job_name: schedule.jobName,
            payload: schedule.payload,
            every_ms: schedule.everyMs,
            remaining_runs: null,
            next_run_at: new Date(),
          },
        ],
      })
      .mockResolvedValue({ rows: [] });
    enqueue.mockRejectedValueOnce(new Error('enqueue failed'));

    await expect(
      (
        scheduler as unknown as { pollDueSchedules(): Promise<void> }
      ).pollDueSchedules(),
    ).rejects.toThrow('enqueue failed');

    expect(client.query).toHaveBeenLastCalledWith('ROLLBACK');
    expect(client.release).toHaveBeenCalledTimes(1);
  });

  it('removes a deterministic schedule from persisted state', async () => {
    await scheduler.remove(schedule.scheduleKey);

    expect(pool.query).toHaveBeenLastCalledWith(
      expect.stringContaining('DELETE FROM desktop_runtime.interval_schedule'),
      [schedule.scheduleKey],
    );
  });
});

import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const EXPECTED_PG_BOSS_REJECTION_CASES = [
  'pg-boss message queue driver contract keeps stalled-recovery allowance independent from ordinary handler failures',
  'pg-boss message queue driver contract keeps crash recovery available when handler retryLimit is zero',
  'pg-boss message queue driver contract uses one stalled recovery by default without adding handler retries',
];

export const EXPECTED_PG_BOSS_PASS_CASES = [
  'pg-boss adapter integration regressions atomically deduplicates concurrent waiting jobs across driver instances',
  'pg-boss adapter integration regressions persists stalled recovery policy across separate producer and worker drivers',
  'pg-boss adapter integration regressions does not abort a healthy handler after one Bull-style lock period',
  'pg-boss adapter integration regressions preserves second-level cron cadence across consecutive occurrences',
  'pg-boss adapter integration regressions consumes a cron limit per occurrence without suppressing its retry',
  'pg-boss message queue driver contract delivers an immediate job with its name and data preserved',
  'pg-boss message queue driver contract does not deliver a delayed job early and eventually delivers it',
  'pg-boss message queue driver contract processes lower numeric priority before higher numeric priority',
  'pg-boss message queue driver contract attempts a job exactly three times when retryLimit is two',
  'pg-boss message queue driver contract never exceeds configured concurrency',
  'pg-boss message queue driver contract deduplicates a matching ready job including prioritized jobs but permits another once active',
  'pg-boss message queue driver contract upserts and removes a cron-pattern schedule',
  'pg-boss message queue driver contract upserts an interval schedule, honors its limit, and removes it',
  'pg-boss message queue driver contract reclaims a non-acknowledged job after a worker is killed and restarted',
  'pg-boss message queue driver contract bounds shutdown and aborts a long-running handler',
  'pg-boss message queue driver contract retains completed and failed jobs for inspection',
  'pg-boss message queue driver contract reports healthy queue depth and active and failed counts',
  'pg-boss message queue driver contract retries a failed job and deletes a waiting job',
  'pg-boss message queue driver contract leaves a job available when the worker is stopped before claim',
  'pg-boss message queue driver contract recovers a claimed mid-handler job after lease expiry without job loss',
  'pg-boss message queue driver contract re-enters after a PostgreSQL write while a unique receipt prevents duplicate durable state',
  'pg-boss message queue driver contract does not repeat an external side effect protected by an idempotency receipt after termination before acknowledgement',
];

export const EXPECTED_PG_BOSS_PENDING_CASES = [
  'BullMQ message queue driver contract requires RUNTIME_CONTRACT_DRIVER=bullmq',
];

const EXPECTED_REPORT_COUNTS = {
  numFailedTestSuites: 1,
  numFailedTests: 3,
  numPassedTestSuites: 0,
  numPassedTests: 22,
  numPendingTestSuites: 1,
  numPendingTests: 1,
  numRuntimeErrorTestSuites: 0,
  numTodoTests: 0,
  numTotalTestSuites: 2,
  numTotalTests: 26,
};

const fail = (message) => {
  throw new Error(message);
};

export const verifyPgBossRejectionReport = (report) => {
  if (report.wasInterrupted) {
    fail('The pg-boss contract run was interrupted.');
  }

  if (report.numRuntimeErrorTestSuites !== 0) {
    fail('The pg-boss contract run contained test-suite runtime errors.');
  }

  for (const [field, expected] of Object.entries(EXPECTED_REPORT_COUNTS)) {
    if (report[field] !== expected) {
      fail(
        `Expected ${field}=${expected}; received ${String(report[field])}. The rejection report counts changed.`,
      );
    }
  }

  if (report.success !== false) {
    fail('Expected the raw pg-boss Jest report to have success=false.');
  }

  const suiteStatuses = report.testResults.map(({ status }) => status).sort();

  if (suiteStatuses.join(',') !== 'failed,skipped') {
    fail(
      `Expected exactly one failed pg-boss suite and one skipped BullMQ suite; received ${suiteStatuses.join(',')}.`,
    );
  }

  const assertions = report.testResults.flatMap(
    ({ assertionResults }) => assertionResults,
  );
  const actualStatuses = new Map(
    assertions.map(({ fullName, status }) => [fullName, status]),
  );
  const expectedStatuses = new Map([
    ...EXPECTED_PG_BOSS_PASS_CASES.map((name) => [name, 'passed']),
    ...EXPECTED_PG_BOSS_REJECTION_CASES.map((name) => [name, 'failed']),
    ...EXPECTED_PG_BOSS_PENDING_CASES.map((name) => [name, 'pending']),
  ]);

  if (
    assertions.length !== expectedStatuses.size ||
    actualStatuses.size !== assertions.length
  ) {
    fail(
      'Expected 26 uniquely named assertions in the pg-boss rejection report.',
    );
  }

  const statusMismatches = [...expectedStatuses].filter(
    ([name, status]) => actualStatuses.get(name) !== status,
  );
  const unexpectedAssertions = [...actualStatuses.keys()].filter(
    (name) => !expectedStatuses.has(name),
  );

  if (statusMismatches.length > 0 || unexpectedAssertions.length > 0) {
    fail(
      `The pg-boss rejection assertion status map changed. Mismatches: ${
        statusMismatches
          .map(
            ([name, status]) =>
              `${name} expected ${status}, received ${String(actualStatuses.get(name))}`,
          )
          .join('; ') || 'none'
      }. Unexpected: ${unexpectedAssertions.join('; ') || 'none'}.`,
    );
  }

  return {
    decision: 'REJECT_PG_BOSS',
    failedCases: [...EXPECTED_PG_BOSS_REJECTION_CASES],
    passedCases: report.numPassedTests,
  };
};

const parseArguments = () => {
  const reportIndex = process.argv.indexOf('--report');

  if (reportIndex === -1 || !process.argv[reportIndex + 1]) {
    fail(
      'Usage: node scripts/verify-pg-boss-rejection.mjs --report /path/to/jest-report.json',
    );
  }

  return { reportPath: path.resolve(process.argv[reportIndex + 1]) };
};

const runCli = async () => {
  const { reportPath } = parseArguments();
  const report = JSON.parse(await readFile(reportPath, 'utf8'));
  const result = verifyPgBossRejectionReport(report);

  process.stdout.write(
    `${result.decision}: observed exactly ${result.failedCases.length} documented pg-boss incompatibilities; ${result.passedCases} other spike cases passed.\n`,
  );
};

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  await runCli();
}

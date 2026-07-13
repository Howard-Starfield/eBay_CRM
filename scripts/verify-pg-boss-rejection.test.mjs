import assert from 'node:assert/strict';
import test from 'node:test';

import {
  EXPECTED_PG_BOSS_PASS_CASES,
  EXPECTED_PG_BOSS_PENDING_CASES,
  EXPECTED_PG_BOSS_REJECTION_CASES,
  verifyPgBossRejectionReport,
} from './verify-pg-boss-rejection.mjs';

const createValidReport = () => ({
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
  success: false,
  wasInterrupted: false,
  testResults: [
    {
      status: 'failed',
      assertionResults: [
        ...EXPECTED_PG_BOSS_PASS_CASES.map((fullName) => ({
          fullName,
          status: 'passed',
        })),
        ...EXPECTED_PG_BOSS_REJECTION_CASES.map((fullName) => ({
          fullName,
          status: 'failed',
        })),
      ],
    },
    {
      status: 'skipped',
      assertionResults: EXPECTED_PG_BOSS_PENDING_CASES.map((fullName) => ({
        fullName,
        status: 'pending',
      })),
    },
  ],
});

const assertionNamed = (report, fullName) =>
  report.testResults
    .flatMap(({ assertionResults }) => assertionResults)
    .find((assertion) => assertion.fullName === fullName);

test('accepts the exact complete pg-boss rejection report', () => {
  assert.doesNotThrow(() => verifyPgBossRejectionReport(createValidReport()));
});

test('rejects a report when pg-boss unexpectedly satisfies one required semantic', () => {
  const report = createValidReport();

  assertionNamed(report, EXPECTED_PG_BOSS_REJECTION_CASES[0]).status = 'passed';
  report.numFailedTests = 2;
  report.numPassedTests = 23;

  assert.throws(
    () => verifyPgBossRejectionReport(report),
    /rejection report counts changed/,
  );
});

test('rejects a report with an unrelated contract failure', () => {
  const report = createValidReport();

  assertionNamed(report, EXPECTED_PG_BOSS_PASS_CASES[0]).status = 'failed';
  report.numFailedTests = 4;
  report.numPassedTests = 21;

  assert.throws(
    () => verifyPgBossRejectionReport(report),
    /rejection report counts changed/,
  );
});

test('rejects swapped mandatory pass and opposite-adapter sentinel statuses', () => {
  const report = createValidReport();

  assertionNamed(
    report,
    'pg-boss message queue driver contract delivers an immediate job with its name and data preserved',
  ).status = 'pending';
  assertionNamed(
    report,
    'BullMQ message queue driver contract requires RUNTIME_CONTRACT_DRIVER=bullmq',
  ).status = 'passed';

  assert.throws(
    () => verifyPgBossRejectionReport(report),
    /assertion status map/,
  );
});

test('rejects interrupted, runtime-error, or incomplete runs', () => {
  assert.throws(
    () =>
      verifyPgBossRejectionReport({
        ...createValidReport(),
        wasInterrupted: true,
      }),
    /interrupted/,
  );
  assert.throws(
    () =>
      verifyPgBossRejectionReport({
        ...createValidReport(),
        numRuntimeErrorTestSuites: 1,
      }),
    /runtime errors/,
  );
  assert.throws(
    () =>
      verifyPgBossRejectionReport({
        ...createValidReport(),
        numPassedTests: 21,
        numTotalTests: 25,
      }),
    /rejection report counts changed/,
  );
});

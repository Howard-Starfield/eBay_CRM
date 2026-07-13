import assert from 'node:assert/strict';
import test from 'node:test';

import {
  EXPECTED_PG_BOSS_REJECTION_CASES,
  verifyPgBossRejectionReport,
} from './verify-pg-boss-rejection.mjs';

const reportWith = ({ failedNames, passedTests = 22 }) => ({
  numFailedTests: failedNames.length,
  numPassedTests: passedTests,
  numRuntimeErrorTestSuites: 0,
  wasInterrupted: false,
  testResults: [
    {
      assertionResults: [
        ...failedNames.map((fullName) => ({ fullName, status: 'failed' })),
        ...Array.from({ length: passedTests }, (_, index) => ({
          fullName: `passing contract case ${index + 1}`,
          status: 'passed',
        })),
      ],
    },
  ],
});

test('accepts only the exact three known pg-boss semantic failures', () => {
  assert.doesNotThrow(() =>
    verifyPgBossRejectionReport(
      reportWith({ failedNames: [...EXPECTED_PG_BOSS_REJECTION_CASES] }),
    ),
  );
});

test('rejects a report when pg-boss unexpectedly satisfies one required semantic', () => {
  assert.throws(
    () =>
      verifyPgBossRejectionReport(
        reportWith({
          failedNames: EXPECTED_PG_BOSS_REJECTION_CASES.slice(0, 2),
        }),
      ),
    /Expected exactly the three documented pg-boss incompatibilities/,
  );
});

test('rejects a report with an unrelated contract failure', () => {
  assert.throws(
    () =>
      verifyPgBossRejectionReport(
        reportWith({
          failedNames: [
            ...EXPECTED_PG_BOSS_REJECTION_CASES,
            'pg-boss message queue driver contract delivers an immediate job',
          ],
        }),
      ),
    /Unexpected failed contract cases/,
  );
});

test('rejects interrupted, runtime-error, or incomplete runs', () => {
  assert.throws(
    () =>
      verifyPgBossRejectionReport({
        ...reportWith({ failedNames: [...EXPECTED_PG_BOSS_REJECTION_CASES] }),
        wasInterrupted: true,
      }),
    /interrupted/,
  );
  assert.throws(
    () =>
      verifyPgBossRejectionReport({
        ...reportWith({ failedNames: [...EXPECTED_PG_BOSS_REJECTION_CASES] }),
        numRuntimeErrorTestSuites: 1,
      }),
    /runtime errors/,
  );
  assert.throws(
    () =>
      verifyPgBossRejectionReport(
        reportWith({
          failedNames: [...EXPECTED_PG_BOSS_REJECTION_CASES],
          passedTests: 21,
        }),
      ),
    /at least 22 passing pg-boss spike cases/,
  );
});

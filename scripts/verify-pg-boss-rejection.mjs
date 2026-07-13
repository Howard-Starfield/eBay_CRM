import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const EXPECTED_PG_BOSS_REJECTION_CASES = [
  'pg-boss message queue driver contract keeps stalled-recovery allowance independent from ordinary handler failures',
  'pg-boss message queue driver contract keeps crash recovery available when handler retryLimit is zero',
  'pg-boss message queue driver contract uses one stalled recovery by default without adding handler retries',
];

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

  const failedNames = report.testResults
    .flatMap(({ assertionResults }) => assertionResults)
    .filter(({ status }) => status === 'failed')
    .map(({ fullName }) => fullName);
  const unexpectedFailures = failedNames.filter(
    (name) => !EXPECTED_PG_BOSS_REJECTION_CASES.includes(name),
  );

  if (unexpectedFailures.length > 0) {
    fail(`Unexpected failed contract cases: ${unexpectedFailures.join('; ')}`);
  }

  const exactExpectedSet =
    failedNames.length === EXPECTED_PG_BOSS_REJECTION_CASES.length &&
    EXPECTED_PG_BOSS_REJECTION_CASES.every((name) =>
      failedNames.includes(name),
    );

  if (!exactExpectedSet || report.numFailedTests !== 3) {
    fail(
      'Expected exactly the three documented pg-boss incompatibilities; the rejection evidence changed.',
    );
  }

  if (report.numPassedTests < 22) {
    fail(
      `Expected at least 22 passing pg-boss spike cases; received ${report.numPassedTests}.`,
    );
  }

  return {
    decision: 'REJECT_PG_BOSS',
    failedCases: failedNames,
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

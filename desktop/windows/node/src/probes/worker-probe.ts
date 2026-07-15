import { completeProbeProcess, runProbe } from './probe-orchestrator.js';

async function initializeWorkerRole(): Promise<void> {}

const result = await runProbe({
  expectedRole: 'worker',
  args: process.argv.slice(2),
  environment: process.env,
  processId: process.pid,
  initialize: initializeWorkerRole,
});

completeProbeProcess(result);

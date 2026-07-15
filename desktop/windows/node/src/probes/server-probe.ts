import { completeProbeProcess, runProbe } from './probe-orchestrator.js';

async function initializeServerRole(): Promise<void> {}

const result = await runProbe({
  expectedRole: 'server',
  args: process.argv.slice(2),
  environment: process.env,
  processId: process.pid,
  initialize: initializeServerRole,
});

completeProbeProcess(result);

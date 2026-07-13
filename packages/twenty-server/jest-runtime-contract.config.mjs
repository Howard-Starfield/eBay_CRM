import serverJestConfig from './jest.config.mjs';

export default {
  ...serverJestConfig,
  testRegex: '.*\\.driver\\.contract-spec\\.ts$',
  maxWorkers: 1,
  testTimeout: 60_000,
};

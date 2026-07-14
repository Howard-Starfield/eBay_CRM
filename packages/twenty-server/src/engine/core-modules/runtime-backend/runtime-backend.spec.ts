import {
  RUNTIME_BACKENDS,
  isRuntimeBackend,
} from 'src/engine/core-modules/runtime-backend/runtime-backend.constants';
import { ConfigVariables } from 'src/engine/core-modules/twenty-config/config-variables';

describe('runtime backend', () => {
  it('should expose the supported runtime backends', () => {
    expect(RUNTIME_BACKENDS).toEqual({
      POSTGRES_DESKTOP: 'postgres-desktop',
      REDIS: 'redis',
    });
  });

  it('should identify supported runtime backends', () => {
    expect(isRuntimeBackend('postgres-desktop')).toBe(true);
    expect(isRuntimeBackend('redis')).toBe(true);
    expect(isRuntimeBackend('sqlite')).toBe(false);
  });

  it('should default to the postgres desktop runtime backend', () => {
    expect(new ConfigVariables().RUNTIME_BACKEND).toBe('postgres-desktop');
  });
});

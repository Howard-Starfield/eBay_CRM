import {
  RUNTIME_BACKENDS,
  isRuntimeBackend,
} from 'src/engine/core-modules/runtime-backend/runtime-backend.constants';
import {
  ConfigVariables,
  validate,
} from 'src/engine/core-modules/twenty-config/config-variables';

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

  it('should not infer a runtime backend when none is provided', () => {
    const config = new ConfigVariables();

    expect(config.RUNTIME_BACKEND).toBeUndefined();
    expect(() => validate({ REDIS_URL: 'redis://localhost:6379' })).toThrow(
      'Config variables validation failed',
    );
  });

  it.each(['postgres-desktop', 'redis'])(
    'should accept the explicit %s runtime backend',
    (runtimeBackend) => {
      expect(
        validate({ RUNTIME_BACKEND: runtimeBackend }).RUNTIME_BACKEND,
      ).toBe(runtimeBackend);
    },
  );

  it.each([
    null,
    'sqlite',
    'Redis',
    ' redis',
    'redis ',
    'POSTGRES-DESKTOP',
    '',
  ])('should reject the malformed runtime backend %j', (runtimeBackend) => {
    expect(() => validate({ RUNTIME_BACKEND: runtimeBackend })).toThrow(
      'Config variables validation failed',
    );
  });
});

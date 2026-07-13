export const RUNTIME_BACKENDS = {
  POSTGRES_DESKTOP: 'postgres-desktop',
  REDIS: 'redis',
} as const;

export type RuntimeBackend =
  (typeof RUNTIME_BACKENDS)[keyof typeof RUNTIME_BACKENDS];

export const isRuntimeBackend = (value: unknown): value is RuntimeBackend =>
  typeof value === 'string' &&
  Object.values(RUNTIME_BACKENDS).includes(value as RuntimeBackend);

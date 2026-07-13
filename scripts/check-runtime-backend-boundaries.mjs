import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const restrictedPackages = new Set([
  'bullmq',
  'ioredis',
  'redis',
  'connect-redis',
  'cache-manager-redis-yet',
  'graphql-redis-subscriptions',
]);

const supportedExtensions = new Set(['.ts', '.tsx', '.js', '.mjs', '.cjs']);
const skippedDirectories = new Set([
  '.git',
  '.yarn',
  'node_modules',
  'dist',
  'coverage',
]);
const packageSpecifierPatterns = [
  /\bfrom\s*['"]([^'"]+)['"]/g,
  /\bimport\s*['"]([^'"]+)['"]/g,
  /\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
  /\brequire\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
];

const normalizePath = (value) => value.split(path.sep).join('/');

const walkSourceFiles = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const sourceFiles = [];

  for (const entry of entries) {
    if (entry.isDirectory() && skippedDirectories.has(entry.name)) {
      continue;
    }

    const entryPath = path.join(directory, entry.name);

    if (entry.isDirectory()) {
      sourceFiles.push(...(await walkSourceFiles(entryPath)));
    } else if (
      entry.isFile() &&
      supportedExtensions.has(path.extname(entry.name))
    ) {
      sourceFiles.push(entryPath);
    }
  }

  return sourceFiles;
};

const findRestrictedSpecifiers = (source) => {
  const specifiers = new Set();

  for (const pattern of packageSpecifierPatterns) {
    for (const match of source.matchAll(pattern)) {
      if (restrictedPackages.has(match[1])) {
        specifiers.add(match[1]);
      }
    }
  }

  return specifiers;
};

export const findViolations = async ({ root, policy }) => {
  const baselineDirectImports = new Set(
    policy.baselineDirectImports.map(normalizePath),
  );
  const adapterPathPrefixes = policy.adapterPathPrefixes.map(normalizePath);
  const sourcePathPrefixes = (policy.sourcePathPrefixes ?? ['']).map(
    normalizePath,
  );
  const sourceFiles = await walkSourceFiles(root);
  const violations = [];

  for (const sourceFile of sourceFiles) {
    const relativePath = normalizePath(path.relative(root, sourceFile));

    if (
      !sourcePathPrefixes.some((prefix) => relativePath.startsWith(prefix)) ||
      baselineDirectImports.has(relativePath) ||
      adapterPathPrefixes.some((prefix) => relativePath.startsWith(prefix))
    ) {
      continue;
    }

    const source = await readFile(sourceFile, 'utf8');

    for (const packageSpecifier of findRestrictedSpecifiers(source)) {
      violations.push(`${relativePath} -> ${packageSpecifier}`);
    }
  }

  return violations.sort();
};

const runCli = async () => {
  const repositoryRoot = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '..',
  );
  const policyPath = path.join(
    repositoryRoot,
    'packages',
    'twenty-server',
    'runtime-backend-boundaries.json',
  );
  const policy = JSON.parse(await readFile(policyPath, 'utf8'));
  const violations = await findViolations({ root: repositoryRoot, policy });

  if (violations.length > 0) {
    console.error(
      ['Runtime backend boundary violations:', ...violations].join('\n'),
    );
    process.exitCode = 1;
    return;
  }

  console.log('Runtime backend boundaries passed.');
};

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  await runCli();
}

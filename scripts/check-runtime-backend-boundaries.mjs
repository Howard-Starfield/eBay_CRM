import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import ts from 'typescript';

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
const scriptKinds = new Map([
  ['.ts', ts.ScriptKind.TS],
  ['.tsx', ts.ScriptKind.TSX],
  ['.js', ts.ScriptKind.JS],
  ['.mjs', ts.ScriptKind.JS],
  ['.cjs', ts.ScriptKind.JS],
]);

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

const findRestrictedSpecifiers = ({ source, sourceFilePath }) => {
  const specifiers = new Set();
  const sourceFile = ts.createSourceFile(
    sourceFilePath,
    source,
    ts.ScriptTarget.Latest,
    true,
    scriptKinds.get(path.extname(sourceFilePath)),
  );

  const addSpecifier = (moduleSpecifier) => {
    if (
      moduleSpecifier &&
      ts.isStringLiteralLike(moduleSpecifier) &&
      restrictedPackages.has(moduleSpecifier.text)
    ) {
      specifiers.add(moduleSpecifier.text);
    }
  };

  const visit = (node) => {
    if (
      ts.isImportEqualsDeclaration(node) &&
      ts.isExternalModuleReference(node.moduleReference)
    ) {
      addSpecifier(node.moduleReference.expression);
    } else if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) {
      addSpecifier(node.moduleSpecifier);
    } else if (ts.isCallExpression(node)) {
      const isDynamicImport =
        node.expression.kind === ts.SyntaxKind.ImportKeyword;
      const isRequireCall =
        ts.isIdentifier(node.expression) && node.expression.text === 'require';

      if (isDynamicImport || isRequireCall) {
        addSpecifier(node.arguments[0]);
      }
    }

    ts.forEachChild(node, visit);
  };

  visit(sourceFile);

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

    for (const packageSpecifier of findRestrictedSpecifiers({
      source,
      sourceFilePath: sourceFile,
    })) {
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

import {
  copyFile,
  lstat,
  mkdir,
  readdir,
  realpath,
} from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const serverRoot = path.resolve(process.cwd());
const packagesRoot = path.resolve(serverRoot, '..');
const sdkRoot = path.join(packagesRoot, 'twenty-client-sdk');
const sourcePackage = path.join(sdkRoot, 'package.json');
const sourceDist = path.join(sdkRoot, 'dist');
const destinationRoot = path.join(
  serverRoot,
  'dist',
  'assets',
  'twenty-client-sdk',
);
const MAXIMUM_TRAVERSAL_DEPTH = 64;
const MAXIMUM_TRAVERSAL_ENTRIES = 250_000;
const MAXIMUM_TRAVERSAL_BYTES = 16 * 1024 * 1024 * 1024;

function createTraversalBudget() {
  let entries = 0;
  let aggregateBytes = 0;
  return {
    observe(metadata, depth) {
      const bytes = metadata.isFile() ? metadata.size : 0;
      if (!Number.isSafeInteger(bytes) || bytes < 0 ||
          depth < 0 || depth > MAXIMUM_TRAVERSAL_DEPTH ||
          entries === MAXIMUM_TRAVERSAL_ENTRIES ||
          aggregateBytes > MAXIMUM_TRAVERSAL_BYTES - bytes) {
        throw new Error('copy-build-assets-traversal-budget');
      }
      entries += 1;
      aggregateBytes += bytes;
    },
  };
}

function isWithin(candidate, root) {
  const relative = path.relative(root, candidate);
  return relative === '' ||
    (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

async function requireOrdinary(pathname, kind, requiredRoot) {
  const metadata = await lstat(pathname);
  if (metadata.isSymbolicLink() ||
      (kind === 'directory' ? !metadata.isDirectory() : !metadata.isFile())) {
    throw new Error('copy-build-assets-nonordinary-input');
  }
  const canonical = await realpath(pathname);
  if (!isWithin(canonical, requiredRoot)) {
    throw new Error('copy-build-assets-path-escape');
  }
  return metadata;
}

async function requireExactEntryIdentity(pathname) {
  const parent = path.dirname(pathname);
  const expected = path.basename(pathname);
  const entries = await readdir(parent);
  const matches = entries.filter(
    (entry) => entry.toUpperCase() === expected.toUpperCase(),
  );
  if (matches.length !== 1 || matches[0] !== expected) {
    throw new Error('copy-build-assets-destination-identity-invalid');
  }
}

async function validateExistingDestinationAncestors(target, canonicalServer) {
  const absoluteTarget = path.resolve(target);
  if (!isWithin(absoluteTarget, serverRoot)) {
    throw new Error('copy-build-assets-path-escape');
  }
  const relative = path.relative(serverRoot, absoluteTarget);
  let current = serverRoot;
  for (const segment of relative.split(path.sep).filter(Boolean)) {
    current = path.join(current, segment);
    try {
      await lstat(current);
    } catch (error) {
      if (error?.code === 'ENOENT') {
        return;
      }
      throw error;
    }
    await requireExactEntryIdentity(current);
    await requireOrdinary(current, 'directory', canonicalServer);
  }
}

async function validateOrdinaryDestinationTree(
  root,
  canonicalServer,
  budget,
  depth,
) {
  const rootMetadata = await requireOrdinary(root, 'directory', canonicalServer);
  budget.observe(rootMetadata, depth);
  const entries = await readdir(root, { withFileTypes: true });
  const identities = new Set();
  for (const entry of entries) {
    const identity = entry.name.toUpperCase();
    if (identities.has(identity)) {
      throw new Error('copy-build-assets-destination-identity-invalid');
    }
    identities.add(identity);
    const pathname = path.join(root, entry.name);
    if (entry.isSymbolicLink()) {
      throw new Error('copy-build-assets-reparse-destination');
    }
    if (entry.isDirectory()) {
      await requireOrdinary(pathname, 'directory', canonicalServer);
      await validateOrdinaryDestinationTree(
        pathname,
        canonicalServer,
        budget,
        depth + 1,
      );
      continue;
    }
    if (!entry.isFile()) {
      throw new Error('copy-build-assets-nonordinary-destination');
    }
    const metadata = await requireOrdinary(pathname, 'file', canonicalServer);
    budget.observe(metadata, depth + 1);
  }
}

async function copyOrdinaryDirectory(source, destination, budget, depth) {
  const sourceMetadata = await requireOrdinary(source, 'directory', sdkRoot);
  budget.observe(sourceMetadata, depth);
  await mkdir(destination, { recursive: false });
  const entries = await readdir(source, { withFileTypes: true });
  entries.sort((left, right) => left.name.localeCompare(right.name, 'en'));
  for (const entry of entries) {
    const sourcePath = path.join(source, entry.name);
    const destinationPath = path.join(destination, entry.name);
    if (entry.isSymbolicLink()) {
      throw new Error('copy-build-assets-reparse-input');
    }
    if (entry.isDirectory()) {
      await copyOrdinaryDirectory(
        sourcePath,
        destinationPath,
        budget,
        depth + 1,
      );
      continue;
    }
    if (!entry.isFile()) {
      throw new Error('copy-build-assets-nonordinary-input');
    }
    const metadata = await requireOrdinary(sourcePath, 'file', sdkRoot);
    budget.observe(metadata, depth + 1);
    await copyFile(sourcePath, destinationPath);
  }
}

async function main() {
  const canonicalServer = await realpath(serverRoot);
  const canonicalPackages = await realpath(packagesRoot);
  if (!isWithin(canonicalServer, canonicalPackages) ||
      path.basename(canonicalServer) !== 'twenty-server') {
    throw new Error('copy-build-assets-working-directory-invalid');
  }
  await requireOrdinary(path.join(serverRoot, 'dist'), 'directory', canonicalServer);
  await requireOrdinary(sdkRoot, 'directory', canonicalPackages);
  const sourcePackageMetadata = await requireOrdinary(sourcePackage, 'file', sdkRoot);
  await requireOrdinary(sourceDist, 'directory', sdkRoot);

  try {
    await lstat(destinationRoot);
    throw new Error('copy-build-assets-destination-exists');
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error;
    }
  }

  await validateExistingDestinationAncestors(
    path.dirname(destinationRoot),
    canonicalServer,
  );
  await mkdir(path.dirname(destinationRoot), { recursive: true });
  await mkdir(destinationRoot, { recursive: false });
  await validateExistingDestinationAncestors(destinationRoot, canonicalServer);
  const sourceBudget = createTraversalBudget();
  sourceBudget.observe(sourcePackageMetadata, 0);
  await copyFile(sourcePackage, path.join(destinationRoot, 'package.json'));
  await copyOrdinaryDirectory(
    sourceDist,
    path.join(destinationRoot, 'dist'),
    sourceBudget,
    0,
  );
  await validateExistingDestinationAncestors(destinationRoot, canonicalServer);
  await validateOrdinaryDestinationTree(
    destinationRoot,
    canonicalServer,
    createTraversalBudget(),
    0,
  );
}

await main();

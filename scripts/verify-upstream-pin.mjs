import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import { resolve, relative } from 'node:path';

const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  args.set(process.argv[index], process.argv[index + 1]);
}

const upstreamRoot = resolve(args.get('--upstream-root') ?? '');
const localRoot = resolve(args.get('--local-root') ?? process.cwd());
if (!args.has('--upstream-root')) {
  throw new Error('Usage: node scripts/verify-upstream-pin.mjs --upstream-root C:\\path\\to\\upstream [--local-root C:\\path\\to\\local]');
}

const walk = async (root, directory = root) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const paths = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    if (entry.name === '.git') continue;
    const absolutePath = resolve(directory, entry.name);
    if (entry.isDirectory()) paths.push(...await walk(root, absolutePath));
    else if (entry.isFile()) paths.push(relative(root, absolutePath).replaceAll('\\', '/'));
  }
  return paths;
};

const sha256 = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');
const failures = [];
for (const path of await walk(upstreamRoot)) {
  try {
    const [upstreamHash, localHash] = await Promise.all([
      sha256(resolve(upstreamRoot, path)),
      sha256(resolve(localRoot, path)),
    ]);
    if (upstreamHash !== localHash) failures.push(`modified: ${path}`);
  } catch {
    failures.push(`missing: ${path}`);
  }
}

if (failures.length > 0) {
  process.stderr.write(`${failures.join('\n')}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write('Upstream tree matches local snapshot.\n');
}

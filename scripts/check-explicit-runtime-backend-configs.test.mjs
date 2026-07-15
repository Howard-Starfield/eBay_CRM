import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..',
);

const checks = [
  [
    'server unit-test environment',
    'packages/twenty-server/.env.test',
    /^RUNTIME_BACKEND=redis$/gm,
    1,
  ],
  [
    'server end-to-end environment',
    'packages/twenty-server/.env.e2e-testing-server',
    /^RUNTIME_BACKEND=redis$/gm,
    1,
  ],
  [
    'server example environment',
    'packages/twenty-server/.env.example',
    /^RUNTIME_BACKEND=redis$/gm,
    1,
  ],
  [
    'Docker example environment',
    'packages/twenty-docker/.env.example',
    /^RUNTIME_BACKEND=redis$/gm,
    1,
  ],
  [
    'Docker Compose server and worker',
    'packages/twenty-docker/docker-compose.yml',
    /^\s+RUNTIME_BACKEND:\s*redis\s*$/gm,
    2,
  ],
  [
    'Docker runtime images',
    'packages/twenty-docker/twenty/Dockerfile',
    /^(?:ENV\s+|\s+)RUNTIME_BACKEND=redis(?:\s+\\)?$/gm,
    2,
  ],
  [
    'Podman Compose server and worker',
    'packages/twenty-docker/podman/podman-compose.yml',
    /^\s+RUNTIME_BACKEND:\s*["']?redis["']?\s*$/gm,
    2,
  ],
  [
    'Podman manual server and worker',
    'packages/twenty-docker/podman/manual-steps-to-deploy-twenty-on-podman',
    /^\s+-e RUNTIME_BACKEND=redis \\$/gm,
    2,
  ],
  [
    'Kubernetes server',
    'packages/twenty-docker/k8s/manifests/deployment-server.yaml',
    /^\s+- name:\s*["']?RUNTIME_BACKEND["']?\s*$\r?\n^\s+value:\s*["']?redis["']?\s*$/gm,
    1,
  ],
  [
    'Kubernetes worker',
    'packages/twenty-docker/k8s/manifests/deployment-worker.yaml',
    /^\s+- name:\s*["']?RUNTIME_BACKEND["']?\s*$\r?\n^\s+value:\s*["']?redis["']?\s*$/gm,
    1,
  ],
  [
    'Terraform server',
    'packages/twenty-docker/k8s/terraform/deployment-server.tf',
    /^\s+name\s*=\s*"RUNTIME_BACKEND"\s*$\r?\n^\s+value\s*=\s*"redis"\s*$/gm,
    1,
  ],
  [
    'Terraform worker',
    'packages/twenty-docker/k8s/terraform/deployment-worker.tf',
    /^\s+name\s*=\s*"RUNTIME_BACKEND"\s*$\r?\n^\s+value\s*=\s*"redis"\s*$/gm,
    1,
  ],
  [
    'Helm server',
    'packages/twenty-docker/helm/twenty/templates/deployment-server.yaml',
    /^\s+- name:\s*RUNTIME_BACKEND\s*$\r?\n^\s+value:\s*["']?redis["']?\s*$/gm,
    1,
  ],
  [
    'Helm worker',
    'packages/twenty-docker/helm/twenty/templates/deployment-worker.yaml',
    /^\s+- name:\s*RUNTIME_BACKEND\s*$\r?\n^\s+value:\s*["']?redis["']?\s*$/gm,
    1,
  ],
];

for (const [name, relativePath, pattern, expectedCount] of checks) {
  test(`${name} explicitly selects the Redis runtime`, async () => {
    const source = await readFile(
      path.join(repositoryRoot, relativePath),
      'utf8',
    );
    const matches = source.match(pattern) ?? [];

    assert.equal(
      matches.length,
      expectedCount,
      `${relativePath} must contain ${expectedCount} explicit RUNTIME_BACKEND=redis selection(s)`,
    );
  });
}

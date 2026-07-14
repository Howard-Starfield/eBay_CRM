# Upstream Maintenance

Prefer stable tagged Twenty releases over tracking `main`.

Use this update sequence for every upstream refresh:

1. Fetch the candidate stable release from `https://github.com/twentyhq/twenty.git` and check it out in a detached worktree.
2. Read the candidate release notes and identify changes that may affect the fork boundary.
3. Run `node scripts/verify-upstream-pin.mjs` against the detached worktree before applying fork patches.
4. Merge the candidate release into the fork; do not copy individual upstream files.
5. Run both runtime contract jobs.
6. Run the Twenty server unit and integration test suites.
7. Review the fork-boundary diff and confirm that fork-owned changes remain intentional and minimal.
8. Update `.twenty-upstream.json` only after all preceding checks pass.

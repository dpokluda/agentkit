---
name: worktree-management
description: Inspect worktree ownership and manage explicitly authorized workspaces CLI leases, preserving candidates and external or app-native checkouts.
---

# Worktree management

Make the current worktrees understandable before allocating another checkout.
Use the `workspaces` CLI for its own inventory and managed slots, and supported host tools for native session metadata.
This skill is not a worker runner, a sandbox, or permission to create branches or remove work.
Installing the skill does not install the .NET tool, its runtime, or Git.
Use the [workspaces README](../../tools/workspaces/README.md) for prerequisites, packaging, output fields, and error codes.
If the executable is unavailable, report that limitation rather than installing it or pretending manager leases are available.

## Inventory first

Resolve the intended registered repository and inspect its current inventory before acquiring a slot.
For an explicitly authorized registration, choose the clone, a separate pool root, and a job-appropriate capacity.
The following PowerShell examples use illustrative paths and capacity, not defaults:

```powershell
workspaces repo add example --path 'C:\src\example' --pool-root 'C:\agent-worktrees\example' --max-slots 3 --json
workspaces list --repo example --json
workspaces inspect '<returned-workspace-id>' --json
```

Report actual workspace IDs, full paths, branch or detached state, HEAD, local changes, leases, and checkpoint references where known.
Separate observed Git/filesystem state from catalog metadata and worker claims.
Unknown or inaccessible state is not evidence that a checkout is clean or idle.
Use `--json` for automation and check both the process exit and the versioned response's `ok`, `error`, and `warnings`.
Keep the same state root across commands; its default is `%LOCALAPPDATA%\AgentKit\workspaces`, with an explicit `--state-root` override.

Distinguish the manager's own slots from the primary checkout and external worktrees.
App-native and manually created worktrees remain owned by their original lifecycle manager.
A familiar folder prefix or matching remote URL does not prove ownership.
Join session/task metadata only when supported host tools verify the same path.
If that bridge is unavailable, disclose the missing app metadata and activity coverage; do not read an undocumented app database.

Inventory does not authorize setup, builds, dependency installation, network access, or changes in a checkout.
Cached remote refs are local observations, not proof of current server state.

## Acquire only for an approved job

Establish the approved base, new task branch, task and owner identity, request identity, and checkpoint destination before allocation.
Use one acquisition request for that job.
Record the actual returned workspace ID, path, lease ID, base ref/commit, and request identity in the authorized handoff.
Announce those values rather than an intended path or invented session ID.

```powershell
workspaces acquire --repo example --base origin/main --branch firstmate/task-017 `
    --task T017 --owner '<actual-owner-id>' --request-id '<recorded-request-id>' --json
```

Replace the example task/base/branch with the approved job values and record the request ID before the first attempt.
An optional `--session` value is caller-reported metadata; use a real returned host session ID, not a proposed label.
Acquisition uses an existing local ref or commit without fetching and creates a fresh slot and new task branch.

On interruption, inspect existing state and retry only the same request with the same parameters.
Do not generate a new request ID to bypass an uncertain result or a full pool.
Durable leases do not expire merely because a process ended or a timestamp is old.
Stop and report refusal or needs-attention state rather than forcing a branch, evicting another slot, or resetting content.

Allocation and execution are different operations.
No external worker runner is supplied, and native child-session creation does not automatically use a manager-allocated checkout.
Do not allocate a slot for a native worker that will actually run elsewhere.
Use only a supported, authorized execution route and record its real working directory and session identity.
Keep one active writer per checkout.

## Handoff and retain

Before releasing a lease, establish that the job's writer has stopped and use `session-checkpoint` to verify the authorized handoff.
Record the actual candidate and verification evidence, including missing or stale coverage.
Use the returned workspace and lease IDs for release, not a directory-name guess or another job's lease.

Release records the outcome and checkpoint reference and leaves the checkout retained.
For example, after a verified review-ready handoff:

```powershell
workspaces release '<returned-workspace-id>' --lease '<returned-lease-id>' `
    --outcome candidate-ready --checkpoint '<verified-checkpoint-path>' --json
```

Use `blocked`, `failed`, or `stopped` instead when that is the observed outcome; candidate-ready is not human acceptance.
The recorded outcome and checkpoint path are not an independent verification result.
Release does not reset or clean files, delete a branch, publish work, or make the slot available for reuse.
Retained slots still count toward capacity, and cleanup, recycling, and removal are not implemented.
Preserve retained work and report exhausted capacity rather than inventing a cleanup command.
Use `workspaces doctor --repo example --json` for diagnostics, not automatic repair or lease release.
For native or other external checkouts, use the owning system only under separate authorization; never delete their folders through this CLI.

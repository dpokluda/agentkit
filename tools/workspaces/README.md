# Workspaces

`workspaces` inventories the Git worktrees of explicitly registered clones and allocates fresh, bounded worktrees for local tasks.
Leases are durable, and release preserves the candidate rather than preparing it for reuse.
This is a Windows/local-disk first implementation of increments A and B in `specs\agentic-workflow-tools.md`.
Non-Windows execution is explicitly unsupported; there is no untested alternate path-resolution implementation.
It is not a worker runner or a sandbox.

Cleanup, recycling and removal are deferred.
There are no working `cleanup`, `recycle` or `remove` commands.
Retained candidates, failed creations and active leases continue to occupy capacity.
The tool never evicts work, resets files, deletes branches, kills workers, fetches, commits or publishes.

## Build and run

The source project is `tools\workspaces\Workspaces.csproj`.
It requires the .NET 10 SDK to build and installed Git on `PATH`.
The .NET tool package is `AgentKit.Workspaces`, version `0.1.0`, with executable name `workspaces`.
The packaged tool requires the .NET 10 runtime.

From the AgentKit checkout:

```powershell
dotnet build .\tools\workspaces\Workspaces.csproj
dotnet .\tools\workspaces\bin\Debug\net10.0\Workspaces.dll --help
dotnet test .\tests\Workspaces.Tests\Workspaces.Tests.csproj
```

The following creates a package in an explicitly chosen artifact directory, not a live installation:

```powershell
$packageRoot = Join-Path ([IO.Path]::GetTempPath()) 'agentkit-workspaces-packages'
dotnet pack .\tools\workspaces\Workspaces.csproj --configuration Release --output $packageRoot
```

If installation is separately authorized, use an isolated tool directory first:

```powershell
$toolRoot = Join-Path ([IO.Path]::GetTempPath()) 'agentkit-workspaces-tools'
dotnet tool install AgentKit.Workspaces --version 0.1.0 --add-source $packageRoot --tool-path $toolRoot
& (Join-Path $toolRoot 'workspaces.exe') --help
```

Neither building nor packing installs the tool or changes an agent profile.
No daemon, setup script, app database integration or automatic firstmate worker is installed.

## Commands

All commands accept `--state-root <directory>` and `--json`.
Without `--json`, output is human-readable.
Help and version requests use the normal command-line help/version text.

```text
workspaces repo add <name> --path <existing-clone-or-worktree> --pool-root <directory> --max-slots <positive-int>
workspaces list [--repo <name>]
workspaces inspect <workspace-id>
workspaces acquire --repo <name> --base <local-ref-or-commit> --branch <new-branch> --task <task-id> --owner <owner-id> --request-id <id> [--session <session-id>]
workspaces release <workspace-id> --lease <lease-id> --outcome <candidate-ready|blocked|failed|stopped> [--checkpoint <path>]
workspaces doctor [--repo <name>]
```

Registration admits a trusted existing, non-bare clone.
The pool must be absent or empty and separate from its source checkouts, Git administration, other registered pools and the state root.
It does not clone a repository or adopt existing folders.
An identical repeated registration succeeds; conflicting names, clone bindings or settings are refused.
The explicit `--max-slots` is the total capacity, not a limit only on running jobs.
There is no implicit universal capacity and no configuration-update command.

The physical Git common directory identifies a clone.
Aliases and Windows junctions are resolved before registration.
Two clones of one remote remain distinct repositories.
All Git-reported worktrees are inventoried, not only tool-created slots.
A `copilot` prefix does not establish app ownership.
External worktrees are report-only; their actual manager, task and session remain unknown unless there is recorded caller-supplied metadata.

Acquisition creates a new, explicitly named branch from the exact locally resolved base commit.
It never fetches or silently substitutes another base.
Existing branches, sparse-checkout sources and submodule bases are refused in this increment.
Registration is a trust boundary: Git checkout may execute the repository's configured hooks or filters.
Task, owner and optional session values are labels supplied by the caller, not verified host identities.

Use one request ID for one logical acquisition, and keep every parameter unchanged when retrying.
The request is scoped to the registered physical clone.
The same request returns the original workspace and immutable lease, including after release.
Changing base text, branch, task, owner or session is an `idempotency_conflict`, even if two base refs currently resolve to the same commit.
A moving base ref does not change the commit recorded for an existing request.

Release requires the exact lease and records a caller-reported outcome.
It moves `leased` to `retained` without modifying tracked, untracked or ignored content, terminating writers or publishing.
The caller must stop its writer before handoff.
An identical release retry succeeds; a stale lease or an attempt to rewrite an ended handoff is refused.
`candidate-ready` is not a test verdict or human acceptance.
An optional checkpoint is only a path reference; missing/inaccessible references produce warnings and are not reported as persisted evidence.

## A disposable example

Use an existing trusted test clone, explicit temporary state and an empty pool:

```powershell
$state = Join-Path ([IO.Path]::GetTempPath()) 'agentkit-example-state'
$pool = Join-Path ([IO.Path]::GetTempPath()) 'agentkit-example-pool'
workspaces repo add example --path 'C:\src\example' --pool-root $pool --max-slots 2 --state-root $state --json
workspaces list --repo example --state-root $state --json
$allocation = workspaces acquire --repo example --base main --branch task/T017 --task T017 --owner local-job --request-id job-T017 --state-root $state --json | ConvertFrom-Json
workspaces inspect $allocation.data.workspaceId --state-root $state --json
workspaces release $allocation.data.workspaceId --lease $allocation.data.leaseId --outcome candidate-ready --state-root $state --json
workspaces doctor --repo example --state-root $state --json
```

The example does not run a worker.
After release the slot remains on disk and consumes capacity; no cleanup operation is implied.
Keep the returned workspace ID, path, request ID and lease with the job's authorized handoff.
Do not store runtime state or private checkpoints in the public AgentKit source tree.

## State and interrupted operations

The default state root is `%LOCALAPPDATA%\AgentKit\workspaces`.
`--state-root` selects a separate root for tests or an explicitly managed local environment.
Use the same root for cooperating callers.
State is versioned JSON: a registration index, one catalog per physical clone, and a matching ownership marker in each pool.
Slots have stable paths such as `slot-01`.
Directory naming alone never establishes ownership.

A single OS-backed file lock serializes mutations in a state root, including concurrent CLI processes.
Lock acquisition waits at most 10 seconds and then returns `state_busy`.
Each Git process is bounded to 30 seconds with noninteractive execution and structured arguments.
Inventory disables optional Git index locks, fsmonitor commands and lazy fetching; it does not run project checks or setup scripts.

State files are replaced atomically after flushing their contents.
Creation intent is persisted before Git changes a ref or checkout.
If creation is interrupted, its recorded slot is uncertain and continues to count against capacity.
The tool never guesses that files were not created, automatically frees a lease because of age/PID absence, or removes partial work.
Missing, corrupt or newer-version state, changed clone/pool identity, uncataloged pool content and absent cataloged worktrees require attention.
Re-registering a nonempty pool is not a recovery shortcut.
Keep the state root and pool ownership marker together.

`doctor` is read-only and reports these mismatches.
It does not repair state, expire leases, reset a candidate or touch app lifecycle records.
Reconciliation of interrupted operations is manual in this release.
Leases coordinate cooperating callers; they do not prevent unrelated filesystem writers.
Local JSON is trusted user-owned state, not a tamper-proof authorization system.

## JSON and exit contract

JSON stdout contains exactly one envelope for a command result, including parse errors:

```json
{
  "ok": true,
  "data": {
    "workspaceId": "ws-...",
    "path": "C:\\agent-worktrees\\example\\slot-01",
    "leaseId": "lease-...",
    "state": "leased",
    "baseRef": "main",
    "baseCommit": "<exact-local-commit>",
    "replayed": false
  },
  "error": null,
  "warnings": [],
  "schemaVersion": 1
}
```

The example abbreviates acquisition data.
Other returned fields include repo/request IDs, task, owner, caller-reported session, branch, timestamps, outcome and checkpoint reference.
Error and warning objects have `code`, `message` and `severity`.
Errors and diagnostics also go to stderr; they are never mixed into JSON stdout.
An error can contain partial inventory data.
Check `ok` and the process exit code rather than treating nonempty data as success.

`list` returns `data.repositories`, `data.workspaces`, repository-level `data.diagnostics` and an observation timestamp.
`inspect` returns one workspace observation.
`doctor` returns the inventory with the same diagnostic rules and a nonzero result for incomplete/uncertain state.
Workspace observations include stable ID, full path, managed/external/unknown ownership, manager, primary flag, branch/detached state, HEAD,
task/owner/session/lease/request metadata, exact allocation base, tracked/untracked/ignored porcelain entries, sparse mode, Git lock and in-progress operations,
checkpoint metadata, blockers, diagnostics and observation time.
Unknown scalar observations are `null` or explicitly `"unknown"`, never a false clean/idle assertion.
Inventory branch values are Git's full refs such as `refs/heads/main`; acquisition data echoes the requested literal new branch name.
Ignored directories and nested repositories are not recursively inventoried, and no disk-size estimate is claimed.
Host session/automation and process-activity coverage remain explicitly unavailable.

| Exit | Meaning |
| --- | --- |
| `0` | Command completed; warnings may still describe coverage or a missing checkpoint. |
| `2` | Invalid syntax/input, unknown repository/workspace, invalid branch or unresolved base. |
| `3` | Refused mutation: occupied capacity, ownership/conflict, stale lease, unsupported mode or lock contention. |
| `4` | Git/IO failure, cancellation, incomplete observation or state requiring attention. |

Implemented refusal/error codes include `pool_exhausted`, `idempotency_conflict`, `registration_conflict`, `branch_exists`, `stale_lease`,
`external_owner`, `unsupported_repository_mode`, `invalid_arguments`, `invalid_branch`, `invalid_ref`, `invalid_outcome`,
`repo_not_found`, `workspace_not_found`, `state_busy`, `cannot_verify`, `state_needs_attention`, `unsupported_platform` and `operation_cancelled`.
`active_lease` and `dirty_workspace` are inventory blockers, not evidence that release is destructive or forbidden.
`stale_plan` is not emitted because cleanup planning/application is not implemented.
Callers should tolerate additional diagnostic codes and fields within schema version 1.

## Focused verification

The xUnit project uses real disposable local Git repositories, explicit temporary catalogs and pools.
Tests require Windows, installed Git, the .NET 10 SDK and PowerShell 7 for the real junction fixture.
It does not use the public checkout as a test worktree, call a model, access the app database or contact a remote server.
The suite exercises inventory and mutation contracts rather than mocking Git success, including cross-process allocation/idempotency,
actual lock/Git time bounds, killed and failed creation, corrupt/missing/newer state, inaccessible observations and content retention.
Cross-platform, network-filesystem, process-runner and real host/harness integration are not claimed.

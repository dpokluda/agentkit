# Manual checkpoint and verification helpers

These PowerShell 7 helpers persist one current authored note and run one explicitly approved local check.
They do not discover tasks, launch workers, trigger checkpoints, install anything, or authorize publication.
Use them only with a trusted checkout and the job's approved paths, command and side effects.
The tested platform is Windows with installed Git and PowerShell 7.

## Save and inspect a checkpoint

Create an authorized private parent outside all source checkouts first.
There is no default destination, and a missing destination or parent is an error.
Do not put runtime notes, private identifiers or command evidence in this public repository, even behind an ignore rule.

```powershell
$note = Get-Content -LiteralPath $authoredNotePath -Raw
$saved = & .\tools\handoff\Save-Checkpoint.ps1 `
    -Path 'C:\private-job\current.json' -Owner 'job-017/session-a' `
    -Worktree 'C:\agent-worktrees\example' -Note $note

$status = & .\tools\handoff\Get-CheckpointStatus.ps1 `
    -Path $saved.path -Owner 'job-017/session-a' -Worktree 'C:\agent-worktrees\example'
$status.note

# After reading the existing note, deliberately update this exact version.
$updated = & .\tools\handoff\Save-Checkpoint.ps1 `
    -Path $saved.path -Owner 'job-017/session-a' -Worktree 'C:\agent-worktrees\example' `
    -Note $revisedAuthoredNote -ExpectedSha256 $status.sha256
```

`Path`, `Owner`, `Worktree` and `Note` are required for saving.
The note must contain meaningful authored content; its full text and whitespace are preserved in the JSON `note` field.
The helper does not generate missing scope, decisions, verification or next actions.
Follow the existing `session-checkpoint` skill when authoring that content.
`Worktree` must be an existing directory.
Status requires `Path`, expected `Owner` and expected `Worktree`; it inspects only that path.
Missing required values fail without an interactive prompt.

The versioned checkpoint JSON records `schemaVersion: 1`, `owner`, `worktree`, `savedAtUtc`, `note`, `noteSha256` and `candidate`.
A save uses an exclusive cooperating-writer lock at `<Path>.lock`, a temporary sibling and atomic replacement, then verifies readback.
The empty lock file remains beside the checkpoint; it is not another checkpoint or a retained snapshot.
A failed replacement preserves the previous file and surfaces the failure.
If readback fails after replacement, persistence is explicitly unverified rather than reported as saved.

Updating requires the same exact owner, the same worktree and `ExpectedSha256` from the previously inspected save/status response.
Another owner's checkpoint, a changed file, a user-edited note or an unrelated existing document is refused.
There is no force option or automatic reconciliation; preserve manual edits and use a new authorized path when needed.
The lock coordinates these helpers, not arbitrary editors; stop other writers before updating.
Owner identifiers are caller assertions, not authenticated session identities.
Destination paths and their parents must not be links or junctions, so a path alias cannot redirect private writes into a checkout.

Save and status return a PowerShell object with `schemaVersion`, `path`, `state`, `persisted`, `observedAtUtc`,
`expectedOwner`, `expectedWorktree`, `owner`, `worktree`, `lastVerifiedUpdateUtc`, `sha256`, `note`,
`candidate`, `currentCandidate` and `reasons`.
Pipe it to `ConvertTo-Json -Depth 12` when JSON is needed.
`lastVerifiedUpdateUtc` is the saved capture timestamp of the verified checkpoint record, not a test-pass timestamp.
It is a recorded claim from the tool, not independent proof against later tampering.

`current` means the valid saved record matches the expected owner/location and its covered candidate still matches.
`missing` means no record exists at the requested path.
`stale` means the covered candidate changed.
`unknown` means a record or candidate cannot be read, validated or compared; reasons are returned instead of empty success data.
`persisted` is `true` for an observed existing record, `false` for absence and `null` when existence could not be established.
A persisted but invalid or stale record is not a safe resume point without reconciliation.
Status does not execute checks, change Git, scan other jobs or inspect host session databases.

## Run one approved local check

The executable and argument array are executable code requiring job approval.
The helper cannot enforce that approval or stop an approved command from using credentials, networking or changing files.
No checks, setup hooks, package managers or shell wrappers are discovered or invoked automatically.
Avoid secrets in arguments or output; exact arguments and raw logs are retained in the approved private directory.

```powershell
$result = & .\tools\handoff\Invoke-LocalVerification.ps1 `
    -Worktree 'C:\agent-worktrees\example' `
    -Executable 'C:\Program Files\PowerShell\7\pwsh.exe' `
    -ArgumentList @('-NoProfile', '-NonInteractive', '-File', '.\tests\Project.Tests.ps1') `
    -EvidenceDirectory 'C:\private-job\check-001'
```

`Worktree`, an absolute native `Executable` path and a new explicit `EvidenceDirectory` are required.
On Windows the executable must be an `.exe` or `.com`; other script types require their explicitly approved interpreter.
`ArgumentList` is a string array, defaulting to an empty array; spaces, quotes, brackets and empty arguments are passed without shell concatenation.
Pass PowerShell flags such as `-NoProfile` explicitly when PowerShell is the approved executable.
Scripts and shell built-ins require their explicitly approved interpreter as the executable.
The evidence directory's parent must already exist outside source checkouts; an existing evidence directory is never reused.
Preflight failures execute nothing and throw without pretending an evidence record was saved.

The command runs in the specified worktree and its raw stdout and stderr are drained concurrently into separate `stdout.log` and `stderr.log` files.
`verification.json` is written and read back after completion or a caught launch/capture error.
The returned object is the same record with `schemaVersion: 1`, `worktree`, `executable`, `argumentList`,
`attemptedAtUtc`, `startedAtUtc`, `completedAtUtc`, `exitCode`, `outcome`, `candidateBefore`, `candidateAfter`,
`freshness`, both log paths and SHA256 hashes, `evidencePath`, `error` and `reasons`.
All timestamps are UTC.
The stored exit code is the true native exit code, or `null` if no exit was observed.

`outcome` describes the command: `passed` for exit zero, `failed` for a nonzero exit, and `blocked` for launch or capture errors.
`freshness` separately reports `current`, `stale` or `unknown` candidate coverage.
A failed command is still failed even when its candidate is current.
A zero-exit command that changes the covered candidate records `outcome: passed` and `freshness: stale`, then throws.
It does not verify the resulting candidate.
Unknown Git/candidate data is recorded honestly; a zero exit with `freshness: unknown` is not candidate-ready evidence.

The helper emits its record and then throws for failed checks, blocked execution/capture or stale results.
With `pwsh -File`, these failures produce a nonzero helper exit; the child's exact code remains in `verification.json`.
Callers must honor terminating errors, `outcome`, `freshness` and coverage limitations, not infer success from an output object or log's existence.
An evidence-write/readback error is also a terminating failure; raw logs may remain for manual inspection.
There is no timeout, background-runner lifecycle or interruption recovery.
Killing the helper or host can leave incomplete evidence and a still-running command; reconcile actual processes before retrying.

## Candidate coverage and limits

The shared candidate helper uses read-only local Git commands with optional index locking and filesystem monitors disabled.
Inherited `GIT_*` overrides are removed from those observation commands so another repository or index cannot redirect candidate coverage.
It records the direct worktree path, physical Git common-directory path, HEAD, branch, index hash, content counts, observation time and a SHA256 fingerprint.
The fingerprint includes the index entries and direct file hashes for every tracked and non-ignored untracked path.
Adding, changing or removing untracked bytes invalidates coverage even when Git's short status text would look identical.
Different physical clones are different candidates.
Nothing fetches, refreshes the index, runs project filters/hooks, changes configuration or spends model quota.

Ignored files, timestamps, permissions, environment and external dependencies are explicitly outside the fingerprint.
If a check depends on those inputs, document and inspect them separately; `current` is limited to the published fingerprint coverage.
Submodules, sparse checkouts, unmerged entries, linked content and nested repositories are unsupported and yield `unknown`, not a clean claim.
A missing Git executable, unborn HEAD or unreadable content also yields `unknown` with its reason while preserving a useful authored note.
Snapshots are not an atomic filesystem view and do not establish that no other writer is active.
Stop other writers before capture or verification and recheck the candidate after any later edit.
Verification evidence is a local observation, not human acceptance, independent review, backup or deployment proof.

## Focused regression harness

```powershell
pwsh -NoProfile -NonInteractive -File .\tests\Handoff.Tests.ps1
```

The self-contained harness downloads no packages and creates only disposable synthetic Git repositories and explicit temporary private destinations.
It exercises save/readback and real write failure, ownership/version/user-edit protection, missing/stale/unknown states,
tracked/index/untracked changes, raw output from real passing/failing processes, literal argument and path handling, and no unexpected source/Git mutation.
Fresh-process fingerprint stability, equivalent Windows path casing, relative paths, trailing separators, native failure exits,
large concurrent stdout/stderr streams, junction redirection and unsupported candidate layouts are included.
Temporary fixtures are removed without traversing linked targets.

---
name: session-checkpoint
description: Save a compact, evidence-backed checkpoint at an authorized work boundary or before reset, preserving decisions, verification, blockers, and resume pointers.
---

# Session checkpoint

Preserve the minimum current state a fresh session needs to continue safely.
Use at a meaningful work boundary, consequential decision, blocker, or before ending/resetting an implementation or investigation session.
Do not turn ordinary questions into checkpoint work or keep a transcript-shaped journal.

Follow the installed instructions and the current task's authorization.
This skill captures execution state; it does not change engineering policy, approve work, or grant execution permissions.
Local handoff helpers are optional explicit actions, not automatic capture hooks or a worker runner.
Use the [handoff helper README](../../tools/handoff/README.md) for their full contract and coverage limits.
Installing this skill does not install or run the helpers' dependencies.

## Choose a safe destination

Use an explicitly designated checkpoint path first, then an established authorized project handoff convention.
A host-provided persistent session-artifact area may be used when the task permits private session artifacts.
Reuse a destination already established for the job rather than asking again at every checkpoint.

Do not invent a global memory file, create an ignore rule, or write to a primary checkout the host has declared read-only.
Do not edit GLOBAL, OPINIONS, VOICE, project instructions, or approved acceptance criteria as part of checkpointing.
If no authorized persistent destination is available, return an inline checkpoint labelled "not persisted" and ask once for a destination when needed.
Never claim the session is safe to reset merely because an inline summary was produced.

Keep private material within the approved project/provider boundary.
Exclude credentials, secrets, sensitive personal information, raw transcripts, and unnecessary source or telemetry dumps.
Record a safe reference instead of copying sensitive evidence.
If a command contains a secret, do not persist it verbatim; name the redaction and the missing reproducibility detail.

## Inspect, then update

1. Read the existing checkpoint and the relevant current artifacts.
   Establish the project, isolated worktree, current task, and actual implementation state from available evidence.
2. Reconcile planned work with observed changes and saved task state.
   Keep completed work, uncertain claims, remaining work, and human acceptance distinct.
   Do not overwrite another worker's checkpoint or infer ownership from an old path.
3. Capture consequential decisions, their reasons, and rejected approaches that would otherwise be repeated.
   Preserve a pointer to an authoritative spec, plan, test, or configuration instead of duplicating it.
4. Record verification that actually ran, including working directory, command, outcome, evidence location, and relevant limitations.
   Distinguish direct observation from a worker-reported result and old evidence from a current check.
   Identify the actual candidate covered, including uncommitted work where relevant; a branch name or unchanged HEAD alone is insufficient.
   Use a helper's returned candidate/evidence basis when available, without presenting an old result as a current check.
   Do not execute side-effecting commands solely to make the checkpoint look complete.
5. Replace stale current-state entries deliberately while preserving useful rationale.
   Keep one compact operating map, not successive full-session summaries or an automatic archive scheme.
6. Write only to the authorized destination and read it back to confirm persistence.
   Surface write failures and uncaptured state explicitly; do not substitute an old checkpoint for a successful current save.

## Checkpoint shape

Adapt the existing document's structure rather than forcing a second format into it.
When a new checkpoint is authorized, the following fields are sufficient:

```text
# Current checkpoint
Status: in-progress | candidate-ready | blocked | failed | stopped
Observed at: <timestamp>

## Scope and authority
Project, mode, approved spec/task IDs, non-goals, allowed operations, limits.
Use references to the job brief and standing instructions where sufficient.

## Actual state
Session/worktree, branch and diff/revision basis where available.
Relevant changed files, completed work, remaining work, and ownership.
Record only identifiers the host actually returned; disclose missing metadata.

## Decisions
Accepted decisions, their reasons, and useful rejected alternatives.
Identify proposed decisions and requirement changes that still need approval.

## Verification
Working directory, actual command, observed/reported outcome, evidence reference.
Checks not run, environment limitations, stale evidence, and remaining risks.

## Next action
One bounded next task, blockers, required user decisions, and active worker state.

## Instruction proposals
Review coverage and gaps, or "not reviewed".
Pointers to instruction-review findings with evidence, scope/owner, minimal change,
and approval status; use "none identified" when reviewed without justified proposals.
Proposals are not applied policy.

## Resume pointers
Exact artifacts and session/worktree references needed to resume.
```

An active task or partial experiment is not a completed feature.
When a worker is still running, capture that fact and avoid instructing another writer to resume concurrently.

## Use the local helpers

Resolve the scripts from the stable AgentKit source checkout and run them in PowerShell 7, not relative to an assumed worker path.
The examples use `$agentKitRoot` for that checkout and job-approved `$checkpointPath`, `$owner`, `$worktree`, and `$authoredNote` values.
Use private destinations under existing parents outside source checkouts; no destination or owner is inferred.
The complete authored resume note belongs in the JSON checkpoint's `note`, not just a status label.

```powershell
$saved = & "$agentKitRoot\tools\handoff\Save-Checkpoint.ps1" `
    -Path $checkpointPath -Owner $owner -Worktree $worktree -Note $authoredNote

$status = & "$agentKitRoot\tools\handoff\Get-CheckpointStatus.ps1" `
    -Path $checkpointPath -Owner $owner -Worktree $worktree
```

Keep the returned `sha256`.
After reading and reconciling the existing note, update with the same owner/worktree and `-ExpectedSha256 $saved.sha256`.
An ownership or file-hash conflict is a refusal to resolve, not permission to overwrite or replace the saved hash blindly.
Save and status return PowerShell objects; status inspects only that explicit path and does not discover other checkpoints or verify every linked artifact.
Report `current`, `missing`, `stale`, or `unknown` with the returned `reasons`, candidate basis, and `lastVerifiedUpdateUtc`.
`persisted` reports observed existence, not a valid or safe resume point; `lastVerifiedUpdateUtc` is the capture timestamp, not a test-pass time.
An old unchanged note can still be current for its observed candidate; a recent save does not prove that all verification is current.

For one explicitly approved local check, use its absolute native executable path and argument array:

```powershell
$verification = & "$agentKitRoot\tools\handoff\Invoke-LocalVerification.ps1" `
    -Worktree $worktree -Executable $approvedExecutable -ArgumentList $approvedArguments `
    -EvidenceDirectory $newEvidenceDirectory
```

`$newEvidenceDirectory` must be an explicitly authorized new directory with an existing parent outside source checkouts.
After successful preflight, the helper writes `verification.json`, `stdout.log`, and `stderr.log`, including captured failed or blocked execution.
Preflight failures execute nothing and do not produce a verification record.
A failed check, failed launch, or zero-exit check that changes the covered candidate throws; retain the evidence and report the failure or stale basis.
Inspect `outcome` and `freshness` separately, and reference the evidence in the authored note.
The candidate fingerprint covers Git HEAD/index and tracked plus non-ignored untracked file bytes, not ignored content or external dependencies.
Unsupported or uncertain content is unknown, and concurrent writers remain a coverage gap; the helper is not an isolation mechanism.
Do not run another check, install prerequisites, or change commands without the job's authority.
There is no timeout or worker supervision; after interruption, reconcile the actual process state before retrying.

## Finish

Name the saved checkpoint and give a short resume pointer containing only real, reachable artifacts.
State the unfinished action or blocker and any information that could not be persisted.
If a referenced artifact belongs to another worktree or needs special access, say so.
If nothing was written, say "not persisted" rather than implying durable capture.

Check coverage against the actual authorized checkpoint and evidence locations, not the number of saves.
Report missing, stale, unavailable, or unverified records explicitly.
Saving a current resume note does not make earlier verification current or turn a failed check into a pass.
Keep required handoff artifacts outside a checkout that may be removed, or disclose that they will not survive its removal.

A fresh session must recheck current workspace and worker state before acting.
The checkpoint is evidence-backed context, not permission to repeat commands or proof that the repository has not changed.

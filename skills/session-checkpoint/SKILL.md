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

## Learning candidates
Pointers to concise proposals, or "none identified".
No automatic global or project-policy changes.

## Resume pointers
Exact artifacts and session/worktree references needed to resume.
```

An active task or partial experiment is not a completed feature.
When a worker is still running, capture that fact and avoid instructing another writer to resume concurrently.

## Finish

Name the saved checkpoint and give a short resume pointer containing only real, reachable artifacts.
State the unfinished action or blocker and any information that could not be persisted.
If a referenced artifact belongs to another worktree or needs special access, say so.
If nothing was written, say "not persisted" rather than implying durable capture.

A fresh session must recheck current workspace and worker state before acting.
The checkpoint is evidence-backed context, not permission to repeat commands or proof that the repository has not changed.

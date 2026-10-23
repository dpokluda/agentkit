# Agentic Workflow Tools

Implementation input specification

Date: 22 October 2026

Status: Design input for a new implementation session. The tools described here have not been implemented.

## 1. Purpose and decision status

Build a small set of local tools that make agent-assisted development easier to delegate, inspect, resume, and clean up.
Keep GitHub Spec Kit as the source of feature intent and tasks, and keep the developer responsible for acceptance and publication.
The tools should help firstmate coordinate useful work without relying on one long conversation or repeated reminders.

This is an umbrella specification, not a request to implement every capability in one change.
It records the agreed goals and boundaries, the most developed interface proposal, and the decisions that still need an implementation plan.
Requirements described as **required** are desired behavior.
**Proposed** interfaces and technology choices are the baseline from the discussion, not existing commands or tested integrations.
**Open** decisions must be resolved before implementing the affected behavior.

The first implementation should address workspace visibility.
Pooling and cleanup follow, then one bounded worker-execution path with checkpoints and verification evidence.
Do not begin by building a general orchestration platform.

## 2. Problems to solve

Conversation-only work leaves requirements, rejected alternatives, and implementation progress scattered across sessions.
An exported plan describes intended work but does not reliably describe the current code or the checks already performed.
Compaction and session restarts can therefore cause repeated explanations, reopened decisions, and duplicated work.

Separate worker checkouts provide isolation but are difficult to understand from randomly generated directory names.
A developer needs to know which task owns a checkout, where the useful result is, whether another process is using it, and whether its contents are preserved elsewhere.
A clean Git status does not prove that a worktree is disposable.

The developer also wants to delegate approved implementation or experiments while away.
The result should be an understandable design and reviewable code, accompanied by observed validation and explicit gaps.
It must not depend on unsupervised publishing, unlimited iteration, or the agent granting itself additional permissions.

Repeated corrections should lead to considered improvements in instructions or mechanical checks.
They should not require a "remember this" request every time, nor produce an ever-growing collection of duplicate rules.

## 3. Existing foundation

AgentKit already contains the following authoring sources:

| Existing artifact | Role | Important limitation |
| --- | --- | --- |
| `instructions\GLOBAL.md` | Working relationship, scope, and authorization | Written guidance is not a permission-enforcement mechanism. |
| `instructions\OPINIONS.md` | Engineering preferences, including simple PowerShell scripts | Prefer the smallest correct solution, not an arbitrary line count or absence of safeguards. |
| `instructions\VOICE.md` | Writing style | Technical documentation should stay direct and readable. |
| `agents\firstmate.agent.md` | Coordinator profile | Currently relies on host-native session tools and model-followed instructions. |
| `skills\session-checkpoint\SKILL.md` | Procedure for saving a current handoff note | No coded persistence trigger, universal destination, or checkpoint inventory exists. |
| `skills\instruction-review\SKILL.md` | Evidence-backed proposals for instruction improvements | No continuous transcript-mining service or automatic policy editing exists. |
| Public/private installation scripts | Expose selected authoring content through local profile links | Installation is separate from job execution and must not be redesigned as part of these tools. |

The public repository contains reusable definitions and code.
Private agents and work-specific material remain in a separate private repository.
Runtime state, transcripts, checkpoints, credentials, and job evidence do not belong in the public repository's `memory` directory.

There is no workspace CLI or worker runner in the existing codebase.
Do not infer executable capabilities from the firstmate profile.
The repository was not initialized with Spec Kit when this document was prepared; inspect the current checkout rather than assume scaffolding exists.

## 4. Toolset and build boundaries

These are five capabilities, not a requirement for five executables.

| Capability | What to build | Interface and technology status |
| --- | --- | --- |
| Workspace management | Inventory, tool-owned pooling, leases, retention, and guarded cleanup | Most developed proposal: `workspaces` CLI, C#/.NET 10, System.CommandLine, installed Git. |
| Worker execution | Launch and observe one approved coding-agent job in its allocated checkout | Separate runner; exact executable name and CLI are open. Reuse C# where useful rather than introduce another stack without a reason. |
| Checkpoint support | Persist current handoffs and report coverage/freshness | Extend the current skill and job records. A separate service is not required. |
| Instruction improvement | Capture evidence and propose scoped, reviewed changes | Extend `instruction-review` and the handoff workflow first. A transcript collector or new executable needs a demonstrated requirement. |
| Local verification | Execute agreed project checks and produce trustworthy evidence | Use existing project commands; connect their results to the runner. Do not build a replacement CI system. |

An initial Windows/local-disk implementation is the proposed platform scope.
Keep paths and state handling portable, but do not claim macOS/Linux support until those platforms are exercised.
The initial scope is one user on one execution host, not a shared network pool or distributed worker fleet.

Use PowerShell for simple entry points and glue when standard cmdlets suffice.
The proposed C# choice is for the stateful lifecycle tool, where leases, concurrency, and interrupted operations justify it.
Do not turn small scripts into frameworks to imitate the C# component.

## 5. Shared operating rules

### 5.1. Authority and ownership

An existing spec is not permission to implement it.
Firstmate may select only explicitly eligible tasks or a task allowlist supplied for the job.
The job must identify required behavior, non-goals, allowed implementation decisions, validation, and stopping conditions.
Unresolved product, security, or shared-environment decisions remain with the developer.

Normal jobs do not authorize staging, commits, pushes, PR creation, merges, provider changes, or modifications to the primary checkout.
Workspace acquisition may create the explicitly requested new task branch in its managed checkout.
That does not authorize switching or resetting branches in other checkouts.
Publication and integration require separate authorization.

Each checkout has exactly one lifecycle owner.
The workspace manager owns only its own slots.
The app, Treehouse, and manual checkouts must not be reset, recycled, or removed through an unrelated manager.
Filename prefixes and a matching remote URL do not establish ownership.

### 5.2. Facts, claims, and acceptance

Separate observed Git/process/filesystem evidence from agent-reported outcomes.
`candidate-ready` means ready for human review, not human acceptance or deployed correctness.
Record verification against the exact candidate it covers.
A later code change makes at least part of the earlier evidence stale and may require renewed review.

Missing access or inspection errors are not evidence that a resource is clean, idle, or absent.
Report unknown values and the scope of observation.
Do not disguise unavailable checks as passes.

### 5.3. Preservation

Preserve useful results whether committed or uncommitted.
Do not automatically reset, discard, delete, or recycle a checkout when a worker finishes or fails.
Do not infer permission to abandon work from the word "experiment."
Do not automatically kill unrelated processes or delete branches.

An app-managed archive can remove its worktree, so a checkpoint stored only there is not automatically durable after archival.
Before cleanup, preserve required evidence outside the removal target or establish that losing it is explicitly acceptable.

### 5.4. Execution and privacy

Local code execution is different from local model inference.
Use only an approved provider and data route, and do not change providers automatically.
A worktree is not a sandbox for credentials, processes, network calls, or cloud resources.
Even Git checkout can invoke repository hooks or filters; trusted-repository admission still matters.

Inventory and ordinary status must not run project setup/build scripts, install dependencies, refresh credentials, or make network calls.
Running project checks belongs to an approved job.
Keep private material in its approved local/project boundary.
Redaction is not a guarantee that arbitrary transcripts are safe to publish.

## 6. Workspace-management CLI

### 6.1. Public interface proposal

Working executable name: `workspaces`.
The package name is not settled.
The following commands are the proposed public contract, not commands available today.
Paths and task identifiers are neutral examples.

```text
workspaces repo add example --path "C:\src\example" --pool-root "C:\agent-worktrees\example" --max-slots 3

workspaces list --repo example --json
workspaces inspect <workspace-id> --json

workspaces acquire --repo example --base origin/main --branch firstmate/task-017
                   --task T017 --owner <owner-id> --request-id <request-id> --json

workspaces release <workspace-id> --lease <lease-id>
                   --outcome candidate-ready --checkpoint "<checkpoint-path>" --json

workspaces cleanup plan --repo example --action recycle --target origin/main --json
workspaces cleanup plan --repo example --action remove --target origin/main --json
workspaces cleanup apply <plan-id> --yes --json

workspaces doctor --repo example --json
```

Wrapped commands above are presented for readability, not as shell-specific continuation syntax.
Human-readable output is the default; `--json` is the stable interface for skills and automation.
Planning should also support selection of one workspace rather than require a repository-wide operation.
Exact option validation and short aliases can be finalized in the implementation plan without expanding the feature scope.

### 6.2. Registration and identity

`repo add` registers an existing Git clone and a separate pool root.
It does not clone a repository, create an app project, change shared Git settings, or initialize an untrusted project.
Record the physical clone identity based on its Git common directory, resolving path aliases deliberately.
Two clones of the same remote are different owners of their worktrees and must not share slots accidentally.

Keep slot paths stable, for example `C:\agent-worktrees\example\slot-01`.
Use task labels and owner/session metadata for human readability rather than renaming the directory on every acquisition.
The maximum slot count includes leased, retained, and needs-attention slots, not just currently running workers.
The example limit of three is illustrative, not an approved universal default.

### 6.3. Inventory

Required inventory fields include repository and workspace IDs, full path, manager/ownership classification, primary-checkout flag, branch or detached state, and HEAD.
Include purpose/task and session references when known.
Report tracked modifications, untracked content, ignored-content observations, lease state, checkpoint reference, and observation timestamps.
Explain retention/reuse/cleanup blockers rather than emit only a green/red badge.

Git determines which worktrees exist.
The tool's catalog supplies only the metadata it owns.
For app/manual worktrees, an unavailable task or session name remains unknown.
Do not label a path app-owned solely because it starts with `copilot`.

The skill may join verified paths with session metadata from the host's supported tools.
A standalone CLI must remain useful without that bridge and must disclose the missing session/automation coverage.
Do not read or edit an undocumented app database as a required integration.

Disk usage is useful but optional, and any reported size must identify its coverage and uncertainty.
Do not let expensive recursive size scans block a basic inventory.

### 6.4. Pool lifecycle and leases

The normal lifecycle is:

```text
available -> leased -> retained
retained -> available       only through approved recycling
retained -> removed         only through approved removal
interrupted/uncertain operation -> needs-attention
```

A catalog label alone does not prove that a slot is currently reusable.
Recheck ownership, contents, and relevant activity before acquisition.
Create a fresh slot when capacity allows and no existing slot is safely available.
When capacity is occupied by work that must be retained, fail with `pool_exhausted` rather than evict it.

Each acquisition has an immutable lease ID.
A lease survives worker or coordinator process exit until explicitly released.
Do not expire leases merely because a timestamp is old or a PID is absent.
Release with a stale lease ID must fail without affecting a newer acquisition.

Acquisition also accepts an idempotency request ID.
Retrying the same request with the same parameters must not allocate another workspace.
Reusing that request ID for different parameters is a conflict.
If an interrupted acquisition cannot be safely reconciled, report needs-attention instead of guessing it failed before creating files.

Resolve the requested base to an exact commit and record both ref and commit.
Do not silently use another base when the requested ref is missing.
The initial proposed path creates an explicitly named new task branch; never force an existing branch or check it out in two places.
Detached acquisitions and intentional reuse of an existing task branch are open follow-up choices, not required initial features.

No implicit fetch occurs during acquisition.
A cached `origin/main` is local evidence, not proof of the current server state.
Refresh only through a separately explicit authorized network step and report which evidence was used.

`release` records outcome and checkpoint metadata and ends that acquisition's ownership.
It moves the checkout to retained, not available.
It must not reset/clean content, terminate processes, delete refs, or publish anything.
The coordinator/runner must stop its writer before reporting handoff; known continuing writers must remain visible and block recycling.

### 6.5. Cleanup planning and application

Plan and apply are separate commands.
Planning must not change any checkout.
It may persist the plan in the tool's own state so it can be reviewed and applied later.

A plan identifies each exact checkout, repository identity, manager, lease generation/state, HEAD and branch, and chosen preservation/integration target.
Include relevant content and activity observations and explain every blocked item.
Apply rechecks the plan's preconditions under the operation lock.
Changed identity, ownership, relevant refs, or contents invalidate the plan rather than expand its scope.
The confirmation flag is not a bypass for these checks and does not itself prove human approval.

Recycling and removal are different actions.
Recycling prepares an owned retained slot for a subsequent job.
Removal uses the owning manager's normal worktree-removal operation.
Do not recursively delete arbitrary paths, repository roots, or the home directory.
Do not include a generic destructive `--force` escape hatch in the first release.

Tracked modifications, untracked work, unresolved leases, in-progress Git operations, and relevant running workers block cleanup.
Inspect ignored files separately: ignored does not mean disposable.
Only explicitly approved cache paths may be treated as reusable/removable cache.
Unknown ignored data blocks automatic recycling/removal.
Do not copy secrets or machine configuration into a new slot as an implicit setup feature.

Commit preservation must refer to the intended target, not "contained in any remote branch."
Pushed-for-review and accepted/merged are different states.
An ancestry check cannot prove that a squash merge or manual reimplementation preserved a change.
Unproven preservation stays retained; designing an explicit reconciliation policy is follow-up work.
Keep branch refs by default even when an owned checkout is recycled or removed.

If a decision requires current remote evidence and that evidence is unavailable, do not call cached refs current.
Remote reachability should not prevent offline inspection, but it may prevent a remote-dependent cleanup decision.

An external/app checkout is report-only for this CLI.
The skill can direct the developer to the owning app/session lifecycle, but absence of a supported action is not permission to delete its folder.

### 6.6. State, concurrency, and recovery

Use a small versioned local catalog outside the mutable worker checkout.
The proposed starting mechanism is one JSON state file per physical repository, an OS-backed lock, and atomic state-file replacement.
Do not add a database, daemon, event bus, or generic storage abstraction without a concrete need.
Allow tests to select a separate state root.
The exact production state-root location is an open packaging decision; it must not default inside the public source tree.

Serialize allocation and lifecycle mutations for the same repository.
Record an in-progress operation before modifying Git/filesystem state, then verify its completion before advertising availability.
Filesystem and Git updates are not one atomic transaction; partial failure must be represented.
Corrupt, missing-with-existing-slots, or unsupported-version state must not become an empty reusable pool.
`doctor` diagnoses mismatches and interrupted operations without automatically freeing leases, resetting files, or repairing app state.

Use the installed Git executable rather than reimplementing worktree internals.
Use documented machine-readable output such as `git worktree list --porcelain -z` and a porcelain status format.
Handle NUL delimiters, paths with spaces/brackets, errors, and exit codes explicitly.
Use structured process arguments rather than concatenate shell command strings.
Never write `.git` administrative files by hand.

Leases coordinate cooperating callers; they are not filesystem access controls.
Git's worktree lock is not a general writer lock.
PID plus process start time is useful evidence, but Windows process observation is incomplete and PID absence alone does not prove safety.
Report pertinent observation gaps and do not add automatic process killing as a substitute.

### 6.7. Responses and errors

Provide a versioned JSON response shape with stable IDs, error codes, and explicit unknowns/warnings.
Keep JSON on stdout and diagnostics on stderr.
JSON/noninteractive execution must not wait indefinitely for an unanswered prompt.
Publish exit-code semantics, including a nonzero status for refused or failed mutations.

The following is an illustrative acquire response, not a finalized JSON Schema:

```json
{
  "schemaVersion": 1,
  "ok": true,
  "data": {
    "workspaceId": "ws-example-01",
    "path": "C:\\agent-worktrees\\example\\slot-01",
    "leaseId": "lease-example",
    "owner": "owner-example",
    "task": "T017",
    "baseRef": "origin/main",
    "baseCommit": "<resolved-commit-id>",
    "state": "leased"
  },
  "warnings": []
}
```

At minimum, distinguish `pool_exhausted`, `dirty_workspace`, `active_lease`, `stale_lease`, `stale_plan`, `external_owner`, `cannot_verify`, and `state_needs_attention`.
Do not reduce all of them to "not found" or return success-shaped empty results.
Do not report `candidate-ready` metadata as an independent test verdict.

### 6.8. Proposed technology and packaging

Use C#/.NET 10 and System.CommandLine for the stateful CLI.
Use the installed Git executable through `ProcessStartInfo.ArgumentList`, redirected output, explicit exit-code handling, and cancellation/timeouts.
Package as a .NET tool initially.
Self-contained binaries may be added later; each target OS/architecture needs its own supported build.
Native AOT is an optional optimization, not an initial requirement.

Start with one small console project and tests.
Keep the relevant domain logic testable without building a framework or splitting it into many libraries.
Use an existing repository test convention if one has been added; otherwise propose a normal .NET test project before scaffolding.
Do not choose a Git library, plugin model, or database just because it might be useful later.

## 7. Firstmate worker runner

### 7.1. Scope and host boundary

Build an execution path that launches one approved job in its allocated checkout and records enough state to observe and resume it.
The initial proposed worker is an installed, approved coding-agent CLI such as Copilot CLI.
Do not assume its flags, permissions, headless behavior, or session-resume protocol; verify the supported version during implementation.

Firstmate can remain in the app's main-checkout coordinator session.
The external runner starts the worker with the leased checkout as its fixed working directory.
The app's native child-session creation does not automatically allocate from this tool's pool, and external CLI workers do not automatically appear as app-native children.
Visibility must be explicit.

The current firstmate profile must eventually be updated to select this backend when it exists.
Until then it remains a profile using available native session tools, not evidence that an external runner works.
Never create a second external worker merely because native session status is unavailable.

### 7.2. Job brief

The job brief must contain or reference the following:

- Registered repository, approved task IDs, spec/plan/task paths, and implementation or experiment mode.
- Required behavior, acceptance criteria, non-goals, and decisions delegated to the worker.
- Baseline/base ref and permitted file/workspace scope.
- Approved validation commands and their understood network/external side effects.
- Approved harness/provider route and permission profile, without embedding credentials.
- Stopping conditions, enforceable limits, and any advisory limits the host cannot actually enforce.
- Checkpoint/evidence destination and the expected human handoff.

Choose only from an approved eligible-task list with satisfied dependencies.
Do not scrape every spec as an automatically authorized backlog.
Spec Kit task files remain authoritative; job records track execution, not a second competing feature tracker.

An implementation job follows an approved approach.
An experiment may explore an agreed design question while holding required behavior fixed.
Start with the simplest plausible candidate and build another only when the comparison can resolve a meaningful uncertainty.
Preserve useful rejected approaches and the reason for the recommendation.

### 7.3. Minimum execution behavior

Preflight the CLI, necessary permissions, repository trust, validation prerequisites, and evidence location before an away run.
Do not silently install tools, authenticate a different integration, enable broad permissions, switch models/providers, or approve new shared-environment access.

Record job ID, workspace/lease identity, worker process and start time, real session ID if available, logs, current outcome, and checkpoint reference.
Distinguish real identity from an invented display label.
Use notifications or actual process completion rather than model-driven sleep/poll loops.
Do not mistake an idle turn or CLI exit alone for feature acceptance.

The runner should support starting, inspecting status, stopping its own worker, and reconciling a saved job after interruption.
Stopping preserves the checkout.
A stale job record must not start another writer while the original process still owns the work.
A worker crash leaves the workspace preserved; uncertainty must not release it into reuse.

Initially, a foreground runner plus persisted state is sufficient.
Background launch may use the host's supported process mechanism, but must not be described as restart-proof scheduling.
An always-on daemon, boot service, and calendar scheduling are outside the first increment.
The execution host still needs to remain available.

Record outcomes such as running, candidate-ready, blocked, failed, and stopped separately from human acceptance.
Limits must state what is enforced.
Wall-clock/process or iteration limits may be enforceable; token/cost limits require reliable live counters and must not be claimed when unavailable.

### 7.4. Public interface and stack still to decide

No final runner executable name, command syntax, or job-brief serialization was selected in the discussion.
The implementation plan should propose a minimal interface for start/status/stop/resume, using stable job/workspace IDs and structured output.
Do not present invented commands as previously approved.

C#/.NET is a reasonable default to share the maintenance/toolchain experience with workspace management.
A small PowerShell launch adapter is also acceptable if that is all the first increment needs.
Choose based on actual required lifecycle behavior, not a goal to create a second application.
Keep model policy and worktree lifecycle out of a generic "run anything" framework.

## 8. Checkpoint persistence and coverage

Extend the existing checkpoint skill rather than replace its meaning.
A checkpoint is a current resume note with references to actual work and evidence.
It is not a code backup, a full conversation archive, or a restoration of hidden model state.

Choose the destination once per job from explicit authorization or an established permitted convention.
If no destination is available, report not persisted rather than claim it saved.
A default location was not settled; resolve it before implementing automatic capture.
The destination must respect public/private boundaries and the retention policy of its worktree/session.

The checkpoint content must identify job/scope, actual worktree/revision or diff basis, completed and remaining tasks, decisions and reasons, verification, blockers, and next action.
Preserve links to authoritative artifacts rather than duplicate them.
Record whether evidence is directly observed, worker-reported, stale, or unavailable.

Tool support should verify persistence and report current coverage by job/workstream: owner, file location, last verified update, candidate/evidence basis, and missing/stale state.
A current checkpoint may be updated repeatedly; save count and coverage are not interchangeable.
No numbered snapshot archive is required by this specification.

The runner/firstmate should request capture at a meaningful slice boundary, consequential decision, blocker, and handoff.
A model-followed instruction to do so is not a coded guarantee.
Only claim automatic event triggering after an actual supported lifecycle integration has been implemented and exercised.
Partial capture must remain visible in the handoff.

These records can be part of the runner's state and status output.
A separate checkpoint server, database, or daemon is not required.
Coordinate writing so one worker does not overwrite another's note or user edits.

## 9. Evidence-backed instruction improvement

Extend the `instruction-review` workflow to consider new corrections and accepted decisions at handoff without another reminder.
Start from current job evidence/checkpoints and available, authorized session history.
Do not add a universal transcript scraper or silently scan unrelated projects/home directories.
If history coverage is incomplete, report the boundaries.

A proposal should include the observed issue or decision, safe evidence references, scope, existing owner/destination, minimal change, and approval status.
Classify already-covered, transient, uncertain, and contradictory material rather than blindly append it.
Prefer tests, analyzers, or configuration when they can enforce the real lesson.
One explicit accepted decision may deserve immediate project capture; inferred recurring patterns need corroboration.

Feature state belongs with the feature.
Project conventions belong in the project owner.
Only genuine cross-project engineering/writing preferences belong in the corresponding global instructions.
No private incident details, identifiers, raw transcripts, or source excerpts should become global/public examples.

Analysis and saved proposals do not authorize applying policy edits.
Application requires specific approval of the change and destination and must preserve intervening edits.
Edit canonical source, not a generated copy or replacement of an installed link.
No automatic staging, commit, or publication follows.

The first version may remain a skill plus a structured handoff section.
A coded evidence/proposal tool is justified only after identifying a concrete gap in that approach.
The model/harness route must already be approved; "local-first" collection is not a guarantee of local inference.

## 10. Local validation and the review packet

Run existing project commands for the agreed candidate instead of building a new test orchestrator.
Record working directory, command, start/end or observation time, exit code, outcome, log reference, and the candidate it covers.
Separate failed, passed, not run, blocked, and stale results.
Do not claim independent verification when only a worker summary is available.

Validation commands are executable code, not passive metadata.
Running them needs job approval and an understood side-effect boundary.
Do not automatically execute arbitrary setup hooks discovered in a changed feature branch.
Do not remove a check, weaken a test, or change acceptance criteria to manufacture a pass.

The review packet must include the task/spec references, exact candidate location/basis, requirement coverage, actual checks, gaps, meaningful decisions, checkpoint coverage, and pending instruction proposals.
A separate read-only reviewer is useful when its context can expose a substantive issue.
Do not treat two models agreeing as a correctness gate.

An experiment should compare the agreed observable criteria, explain why the chosen design is simpler or more appropriate, and preserve useful code/evidence.
A failed or blocked experiment can still produce a useful honest report.
Local checks do not establish deployment behavior or correctness in every cloud environment.

## 11. Companion skill integration

A proposed `worktree-management` skill should teach the CLI contract without duplicating its lifecycle algorithms in Markdown.
It should inspect before allocating, reuse a known job's identity, acquire once, announce the returned path/lease, and record the mapping.
It should release into retained state after a verified handoff, never silently recycle the candidate.

Firstmate should clearly distinguish native app workers from externally launched workers.
Choose the supported execution path explicitly or through a preapproved job default.
Do not promise visibility in the app for external workers.
Use the owning system for cleanup.

The existing checkpoint and instruction-review skills remain independently useful.
They should refer to real returned paths and IDs and disclose unavailable tool/harness integration.
Installing a skill does not install the executable, supply credentials, or enable unattended execution.

## 12. Incremental delivery and acceptance criteria

Each increment should produce a small reviewable change and maintain the previous behavior.
Do not scaffold all future tools before demonstrating the first useful path.

| Increment | Deliverable | Acceptance evidence |
| --- | --- | --- |
| A: Inventory | Registration, list, inspect, clear ownership/unknowns, human/JSON output | Existing Git worktrees are accurately identified without repository/network mutations; app metadata gaps stay explicit. |
| B: Managed allocation | Bounded owned pool, atomic acquisition, durable leases, retained release | Concurrent callers cannot get one slot; retry does not duplicate it; uncommitted results survive release and process exit. |
| C: Cleanup | Reviewed recycle/remove plans and rechecked application | Unsafe/stale/external candidates are refused; only exact approved owned checkouts can change. |
| D: One execution path | One approved CLI worker, persisted job identity, local checks, checkpoint handoff | The real worker uses the allocated checkout; a fresh coordinator can locate the result and detect an active original worker. |
| E: Learning/reporting | Checkpoint coverage and reviewed instruction proposals | Missing/stale capture and unsupported claims remain visible; no policy changes occur without approval. |

The validation set for the affected increment must include the relevant cases below:

- Paths with spaces/brackets, relative paths, Windows case/alias/junction handling, and repositories in different physical clones of one remote.
- Tracked, untracked, ignored, nested-repository, and in-progress Git states; unsupported submodule/sparse modes must not be silently mutated.
- Concurrent acquisition, repeated request IDs, stale lease IDs, interrupted creation, interrupted cleanup, and corrupt or newer-version state.
- Checkout content or target refs changing between cleanup plan and apply; preservation that cannot be established after squash/manual integration.
- Known active writers, reused PIDs, and relevant observation/permission errors.
- Missing CLI/toolchain, denied execution permissions, worker failure, graceful stop, and interruption/resume without a duplicate writer.
- Actual checkpoint write failure, an inaccessible/stale reference, unchanged or user-edited instructions, and duplicate/no-op learning proposals.
- JSON parseability, documented exit behavior, stdout/stderr separation, no-write inventory, and no surprise network or publication operations.

Use small disposable Git repositories and explicit temporary state/pool roots.
Verify actual filesystem and command behavior, not only mocked invocations.
Real-agent smoke checks require an approved provider and explicit bounded execution; ordinary unit/integration tests should not spend model quota.
An unavailable privilege/platform/harness case is skipped or blocked, not passed.
Do not install dependencies until needed by changed manifests or an actual missing-dependency failure.

## 13. Non-goals for the first release

No GUI, canvas extension, MCP server, always-on daemon, remote execution fleet, automatic PR lifecycle, provider routing, or general-purpose scheduler.
No automatic commits or publication.
No broad destructive cleanup override, automatic time-based lease expiry, secret/configuration seeding, or process killing of unrelated work.
No private-source indexing inside the public code repository.
No replacement for Spec Kit, project tests, Git, or an approved backup system.

A CLI is the initial user and agent interface.
A canvas or MCP integration can later consume that stable interface if a real usage problem justifies it.

## 14. Decisions to resolve before the affected implementation

Resolve these in the implementation plan, not by presenting the proposal as already working:

| Decision | Proposed baseline or constraint |
| --- | --- |
| Product/package names and source layout | `workspaces` is the workspace CLI working name; no runner name is settled. Keep tools under AgentKit's existing `tools` convention where appropriate. |
| Local state location and distribution | User-local state outside source checkouts; versioned JSON/OS lock initially; .NET tool package for the workspace CLI. |
| Cross-platform support | Windows/local disk first; other platforms need their own validation. |
| App metadata bridge | Use supported host tools when available; standalone output must tolerate unknown session metadata. No private database editing. |
| Branch/detached acquisition options | Explicit new task branch/base is the initial proposed path; no forced branch reuse. |
| Remote freshness and squash/manual reconciliation | Offline inventory is valid; remote-dependent cleanup requires adequate explicit evidence. Unproven cases remain retained. |
| Cache paths and retention | No automatic ignored-file disposal; only deliberately approved caches are treated as cache. |
| Runner CLI, permissions, limits, and resume | Verify the actual harness contract; implement one bounded path before generalizing. |
| Checkpoint destination and hooks | Choose per-job authorization/convention first; actual event support is not assumed. |
| Learning automation packaging | Start with existing skill/handoff evidence; do not build a service without a concrete gap. |

Do not ask the developer to repeat settled goals, safety boundaries, or the tool inventory.
Ask a focused question only when an unresolved choice materially changes the selected increment.

## 15. Starting a new implementation session

Read this document, the repository README, `instructions\GLOBAL.md`, `instructions\OPINIONS.md`, `instructions\VOICE.md`, and the three existing firstmate/checkpoint/review definitions.
Inspect current repository state and preserve unrelated user changes.
The developer currently prefers work in the existing primary branch; this document does not authorize creating a new development worktree, switching branches, commits, or pushes.
Future worker isolation and fixture creation are separate operations requiring the appropriate task authority.

Start by planning increment A, the read-only workspace inventory, unless the developer selects another increment.
If adopting Spec Kit for this repository, use this umbrella brief to produce feature-scoped specifications/plans/tasks rather than implement the whole document as one feature.
Keep the selected scope, unknowns, acceptance checks, and permission requirements visible.

Example session request:

> Read `specs\agentic-workflow-tools.md` and the existing AgentKit instructions.
> Plan increment A only: workspace registration, inventory, and inspection.
> Identify any blocking decisions, then propose the smallest implementation and its acceptance checks before coding.
> Do not implement pooling, cleanup mutations, worker execution, or changes to the installed environment yet.

The artifact remains an input specification.
No tool, third-party package, live profile, job, or external integration was installed or executed by authoring it.

## 16. References and related local definitions

Related repository files:

- [Global instructions](../instructions/GLOBAL.md)
- [Engineering opinions](../instructions/OPINIONS.md)
- [Writing voice](../instructions/VOICE.md)
- [Firstmate profile](../agents/firstmate.agent.md)
- [Checkpoint skill](../skills/session-checkpoint/SKILL.md)
- [Instruction-review skill](../skills/instruction-review/SKILL.md)

Public background and prior art:

- [GitHub Spec Kit](https://github.com/github/spec-kit)
- [Treehouse](https://github.com/kunchenguid/treehouse)
- [Firstmate](https://github.com/kunchenguid/firstmate)
- [gnhf](https://github.com/kunchenguid/gnhf)
- [Backpass](https://github.com/kunchenguid/backpass)
- [Standalone stow skill](https://github.com/kunchenguid/firstmate/blob/main/skills/stow/SKILL.md)
- [no-mistakes](https://github.com/kunchenguid/no-mistakes)
- [Git worktree contract](https://git-scm.com/docs/git-worktree)
- [System.CommandLine](https://learn.microsoft.com/dotnet/standard/commandline/)
- [.NET process argument handling](https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.argumentlist)

These are sources of ideas, not installed dependencies or endorsements of their permission/publication defaults.
The earlier investigation examined Treehouse at commit `42b88127e55def0dd5d371296eacd287dafbf1ca`; verify current behavior before reusing code or interfaces.

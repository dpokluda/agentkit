# AgentKit

A personal collection of tools, agents, skills, and instructions to support agentic development workflows.

## Structure

- **`agents/`**: Custom agents with specialized roles and responsibilities.
  Profiles are consolidated in `agents/*.agent.md`; both runtimes link to this same collection.
- **`instructions/`**: Reusable instructions, guidelines, and conventions for AI agents.
  `GLOBAL.md` covers behavior and safety, `OPINIONS.md` covers engineering practices, and `VOICE.md` covers writing preferences.
- **`memory/`**: Reserved for sanitized examples, templates, or documentation, not live memory, transcripts, or checkpoints.
  Runtime capture stays in an authorized private project or persistent session-artifact area outside this public repository.
- **`skills/`**: Reusable capabilities and workflows that agents can invoke.
  Each skill directory includes its `SKILL.md` and any helper scripts.
- **`scripts/`**: Local environment setup, starting with `Install-AgentKit.ps1`.
- **`templates/`**: Starter repository instructions, including `AGENTS.md`.
- **`tests/`**: PowerShell installer/handoff regression tests and .NET workspace tests.
- **`tools/`**: The `workspaces` CLI and explicit checkpoint/local-verification helpers, separate from profile installation.

## Usage

The components are intended to be used independently or combined as needed, depending on the agent, environment, and workflow.

This public set includes the `firstmate` and `principal-strategy-reviewer` agent profiles.
The shared skills are `csharp-stylefix`, `csharp-xmldoc`, `instruction-review`, `session-checkpoint`, `session-id`, `strategy-review`,
`technical-whitepaper`, `teams-thread-extract`, and `worktree-management`.
The session-ID and Teams skills include their local helper scripts.

The current `study-book-author` family is deliberately excluded as a unit, including its agent profiles, skill, and reference workflows.
It needs a separate public-only extraction rather than shipping references to private-context dependencies.
The current `ado-pr-review` and `investigate-icm` agents are also excluded.
Work-specific content belongs in a separately reviewed private overlay, not in this repository or behind a public `.gitignore`.
The public installer does not create or compose private content.

## Install into an isolated profile first

Use **PowerShell 7** and a stable source checkout.
`scripts/Install-AgentKit.ps1` defaults `-ContentRoot` to its parent AgentKit directory, not the caller's working directory.
An explicit relative `-ContentRoot` is resolved against the caller before the script navigates to `-HomeRoot`.
The name describes its purpose: configuring local agent profiles with links to the authoring source.
It does not configure models, MCP registrations, credentials, or tool permissions, or install .NET tools and their dependencies.

From this checkout, preview and optionally exercise a temporary profile:

```powershell
$testHome = Join-Path ([IO.Path]::GetTempPath()) ("agentkit-preview-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testHome | Out-Null
pwsh -NoProfile -File .\scripts\Install-AgentKit.ps1 -HomeRoot $testHome -WhatIf
pwsh -NoProfile -File .\scripts\Install-AgentKit.ps1 -HomeRoot $testHome
```

Every example above targets a temporary home, not the live profile.
If `-HomeRoot` is omitted, the installer targets `$HOME`; do that only after a separately approved live cutover.
Keep the host-recognized `.copilot` and `.claude` directory names.
`-HomeRoot` must already exist.
The installer first creates `HomeRoot/agentkit` as a directory symbolic link to `ContentRoot`.
All runtime links point through that alias.
Do not install live links from a review worktree that may later be removed.

### Installed links

| Link location under `HomeRoot` | Points to |
| --- | --- |
| `agentkit` | `ContentRoot` |
| `.copilot/agents` | `HomeRoot/agentkit/agents` |
| `.claude/agents` | `HomeRoot/agentkit/agents` |
| `.copilot/skills` | `HomeRoot/agentkit/skills` |
| `.claude/skills` | `HomeRoot/agentkit/skills` |
| `.copilot/copilot-instructions.md` | `HomeRoot/agentkit/instructions/GLOBAL.md` |
| `.copilot/OPINIONS.md` | `HomeRoot/agentkit/instructions/OPINIONS.md` |
| `.copilot/VOICE.md` | `HomeRoot/agentkit/instructions/VOICE.md` |
| `.claude/CLAUDE.md` | `HomeRoot/agentkit/instructions/GLOBAL.md` |
| `.claude/OPINIONS.md` | `HomeRoot/agentkit/instructions/OPINIONS.md` |
| `.claude/VOICE.md` | `HomeRoot/agentkit/instructions/VOICE.md` |

Agent and skill collections are links to the entire selected authoring directories.
New entries appear through both profiles without rerunning installation, subject to the host's discovery cache.
Definitions written through those collection links belong to the selected content checkout; they are not automatically committed.
The installer does not translate or filter agent formats, so verify discovery in the chosen host.
The project template is not installed globally: copy `templates/AGENTS.md` into a new repository and complete its project-specific sections.

### Content selection

The installer accepts `-ContentRoot`, `-HomeRoot`, and the ordinary `-WhatIf` and `-Confirm` options.
The selected content root must contain `agents/`, `skills/`, and an `instructions/` directory with `GLOBAL.md`, `OPINIONS.md`, and `VOICE.md`.
Keep private content outside this public repository and review it separately.
Any private composition is separate from this installer, which only links the selected root.

### Conflicts and failures

Preflight checks required content and every intended link location before changing anything.
Any occupied link location stops the script, including a regular file, directory, already-correct link, or broken link.
Linked or non-directory profile parents are also rejected rather than traversed.
Existing items are never overwritten, moved, or automatically replaced.

`-WhatIf` runs preflight and previews the links without creating files or profile folders.
A declined confirmation or link-creation error stops the script with an error.
Earlier links can remain after a later runtime failure because there is deliberately no rollback or automatic cleanup.
Inspect and reconcile them manually before another run; rerunning does not skip existing links.
The installer does not silently fall back to copies or emit a successful completion status after an error.

Windows symbolic-link creation may require Developer Mode or an approved elevated process.
The installer does not change those settings or elevate itself.
Do not run concurrent profile changes during installation; preflight is not a filesystem transaction.

### Pending legacy cutover

An existing setup may link the entire `.copilot/agents`, `.copilot/skills`, `.claude/agents`, or `.claude/skills` directory through `~/ai-setup`.
The new installer **stops at those occupied link locations before any changes**.
It does not follow them to create entries inside the legacy authoring source.
It does not automatically unlink, move, or replace them.

Before any later live cutover, inventory the old entries, preserve private and independently installed components, and approve a separate migration plan.
Do not drop private or independently installed entries when reconciling an existing setup.
Do not simply retarget `~/ai-setup`: repository instructions now live under `instructions/`, and the installed instruction paths have changed.
Publishing these files does not install them or change an existing home alias or live profiles.
Private composition and live cutover require separate review.

## Discovery and dependencies

These locations target Copilot CLI and supported Copilot Agent Host harnesses, and Claude Code.
VS Code's local agent may use a different user profile location; verify discovery in the chosen harness.
Start a new session or refresh customization discovery after installing.
In Copilot CLI, use `/instructions`; in VS Code, inspect Agent Customizations and response references or debug logs.
In Claude Code, use the corresponding built-in memory, rule, agent, and skill discovery commands.

`GLOBAL.md` describes when to consult the engineering and writing preferences.
Those references are not a universal file-import syntax.
Root-level `OPINIONS.md` and `VOICE.md` are consulted through GLOBAL's directions, not installed as automatically loaded modular rules.
Description-only frontmatter does not provide universal task-conditional loading.
Separating the files improves maintenance but does not, by itself, reduce context use.

Firstmate uses the host's supported native project and isolated-session tools for workers.
Workspace allocation is separate from worker execution; a leased checkout does not make a native app worker run there.
No external worker runner or automatic checkpoint hook is supplied.
The session-ID skill uses the current host's session tools and reports unavailable data rather than inventing it.
Teams extraction requires a separately configured, authorized WorkIQ connector; its renderer uses Python 3.10+ and the standard library.
The public procedures and helpers contain no actual conversations or credentials.
Installing the files does not install their tool dependencies or grant connector access.

## Supervised local workflow

Firstmate keeps Spec Kit as the requirements, design, and task authority while returning a review-ready candidate in a preserved local worktree.
Select it through the host's agent selection mechanism and provide a bounded job brief:

```text
Project: <configured project>
Mode: implementation | experiment
Eligible work: <approved spec paths and task IDs>
Acceptance and non-goals: <observable behavior and boundaries>
Local validation: <approved commands, prerequisites, and external side effects>
Limits: <stopping conditions and agreed resource limits; identify enforceable caps>
Checkpoint: <approved private path or persistent session-artifact convention>
Deliverable: a review-ready worktree, with evidence and an honest continuation point
```

The companion `worktree-management` skill inventories worktrees, distinguishes lifecycle ownership, and preserves allocated candidates.
The `session-checkpoint` and `instruction-review` skills preserve authorized execution state and propose evidence-backed improvements.
Use real returned paths and IDs, report missing or stale evidence, and keep proposed instruction edits separate from approved policy.
These skills do not create a second task tracker or automatically change standing policy.
An existing spec is not authorization to implement it, and an experiment is not acceptance of its design.
Rehearse discovery, local validation, and actual host permission boundaries before an unattended run.
These definitions do not sandbox tools, enforce spending limits, schedule jobs, install an orchestration stack, or authorize publication.
This is the local coordinator profile, not an installation of Kun's full Firstmate distro.

## Local workflow tools

The [workspaces tool](tools/workspaces/README.md) targets Windows/local disks and uses .NET 10, System.CommandLine, and installed Git.
It registers existing clones, inventories worktrees, allocates bounded fresh slots with durable leases, and releases candidates into retained state.
Retained slots still consume capacity; recycling, removal, and an external worker runner are not implemented.
State defaults to `%LOCALAPPDATA%\AgentKit\workspaces`, outside source checkouts, with an explicit `--state-root` override.
The standalone CLI reports unknown app/session metadata rather than inferring it from worktree names.

From this checkout with the .NET 10 SDK, build, test, pack, or inspect source-command help:

```powershell
dotnet build .\tools\workspaces\Workspaces.csproj
dotnet test .\tests\Workspaces.Tests\Workspaces.Tests.csproj
dotnet pack .\tools\workspaces\Workspaces.csproj --configuration Release
dotnet run --project .\tools\workspaces\Workspaces.csproj -- --help
```

These source commands may restore NuGet dependencies.
The workspace tests additionally require PowerShell 7.
Packing creates `AgentKit.Workspaces`; it does not install the tool.
Use the tool README for installation, CLI/JSON examples, exit codes, and limitations.

The [handoff helpers](tools/handoff/README.md) require PowerShell 7 and explicit, authorized private destinations.
`Save-Checkpoint.ps1` preserves the authored resume note and observed candidate; `Get-CheckpointStatus.ps1` checks that one named record.
`Invoke-LocalVerification.ps1` runs one approved native command and records its candidate basis, exit code, and separate output logs.
Use a new evidence directory with an existing parent outside source checkouts.
Missing or stale evidence remains visible; these helpers do not schedule capture, discover all checkpoints, or apply instruction proposals.
The `session-checkpoint` skill shows the handoff workflow without replacing Spec Kit task state.

## Validate the installer

```powershell
pwsh -NoProfile -File .\tests\Install-AgentKit.Tests.ps1
```

The self-contained harness needs PowerShell 7 and no downloaded test packages.
It uses short synthetic content roots and temporary homes.
It never uses the real home as an installation destination.
Coverage includes missing content, occupied locations, no-write previews, alias-first/root-level mapping calls, and caller-directory restoration.
Real symbolic-link cases require filesystem permission and are explicitly skipped when it is unavailable.
Those cases cover actual link paths, rerun rejection, broken links, and partial links remaining after a later failure.
Cleanup unlinks test links without traversing their targets.
Runtime discovery in Copilot or Claude remains a separate attended check; filesystem tests do not prove a harness loaded the content.

## License

See [LICENSE](LICENSE) for details.
# Specify helpers

[GitHub Spec Kit](https://github.com/github/spec-kit) provides structured workflows for developing software with an AI coding agent.
Its spec-driven development workflow turns a feature description into a specification, an implementation plan, tasks, and working code.
The `specify` command is Spec Kit's command-line tool for setting up and maintaining that workflow.

Spec Kit is not an AI model or a replacement for a coding agent.
An agent such as GitHub Copilot or Claude Code reads the instructions and project artifacts and performs the work.
Spec Kit is development tooling, not a dependency of the application being built.

## CLI installation versus repository setup

The CLI is installed once for the current user, using uv in these helpers.
It can manage multiple repositories.
Updating that installation changes the available CLI and bundled assets, but does not refresh files already installed into repositories.

Each initialized repository has its own Spec Kit setup.
Shared scripts, templates, and the project constitution live under `.specify/`.
Agent-specific instructions are installed separately; for example, Copilot's default integration uses `.github/skills/`.
Authored feature specifications, plans, and task lists normally live under `specs/`.

A repository update refreshes its managed integration files and shared assets using the installed CLI.
It does not rewrite the authored specifications, plans, or constitution.
Review the resulting diff before committing, especially when allowing replacement of modified managed files.

## These helpers

- [install-specify.ps1](install-specify.ps1) installs the latest stable CLI from its official GitHub release.
  It checks prerequisites and refuses to replace an existing installation.
- [check-specify.ps1](check-specify.ps1) reports repository versions and managed-file status with `-Repo`, or checks for a newer CLI with `-Global`.
- [update-specify.ps1](update-specify.ps1) refreshes repository assets with `-Repo`, or upgrades the CLI with `-Global`.
  Select both to upgrade the CLI first and then refresh the repository.

Check and update default to `-Repo`.
Run repository operations from the target repository root, not from this helper folder.
The scripts are standalone and do not initialize repositories, manage Git branches, or commit changes.
Optional Spec Kit extensions and presets are outside their update scope.

See the [main README's setup and usage guidance](../../README.md#spec-kit-helpers) for prerequisites, command examples, and overwrite controls.
These helpers are separate from `scripts/Install-AgentKit.ps1`, which configures local agent-profile links.

## Using Spec Kit for a feature

Establish project principles with `/speckit-constitution`, then use `/speckit-specify`, `/speckit-plan`, and `/speckit-tasks` to define the work.
Use `/speckit-implement` to carry out the tasks and `/speckit-converge` to assess remaining gaps.
These are Copilot skill invocations in the agent's chat, not PowerShell commands.
Other supported agents can use different invocation syntax.

For the full workflow and supported integrations, see the [official documentation](https://github.github.io/spec-kit/).

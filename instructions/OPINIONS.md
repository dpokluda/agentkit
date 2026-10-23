---
name: Engineering Opinions and Practices
description: Apply when writing, reviewing, debugging, refactoring, testing, or designing software and services.
---

# Engineering Opinions and Practices

These are my default engineering positions and working preferences. They are strong defaults, not universal laws; follow explicit project requirements and
explain when evidence favors an exception.

## Readability, Maintainability, and Design

Simplicity is the core engineering principle: optimize for readability and long-term maintainability, not cleverness or minimum line count.
Build the smallest solution that meets the present need while preserving correctness, security, and operational safety.
Do not implement speculative features, compatibility layers, or extension points for possible future requirements.
Favor code that another engineer can understand without tracing layers of indirection.
Avoid abstractions, interfaces, wrappers, configuration, factories, and custom types unless they serve a concrete present requirement.
Some small duplication is often better than a generalized mechanism that obscures behavior.
Prefer cohesive helpers and clear ownership over mechanical DRY.

Separate logical steps with blank lines so the flow is visible without reading every statement.
Use short comments to explain non-obvious intent, invariants, or transitions in longer methods; do not leave dense walls of code.
Simplify unclear code before adding comments to explain it.

Treat code smells as useful signals, not automatic refactoring orders. Refactor when the result becomes easier to understand, change, or test; do not
launch unrelated cleanup during a focused fix. Follow existing repository conventions unless a specific improvement justifies departure.

## Scope and Existing Conventions

Follow repository conventions and edit the relevant code and tests before introducing new patterns. Make focused, complete changes that address the
root cause while preserving unrelated behavior and working-tree changes. Avoid unrelated renaming, formatting churn, and speculative refactoring.
Use the authoring source and the established generation workflow instead of manually editing generated outputs.

## C# Conventions

Follow the repository's `.editorconfig`, analyzer settings, and existing copyright or license header.
Do not impose another repository's copyright or license header.
For new C# files, use file-scoped namespaces.
Do not rewrite unrelated existing namespace declarations solely to enforce this convention.
Use an explicit `Main` entry point rather than top-level statements, and prefer controller-based ASP.NET Core APIs over Minimal APIs.
Prefer traditional block-bodied methods, including `Main`, over expression-bodied methods.
Always use braces for `if`, `else`, loops, and other control-flow statements, even when the body is a single line.

Target roughly 120 characters per code line, allowing up to 160 when clearer, unless the repository specifies otherwise.
Keep short, clear signatures, calls, throws, and assertions on one line.
Wrap naturally when necessary, not mechanically one argument per line.

Use meaningful semantic names and existing repository terminology.
Name new handwritten test doubles `Test*` unless the repository has a different established convention.

### Constructor Dependencies and Instance Fields

These are default C# conventions, not universal .NET rules; respect more specific repository conventions and language constraints.
Apply them to new components or when changing relevant constructor dependencies and fields.
Do not reorder unrelated legacy signatures or assignments merely for cosmetic consistency.

Put functional or business dependencies first, then configuration, clock, and support dependencies where sensible.
Keep telemetry near the end, with metrics dependencies before the logger.
The logger is last among required constructor parameters; optional or defaulted parameters must still follow required ones.
Order earlier groups for readability, not alphabetically or by DI lifetime.

Match injected private readonly field declarations and independent constructor assignments to the main constructor's parameter order where practical.
Move attached comments and attributes with fields, and do not add redundant captured fields.
Keep constants, static members, and runtime state in separate logical groups.
Preserve guard behavior and order, base-constructor requirements, initializer dependencies, and side effects rather than forcing cosmetic assignment order.

## PowerShell Scripts

Prefer simple, working scripts built from standard PowerShell cmdlets and straightforward control flow.
Use lower-level APIs, extra helpers, or abstractions only when a concrete requirement makes them necessary or meaningfully clearer.
Keep complexity proportional to the real task, not imagined future cases.
Do not turn a small script into a framework or add speculative configuration, parameters, backups, rollback, or recovery mechanisms.
For ordinary local/helper functions, declare parameters inline, as in `function Get-Value([string]$Name) { ... }`, rather than in a `param(...)` block.
Keep `param(...)` for script entry points and advanced functions that use attributes such as `CmdletBinding`.

Simplicity must preserve correctness, safety, useful errors, and existing user work.
Verify the script's actual documented behavior and entry points.
A mocked call or successful preview is not full filesystem validation; disclose validation limitations and never claim unperformed checks passed.

## Documentation and Comments

For production APIs, write concise, meaningful documentation for every publicly accessible class, struct, interface, enum, delegate, constructor, method,
property, and other public member, documenting parameters, type parameters, and return values where applicable. In C#, follow the repository's XML
documentation conventions, and use `inheritdoc` only when an actual inherited contract provides sufficient documentation. Avoid empty tags, boilerplate
descriptions, and restating names without explaining the contract.

Major internal components and data structures also need concise documentation explaining their purpose and important ownership or lifecycle rules.
Internal visibility is not a reason to leave the design undocumented.

Ordinary test classes and test methods do not need public API documentation. Add concise explanation when a test's purpose, setup, or behavior is not
straightforward. Prefer readable identifiers over commentary that repeats the code. Where a method contains non-obvious control flow, an invariant, a
tricky algorithm, or a meaningful tradeoff, add a short comment explaining why it works that way. Do not comment every statement or turn implementation
details into redundant prose.

## Test-Driven Development

Favor test-driven development when feasible: for a new behavior, define an observable failing test first; implement the smallest correct solution; confirm
the test passes; then refactor while keeping the tests green. For bug fixes, first reproduce the issue with a regression test that demonstrably fails for
the right reason, then fix the cause and rerun the test. If a regression test cannot reasonably be written before the fix, explain why and provide another
reproducible validation path.

Prefer classical, state-based unit tests that check observable outcomes and contracts. Use small fakes, stubs, and recording spies rather than extensive
expectation-driven mocks. Interaction assertions and mocks are appropriate when the interaction itself is the contract, such as retries, cancellation, or
external protocol behavior. Keep tests deterministic, meaningful, and independent where possible. Never weaken a test simply to make it pass.

## Validation and Feedback

Run the smallest relevant tests or builds first, then expand coverage when the change or its callers require it. Report failed checks and environmental
blockers accurately, and distinguish local test success from end-to-end or deployed behavior. Do not broaden a focused change to repair unrelated test
failures without a clear reason or request.

## Local Developer Experience

Prefer systems that are practical to build, execute, debug, and validate locally. For services, support an F5-style development and debugging loop where
the technology permits it. Provide usable local HTTP request files, fixtures, or similar tools that exercise realistic service behavior without requiring a
production deployment. Make prerequisites, configuration, and test commands discoverable. Isolate risky external effects and do not mistake mock success
for end-to-end validation.

## Reliability and Operational Behavior

Be explicit about lifecycle transitions, cancellation, retries, error handling, idempotency, dependency ownership, and telemetry when they matter. Preserve
original failure information and do not disguise errors as successful results. Prefer bounded metrics and safe diagnostics; never log secrets or assume a
development shortcut is safe in a shared environment.

## Technical Decisions

Explain a recommendation in terms of requirements and tradeoffs rather than appealing to fashionable patterns. Prioritize lasting engineering quality over
minimizing initial coding effort, but keep work proportional to the actual problem. Challenge these preferences constructively when concrete evidence
warrants a different choice.

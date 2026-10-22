---
name: csharp-xmldoc
description: Add, review, and correct meaningful XML documentation for public C# APIs, while keeping edits scoped and ordinary test methods exempt.
---

# C# XML documentation

Follow the installed `OPINIONS.md`: public API contracts need useful documentation, and `<inheritdoc/>` is permitted when inherited docs are genuinely adequate. Do not broaden edit scope through dependency traversal.


You are a C# XML documentation specialist. Your task is to add or update XML documentation comments (`///`) on all public types and members in the specified C# file(s).

## Test File Exclusion

Do not require boilerplate on ordinary test classes and methods. Include reusable public test utilities if requested or when their contracts need documentation. A test project file is any `.cs` file whose path contains a `Test` or `test` directory segment (e.g., `src/Common/Common.Tests/`, `tests/UnitTests/`).

## Determining Which Files to Process

When the user asks to update documentation for "new and updated files", "changed files", "all affected files", or similar:

1. **Identify the requested scope.** Use `git diff --name-only` against the merge base of the current branch (e.g., `git merge-base HEAD origin/main` or the appropriate default branch) to identify files changed across the feature branch, as appropriate to the request — not just the last commit. Also check `git status` for uncommitted changes. Combine both lists.
2. **Filter to C# source files** (`.cs`) and exclude test projects (see above).
3. **Read referenced types for context only.** Inspect relevant base classes, interfaces, and custom types to understand inherited contracts.
4. **Modify only the requested or changed files.** Do not add referenced type files to the edit scope without asking. Verify every public API within that scope.

## Workflow

For each file to process:

1. Read the entire file.
2. **Enumerate every public member explicitly.** List all public types (classes, interfaces, structs, enums, delegates) and their public members (methods, properties, constructors, events, operators, indexers, fields). Do not rely on a visual scan — build a mental or written checklist of every member that needs documentation.
3. For each type that derives from a base class or implements an interface, locate and read the source file(s) for those base types/interfaces. Review their existing XML doc comments before writing any documentation.
4. Check every public member for: missing docs, incomplete docs (e.g., missing `<returns>`, missing `<param>`, missing `<exception>`), empty docs (see Empty Tag Detection below), and inaccurate docs (see Accuracy Verification below).
5. Add or update XML doc comments following the rules below.
6. Do not change any code logic, only add or update documentation comments.
7. **Verification pass.** After editing a file, re-read it and confirm every public member now has complete, non-empty documentation. This catches oversights from earlier steps.

## Empty Tag Detection

A documentation tag that is present but has an **empty body** is just as broken as a missing tag. Specifically watch for:

- `/// <summary></summary>` or `/// <summary>\n/// </summary>` with no text inside
- `/// <param name="x"></param>` with no description
- `/// <returns></returns>` with no description

These patterns commonly appear when a developer adds a parameter or renames one — the IDE scaffolds the tag but the description is left blank. **Treat empty-body tags as missing documentation and fill them in.**

## New File Priority

Files shown as **Added** (`A`) in `git status` are the most likely to have zero documentation. Prioritize these files and be especially thorough — do not assume a small or simple-looking file has adequate docs. A new file with a single class, constructor, and one method still needs documentation on all three.

## Accuracy Verification (Copy-Paste and Drift Detection)

After writing or reviewing documentation, perform these checks:

- **Parameter description accuracy:** For every `<param>` tag, verify the description actually describes THAT specific parameter, not a different one. This is critical for constructors and methods with multiple parameters of similar types. For example, if a constructor takes both `ICustomerRepository` and `IOrderRepositoryFactory`, each `<param>` must describe the correct one — not a copy-pasted description from the other.
- **Return type accuracy:** Verify `<returns>` matches what the method actually returns.
- **Exception accuracy:** Verify `<exception>` tags match exceptions actually thrown in the method body.
- **Semantic correctness over superficial completeness:** A `<summary>` that exists but describes the wrong thing is worse than a missing `<summary>`. Always read the code to understand what a member does before documenting it.

## Inheritance and Interface Documentation Consistency

When a class derives from a base class or implements an interface, documentation must stay consistent with the hierarchy:

- **Before writing any doc comment**, search for and read the base class or interface definition to inspect its existing XML documentation.
Use `<inheritdoc/>` when a genuinely documented inherited contract provides adequate documentation; otherwise write clear local documentation.
- **For inherited/implemented methods, properties, and events:** prefer `<inheritdoc/>` when the inherited contract fully applies. Otherwise provide accurate local `<summary>`, `<param>`, `<returns>`, and `<typeparam>` descriptions. Explain meaningful implementation differences in `<remarks>` where useful.
- **Do not rephrase or rewrite** the base/interface wording just for the sake of variety. The same logical operation must have consistent documentation across the type hierarchy. Divergent wording for the same method across an interface, abstract base, and concrete class is a documentation bug.
- **Update existing docs that have drifted.** If a derived member already has XML doc comments but they are inconsistent with the base/interface documentation, update them to realign. The base/interface documentation is the source of truth for the shared contract.
- **For type-level `<summary>` on a derived class:** document what the concrete class adds or specializes. Reference the base type or interface with `<see cref>` where appropriate (e.g., "Provides a thread-safe implementation of <see cref="IMyService"/>.").

## General Rules

- Use `<summary>` to provide a brief, one-sentence description of what the type or member does. Start the summary with a present-tense, third-person verb.
- Use `<remarks>` for additional information, which can include implementation details, usage notes, or any other relevant context.
- Use `<c>` for language-specific keywords like `null`, `true`, `false`, `int`, `bool`, etc.
- Use `<c>` for inline code snippets.
- Use `<see cref>` to reference other types or members inline (in a sentence).
- Use `<seealso>` for standalone (not in a sentence) references to other types or members in the "See also" section of the online docs.

## Methods

- Use `<param>` to describe method parameters.
  - The description should be a noun phrase that doesn't specify the data type.
  - Begin with an introductory article.
  - If the parameter is a flag enum, start the description with "A bitwise combination of the enumeration values that specifies...".
  - If the parameter is a non-flag enum, start the description with "One of the enumeration values that specifies...".
  - If the parameter is a Boolean, the wording should be of the form "<c>true</c> to ...; otherwise, <c>false</c>.".
  - If the parameter is an "out" parameter, the wording should be of the form "When this method returns, contains .... This parameter is treated as uninitialized.".
- Use `<typeparam>` to describe type parameters in generic types or methods.
- Use `<returns>` to describe what the method returns.
  - The description should be a noun phrase that doesn't specify the data type.
  - Begin with an introductory article.
  - If the return type is Boolean, the wording should be of the form "<c>true</c> if ...; otherwise, <c>false</c>.".

## Constructors

- The summary wording should be "Initializes a new instance of the <Class> class [or struct].".

## Properties

- The `<summary>` should start with:
  - "Gets or sets..." for a read-write property.
  - "Gets..." for a read-only property.
  - "Gets [or sets] a value that indicates whether..." for properties that return a Boolean value.

## Exceptions

- Use `<exception cref>` to document exceptions thrown by constructors, properties, indexers, methods, operators, and events.
- Document all exceptions thrown directly by the member.
- For exceptions thrown by nested members, document only the exceptions users are most likely to encounter.
- The description of the exception describes the condition under which it's thrown.
  - Omit "Thrown if ..." or "If ..." at the beginning of the sentence. Just state the condition directly, for example "An error occurred when accessing a Message Queuing API.".

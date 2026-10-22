---
name: csharp-stylefix
description: Investigate and fix C# dotnet format style violations in changed or explicitly selected files; use for targeted code-style repair without unrelated changes.
---

# C# style fixes

Apply the configured engineering opinions and preserve the user's work. Prefer scoped validation and concise reporting.


You are a C# code style fix specialist. Your task is to automatically fix code style violations detected by `dotnet format style`.

## Scope and safety

Default to only changed C# files, not the entire solution. Respect the user's requested scope and untracked edits. Full-solution cleanup requires explicit request.
Use the solution for analyzer context when appropriate, but pass `--include` for in-scope files. Do not alter unrelated files or treat every analyzer suggestion as automatically correct.


## Workflow

1. **Discover the target.** No path is expected from the user—find the **solution (.sln) file** automatically. Prefer running the analyzer using the solution for consistent configuration—never target individual `.csproj` files, because only the `.sln` ensures all projects (including test projects) are analyzed and all `.editorconfig` rules are applied consistently.
   - First, look for `.sln` files in the current working directory.
   - If none found, walk **up** the directory tree, checking each ancestor for `.sln` files. Stop at the first directory that contains one. Do not walk past a repository root (a directory containing a `.git` folder)—treat that as the upper boundary. If no `.git` folder is encountered, continue up to the filesystem root.
   - If still none found, walk **down** from the current directory (breadth-first), checking child directories for `.sln` files. Use the first match found at the shallowest depth.
   - If multiple `.sln` files are found in the same directory, prefer the one whose name most closely matches the directory name. If still ambiguous, use the first one alphabetically.
   - If no solution is available, use an appropriate project file or explain the limitation.
2. **Determine file scope.**
   - If explicitly running in **all-files mode**, skip this step—all files in the solution are in scope.
   - If running in **changed-files mode** (the default):
     1. Run `git diff --name-only --diff-filter=d HEAD` to get uncommitted changes (staged + unstaged).
     2. Run `git diff --name-only --diff-filter=d main...HEAD` (or the upstream tracking branch if available) to get committed changes on the current branch.
     3. Combine both lists, deduplicate, and keep only `*.cs` files. These are the **in-scope files**.
     4. If no `.cs` files are changed, report that there is nothing to check and stop.
3. **Run the linter.**
   - In **all-files mode** (only when explicitly requested), run `dotnet format style "<target>" --severity error --no-restore --verify-no-changes --verbosity detailed` without `--include`.
   - In **changed-files mode**, run `dotnet format style "<target>" --severity error --no-restore --verify-no-changes --verbosity detailed --include <file1> <file2> ...` passing the in-scope files via the `--include` flag. If the file list is very long (over 50 files), batch them into multiple runs.
   - The command exits non-zero when violations exist; this is expected.
4. **Parse the output.** Each violation is reported as a diagnostic with a rule ID, file path, line number, and column. Collect every violation.
5. **Review each in-scope violation** and fix it when the change is safe and meaningful (see rules below).
6. **Re-run the linter** (using the same scope as step 3) to confirm all violations are resolved. If new violations appear, fix those too. Repeat until the linter passes or no further progress can be made.

## Fixing Diagnostics

Review each in-scope diagnostic. Fix safe, meaningful violations; report questionable or out-of-scope diagnostics rather than introducing misleading changes.

For each diagnostic:

1. Read the diagnostic message carefully—it describes what is wrong and often implies the fix.
2. Read the affected line and its surrounding context in the source file.
3. Apply the smallest change that satisfies the rule. Common patterns include:
   - **Whitespace / formatting issues** (e.g. IDE0055): Remove trailing spaces, fix indentation to match surrounding code, remove extra blank lines, fix spacing around operators/keywords.
   - **Unused expression values** (e.g. IDE0058): Prefix the expression with `_ = ` to discard the return value. Example: `someList.Remove(item);` → `_ = someList.Remove(item);`
   - **Unnecessary assignments** (e.g. IDE0059): Replace the variable with a discard (`_ = <expression>;`) if unused elsewhere, or remove the assignment entirely.
   - **Qualification issues** (e.g. IDE0003/IDE0009): Add or remove `this.` as the diagnostic directs.
   - **Unnecessary usings** (e.g. IDE0005): Delete the reported `using` directive line.
4. If you genuinely cannot determine the correct fix for a specific diagnostic, skip it and report it to the user at the end.

## General Rules

- **Never change code logic.** Only change formatting, whitespace, discards, and using directives. The behavior of the program must remain identical.
- **Preserve the file's existing indent style.** If the file uses 4-space indentation, continue using 4-space indentation. If it uses tabs, use tabs.
- **Make minimal edits.** Only touch the lines reported by the linter. Do not reformat entire files.
- **Batch fixes per file.** When multiple violations exist in the same file, fix them all in one pass to avoid re-reading the file repeatedly.
- **Report a summary** at the end:
  1. State whether the final linter run passed.
  2. Show the total number of violations fixed.
  3. Group fixes by diagnostic ID. For each diagnostic ID, list every affected file with the line number(s) where the fix was applied. Format example:
     - **IDE0055 (Formatting)**: 3 files
       - `src/Common/Foo.cs`: lines 12, 45
       - `src/Common/Bar.cs`: line 7
       - `src/Web/Baz.cs`: line 103
     - **IDE0058 (Expression value unused)**: 1 file
       - `src/Common/Qux.cs`: line 22

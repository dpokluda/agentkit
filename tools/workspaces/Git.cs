using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentKit.Workspaces;

/// <summary>The installed Git process's actual exit code and separate captured output streams.</summary>
internal sealed record GitResult(int ExitCode, string Output, string Error);

/// <summary>Runs bounded local Git commands and parses their documented worktree and status formats.</summary>
internal sealed class Git
{
    /// <summary>Runs Git without shell interpolation, inherited repository overrides, prompts, or lazy network fetches.</summary>
    internal async Task<GitResult> RunAsync(string directory, CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        // Ambient Git variables must not redirect an observation or allocation to another clone or index.
        foreach (var key in process.StartInfo.Environment.Keys
            .Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            process.StartInfo.Environment.Remove(key);
        }
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
            "-c", "color.ui=false", "-C", directory }.Concat(arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new WorkspaceException("cannot_verify", $"Cannot start installed Git: {exception.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new GitResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new WorkspaceException("cannot_verify", "Git exceeded the 30-second execution limit.");
        }
    }

    /// <summary>Requires a successful command; preserves NUL record delimiters while removing console line endings.</summary>
    internal async Task<string> RequiredAsync(string directory, CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = await RunAsync(directory, cancellationToken, arguments);
        if (result.ExitCode != 0)
        {
            throw new WorkspaceException("cannot_verify",
                $"Git {arguments[0]} failed in '{directory}' (exit {result.ExitCode}): {result.Error.Trim()}");
        }

        return result.Output.TrimEnd('\r', '\n');
    }

    /// <summary>Resolves the physical common directory that identifies a clone and its linked worktrees.</summary>
    internal async Task<string> CommonDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var common = await RequiredAsync(path, cancellationToken, "rev-parse", "--path-format=absolute", "--git-common-dir");
        return Paths.Canonical(common, mustExist: true);
    }

    /// <summary>Reads every Git-reported worktree, including locked and prunable external registrations.</summary>
    internal async Task<List<GitWorktree>> WorktreesAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RequiredAsync(path, cancellationToken, "worktree", "list", "--porcelain", "-z");
        var worktrees = new List<GitWorktree>();
        GitWorktree? current = null;

        // NUL-delimited fields preserve spaces, quotes and newlines in paths; an empty field ends each record.
        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (current is not null)
                {
                    worktrees.Add(current);
                    current = null;
                }
            }
            else if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    throw new WorkspaceException("cannot_verify", "Git worktree output is missing a record delimiter.");
                }

                current = new GitWorktree(Path.GetFullPath(field[9..]));
            }
            else if (current is null)
            {
                throw new WorkspaceException("cannot_verify", "Git worktree output contains an unexpected record.");
            }
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                current.Head = field[5..];
            }
            else if (field.StartsWith("branch ", StringComparison.Ordinal))
            {
                current.Branch = field[7..];
            }
            else if (field == "detached")
            {
                current.Detached = true;
            }
            else if (field == "bare")
            {
                current.Bare = true;
            }
            else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))
            {
                current.Locked = true;
                current.LockReason = field.Length > 6 ? field[7..] : null;
            }
            else if (field == "prunable" || field.StartsWith("prunable ", StringComparison.Ordinal))
            {
                current.Prunable = true;
            }
        }

        if (current is not null || worktrees.Count == 0)
        {
            throw new WorkspaceException("cannot_verify", "Git worktree output is incomplete.");
        }

        return worktrees;
    }

    /// <summary>Observes tracked, untracked and ignored entries without refreshing the index or invoking fsmonitor.</summary>
    internal async Task<ContentObservation> ContentAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RequiredAsync(path, cancellationToken, "status", "--porcelain=v1", "-z",
            "--untracked-files=all", "--ignored=matching", "--ignore-submodules=none");
        var fields = output.Split('\0');
        var changed = new List<string>();
        var untracked = new List<string>();
        var ignored = new List<string>();

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length == 0)
            {
                continue;
            }

            if (field.Length < 4 || field[2] != ' ')
            {
                throw new WorkspaceException("cannot_verify", $"Unrecognized Git status output in '{path}'.");
            }

            var status = field[..2];
            var entry = field[3..];
            if (status == "??")
            {
                untracked.Add(entry);
            }
            else if (status == "!!")
            {
                ignored.Add(entry);
            }
            else
            {
                changed.Add(entry);

                // A rename/copy contributes one changed entry followed by a second NUL-delimited source path.
                if (status.Contains('R') || status.Contains('C'))
                {
                    if (++index >= fields.Length || fields[index].Length == 0)
                    {
                        throw new WorkspaceException("cannot_verify", $"Incomplete Git rename status in '{path}'.");
                    }
                }
            }
        }

        return new ContentObservation(changed.Count, untracked.Count, ignored.Count, changed, untracked, ignored,
            "Git porcelain entries; ignored directories and nested repositories are not recursively inventoried.");
    }

    /// <summary>Detects sparse-checkout mode so allocation can refuse a layout it does not support.</summary>
    internal async Task<bool> SparseAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(path, cancellationToken, "config", "--bool", "--get", "core.sparseCheckout");
        if (result.ExitCode == 1)
        {
            return false;
        }

        if (result.ExitCode != 0 || result.Output.Trim() is not ("true" or "false"))
        {
            throw new WorkspaceException("cannot_verify", $"Cannot read sparse-checkout mode in '{path}': {result.Error.Trim()}");
        }

        return result.Output.Trim() == "true";
    }

    /// <summary>Observes Git operation markers and index locks; unreadable markers are errors, not absence.</summary>
    internal async Task<List<string>> OperationsAsync(string path, CancellationToken cancellationToken)
    {
        var directory = await RequiredAsync(path, cancellationToken, "rev-parse", "--absolute-git-dir");
        var operations = new List<string>();
        foreach (var name in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG",
            "rebase-merge", "rebase-apply", "sequencer", "index.lock" })
        {
            try
            {
                File.GetAttributes(Path.Combine(directory, name));
                operations.Add(name);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Absence is observed, rather than inferred from a failed permission check.
            }
        }

        return operations;
    }
}

using System.Diagnostics;
using System.Text;
using AgentKit.Workspaces;
using Xunit;

namespace Workspaces.Tests;

public sealed class OwnershipTests
{
    [Fact]
    public async Task RelativePathsCaseAndJunctionAliasesResolveOnePhysicalClone()
    {
        using var fixture = await GitFixture.CreateAsync();
        var relative = await fixture.CliProcessAsync("repo", "add", "example", "--path",
            Path.GetRelativePath(fixture.Root, fixture.Repository), "--pool-root",
            Path.GetRelativePath(fixture.Root, fixture.PoolRoot), "--max-slots", "2");
        Assert.Equal(0, relative.ExitCode);
        var alias = Path.Combine(fixture.Root, "clone [junction alias]");
        await CreateJunctionAsync(alias, fixture.Repository);
        var repeated = await fixture.CliAsync("repo", "add", "EXAMPLE", "--path", alias.ToUpperInvariant(),
            "--pool-root", fixture.PoolRoot.ToUpperInvariant(), "--max-slots", "2");
        Assert.Equal(0, repeated.ExitCode);
        Assert.Equal(relative.Data.GetProperty("repoId").GetString(), repeated.Data.GetProperty("repoId").GetString());
        var conflict = await fixture.CliAsync("repo", "add", "other-name", "--path", alias,
            "--pool-root", Path.Combine(fixture.Root, "other pool"), "--max-slots", "1");
        Assert.Equal("registration_conflict", conflict.ErrorCode);
        Assert.Single((await fixture.CliAsync("list")).Data.GetProperty("repositories").EnumerateArray());
        Directory.Delete(alias);
    }

    [Fact]
    public async Task ClonesOfTheSameRemoteNeverShareSlotsOrIdentity()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.GitAsync("remote", "add", "origin", "https://example.invalid/workspaces-test.git");
        var second = Path.Combine(fixture.Root, "second [physical clone]");
        var secondPool = Path.Combine(fixture.Root, "second pool");
        await GitFixture.RunGitAsync(fixture.Root, "clone", "--quiet", "--no-hardlinks", "--no-checkout",
            "--", fixture.Repository, second);
        await GitFixture.RunGitAsync(second, "config", "core.hooksPath", Path.Combine(fixture.Root, "empty hooks"));
        await GitFixture.RunGitAsync(second, "remote", "set-url", "origin", "https://example.invalid/workspaces-test.git");
        await GitFixture.RunGitAsync(second, "checkout", "--quiet", "main");
        await fixture.RegisterAsync(maxSlots: 1);
        var registration = await fixture.CliAsync("repo", "add", "second", "--path", second,
            "--pool-root", secondPool, "--max-slots", "1");
        Assert.Equal(0, registration.ExitCode);
        var first = await fixture.AcquireAsync("same-request", "task/one");
        var other = await fixture.CliAsync("acquire", "--repo", "second", "--base", "main",
            "--branch", "task/one", "--task", "T017", "--owner", "owner", "--request-id", "same-request");
        Assert.Equal(0, other.ExitCode);
        Assert.NotEqual(first.Data.GetProperty("repoId").GetString(), other.Data.GetProperty("repoId").GetString());
        Assert.NotEqual(first.Data.GetProperty("workspaceId").GetString(), other.Data.GetProperty("workspaceId").GetString());
        Assert.StartsWith(secondPool, other.Data.GetProperty("path").GetString());
        Assert.Equal("pool_exhausted", (await fixture.AcquireAsync("overfull", "task/overfull")).ErrorCode);
    }

    [Fact]
    public async Task DetachedExternalWorktreesStayReportOnlyAndStatusParsesRenamesAndGitLocks()
    {
        using var fixture = await GitFixture.CreateAsync();
        var external = Path.Combine(fixture.Root, "external [detached checkout]");
        await fixture.GitAsync("worktree", "add", "--detach", "--", external, "main");
        await GitFixture.RunGitAsync(external, "mv", "--", "tracked.txt", "renamed [file].txt");
        await fixture.GitAsync("worktree", "lock", "--reason", "manual writer", external);
        await fixture.RegisterAsync(path: external);
        var list = await fixture.CliAsync("list");
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(fixture.Repository, list.Data.GetProperty("repositories")[0].GetProperty("repositoryPath").GetString());
        var view = Assert.Single(list.Data.GetProperty("workspaces").EnumerateArray(),
            workspace => workspace.GetProperty("path").GetString() == external);
        Assert.True(view.GetProperty("detached").GetBoolean());
        Assert.True(view.GetProperty("gitLocked").GetBoolean());
        Assert.Equal("manual writer", view.GetProperty("lockReason").GetString());
        Assert.Equal(1, view.GetProperty("content").GetProperty("trackedChanges").GetInt32());
        Assert.Equal("renamed [file].txt", view.GetProperty("content").GetProperty("changedPaths")[0].GetString());
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var refused = await fixture.CliAsync("release", view.GetProperty("workspaceId").GetString()!,
            "--lease", "not-owned", "--outcome", "stopped");
        Assert.Equal("external_owner", refused.ErrorCode);
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
    }

    [Fact]
    public async Task ReleasePreservesNestedRepositoriesAndIdempotencyUsesTheOriginalResolvedBase()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync();
        var acquired = await fixture.AcquireAsync("first", "task/first");
        var slot = acquired.Data.GetProperty("path").GetString()!;
        var nested = Path.Combine(slot, "nested [repository]");
        Directory.CreateDirectory(nested);
        await GitFixture.RunGitAsync(nested, "init", "--quiet");
        await File.WriteAllTextAsync(Path.Combine(nested, "candidate.txt"), "preserve nested data");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "new-main.txt"), "later main revision");
        await fixture.GitAsync("add", ".");
        await fixture.GitAsync("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Advance base");
        var retry = await fixture.AcquireAsync("first", "task/first");
        Assert.Equal(acquired.Data.GetProperty("baseCommit").GetString(), retry.Data.GetProperty("baseCommit").GetString());
        Assert.NotEqual(await fixture.GitAsync("rev-parse", "main"), retry.Data.GetProperty("baseCommit").GetString());
        var before = WorkspaceTests.Snapshot(slot);
        var release = await fixture.CliAsync("release", acquired.Data.GetProperty("workspaceId").GetString()!, "--lease",
            acquired.Data.GetProperty("leaseId").GetString()!, "--outcome", "blocked");
        Assert.Equal(0, release.ExitCode);
        Assert.Equal(before, WorkspaceTests.Snapshot(slot));
        var view = await fixture.CliAsync("inspect", acquired.Data.GetProperty("workspaceId").GetString()!);
        Assert.True(view.Data.GetProperty("content").GetProperty("untrackedEntries").GetInt32() > 0);
    }

    [Theory]
    [InlineData("sparse")]
    [InlineData("submodule")]
    public async Task UnsupportedRepositoryModesAreObservedButNeverAllocated(string mode)
    {
        using var fixture = await GitFixture.CreateAsync();
        if (mode == "sparse")
        {
            await fixture.GitAsync("sparse-checkout", "init", "--cone");
        }
        else
        {
            var head = await fixture.GitAsync("rev-parse", "HEAD");
            await fixture.GitAsync("update-index", "--add", "--cacheinfo", $"160000,{head},module");
            await fixture.GitAsync("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Fixture gitlink");
        }
        await fixture.RegisterAsync();
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var result = await fixture.AcquireAsync("unsupported", "task/unsupported");
        Assert.Equal("unsupported_repository_mode", result.ErrorCode);
        Assert.Equal(3, result.ExitCode);
        var inventory = await fixture.CliAsync("list");
        Assert.Equal(0, inventory.ExitCode);
        if (mode == "sparse")
        {
            Assert.True(inventory.Data.GetProperty("workspaces")[0].GetProperty("sparseCheckout").GetBoolean());
        }
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
    }

    [Fact]
    public async Task InProgressGitOperationsAreReportedAndBlockAllocation()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.GitAsync("checkout", "--quiet", "-b", "side");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "side.txt"), "side");
        await fixture.GitAsync("add", ".");
        await fixture.GitAsync("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Side change");
        await fixture.GitAsync("checkout", "--quiet", "main");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "main.txt"), "main");
        await fixture.GitAsync("add", ".");
        await fixture.GitAsync("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Main change");
        await fixture.GitAsync("-c", "commit.gpgsign=false", "merge", "--no-commit", "--no-ff", "side");
        await fixture.RegisterAsync();
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var list = await fixture.CliAsync("list");
        Assert.Contains(list.Data.GetProperty("workspaces")[0].GetProperty("inProgressOperations").EnumerateArray(),
            operation => operation.GetString() == "MERGE_HEAD");
        Assert.Equal("state_needs_attention", (await fixture.AcquireAsync("blocked", "task/blocked")).ErrorCode);
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
    }

    [Fact]
    public async Task InventoryDoesNotRunAConfiguredFsmonitorHook()
    {
        using var fixture = await GitFixture.CreateAsync();
        var hook = Path.Combine(fixture.Root, "fsmonitor hook");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf invoked > fsmonitor-invoked.txt\nexit 1\n", new UTF8Encoding(false));
        await fixture.GitAsync("config", "core.fsmonitor", hook);
        await fixture.RegisterAsync();
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var inventory = await fixture.CliAsync("list");
        Assert.Equal(0, inventory.ExitCode);
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "fsmonitor-invoked.txt")));
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
    }

    private static async Task CreateJunctionAsync(string path, string target)
    {
        var script = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '"
            + path.Replace("'", "''", StringComparison.Ordinal) + "' -Target '"
            + target.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null";
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        Assert.True(process.ExitCode == 0, await output + await error);
    }
}

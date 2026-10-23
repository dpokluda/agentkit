using System.Diagnostics;
using System.Text.Json;
using AgentKit.Workspaces;
using Xunit;

namespace Workspaces.Tests;

public sealed class CommandBoundaryTests
{
    [Fact]
    public async Task MissingGitProducesVersionedJsonAndNoStateOrCheckoutWrites()
    {
        using var fixture = await GitFixture.CreateAsync();
        using var process = fixture.CreateCli("repo", "add", "example", "--path", fixture.Repository,
            "--pool-root", fixture.PoolRoot, "--max-slots", "1");
        process.StartInfo.Environment["PATH"] = "";
        process.Start();
        var result = await GitFixture.ReadCliAsync(process);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("cannot_verify", result.ErrorCode);
        Assert.Equal(1, result.Json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(result.Stderr);
        Assert.False(Directory.Exists(fixture.StateRoot));
        Assert.False(Directory.Exists(fixture.PoolRoot));
    }

    [Fact]
    public async Task InheritedGitEnvironmentCannotRedirectCloneOrIndexIdentity()
    {
        using var fixture = await GitFixture.CreateAsync();
        var foreignIndex = Path.Combine(fixture.Root, "foreign index");
        using var process = fixture.CreateCli("repo", "add", "example", "--path", fixture.Repository,
            "--pool-root", fixture.PoolRoot, "--max-slots", "1");
        process.StartInfo.Environment["GIT_DIR"] = Path.Combine(fixture.Root, "not a git directory");
        process.StartInfo.Environment["GIT_WORK_TREE"] = fixture.Root;
        process.StartInfo.Environment["GIT_INDEX_FILE"] = foreignIndex;
        process.StartInfo.Environment["GIT_CONFIG_COUNT"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_KEY_0"] = "core.bare";
        process.StartInfo.Environment["GIT_CONFIG_VALUE_0"] = "true";
        process.Start();
        var result = await GitFixture.ReadCliAsync(process);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(fixture.Repository, result.Data.GetProperty("repositoryPath").GetString());
        Assert.False(File.Exists(foreignIndex));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("index")]
    public async Task InaccessibleObservationsStayUnknownRatherThanClean(string inaccessible)
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync();
        var candidate = await fixture.AcquireAsync("first", "task/first");
        var candidatePath = candidate.Data.GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(candidatePath, "keep.txt"), "candidate");
        var candidateBefore = WorkspaceTests.Snapshot(candidatePath);
        var lockedPath = inaccessible == "catalog" ? fixture.CatalogPath
            : Path.Combine(fixture.Repository, ".git", "index");
        using (var denied = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var list = await fixture.CliAsync("list");
            Assert.Equal(4, list.ExitCode);
            Assert.False(list.Json.RootElement.GetProperty("ok").GetBoolean());
            Assert.NotEmpty(list.Stderr);
            var primary = Assert.Single(list.Data.GetProperty("workspaces").EnumerateArray(),
                item => item.GetProperty("isPrimary").GetBoolean());
            if (inaccessible == "index")
            {
                Assert.Equal(JsonValueKind.Null, primary.GetProperty("content").ValueKind);
            }
            else
            {
                Assert.All(list.Data.GetProperty("workspaces").EnumerateArray(),
                    item => Assert.Equal("unknown", item.GetProperty("ownership").GetString()));
                Assert.Equal("state_needs_attention", (await fixture.AcquireAsync("second", "task/second")).ErrorCode);
            }
            Assert.Equal(candidateBefore, WorkspaceTests.Snapshot(candidatePath));
        }
        Assert.Equal(0, (await fixture.CliAsync("list")).ExitCode);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("recycle")]
    [InlineData("remove")]
    public async Task DeferredMutationsAreRejectedWithoutCreatingRuntimeState(string command)
    {
        using var fixture = await GitFixture.CreateAsync();
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var result = await fixture.CliAsync(command, "--yes");
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
        Assert.False(Directory.Exists(fixture.StateRoot));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task PoolCapacityMustBeExplicitlyPositive(string capacity)
    {
        using var fixture = await GitFixture.CreateAsync();
        var result = await fixture.CliAsync("repo", "add", "example", "--path", fixture.Repository,
            "--pool-root", fixture.PoolRoot, "--max-slots", capacity);
        Assert.Equal(2, result.ExitCode);
        Assert.False(Directory.Exists(fixture.StateRoot));
        var missing = await fixture.CliAsync("repo", "add", "example", "--path", fixture.Repository,
            "--pool-root", fixture.PoolRoot);
        Assert.Equal(2, missing.ExitCode);
        Assert.False(Directory.Exists(fixture.StateRoot));
    }

    [Fact]
    public async Task HumanOutputIsReadableAndWarningsStayOnStderr()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync();
        var allocation = await fixture.AcquireAsync("human", "task/human");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(["inspect", allocation.Data.GetProperty("workspaceId").GetString()!,
            "--state-root", fixture.StateRoot], stdout, stderr);
        Assert.Equal(0, exit);
        Assert.Contains("Lease", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.PoolRoot, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning [", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("activity_unknown", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("cleanup_deferred", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContendedMutationLockHasABoundedWaitAndDoesNotAllocate()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync();
        var before = WorkspaceTests.Snapshot(fixture.Root);
        CliOutput result;
        var timer = Stopwatch.StartNew();
        using (var held = new FileStream(Path.Combine(fixture.StateRoot, "mutation.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await fixture.AcquireAsync("contended", "task/contended");
        }
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("state_busy", result.ErrorCode);
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(20));
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
    }
}

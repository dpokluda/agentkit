using System.Diagnostics;
using System.Text.Json;
using AgentKit.Workspaces;
using Xunit;

namespace Workspaces.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task InventoryIncludesExternalWorktreesAndDoesNotWriteGitOrState()
    {
        using var fixture = await GitFixture.CreateAsync();
        var external = Path.Combine(fixture.Root, "copilot [manual worktree]");
        await fixture.GitAsync("worktree", "add", "-b", "manual", external);
        await File.AppendAllTextAsync(Path.Combine(external, "tracked.txt"), "modified");
        await File.WriteAllTextAsync(Path.Combine(external, "untracked [file].txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(external, "ignored.tmp"), "keep");
        await fixture.RegisterAsync();
        var before = Snapshot(fixture.StateRoot, Path.Combine(fixture.Repository, ".git"));

        var listed = await fixture.CliAsync("list", "--repo", "example");

        Assert.Equal(0, listed.ExitCode);
        var workspaces = listed.Data.GetProperty("workspaces").EnumerateArray().ToArray();
        Assert.Equal(2, workspaces.Length);
        Assert.Single(workspaces, item => item.GetProperty("isPrimary").GetBoolean());
        var manual = Assert.Single(workspaces, item => item.GetProperty("path").GetString() == external);
        Assert.Equal("external", manual.GetProperty("ownership").GetString());
        Assert.Equal("unknown", manual.GetProperty("manager").GetString());
        Assert.Equal(JsonValueKind.Null, manual.GetProperty("task").ValueKind);
        Assert.Equal("unknown", manual.GetProperty("activity").GetString());
        Assert.True(manual.GetProperty("content").GetProperty("trackedChanges").GetInt32() > 0);
        Assert.True(manual.GetProperty("content").GetProperty("untrackedEntries").GetInt32() > 0);
        Assert.True(manual.GetProperty("content").GetProperty("ignoredEntries").GetInt32() > 0);

        var inspected = await fixture.CliAsync("inspect", manual.GetProperty("workspaceId").GetString()!);
        Assert.Equal(0, inspected.ExitCode);
        Assert.Equal(external, inspected.Data.GetProperty("path").GetString());
        Assert.Equal(0, (await fixture.CliAsync("doctor")).ExitCode);
        Assert.Equal(before, Snapshot(fixture.StateRoot, Path.Combine(fixture.Repository, ".git")));
    }

    [Fact]
    public async Task AcquisitionIsIdempotentAndReleaseRetainsEveryKindOfContentAndCapacity()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 1);
        var acquired = await fixture.AcquireAsync("request-1", "task/one");
        Assert.Equal(0, acquired.ExitCode);
        var workspace = acquired.Data.GetProperty("workspaceId").GetString()!;
        var path = acquired.Data.GetProperty("path").GetString()!;
        var lease = acquired.Data.GetProperty("leaseId").GetString()!;
        Assert.Equal("leased", acquired.Data.GetProperty("state").GetString());
        Assert.Equal(await fixture.GitAsync("rev-parse", "main"), acquired.Data.GetProperty("baseCommit").GetString());

        await File.AppendAllTextAsync(Path.Combine(path, "tracked.txt"), "candidate");
        await File.WriteAllTextAsync(Path.Combine(path, "untracked.txt"), "candidate");
        await File.WriteAllTextAsync(Path.Combine(path, "ignored.tmp"), "candidate");
        var contents = Snapshot(path);
        var retry = await fixture.AcquireAsync("request-1", "task/one");
        Assert.Equal(0, retry.ExitCode);
        Assert.Equal(workspace, retry.Data.GetProperty("workspaceId").GetString());
        Assert.Equal(lease, retry.Data.GetProperty("leaseId").GetString());
        Assert.True(retry.Data.GetProperty("replayed").GetBoolean());
        await fixture.RegisterAsync(maxSlots: 1);
        var conflict = await fixture.AcquireAsync("request-1", "task/different");
        Assert.Equal("idempotency_conflict", conflict.ErrorCode);

        var stale = await fixture.CliAsync("release", workspace, "--lease", "wrong", "--outcome", "candidate-ready");
        Assert.Equal("stale_lease", stale.ErrorCode);
        var released = await fixture.CliAsync("release", workspace, "--lease", lease, "--outcome", "candidate-ready",
            "--checkpoint", Path.Combine(fixture.Root, "missing checkpoint.md"));
        Assert.Equal(0, released.ExitCode);
        Assert.Equal("retained", released.Data.GetProperty("state").GetString());
        Assert.Equal(contents, Snapshot(path));
        var exhausted = await fixture.AcquireAsync("request-2", "task/two");
        Assert.Equal(3, exhausted.ExitCode);
        Assert.Equal("pool_exhausted", exhausted.ErrorCode);
        Assert.Equal(contents, Snapshot(path));
        var retryAfterRelease = await fixture.AcquireAsync("request-1", "task/one");
        Assert.Equal("retained", retryAfterRelease.Data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ConcurrentAllocationsCannotExceedCapacityOrShareLease()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(index => fixture.AcquireAsync($"request-{index}", $"task/{index}")));
        var successful = results.Where(result => result.ExitCode == 0).ToArray();
        Assert.Equal(2, successful.Length);
        Assert.Equal(2, successful.Select(result => result.Data.GetProperty("path").GetString()).Distinct().Count());
        Assert.Equal(2, successful.Select(result => result.Data.GetProperty("leaseId").GetString()).Distinct().Count());
        Assert.All(results.Where(result => result.ExitCode != 0),
            result => Assert.Equal("pool_exhausted", result.ErrorCode));
        Assert.Equal(3, (await fixture.GitAsync("worktree", "list", "--porcelain"))
            .Split("worktree ", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task InvalidInputAndMissingRefsDoNotAllocate()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync();
        var missing = await fixture.AcquireAsync("missing", "task/missing", baseRef: "origin/does-not-exist");
        Assert.Equal("invalid_ref", missing.ErrorCode);
        var badBranch = await fixture.AcquireAsync("bad", "bad..branch");
        Assert.Equal(2, badBranch.ExitCode);
        var existing = await fixture.AcquireAsync("exists", "main");
        Assert.Equal("branch_exists", existing.ErrorCode);
        Assert.Empty(Directory.GetDirectories(fixture.PoolRoot));
        Assert.Single((await fixture.CliAsync("list")).Data.GetProperty("workspaces").EnumerateArray());
        var syntax = await fixture.CliAsync("acquire", "--repo", "example");
        Assert.Equal(2, syntax.ExitCode);
        Assert.Equal("invalid_arguments", syntax.ErrorCode);
        Assert.NotEmpty(syntax.Stderr);
    }

    internal static string[] Snapshot(params string[] roots)
    {
        return roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .Select(path => $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();
    }
}

internal sealed class GitFixture : IDisposable
{
    private GitFixture(string root)
    {
        Root = root;
        Repository = Path.Combine(root, "source [clone with spaces]");
        StateRoot = Path.Combine(root, "state [local]");
        PoolRoot = Path.Combine(root, "pool [managed slots]");
    }

    internal string Root { get; }
    internal string Repository { get; }
    internal string StateRoot { get; }
    internal string PoolRoot { get; }
    internal string CatalogPath => Directory.EnumerateFiles(StateRoot, "repo-*.json").Single();

    internal static async Task<GitFixture> CreateAsync()
    {
        var fixture = new GitFixture(Paths.Canonical(Path.Combine(Path.GetTempPath(), $"agentkit-workspaces-{Guid.NewGuid():N}")));
        Directory.CreateDirectory(fixture.Repository);
        await fixture.GitAsync("init", "--initial-branch=main");
        await fixture.GitAsync("config", "user.name", "Workspace Test");
        await fixture.GitAsync("config", "user.email", "workspace-test@example.invalid");
        await fixture.GitAsync("config", "core.hooksPath", Path.Combine(fixture.Root, "empty hooks"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "tracked.txt"), "baseline\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".gitignore"), "*.tmp\n");
        await fixture.GitAsync("add", ".");
        await fixture.GitAsync("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Fixture baseline");
        return fixture;
    }

    internal async Task RegisterAsync(int maxSlots = 3, string? path = null)
    {
        var result = await CliAsync("repo", "add", "example", "--path", path ?? Repository,
            "--pool-root", PoolRoot, "--max-slots", maxSlots.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    internal Task<CliOutput> AcquireAsync(string request, string branch, string baseRef = "main")
    {
        return CliAsync("acquire", "--repo", "example", "--base", baseRef, "--branch", branch,
            "--task", "T017", "--owner", "test-owner", "--request-id", request);
    }

    internal async Task<CliOutput> CliAsync(params string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var args = arguments.Concat(["--state-root", StateRoot, "--json"]).ToArray();
        var exitCode = await Program.RunAsync(args, stdout, stderr);
        return new CliOutput(exitCode, stdout.ToString(), stderr.ToString());
    }

    internal Process CreateCli(params string[] arguments)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo(DotnetPath)
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments.Concat(["--state-root", StateRoot, "--json"]))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        return process;
    }

    internal Process StartCli(params string[] arguments)
    {
        var process = CreateCli(arguments);
        process.Start();
        return process;
    }

    internal async Task<CliOutput> CliProcessAsync(params string[] arguments)
    {
        using var process = StartCli(arguments);
        return await ReadCliAsync(process);
    }

    internal static async Task<CliOutput> ReadCliAsync(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
        return new CliOutput(process.ExitCode, await stdout, await stderr);
    }

    internal static string DotnetPath => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    internal Task<string> GitAsync(params string[] arguments)
    {
        return RunGitAsync(Repository, arguments);
    }

    internal static async Task<string> RunGitAsync(string directory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, "no-global-config");
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout;
        Assert.True(process.ExitCode == 0, $"{string.Join(' ', arguments)}: {await stderr}");
        return output.TrimEnd('\r', '\n');
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class CliOutput
{
    internal CliOutput(int exitCode, string stdout, string stderr)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        Json = JsonDocument.Parse(stdout);
    }

    internal int ExitCode { get; }
    internal string Stdout { get; }
    internal string Stderr { get; }
    internal JsonDocument Json { get; }
    internal JsonElement Data => Json.RootElement.GetProperty("data");
    internal string? ErrorCode => Json.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null
        ? null : Json.RootElement.GetProperty("error").GetProperty("code").GetString();
}

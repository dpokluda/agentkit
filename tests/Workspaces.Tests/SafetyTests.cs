using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentKit.Workspaces;
using Xunit;

namespace Workspaces.Tests;

public sealed class SafetyTests
{
    [Fact]
    public async Task IndependentProcessesShareOneIdempotentLeaseAndItDoesNotExpireWithAge()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 1);
        var args = new[] { "acquire", "--repo", "example", "--base", "main", "--branch", "task/process",
            "--task", "T017", "--owner", "test-owner", "--request-id", "same-request" };
        var outputs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.CliProcessAsync(args)));
        Assert.All(outputs, output => Assert.True(output.ExitCode == 0, output.Stdout + output.Stderr));
        Assert.Single(outputs.Select(output => output.Data.GetProperty("workspaceId").GetString()).Distinct());
        Assert.Single(outputs.Select(output => output.Data.GetProperty("leaseId").GetString()).Distinct());
        Assert.Equal(1, outputs.Count(output => !output.Data.GetProperty("replayed").GetBoolean()));

        var state = JsonNode.Parse(await File.ReadAllTextAsync(fixture.CatalogPath))!;
        state["slots"]![0]!["createdAt"] = DateTimeOffset.UtcNow.AddYears(-5);
        await File.WriteAllTextAsync(fixture.CatalogPath, state.ToJsonString(JsonContract.Options));
        var oldLease = await fixture.CliProcessAsync("list");
        var leased = Assert.Single(oldLease.Data.GetProperty("workspaces").EnumerateArray(),
            workspace => workspace.GetProperty("ownership").GetString() == "managed");
        Assert.Equal("leased", leased.GetProperty("state").GetString());
        Assert.Equal(outputs[0].Data.GetProperty("leaseId").GetString(), leased.GetProperty("leaseId").GetString());
        Assert.Equal("pool_exhausted", (await fixture.AcquireAsync("new-request", "task/new")).ErrorCode);

        var workspaceId = leased.GetProperty("workspaceId").GetString()!;
        var leaseId = leased.GetProperty("leaseId").GetString()!;
        var wrong = await fixture.CliProcessAsync("release", workspaceId, "--lease", "obsolete", "--outcome", "stopped");
        Assert.Equal("stale_lease", wrong.ErrorCode);
        var release = await fixture.CliProcessAsync("release", workspaceId, "--lease", leaseId, "--outcome", "stopped");
        Assert.Equal(0, release.ExitCode);
        Assert.Equal("retained", release.Data.GetProperty("state").GetString());
        var repeated = await fixture.CliProcessAsync("release", workspaceId, "--lease", leaseId, "--outcome", "stopped");
        Assert.Equal(0, repeated.ExitCode);
        var changed = await fixture.CliProcessAsync("release", workspaceId, "--lease", leaseId, "--outcome", "failed");
        Assert.Equal("stale_lease", changed.ErrorCode);
    }

    [Fact]
    public async Task DifferentProcessRequestsCannotAllocateOverOccupiedCapacity()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => fixture.CliProcessAsync("acquire",
            "--repo", "example", "--base", "main", "--branch", $"task/process-{index}",
            "--task", "T017", "--owner", "owner", "--request-id", $"request-{index}")));
        Assert.Single(results, result => result.ExitCode == 0);
        Assert.All(results.Where(result => result.ExitCode != 0), result => Assert.Equal("pool_exhausted", result.ErrorCode));
        Assert.Single(Directory.EnumerateDirectories(fixture.PoolRoot));
    }

    [Theory]
    [InlineData("catalog-corrupt")]
    [InlineData("catalog-null-slot")]
    [InlineData("catalog-newer")]
    [InlineData("catalog-missing")]
    [InlineData("registry-corrupt")]
    [InlineData("registry-null-repository")]
    [InlineData("registry-missing")]
    [InlineData("registry-newer")]
    [InlineData("marker-changed")]
    [InlineData("uncataloged-directory")]
    [InlineData("interrupted-registration")]
    [InlineData("interrupted-slot")]
    [InlineData("slot-missing")]
    public async Task InvalidStateNeverBecomesAnEmptyReusablePool(string failure)
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 2);
        var acquired = await fixture.AcquireAsync("first", "task/first");
        Assert.Equal(0, acquired.ExitCode);
        var path = acquired.Data.GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "preserved");
        var registryPath = Path.Combine(fixture.StateRoot, "repositories.json");
        switch (failure)
        {
            case "catalog-corrupt":
                await File.WriteAllTextAsync(fixture.CatalogPath, "not-json");
                break;
            case "catalog-null-slot":
                await EditAsync(fixture.CatalogPath, state => state["slots"]!.AsArray().Add((JsonNode?)null));
                break;
            case "catalog-newer":
                await EditAsync(fixture.CatalogPath, state => state["schemaVersion"] = 999);
                break;
            case "catalog-missing":
                File.Delete(fixture.CatalogPath);
                break;
            case "registry-corrupt":
                await File.WriteAllTextAsync(registryPath, "{}");
                break;
            case "registry-null-repository":
                await EditAsync(registryPath, state => state["repositories"]!.AsArray().Add((JsonNode?)null));
                break;
            case "registry-missing":
                File.Delete(registryPath);
                break;
            case "registry-newer":
                await EditAsync(registryPath, state => state["schemaVersion"] = 999);
                break;
            case "marker-changed":
                await EditAsync(Path.Combine(fixture.PoolRoot, StateStore.MarkerName), state => state["poolId"] = "different-owner");
                break;
            case "uncataloged-directory":
                Directory.CreateDirectory(Path.Combine(fixture.PoolRoot, "slot-02"));
                await File.WriteAllTextAsync(Path.Combine(fixture.PoolRoot, "slot-02", "external.txt"), "not ours");
                break;
            case "interrupted-registration":
                await EditAsync(fixture.CatalogPath, state => state["status"] = "initializing");
                break;
            case "interrupted-slot":
                await EditAsync(fixture.CatalogPath, state => state["slots"]![0]!["state"] = "creating");
                break;
            case "slot-missing":
                await fixture.GitAsync("worktree", "remove", "--force", path);
                break;
        }
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var result = await fixture.AcquireAsync("second", "task/second");
        Assert.Equal(4, result.ExitCode);
        Assert.Contains(result.ErrorCode, new[] { "state_needs_attention", "cannot_verify" });
        var doctor = await fixture.CliAsync("doctor");
        Assert.Equal(4, doctor.ExitCode);
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
        Assert.DoesNotContain("task/second", await fixture.GitAsync("branch", "--list"));
    }

    [Fact]
    public async Task LostRegistrationCannotAdoptANonemptyPool()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync();
        Assert.Equal(0, (await fixture.AcquireAsync("first", "task/first")).ExitCode);
        var freshRoot = Path.Combine(fixture.Root, "new state");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunAsync(["repo", "add", "example", "--path", fixture.Repository,
            "--pool-root", fixture.PoolRoot, "--max-slots", "2", "--state-root", freshRoot, "--json"], stdout, stderr);
        var result = new CliOutput(exitCode, stdout.ToString(), stderr.ToString());
        Assert.Equal("external_owner", result.ErrorCode);
        Assert.Single(Directory.EnumerateDirectories(fixture.PoolRoot));
    }

    [Fact]
    public async Task FailedCheckoutLeavesRealFilesAndAnOccupiedNeedsAttentionSlot()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 2);
        var hooks = Path.Combine(fixture.Root, "failure hooks");
        Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\nprintf candidate > hook-result.txt\nexit 23\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await fixture.GitAsync("config", "core.hooksPath", hooks);

        var result = await fixture.AcquireAsync("failed", "task/failed");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal("state_needs_attention", result.ErrorCode);
        var slotPath = Path.Combine(fixture.PoolRoot, "slot-01");
        Assert.True(File.Exists(Path.Combine(slotPath, ".git")));
        Assert.Equal("candidate", await File.ReadAllTextAsync(Path.Combine(slotPath, "hook-result.txt")));
        Assert.Contains("task/failed", await fixture.GitAsync("branch", "--list"));
        var before = WorkspaceTests.Snapshot(fixture.Root);
        Assert.Equal("state_needs_attention", (await fixture.AcquireAsync("failed", "task/failed")).ErrorCode);
        Assert.Equal("state_needs_attention", (await fixture.AcquireAsync("other", "task/other")).ErrorCode);
        var doctor = await fixture.CliAsync("doctor");
        Assert.Equal(4, doctor.ExitCode);
        var view = Assert.Single(doctor.Data.GetProperty("workspaces").EnumerateArray(),
            workspace => workspace.GetProperty("path").GetString() == slotPath);
        Assert.Equal("needs-attention", view.GetProperty("state").GetString());
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
    }

    [Fact]
    public async Task KilledAllocationProcessLeavesDurableInterruptedIntentWithoutDuplicateWriter()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 2);
        var hooks = Path.Combine(fixture.Root, "slow hooks");
        Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "post-checkout"),
            "#!/bin/sh\npwsh -NoProfile -NonInteractive -Command "
                + "'Set-Content -LiteralPath .hook-running -Value running; Start-Sleep -Seconds 20'\n",
            new UTF8Encoding(false));
        await fixture.GitAsync("config", "core.hooksPath", hooks);
        using var process = fixture.StartCli("acquire", "--repo", "example", "--base", "main",
            "--branch", "task/interrupted", "--task", "T017", "--owner", "owner", "--request-id", "interrupted");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timer = Stopwatch.StartNew();
        var marker = Path.Combine(fixture.PoolRoot, "slot-01", ".hook-running");
        try
        {
            while (!File.Exists(marker) && !process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(marker), $"Allocation did not reach its hook; exit={process.HasExited}");
            Assert.False(process.HasExited, "The allocation must still be running when it is interrupted.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(fixture.CatalogPath))!;
        Assert.Contains(catalog["slots"]![0]!["state"]!.GetValue<string>(), new[] { "creating", "needs-attention" });
        var before = WorkspaceTests.Snapshot(fixture.Root);
        var retry = await fixture.AcquireAsync("interrupted", "task/interrupted");
        Assert.Equal("idempotency_conflict", retry.ErrorCode);
        var identical = await fixture.CliAsync("acquire", "--repo", "example", "--base", "main",
            "--branch", "task/interrupted", "--task", "T017", "--owner", "owner", "--request-id", "interrupted");
        Assert.Equal("state_needs_attention", identical.ErrorCode);
        Assert.Equal(4, (await fixture.CliAsync("doctor")).ExitCode);
        Assert.Equal(before, WorkspaceTests.Snapshot(fixture.Root));
        Assert.Single(Directory.EnumerateDirectories(fixture.PoolRoot));
    }

    [Fact]
    public async Task ReadOnlyInventoryOfUnregisteredStateDoesNotCreateTheStateRoot()
    {
        using var fixture = await GitFixture.CreateAsync();
        Assert.Equal(0, (await fixture.CliAsync("list")).ExitCode);
        Assert.Equal(0, (await fixture.CliAsync("doctor")).ExitCode);
        Assert.False(Directory.Exists(fixture.StateRoot));
        var nongit = Path.Combine(fixture.Root, "not git");
        Directory.CreateDirectory(nongit);
        var registration = await fixture.CliAsync("repo", "add", "invalid", "--path", nongit,
            "--pool-root", fixture.PoolRoot, "--max-slots", "1");
        Assert.Equal("cannot_verify", registration.ErrorCode);
        Assert.False(Directory.Exists(fixture.StateRoot));
        Assert.False(Directory.Exists(fixture.PoolRoot));
    }

    [Fact]
    public async Task GitTimeoutIsBoundedAndPreservesTheUncertainCreation()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.RegisterAsync(maxSlots: 1);
        var hooks = Path.Combine(fixture.Root, "timeout hooks");
        Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "post-checkout"),
            "#!/bin/sh\npwsh -NoProfile -NonInteractive -Command "
                + "'Set-Content -LiteralPath timeout-result.txt -Value candidate -NoNewline; Start-Sleep -Seconds 45'\n",
            new UTF8Encoding(false));
        await fixture.GitAsync("config", "core.hooksPath", hooks);
        var timer = Stopwatch.StartNew();

        var result = await fixture.AcquireAsync("timeout", "task/timeout");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal("state_needs_attention", result.ErrorCode);
        Assert.True(result.Stderr.Contains("30-second", StringComparison.Ordinal), result.Stderr);
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(40));
        Assert.Equal("candidate", await File.ReadAllTextAsync(
            Path.Combine(fixture.PoolRoot, "slot-01", "timeout-result.txt")));
        Assert.Equal("state_needs_attention", (await fixture.AcquireAsync("timeout", "task/timeout")).ErrorCode);
        Assert.Single(Directory.EnumerateDirectories(fixture.PoolRoot));
    }

    private static async Task EditAsync(string path, Action<JsonNode> edit)
    {
        var state = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        edit(state);
        await File.WriteAllTextAsync(path, state.ToJsonString(JsonContract.Options));
    }
}

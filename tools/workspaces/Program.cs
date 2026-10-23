using System.CommandLine;
using System.Text.Json;

namespace AgentKit.Workspaces;

/// <summary>Defines CLI commands and keeps command results separate from human diagnostics.</summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        return RunAsync(args, Console.Out, Console.Error);
    }

    /// <summary>Executes one command using supplied output streams, also used by command-level tests.</summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var root = new RootCommand("Inventory Git worktrees and allocate fresh, bounded managed slots without deleting content.");
        var json = new Option<bool>("--json") { Description = "Write the version-1 JSON response to stdout.", Recursive = true };
        var stateRoot = new Option<string?>("--state-root")
        {
            Description = "Catalog root (default: LocalApplicationData\\AgentKit\\workspaces); keep outside source/pool.",
            Recursive = true
        };
        root.Options.Add(json);
        root.Options.Add(stateRoot);

        var repo = new Command("repo", "Register a trusted existing physical Git clone.");
        var add = new Command("add", "Register an absent/empty pool; an explicit capacity is required.");
        var name = new Argument<string>("name") { Description = "Local repository name." };
        var path = RequiredOption("--path", "Existing Git clone or linked worktree.");
        var poolRoot = RequiredOption("--pool-root", "Separate absent or empty directory for fresh slots.");
        var maxSlots = new Option<int>("--max-slots") { Description = "Positive total capacity, including retained slots.", Required = true };
        add.Arguments.Add(name);
        add.Options.Add(path);
        add.Options.Add(poolRoot);
        add.Options.Add(maxSlots);
        Bind(add, (manager, result, token) => manager.RegisterAsync(result.GetValue(name)!, result.GetValue(path)!,
            result.GetValue(poolRoot)!, result.GetValue(maxSlots), token));
        repo.Subcommands.Add(add);
        root.Subcommands.Add(repo);

        var list = new Command("list", "Observe every Git worktree of registered clones; no checkout/state writes.");
        var listRepo = new Option<string?>("--repo") { Description = "Filter by registered name; default is all registrations." };
        list.Options.Add(listRepo);
        Bind(list, (manager, result, token) => manager.ListAsync(result.GetValue(listRepo), token));
        root.Subcommands.Add(list);

        var inspect = new Command("inspect", "Observe one workspace by the stable ID returned from list/acquire.");
        var inspectId = new Argument<string>("workspace-id");
        inspect.Arguments.Add(inspectId);
        Bind(inspect, (manager, result, token) => manager.InspectAsync(result.GetValue(inspectId)!, token));
        root.Subcommands.Add(inspect);

        var acquire = new Command("acquire", "Create a new branch/worktree with a durable lease; never fetch, reuse or evict.");
        var acquireRepo = RequiredOption("--repo", "Registered repository name.");
        var baseRef = RequiredOption("--base", "Local ref or commit; resolved once, without fetching.");
        var branch = RequiredOption("--branch", "Explicit new task branch; existing branches are refused.");
        var task = RequiredOption("--task", "Caller-reported task label.");
        var owner = RequiredOption("--owner", "Caller-reported owner identity.");
        var request = RequiredOption("--request-id", "Idempotency key unique within this registered physical clone.");
        var session = new Option<string?>("--session") { Description = "Optional caller-reported session reference." };
        foreach (var option in new Option[] { acquireRepo, baseRef, branch, task, owner, request, session })
        {
            acquire.Options.Add(option);
        }

        Bind(acquire, (manager, result, token) => manager.AcquireAsync(result.GetValue(acquireRepo)!,
            new AcquisitionParameters(result.GetValue(baseRef)!, result.GetValue(branch)!, result.GetValue(task)!,
                result.GetValue(owner)!, result.GetValue(session)), result.GetValue(request)!, token));
        root.Subcommands.Add(acquire);

        var release = new Command("release", "End a lease and retain the candidate unchanged; capacity remains occupied.");
        var releaseId = new Argument<string>("workspace-id");
        var lease = RequiredOption("--lease", "Immutable lease ID returned by acquire.");
        var outcome = RequiredOption("--outcome", "Caller-reported candidate-ready, blocked, failed or stopped.");
        var checkpoint = new Option<string?>("--checkpoint") { Description = "Optional checkpoint file reference; no file is copied." };
        release.Arguments.Add(releaseId);
        release.Options.Add(lease);
        release.Options.Add(outcome);
        release.Options.Add(checkpoint);
        Bind(release, (manager, result, token) => manager.ReleaseAsync(result.GetValue(releaseId)!, result.GetValue(lease)!,
            result.GetValue(outcome)!, result.GetValue(checkpoint), token));
        root.Subcommands.Add(release);

        var doctor = new Command("doctor", "Read-only diagnostics for clone/pool/catalog mismatches; no automatic repair.");
        var doctorRepo = new Option<string?>("--repo") { Description = "Filter diagnostics to one registered repository." };
        doctor.Options.Add(doctorRepo);
        Bind(doctor, (manager, result, token) => manager.ListAsync(result.GetValue(doctorRepo), token));
        root.Subcommands.Add(doctor);

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            return await WriteAsync(Response.Failure("invalid_arguments",
                string.Join(Environment.NewLine, parsed.Errors.Select(parseError => parseError.Message)), 2),
                args.Any(argument => argument is "--json" or "--json=true"), output, error);
        }

        return await parsed.InvokeAsync(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false
        }, cancellationToken);

        // Use one error boundary so refused mutations still produce the documented JSON response.
        void Bind(Command command, Func<WorkspaceManager, ParseResult, CancellationToken, Task<Response>> action)
        {
            command.SetAction(async (result, token) =>
            {
                Response response;
                try
                {
                    var manager = new WorkspaceManager(new StateStore(result.GetValue(stateRoot) ?? DefaultStateRoot()), new Git());
                    response = await action(manager, result, token);
                }
                catch (WorkspaceException exception)
                {
                    response = Response.Failure(exception.Code, exception.Message, exception.ExitCode);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    response = Response.Failure("cannot_verify", exception.Message);
                }
                catch (ArgumentException exception)
                {
                    response = Response.Failure("invalid_arguments", exception.Message, 2);
                }
                catch (OperationCanceledException)
                {
                    response = Response.Failure("operation_cancelled",
                        "Operation was cancelled; any recorded creation intent/lease remains durable.");
                }

                return await WriteAsync(response, result.GetValue(json), output, error);
            });
        }
    }

    private static Option<string> RequiredOption(string name, string description)
    {
        return new Option<string>(name) { Description = description, Required = true };
    }

    private static string DefaultStateRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
        {
            throw new WorkspaceException("cannot_verify", "LocalApplicationData is unavailable; supply --state-root explicitly.");
        }

        return Path.Combine(local, "AgentKit", "workspaces");
    }

    private static async Task<int> WriteAsync(Response response, bool json, TextWriter output, TextWriter error)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonContract.Options));
        }
        else if (response.Data is not null)
        {
            switch (response.Data)
            {
                case Inventory inventory:
                    await output.WriteLineAsync($"{inventory.Repositories.Count} registered repositories; "
                        + $"{inventory.Workspaces.Count} observed/cataloged workspaces.");
                    foreach (var workspace in inventory.Workspaces)
                    {
                        await WriteWorkspaceAsync(workspace, output, error);
                    }

                    foreach (var diagnostic in inventory.Diagnostics)
                    {
                        await error.WriteLineAsync($"{diagnostic.Severity} [{diagnostic.Code}]: {diagnostic.Message}");
                    }
                    break;
                case WorkspaceView workspace:
                    await WriteWorkspaceAsync(workspace, output, error);
                    break;
                case RepositoryRegistration registration:
                    await output.WriteLineAsync($"Registered {registration.Name} ({registration.RepoId})");
                    await output.WriteLineAsync($"Clone: {registration.RepositoryPath}");
                    await output.WriteLineAsync($"Pool: {registration.PoolRoot}; max slots: {registration.MaxSlots}");
                    break;
                case AcquisitionResult acquisition:
                    await output.WriteLineAsync($"{acquisition.WorkspaceId}  {acquisition.State}  {acquisition.Path}");
                    await output.WriteLineAsync($"Lease: {acquisition.LeaseId}; request: {acquisition.RequestId}");
                    await output.WriteLineAsync($"Task: {acquisition.Task}; owner: {acquisition.Owner}");
                    await output.WriteLineAsync($"Branch: {acquisition.Branch}; base: {acquisition.BaseRef} ({acquisition.BaseCommit})");
                    break;
            }
        }

        if (response.Error is not null)
        {
            await error.WriteLineAsync($"error [{response.Error.Code}]: {response.Error.Message}");
        }

        foreach (var warning in response.Warnings)
        {
            await error.WriteLineAsync($"{warning.Severity} [{warning.Code}]: {warning.Message}");
        }

        return response.ExitCode;
    }

    private static async Task WriteWorkspaceAsync(WorkspaceView workspace, TextWriter output, TextWriter error)
    {
        await output.WriteLineAsync($"{workspace.WorkspaceId}  {workspace.State}  {workspace.Ownership}"
            + $"  {(workspace.IsPrimary ? "primary" : "linked")}  {workspace.Path}");
        await output.WriteLineAsync($"  Branch: {workspace.Branch ?? (workspace.Detached == true ? "(detached)" : "(unknown)")}; "
            + $"HEAD: {workspace.Head ?? "(unknown)"}; task: {workspace.Task ?? "(unknown)"}");
        await output.WriteLineAsync(workspace.Content is null ? "  Content: unknown"
            : $"  Tracked changes: {workspace.Content.TrackedChanges}; untracked entries: {workspace.Content.UntrackedEntries}; "
                + $"ignored entries: {workspace.Content.IgnoredEntries}");
        await output.WriteLineAsync($"  Blockers: {string.Join(", ", workspace.Blockers)}");

        if (workspace.LeaseId is not null)
        {
            await output.WriteLineAsync($"  Owner: {workspace.Owner}; lease: {workspace.LeaseId}; request: {workspace.RequestId}");
            await output.WriteLineAsync($"  Allocation base: {workspace.BaseRef} ({workspace.BaseCommit}); session: {workspace.SessionId ?? "(unknown)"}");
        }

        if (workspace.Checkpoint is not null)
        {
            await output.WriteLineAsync($"  Checkpoint: {workspace.Checkpoint.Path}; exists: {workspace.Checkpoint.Exists?.ToString() ?? "unknown"}");
        }

        await output.WriteLineAsync($"  Observed: {workspace.ObservedAt:O}; activity: {workspace.Activity}");
        foreach (var diagnostic in workspace.Diagnostics)
        {
            await error.WriteLineAsync($"{diagnostic.Severity} [{diagnostic.Code}]: {diagnostic.Message}");
        }
    }
}

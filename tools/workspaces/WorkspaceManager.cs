using System.Text.RegularExpressions;

namespace AgentKit.Workspaces;

/// <summary>
/// Registers trusted clones, observes their worktrees, and manages fresh-slot acquisition and retained release.
/// Only cataloged, identity-checked slots are owned; external worktrees are always report-only.
/// </summary>
internal sealed class WorkspaceManager(StateStore store, Git git)
{
    private static readonly Diagnostic[] ObservationLimits =
    [
        new("activity_unknown", "No worker/process observer is integrated; leases do not prove writer activity or absence."),
        new("session_metadata_unavailable", "Host session/automation coverage is unavailable; supplied session IDs are caller-reported."),
        new("remote_freshness_unknown", "Only local Git evidence is used; no fetch or server freshness check is performed."),
        new("cleanup_deferred", "Recycling, cleanup and removal are not implemented; retained slots continue to consume capacity.")
    ];

    /// <summary>Registers an existing clone and a separate empty pool; an identical registration is safe to retry.</summary>
    internal async Task<Response> RegisterAsync(string name, string path, string poolRoot, int maxSlots,
        CancellationToken cancellationToken)
    {
        RequireText(name, "name");
        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$") || maxSlots <= 0)
        {
            throw new WorkspaceException("invalid_arguments",
                "Repository names use letters, numbers, '.', '_' or '-'; --max-slots must be explicitly positive.", 2);
        }

        path = Paths.Canonical(RequireText(path, "path"), mustExist: true);
        poolRoot = Paths.Canonical(RequireText(poolRoot, "pool-root"));
        var common = await git.CommonDirectoryAsync(path, cancellationToken);
        var worktrees = await git.WorktreesAsync(path, cancellationToken);
        if (worktrees[0].Bare)
        {
            throw new WorkspaceException("unsupported_repository_mode", "Register a non-bare Git clone.", 3);
        }

        // Compare physical clone/pool/state paths before creating any tool-owned files.
        var primary = Paths.Canonical(worktrees[0].Path, mustExist: true);
        foreach (var worktree in worktrees)
        {
            if (Paths.Overlap(store.Root, worktree.Path))
            {
                throw new WorkspaceException("invalid_path",
                    "The state root must be separate from every checkout of this clone.", 2);
            }
        }

        if (Paths.Overlap(poolRoot, common) || Paths.Overlap(store.Root, common) || Paths.Overlap(poolRoot, store.Root))
        {
            throw new WorkspaceException("invalid_path", "Pool, Git administration and state paths must not overlap.", 2);
        }

        using var operationLock = await store.LockAsync(cancellationToken);
        var registry = store.ReadRegistry();
        var existing = registry.Repositories.FirstOrDefault(repo => string.Equals(repo.Name, name,
            StringComparison.OrdinalIgnoreCase) || Paths.Equal(repo.CommonDirectory, common));
        if (existing is not null)
        {
            if (!string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)
                || !Paths.Equal(existing.CommonDirectory, common) || !Paths.Equal(existing.PoolRoot, poolRoot)
                || existing.MaxSlots != maxSlots)
            {
                throw new WorkspaceException("registration_conflict",
                    "This name or physical clone is already registered with different settings.", 3);
            }

            store.CheckPool(existing, store.ReadCatalog(existing));
            return Response.Success(existing, [new("already_registered", "The identical registration already exists.")]);
        }

        foreach (var repo in registry.Repositories)
        {
            if (Paths.Overlap(repo.PoolRoot, poolRoot) || Paths.Overlap(repo.RepositoryPath, poolRoot)
                || Paths.Overlap(repo.CommonDirectory, poolRoot) || Paths.Overlap(repo.PoolRoot, primary))
            {
                throw new WorkspaceException("registration_conflict", $"Pool/clone paths overlap registration '{repo.Name}'.", 3);
            }
        }

        if (Directory.Exists(poolRoot) && Directory.EnumerateFileSystemEntries(poolRoot).Any())
        {
            throw new WorkspaceException("external_owner",
                "A new pool root must be absent or empty; existing contents and lost pool state are never adopted.", 3);
        }

        if (worktrees.Any(worktree => Paths.Overlap(poolRoot, worktree.Path)))
        {
            throw new WorkspaceException("invalid_path", "The pool must be separate from every checkout of this clone.", 2);
        }

        var registration = new RepositoryRegistration(name, Paths.RepositoryId(common), primary, common, poolRoot,
            Guid.NewGuid().ToString("N"), maxSlots);
        var catalog = new Catalog
        {
            SchemaVersion = 1,
            RepoId = registration.RepoId,
            PoolId = registration.PoolId,
            Status = "initializing",
            Slots = []
        };

        // Persist initializing ownership first; interruption must not leave an apparently reusable pool.
        registry.Repositories.Add(registration);
        store.SaveRegistry(registry);
        store.SaveCatalog(registration, catalog);

        Directory.CreateDirectory(poolRoot);
        StateStore.WriteAtomic(Path.Combine(poolRoot, StateStore.MarkerName),
            new PoolMarker(1, registration.RepoId, registration.PoolId, common, poolRoot));

        catalog.Status = "ready";
        store.SaveCatalog(registration, catalog);

        return Response.Success(registration,
            [new("trusted_repository_required", "Allocation runs Git checkout, which can invoke this repository's hooks and filters.")]);
    }

    /// <summary>Creates one fresh branch/worktree with durable creation intent, or replays the exact prior request.</summary>
    internal async Task<Response> AcquireAsync(string repository, AcquisitionParameters parameters, string requestId,
        CancellationToken cancellationToken)
    {
        RequireText(repository, "repo");
        RequireText(parameters.BaseRef, "base");
        RequireText(parameters.Branch, "branch");
        RequireText(parameters.Task, "task");
        RequireText(parameters.Owner, "owner");
        RequireText(requestId, "request-id");
        if (parameters.Session is not null)
        {
            RequireText(parameters.Session, "session");
        }

        using var operationLock = await store.LockAsync(cancellationToken);
        var repo = SelectRepositories(store.ReadRegistry(), repository).Single();
        var catalog = store.ReadCatalog(repo);
        var previous = catalog.Slots.SingleOrDefault(slot => slot.RequestId == requestId);
        if (previous is not null && previous.Parameters != parameters)
        {
            throw new WorkspaceException("idempotency_conflict",
                "This request ID was already used with different acquisition parameters.", 3);
        }

        // Reconcile recorded ownership before a retry or a new allocation; no slot is evicted or reused.
        var worktrees = await ValidateOwnedPoolAsync(repo, catalog, cancellationToken);
        if (previous is not null)
        {
            return Response.Success(AcquisitionResult.From(repo, previous, replayed: true), ObservationLimits);
        }

        if (catalog.Slots.Count >= repo.MaxSlots)
        {
            throw new WorkspaceException("pool_exhausted",
                $"All {repo.MaxSlots} slots are occupied, including retained candidates; nothing was evicted.", 3);
        }

        if (await git.SparseAsync(repo.RepositoryPath, cancellationToken))
        {
            throw new WorkspaceException("unsupported_repository_mode",
                "Managed allocation from sparse-checkout repositories is not supported.", 3);
        }

        var operations = await git.OperationsAsync(repo.RepositoryPath, cancellationToken);
        if (operations.Count > 0)
        {
            throw new WorkspaceException("state_needs_attention",
                $"The source checkout has in-progress Git state: {string.Join(", ", operations)}.");
        }

        // Resolve the requested local base exactly once, and never force or reuse an existing branch.
        var branchCheck = await git.RunAsync(repo.RepositoryPath, cancellationToken,
            "check-ref-format", "--branch", parameters.Branch);
        if (branchCheck.ExitCode != 0 || branchCheck.Output.TrimEnd('\r', '\n') != parameters.Branch)
        {
            throw new WorkspaceException("invalid_branch", "Supply a valid literal new branch name.", 2);
        }

        var existingBranch = await git.RunAsync(repo.RepositoryPath, cancellationToken,
            "show-ref", "--verify", "--quiet", "--", "refs/heads/" + parameters.Branch);
        if (existingBranch.ExitCode == 0)
        {
            throw new WorkspaceException("branch_exists", "The requested branch already exists; branch reuse is not supported.", 3);
        }

        if (existingBranch.ExitCode != 1)
        {
            throw new WorkspaceException("cannot_verify", $"Cannot verify branch absence: {existingBranch.Error.Trim()}");
        }

        var resolved = await git.RunAsync(repo.RepositoryPath, cancellationToken,
            "rev-parse", "--verify", "--end-of-options", parameters.BaseRef + "^{commit}");
        if (resolved.ExitCode != 0)
        {
            throw new WorkspaceException("invalid_ref", $"The requested base does not resolve locally: {resolved.Error.Trim()}", 2);
        }

        var commit = resolved.Output.TrimEnd('\r', '\n');
        if (!Regex.IsMatch(commit, "^(?:[0-9a-f]{40}|[0-9a-f]{64})$"))
        {
            throw new WorkspaceException("cannot_verify", "Git returned an unexpected commit identity.");
        }

        var tree = await git.RequiredAsync(repo.RepositoryPath, cancellationToken, "ls-tree", "-r", "-z", commit);
        if (tree.Split('\0').Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)
            || entry.EndsWith("\t.gitmodules", StringComparison.Ordinal)))
        {
            throw new WorkspaceException("unsupported_repository_mode", "Managed allocation of submodule bases is not supported.", 3);
        }

        var index = Enumerable.Range(1, repo.MaxSlots).First(number => catalog.Slots.All(slot => slot.Index != number));
        var slot = new Slot
        {
            Index = index,
            WorkspaceId = StateStore.WorkspaceId(repo, index),
            Path = StateStore.SlotPath(repo, index),
            State = "creating",
            RequestId = requestId,
            LeaseId = "lease-" + Guid.NewGuid().ToString("N"),
            Parameters = parameters,
            BaseCommit = commit,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (Path.Exists(slot.Path) || worktrees.Any(worktree => Paths.Equal(worktree.Path, slot.Path)))
        {
            throw StateStore.Attention($"The next slot path is already occupied: {slot.Path}");
        }

        // Git and the catalog cannot commit atomically. Persist intent before allowing Git to create anything.
        catalog.Slots.Add(slot);
        store.SaveCatalog(repo, catalog);
        try
        {
            if (Path.Exists(slot.Path))
            {
                throw StateStore.Attention($"The slot path became occupied: {slot.Path}");
            }

            await git.RequiredAsync(repo.RepositoryPath, cancellationToken, "worktree", "add", "--no-track",
                "-b", parameters.Branch, "--", slot.Path, commit);

            var created = await git.WorktreesAsync(repo.RepositoryPath, cancellationToken);
            await CheckSlotAsync(repo, slot, created, cancellationToken);
            var actual = created.Single(worktree => Paths.Equal(worktree.Path, slot.Path));
            if (actual.Head != commit || actual.Branch != "refs/heads/" + parameters.Branch || actual.Detached)
            {
                throw StateStore.Attention("Created worktree HEAD/branch differs from the recorded allocation intent.");
            }

            slot.State = "leased";
            store.SaveCatalog(repo, catalog);
        }
        catch (Exception exception) when (exception is WorkspaceException or IOException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            slot.State = "needs-attention";
            slot.LastError = exception.Message;
            store.SaveCatalog(repo, catalog);
            throw StateStore.Attention($"Allocation did not finish safely; files and refs are preserved. {exception.Message}");
        }

        return Response.Success(AcquisitionResult.From(repo, slot), ObservationLimits);
    }

    /// <summary>Ends the matching lease and retains all content; an identical release retry cannot rewrite the handoff.</summary>
    internal async Task<Response> ReleaseAsync(string workspaceId, string leaseId, string outcome, string? checkpoint,
        CancellationToken cancellationToken)
    {
        RequireText(workspaceId, "workspace-id");
        RequireText(leaseId, "lease");
        if (outcome is not ("candidate-ready" or "blocked" or "failed" or "stopped"))
        {
            throw new WorkspaceException("invalid_outcome", "Outcome must be candidate-ready, blocked, failed or stopped.", 2);
        }

        checkpoint = checkpoint is null ? null : Path.GetFullPath(RequireText(checkpoint, "checkpoint"));
        using var operationLock = await store.LockAsync(cancellationToken);
        var registry = store.ReadRegistry();
        foreach (var repo in registry.Repositories)
        {
            var catalog = store.ReadCatalog(repo);
            var slot = catalog.Slots.SingleOrDefault(candidate => candidate.WorkspaceId == workspaceId);
            if (slot is null)
            {
                continue;
            }

            if (slot.LeaseId != leaseId)
            {
                throw new WorkspaceException("stale_lease", "This lease does not own the recorded acquisition.", 3);
            }

            if (slot.State == "retained")
            {
                if (slot.Outcome != outcome || slot.CheckpointPath != checkpoint)
                {
                    throw new WorkspaceException("stale_lease",
                        "The lease has already ended; a retry cannot change its recorded handoff.", 3);
                }

                return Response.Success(AcquisitionResult.From(repo, slot, replayed: true), ObservationLimits);
            }

            if (slot.State != "leased")
            {
                throw StateStore.Attention("An interrupted or uncertain acquisition cannot be released as a healthy lease.");
            }

            // Release changes only the catalog; the caller is responsible for stopping its writer.
            await ValidateOwnedPoolAsync(repo, catalog, cancellationToken);
            slot.State = "retained";
            slot.ReleasedAt = DateTimeOffset.UtcNow;
            slot.Outcome = outcome;
            slot.CheckpointPath = checkpoint;
            store.SaveCatalog(repo, catalog);

            var warnings = ObservationLimits.ToList();
            if (outcome == "candidate-ready")
            {
                warnings.Add(new("outcome_unverified", "candidate-ready is caller-reported, not an independent validation verdict."));
            }

            if (checkpoint is not null)
            {
                ObserveCheckpoint(checkpoint, warnings);
            }

            return Response.Success(AcquisitionResult.From(repo, slot), warnings);
        }

        var inventory = await ReadInventoryAsync(registry, null, cancellationToken);
        if (inventory.Workspaces.Any(workspace => workspace.WorkspaceId == workspaceId))
        {
            throw new WorkspaceException("external_owner", "This worktree is report-only and is not owned by workspaces.", 3);
        }

        throw new WorkspaceException("workspace_not_found", "No registered workspace has that ID.", 2);
    }

    /// <summary>Returns read-only inventory and diagnostics for list or doctor, preserving any partial observations.</summary>
    internal async Task<Response> ListAsync(string? repository, CancellationToken cancellationToken)
    {
        var inventory = await ReadInventoryAsync(store.ReadRegistry(), repository, cancellationToken);
        return InventoryResponse(inventory, inventory);
    }

    /// <summary>Returns one workspace observation; incomplete inventory is not mistaken for an unknown workspace ID.</summary>
    internal async Task<Response> InspectAsync(string workspaceId, CancellationToken cancellationToken)
    {
        RequireText(workspaceId, "workspace-id");
        var inventory = await ReadInventoryAsync(store.ReadRegistry(), null, cancellationToken);
        var workspace = inventory.Workspaces.SingleOrDefault(workspace => workspace.WorkspaceId == workspaceId);
        if (workspace is null)
        {
            if (inventory.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
            {
                return InventoryResponse(inventory, inventory);
            }

            throw new WorkspaceException("workspace_not_found", "No registered workspace has that ID.", 2);
        }

        return InventoryResponse(inventory, workspace);
    }

    /// <summary>Joins Git's worktree inventory with tool metadata without inferring ownership from directory names.</summary>
    private async Task<Inventory> ReadInventoryAsync(Registry registry, string? repository,
        CancellationToken cancellationToken)
    {
        var registrations = SelectRepositories(registry, repository);
        var workspaces = new List<WorkspaceView>();
        var diagnostics = new List<Diagnostic>();

        foreach (var repo in registrations)
        {
            Catalog? catalog = null;
            var verified = false;

            // Keep metadata and Git failures separate so an unreadable catalog does not hide external worktrees.
            try
            {
                catalog = store.ReadCatalog(repo);
                store.CheckPool(repo, catalog);
                verified = true;
            }
            catch (Exception exception) when (IsObservationError(exception))
            {
                diagnostics.Add(ObservationError(repo.Name, exception));
            }

            List<GitWorktree> actual = [];
            try
            {
                await CheckRepositoryAsync(repo, cancellationToken);
                actual = await git.WorktreesAsync(repo.RepositoryPath, cancellationToken);
            }
            catch (Exception exception) when (IsObservationError(exception))
            {
                diagnostics.Add(ObservationError(repo.Name, exception));
                verified = false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < actual.Count; index++)
            {
                var worktree = actual[index];
                var slot = catalog?.Slots.SingleOrDefault(slot => Paths.Equal(slot.Path, worktree.Path));
                if (slot is not null)
                {
                    seen.Add(slot.WorkspaceId);
                }

                workspaces.Add(await ObserveAsync(repo, worktree, slot, index == 0, verified, cancellationToken));
            }

            // Missing Git registrations still leave durable slot records and consume pool capacity.
            foreach (var slot in catalog?.Slots.Where(slot => !seen.Contains(slot.WorkspaceId)) ?? [])
            {
                workspaces.Add(MissingWorkspace(repo, slot));
            }
        }

        return new Inventory(registrations, workspaces, diagnostics, DateTimeOffset.UtcNow);
    }

    /// <summary>Attaches observed contents and ownership evidence to one Git-reported checkout.</summary>
    private async Task<WorkspaceView> ObserveAsync(RepositoryRegistration repo, GitWorktree worktree, Slot? slot,
        bool primary, bool poolVerified, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        ContentObservation? content = null;
        List<string>? operations = null;
        bool? sparse = null;
        var path = worktree.Path;
        var identityVerified = false;

        try
        {
            path = Paths.Canonical(worktree.Path, mustExist: true);
            if (!Paths.Equal(path, worktree.Path) && slot is not null)
            {
                throw StateStore.Attention($"Managed path identity changed: {worktree.Path}");
            }

            if (!Paths.Equal(await git.CommonDirectoryAsync(path, cancellationToken), repo.CommonDirectory))
            {
                throw StateStore.Attention($"Worktree belongs to a different physical clone: {path}");
            }

            identityVerified = true;
            if (!worktree.Bare)
            {
                content = await git.ContentAsync(path, cancellationToken);
                operations = await git.OperationsAsync(path, cancellationToken);
                sparse = await git.SparseAsync(path, cancellationToken);
            }
        }
        catch (Exception exception) when (IsObservationError(exception))
        {
            diagnostics.Add(ObservationError(path, exception));
        }

        if (slot?.State is "creating" or "needs-attention")
        {
            diagnostics.Add(new("state_needs_attention",
                $"Allocation is interrupted/uncertain; no automatic reconciliation is attempted. {slot.LastError}", "error"));
        }

        var owned = slot is not null && poolVerified && identityVerified && !primary;
        var ownership = "unknown";
        if (owned)
        {
            ownership = "managed";
        }
        else if (slot is null && poolVerified)
        {
            ownership = "external";
        }

        var state = slot?.State ?? "external";
        if (state == "creating")
        {
            state = "needs-attention";
        }

        var blockers = Blockers(slot, owned, content, operations, worktree.Locked);
        var checkpoint = slot?.CheckpointPath is not null ? ObserveCheckpoint(slot.CheckpointPath, diagnostics) : null;

        return new WorkspaceView
        {
            WorkspaceId = slot?.WorkspaceId ?? Paths.ExternalId(repo.CommonDirectory, path),
            RepoId = repo.RepoId,
            Repository = repo.Name,
            Path = path,
            Ownership = ownership,
            Manager = owned ? "agentkit" : "unknown",
            IsPrimary = primary,
            Branch = worktree.Branch,
            Detached = worktree.Detached,
            Head = worktree.Head,
            State = state,
            Task = slot?.Parameters.Task,
            Owner = slot?.Parameters.Owner,
            SessionId = slot?.Parameters.Session,
            LeaseId = slot?.LeaseId,
            RequestId = slot?.RequestId,
            BaseRef = slot?.Parameters.BaseRef,
            BaseCommit = slot?.BaseCommit,
            Outcome = slot?.Outcome,
            Activity = "unknown",
            Content = content,
            SparseCheckout = sparse,
            GitLocked = worktree.Locked,
            LockReason = worktree.LockReason,
            Prunable = worktree.Prunable,
            InProgressOperations = operations,
            Checkpoint = checkpoint,
            Blockers = blockers,
            Diagnostics = diagnostics,
            ObservedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Reports an orphaned catalog record without treating the missing checkout as an available slot.</summary>
    private static WorkspaceView MissingWorkspace(RepositoryRegistration repo, Slot slot)
    {
        return new WorkspaceView
        {
            WorkspaceId = slot.WorkspaceId,
            RepoId = repo.RepoId,
            Repository = repo.Name,
            Path = slot.Path,
            Ownership = "unknown",
            Manager = "unknown",
            IsPrimary = false,
            State = "needs-attention",
            Task = slot.Parameters.Task,
            Owner = slot.Parameters.Owner,
            SessionId = slot.Parameters.Session,
            LeaseId = slot.LeaseId,
            RequestId = slot.RequestId,
            BaseRef = slot.Parameters.BaseRef,
            BaseCommit = slot.BaseCommit,
            Outcome = slot.Outcome,
            Activity = "unknown",
            Blockers = ["state_needs_attention", "cannot_verify", "cleanup_deferred", "activity_unknown"],
            Diagnostics = [new("state_needs_attention",
                $"Cataloged slot is absent from Git's worktree inventory. {slot.LastError}", "error")],
            ObservedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Rechecks pool and clone identity under the mutation lock; uncertainty blocks further allocation.</summary>
    private async Task<List<GitWorktree>> ValidateOwnedPoolAsync(RepositoryRegistration repo, Catalog catalog,
        CancellationToken cancellationToken)
    {
        store.CheckPool(repo, catalog);
        await CheckRepositoryAsync(repo, cancellationToken);
        var worktrees = await git.WorktreesAsync(repo.RepositoryPath, cancellationToken);
        foreach (var slot in catalog.Slots)
        {
            if (slot.State is "creating" or "needs-attention")
            {
                throw StateStore.Attention($"Slot '{slot.WorkspaceId}' has an interrupted operation; use doctor to inspect.");
            }

            await CheckSlotAsync(repo, slot, worktrees, cancellationToken);
        }

        return worktrees;
    }

    private async Task CheckRepositoryAsync(RepositoryRegistration repo, CancellationToken cancellationToken)
    {
        if (!Paths.Equal(Paths.Canonical(repo.RepositoryPath, mustExist: true), repo.RepositoryPath)
            || !Paths.Equal(await git.CommonDirectoryAsync(repo.RepositoryPath, cancellationToken), repo.CommonDirectory))
        {
            throw StateStore.Attention($"Registered physical clone identity changed for '{repo.Name}'.");
        }
    }

    /// <summary>Requires the recorded path to remain a linked worktree of this physical clone, never its primary checkout.</summary>
    private async Task CheckSlotAsync(RepositoryRegistration repo, Slot slot, List<GitWorktree> worktrees,
        CancellationToken cancellationToken)
    {
        var index = worktrees.FindIndex(worktree => Paths.Equal(worktree.Path, slot.Path));
        if (index <= 0 || !Paths.Equal(Paths.Canonical(slot.Path, mustExist: true), slot.Path)
            || !Paths.Equal(await git.CommonDirectoryAsync(slot.Path, cancellationToken), repo.CommonDirectory))
        {
            throw StateStore.Attention($"Cannot verify owned linked worktree '{slot.WorkspaceId}' at '{slot.Path}'.");
        }
    }

    private static List<RepositoryRegistration> SelectRepositories(Registry registry, string? name)
    {
        if (name is null)
        {
            return registry.Repositories;
        }

        var repo = registry.Repositories.SingleOrDefault(repo => string.Equals(repo.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkspaceException("repo_not_found", $"Repository '{name}' is not registered.", 2);
        return [repo];
    }

    private static Response InventoryResponse(Inventory inventory, object data)
    {
        var all = inventory.Diagnostics.Concat(inventory.Workspaces.SelectMany(workspace => workspace.Diagnostics)).ToArray();
        var warnings = ObservationLimits.Concat(all.Where(diagnostic => diagnostic.Severity != "error")).ToArray();
        var errors = all.Where(diagnostic => diagnostic.Severity == "error").ToArray();
        if (errors.Length == 0)
        {
            return Response.Success(data, warnings);
        }

        var code = errors.Any(error => error.Code == "state_needs_attention") ? "state_needs_attention" : "cannot_verify";
        return Response.Failure(code, "Inventory is incomplete or contains uncertain state; inspect the returned diagnostics.",
            data: data, warnings: warnings);
    }

    private static List<string> Blockers(Slot? slot, bool owned, ContentObservation? content,
        IReadOnlyList<string>? operations, bool gitLocked)
    {
        var blockers = new List<string> { "cleanup_deferred", "activity_unknown" };
        if (!owned)
        {
            blockers.Add(slot is null ? "external_owner" : "cannot_verify");
        }

        if (slot?.State == "leased")
        {
            blockers.Add("active_lease");
        }
        if (slot?.State is "creating" or "needs-attention")
        {
            blockers.Add("state_needs_attention");
        }
        if (slot?.State == "retained")
        {
            blockers.Add("retained_candidate");
        }

        if (content is null)
        {
            blockers.Add("cannot_verify_content");
        }
        else
        {
            if (content.TrackedChanges > 0)
            {
                blockers.Add("dirty_workspace");
            }

            if (content.UntrackedEntries > 0)
            {
                blockers.Add("untracked_content");
            }

            if (content.IgnoredEntries > 0)
            {
                blockers.Add("ignored_content");
            }
        }

        if (operations?.Count > 0)
        {
            blockers.Add("git_operation_in_progress");
        }
        if (gitLocked)
        {
            blockers.Add("git_worktree_locked");
        }

        return blockers;
    }

    /// <summary>Reports reference metadata only; checkpoint content and candidate freshness belong to the handoff helpers.</summary>
    private static CheckpointObservation ObserveCheckpoint(string path, List<Diagnostic> diagnostics)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                diagnostics.Add(new("checkpoint_unavailable", $"Checkpoint is a directory, not a file: {path}"));
                return new CheckpointObservation(path, null, null, "Reference only; content has not been validated.");
            }

            return new CheckpointObservation(path, true, File.GetLastWriteTimeUtc(path),
                "Filesystem metadata only; candidate coverage and content have not been validated.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            diagnostics.Add(new("checkpoint_unavailable", $"Checkpoint reference is missing: {path}"));
            return new CheckpointObservation(path, false, null, "Missing reference; no persistence claim.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("checkpoint_unavailable", $"Cannot observe checkpoint '{path}': {exception.Message}"));
            return new CheckpointObservation(path, null, null, "Reference could not be inspected.");
        }
    }

    private static bool IsObservationError(Exception exception)
    {
        return exception is WorkspaceException or IOException or UnauthorizedAccessException;
    }

    private static Diagnostic ObservationError(string context, Exception exception)
    {
        return new Diagnostic(exception is WorkspaceException error ? error.Code : "cannot_verify",
            $"{context}: {exception.Message}", "error");
    }

    private static string RequireText(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new WorkspaceException("invalid_arguments", $"'{option}' must be nonempty text without control characters.", 2);
        }

        return value;
    }
}

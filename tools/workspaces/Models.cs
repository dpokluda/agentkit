using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentKit.Workspaces;

/// <summary>Shared serialization settings for the versioned catalog and command responses.</summary>
internal static class JsonContract
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
}

/// <summary>A stable error or warning code with a human explanation; warnings can accompany a successful command.</summary>
internal sealed record Diagnostic(string Code, string Message, string Severity = "warning");

/// <summary>The JSON command envelope. ExitCode controls the process but is not serialized into its data.</summary>
internal sealed record Response(bool Ok, object? Data, Diagnostic? Error, IReadOnlyList<Diagnostic> Warnings)
{
    public int SchemaVersion => 1;

    [JsonIgnore]
    public int ExitCode { get; init; }

    internal static Response Success(object data, IReadOnlyList<Diagnostic>? warnings = null)
    {
        return new Response(true, data, null, warnings ?? []);
    }

    internal static Response Failure(string code, string message, int exitCode = 4, object? data = null,
        IReadOnlyList<Diagnostic>? warnings = null)
    {
        return new Response(false, data, new Diagnostic(code, message, "error"), warnings ?? []) { ExitCode = exitCode };
    }
}

/// <summary>An expected command refusal or observation failure, translated into the JSON error contract.</summary>
internal sealed class WorkspaceException(string code, string message, int exitCode = 4) : Exception(message)
{
    internal string Code { get; } = code;
    internal int ExitCode { get; } = exitCode;
}

/// <summary>The state-root index binding local names to physical clones and their separately owned pools.</summary>
internal sealed class Registry
{
    public required int SchemaVersion { get; init; }
    public required List<RepositoryRegistration> Repositories { get; init; }
}

/// <summary>
/// One trusted clone and its fixed pool configuration. CommonDirectory identifies the clone, not its remote URL.
/// PoolId distinguishes this registration's ownership from a previous pool at the same path.
/// </summary>
internal sealed record RepositoryRegistration(
    string Name,
    string RepoId,
    string RepositoryPath,
    string CommonDirectory,
    string PoolRoot,
    string PoolId,
    int MaxSlots);

/// <summary>
/// Durable slot records for one registered pool. An initializing catalog signals interrupted registration,
/// and all recorded slots count against capacity until explicitly reconciled.
/// </summary>
internal sealed class Catalog
{
    public required int SchemaVersion { get; init; }
    public required string RepoId { get; init; }
    public required string PoolId { get; init; }
    public required string Status { get; set; }
    public required List<Slot> Slots { get; init; }
}

/// <summary>The pool-root ownership record; a matching directory name alone never establishes tool ownership.</summary>
internal sealed record PoolMarker(int SchemaVersion, string RepoId, string PoolId, string CommonDirectory, string PoolRoot);

/// <summary>The caller's acquisition request; retries must match every field, even if a base ref later moves.</summary>
internal sealed record AcquisitionParameters(string BaseRef, string Branch, string Task, string Owner, string? Session);

/// <summary>
/// A stable slot and its immutable acquisition/lease identity. Creation intent is saved before Git runs;
/// success becomes leased, release becomes retained, and uncertain creation requires manual attention.
/// </summary>
internal sealed class Slot
{
    public required int Index { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Path { get; init; }
    public required string State { get; set; }
    public required string RequestId { get; init; }
    public required string LeaseId { get; init; }

    public required AcquisitionParameters Parameters { get; init; }
    public required string BaseCommit { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public string? LastError { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? Outcome { get; set; }
    public string? CheckpointPath { get; set; }
}

/// <summary>
/// The acquisition or release response, projected from the durable slot without exposing the catalog layout.
/// Replayed identifies an idempotent retry; Outcome is caller-reported, not an independent test verdict.
/// </summary>
internal sealed record AcquisitionResult(
    string WorkspaceId,
    string RepoId,
    string Path,
    string LeaseId,
    string RequestId,
    string Owner,
    string Task,
    string? SessionId,
    string BaseRef,
    string BaseCommit,
    string Branch,
    string State,
    bool Replayed,
    string? Outcome,
    string? CheckpointPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt)
{
    internal static AcquisitionResult From(RepositoryRegistration repo, Slot slot, bool replayed = false)
    {
        return new AcquisitionResult(
            WorkspaceId: slot.WorkspaceId, RepoId: repo.RepoId, Path: slot.Path,
            LeaseId: slot.LeaseId, RequestId: slot.RequestId,
            Owner: slot.Parameters.Owner, Task: slot.Parameters.Task, SessionId: slot.Parameters.Session,
            BaseRef: slot.Parameters.BaseRef, BaseCommit: slot.BaseCommit, Branch: slot.Parameters.Branch,
            State: slot.State, Replayed: replayed, Outcome: slot.Outcome, CheckpointPath: slot.CheckpointPath,
            CreatedAt: slot.CreatedAt, ReleasedAt: slot.ReleasedAt);
    }
}

/// <summary>One parsed Git worktree record, before catalog ownership and content observations are attached.</summary>
internal sealed class GitWorktree(string path)
{
    internal string Path { get; } = path;
    internal string? Head { get; set; }
    internal string? Branch { get; set; }
    internal bool Detached { get; set; }
    internal bool Bare { get; set; }

    internal bool Locked { get; set; }
    internal string? LockReason { get; set; }
    internal bool Prunable { get; set; }
}

/// <summary>Porcelain status entries, not a recursive file inventory or proof that ignored data is disposable.</summary>
internal sealed record ContentObservation(
    int TrackedChanges,
    int UntrackedEntries,
    int IgnoredEntries,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> UntrackedPaths,
    IReadOnlyList<string> IgnoredPaths,
    string Coverage);

/// <summary>Filesystem evidence about a checkpoint reference; it does not validate the note or its candidate coverage.</summary>
internal sealed record CheckpointObservation(
    string Path,
    bool? Exists,
    DateTimeOffset? LastWriteTime,
    string Evidence);

/// <summary>
/// One inventory observation combining Git facts with verified catalog metadata.
/// Null fields remain unknown; managed ownership requires both clone and pool identity checks.
/// </summary>
internal sealed class WorkspaceView
{
    public required string WorkspaceId { get; init; }
    public required string RepoId { get; init; }
    public required string Repository { get; init; }
    public required string Path { get; init; }
    public required string Ownership { get; init; }
    public required string Manager { get; init; }
    public required bool IsPrimary { get; init; }
    public string? Branch { get; init; }
    public bool? Detached { get; init; }
    public string? Head { get; init; }
    public required string State { get; init; }

    public string? Task { get; init; }
    public string? Owner { get; init; }
    public string? SessionId { get; init; }
    public string? LeaseId { get; init; }
    public string? RequestId { get; init; }
    public string? BaseRef { get; init; }
    public string? BaseCommit { get; init; }
    public string? Outcome { get; init; }

    public required string Activity { get; init; }
    public ContentObservation? Content { get; init; }
    public bool? SparseCheckout { get; init; }
    public bool? GitLocked { get; init; }
    public string? LockReason { get; init; }
    public bool? Prunable { get; init; }
    public IReadOnlyList<string>? InProgressOperations { get; init; }
    public CheckpointObservation? Checkpoint { get; init; }

    public required IReadOnlyList<string> Blockers { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>The selected registrations and their observations, including partial results when some resources cannot be read.</summary>
internal sealed record Inventory(
    IReadOnlyList<RepositoryRegistration> Repositories,
    IReadOnlyList<WorkspaceView> Workspaces,
    IReadOnlyList<Diagnostic> Diagnostics,
    DateTimeOffset ObservedAt);

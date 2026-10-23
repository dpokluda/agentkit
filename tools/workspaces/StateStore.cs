using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AgentKit.Workspaces;

/// <summary>
/// Persists the registration index and per-clone catalogs outside worker checkouts.
/// Callers hold one state-root lock for mutations; reads never create or repair state.
/// </summary>
internal sealed class StateStore
{
    internal const string MarkerName = ".agentkit-workspaces.json";

    internal StateStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new WorkspaceException("invalid_path", "Supply a nonempty state-root directory.", 2);
        }

        Root = Paths.Canonical(root);
    }

    internal string Root { get; }
    private string RegistryPath => Path.Combine(Root, "repositories.json");

    /// <summary>Acquires the cooperating-writer lock; disposing the returned stream releases it.</summary>
    internal async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var timer = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(Root, "mutation.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    throw new WorkspaceException("state_busy", "Another mutation holds the state lock; retry later.", 3);
                }

                await Task.Delay(50, cancellationToken);
            }
        }
    }

    /// <summary>Reads validated clone bindings; lost or malformed state must not become an empty reusable pool.</summary>
    internal Registry ReadRegistry()
    {
        if (!File.Exists(RegistryPath))
        {
            if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root)
                .Any(path => !string.Equals(Path.GetFileName(path), "mutation.lock", StringComparison.Ordinal)))
            {
                throw Attention("Registration state is missing but the state root is not empty.");
            }

            return new Registry { SchemaVersion = 1, Repositories = [] };
        }

        var registry = Read<Registry>(RegistryPath);
        if (registry.SchemaVersion != 1)
        {
            throw Attention($"Unsupported registry schema version {registry.SchemaVersion}.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pools = new HashSet<string>(Paths.Comparer);
        foreach (var repo in registry.Repositories)
        {
            if (repo is null || string.IsNullOrWhiteSpace(repo.Name) || !names.Add(repo.Name)
                || string.IsNullOrWhiteSpace(repo.RepoId) || !ids.Add(repo.RepoId)
                || repo.RepoId != Paths.RepositoryId(repo.CommonDirectory)
                || !Guid.TryParseExact(repo.PoolId, "N", out _) || repo.MaxSlots <= 0
                || !Path.IsPathFullyQualified(repo.RepositoryPath) || !Path.IsPathFullyQualified(repo.CommonDirectory)
                || !Path.IsPathFullyQualified(repo.PoolRoot) || !pools.Add(repo.PoolRoot))
            {
                throw Attention("Registration state contains invalid or duplicate repository bindings.");
            }
        }

        return registry;
    }

    /// <summary>Reads one pool's validated slots, including interrupted acquisitions that still occupy capacity.</summary>
    internal Catalog ReadCatalog(RepositoryRegistration repo)
    {
        var path = CatalogPath(repo);
        if (!File.Exists(path))
        {
            throw Attention($"Catalog is missing for '{repo.Name}'; existing slots must not be treated as empty.");
        }

        var catalog = Read<Catalog>(path);
        if (catalog.SchemaVersion != 1 || catalog.RepoId != repo.RepoId || catalog.PoolId != repo.PoolId
            || catalog.Status is not ("initializing" or "ready"))
        {
            throw Attention($"Catalog identity, status, or schema is invalid for '{repo.Name}'.");
        }

        var indexes = new HashSet<int>();
        var requests = new HashSet<string>(StringComparer.Ordinal);
        var leases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in catalog.Slots)
        {
            if (slot is null || slot.Index < 1 || slot.Index > repo.MaxSlots || !indexes.Add(slot.Index)
                || slot.WorkspaceId != WorkspaceId(repo, slot.Index)
                || !Paths.Equal(slot.Path, SlotPath(repo, slot.Index))
                || string.IsNullOrWhiteSpace(slot.RequestId) || !requests.Add(slot.RequestId)
                || string.IsNullOrWhiteSpace(slot.LeaseId) || !leases.Add(slot.LeaseId)
                || slot.State is not ("creating" or "leased" or "retained" or "needs-attention")
                || string.IsNullOrWhiteSpace(slot.BaseCommit) || slot.CreatedAt == default
                || string.IsNullOrWhiteSpace(slot.Parameters.BaseRef) || string.IsNullOrWhiteSpace(slot.Parameters.Branch)
                || string.IsNullOrWhiteSpace(slot.Parameters.Task) || string.IsNullOrWhiteSpace(slot.Parameters.Owner)
                || (slot.State == "retained" && (slot.ReleasedAt is null || slot.Outcome is null)))
            {
                throw Attention($"Catalog contains an invalid slot or duplicate request/lease for '{repo.Name}'.");
            }
        }

        return catalog;
    }

    /// <summary>Checks ownership and rejects unrecorded content; it does not adopt, delete, or repair directories.</summary>
    internal void CheckPool(RepositoryRegistration repo, Catalog catalog)
    {
        if (catalog.Status != "ready")
        {
            throw Attention($"Registration of '{repo.Name}' was interrupted; inspect its state and pool.");
        }

        if (!Paths.Equal(Paths.Canonical(repo.PoolRoot, mustExist: true), repo.PoolRoot))
        {
            throw Attention($"Pool root identity changed for '{repo.Name}'.");
        }

        var marker = Read<PoolMarker>(Path.Combine(repo.PoolRoot, MarkerName));
        if (marker.SchemaVersion != 1 || marker.RepoId != repo.RepoId || marker.PoolId != repo.PoolId
            || !Paths.Equal(marker.CommonDirectory, repo.CommonDirectory) || !Paths.Equal(marker.PoolRoot, repo.PoolRoot))
        {
            throw Attention($"Pool ownership marker does not match '{repo.Name}'.");
        }

        var knownPaths = catalog.Slots.Select(slot => slot.Path).ToHashSet(Paths.Comparer);
        foreach (var entry in Directory.EnumerateFileSystemEntries(repo.PoolRoot))
        {
            if (Paths.Equal(entry, Path.Combine(repo.PoolRoot, MarkerName)))
            {
                continue;
            }

            if (!knownPaths.Contains(entry))
            {
                throw Attention($"Uncataloged pool content must be retained and reconciled: {entry}");
            }

            if (!Paths.Equal(Paths.Canonical(entry, mustExist: true), entry))
            {
                throw Attention($"Managed slot path was replaced by an alias: {entry}");
            }
        }
    }

    internal void SaveRegistry(Registry registry)
    {
        WriteAtomic(RegistryPath, registry);
    }

    internal void SaveCatalog(RepositoryRegistration repo, Catalog catalog)
    {
        WriteAtomic(CatalogPath(repo), catalog);
    }

    internal static string SlotPath(RepositoryRegistration repo, int index)
    {
        return Path.Combine(repo.PoolRoot, $"slot-{index:D2}");
    }

    internal static string WorkspaceId(RepositoryRegistration repo, int index)
    {
        return $"ws-{repo.RepoId[5..17]}-{repo.PoolId[..12]}-{index:D2}";
    }

    /// <summary>Reads a versioned state record, preserving unreadable or invalid files for manual diagnosis.</summary>
    internal static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonContract.Options)
                ?? throw Attention($"State is null: {path}");
        }
        catch (JsonException exception)
        {
            throw Attention($"Invalid state JSON at '{path}': {exception.Message}");
        }
        catch (IOException exception)
        {
            throw Attention($"Cannot read state at '{path}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Attention($"Cannot access state at '{path}': {exception.Message}");
        }
    }

    /// <summary>Flushes a sibling file before replacing the current record, avoiding a partially written catalog.</summary>
    internal static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonContract.Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static WorkspaceException Attention(string message)
    {
        return new WorkspaceException("state_needs_attention", message);
    }

    private string CatalogPath(RepositoryRegistration repo)
    {
        return Path.Combine(Root, repo.RepoId + ".json");
    }
}

/// <summary>
/// Resolves Windows directory identity for ownership checks and derives stable clone/worktree IDs.
/// Junctions and short-name aliases must not let two registrations claim the same physical directory.
/// </summary>
internal static class Paths
{
    internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    internal static bool Equal(string left, string right)
    {
        return Comparer.Equals(left, right);
    }

    /// <summary>Returns a physical directory path, resolving existing ancestors when a pool has not been created yet.</summary>
    internal static string Canonical(string path, bool mustExist = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new WorkspaceException("unsupported_platform", "This version supports Windows local disks only.", 3);
        }

        // Save absent path components, resolve the nearest existing ancestor, then restore the suffix.
        var suffix = new Stack<string>();
        var existing = Path.GetFullPath(path);
        while (true)
        {
            try
            {
                if ((File.GetAttributes(existing) & FileAttributes.Directory) == 0)
                {
                    throw new WorkspaceException("invalid_path", $"Expected a directory: {existing}", 2);
                }

                break;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (mustExist)
                {
                    throw new WorkspaceException("cannot_verify", $"Required directory is missing: {existing}");
                }

                suffix.Push(Path.GetFileName(existing));
                existing = Path.GetDirectoryName(existing)
                    ?? throw new WorkspaceException("cannot_verify", $"Cannot resolve directory root: {path}");
            }
        }

        // A directory handle resolves junctions, parent aliases and DOS short names to one physical identity.
        using var handle = OpenDirectory(existing, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw PhysicalPathError(existing);
        }

        var buffer = new StringBuilder(256);
        var length = FinalPath(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.Capacity = checked((int)length + 1);
            length = FinalPath(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0 || length >= buffer.Capacity)
        {
            throw PhysicalPathError(existing);
        }

        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            resolved = @"\\" + resolved[8..];
        }
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            resolved = resolved[4..];
        }

        while (suffix.TryPop(out var part))
        {
            resolved = Path.Combine(resolved, part);
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static WorkspaceException PhysicalPathError(string path)
    {
        return new WorkspaceException("cannot_verify",
            $"Cannot resolve physical directory '{path}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, FileShare share,
        IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FinalPath(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    internal static bool Overlap(string left, string right)
    {
        return Within(left, right) || Within(right, left);
    }

    private static bool Within(string candidate, string parent)
    {
        return Equal(candidate, parent)
            || candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static string RepositoryId(string commonDirectory)
    {
        return "repo-" + Hash(commonDirectory.ToUpperInvariant());
    }

    internal static string ExternalId(string commonDirectory, string path)
    {
        return "ws-ext-" + Hash(commonDirectory.ToUpperInvariant() + "\0" + path.ToUpperInvariant())[..24];
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

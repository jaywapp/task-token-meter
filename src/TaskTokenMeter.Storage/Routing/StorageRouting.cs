using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskTokenMeter.Core.Contracts;

namespace TaskTokenMeter.Storage.Routing;

public interface IStorageFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    Stream OpenFile(string path, FileMode mode, FileAccess access, FileShare share);
    string GetFullPath(string path);
}

public sealed class PhysicalStorageFileSystem : IStorageFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents, new UTF8Encoding(false));
    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public Stream OpenFile(string path, FileMode mode, FileAccess access, FileShare share) =>
        new FileStream(path, mode, access, share, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
    public string GetFullPath(string path) => Path.GetFullPath(path);
}

public interface IStorageLockManager
{
    Task<IAsyncDisposable> AcquireWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IAsyncDisposable> AcquireRegistryAsync(CancellationToken cancellationToken = default);
}

public sealed class FileStorageLockManager : IStorageLockManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string lockRoot;
    private readonly IStorageFileSystem fileSystem;
    private readonly TimeSpan retryDelay;

    public FileStorageLockManager(
        string lockRoot,
        IStorageFileSystem? fileSystem = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockRoot);
        this.fileSystem = fileSystem ?? new PhysicalStorageFileSystem();
        this.lockRoot = this.fileSystem.GetFullPath(lockRoot);
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(25);
    }

    public Task<IAsyncDisposable> AcquireWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        AcquireAsync($"workspace-{workspaceId:D}.lock", cancellationToken);

    public Task<IAsyncDisposable> AcquireRegistryAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync("registry.lock", cancellationToken);

    private async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken)
    {
        fileSystem.CreateDirectory(lockRoot);
        var path = Path.Combine(lockRoot, name);
        var processLock = ProcessLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = fileSystem.OpenFile(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new StorageLockLease(stream, processLock);
                }
                catch (IOException)
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    private sealed class StorageLockLease(Stream stream, SemaphoreSlim processLock) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            processLock.Release();
        }
    }
}

public sealed record StorageLineageEntry(
    string StoreId,
    StorageMode Mode,
    string CanonicalDataRoot,
    long RouteGeneration,
    string MigrationReceipt,
    string ManifestHash,
    DateTimeOffset SupersededAt);

public sealed record RegisteredStorageRoute(
    string CanonicalWorkspaceRoot,
    StorageRoute ActiveRoute,
    IReadOnlyList<StorageLineageEntry> SupersededStores);

public interface IStorageRouteRegistry : IStorageRouteAuthority, IWorkspaceWriteCoordinator
{
    Task<RegisteredStorageRoute?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<RegisteredStorageRoute?> GetByWorkspacePathAsync(string workspaceRoot, CancellationToken cancellationToken = default);
    Task<RegisteredStorageRoute> InitializeAsync(
        string workspaceRoot,
        StorageMode mode,
        string dataRoot,
        CancellationToken cancellationToken = default);
    Task<RegisteredStorageRoute> SwitchAsync(
        StorageRoute expectedRoute,
        StorageRoute replacementRoute,
        string migrationReceipt,
        string sourceManifestHash,
        CancellationToken cancellationToken = default);
}

public sealed class FileStorageRouteRegistry : IStorageRouteRegistry
{
    private const int SchemaVersion = 1;
    private readonly string registryPath;
    private readonly IStorageFileSystem fileSystem;
    private readonly IStorageLockManager lockManager;
    private readonly TimeProvider timeProvider;

    public FileStorageRouteRegistry(
        string registryPath,
        IStorageLockManager lockManager,
        IStorageFileSystem? fileSystem = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        this.fileSystem = fileSystem ?? new PhysicalStorageFileSystem();
        this.registryPath = this.fileSystem.GetFullPath(registryPath);
        this.lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> IsActiveAsync(StorageRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        var registered = await GetAsync(route.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return registered is not null && RouteEquals(registered.ActiveRoute, route);
    }

    public Task<IAsyncDisposable> AcquireWorkspaceWriteAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        lockManager.AcquireWorkspaceAsync(workspaceId, cancellationToken);

    public Task<RegisteredStorageRoute?> GetAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = ReadDocument();
        return Task.FromResult(document.Workspaces.TryGetValue(WorkspaceKey(workspaceId), out var entry)
            ? ToRegistered(entry)
            : null);
    }

    public Task<RegisteredStorageRoute?> GetByWorkspacePathAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = CanonicalPath(workspaceRoot);
        var entry = ReadDocument().Workspaces.Values.FirstOrDefault(
            candidate => PathsEqual(candidate.CanonicalWorkspaceRoot, canonical));
        return Task.FromResult(entry is null ? null : ToRegistered(entry));
    }

    public async Task<RegisteredStorageRoute> InitializeAsync(
        string workspaceRoot,
        StorageMode mode,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var canonicalWorkspace = CanonicalPath(workspaceRoot);
        var workspaceId = StorageWorkspaceIdentity.FromCanonicalPath(canonicalWorkspace);
        await using var workspaceLock = await lockManager.AcquireWorkspaceAsync(workspaceId, cancellationToken)
            .ConfigureAwait(false);
        await using var registryLock = await lockManager.AcquireRegistryAsync(cancellationToken).ConfigureAwait(false);
        var document = ReadDocument();
        var key = WorkspaceKey(workspaceId);
        if (document.Workspaces.TryGetValue(key, out var existing))
        {
            if (!PathsEqual(existing.CanonicalWorkspaceRoot, canonicalWorkspace))
            {
                throw new StorageRouteConflictException(workspaceId);
            }

            return ToRegistered(existing);
        }

        var canonicalDataRoot = CanonicalPath(dataRoot);
        var route = new StorageRoute(
            workspaceId,
            mode,
            canonicalDataRoot,
            1,
            StorageStoreIdentity.FromDataRoot(canonicalDataRoot));
        document.Workspaces[key] = RegistryWorkspaceEntry.From(canonicalWorkspace, route);
        document.Revision = checked(document.Revision + 1);
        WriteDocument(document);
        return ToRegistered(document.Workspaces[key]);
    }

    public async Task<RegisteredStorageRoute> SwitchAsync(
        StorageRoute expectedRoute,
        StorageRoute replacementRoute,
        string migrationReceipt,
        string sourceManifestHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRoute);
        ArgumentNullException.ThrowIfNull(replacementRoute);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationReceipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceManifestHash);
        if (expectedRoute.WorkspaceId != replacementRoute.WorkspaceId ||
            replacementRoute.RouteGeneration != checked(expectedRoute.RouteGeneration + 1))
        {
            throw new ArgumentException("The replacement route must advance the same workspace by one generation.");
        }

        await using var registryLock = await lockManager.AcquireRegistryAsync(cancellationToken).ConfigureAwait(false);
        var document = ReadDocument();
        var key = WorkspaceKey(expectedRoute.WorkspaceId);
        if (!document.Workspaces.TryGetValue(key, out var entry))
        {
            throw new StorageRouteConflictException(expectedRoute.WorkspaceId);
        }

        if (RouteEquals(entry.ActiveRoute, replacementRoute))
        {
            return ToRegistered(entry);
        }

        if (!RouteEquals(entry.ActiveRoute, expectedRoute))
        {
            throw new StorageRouteConflictException(expectedRoute.WorkspaceId);
        }

        entry.SupersededStores.RemoveAll(item => string.Equals(
            item.StoreId, expectedRoute.ActiveStoreId, StringComparison.Ordinal));
        entry.SupersededStores.Add(new RegistryLineageEntry
        {
            StoreId = expectedRoute.ActiveStoreId,
            Mode = expectedRoute.Mode,
            CanonicalDataRoot = CanonicalPath(expectedRoute.CanonicalDataRoot),
            RouteGeneration = expectedRoute.RouteGeneration,
            MigrationReceipt = migrationReceipt,
            ManifestHash = sourceManifestHash,
            SupersededAt = timeProvider.GetUtcNow()
        });
        entry.SupersededStores.RemoveAll(item => string.Equals(
            item.StoreId, replacementRoute.ActiveStoreId, StringComparison.Ordinal));
        entry.ActiveRoute = replacementRoute with { CanonicalDataRoot = CanonicalPath(replacementRoute.CanonicalDataRoot) };
        document.Revision = checked(document.Revision + 1);
        WriteDocument(document);
        return ToRegistered(entry);
    }

    private RegistryDocument ReadDocument()
    {
        if (!fileSystem.FileExists(registryPath))
        {
            return new RegistryDocument();
        }

        var document = JsonSerializer.Deserialize<RegistryDocument>(
            fileSystem.ReadAllText(registryPath), RegistryJson.Options)
            ?? throw new InvalidDataException("The storage route registry is empty or invalid.");
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new NotSupportedException($"Storage registry schema {document.SchemaVersion} is not supported.");
        }

        return document;
    }

    private void WriteDocument(RegistryDocument document)
    {
        var directory = Path.GetDirectoryName(registryPath)
            ?? throw new InvalidOperationException("The registry path has no parent directory.");
        fileSystem.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(registryPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            fileSystem.WriteAllText(temporary, JsonSerializer.Serialize(document, RegistryJson.Options));
            fileSystem.MoveFile(temporary, registryPath, overwrite: true);
        }
        finally
        {
            if (fileSystem.FileExists(temporary))
            {
                fileSystem.DeleteFile(temporary);
            }
        }
    }

    private static RegisteredStorageRoute ToRegistered(RegistryWorkspaceEntry entry) =>
        new(
            entry.CanonicalWorkspaceRoot,
            entry.ActiveRoute,
            entry.SupersededStores
                .Select(static item => new StorageLineageEntry(
                    item.StoreId,
                    item.Mode,
                    item.CanonicalDataRoot,
                    item.RouteGeneration,
                    item.MigrationReceipt,
                    item.ManifestHash,
                    item.SupersededAt))
                .ToArray());

    private string CanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(fileSystem.GetFullPath(path));

    private static bool RouteEquals(StorageRoute left, StorageRoute right) =>
        left.WorkspaceId == right.WorkspaceId &&
        left.Mode == right.Mode &&
        left.RouteGeneration == right.RouteGeneration &&
        string.Equals(left.ActiveStoreId, right.ActiveStoreId, StringComparison.Ordinal) &&
        PathsEqual(left.CanonicalDataRoot, right.CanonicalDataRoot);

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string WorkspaceKey(Guid workspaceId) => workspaceId.ToString("D");

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; } = FileStorageRouteRegistry.SchemaVersion;
        public long Revision { get; set; }
        public Dictionary<string, RegistryWorkspaceEntry> Workspaces { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RegistryWorkspaceEntry
    {
        public required string CanonicalWorkspaceRoot { get; set; }
        public required StorageRoute ActiveRoute { get; set; }
        public List<RegistryLineageEntry> SupersededStores { get; set; } = [];

        public static RegistryWorkspaceEntry From(string workspaceRoot, StorageRoute route) =>
            new() { CanonicalWorkspaceRoot = workspaceRoot, ActiveRoute = route };
    }

    private sealed class RegistryLineageEntry
    {
        public required string StoreId { get; set; }
        public StorageMode Mode { get; set; }
        public required string CanonicalDataRoot { get; set; }
        public long RouteGeneration { get; set; }
        public required string MigrationReceipt { get; set; }
        public required string ManifestHash { get; set; }
        public DateTimeOffset SupersededAt { get; set; }
    }

    private static class RegistryJson
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    }
}

public static class StorageWorkspaceIdentity
{
    public static Guid FromCanonicalPath(string canonicalWorkspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalWorkspaceRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalWorkspaceRoot));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}

public static class StorageStoreIdentity
{
    public static string FromDataRoot(string canonicalDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalDataRoot));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}

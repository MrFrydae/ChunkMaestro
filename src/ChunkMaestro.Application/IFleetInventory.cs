using ChunkMaestro.Domain;

namespace ChunkMaestro.Application;

/// <summary>Version 1 local inventory contract. Registration only; no discovery, adoption side effects,
/// networking, credential values or remote commands. Caller supplies non-secret metadata.</summary>
public interface IFleetInventory
{
    /// <summary>Read cached inventory; a new store returns empty collections, never sample hosts.</summary>
    Task<FleetInventory> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>Create an explicit grouping. Reject empty or duplicate identity.</summary>
    Task RegisterFleetAsync(FleetId id, string name, CancellationToken cancellationToken = default);
    /// <summary>Register a host manually. Reject duplicate canonical address within a fleet.</summary>
    Task RegisterHostAsync(HostId id, FleetId fleetId, string name, string address, CancellationToken cancellationToken = default);
    /// <summary>Register a service and endpoints atomically. Reject duplicate service or occupied
    /// transport/port on the same host (conservatively including alternate bind addresses).</summary>
    Task RegisterInstanceAsync(InstanceId id, HostId hostId, string name, string serviceName,
        IReadOnlyList<Endpoint> endpoints, CancellationToken cancellationToken = default);
}
/// <summary>Stable failure categories, without raw SQL, connection strings or caller input.</summary>
public enum StorageFailure
{
    /// <summary>ControllerInUse.</summary>
    ControllerInUse,
    /// <summary>InvalidLocation.</summary>
    InvalidLocation,
    /// <summary>CorruptDatabase.</summary>
    CorruptDatabase,
    /// <summary>NewerSchema.</summary>
    NewerSchema,
    /// <summary>UnsupportedSchema.</summary>
    UnsupportedSchema,
    /// <summary>MigrationFailed.</summary>
    MigrationFailed,
    /// <summary>InvalidRegistration.</summary>
    InvalidRegistration,
    /// <summary>StorageUnavailable.</summary>
    StorageUnavailable
}
/// <summary>Actionable storage failure. BackupPath identifies a preserved local recovery point when available.</summary>
public sealed class FleetStorageException(StorageFailure failure, string message, string? backupPath = null) : Exception(message)
{
    /// <summary>Machine-readable failure.</summary>
    public StorageFailure Failure
    {
        get;
    } = failure;
    /// <summary>Completed SQLite-consistent pre-migration backup, if created.</summary>
    public string? BackupPath
    {
        get;
    } = backupPath;
}

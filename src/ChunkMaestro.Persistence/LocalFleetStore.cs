using System.Net;
using System.Text.RegularExpressions;
using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChunkMaestro.Persistence;

/// <summary>One owner-scoped local controller session. Dispose to release OS locks. Never restores,
/// deletes, downgrades, contacts hosts or stores credential values.</summary>
public sealed class LocalFleetStore : IFleetInventory, IAsyncDisposable
{
    private readonly FileStream controllerLock;
    private readonly DbContextOptions<FleetDbContext> options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    private LocalFleetStore(FileStream controllerLock, DbContextOptions<FleetDbContext> options)
    {
        this.controllerLock = controllerLock;
        this.options = options;
    }

    /// <summary>Initialize safely or throw a categorized failure. Existing database and completed backup
    /// survive failure. No store is returned until all migrations and integrity checks succeed.</summary>
    public static Task<LocalFleetStore> OpenAsync(StorageLocation location, CancellationToken cancellationToken = default)
        => OpenCoreAsync(location, null, cancellationToken);

    internal static async Task<LocalFleetStore> OpenCoreAsync(StorageLocation location,
        Func<FleetDbContext, CancellationToken, Task>? migrate, CancellationToken cancellationToken = default)
    {
        FileStream? controller = null;
        string? backupPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = location.Prepare();
            try
            {
                controller = StorageLocation.CreateOrOpen(Path.Combine(directory, "controller.lock"));
            }

            catch (IOException)
            {
                throw new FleetStorageException(StorageFailure.ControllerInUse, "Another controller holds this local store.");
            }

            using FileStream migrationLock = StorageLocation.CreateOrOpen(Path.Combine(directory, "migration.lock"));
            string databasePath = Path.Combine(directory, "fleet.db");
            using (StorageLocation.CreateOrOpen(databasePath))
            {

            }

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 3
            }.ToString();
            DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connectionString).Options;
            await using FleetDbContext db = new FleetDbContext(options);
            await db.Database.OpenConnectionAsync(cancellationToken);
            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();
            await VerifyIntegrityAsync(connection, cancellationToken);
            string[] known = db.Database.GetMigrations().ToArray();
            string[] applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            if (applied.Any(x => !known.Contains(x, StringComparer.Ordinal)))
            {
                throw new FleetStorageException(StorageFailure.NewerSchema, "Database migration history is newer or incompatible. Open it with the matching application; no downgrade was attempted.");
            }

            if (!applied.SequenceEqual(known.Take(applied.Length)))
            {
                throw new FleetStorageException(StorageFailure.UnsupportedSchema, "Database migration history is not a supported prefix.");
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version";
                int version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                if (version > known.Length)
                {
                    throw new FleetStorageException(StorageFailure.NewerSchema, "Database schema version is newer than this application.");
                }

                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%'";
                if (applied.Length == 0 && Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                {
                    throw new FleetStorageException(StorageFailure.UnsupportedSchema, "Unversioned database contains tables. No migration was attempted.");
                }

                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                {
                    command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsLock WHERE Id=1";
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                    {
                        throw new FleetStorageException(StorageFailure.MigrationFailed, "An abandoned EF migration lock requires reviewed recovery. Database was preserved.");
                    }
                }
            }
            if (applied.Length < known.Length)
            {
                backupPath = await CreateBackupAsync(connection, directory, cancellationToken);
                try
                {
                    if (migrate is null)
                    {
                        await db.Database.MigrateAsync(cancellationToken);
                    }
                    else
                    {
                        await migrate(db, cancellationToken);
                    }

                    await VerifyIntegrityAsync(connection, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new FleetStorageException(StorageFailure.MigrationFailed,
                        "Migration failed. Preserve the database and use the completed backup for reviewed recovery.", backupPath);
                }
            }
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", cancellationToken);
            LocalFleetStore result = new LocalFleetStore(controller, options);
            controller = null;
            return result;
        }
        catch (SqliteException ex)
        {
            throw new FleetStorageException(ex.SqliteErrorCode is 11 or 26 ? StorageFailure.CorruptDatabase : StorageFailure.StorageUnavailable,
                ex.SqliteErrorCode is 11 or 26 ? "SQLite integrity or format check failed. Preserve the database and sidecars for recovery." : "SQLite storage could not be initialized; data was preserved.", backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FleetStorageException(StorageFailure.StorageUnavailable, "Local storage is unavailable; existing files were preserved.", backupPath);
        }
        finally
        {
            controller?.Dispose();
        }

    }

    internal static async Task<string> CreateBackupAsync(SqliteConnection connection, string directory, CancellationToken token)
    {
        string candidate = Path.Combine(directory, $"before-migration-{Guid.NewGuid():N}.db");
        using (StorageLocation.CreateOrOpen(candidate))
        {

        }

        using SqliteConnection backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = candidate,
            Pooling = false
        }
.ToString());
        await backup.OpenAsync(token);
        connection.BackupDatabase(backup);
        await VerifyIntegrityAsync(backup, token);
        return candidate;
    }

    private static async Task VerifyIntegrityAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(0) != "ok" || await reader.ReadAsync(token))
        {
            throw new FleetStorageException(StorageFailure.CorruptDatabase, "SQLite integrity check failed; files were preserved.");
        }
    }

    /// <inheritdoc/>
    public async Task<FleetInventory> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await using FleetDbContext db = new FleetDbContext(options);
            FleetRow[] fleets = await db.Fleets.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
            HostRow[] hosts = await db.Hosts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
            InstanceRow[] instances = await db.Instances.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
            EndpointRow[] endpoints = await db.Endpoints.AsNoTracking().ToArrayAsync(cancellationToken);
            return new(fleets.Select(x => new Fleet(new(x.Id), x.Name)).ToArray(),
                hosts.Select(x => new Host(new(x.Id), new(x.FleetId), x.Name, x.Address, DateTimeOffset.FromUnixTimeMilliseconds(x.RegisteredAt), Observation.Unknown)).ToArray(),
                instances.Select(x => new Instance(new(x.Id), new(x.HostId), x.Name, x.ServiceName, DateTimeOffset.FromUnixTimeMilliseconds(x.RegisteredAt),
                    Observation.Unknown, endpoints.Where(e => e.InstanceId == x.Id).OrderBy(e => e.Edition).ThenBy(e => e.Port)
                        .Select(e => new Endpoint((EndpointEdition)e.Edition, e.Address, e.Port)).ToArray())).ToArray(), DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }

    }

    /// <inheritdoc/>
    public Task RegisterFleetAsync(FleetId id, string name, CancellationToken cancellationToken = default)
    {
        Identity(id.Value);
        FleetRow row = new FleetRow
        {
            Id = id.Value,
            Name = Label(name)
        }
;
        return WriteAsync(db =>
{
    db.Fleets.Add(row);
    return Task.CompletedTask;
}
, cancellationToken);
    }

    /// <inheritdoc/>
    public Task RegisterHostAsync(HostId id, FleetId fleetId, string name, string address, CancellationToken cancellationToken = default)
    {
        Identity(id.Value);
        Identity(fleetId.Value);
        HostRow row = new HostRow
        {
            Id = id.Value,
            FleetId = fleetId.Value,
            Name = Label(name),
            Address = Address(address),
            RegisteredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }
;
        return WriteAsync(db =>
{
    db.Hosts.Add(row);
    return Task.CompletedTask;
}
, cancellationToken);
    }

    /// <inheritdoc/>
    public Task RegisterInstanceAsync(InstanceId id, HostId hostId, string name, string serviceName, IReadOnlyList<Endpoint> endpoints, CancellationToken cancellationToken = default)
    {
        Identity(id.Value);
        Identity(hostId.Value);
        if (serviceName is null || !Regex.IsMatch(serviceName, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,120}\.service\z") || endpoints is null || endpoints.Count is < 1 or > 16)
        {
            throw Invalid();
        }

        InstanceRow row = new InstanceRow
        {
            Id = id.Value,
            HostId = hostId.Value,
            Name = Label(name),
            ServiceName = serviceName,
            RegisteredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }
;
        EndpointRow[] endpointRows = endpoints.Select(e =>
        {
            if (e is null || !Enum.IsDefined(e.Edition) || e.Port is < 1 or > 65535)
            {
                throw Invalid();
            }

            return new EndpointRow
            {
                InstanceId = id.Value,
                HostId = hostId.Value,
                Edition = (int)e.Edition,
                Address = Address(e.Address),
                Port = e.Port
            }
;
        }).ToArray();
        if (endpointRows.DistinctBy(e => (e.Edition, e.Port)).Count() != endpointRows.Length)
        {
            throw Invalid();
        }

        return WriteAsync(db =>
        {
            db.Instances.Add(row);
            db.Endpoints.AddRange(endpointRows);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private async Task WriteAsync(Func<FleetDbContext, Task> add, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await using FleetDbContext db = new FleetDbContext(options);
            await add(db);
            try
            {
                await db.SaveChangesAsync(token);
            }

            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sql && sql.SqliteErrorCode == 19)
            {
                throw Invalid();
            }

        }
        finally
        {
            gate.Release();
        }

    }

    private static void Identity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw Invalid();
        }
    }
    private static string Label(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl))
        {
            throw Invalid();
        }

        return value.Trim();
    }
    private static string Address(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value != value.Trim())
        {
            throw Invalid();
        }

        if (IPAddress.TryParse(value, out IPAddress? ip))
        {
            return ip.ToString().ToLowerInvariant();
        }

        string host = value.TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains('_'))
        {
            throw Invalid();
        }

        return host;
    }
    private static FleetStorageException Invalid() => new(StorageFailure.InvalidRegistration, "Invalid or duplicate registration target; inventory was not changed.");

    /// <summary>Wait for local reads/writes, then release controller ownership without deleting files.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!disposed)
            {
                disposed = true;
                controllerLock.Dispose();
            }

        }

        finally
        {
            gate.Release();
        }

    }
}

using System.Diagnostics;
using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class StorageTests : IDisposable
{
    private readonly string directory;
    private StorageLocation Location => StorageLocation.ForTests(directory);
    public StorageTests()
    {
        string root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        directory = Path.Combine(root, "chunkmaestro-storage-test-" + Guid.NewGuid().ToString("N"));
    }
    private async Task<SqliteConnection> ConnectionAsync()
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Pooling = false
        }
.ToString());
        await connection.OpenAsync();
        return connection;
    }
    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    [Fact]
    public async Task Empty_start_creates_versioned_database_without_sample_inventory()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await store.ReadAsync();
        Assert.Empty(inventory.Fleets);
        Assert.Empty(inventory.Hosts);
        Assert.Empty(inventory.Instances);
        await using SqliteConnection connection = await ConnectionAsync();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }
    [Fact]
    public async Task Manual_inventory_reloads_with_unknown_health_and_distinct_protocols()
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        DateTimeOffset registered;
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Home");
            await store.RegisterHostAsync(host, fleet, "Host", "MC.EXAMPLE.");
            await store.RegisterInstanceAsync(instance, host, "Server", "minecraft.service",
                [new(EndpointEdition.Java, "mc.example", 25565), new(EndpointEdition.Bedrock, "mc.example", 25565)]);
            registered = Assert.Single((await store.ReadAsync()).Hosts).RegisteredAt;
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await reopened.ReadAsync();
        Assert.Equal(fleet, Assert.Single(inventory.Fleets).Id);
        Host h = Assert.Single(inventory.Hosts);
        Assert.Equal("mc.example", h.Address);
        Assert.Equal(registered, h.RegisteredAt);
        Assert.Equal(Observation.Unknown, h.Observation);
        Assert.Null(h.Observation.LastSuccessfulContactAt);
        Instance i = Assert.Single(inventory.Instances);
        Assert.Equal(instance, i.Id);
        Assert.Equal(host, i.HostId);
        Assert.All(i.Endpoints, e => Assert.Equal(Observation.Unknown, e.Health));
        Assert.Equal([EndpointTransport.Tcp, EndpointTransport.Udp], i.Endpoints.Select(e => e.Transport));
    }
    [Fact]
    public async Task Duplicate_targets_and_missing_parents_are_rejected_atomically()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId f = new FleetId(Guid.NewGuid());
        HostId h = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(f, "Home");
        await store.RegisterHostAsync(h, f, "Host", "mc.example");
        await InvalidAsync(() => store.RegisterHostAsync(new(Guid.NewGuid()), f, "Alias", "MC.EXAMPLE."));
        await InvalidAsync(() => store.RegisterHostAsync(new(Guid.NewGuid()), new(Guid.NewGuid()), "Orphan", "other.example"));
        await store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "One", "minecraft.service", [new(EndpointEdition.Java, "mc.example", 25565)]);
        await InvalidAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "Duplicate service", "minecraft.service", [new(EndpointEdition.Java, "mc.example", 25566)]));
        await InvalidAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "Conflict", "other.service", [new(EndpointEdition.Java, "0.0.0.0", 25565)]));
        Assert.Single((await store.ReadAsync()).Instances);
    }
    [Theory]
    [InlineData("https://user:password@example.com")]
    [InlineData("../target")]
    [InlineData("host; echo value")]
    [InlineData("")]
    public async Task Invalid_addresses_do_not_enter_database(string address)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId f = new FleetId(Guid.NewGuid());
        await store.RegisterFleetAsync(f, "Home");
        await InvalidAsync(() => store.RegisterHostAsync(new(Guid.NewGuid()), f, "Host", address));
        Assert.Empty((await store.ReadAsync()).Hosts);
    }
    [Fact]
    public async Task Invalid_endpoint_and_identity_are_rejected()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await InvalidAsync(() => store.RegisterFleetAsync(new(Guid.Empty), "Fleet"));
        FleetId f = new FleetId(Guid.NewGuid());
        HostId h = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(f, "Fleet");
        await store.RegisterHostAsync(h, f, "Host", "localhost");
        foreach (Endpoint e in new[]
{
new Endpoint((EndpointEdition)99, "localhost", 10), new Endpoint(EndpointEdition.Java, "localhost", 0), new Endpoint(EndpointEdition.Java, "localhost", 65536)
}
)
        {
            await InvalidAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "One", "one.service", [e]));
        }

        await InvalidAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "One", "../one.service", [new(EndpointEdition.Java, "localhost", 10)]));
    }
    private static async Task InvalidAsync(Func<Task> action) =>
        Assert.Equal(StorageFailure.InvalidRegistration, (await Assert.ThrowsAsync<FleetStorageException>(action)).Failure);

    [Fact]
    public async Task Committed_model_matches_snapshot_and_direct_downgrade_is_disabled()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "fleet.db"),
                Pooling = false
            }
.ToString()).Options;
        await using FleetDbContext db = new FleetDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
        string current = Assert.Single(db.Database.GetMigrations());
        Assert.Throws<NotSupportedException>(() => db.GetService<IMigrator>().GenerateScript(current, Migration.InitialDatabase));
        Assert.Empty((await store.ReadAsync()).Hosts);
    }

    [Fact]
    public async Task Second_controller_process_is_rejected_and_exit_releases_lock()
    {
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")

        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
;
        start.ArgumentList.Add(typeof(StorageTests).Assembly.Location);
        start.ArgumentList.Add("--hold-test-store");
        start.ArgumentList.Add(directory);
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("LOCKED", ready);
            FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
            Assert.Equal(StorageFailure.ControllerInUse, error.Failure);
            process.Kill();
            await process.WaitForExitAsync();
            await using LocalFleetStore recovered = await LocalFleetStore.OpenAsync(Location);
            Assert.Empty((await recovered.ReadAsync()).Hosts);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }

        }

    }
    [Fact]
    public async Task Newer_history_is_rejected_without_changing_database_or_attempting_downgrade()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {

        }

        await using (SqliteConnection connection = await ConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO __EFMigrationsHistory VALUES ('99999999999999_Future','99.0.0')");
        }

        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db"));
        FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.NewerSchema, error.Failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db")));
    }
    [Fact]
    public async Task Newer_user_version_is_rejected()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {

        }

        await using (SqliteConnection connection = await ConnectionAsync())
        {
            await ExecuteAsync(connection, "PRAGMA user_version=999");
        }

        Assert.Equal(StorageFailure.NewerSchema, (await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location))).Failure);
    }
    [Fact]
    public async Task Corrupted_database_has_safe_diagnostic_and_is_preserved()
    {
        Location.Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(directory, "fleet.db")))
        {

        }

        byte[] invalid = "not a sqlite database"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(directory, "fleet.db"), invalid);
        FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.CorruptDatabase, error.Failure);
        Assert.DoesNotContain(directory, error.Message);
        Assert.Equal(invalid, await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db")));
    }
    [Fact]
    public async Task Failed_migration_preserves_consistent_backup_and_releases_controller_lock()
    {
        // Start from a legitimate empty SQLite file with no migration history.
        Location.Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(directory, "fleet.db")))
        {

        }

        await using (SqliteConnection initial = await ConnectionAsync())
        {
            await ExecuteAsync(initial, "PRAGMA journal_mode=WAL; PRAGMA user_version=0");
        }

        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenCoreAsync(Location,
            async (db, token) =>
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE Interrupted (Id INTEGER)", token);
                throw new IOException("simulated interrupted migration");
            }));
        Assert.Equal(StorageFailure.MigrationFailed, failure.Failure);
        Assert.NotNull(failure.BackupPath);
        Assert.True(File.Exists(failure.BackupPath));
        await using SqliteConnection backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = failure.BackupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }
.ToString());
        await backup.OpenAsync();
        Assert.Equal("ok", await ScalarAsync(backup, "PRAGMA integrity_check"));
        Assert.Equal(0L, await ScalarAsync(backup, "SELECT COUNT(*) FROM sqlite_master WHERE name='Interrupted'"));
        await using SqliteConnection live = await ConnectionAsync();
        Assert.Equal(1L, await ScalarAsync(live, "SELECT COUNT(*) FROM sqlite_master WHERE name='Interrupted'"));
        await live.DisposeAsync();
        await backup.DisposeAsync();
        Assert.Equal(StorageFailure.UnsupportedSchema, (await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location))).Failure);
    }
    [Fact]
    public async Task Online_backup_includes_committed_uncheckpointed_WAL_data()
    {
        Location.Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(directory, "fleet.db")))
        {

        }

        await using SqliteConnection live = await ConnectionAsync();
        await ExecuteAsync(live, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Evidence (Value TEXT); INSERT INTO Evidence VALUES ('committed-in-wal')");
        Assert.True(new FileInfo(Path.Combine(directory, "fleet.db-wal")).Length > 0);
        string path = await LocalFleetStore.CreateBackupAsync(live, directory, default);
        await using SqliteConnection backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly
        }
.ToString());
        await backup.OpenAsync();
        Assert.Equal("committed-in-wal", await ScalarAsync(backup, "SELECT Value FROM Evidence"));
        Assert.Equal("ok", await ScalarAsync(backup, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Abandoned_ef_lock_fails_promptly_instead_of_hanging_or_blindly_clearing_it()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {

        }

        await using (SqliteConnection connection = await ConnectionAsync())
        {
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS __EFMigrationsLock (Id INTEGER PRIMARY KEY, Timestamp TEXT NOT NULL); INSERT OR REPLACE INTO __EFMigrationsLock VALUES (1,'test')");
        }

        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.MigrationFailed, failure.Failure);
    }
    [Fact]
    public async Task Symlink_database_is_rejected_without_modifying_target()
    {
        Location.Prepare();
        string target = Path.Combine(directory, "target");
        using (StorageLocation.CreateOrOpen(target))
        {

        }

        await File.WriteAllTextAsync(target, "preserve");
        File.CreateSymbolicLink(Path.Combine(directory, "fleet.db"), target);
        Assert.Equal(StorageFailure.InvalidLocation, (await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location))).Failure);
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));
    }
    [Fact]
    public void Unconfirmed_local_storage_is_rejected() =>
        Assert.Equal(StorageFailure.InvalidLocation, Assert.Throws<FleetStorageException>(() => StorageLocation.ForCurrentUser(false)).Failure);
    [Fact]
    public void Cached_fresh_observation_becomes_stale_without_changing_provenance()
    {
        DateTimeOffset time = DateTimeOffset.FromUnixTimeSeconds(100);
        Observation fresh = new Observation(ObservationState.Fresh, time, time, "future-agent");
        Assert.Equal(fresh with
        {
            State = ObservationState.Stale
        }
, fresh.AsCached());
        Assert.Equal(Observation.Unknown, Observation.Unknown.AsCached());
    }
    public void Dispose()
    {
        // Only this fixture's generated temporary files; production code never deletes data.
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

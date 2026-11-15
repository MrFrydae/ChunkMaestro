using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
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
    public async Task UnicodeStoragePathPreservesExactDirectoryIdentity()
    {
        string unicodeDirectory = Path.Combine(directory, "世界-å-minecraft");
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(unicodeDirectory));
        Assert.Empty((await store.ReadAsync()).Hosts);
        Assert.True(File.Exists(Path.Combine(unicodeDirectory, "fleet.db")));
    }

    [Fact]
    public async Task EmptyStartCreatesVersionedDatabaseWithoutSampleInventory()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await store.ReadAsync();
        Assert.Empty(inventory.Fleets);
        Assert.Empty(inventory.Hosts);
        Assert.Empty(inventory.Instances);
        await using SqliteConnection connection = await ConnectionAsync();
        DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options;
        await using FleetDbContext db = new FleetDbContext(options);
        Assert.Equal(db.Database.GetMigrations().ToArray(), (await db.Database.GetAppliedMigrationsAsync()).ToArray());
    }
    [Fact]
    public async Task ManualInventoryReloadsWithUnknownHealthAndDistinctProtocols()
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
    public async Task DuplicateTargetsAndMissingParentsAreRejectedAtomically()
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
    public async Task InvalidAddressesDoNotEnterDatabase(string address)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId f = new FleetId(Guid.NewGuid());
        await store.RegisterFleetAsync(f, "Home");
        await InvalidAsync(() => store.RegisterHostAsync(new(Guid.NewGuid()), f, "Host", address));
        Assert.Empty((await store.ReadAsync()).Hosts);
    }
    [Fact]
    public async Task InvalidEndpointAndIdentityAreRejected()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await InvalidAsync(() => store.RegisterFleetAsync(new(Guid.Empty), "Fleet"));
        FleetId f = new FleetId(Guid.NewGuid());
        HostId h = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(f, "Fleet");
        await store.RegisterHostAsync(h, f, "Host", "localhost");
        Endpoint[] invalidEndpoints =
        [
            new Endpoint((EndpointEdition)99, "localhost", 10),
            new Endpoint(EndpointEdition.Java, "localhost", 0),
            new Endpoint(EndpointEdition.Java, "localhost", 65536)
        ];
        foreach (Endpoint e in invalidEndpoints)
        {
            await InvalidAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "One", "one.service", [e]));
        }

        await InvalidAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), h, "One", "../one.service", [new(EndpointEdition.Java, "localhost", 10)]));
    }
    private static async Task InvalidAsync(Func<Task> action) =>
        Assert.Equal(StorageFailure.InvalidRegistration, (await Assert.ThrowsAsync<FleetStorageException>(action)).Failure);

    [Theory]
    [InlineData("minecraft@survival.service")]
    [InlineData("minecraft@creative-1.service")]
    public async Task InstantiatedServiceMetadataPersistsWithoutEnrollment(string serviceName)
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Fleet");
            await store.RegisterHostAsync(host, fleet, "Host", "mc.example");
            await store.RegisterInstanceAsync(instance, host, "Survival", serviceName,
                [new Endpoint(EndpointEdition.Java, "mc.example", 25565)]);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        Instance saved = Assert.Single((await reopened.ReadAsync()).Instances);
        Assert.Equal(instance, saved.Id);
        Assert.Equal(serviceName, saved.ServiceName);
        Assert.Equal(Observation.Unknown, saved.Observation);
    }

    [Theory]
    [InlineData("minecraft@.service")]
    [InlineData("@survival.service")]
    [InlineData("minecraft@@survival.service")]
    [InlineData("minecraft@survival;echo.service")]
    [InlineData("minecraft@../survival.service")]
    [InlineData("minecraft@survival.service\n")]
    public async Task InvalidInstantiatedServiceMetadataIsRejected(string serviceName)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Fleet");
        await store.RegisterHostAsync(host, fleet, "Host", "mc.example");
        await InvalidAsync(() => store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Rejected", serviceName,
            [new Endpoint(EndpointEdition.Java, "mc.example", 25565)]));
        Assert.Empty((await store.ReadAsync()).Instances);
    }

    [Fact]
    public async Task InstantiatedServiceComponentsRemainBounded()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Fleet");
        await store.RegisterHostAsync(host, fleet, "Host", "mc.example");
        string boundary = new string('m', 121) + "@" + new string('s', 121) + ".service";
        await store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Boundary", boundary,
            [new Endpoint(EndpointEdition.Java, "mc.example", 25565)]);
        string[] overlong =
        [
            new string('m', 122) + "@survival.service",
            "minecraft@" + new string('s', 122) + ".service"
        ];
        foreach (string serviceName in overlong)
        {
            await InvalidAsync(() => store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Rejected", serviceName,
                [new Endpoint(EndpointEdition.Java, "mc.example", 25566)]));
        }
        Assert.Equal(boundary, Assert.Single((await store.ReadAsync()).Instances).ServiceName);
    }

    [Fact]
    public async Task CommittedModelMatchesSnapshotAndDirectDowngradeIsDisabled()
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
        string current = db.Database.GetMigrations().Last();
        Assert.Throws<NotSupportedException>(() => db.GetService<IMigrator>().GenerateScript(current, Migration.InitialDatabase));
        Assert.Empty((await store.ReadAsync()).Hosts);
    }

    [Fact]
    public async Task SecondControllerProcessIsRejectedAndExitReleasesLock()
    {
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")

        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
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
    public async Task NewerHistoryIsRejectedWithoutChangingDatabaseOrAttemptingDowngrade()
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
    public async Task NewerUserVersionIsRejected()
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
    public async Task CorruptedDatabaseHasSafeDiagnosticAndIsPreserved()
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
    public async Task FailedMigrationPreservesConsistentBackupAndReleasesControllerLock()
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
        // A read-only SQLite connection can leave WAL/SHM files behind. Create fixture
        // sidecars with the same strict ownership as the store rather than the elevated
        // Windows token's default Administrators owner. Never repair existing files.
        foreach (string suffix in new[]
        {
            "-wal",
            "-shm"
        })
        {
            using FileStream sidecar = StorageLocation.CreateOrOpen(failure.BackupPath + suffix);
        }
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
    public async Task OnlineBackupIncludesCommittedUncheckpointedWALData()
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
    public async Task AbandonedEfLockFailsPromptlyInsteadOfHangingOrBlindlyClearingIt()
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
    public async Task SymlinkDatabaseIsRejectedWithoutModifyingTarget()
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
    public void UnconfirmedLocalStorageIsRejected() =>
        Assert.Equal(StorageFailure.InvalidLocation, Assert.Throws<FleetStorageException>(() => StorageLocation.ForCurrentUser(false)).Failure);
    [Fact]
    public void CachedFreshObservationBecomesStaleWithoutChangingProvenance()
    {
        DateTimeOffset time = DateTimeOffset.FromUnixTimeSeconds(100);
        Observation fresh = new Observation(ObservationState.Fresh, time, time, "future-agent");
        Assert.Equal(fresh with
        {
            State = ObservationState.Stale
        }, fresh.AsCached());
        Assert.Equal(Observation.Unknown, Observation.Unknown.AsCached());
    }
    [Theory]
    [InlineData("DROP TABLE Endpoints")]
    [InlineData("DROP INDEX IX_Hosts_FleetId_Address")]
    [InlineData("ALTER TABLE Hosts ADD COLUMN Unexpected TEXT")]
    [InlineData("CREATE TRIGGER Unexpected AFTER INSERT ON Fleets BEGIN DELETE FROM Hosts; END")]
    [InlineData("PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql=replace(sql, 'REFERENCES \"Fleets\"', 'REFERENCES \"Hosts\"') WHERE name='Hosts'; PRAGMA writable_schema=OFF")]
    public async Task ModifiedSchemaIsRefusedWithoutRepairAndLockIsReleased(string sql)
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
        }
        await using (SqliteConnection connection = await ConnectionAsync())
        {
            await ExecuteAsync(connection, sql);
        }
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db"));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
            Assert.Contains(error.Failure, new[]
            {
                StorageFailure.UnsupportedSchema,
                StorageFailure.CorruptDatabase
            });
            Assert.DoesNotContain(directory, error.ToString());
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db")));
        }
    }

    [Fact]
    public async Task LogicalForeignKeyCorruptionIsRefusedAndBackupIsNotCertified()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
        }
        await using (SqliteConnection connection = await ConnectionAsync())
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF; INSERT INTO Hosts VALUES ('orphan','missing','name','mc.example',0)");
            Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA integrity_check"));
            FleetStorageException backup = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.CreateBackupAsync(connection, directory, default));
            Assert.Equal(StorageFailure.CorruptDatabase, backup.Failure);
        }
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db"));
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
        Assert.DoesNotContain(directory, failure.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "fleet.db")));
    }

    [Fact]
    public async Task HeldWriterReturnsTaskPromptlyAndFailureIsSanitizedAtomicAndRecoverable()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await using SqliteConnection writer = await ConnectionAsync();
        await ExecuteAsync(writer, "BEGIN IMMEDIATE");
        FleetId id = new FleetId(Guid.NewGuid());
        Stopwatch clock = Stopwatch.StartNew();
        Task operation = store.RegisterFleetAsync(id, "denied");
        TimeSpan callerTime = clock.Elapsed;
        Assert.True(callerTime < TimeSpan.FromSeconds(1), $"Caller blocked for {callerTime}");
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => operation);
        Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
        Assert.DoesNotContain(directory, failure.ToString());
        Assert.DoesNotContain("SQLite Error", failure.ToString());
        await ExecuteAsync(writer, "ROLLBACK");
        Assert.Empty((await store.ReadAsync()).Fleets);
        using CancellationTokenSource canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RegisterFleetAsync(id, "canceled", canceled.Token));
        Assert.Empty((await store.ReadAsync()).Fleets);
        await store.RegisterFleetAsync(id, "reviewed retry");
        Assert.Equal(id, Assert.Single((await store.ReadAsync()).Fleets).Id);
    }

    [Fact]
    public async Task DirectoryReplacementCannotRedirectOriginalSession()
    {
        await using LocalFleetStore original = await LocalFleetStore.OpenAsync(Location);
        await original.RegisterFleetAsync(new FleetId(Guid.NewGuid()), "original");
        string moved = directory + "-moved";
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => Directory.Move(directory, moved));
            Assert.Equal("original", Assert.Single((await original.ReadAsync()).Fleets).Name);
        }
        else
        {
            Directory.Move(directory, moved);
            try
            {
                await using LocalFleetStore replacement = await LocalFleetStore.OpenAsync(Location);
                await replacement.RegisterFleetAsync(new FleetId(Guid.NewGuid()), "replacement");
                Assert.Equal(StorageFailure.InvalidLocation, (await Assert.ThrowsAsync<FleetStorageException>(() => original.ReadAsync())).Failure);
                Assert.Equal(StorageFailure.InvalidLocation, (await Assert.ThrowsAsync<FleetStorageException>(() => original.RegisterFleetAsync(new FleetId(Guid.NewGuid()), "must not commit"))).Failure);
                Assert.Equal("replacement", Assert.Single((await replacement.ReadAsync()).Fleets).Name);
            }
            finally
            {
                await original.DisposeAsync();
                Directory.Delete(moved, true);
            }
        }
    }

    [Theory]
    [InlineData("fleet.db")]
    [InlineData("controller.lock")]
    public async Task FileReplacementWhileHeldIsRefused(string filename)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        string path = Path.Combine(directory, filename);
        string moved = path + "-moved";
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Move(path, moved));
            Assert.Empty((await store.ReadAsync()).Fleets);
        }
        else
        {
            File.Move(path, moved);
            using (StorageLocation.CreateOrOpen(path))
            {
            }
            Assert.Equal(StorageFailure.InvalidLocation, (await Assert.ThrowsAsync<FleetStorageException>(() => store.ReadAsync())).Failure);
            Assert.Equal(StorageFailure.InvalidLocation, (await Assert.ThrowsAsync<FleetStorageException>(() => store.RegisterFleetAsync(new FleetId(Guid.NewGuid()), "denied"))).Failure);
            Assert.Equal(0, new FileInfo(path).Length);
        }
    }

    [Fact]
    public async Task LiveSqliteSidecarsRemainConfinedAcrossReadsWritesAndReopen()
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        string[] suffixes = ["-wal", "-shm"];
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Sidecar ownership");
            Assert.Equal(fleet, Assert.Single((await store.ReadAsync()).Fleets).Id);
            foreach (string suffix in suffixes)
            {
                string path = Path.Combine(directory, "fleet.db" + suffix);
                Assert.True(File.Exists(path));
                StorageLocation.CheckScope(path);
            }
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        Assert.Equal(fleet, Assert.Single((await reopened.ReadAsync()).Fleets).Id);
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            StorageLocation.CheckScope(path);
        }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task UnsafeExistingSidecarIsRejectedWithoutRepair(string suffix)
    {
        Location.Prepare();
        string path = Path.Combine(directory, "fleet.db" + suffix);
        using (StorageLocation.CreateOrOpen(path))
        {
        }
        await File.WriteAllTextAsync(path, "preserve-sidecar");
        string before;
        if (OperatingSystem.IsWindows())
        {
            FileInfo file = new FileInfo(path);
            FileSecurity security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(security);
            before = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            before = File.GetUnixFileMode(path).ToString();
        }
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.InvalidLocation, failure.Failure);
        Assert.Equal("preserve-sidecar", await File.ReadAllTextAsync(path));
        string after = OperatingSystem.IsWindows()
            ? new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All)
            : File.GetUnixFileMode(path).ToString();
        Assert.Equal(before, after);
        Assert.False(File.Exists(Path.Combine(directory, "fleet.db")));
    }

    [Fact]
    public async Task OpenReturnsBeforeSynchronousMigrationWorkCompletes()
    {
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Stopwatch clock = Stopwatch.StartNew();
        Task<LocalFleetStore> opening = LocalFleetStore.OpenCoreAsync(Location, async (db, token) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), token));
            await db.Database.MigrateAsync(token);
        });
        TimeSpan callerTime = clock.Elapsed;
        try
        {
            Assert.True(callerTime < TimeSpan.FromSeconds(1), $"Caller blocked for {callerTime}");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(opening.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        await using LocalFleetStore store = await opening;
        Assert.Empty((await store.ReadAsync()).Fleets);
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

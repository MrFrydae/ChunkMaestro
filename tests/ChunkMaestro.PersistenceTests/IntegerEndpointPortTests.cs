using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class IntegerEndpointPortTests : IDisposable
{
    private const string Initial = "20261114034833_InitialFleet";
    private const string Previous = "20261115020704_EnforceEndpointInvariants";
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "cm-integer-port-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Fleet = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Host = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Instance = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(Initial)]
    [InlineData(Previous)]
    public async Task FractionalHistoricalPortRefusedBeforeBackupOrMaterializationWithExplicitRecovery(string version)
    {
        await SeedAsync(root, version);
        await using (SqliteConnection connection = await ConnectAsync(root))
        {
            await ExecuteAsync(connection, "UPDATE Endpoints SET Port=25565.5 WHERE Port=25565");
            Assert.Equal("real:25565.5", await ScalarAsync(connection, "SELECT typeof(Port)||':'||quote(Port) FROM Endpoints WHERE Port=25565.5"));
        }
        string backup = Path.Combine(root, "before-migration-existing.db");
        // Elevated Windows tokens may assign Administrators as the default
        // owner. Create this existing-backup fixture through the same secure
        // file boundary as production, then copy bytes without replacing it.
        using (FileStream backupFile = StorageLocation.CreateOrOpen(backup))
        {
            using FileStream databaseFile = File.OpenRead(Path.Combine(root, "fleet.db"));
            await databaseFile.CopyToAsync(backupFile);
        }
        StorageLocation.CheckScope(backup);
        string before = await RawAsync(root);
        string databaseHash = Hash(Path.Combine(root, "fleet.db"));
        string backupHash = Hash(backup);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(StorageLocation.ForTests(root)));
            Assert.Equal(StorageFailure.CorruptDatabase, error.Failure);
            Assert.Null(error.BackupPath);
            Assert.DoesNotContain("25565", error.Message);
            Assert.DoesNotContain(root, error.Message);
            Assert.Contains("recovery", error.Message);
            Assert.Equal(databaseHash, Hash(Path.Combine(root, "fleet.db")));
            Assert.Equal(backupHash, Hash(backup));
            Assert.Equal([backup], Directory.GetFiles(root, "before-migration-*.db"));
            Assert.Equal(before, await RawAsync(root));
        }

        // Explicit correction only in a separate disposable fixture. Original stays intact.
        string recovered = Path.Combine(root, "reviewed-recovery");
        Prepare(recovered);
        File.Copy(backup, Path.Combine(recovered, "fleet.db"), true);
        await using (SqliteConnection connection = await ConnectAsync(recovered))
        {
            await ExecuteAsync(connection, "UPDATE Endpoints SET Port=25565 WHERE Port=25565.5");
        }
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovered)))
        {
            Assert.Equal(3, Assert.Single((await store.ReadAsync()).Instances).Endpoints.Count);
            FleetStorageException duplicate = await Assert.ThrowsAsync<FleetStorageException>(() => store.RegisterInstanceAsync(
                new InstanceId(Guid.NewGuid()), new HostId(Host), "Duplicate", "duplicate.service", [new Endpoint(EndpointEdition.Java, "alias.example", 25565)]));
            Assert.Equal(StorageFailure.InvalidRegistration, duplicate.Failure);
            Assert.Single((await store.ReadAsync()).Instances);
        }
        Assert.Equal(before, await RawAsync(root));
        Assert.Equal(databaseHash, Hash(Path.Combine(root, "fleet.db")));
        Assert.Equal(backupHash, Hash(backup));
    }

    [Theory]
    [InlineData(Initial)]
    [InlineData(Previous)]
    public async Task PopulatedUpgradeAndCompatibleBackupPreserveIntegerPortsAndRejectFractionalWrites(string version)
    {
        await SeedAsync(root, version);
        string before = await RawAsync(root, false);
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(root)))
        {
            Assert.Equal([1, 25565, 65535], Assert.Single((await store.ReadAsync()).Instances).Endpoints.Select(endpoint => endpoint.Port).Order());
        }
        Assert.Equal(before, await RawAsync(root, false));
        string backup = Assert.Single(Directory.GetFiles(root, "before-migration-*.db"));
        string backupHash = Hash(backup);
        string recovered = Path.Combine(root, "compatible-copy");
        Prepare(recovered);
        File.Copy(backup, Path.Combine(recovered, "fleet.db"), true);
        await using (SqliteConnection connection = await ConnectAsync(recovered))
        {
            await SupportedSchema.VerifyAsync(connection, version, default);
            await using FleetDbContext context = Context(connection);
            Assert.Equal(version, (await context.Database.GetAppliedMigrationsAsync()).Last());
        }
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovered)))
        {
            Assert.Equal(3, Assert.Single((await store.ReadAsync()).Instances).Endpoints.Count);
        }
        Assert.Equal(before, await RawAsync(recovered, false));
        Assert.Equal(backupHash, Hash(backup));
        await using (SqliteConnection connection = await ConnectAsync(root))
        {
            foreach (string sql in new string[]
            {
                "UPDATE Endpoints SET Port=25565.5 WHERE Port=25565",
                "INSERT INTO Endpoints SELECT InstanceId,Edition,'alias.example',25565.5,HostId FROM Endpoints WHERE Port=25565"
            })
            {
                SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql));
                Assert.Equal(275, error.SqliteExtendedErrorCode);
                Assert.Equal(before, await RawAsync(root, false));
            }
            // INTEGER affinity normalizes whole REAL and numeric text; test stored type.
            await ExecuteAsync(connection, "UPDATE Endpoints SET Port=25565.0 WHERE Port=25565");
            Assert.Equal("integer:25565", await ScalarAsync(connection, "SELECT typeof(Port)||':'||quote(Port) FROM Endpoints WHERE Port=25565"));
            await ExecuteAsync(connection, "UPDATE Endpoints SET Port='25565' WHERE Port=25565");
            Assert.Equal("integer:25565", await ScalarAsync(connection, "SELECT typeof(Port)||':'||quote(Port) FROM Endpoints WHERE Port=25565"));
            await using FleetDbContext context = Context(connection);
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Throws<NotSupportedException>(() => context.GetService<IMigrator>().GenerateScript(context.Database.GetMigrations().Last(), version));
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(root));
        Assert.Equal([1, 25565, 65535], Assert.Single((await reopened.ReadAsync()).Instances).Endpoints.Select(endpoint => endpoint.Port).Order());
    }

    [Fact]
    public async Task NewLeafRebuildFailureAfterDropRollsBackRowsSchemaAndHistory()
    {
        await SeedAsync(root, Previous);
        string before = await RawAsync(root);
        await using (SqliteConnection connection = await ConnectAsync(root))
        {
            DropFailureInterceptor interceptor = new DropFailureInterceptor();
            await using FleetDbContext context = new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>()
                .UseSqlite(connection).AddInterceptors(interceptor).Options);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.MigrateAsync());
            Assert.True(interceptor.Observed);
            await SupportedSchema.VerifyAsync(connection, Previous, default);
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM sqlite_schema WHERE name='ef_temp_Endpoints'"));
            Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA integrity_check"));
        }
        Assert.Equal(before, await RawAsync(root));
    }

    private sealed class DropFailureInterceptor : DbCommandInterceptor
    {
        internal bool Observed
        {
            get; private set;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Trim().Equals("DROP TABLE \"Endpoints\";", StringComparison.Ordinal))
            {
                Assert.NotNull(command.Transaction);
                using DbCommand probe = command.Connection!.CreateCommand();
                probe.Transaction = command.Transaction;
                probe.CommandText = "PRAGMA foreign_keys";
                Assert.Equal(1L, await probe.ExecuteScalarAsync(cancellationToken));
                probe.CommandText = "SELECT count(*) FROM ef_temp_Endpoints";
                Assert.Equal(3L, await probe.ExecuteScalarAsync(cancellationToken));
                Observed = true;
                throw new InvalidOperationException("Disposable fixture failure after transactional DROP");
            }
            return result;
        }
    }

    private static async Task SeedAsync(string directory, string version)
    {
        Prepare(directory);
        await using SqliteConnection connection = await ConnectAsync(directory);
        await using FleetDbContext context = Context(connection);
        await context.GetService<IMigrator>().MigrateAsync(version);
        context.Fleets.Add(new FleetRow
        {
            Id = Fleet,
            Name = "Fixture"
        });
        context.Hosts.Add(new HostRow
        {
            Id = Host,
            FleetId = Fleet,
            Name = "Fixture",
            Address = "fixture.example",
            RegisteredAt = 1730000000123
        });
        context.Instances.Add(new InstanceRow
        {
            Id = Instance,
            HostId = Host,
            Name = "Fixture",
            ServiceName = "fixture.service",
            RegisteredAt = 1730000000456
        });
        foreach (int port in new int[]
        {
            1, 25565, 65535
        })
        {
            context.Endpoints.Add(new EndpointRow
            {
                InstanceId = Instance,
                HostId = Host,
                Edition = 0,
                Address = "fixture.example",
                Port = port
            });
        }
        await context.SaveChangesAsync();
    }

    private static void Prepare(string directory)
    {
        StorageLocation.ForTests(directory).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(directory, "fleet.db")))
        {
        }
    }

    private static FleetDbContext Context(SqliteConnection connection)
    {
        return new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options);
    }

    private static async Task<SqliteConnection> ConnectAsync(string directory)
    {
        StorageLocation.PrepareSqliteSidecars(Path.Combine(directory, "fleet.db"));
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> RawAsync(string directory, bool history = true)
    {
        await using SqliteConnection connection = await ConnectAsync(directory);
        List<string> rows = [];
        foreach (string table in history ? new string[]
        {
            "Fleets", "Hosts", "Instances", "Endpoints", "__EFMigrationsHistory"
        } : new string[]
        {
            "Fleets", "Hosts", "Instances", "Endpoints"
        })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                List<string> values = [table];
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    values.Add(Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture)!);
                }
                rows.Add(string.Join("|", values));
            }
        }
        rows.Add((string) (await ScalarAsync(connection, "SELECT group_concat(typeof(Port)||':'||quote(Port),'|') FROM (SELECT Port FROM Endpoints ORDER BY Port)"))!);
        return string.Join("\n", rows);
    }

    private static string Hash(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
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

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

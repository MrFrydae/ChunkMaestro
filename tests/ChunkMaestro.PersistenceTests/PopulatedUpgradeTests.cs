using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace ChunkMaestro.PersistenceTests;

public sealed class PopulatedUpgradeTests(ITestOutputHelper output) : IDisposable
{
    private const string Initial = "20261114034833_InitialFleet";
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "chunkmaestro-upgrade-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Fleet = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Host = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Instance = Guid.Parse("30000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task PopulatedInitialUpgradePreservesAllMetadataAndCompatibleBackup()
    {
        string source = Path.Combine(root, "source");
        string before = await SeedAsync(source);
        string[] versions;
        await using (SqliteConnection connection = await ConnectAsync(source))
        {
            await using FleetDbContext context = Context(connection);
            versions = context.Database.GetMigrations().ToArray();
            Assert.Equal(2, versions.Length);
            Assert.Equal(Initial, versions[0]);
            await SupportedSchema.VerifyAsync(connection, Initial, default);
        }
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(source)))
        {
            FleetInventory inventory = await store.ReadAsync();
            Assert.Equal(new FleetId(Fleet), Assert.Single(inventory.Fleets).Id);
            Assert.Equal(new HostId(Host), Assert.Single(inventory.Hosts).Id);
            Instance instance = Assert.Single(inventory.Instances);
            Assert.Equal(new InstanceId(Instance), instance.Id);
            Assert.Equal(new HostId(Host), instance.HostId);
            Assert.Equal("minecraft@fixture.service", instance.ServiceName);
            Assert.Equal(Observation.Unknown, instance.Observation);
            Assert.Equal([EndpointEdition.Java, EndpointEdition.Java, EndpointEdition.Bedrock, EndpointEdition.Bedrock], instance.Endpoints.Select(e => e.Edition));
        }
        string backup = Assert.Single(Directory.GetFiles(source, "before-migration-*.db"));
        await using (SqliteConnection connection = await ConnectAsync(source))
        {
            Assert.Equal(before, await MetadataAsync(connection));
            await SupportedSchema.VerifyAsync(connection, versions[1], default);
            await using FleetDbContext context = Context(connection);
            Assert.Equal(versions, await context.Database.GetAppliedMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Throws<NotSupportedException>(() => context.GetService<IMigrator>().GenerateScript(versions[1], Initial));
            foreach (int port in new int[]
        {
            0, 65536
        })
            {
                SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"UPDATE Endpoints SET Port={port} WHERE Edition=0 AND Port=1"));
                Assert.Equal(275, failure.SqliteExtendedErrorCode);
            }
            SqliteException editionFailure = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE Endpoints SET Edition=2 WHERE Edition=0 AND Port=1"));
            Assert.Equal(275, editionFailure.SqliteExtendedErrorCode);
            Assert.Equal(before, await MetadataAsync(connection));
        }
        await VerifyOldCopyAsync(backup, before);
        output.WriteLine($"Upgrade {versions[0]} -> {versions[1]}: all row values preserved; backup verified as InitialFleet; CHECK failures use SQLITE_CONSTRAINT_CHECK (275).");
        string recovered = await CopyBackupAsync(backup);
        await using (LocalFleetStore recovery = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovered)))
        {
            Assert.Equal(new InstanceId(Instance), Assert.Single((await recovery.ReadAsync()).Instances).Id);
        }
        await using SqliteConnection restored = await ConnectAsync(recovered);
        Assert.Equal(before, await MetadataAsync(restored));
        Assert.True(File.Exists(Path.Combine(source, "fleet.db")));
        Assert.True(File.Exists(backup));
    }

    [Theory]
    [InlineData("Port", 0)]
    [InlineData("Port", 65536)]
    [InlineData("Edition", -1)]
    [InlineData("Edition", 2)]
    public async Task InvalidExistingEndpointFailsWithoutRepairAndPreservesBackup(string column, int value)
    {
        string before = await SeedAsync(root);
        await using (SqliteConnection connection = await ConnectAsync(root))
        {
            // Column and value are closed test-case inputs, never application input.
            await ExecuteAsync(connection, $"UPDATE Endpoints SET {column}={value} WHERE Edition=0 AND Port=1");
            before = await MetadataAsync(connection);
        }
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(StorageLocation.ForTests(root)));
        Assert.Equal(StorageFailure.MigrationFailed, failure.Failure);
        string backup = Assert.Single(Directory.GetFiles(root, "before-migration-*.db"));
        Assert.Equal(backup, failure.BackupPath);
        await VerifyOldCopyAsync(backup, before);
        await using SqliteConnection original = await ConnectAsync(root);
        Assert.Equal(before, await MetadataAsync(original));
        await using FleetDbContext context = Context(original);
        Assert.Equal([Initial], await context.Database.GetAppliedMigrationsAsync());
        await SupportedSchema.VerifyAsync(original, Initial, default);
        Assert.Equal("ok", await ScalarAsync(original, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task KillAfterNewMigrationDropsPopulatedEndpointsRollsBackAndRecoversSeparately()
    {
        string? childRoot = Environment.GetEnvironmentVariable("CHUNKMAESTRO_ENDPOINT_UPGRADE_CHILD");
        if (childRoot is not null)
        {
            await using SqliteConnection connection = await ConnectAsync(childRoot);
            await using FleetDbContext context = Context(connection, new PauseAfterDropInterceptor(childRoot));
            await context.Database.MigrateAsync();
            Assert.Fail("Child must be killed during the actual forward migration transaction");
        }
        string before = await SeedAsync(root);
        string backup;
        await using (SqliteConnection connection = await ConnectAsync(root))
        {
            backup = await LocalFleetStore.CreateBackupAsync(connection, root, default);
        }
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CHUNKMAESTRO_DOTNET") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(PopulatedUpgradeTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~KillAfterNewMigrationDropsPopulatedEndpointsRollsBackAndRecoversSeparately");
        start.Environment["CHUNKMAESTRO_ENDPOINT_UPGRADE_CHILD"] = root;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Child did not start");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!File.Exists(Path.Combine(root, "drop-executed")) && !child.HasExited)
            {
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(child.HasExited);
            Assert.True(File.Exists(Path.Combine(root, "drop-executed")));
            output.WriteLine(await File.ReadAllTextAsync(Path.Combine(root, "drop-executed")));
            string marker = await File.ReadAllTextAsync(Path.Combine(root, "drop-executed"));
            using Process migrationProcess = Process.GetProcessById(int.Parse(marker.Split('\n')[1], CultureInfo.InvariantCulture));
            migrationProcess.Kill(true);
            await migrationProcess.WaitForExitAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
                await child.WaitForExitAsync();
            }
        }
        await stdout;
        await stderr;
        await using (SqliteConnection original = await ConnectAsync(root))
        {
            Assert.Equal(before, await MetadataAsync(original));
            await SupportedSchema.VerifyAsync(original, Initial, default);
            Assert.Equal("ok", await ScalarAsync(original, "PRAGMA integrity_check"));
            Assert.Equal(0L, await ScalarAsync(original, "SELECT COUNT(*) FROM sqlite_schema WHERE name='ef_temp_Endpoints'"));
            await using FleetDbContext context = Context(original);
            Assert.Equal([Initial], await context.Database.GetAppliedMigrationsAsync());
        }
        // EF's independently committed lock survives process death. Do not clear it or retry blindly.
        FleetStorageException refusal = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(StorageLocation.ForTests(root)));
        Assert.Equal(StorageFailure.MigrationFailed, refusal.Failure);
        await VerifyOldCopyAsync(backup, before);
        string recovery = await CopyBackupAsync(backup);
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovery)))
        {
            Assert.Equal(new InstanceId(Instance), Assert.Single((await store.ReadAsync()).Instances).Id);
        }
        await using SqliteConnection recovered = await ConnectAsync(recovery);
        Assert.Equal(before, await MetadataAsync(recovered));
        output.WriteLine("Killed child after destructive DDL: original metadata, InitialFleet history and schema restored; no temporary table; abandoned lock refused; separate backup copy upgraded successfully.");
    }

    private sealed class PauseAfterDropInterceptor(string directory) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Trim().Equals("DROP TABLE \"Endpoints\";", StringComparison.Ordinal))
            {
                Assert.NotNull(command.Transaction);
                using DbCommand probe = command.Connection!.CreateCommand();
                probe.Transaction = command.Transaction;
                probe.CommandText = "PRAGMA foreign_keys";
                Assert.Equal(1L, await probe.ExecuteScalarAsync(cancellationToken));
                probe.CommandText = "SELECT COUNT(*) FROM ef_temp_Endpoints";
                Assert.Equal(4L, await probe.ExecuteScalarAsync(cancellationToken));
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='Endpoints'";
                Assert.Equal(0L, await probe.ExecuteScalarAsync(cancellationToken));
                using (StorageLocation.CreateOrOpen(Path.Combine(directory, "drop-executed")))
                {
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "drop-executed"), "DROP executed inside transaction; original table absent; four copied endpoints present\n" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }
    }

    private static FleetDbContext Context(SqliteConnection connection, DbCommandInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<FleetDbContext> builder = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        return new FleetDbContext(builder.Options);
    }

    private static async Task<string> SeedAsync(string directory)
    {
        StorageLocation.ForTests(directory).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(directory, "fleet.db")))
        {
        }
        await using SqliteConnection connection = await ConnectAsync(directory);
        await using FleetDbContext context = Context(connection);
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0");
        context.Fleets.Add(new FleetRow
        {
            Id = Fleet,
            Name = "Upgrade fleet"
        });
        context.Hosts.Add(new HostRow
        {
            Id = Host,
            FleetId = Fleet,
            Name = "Upgrade host",
            Address = "fixture.example",
            RegisteredAt = 1730000000123
        });
        context.Instances.Add(new InstanceRow
        {
            Id = Instance,
            HostId = Host,
            Name = "Upgrade instance",
            ServiceName = "minecraft@fixture.service",
            RegisteredAt = 1730000000456
        });
        foreach (int edition in new int[]
        {
            0, 1
        })
        {
            foreach (int port in new int[]
        {
            1, 65535
        })
            {
                context.Endpoints.Add(new EndpointRow
                {
                    InstanceId = Instance,
                    HostId = Host,
                    Edition = edition,
                    Address = edition == 0 ? "java.fixture.example" : "bedrock.fixture.example",
                    Port = port
                });
            }
        }
        await context.SaveChangesAsync();
        return await MetadataAsync(connection);
    }

    private async Task<string> CopyBackupAsync(string backup)
    {
        string directory = Path.Combine(root, "reviewed-recovery");
        StorageLocation.ForTests(directory).Prepare();
        string destination = Path.Combine(directory, "fleet.db");
        using (StorageLocation.CreateOrOpen(destination))
        {
        }
        File.Copy(backup, destination, true);
        await using SqliteConnection connection = await ConnectAsync(directory);
        await SupportedSchema.VerifyAsync(connection, Initial, default);
        return directory;
    }

    private static async Task VerifyOldCopyAsync(string backup, string before)
    {
        await using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await SupportedSchema.VerifyAsync(connection, Initial, default);
        await using FleetDbContext context = Context(connection);
        Assert.Equal([Initial], await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await MetadataAsync(connection));
        Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA integrity_check"));
    }

    private static async Task<string> MetadataAsync(SqliteConnection connection)
    {
        List<string[]> rows = [];
        foreach (string table in new string[]
        {
            "Fleets", "Hosts", "Instances", "Endpoints"
        })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1,2,3,4";
            // Fleets has only two columns; use its complete key order explicitly.
            if (table == "Fleets")
            {
                command.CommandText = "SELECT * FROM Fleets ORDER BY Id";
            }
            using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string[] row = new string[reader.FieldCount + 1];
                row[0] = table;
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    row[index + 1] = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!;
                }
                rows.Add(row);
            }
        }
        return JsonSerializer.Serialize(rows);
    }

    private static async Task<SqliteConnection> ConnectAsync(string directory)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        StorageLocation.PrepareSqliteSidecars(Path.Combine(directory, "fleet.db"));
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

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

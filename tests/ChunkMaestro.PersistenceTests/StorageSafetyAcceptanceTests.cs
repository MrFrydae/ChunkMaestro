using System.Data.Common;
using System.Diagnostics;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class StorageSafetyAcceptanceTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "chunkmaestro-safety-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PopulatedSupportedSchemaBackupRestoresExactIdentitiesSeparately()
    {
        string source = Path.Combine(directory, "source");
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        string backup;
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(source)))
        {
            await store.RegisterFleetAsync(fleet, "Recovery fleet");
            await store.RegisterHostAsync(host, fleet, "Recovery host", "mc.example");
            await store.RegisterInstanceAsync(instance, host, "Recovery instance", "minecraft.service", [new Endpoint(EndpointEdition.Java, "mc.example", 25565)]);
            await using SqliteConnection connection = await ConnectAsync(Path.Combine(source, "fleet.db"));
            backup = await LocalFleetStore.CreateBackupAsync(connection, source, default);
        }
        string recovery = Path.Combine(directory, "recovery");
        StorageLocation.ForTests(recovery).Prepare();
        string copy = Path.Combine(recovery, "fleet.db");
        using (StorageLocation.CreateOrOpen(copy))
        {
        }
        File.Copy(backup, copy, true);
        await using LocalFleetStore restored = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovery));
        FleetInventory inventory = await restored.ReadAsync();
        Assert.Equal(fleet, Assert.Single(inventory.Fleets).Id);
        Host restoredHost = Assert.Single(inventory.Hosts);
        Assert.Equal(host, restoredHost.Id);
        Assert.Equal(fleet, restoredHost.FleetId);
        Instance restoredInstance = Assert.Single(inventory.Instances);
        Assert.Equal(instance, restoredInstance.Id);
        Assert.Equal(host, restoredInstance.HostId);
        Assert.Equal("minecraft.service", restoredInstance.ServiceName);
        Assert.Equal(25565, Assert.Single(restoredInstance.Endpoints).Port);
        Assert.Equal(Observation.Unknown, restoredInstance.Observation);
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(Path.Combine(source, "fleet.db")));
    }

    [Fact]
    public async Task ActualEfMigrationAtSqlitePageLimitRollsBackAndPreservesCompletedBackup()
    {
        StorageLocation.ForTests(directory).Prepare();
        string database = Path.Combine(directory, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        await using SqliteConnection connection = await ConnectAsync(database);
        string backup = await LocalFleetStore.CreateBackupAsync(connection, directory, default);
        PageLimitInterceptor limit = new PageLimitInterceptor();
        DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).AddInterceptors(limit).Options;
        await using FleetDbContext context = new FleetDbContext(options);
        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => context.Database.MigrateAsync());
        Assert.Equal(13, failure.SqliteErrorCode);
        Assert.True(limit.Armed);
        using SqliteCommand check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Fleets'";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
        await using SqliteConnection recovered = await ConnectAsync(backup);
        using SqliteCommand recoveryCheck = recovered.CreateCommand();
        recoveryCheck.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await recoveryCheck.ExecuteScalarAsync());
        recoveryCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Fleets'";
        Assert.Equal(0L, await recoveryCheck.ExecuteScalarAsync());
    }

    [Fact]
    public async Task KillDuringActualEfDdlPreservesBackupAndRollsBackUncommittedSchema()
    {
        string? childRoot = Environment.GetEnvironmentVariable("CHUNKMAESTRO_MIGRATION_CHILD");
        if (childRoot is not null)
        {
            DbContextOptions<FleetDbContext> childOptions = new DbContextOptionsBuilder<FleetDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(childRoot, "fleet.db"),
                    Pooling = false
                }.ToString()).AddInterceptors(new PauseAfterDdlInterceptor(childRoot)).Options;
            await using FleetDbContext childContext = new FleetDbContext(childOptions);
            await childContext.Database.MigrateAsync();
            Assert.Fail("Migration child must be killed after real DDL, before commit");
        }
        StorageLocation.ForTests(directory).Prepare();
        string database = Path.Combine(directory, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        string backup;
        await using (SqliteConnection original = await ConnectAsync(database))
        {
            backup = await LocalFleetStore.CreateBackupAsync(original, directory, default);
        }
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CHUNKMAESTRO_DOTNET") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(StorageSafetyAcceptanceTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~KillDuringActualEfDdlPreservesBackupAndRollsBackUncommittedSchema");
        start.Environment["CHUNKMAESTRO_MIGRATION_CHILD"] = directory;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Child failed to start");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(directory, "ddl-executed")) && !child.HasExited)
            {
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(child.HasExited);
            Assert.True(File.Exists(Path.Combine(directory, "ddl-executed")));
            // The interceptor signals only after SQLite has executed CREATE TABLE in EF's transaction.
            child.Kill(true);
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
        await using SqliteConnection reopened = await ConnectAsync(database);
        using SqliteCommand check = reopened.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Fleets'";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
        Assert.True(File.Exists(backup));
        await using SqliteConnection recovered = await ConnectAsync(backup);
        using SqliteCommand recoveryCheck = recovered.CreateCommand();
        recoveryCheck.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await recoveryCheck.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BoundedSqliteFullDuringBackupPreservesPopulatedSource()
    {
        StorageLocation.ForTests(directory).Prepare();
        string sourcePath = Path.Combine(directory, "source.db");
        string targetPath = Path.Combine(directory, "partial.db");
        using (StorageLocation.CreateOrOpen(sourcePath))
        {
        }
        using (StorageLocation.CreateOrOpen(targetPath))
        {
        }
        await using SqliteConnection source = await ConnectAsync(sourcePath);
        using SqliteCommand populate = source.CreateCommand();
        populate.CommandText = "CREATE TABLE Evidence (Id TEXT PRIMARY KEY, Payload BLOB); INSERT INTO Evidence VALUES ('preserved-id', zeroblob(1048576))";
        await populate.ExecuteNonQueryAsync();
        await using SqliteConnection target = await ConnectAsync(targetPath);
        using SqliteCommand limit = target.CreateCommand();
        limit.CommandText = "PRAGMA max_page_count=1";
        Assert.Equal(1L, await limit.ExecuteScalarAsync());
        SqliteException failure = Assert.Throws<SqliteException>(() => source.BackupDatabase(target));
        Assert.Equal(13, failure.SqliteErrorCode);
        populate.CommandText = "SELECT length(Payload) FROM Evidence WHERE Id='preserved-id'";
        Assert.Equal(1048576L, await populate.ExecuteScalarAsync());
        populate.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await populate.ExecuteScalarAsync());
        // This is an actual sqlite3_backup failure bounded by max_page_count, not filesystem ENOSPC.
        string completed = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        Assert.NotEqual(targetPath, completed);
        await using SqliteConnection recovered = await ConnectAsync(completed);
        using SqliteCommand verify = recovered.CreateCommand();
        verify.CommandText = "SELECT length(Payload) FROM Evidence WHERE Id='preserved-id'";
        Assert.Equal(1048576L, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task KillAfterActualSqliteBackupStepPreservesWalAndCompletedCopy()
    {
        string? childRoot = Environment.GetEnvironmentVariable("CHUNKMAESTRO_BACKUP_CHILD");
        if (childRoot is not null)
        {
            await using SqliteConnection childSource = await ConnectAsync(Path.Combine(childRoot, "source.db"));
            await using SqliteConnection childTarget = await ConnectAsync(Path.Combine(childRoot, "partial.db"));
            using SQLitePCL.sqlite3_backup operation = SQLitePCL.raw.sqlite3_backup_init(childTarget.Handle, "main", childSource.Handle, "main");
            Assert.NotNull(operation);
            Assert.Equal(0, SQLitePCL.raw.sqlite3_backup_step(operation, 1));
            Assert.True(SQLitePCL.raw.sqlite3_backup_remaining(operation) > 0);
            await File.WriteAllTextAsync(Path.Combine(childRoot, "backup-stepped"), "one actual native page copied; more remain");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            Assert.Fail("Backup child must be killed while the native backup remains incomplete");
        }
        StorageLocation.ForTests(directory).Prepare();
        string sourcePath = Path.Combine(directory, "source.db");
        string partialPath = Path.Combine(directory, "partial.db");
        using (StorageLocation.CreateOrOpen(sourcePath))
        {
        }
        using (StorageLocation.CreateOrOpen(partialPath))
        {
        }
        await using SqliteConnection source = await ConnectAsync(sourcePath);
        using SqliteCommand command = source.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Evidence (Id TEXT PRIMARY KEY, Payload BLOB); INSERT INTO Evidence VALUES ('wal-id', zeroblob(1048576))";
        await command.ExecuteNonQueryAsync();
        string completed = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        byte[] wal = await File.ReadAllBytesAsync(sourcePath + "-wal");
        Assert.NotEmpty(wal);
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CHUNKMAESTRO_DOTNET") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(StorageSafetyAcceptanceTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~KillAfterActualSqliteBackupStepPreservesWalAndCompletedCopy");
        start.Environment["CHUNKMAESTRO_BACKUP_CHILD"] = directory;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Child failed to start");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(directory, "backup-stepped")) && !child.HasExited)
            {
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(child.HasExited);
            Assert.True(File.Exists(Path.Combine(directory, "backup-stepped")));
            child.Kill(true);
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
        Assert.Equal(wal, await File.ReadAllBytesAsync(sourcePath + "-wal"));
        Assert.True(File.Exists(partialPath));
        Assert.NotEqual(partialPath, completed);
        command.CommandText = "SELECT length(Payload) FROM Evidence WHERE Id='wal-id'";
        Assert.Equal(1048576L, await command.ExecuteScalarAsync());
        await using SqliteConnection recovery = await ConnectAsync(completed);
        using SqliteCommand verify = recovery.CreateCommand();
        verify.CommandText = "SELECT length(Payload) FROM Evidence WHERE Id='wal-id'";
        Assert.Equal(1048576L, await verify.ExecuteScalarAsync());
        verify.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await verify.ExecuteScalarAsync());
    }

    private sealed class PauseAfterDdlInterceptor(string root) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TABLE \"Fleets\"", StringComparison.Ordinal))
            {
                Assert.NotNull(command.Transaction);
                await File.WriteAllTextAsync(Path.Combine(root, "ddl-executed"), command.CommandText, cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }
    }

    private sealed class PageLimitInterceptor : DbCommandInterceptor
    {
        public bool Armed
        {
            get; private set;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Armed && command.CommandText.Contains("CREATE TABLE \"Fleets\"", StringComparison.Ordinal))
            {
                using DbCommand limit = command.Connection!.CreateCommand();
                limit.Transaction = command.Transaction;
                limit.CommandText = "PRAGMA page_count";
                long pages = Convert.ToInt64(limit.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                limit.CommandText = "PRAGMA max_page_count=" + pages.ToString(System.Globalization.CultureInfo.InvariantCulture);
                limit.ExecuteScalar();
                Armed = true;
            }
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<SqliteConnection> ConnectAsync(string path)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

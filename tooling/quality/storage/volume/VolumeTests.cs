using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class VolumeTests
{
    [Fact]
    public async Task ActualFullVolumeRejectsBackupAndEfMigrationWithoutLosingSource()
    {
        string root = Environment.GetEnvironmentVariable("CHUNKMAESTRO_DISPOSABLE_VOLUME") ?? throw new InvalidOperationException("Run volume.py; never supply an existing volume");
        string directory = Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N"));
        StorageLocation.ForTests(directory).Prepare();
        string database = Path.Combine(directory, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        await using SqliteConnection source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false
        }.ToString());
        await source.OpenAsync();
        using SqliteCommand command = source.CreateCommand();
        command.CommandText = "CREATE TABLE Evidence (Id TEXT PRIMARY KEY, Payload BLOB); INSERT INTO Evidence VALUES ('full-volume-id', zeroblob(2097152))";
        await command.ExecuteNonQueryAsync();
        string completed = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string migrationPath = Path.Combine(directory, "migration.db");
        using (StorageLocation.CreateOrOpen(migrationPath))
        {
        }
        await using SqliteConnection migration = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = migrationPath,
            Pooling = false
        }.ToString());
        await migration.OpenAsync();
        string filler = Path.Combine(directory, "owned-filler");
        // Bound writes to 128 MiB even if a fixture mount failed. Never fill a host disk.
        using (FileStream fill = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            byte[] block = new byte[4096];
            new Random(41).NextBytes(block);
            bool full = false;
            try
            {
                for (int i = 0; i < 32768; i++)
                {
                    fill.Write(block);
                    fill.Flush(true);
                }
            }
            catch (IOException)
            {
                full = true;
            }
            Assert.True(full, "Disposable 64 MiB volume must fill within the 128 MiB write bound");
        }
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => LocalFleetStore.CreateBackupAsync(source, directory, default));
            Assert.NotNull(failure);
            Assert.True(failure is IOException || failure is SqliteException sql && sql.SqliteErrorCode == 13, failure.ToString());
            DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(migration).Options;
            await using FleetDbContext context = new FleetDbContext(options);
            SqliteException migrationFailure = await Assert.ThrowsAsync<SqliteException>(() => context.Database.MigrateAsync());
            // SQLite can report CANTOPEN when a full filesystem cannot create the journal.
            Assert.True(migrationFailure.SqliteErrorCode is 13 or 14, migrationFailure.ToString());
        }
        finally
        {
            File.Delete(filler);
        }
        command.CommandText = "SELECT length(Payload) FROM Evidence WHERE Id='full-volume-id'";
        Assert.Equal(2097152L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
        await using SqliteConnection recovery = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = completed,
            Pooling = false
        }.ToString());
        await recovery.OpenAsync();
        using SqliteCommand verify = recovery.CreateCommand();
        verify.CommandText = "SELECT length(Payload) FROM Evidence WHERE Id='full-volume-id'";
        Assert.Equal(2097152L, await verify.ExecuteScalarAsync());
        using SqliteCommand migrationCheck = migration.CreateCommand();
        migrationCheck.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await migrationCheck.ExecuteScalarAsync());
        migrationCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Fleets'";
        Assert.Equal(0L, await migrationCheck.ExecuteScalarAsync());
    }
}

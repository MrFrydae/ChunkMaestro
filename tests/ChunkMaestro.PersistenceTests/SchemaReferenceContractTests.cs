using ChunkMaestro.Application;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class SchemaReferenceContractTests
{
    [Fact]
    public async Task SupportedSchemaRejectsMissingParentWithoutRepairAndAcceptsReviewedRemoval()
    {
        await using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options;
        await using FleetDbContext db = new FleetDbContext(options);
        await db.Database.MigrateAsync();
        string migration = db.Database.GetMigrations().Last();
        await SupportedSchema.VerifyAsync(connection, migration, default);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF; INSERT INTO Hosts VALUES ('private-orphan','private-missing-parent','private-label','private.example',0)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => SupportedSchema.VerifyAsync(connection, migration, default));
            Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));
            Assert.DoesNotContain(failure.Message, char.IsControl);
            Assert.DoesNotContain("private-", failure.ToString());
            Assert.DoesNotContain("private.example", failure.ToString());
            Assert.DoesNotContain("SQLite Error", failure.ToString());
            Assert.Null(failure.InnerException);
            Assert.Null(failure.BackupPath);
            command.CommandText = "SELECT COUNT(*) FROM Hosts WHERE Id='private-orphan' AND FleetId='private-missing-parent'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM Fleets";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        command.CommandText = "DELETE FROM Hosts WHERE Id='private-orphan'; PRAGMA foreign_keys=ON";
        await command.ExecuteNonQueryAsync();
        await SupportedSchema.VerifyAsync(connection, migration, default);
    }
}

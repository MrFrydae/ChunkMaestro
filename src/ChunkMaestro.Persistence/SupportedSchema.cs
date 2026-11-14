using ChunkMaestro.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ChunkMaestro.Persistence;

internal static class SupportedSchema
{
    internal static async Task VerifyAsync(SqliteConnection actual, string? migration, CancellationToken token)
    {
        await using SqliteConnection expected = new SqliteConnection("Data Source=:memory:");
        await expected.OpenAsync(token);
        DbContextOptions<FleetDbContext> options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(expected).Options;
        await using FleetDbContext reference = new FleetDbContext(options);
        if (migration != null)
        {
            await reference.GetService<IMigrator>().MigrateAsync(migration, token);
        }
        string[] wanted = await ObjectsAsync(expected, token);
        string[] found = await ObjectsAsync(actual, token);
        if (!wanted.SequenceEqual(found, StringComparer.Ordinal))
        {
            throw new FleetStorageException(StorageFailure.UnsupportedSchema,
                "Database structure differs from the supported migration schema. Preserve files for reviewed recovery.");
        }
        using SqliteCommand command = actual.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            throw new FleetStorageException(StorageFailure.CorruptDatabase,
                "Database contains invalid references. Preserve files for reviewed recovery.");
        }
    }

    private static async Task<string[]> ObjectsAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        // sqlite_schema SQL is SQLite's stored authoritative DDL: includes columns, keys,
        // constraints, FK actions, collations, partial indexes, triggers and views. Exact
        // comparison intentionally rejects even benign manual rewrites; no SQL parser guesses.
        command.CommandText = "SELECT type, name, tbl_name, coalesce(sql,'') FROM sqlite_schema " +
            "WHERE substr(name,1,7) != 'sqlite_' ORDER BY type, name";
        using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(token);
        List<string> result = [];
        while (await reader.ReadAsync(token))
        {
            result.Add(string.Join('\0', reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return result.ToArray();
    }
}

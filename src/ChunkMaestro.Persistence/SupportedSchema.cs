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
        using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
            {
                throw new FleetStorageException(StorageFailure.CorruptDatabase,
                    "Database contains invalid references. Preserve files for reviewed recovery.");
            }
        }
        if (migration != null)
        {
            // INTEGER affinity permits fractional REAL values. Refuse before upgrade
            // backup or provider materialization can truncate a persisted reservation.
            // Keep historical integer range failures on the existing migration path.
            command.CommandText = "SELECT 1 FROM Endpoints WHERE typeof(Port)<>'integer' LIMIT 1";
            if (await command.ExecuteScalarAsync(token) is not null)
            {
                throw new FleetStorageException(StorageFailure.CorruptDatabase,
                    "Database contains invalid endpoint ports. Preserve files for reviewed recovery.");
            }
            await VerifyIdentitiesAsync(actual, token);
            command.CommandText = "SELECT 1 FROM Hosts WHERE typeof(RegisteredAt)<>'integer' " +
                "OR RegisteredAt<$minimum OR RegisteredAt>$maximum " +
                "UNION ALL SELECT 1 FROM Instances WHERE typeof(RegisteredAt)<>'integer' " +
                "OR RegisteredAt<$minimum OR RegisteredAt>$maximum LIMIT 1";
            command.Parameters.AddWithValue("$minimum", DateTimeOffset.MinValue.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$maximum", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
            if (await command.ExecuteScalarAsync(token) is not null)
            {
                throw new FleetStorageException(StorageFailure.CorruptDatabase,
                    "Database contains invalid registration timestamps. Preserve files for reviewed recovery.");
            }
            command.Parameters.Clear();
            // Both individual foreign keys can be valid while their host pair is
            // inconsistent. Port ownership must use the instance's actual host.
            command.CommandText = "SELECT 1 FROM Endpoints AS e JOIN Instances AS i ON e.InstanceId=i.Id " +
                "WHERE e.HostId<>i.HostId LIMIT 1";
            if (await command.ExecuteScalarAsync(token) is not null)
            {
                throw new FleetStorageException(StorageFailure.CorruptDatabase,
                    "Database contains inconsistent endpoint ownership. Preserve files for reviewed recovery.");
            }
        }
    }

    private static async Task VerifyIdentitiesAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Fleets UNION ALL SELECT Id FROM Hosts " +
            "UNION ALL SELECT FleetId FROM Hosts UNION ALL SELECT Id FROM Instances " +
            "UNION ALL SELECT HostId FROM Instances UNION ALL SELECT InstanceId FROM Endpoints " +
            "UNION ALL SELECT HostId FROM Endpoints";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            try
            {
                Guid identity = reader.GetGuid(0);
                // SQLite compares persisted keys literally. GetGuid alone also
                // accepts alternate text and BLOB forms that could materialize
                // as the same public identity as a different stored primary key.
                if (identity != Guid.Empty && reader.GetValue(0) is string encoded &&
                    string.Equals(encoded, identity.ToString("D").ToUpperInvariant(), StringComparison.Ordinal))
                {
                    continue;
                }
            }
            catch (Exception error) when (error is FormatException or InvalidCastException)
            {

            }
            throw new FleetStorageException(StorageFailure.CorruptDatabase,
                "Database contains invalid identities. Preserve files for reviewed recovery.");
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

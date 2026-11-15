using ChunkMaestro.Application;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ChunkMaestro.Persistence;

/// <summary>Copies bounded native page batches and publishes only a verified, closed recovery point.</summary>
internal static class IncrementalBackupDriver
{
    internal static Task<string> CreateAsync(SqliteConnection source, string directory,
        Func<SqliteConnection, CancellationToken, Task> verify, Action<int>? afterStep, CancellationToken token)
        => Task.Run(() => CreateWorkerAsync(source, directory, verify, afterStep, token), token);

    private static async Task<string> CreateWorkerAsync(SqliteConnection source, string directory,
        Func<SqliteConnection, CancellationToken, Task> verify, Action<int>? afterStep, CancellationToken token)
    {
        string identifier = Guid.NewGuid().ToString("N");
        string partial = Path.Combine(directory, $".backup-{identifier}.partial");
        string completed = Path.Combine(directory, $"before-migration-{identifier}.db");
        bool created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using (FileStream candidate = CreateCandidate(partial))
            {
                created = true;
            }
            using (SqliteConnection destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = partial,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 0
            }.ToString()))
            {
                await destination.OpenAsync(token);
                Copy(source, destination, afterStep, token);
                token.ThrowIfCancellationRequested();
                // A standalone recovery point must not depend on WAL sidecars.
                using (SqliteCommand journal = destination.CreateCommand())
                {
                    journal.CommandText = "PRAGMA journal_mode=DELETE";
                    if (!string.Equals(Convert.ToString(await journal.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture), "delete", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unavailable();
                    }
                }
                await verify(destination, token);
            }
            using (FileStream candidate = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                candidate.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, completed, false);
            return completed;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw Unavailable();
        }
        finally
        {
            if (created)
            {
                // Delete only this invocation's unpublished files. A crash can leave these
                // explicitly partial files for reviewed cleanup, never a completed name.
                string[] suffixes = ["", "-journal", "-wal", "-shm"];
                foreach (string suffix in suffixes)
                {
                    try
                    {
                        File.Delete(partial + suffix);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Cleanup refusal must not mask cancellation or the original failure.
                    }
                }
            }
        }
    }

    private static void Copy(SqliteConnection source, SqliteConnection destination, Action<int>? afterStep, CancellationToken token)
    {
        using sqlite3_backup operation = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
        if (operation.IsInvalid)
        {
            throw Unavailable();
        }
        int finish;
        try
        {
            int result;
            do
            {
                token.ThrowIfCancellationRequested();
                result = raw.sqlite3_backup_step(operation, 32);
                if (result != raw.SQLITE_OK && result != raw.SQLITE_DONE)
                {
                    throw Unavailable();
                }
                // Test-only observation occurs strictly after a successful real native step.
                afterStep?.Invoke(raw.sqlite3_backup_remaining(operation));
            }
            while (result != raw.SQLITE_DONE);
        }
        finally
        {
            // Dispose alone drops the native finish result. Explicit finish both checks
            // that result and invalidates the SafeHandle, preventing a second finish.
            finish = raw.sqlite3_backup_finish(operation);
        }
        if (finish != raw.SQLITE_OK)
        {
            throw Unavailable();
        }
    }

    private static FileStream CreateCandidate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw StorageLocation.Invalid();
            FileSecurity security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private static FleetStorageException Unavailable()
        => new(StorageFailure.StorageUnavailable, "Backup could not be completed. Existing database and completed backups were preserved; no migration was attempted.");
}

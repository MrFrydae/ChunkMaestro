using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class BackupPublicationTests : IDisposable
{
    private static readonly string[] OriginalNames = ["fleet.db", "fleet.db-wal", "before-migration-preserved.db"];
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "chunkmaestro-publication-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishedBackupRestoresCommittedWalThroughPublicStore()
    {
        await using SqliteConnection source = await SeedAsync();
        string wal = Path.Combine(directory, "fleet.db-wal");
        Assert.True(new FileInfo(wal).Length > 0);
        int steps = 0;
        string backup = await LocalFleetStore.CreateBackupAsync(source, directory, default, remaining =>
        {
            steps++;
            Assert.DoesNotContain(Directory.GetFiles(directory, "before-migration-*.db"), path => path != Baseline);
            Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
        });
        Assert.True(steps > 1);
        Assert.Matches("^before-migration-[a-f0-9]{32}\\.db$", Path.GetFileName(backup));
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        string recovery = Path.Combine(directory, "recovery");
        StorageLocation.ForTests(recovery).Prepare();
        string database = Path.Combine(recovery, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        File.Copy(backup, database, true);
        await using LocalFleetStore restored = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovery));
        Assert.Equal(4096, (await restored.ReadAsync()).Fleets.Count);
        Assert.Equal("preserved", File.ReadAllText(Baseline));
    }

    [Fact]
    public async Task CancellationAfterActualStepCleansCandidateAndPreservesOriginal()
    {
        await using SqliteConnection source = await SeedAsync();
        string[] before = OriginalHashes();
        long sharedMemoryLength = new FileInfo(Path.Combine(directory, "fleet.db-shm")).Length;
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        int remainingPages = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFleetStore.CreateBackupAsync(source, directory, cancellation.Token, remaining =>
        {
            remainingPages = remaining;
            cancellation.Cancel();
        }));
        Assert.True(remainingPages > 0);
        Assert.Equal(before, OriginalHashes());
        Assert.Equal(sharedMemoryLength, new FileInfo(Path.Combine(directory, "fleet.db-shm")).Length);
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        Assert.Equal(Baseline, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
    }

    [Fact]
    public async Task CallerReturnsWhileActualNativeCopyIsPausedAndCanCancel()
    {
        await using SqliteConnection source = await SeedAsync();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource<int> stepped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> backup = LocalFleetStore.CreateBackupAsync(source, directory, cancellation.Token, remaining =>
        {
            stepped.SetResult(remaining);
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        });
        try
        {
            Assert.True(await stepped.Task.WaitAsync(TimeSpan.FromSeconds(10)) > 0);
            Assert.False(backup.IsCompleted);
            cancellation.Cancel();
        }
        finally
        {
            resume.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
    }

    [Fact]
    public async Task DestinationCreationFailureIsSanitizedAndDoesNotPublishOrChangeSource()
    {
        await using SqliteConnection source = await SeedAsync();
        string[] before = OriginalHashes();
        long sharedMemoryLength = new FileInfo(Path.Combine(directory, "fleet.db-shm")).Length;
        // A regular file as the destination parent deterministically refuses creation on both OSes.
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() =>
            LocalFleetStore.CreateBackupAsync(source, Baseline, default));
        Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
        Assert.DoesNotContain(directory, failure.Message);
        Assert.Equal(before, OriginalHashes());
        Assert.Equal(sharedMemoryLength, new FileInfo(Path.Combine(directory, "fleet.db-shm")).Length);
        Assert.Equal("preserved", File.ReadAllText(Baseline));
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
    }

    [Fact]
    public async Task PublicationCollisionPreservesExistingCompletedFileAndCleansCandidate()
    {
        await using SqliteConnection source = await SeedAsync();
        string? collision = null;
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() =>
            LocalFleetStore.CreateBackupAsync(source, directory, default, remaining =>
            {
                if (collision is null)
                {
                    string partial = Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
                    string identifier = Path.GetFileName(partial)[8..^8];
                    collision = Path.Combine(directory, $"before-migration-{identifier}.db");
                    using FileStream reserved = StorageLocation.CreateOrOpen(collision);
                    reserved.WriteByte(42);
                }
            }));
        Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
        Assert.Equal(42, Assert.Single(File.ReadAllBytes(collision!)));
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        Assert.Equal("preserved", File.ReadAllText(Baseline));
    }

    [Fact]
    public async Task NativeStepRefusalAbortsWithoutRetryAndReleasesBackupHandle()
    {
        await using SqliteConnection source = await SeedAsync();
        SqliteTransaction? transaction = null;
        int steps = 0;
        try
        {
            FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() =>
                LocalFleetStore.CreateBackupAsync(source, directory, default, remaining =>
                {
                    steps++;
                    Assert.True(remaining > 0);
                    transaction = source.BeginTransaction();
                }));
            Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
            Assert.Equal(1, steps);
        }
        finally
        {
            transaction?.Dispose();
        }
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        // A fresh explicit request succeeds after the caller resolves contention.
        Assert.True(File.Exists(await LocalFleetStore.CreateBackupAsync(source, directory, default)));
    }

    [Fact]
    public async Task KillAfterProductionStepLeavesOnlyUnpublishedCandidateAndRecoverableOriginal()
    {
        string? childDirectory = Environment.GetEnvironmentVariable("CHUNKMAESTRO_PUBLICATION_CHILD");
        if (childDirectory is not null)
        {
            await using SqliteConnection source = await ConnectAsync(Path.Combine(childDirectory, "fleet.db"));
            await LocalFleetStore.CreateBackupAsync(source, childDirectory, default, remaining =>
            {
                Assert.True(remaining > 0);
                using NamedPipeClientStream signal = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("CHUNKMAESTRO_PUBLICATION_PIPE")!, PipeDirection.InOut);
                signal.Connect(15000);
                signal.Write(BitConverter.GetBytes(Environment.ProcessId));
                signal.Write(BitConverter.GetBytes(remaining));
                signal.Flush();
                signal.ReadByte();
            });
            Assert.Fail("Child must be killed before backup completion");
            return;
        }
        await using SqliteConnection original = await SeedAsync();
        string[] before = OriginalHashes();
        long sharedMemoryLength = new FileInfo(Path.Combine(directory, "fleet.db-shm")).Length;
        string pipeName = "cm-backup-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream signal = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CHUNKMAESTRO_DOTNET") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(BackupPublicationTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~KillAfterProductionStepLeavesOnlyUnpublishedCandidateAndRecoverableOriginal");
        start.Environment["CHUNKMAESTRO_PUBLICATION_CHILD"] = directory;
        start.Environment["CHUNKMAESTRO_PUBLICATION_PIPE"] = pipeName;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start backup child");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await signal.WaitForConnectionAsync(deadline.Token);
            byte[] evidence = new byte[8];
            await signal.ReadExactlyAsync(evidence, deadline.Token);
            using Process worker = Process.GetProcessById(BitConverter.ToInt32(evidence, 0));
            Assert.True(BitConverter.ToInt32(evidence, 4) > 0);
            Assert.False(child.HasExited);
            Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
            Assert.Equal(Baseline, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
            foreach (string candidate in Directory.GetFiles(directory, ".backup-*"))
            {
                StorageLocation.CheckScope(candidate);
            }
            worker.Kill(true);
            await worker.WaitForExitAsync(deadline.Token);
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
        Assert.Equal(before, OriginalHashes());
        Assert.Equal(sharedMemoryLength, new FileInfo(Path.Combine(directory, "fleet.db-shm")).Length);
        Assert.Equal("preserved", File.ReadAllText(Baseline));
        Assert.Equal(Baseline, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
        await original.DisposeAsync();
        await using LocalFleetStore recovered = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        Assert.Equal(4096, (await recovered.ReadAsync()).Fleets.Count);
        Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
    }

    private string Baseline => Path.Combine(directory, "before-migration-preserved.db");

    private async Task<SqliteConnection> SeedAsync()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
        }
        foreach (string backup in Directory.GetFiles(directory, "before-migration-*.db"))
        {
            File.Delete(backup);
        }
        using (FileStream baseline = StorageLocation.CreateOrOpen(Baseline))
        {
            baseline.Write(System.Text.Encoding.UTF8.GetBytes("preserved"));
        }
        SqliteConnection source = await ConnectAsync(Path.Combine(directory, "fleet.db"));
        using SqliteCommand command = source.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint=0; WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<4096) INSERT INTO Fleets(Id,Name) SELECT printf('00000000-0000-0000-0000-%012d',x), printf('%0120d',x) FROM n;";
        await command.ExecuteNonQueryAsync();
        return source;
    }

    private static async Task<SqliteConnection> ConnectAsync(string database)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private string[] OriginalHashes()
        => OriginalNames
            .Select(name => HashLiveFile(Path.Combine(directory, name))).ToArray();

    private static string HashLiveFile(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

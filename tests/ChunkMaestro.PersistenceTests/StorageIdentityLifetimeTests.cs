using ChunkMaestro.Application;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

[SupportedOSPlatform("macos")]
public sealed class StorageIdentityLifetimeTests : IDisposable
{
    private readonly string root = Path.Combine("/private/tmp", "chunkmaestro-identity-lifetime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RejectedConstructorClosesNativeDirectoryDescriptorBeforeReturningFailure()
    {
        StorageLocation.ForTests(root).Prepare();
        Assert.Empty(OpenFixturePaths());
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        try
        {
            FleetStorageException failure = Assert.Throws<FleetStorageException>(() => new StorageIdentity(root));
            Assert.Equal(StorageFailure.InvalidLocation, failure.Failure);
            Assert.DoesNotContain(root, failure.ToString(), StringComparison.Ordinal);
            // macOS permits rename/unlink with an open descriptor: inspect the kernel instead.
            Assert.Empty(OpenFixturePaths());
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using (StorageIdentity retry = new StorageIdentity(root))
        {
            retry.Verify();
            Assert.Contains(root, OpenFixturePaths());
        }
        Assert.Empty(OpenFixturePaths());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedBindingReleasesPartialCapturesAndRequiresTheExactControllerPath(bool expectedControllerExists)
    {
        CreateDatabase();
        string expected = Path.Combine(root, "controller.lock");
        if (expectedControllerExists)
        {
            using FileStream file = StorageLocation.CreateOrOpen(expected);
            file.WriteByte(37);
        }
        string borrowed = Path.Combine(root, "borrowed.lock");
        using (FileStream caller = StorageLocation.CreateOrOpen(borrowed))
        {
            using (StorageIdentity identity = new StorageIdentity(root))
            {
                FleetStorageException failure = Assert.Throws<FleetStorageException>(() => identity.BindFiles(caller));
                Assert.Equal(StorageFailure.InvalidLocation, failure.Failure);
                Assert.DoesNotContain(root, failure.ToString(), StringComparison.Ordinal);
            }
            Assert.Equal(borrowed, Assert.Single(OpenFixturePaths()));
            caller.WriteByte(91);
            caller.Flush();
            caller.Position = 0;
            Assert.Equal(91, caller.ReadByte());
            Assert.Equal(-1, caller.ReadByte());
            Assert.Equal(expectedControllerExists, File.Exists(expected));
            if (expectedControllerExists)
            {
                Assert.Equal(new byte[]
                {
                    37
                }, File.ReadAllBytes(expected));
            }
        }
        Assert.Empty(OpenFixturePaths());
        using (FileStream controller = StorageLocation.CreateOrOpen(expected))
        {
            using StorageIdentity retry = new StorageIdentity(root);
            retry.BindFiles(controller);
            retry.Verify();
        }
        Assert.Empty(OpenFixturePaths());
        AssertDatabasePreserved();
    }

    [Fact]
    public void RepeatedDisposalClosesCapturesButRetainsCallerLockUntilCallerDisposes()
    {
        CreateDatabase();
        string controllerPath = Path.Combine(root, "controller.lock");
        using (FileStream controller = StorageLocation.CreateOrOpen(controllerPath))
        {
            using StorageIdentity identity = new StorageIdentity(root);
            identity.BindFiles(controller);
            identity.Verify();
            Assert.Contains(root, OpenFixturePaths());
            Assert.Contains(Path.Combine(root, "fleet.db"), OpenFixturePaths());
            identity.Dispose();
            identity.Dispose();
            Assert.Equal(controllerPath, Assert.Single(OpenFixturePaths()));
            // The borrowed descriptor still owns the real nonblocking macOS flock.
            Assert.Throws<IOException>(() => StorageLocation.CreateOrOpen(controllerPath));
            controller.WriteByte(43);
            controller.Flush();
            Assert.Equal(1, controller.Length);
        }
        Assert.Empty(OpenFixturePaths());
        using (FileStream reopened = StorageLocation.CreateOrOpen(controllerPath))
        {
            Assert.Equal(43, reopened.ReadByte());
            using StorageIdentity retry = new StorageIdentity(root);
            retry.BindFiles(reopened);
            retry.Verify();
        }
        Assert.Empty(OpenFixturePaths());
        AssertDatabasePreserved();
    }

    private void CreateDatabase()
    {
        StorageLocation.ForTests(root).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(root, "fleet.db")))
        {
        }
        using SqliteConnection connection = Connect();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE lifetime_sentinel(value TEXT NOT NULL); INSERT INTO lifetime_sentinel VALUES ('retained')";
        command.ExecuteNonQuery();
    }

    private void AssertDatabasePreserved()
    {
        using SqliteConnection connection = Connect();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM lifetime_sentinel";
        Assert.Equal("retained", command.ExecuteScalar());
        command.CommandText = "PRAGMA quick_check";
        Assert.Equal("ok", command.ExecuteScalar());
    }

    private SqliteConnection Connect()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "fleet.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
    }

    private string[] OpenFixturePaths()
    {
        // lsof queries the kernel for this process, never StorageIdentity's private fields.
        // Filter a unique fixture path so parallel suites cannot change these observations.
        ProcessStartInfo start = new ProcessStartInfo("/usr/sbin/lsof")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-nP");
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add("-p");
        string processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        start.ArgumentList.Add(processId);
        start.ArgumentList.Add("-Fpn");
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            Assert.Fail("macOS lsof descriptor inspection exceeded ten seconds.");
        }
        string[] lines = output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(process.ExitCode is 0 or 1, error.GetAwaiter().GetResult());
        Assert.True(string.IsNullOrWhiteSpace(error.GetAwaiter().GetResult()));
        Assert.Contains("p" + processId, lines);
        return lines.Where(line => line.StartsWith('n')).Select(line => line[1..])
            .Where(path => path == root || path.StartsWith(root + "/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

}

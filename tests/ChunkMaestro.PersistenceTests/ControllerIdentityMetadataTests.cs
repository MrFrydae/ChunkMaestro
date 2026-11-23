using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class ControllerIdentityMetadataTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "cm-metadata-" + Guid.NewGuid().ToString("N"));
    private StorageLocation Location => StorageLocation.ForTests(directory);
    private string Database => Path.Combine(directory, "fleet.db");

    [Fact]
    public async Task PendingReopensPublishesExactlyOnceAndCopiesMutableMaterial()
    {
        ControllerIdentityRow pending = Create();
        byte[] original = pending.CertificateDer.ToArray();
        await using (LocalFleetStore store = await SeedAsync(pending))
        {
            Task begin = store.BeginIdentityMetadataAsync(pending);
            pending.CertificateDer[0] ^= 1;
            await begin;
            pending.CertificateDer = original;
            ControllerIdentityRow read = Assert.IsType<ControllerIdentityRow>(await store.ReadIdentityMetadataAsync(pending.FleetId));
            Assert.Equal(0, read.State);
            Assert.Null(read.PublishedAt);
            Assert.Equal(pending.Reference, read.Reference);
            read.CertificateDer[0] ^= 1;
            Assert.Equal(original, (await store.ReadIdentityMetadataAsync(pending.FleetId))!.CertificateDer);
            await Assert.ThrowsAsync<FleetStorageException>(() => store.BeginIdentityMetadataAsync(pending));
        }
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            Assert.Equal(original, (await store.ReadIdentityMetadataAsync(pending.FleetId))!.CertificateDer);
            await store.PublishIdentityMetadataAsync(pending, pending.CreatedAt + 1);
            await Assert.ThrowsAsync<FleetStorageException>(() => store.PublishIdentityMetadataAsync(pending, pending.CreatedAt + 2));
            await Assert.ThrowsAsync<FleetStorageException>(() => store.BeginIdentityMetadataAsync(pending));
            Assert.Single((await store.ReadAsync()).Fleets);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        ControllerIdentityRow ready = Assert.IsType<ControllerIdentityRow>(await reopened.ReadIdentityMetadataAsync(pending.FleetId));
        Assert.Equal(1, ready.State);
        Assert.Equal(pending.CreatedAt + 1, ready.PublishedAt);
        Assert.Equal(original, ready.CertificateDer);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("fleet")]
    [InlineData("controller")]
    [InlineData("reference")]
    [InlineData("hash")]
    [InlineData("spki")]
    [InlineData("der")]
    [InlineData("profile")]
    [InlineData("purpose")]
    [InlineData("created")]
    [InlineData("publication")]
    public async Task ChangedScopeOrPayloadCannotPublish(string change)
    {
        ControllerIdentityRow pending = Create();
        await using LocalFleetStore store = await SeedAsync(pending);
        await store.BeginIdentityMetadataAsync(pending);
        ControllerIdentityRow wrong = pending.Copy();
        long published = pending.CreatedAt;
        switch (change)
        {
            case "attempt":
                wrong.AttemptId = Guid.NewGuid();
                break;
            case "fleet":
                wrong.FleetId = Guid.NewGuid();
                break;
            case "controller":
                wrong.ControllerId = Guid.NewGuid();
                break;
            case "reference":
                wrong.CredentialId = Guid.NewGuid();
                break;
            case "hash":
                wrong.CertificateHash[0] ^= 1;
                break;
            case "spki":
                wrong.SpkiHash[0] ^= 1;
                break;
            case "der":
                wrong.CertificateDer[0] ^= 1;
                break;
            case "profile":
                wrong.ProfileVersion = 2;
                break;
            case "purpose":
                wrong.Purpose = "wrong";
                break;
            case "created":
                wrong.CreatedAt++;
                break;
            case "publication":
                published--;
                break;
        }
        await Assert.ThrowsAsync<FleetStorageException>(() => store.PublishIdentityMetadataAsync(wrong, published));
        ControllerIdentityRow read = Assert.IsType<ControllerIdentityRow>(await store.ReadIdentityMetadataAsync(pending.FleetId));
        Assert.Equal(0, read.State);
        Assert.Equal(pending.AttemptId, read.AttemptId);
        Assert.Null(read.PublishedAt);
    }

    [Fact]
    public async Task MissingFleetAndCrossRowRebindingAreRefused()
    {
        ControllerIdentityRow pending = Create();
        await using LocalFleetStore store = await SeedAsync(pending);
        ControllerIdentityRow missing = Create();
        await Assert.ThrowsAsync<FleetStorageException>(() => store.BeginIdentityMetadataAsync(missing));
        await store.BeginIdentityMetadataAsync(pending);
        await store.RegisterFleetAsync(new FleetId(missing.FleetId), "Other");
        foreach (string field in new[]
        {
            "attempt", "reference", "controller"
        })
        {
            ControllerIdentityRow duplicate = field == "controller" ? Create(missing.FleetId, pending.ControllerId) : missing.Copy();
            if (field == "attempt")
            {
                duplicate.AttemptId = pending.AttemptId;
            }
            if (field == "reference")
            {
                duplicate.CredentialId = pending.CredentialId;
            }
            await Assert.ThrowsAsync<FleetStorageException>(() => store.BeginIdentityMetadataAsync(duplicate));
        }
        Assert.Null(await store.ReadIdentityMetadataAsync(missing.FleetId));
    }

    [Fact]
    public async Task CancellationDisposalAndInjectedStatementRollbackPreservePending()
    {
        ControllerIdentityRow pending = Create();
        LocalFleetStore store = await SeedAsync(pending);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginIdentityMetadataAsync(pending, cancelled.Token));
        Assert.Null(await store.ReadIdentityMetadataAsync(pending.FleetId));
        await store.BeginIdentityMetadataAsync(pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishIdentityMetadataAsync(pending, pending.CreatedAt, cancelled.Token));
        await ExecuteAsync("CREATE TRIGGER metadata_fault AFTER UPDATE ON ControllerIdentities BEGIN SELECT RAISE(ABORT, 'synthetic fault'); END");
        await Assert.ThrowsAsync<FleetStorageException>(() => store.PublishIdentityMetadataAsync(pending, pending.CreatedAt));
        Assert.Equal(0, (await store.ReadIdentityMetadataAsync(pending.FleetId))!.State);
        await ExecuteAsync("DROP TRIGGER metadata_fault");
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadIdentityMetadataAsync(pending.FleetId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PublishIdentityMetadataAsync(pending, pending.CreatedAt));
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        Assert.Equal(pending.AttemptId, (await reopened.ReadIdentityMetadataAsync(pending.FleetId))!.AttemptId);
    }

    [Theory]
    [InlineData("State=2")]
    [InlineData("PublishedAt=1")]
    [InlineData("CreatedAt=0.5")]
    [InlineData("CreatedAt=253402300800000")]
    [InlineData("AttemptId='00000000-0000-0000-0000-000000000000'")]
    [InlineData("CredentialId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'")]
    [InlineData("CertificateHash=zeroblob(32)")]
    [InlineData("SpkiHash=zeroblob(32)")]
    [InlineData("CertificateDer=x'00'")]
    [InlineData("ProfileVersion=2")]
    [InlineData("Purpose='wrong'")]
    public async Task LogicalCorruptionRefusedWithoutChangingDatabaseOrCompletedBackup(string assignment)
    {
        ControllerIdentityRow pending = Create();
        await using (LocalFleetStore store = await SeedAsync(pending))
        {
            await store.BeginIdentityMetadataAsync(pending);
        }
        string backup = Assert.Single(Directory.GetFiles(directory, "before-migration-*.db"));
        byte[] savedBackup = await File.ReadAllBytesAsync(backup);
        await ExecuteAsync("PRAGMA ignore_check_constraints=ON; UPDATE ControllerIdentities SET " + assignment);
        byte[] before = await File.ReadAllBytesAsync(Database);
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
        Assert.Null(failure.InnerException);
        Assert.Equal(before, await File.ReadAllBytesAsync(Database));
        Assert.Equal(savedBackup, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task MissingUniqueConstraintIsRefusedWithoutRepair()
    {
        ControllerIdentityRow pending = Create();
        await using (LocalFleetStore store = await SeedAsync(pending))
        {
            await store.BeginIdentityMetadataAsync(pending);
        }
        await ExecuteAsync("DROP INDEX IX_ControllerIdentities_AttemptId");
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.UnsupportedSchema, failure.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BackupCopyRetainsExactNonsecretAttemptAndPublicationState(int state)
    {
        ControllerIdentityRow pending = Create();
        string backup;
        await using (LocalFleetStore store = await SeedAsync(pending))
        {
            await store.BeginIdentityMetadataAsync(pending);
            if (state == 1)
            {
                await store.PublishIdentityMetadataAsync(pending, pending.CreatedAt);
            }
            await using SqliteConnection connection = await ConnectAsync();
            backup = await LocalFleetStore.CreateBackupAsync(connection, directory, default);
        }
        string recovered = Path.Combine(directory, "recovery");
        StorageLocation.ForTests(recovered).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(recovered, "fleet.db")))
        {
        }
        File.Copy(backup, Path.Combine(recovered, "fleet.db"), true);
        await using LocalFleetStore restored = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovered));
        ControllerIdentityRow read = Assert.IsType<ControllerIdentityRow>(await restored.ReadIdentityMetadataAsync(pending.FleetId));
        Assert.Equal(state, read.State);
        Assert.Equal(pending.AttemptId, read.AttemptId);
        Assert.Equal(pending.Reference, read.Reference);
        Assert.Equal(pending.CertificateDer, read.CertificateDer);
        Assert.Equal(pending.CertificateHash, read.CertificateHash);
        Assert.Equal(pending.SpkiHash, read.SpkiHash);
        Assert.Equal(state == 0 ? (long?) null : pending.CreatedAt, read.PublishedAt);
    }

    [Fact]
    public async Task GenuinePopulatedThirdMigrationUpgradesAndKeepsCompatibleBackup()
    {
        ControllerIdentityRow pending = Create();
        Location.Prepare();
        using (StorageLocation.CreateOrOpen(Database))
        {
        }
        const string prior = "20261117171405_EnforceIntegerEndpointPorts";
        await using (SqliteConnection connection = await ConnectAsync())
        {
            await using FleetDbContext db = new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options);
            await db.GetService<IMigrator>().MigrateAsync(prior);
            db.Fleets.Add(new FleetRow
            {
                Id = pending.FleetId,
                Name = "Prior fleet"
            });
            await db.SaveChangesAsync();
            await SupportedSchema.VerifyAsync(connection, prior, default);
        }
        await using (LocalFleetStore upgraded = await LocalFleetStore.OpenAsync(Location))
        {
            Assert.Single((await upgraded.ReadAsync()).Fleets);
            Assert.Null(await upgraded.ReadIdentityMetadataAsync(pending.FleetId));
            await upgraded.BeginIdentityMetadataAsync(pending);
        }
        string backup = Assert.Single(Directory.GetFiles(directory, "before-migration-*.db"));
        SqliteConnectionStringBuilder backupOptions = new()
        {
            DataSource = backup,
            Pooling = false
        };
        await using (SqliteConnection copy = new(backupOptions.ToString()))
        {
            await copy.OpenAsync();
            await SupportedSchema.VerifyAsync(copy, prior, default);
            using SqliteCommand count = copy.CreateCommand();
            count.CommandText = "SELECT count(*) FROM Fleets";
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        // The verified backup must be released before fixture cleanup on Windows.
        using FileStream releasedBackup = File.Open(backup, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(releasedBackup.Length > 0);
    }

    [Theory]
    [InlineData("State=2")]
    [InlineData("State=1")]
    [InlineData("PublishedAt=1")]
    [InlineData("CreatedAt=0.5")]
    [InlineData("CertificateDer=zeroblob(4097)")]
    [InlineData("CertificateHash=zeroblob(31)")]
    [InlineData("SpkiHash='wrong'")]
    [InlineData("ControllerId='invalid'")]
    [InlineData("ProfileVersion=2")]
    [InlineData("Purpose='wrong'")]
    public async Task SqlConstraintsEnforceRealWrites(string assignment)
    {
        ControllerIdentityRow pending = Create();
        await using LocalFleetStore store = await SeedAsync(pending);
        await store.BeginIdentityMetadataAsync(pending);
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE ControllerIdentities SET " + assignment));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(0, (await store.ReadIdentityMetadataAsync(pending.FleetId))!.State);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM Fleets"));
    }

    [Fact]
    public async Task ConcurrentBeginAndPublicationEachHaveOneWinner()
    {
        ControllerIdentityRow pending = Create();
        await using LocalFleetStore store = await SeedAsync(pending);
        Task first = store.BeginIdentityMetadataAsync(pending);
        Task second = store.BeginIdentityMetadataAsync(pending);
        await AssertOneWinnerAsync(first, second);
        first = store.PublishIdentityMetadataAsync(pending, pending.CreatedAt);
        second = store.PublishIdentityMetadataAsync(pending, pending.CreatedAt + 1);
        await AssertOneWinnerAsync(first, second);
        long? publication = (await store.ReadIdentityMetadataAsync(pending.FleetId))!.PublishedAt;
        Assert.True(publication == pending.CreatedAt || publication == pending.CreatedAt + 1);
    }

    private static async Task AssertOneWinnerAsync(Task first, Task second)
    {
        try
        {
            await Task.WhenAll(first, second);
        }
        catch (FleetStorageException)
        {
        }
        Assert.Equal(1, new Task[]
        {
            first, second
        }.Count(task => task.IsCompletedSuccessfully));
        Task failure = Assert.Single(new Task[]
        {
            first, second
        }, task => task.IsFaulted);
        Assert.IsType<FleetStorageException>(failure.Exception!.InnerException);
    }

    [Fact]
    public async Task ExpiredPublicMetadataSurvivesReopenWithoutRegeneration()
    {
        ControllerIdentityRow pending = Create(instant: new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using (LocalFleetStore store = await SeedAsync(pending))
        {
            await store.BeginIdentityMetadataAsync(pending);
            await store.PublishIdentityMetadataAsync(pending, pending.CreatedAt);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        Assert.Equal(pending.CertificateDer, (await reopened.ReadIdentityMetadataAsync(pending.FleetId))!.CertificateDer);
        Assert.Single((await reopened.ReadAsync()).Fleets);
    }

    [Theory]
    [InlineData("CHECK")]
    [InlineData("FK")]
    public async Task AlteredConstraintsAreRefusedWithoutRepair(string kind)
    {
        ControllerIdentityRow pending = Create();
        await using (LocalFleetStore store = await SeedAsync(pending))
        {
            await store.BeginIdentityMetadataAsync(pending);
        }
        await ExecuteAsync(kind == "CHECK"
            ? "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql, 'length(CertificateDer) BETWEEN 1 AND 4096', 'length(CertificateDer) BETWEEN 1 AND 8192') WHERE name='ControllerIdentities'; PRAGMA writable_schema=OFF"
            : "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql, 'ON DELETE RESTRICT', 'ON DELETE CASCADE') WHERE name='ControllerIdentities'; PRAGMA writable_schema=OFF");
        byte[] before = await File.ReadAllBytesAsync(Database);
        FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.UnsupportedSchema, error.Failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Database));
    }

    [Theory]
    [InlineData("FleetId='invalid'")]
    [InlineData("ControllerId='00000000-0000-0000-0000-000000000000'")]
    [InlineData("AttemptId=zeroblob(16)")]
    [InlineData("CredentialId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'")]
    [InlineData("ProfileVersion=2")]
    [InlineData("Purpose='wrong'")]
    [InlineData("State=2")]
    [InlineData("CertificateDer='wrong'")]
    [InlineData("CertificateDer=zeroblob(4097)")]
    [InlineData("CertificateHash='wrong'")]
    [InlineData("SpkiHash='wrong'")]
    [InlineData("CreatedAt=0.5")]
    [InlineData("CreatedAt=253402300800000")]
    [InlineData("PublishedAt=0.5")]
    [InlineData("PublishedAt=1")]
    public async Task InternalReadValidatesLogicalRowsWithoutRelyingOnQuickCheck(string assignment)
    {
        ControllerIdentityRow pending = Create();
        await using LocalFleetStore store = await SeedAsync(pending);
        await store.BeginIdentityMetadataAsync(pending);
        await ExecuteAsync("PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; UPDATE ControllerIdentities SET " + assignment);
        FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() => store.ReadIdentityMetadataAsync(pending.FleetId));
        Assert.Equal(StorageFailure.CorruptDatabase, error.Failure);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InvalidRecordArgumentsCannotBeginOrCreateMetadata()
    {
        ControllerIdentityRow pending = Create();
        await using LocalFleetStore store = await SeedAsync(pending);
        Action<ControllerIdentityRow>[] invalid =
        [
            row => row.FleetId = Guid.Empty,
            row => row.ControllerId = Guid.Empty,
            row => row.AttemptId = Guid.Empty,
            row => row.CredentialId = Guid.Empty,
            row => row.State = 2,
            row => row.CreatedAt = -62135596800001L,
            row => row.CertificateDer = null!,
            row => row.CertificateHash = null!,
            row => row.SpkiHash = null!,
            row => row.CertificateHash = new byte[31],
            row => row.SpkiHash = new byte[31],
            row => row.CertificateDer = new byte[4097],
            row => row.State = ControllerIdentityRow.Ready,
            row =>
            {
                row.State = ControllerIdentityRow.Ready;
                row.PublishedAt = row.CreatedAt - 1;
            },
            row =>
            {
                row.State = ControllerIdentityRow.Ready;
                row.PublishedAt = 253402300800000L;
            },
            row =>
            {
                row.State = ControllerIdentityRow.Ready;
                row.PublishedAt = row.CreatedAt;
            }
        ];
        foreach (Action<ControllerIdentityRow> mutate in invalid)
        {
            ControllerIdentityRow wrong = pending.Copy();
            mutate(wrong);
            await Assert.ThrowsAsync<FleetStorageException>(() => store.BeginIdentityMetadataAsync(wrong));
        }
        Assert.Null(await store.ReadIdentityMetadataAsync(pending.FleetId));
    }

    private async Task<LocalFleetStore> SeedAsync(ControllerIdentityRow row)
    {
        LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await store.RegisterFleetAsync(new FleetId(row.FleetId), "Metadata fixture");
        return store;
    }

    private async Task<SqliteConnection> ConnectAsync()
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Database,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using SqliteConnection connection = await ConnectAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // Public certificate fixture only; no private material is exported or stored.
    private static ControllerIdentityRow Create(Guid? fleetId = null, Guid? controllerId = null, DateTimeOffset? instant = null)
    {
        Guid fleet = fleetId ?? Guid.NewGuid();
        Guid controller = controllerId ?? Guid.NewGuid();
        DateTimeOffset created = instant ?? new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=ChunkMaestro Fleet Controller CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        SubjectAlternativeNameBuilder san = new();
        san.AddUri(new Uri($"https://identity.chunkmaestro.invalid/v1/fleets/{fleet:D}/controllers/{controller:D}"));
        request.CertificateExtensions.Add(san.Build(false));
        byte[] serial = new byte[16];
        serial[0] = 1;
        using X509Certificate2 certificate = request.Create(request.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
            created.AddMinutes(-5), created.AddYears(5), serial);
        return new ControllerIdentityRow
        {
            FleetId = fleet,
            ControllerId = controller,
            AttemptId = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(),
            ProfileVersion = 1,
            Purpose = ControllerIdentityRow.RootPurpose,
            CertificateDer = certificate.RawData,
            CertificateHash = SHA256.HashData(certificate.RawData),
            SpkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo()),
            CreatedAt = created.ToUnixTimeMilliseconds()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

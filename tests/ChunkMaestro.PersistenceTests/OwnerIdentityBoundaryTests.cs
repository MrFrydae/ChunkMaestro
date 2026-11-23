using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class OwnerIdentityBoundaryTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "cm-owner-denial-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("initialize", false)]
    [InlineData("recover", false)]
    [InlineData("verify", false)]
    [InlineData("initialize", true)]
    [InlineData("recover", true)]
    [InlineData("verify", true)]
    public async Task PublicUseCasesPreserveRealSqliteAndRetainedStore(string intent, bool ready)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        FleetId fleet = new(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Nonsecret fleet");
        ControllerIdentityRow row = Create(fleet.Value);
        await store.BeginIdentityMetadataAsync(row);
        if (ready)
        {
            await store.PublishIdentityMetadataAsync(row, row.CreatedAt + 1);
        }
        string before = await SnapshotAsync();
        await using (OwnerFleetSession owner = new(store))
        {
            OwnerIdentityObservation observation = await owner.ObserveAsync(fleet);
            Assert.True(observation.FleetExists);
            Assert.Equal(OwnerIdentityResult.Unsupported, observation.Capability);
            Assert.Equal(OwnerIdentityResult.Unsupported, await InvokeAsync(owner, observation, intent));
            Assert.Equal(before, await SnapshotAsync());
            Assert.Single((await store.ReadAsync()).Fleets);
        }
        // Owner session disposal does not dispose or reopen the borrowed controller session.
        Assert.Single((await store.ReadAsync()).Fleets);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("recover")]
    [InlineData("verify")]
    public async Task UnknownStaleForgedCanceledAndClosedRequestsHaveZeroIdentityEffects(string intent)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        FleetId fleet = new(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Nonsecret fleet");
        string before = await SnapshotAsync();
        OwnerFleetSession owner = new(store);
        OwnerIdentityObservation unknown = await owner.ObserveAsync(new FleetId(Guid.NewGuid()));
        Assert.False(unknown.FleetExists);
        Assert.Equal(OwnerIdentityResult.Refused, await InvokeAsync(owner, unknown, intent));
        OwnerIdentityObservation stale = await owner.ObserveAsync(fleet);
        OwnerIdentityObservation current = await owner.ObserveAsync(fleet);
        Assert.Equal(OwnerIdentityResult.Refused, await InvokeAsync(owner, stale, intent));
        Assert.Equal(OwnerIdentityResult.Refused, await InvokeAsync(owner, current with
        {
            Fleet = new FleetId(Guid.NewGuid())
        }, intent));
        Assert.Equal(OwnerIdentityResult.Refused, await InvokeAsync(owner, current with
        {
        }, intent));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Equal(OwnerIdentityResult.Refused, await InvokeAsync(owner, current, intent, canceled.Token));
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        Assert.Equal(OwnerIdentityResult.Refused, await InvokeAsync(owner, current, intent));
        Assert.Equal(OwnerIdentityResult.Refused, (await owner.ObserveAsync(fleet)).Capability);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Single((await store.ReadAsync()).Fleets);
    }

    [Fact]
    public async Task DisposedBorrowedStoreCannotBecomeAuthorization()
    {
        LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        FleetId fleet = new(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Nonsecret fleet");
        await using OwnerFleetSession owner = new(store);
        OwnerIdentityObservation prior = await owner.ObserveAsync(fleet);
        await store.DisposeAsync();
        // Unsupported is still a denial, even when a previous inventory snapshot was valid.
        Assert.Equal(OwnerIdentityResult.Unsupported, await owner.InitializeAsync(prior));
        Assert.Equal(OwnerIdentityResult.Refused, (await owner.ObserveAsync(fleet)).Capability);
    }

    private static Task<OwnerIdentityResult> InvokeAsync(OwnerFleetSession owner, OwnerIdentityObservation observation, string intent, CancellationToken token = default)
    {
        return intent switch
        {
            "initialize" => owner.InitializeAsync(observation, token),
            "recover" => owner.RecoverExactAsync(observation, token),
            _ => owner.VerifyExistingAsync(observation, token)
        };
    }

    // Exact logical schema + every persisted value, including blobs and migration/identity rows.
    // Read-only snapshot excludes normal SQLite WAL/checkpoint byte layout, not metadata.
    private async Task<string> SnapshotAsync()
    {
        await using SqliteConnection db = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await db.OpenAsync();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master ORDER BY name";
        List<string> rows = new();
        List<string> tables = new();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                rows.Add(name + ":" + (reader.IsDBNull(1) ? "NULL" : reader.GetString(1)));
                tables.Add(name);
            }
        }
        foreach (string table in tables)
        {
            command.CommandText = "SELECT type FROM sqlite_master WHERE name=$name";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$name", table);
            if ((string?) await command.ExecuteScalarAsync() != "table")
            {
                continue;
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            List<string> contents = new();
            while (await reader.ReadAsync())
            {
                List<string> values = new();
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    object value = reader.GetValue(column);
                    values.Add(value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL");
                }
                contents.Add(System.Text.Json.JsonSerializer.Serialize(values));
            }
            rows.Add(table + ":" + string.Join("|", contents.Order(StringComparer.Ordinal)));
        }
        return string.Join("\n", rows);
    }

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

using ChunkMaestro.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChunkMaestro.Persistence;

public sealed partial class LocalFleetStore
{
    // Internal metadata only: the future composed service must commit Pending BEFORE
    // native creation, reopen the exact native key, validate the full current profile
    // and separately prove possession BEFORE calling PublishIdentityMetadataAsync.
    internal Task BeginIdentityMetadataAsync(ControllerIdentityRow pending, CancellationToken token = default)
    {
        ControllerIdentityRow captured = Capture(pending);
        if (captured.State != ControllerIdentityRow.Pending || captured.PublishedAt != null)
        {
            throw MetadataConflict();
        }
        return MetadataAsync(async db =>
        {
            db.ControllerIdentities.Add(captured);
            await db.SaveChangesAsync(token);
            return true;
        }, token);
    }

    internal Task<ControllerIdentityRow?> ReadIdentityMetadataAsync(Guid fleetId, CancellationToken token = default)
    {
        Identity(fleetId);
        return MetadataAsync(async db =>
        {
            await VerifyIdentityMetadataAsync(session, token);
            ControllerIdentityRow? row = await db.ControllerIdentities.AsNoTracking().SingleOrDefaultAsync(x => x.FleetId == fleetId, token);
            return row?.Copy();
        }, token);
    }

    // A replay is always a conflict, including an otherwise identical Ready row.
    // PublishedAt is immutable. No proof bool or signing entry point is exposed.
    internal Task PublishIdentityMetadataAsync(ControllerIdentityRow expectedPending, long publishedAt, CancellationToken token = default)
    {
        ControllerIdentityRow captured = Capture(expectedPending);
        if (captured.State != ControllerIdentityRow.Pending || captured.PublishedAt != null || publishedAt < captured.CreatedAt || publishedAt > 253402300799999L)
        {
            throw MetadataConflict();
        }
        return MetadataAsync(async db =>
        {
            await VerifyIdentityMetadataAsync(session, token);
            int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ControllerIdentities SET State=1, PublishedAt={publishedAt}
                WHERE FleetId={captured.FleetId} AND ControllerId={captured.ControllerId}
                  AND AttemptId={captured.AttemptId} AND CredentialId={captured.CredentialId}
                  AND ProfileVersion={captured.ProfileVersion} AND Purpose={captured.Purpose}
                  AND CertificateDer={captured.CertificateDer} AND CertificateHash={captured.CertificateHash}
                  AND SpkiHash={captured.SpkiHash} AND CreatedAt={captured.CreatedAt}
                  AND State=0 AND PublishedAt IS NULL
                """, token);
            if (changed != 1)
            {
                throw MetadataConflict();
            }
            return true;
        }, token);
    }

    private static ControllerIdentityRow Capture(ControllerIdentityRow value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.CertificateDer is null || value.CertificateHash is null || value.SpkiHash is null ||
            value.CertificateDer.Length > 4096 || value.CertificateHash.Length != 32 || value.SpkiHash.Length != 32)
        {
            throw ControllerIdentityRow.Invalid();
        }
        ControllerIdentityRow captured = value.Copy();
        captured.Validate();
        return captured;
    }

    private Task<T> MetadataAsync<T>(Func<FleetDbContext, Task<T>> operation, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                identity.Verify();
                await using FleetDbContext db = new(options);
                try
                {
                    return await operation(db);
                }
                finally
                {
                    // A post-commit refusal cannot promise rollback; reopen the same attempt.
                    identity.Verify();
                }
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sql && sql.SqliteErrorCode == 19)
            {
                throw MetadataConflict();
            }
            catch (Exception ex) when (ex is SqliteException or DbUpdateException or IOException or UnauthorizedAccessException)
            {
                throw new FleetStorageException(StorageFailure.StorageUnavailable,
                    "Controller metadata operation unavailable; outcome may be unknown. Reopen the same attempt; no mutation was replayed.");
            }
            finally
            {
                gate.Release();
            }
        }, token);
    }

    private static FleetStorageException MetadataConflict()
    {
        return new FleetStorageException(StorageFailure.InvalidRegistration,
            "Controller metadata conflict. Preserve the existing identity and reconcile the same attempt.");
    }

    internal static async Task VerifyIdentityMetadataAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FleetId,ControllerId,AttemptId,CredentialId,ProfileVersion,Purpose,State,CertificateDer,CertificateHash,SpkiHash,CreatedAt,PublishedAt FROM ControllerIdentities";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            try
            {
                Guid[] ids = new Guid[4];
                for (int index = 0; index < ids.Length; index++)
                {
                    if (reader.GetValue(index) is not string text || !Guid.TryParseExact(text, "D", out ids[index]) ||
                        ids[index] == Guid.Empty || !string.Equals(text, ids[index].ToString("D").ToUpperInvariant(), StringComparison.Ordinal))
                    {
                        throw ControllerIdentityRow.Invalid();
                    }
                }
                if (reader.GetValue(4) is not long profile || profile != 1 || reader.GetValue(5) is not string purpose ||
                    reader.GetValue(6) is not long state || state is < 0 or > 1 ||
                    reader.GetValue(7) is not byte[] certificate || certificate.Length is < 1 or > 4096 ||
                    reader.GetValue(8) is not byte[] certificateHash || reader.GetValue(9) is not byte[] spkiHash ||
                    reader.GetValue(10) is not long created || (!reader.IsDBNull(11) && reader.GetValue(11) is not long))
                {
                    throw ControllerIdentityRow.Invalid();
                }
                ControllerIdentityRow row = new()
                {
                    FleetId = ids[0],
                    ControllerId = ids[1],
                    AttemptId = ids[2],
                    CredentialId = ids[3],
                    ProfileVersion = (int) profile,
                    Purpose = purpose,
                    State = (int) state,
                    CertificateDer = certificate,
                    CertificateHash = certificateHash,
                    SpkiHash = spkiHash,
                    CreatedAt = created,
                    PublishedAt = reader.IsDBNull(11) ? null : reader.GetInt64(11)
                };
                row.Validate();
            }
            catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
            {
                throw ControllerIdentityRow.Invalid();
            }
        }
    }
}

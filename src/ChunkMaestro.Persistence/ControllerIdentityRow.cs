using ChunkMaestro.Application;
using ChunkMaestro.ControllerIdentity;
using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;

namespace ChunkMaestro.Persistence;

// Public material only. A row, including Ready, is never evidence of private-key possession.
internal sealed class ControllerIdentityRow
{
    internal const int Pending = 0;
    internal const int Ready = 1;
    internal const string RootPurpose = "controller-root-v1";
    public Guid FleetId
    {
        get; set;
    }

    public Guid ControllerId
    {
        get; set;
    }

    public Guid AttemptId
    {
        get; set;
    }

    public Guid CredentialId
    {
        get; set;
    }

    public int ProfileVersion
    {
        get; set;
    }

    public string Purpose
    {
        get; set;
    } = "";

    public int State
    {
        get; set;
    }

    public byte[] CertificateDer
    {
        get; set;
    } = [];

    public byte[] CertificateHash
    {
        get; set;
    } = [];

    public byte[] SpkiHash
    {
        get; set;
    } = [];

    public long CreatedAt
    {
        get; set;
    }

    public long? PublishedAt
    {
        get; set;
    }

    [NotMapped]
    internal CredentialReference Reference => new(new CredentialScope(new FleetId(FleetId), ControllerId, Purpose), CredentialId);

    internal ControllerIdentityRow Copy()
    {
        return new ControllerIdentityRow
        {
            FleetId = FleetId,
            ControllerId = ControllerId,
            AttemptId = AttemptId,
            CredentialId = CredentialId,
            ProfileVersion = ProfileVersion,
            Purpose = Purpose,
            State = State,
            CertificateDer = (byte[]) CertificateDer.Clone(),
            CertificateHash = (byte[]) CertificateHash.Clone(),
            SpkiHash = (byte[]) SpkiHash.Clone(),
            CreatedAt = CreatedAt,
            PublishedAt = PublishedAt
        };
    }

    internal void Validate()
    {
        if (FleetId == Guid.Empty || ControllerId == Guid.Empty || AttemptId == Guid.Empty || CredentialId == Guid.Empty ||
            ProfileVersion != RootProfile.Version || Purpose != RootPurpose || State is < Pending or > Ready ||
            CreatedAt < -62135596800000L || CreatedAt > 253402300799999L ||
            (State == Pending ? PublishedAt != null : PublishedAt == null || PublishedAt < CreatedAt || PublishedAt > 253402300799999L))
        {
            throw Invalid();
        }
        try
        {
            // Recorded-time validation preserves expired public inventory. Authority/use must
            // separately check current validity AND reopen the native exact key with possession.
            RootProfile.Validate(CertificateDer, FleetId, ControllerId, CertificateHash, SpkiHash,
                DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt));
        }
        catch (CryptographicException)
        {
            throw Invalid();
        }
    }

    internal static FleetStorageException Invalid()
    {
        return new FleetStorageException(StorageFailure.CorruptDatabase,
            "Invalid controller identity metadata. Preserve files and native references for reviewed recovery.");
    }
}

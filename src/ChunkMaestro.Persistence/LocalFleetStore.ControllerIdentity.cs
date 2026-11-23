using ChunkMaestro.ControllerIdentity;
using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChunkMaestro.Persistence;

internal sealed record RootIdentityLocator(Guid Fleet, Guid Controller, Guid Attempt, Guid Credential);

public sealed partial class LocalFleetStore
{
    // Trusted-core only. No owner assertion or public initialization/authorization API.
    // Lock order: operation lease -> DB gate -> release DB gate -> native store.
    // CredentialStore and clock are borrowed; their lifetime belongs to the caller.
    private readonly SemaphoreSlim identityOperation = new(1, 1);

    internal Task<RootIdentityLocator> CreateRootIdentityAsync(Guid fleet, Guid controller,
        CredentialStore credentials, TimeProvider clock, CancellationToken token = default)
    {
        return IdentityOperationAsync(async () =>
        {
            DateTimeOffset now = clock.GetUtcNow();
            using RootMaterial material = RootMaterial.Generate(fleet, controller, clock);
            byte[] certificate = material.GetPublicCertificate();
            using X509Certificate2 publicCertificate = X509CertificateLoader.LoadCertificate(certificate);
            using ECDsa publicKey = publicCertificate.GetECDsaPublicKey()!;
            CredentialScope scope = new(new FleetId(fleet), controller, ControllerIdentityRow.RootPurpose);
            CredentialReference reference = CredentialStore.Reserve(scope);
            ControllerIdentityRow pending = new()
            {
                FleetId = fleet,
                ControllerId = controller,
                AttemptId = Guid.NewGuid(),
                CredentialId = reference.Id,
                ProfileVersion = RootProfile.Version,
                Purpose = ControllerIdentityRow.RootPurpose,
                CertificateDer = certificate,
                CertificateHash = SHA256.HashData(certificate),
                SpkiHash = SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()),
                CreatedAt = now.ToUnixTimeMilliseconds()
            };
            // Any failure here (including post-commit identity/cancellation refusal) exits
            // before native mutation. Explicit resume only ever reads the exact locator.
            await BeginIdentityMetadataAsync(pending, token).ConfigureAwait(false);
            await CheckIdentityBoundaryAsync(token).ConfigureAwait(false);
            RootProfile.Validate(pending.CertificateDer, fleet, controller, pending.CertificateHash,
                pending.SpkiHash, clock.GetUtcNow());
            byte[] envelope = RootEnvelope.Encode(material, fleet, controller, pending.AttemptId,
                reference.Id, pending.CertificateHash, pending.SpkiHash);
            try
            {
                await credentials.CreateAsync(scope, reference, envelope, token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
                await CheckIdentityBoundaryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await ValidateAndPublishRootAsync(pending, credentials, clock, token).ConfigureAwait(false);
            return Locator(pending);
        }, token);
    }

    internal Task<RootIdentityLocator> ResumeRootIdentityAsync(RootIdentityLocator expected,
        CredentialStore credentials, TimeProvider clock, CancellationToken token = default)
    {
        return OpenRootIdentityAsync(expected, credentials, clock, true, token);
    }

    // Ready metadata is inventory only: every call reopens native storage and proves possession.
    internal Task<RootIdentityLocator> ReopenRootIdentityAsync(RootIdentityLocator expected,
        CredentialStore credentials, TimeProvider clock, CancellationToken token = default)
    {
        return OpenRootIdentityAsync(expected, credentials, clock, false, token);
    }

    private Task<RootIdentityLocator> OpenRootIdentityAsync(RootIdentityLocator expected,
        CredentialStore credentials, TimeProvider clock, bool resume, CancellationToken token)
    {
        return IdentityOperationAsync(async () =>
        {
            ControllerIdentityRow? row = await ReadIdentityMetadataAsync(expected.Fleet, token).ConfigureAwait(false);
            if (row is null || Locator(row) != expected || (!resume && row.State != ControllerIdentityRow.Ready))
            {
                throw MetadataConflict();
            }
            // Resume also reconciles an ambiguous successful publication without replaying UPDATE.
            await ValidateAndPublishRootAsync(row, credentials, clock, token).ConfigureAwait(false);
            return Locator(row);
        }, token);
    }

    private async Task ValidateAndPublishRootAsync(ControllerIdentityRow row, CredentialStore credentials,
        TimeProvider clock, CancellationToken token)
    {
        await CheckIdentityBoundaryAsync(token).ConfigureAwait(false);
        DateTimeOffset before = clock.GetUtcNow();
        if (before.ToUnixTimeMilliseconds() < row.CreatedAt)
        {
            throw new CryptographicException("Controller clock moved backwards.");
        }
        CredentialReference reference = row.Reference;
        try
        {
            await credentials.UseAsync(reference.Scope, reference, secret =>
        {
            token.ThrowIfCancellationRequested();
            RootEnvelope.Validate(secret, row.CertificateDer, row.FleetId, row.ControllerId,
                row.AttemptId, row.CredentialId, row.CertificateHash, row.SpkiHash, before);
        }, token).ConfigureAwait(false);
        }
        finally
        {
            await CheckIdentityBoundaryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await CheckIdentityBoundaryAsync(token).ConfigureAwait(false);
        DateTimeOffset after = clock.GetUtcNow();
        if (after < before)
        {
            throw new CryptographicException("Controller clock moved backwards.");
        }
        RootProfile.Validate(row.CertificateDer, row.FleetId, row.ControllerId,
            row.CertificateHash, row.SpkiHash, after);
        if (row.State == ControllerIdentityRow.Pending)
        {
            await PublishIdentityMetadataAsync(row, after.ToUnixTimeMilliseconds(), token).ConfigureAwait(false);
        }
        await CheckIdentityBoundaryAsync(token).ConfigureAwait(false);
    }

    private Task<RootIdentityLocator> IdentityOperationAsync(Func<Task<RootIdentityLocator>> operation, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            await identityOperation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CheckIdentityBoundaryAsync(token).ConfigureAwait(false);
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                identityOperation.Release();
            }
        }, token);
    }

    private async Task CheckIdentityBoundaryAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            identity.Verify();
        }
        finally
        {
            gate.Release();
        }
    }

    private static RootIdentityLocator Locator(ControllerIdentityRow row)
    {
        return new RootIdentityLocator(row.FleetId, row.ControllerId, row.AttemptId, row.CredentialId);
    }
}

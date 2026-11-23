using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChunkMaestro.ControllerIdentity;

internal sealed class RootMaterial : IDisposable
{
    private readonly ECDsa key;
    private readonly byte[] certificate;
    private bool disposed;

    private RootMaterial(ECDsa key, byte[] certificate)
    {
        this.key = key;
        this.certificate = certificate;
    }

    internal byte[] GetPublicCertificate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return (byte[]) certificate.Clone();
    }

    internal static RootMaterial Generate(Guid fleet, Guid controller, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTimeOffset captured = clock.GetUtcNow().ToUniversalTime();
        captured = captured.AddTicks(-(captured.Ticks % TimeSpan.TicksPerSecond));
        // Check representability before allocating a private key.
        DateTimeOffset before = captured.AddMinutes(-5);
        DateTimeOffset after = captured.AddYears(5);
        if (before.Year < 1950)
        {
            throw new ArgumentOutOfRangeException(nameof(clock));
        }
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            CertificateRequest request = RootProfile.Request(key, fleet, controller);
            byte[] serial = new byte[16];
            do
            {
                RandomNumberGenerator.Fill(serial);
                serial[0] &= 0x7f;
            }
            while (serial[0] == 0);
            using X509Certificate2 cert = request.Create(request.SubjectName,
                X509SignatureGenerator.CreateForECDsa(key), before, after, serial);
            return new RootMaterial(key, cert.RawData);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    // Trusted-core import only: caller retains responsibility for zeroing its input buffer.
    // No vault contract, exported key DTO, or presentation-facing entry point is supplied.
    internal static RootMaterial Reopen(ReadOnlySpan<byte> pkcs8, ReadOnlyMemory<byte> certificate,
        Guid fleet, Guid controller, ReadOnlySpan<byte> certificateHash,
        ReadOnlySpan<byte> spkiHash, DateTimeOffset now)
    {
        RootProfile.Validate(certificate, fleet, controller, certificateHash, spkiHash, now);
        ECDsa key = ECDsa.Create();
        RootMaterial? material = null;
        byte[]? canonical = null;
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out int consumed);
            canonical = key.ExportPkcs8PrivateKey();
            if (consumed != pkcs8.Length || !CryptographicOperations.FixedTimeEquals(canonical, pkcs8))
            {
                throw new CryptographicException("Noncanonical private material.");
            }
            material = new RootMaterial(key, certificate.ToArray());
            material.ValidatePossession(fleet, controller, certificateHash, spkiHash, now);
            return material;
        }
        catch
        {
            if (material is not null)
            {
                material.Dispose();
            }
            else
            {
                key.Dispose();
            }
            throw;
        }
        finally
        {
            if (canonical is not null)
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }
    }

    // No caller-chosen message or returned signature. The fresh internal challenge is bound
    // to expected scope and both exact hashes, after full certificate validation.
    internal void ValidatePossession(Guid fleet, Guid controller, ReadOnlySpan<byte> certificateHash,
        ReadOnlySpan<byte> spkiHash, DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] spki = RootProfile.Validate(certificate, fleet, controller, certificateHash, spkiHash, now);
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(spki, out _);
        byte[] challenge = new byte[160];
        byte[]? signature = null;
        try
        {
            "ChunkMaestro.RootPossession.v1\0"u8.CopyTo(challenge);
            fleet.TryWriteBytes(challenge.AsSpan(32, 16));
            controller.TryWriteBytes(challenge.AsSpan(48, 16));
            certificateHash.CopyTo(challenge.AsSpan(64, 32));
            spkiHash.CopyTo(challenge.AsSpan(96, 32));
            RandomNumberGenerator.Fill(challenge.AsSpan(128, 32));
            signature = key.SignData(challenge, HashAlgorithmName.SHA256);
            if (!verifier.VerifyData(challenge, signature, HashAlgorithmName.SHA256))
            {
                throw new CryptographicException("Private key binding mismatch.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            if (signature is not null)
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            key.Dispose();
        }
    }
}

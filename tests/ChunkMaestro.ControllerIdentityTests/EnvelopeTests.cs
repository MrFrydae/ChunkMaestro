using ChunkMaestro.ControllerIdentity;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ChunkMaestro.ControllerIdentityTests;

public sealed class EnvelopeTests
{
    [Fact]
    public void EnvelopeFullyConsumesCanonicalPrivateKeyAndBindsEveryHeaderByte()
    {
        Guid fleet = Guid.NewGuid();
        Guid controller = Guid.NewGuid();
        Guid attempt = Guid.NewGuid();
        Guid locator = Guid.NewGuid();
        using RootMaterial material = RootMaterial.Generate(fleet, controller, TimeProvider.System);
        byte[] der = material.GetPublicCertificate();
        using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(der);
        using ECDsa key = cert.GetECDsaPublicKey()!;
        byte[] hash = SHA256.HashData(der);
        byte[] spki = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        byte[] envelope = RootEnvelope.Encode(material, fleet, controller, attempt, locator, hash, spki);
        try
        {
            Assert.InRange(envelope.Length, 145, 2560);
            RootEnvelope.Validate(envelope, der, fleet, controller, attempt, locator, hash, spki, DateTimeOffset.UtcNow);
            for (int index = 0; index < envelope.Length; index++)
            {
                envelope[index] ^= 1;
                Assert.ThrowsAny<CryptographicException>(() => RootEnvelope.Validate(envelope, der,
                    fleet, controller, attempt, locator, hash, spki, DateTimeOffset.UtcNow));
                envelope[index] ^= 1;
            }
            for (int length = 0; length < envelope.Length; length++)
            {
                int captured = length;
                Assert.ThrowsAny<CryptographicException>(() => RootEnvelope.Validate(envelope.AsSpan(0, captured),
                    der, fleet, controller, attempt, locator, hash, spki, DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
        Assert.All(envelope, value => Assert.Equal(0, value));
        Assert.Throws<InvalidOperationException>(() => material.WithPrivateKey(secret => throw new InvalidOperationException("synthetic")));
        material.ValidatePossession(fleet, controller, hash, spki, DateTimeOffset.UtcNow);
        material.Dispose();
        Assert.Throws<ObjectDisposedException>(() => material.WithPrivateKey(secret => Assert.Fail("Disposed export")));
    }
}

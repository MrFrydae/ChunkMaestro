using ChunkMaestro.ControllerIdentity;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ChunkMaestro.ControllerIdentityTests;

public sealed class ProfileTests
{
    private static readonly Guid Fleet = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly Guid Controller = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GenerationRejectsPre1950ValidityWithoutPreventingValidIssuance()
    {
        DateTimeOffset earliest = new(1950, 1, 1, 0, 5, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => RootMaterial.Generate(Fleet, Controller, new CountingClock(earliest.AddSeconds(-1))));
        using RootMaterial material = RootMaterial.Generate(Fleet, Controller, new CountingClock(earliest));
        byte[] der = material.GetPublicCertificate();
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(der);
        using ECDsa publicKey = certificate.GetECDsaPublicKey()!;
        Assert.Equal(new DateTime(1950, 1, 1, 0, 0, 0, DateTimeKind.Utc), certificate.NotBefore.ToUniversalTime());
        material.ValidatePossession(Fleet, Controller, SHA256.HashData(der),
            SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()), earliest);
    }

    [Fact]
    public void SignedValidityThatOverflowsTheRequiredLifetimeIsRefused()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = RootProfile.Request(key, Fleet, Controller);
        DateTimeOffset before = new(9998, 1, 1, 0, 0, 0, TimeSpan.Zero);
        byte[] serial = new byte[16];
        serial[0] = 1;
        using X509Certificate2 certificate = request.Create(request.SubjectName,
            X509SignatureGenerator.CreateForECDsa(key), before, before.AddYears(1), serial);
        byte[] der = certificate.RawData;
        byte[] hash = SHA256.HashData(der);
        byte[] spkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Fleet, Controller,
            hash, spkiHash, before.AddHours(1)));
    }

    [Fact]
    public void GeneratedRootHasExactProfileAndOwnedLifetime()
    {
        CountingClock clock = new(Now.AddTicks(12345));
        RootMaterial material = RootMaterial.Generate(Fleet, Controller, clock);
        byte[] der = material.GetPublicCertificate();
        using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(der);
        using ECDsa key = cert.GetECDsaPublicKey()!;
        byte[] hash = SHA256.HashData(der);
        byte[] spkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        material.ValidatePossession(Fleet, Controller, hash, spkiHash, Now);
        Assert.Equal(1, clock.Reads);
        Assert.False(cert.HasPrivateKey);
        Assert.Equal(3, cert.Version);
        Assert.Equal(Now.AddMinutes(-5).UtcDateTime, cert.NotBefore.ToUniversalTime());
        Assert.Equal(Now.AddYears(5).UtcDateTime, cert.NotAfter.ToUniversalTime());
        Assert.Equal("1.2.840.10045.4.3.2", cert.SignatureAlgorithm.Value);
        Assert.Equal(4, cert.Extensions.Count);
        AsnReader sanDocument = new(cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().RawData, AsnEncodingRules.DER);
        AsnReader san = sanDocument.ReadSequence();
        Assert.Equal(RootProfile.IdentityUri(Fleet, Controller), san.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
        san.ThrowIfNotEmpty();
        sanDocument.ThrowIfNotEmpty();
        X509BasicConstraintsExtension constraints = cert.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.Critical && constraints.CertificateAuthority && constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);
        X509KeyUsageExtension usage = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);
        Assert.False(cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().Critical);
        AsnReader doc = new(der, AsnEncodingRules.DER);
        AsnReader tbs = doc.ReadSequence().ReadSequence();
        tbs.ReadEncodedValue();
        ReadOnlyMemory<byte> serial = tbs.ReadIntegerBytes();
        Assert.Equal(16, serial.Length);
        Assert.InRange(serial.Span[0], (byte) 1, (byte) 127);
        der[0] ^= 1;
        Assert.NotEqual(der, material.GetPublicCertificate());
        material.Dispose();
        material.Dispose();
        Assert.Throws<ObjectDisposedException>(() => material.GetPublicCertificate());
        Assert.Throws<ObjectDisposedException>(() => material.ValidatePossession(Fleet, Controller, hash, spkiHash, Now));
    }

    [Theory]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    [InlineData(0, true)]
    public void StrictStartBoundary(int offset, bool valid)
    {
        using RootMaterial material = RootMaterial.Generate(Fleet, Controller, new CountingClock(Now));
        Check(material.GetPublicCertificate(), Now.AddSeconds(offset), valid);
    }

    [Fact]
    public void StrictEndBoundaryAndTimeOverflow()
    {
        using RootMaterial material = RootMaterial.Generate(Fleet, Controller, new CountingClock(Now));
        byte[] der = material.GetPublicCertificate();
        Check(der, Now.AddYears(5).AddTicks(-1), true);
        Check(der, Now.AddYears(5), false);
        Check(der, Now.AddYears(5).AddTicks(1), false);
        Assert.Throws<ArgumentOutOfRangeException>(() => RootMaterial.Generate(Fleet, Controller, new CountingClock(DateTimeOffset.MinValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RootMaterial.Generate(Fleet, Controller, new CountingClock(DateTimeOffset.MaxValue)));
        Assert.Throws<CryptographicException>(() => RootMaterial.Generate(Guid.Empty, Controller, new CountingClock(Now)));
    }

    [Theory]
    [InlineData(2049, 12, 31)]
    [InlineData(2028, 2, 29)]
    public void CalendarAndGeneralizedTimeRoundTrip(int year, int month, int day)
    {
        DateTimeOffset now = new(year, month, day, 12, 0, 0, TimeSpan.Zero);
        using RootMaterial material = RootMaterial.Generate(Fleet, Controller, new CountingClock(now));
        Check(material.GetPublicCertificate(), now, true);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("duplicate", false)]
    [InlineData("unknown", false)]
    [InlineData("critical", false)]
    [InlineData("usage", false)]
    [InlineData("constraints", false)]
    [InlineData("san", false)]
    [InlineData("uppercase", false)]
    [InlineData("algorithm", false)]
    [InlineData("curve", false)]
    [InlineData("lifetime", false)]
    [InlineData("serial", false)]
    [InlineData("ski", false)]
    [InlineData("extraSan", false)]
    [InlineData("missing", false)]
    public void IndependentlySignedFixturesRejectProfileViolations(string mutation, bool valid)
    {
        using ECDsa key = ECDsa.Create(mutation == "curve" ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(RootProfile.Subject, key, mutation == "algorithm" ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, mutation == "constraints" ? 1 : 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(mutation == "usage" ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(mutation == "ski" ? new X509SubjectKeyIdentifierExtension("01020304", false) : new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        SubjectAlternativeNameBuilder san = new();
        string uri = RootProfile.IdentityUri(Fleet, mutation == "san" ? Guid.NewGuid() : Controller);
        if (mutation == "uppercase")
        {
            uri = uri.Replace(Fleet.ToString("D"), Fleet.ToString("D").ToUpperInvariant(), StringComparison.Ordinal);
        }
        san.AddUri(new Uri(uri));
        if (mutation == "extraSan")
        {
            san.AddUri(new Uri("https://identity.chunkmaestro.invalid/extra"));
        }
        if (mutation != "missing")
        {
            request.CertificateExtensions.Add(san.Build(mutation == "critical"));
        }
        if (mutation == "unknown")
        {
            request.CertificateExtensions.Add(new X509Extension("1.2.3.4", [5, 0], false));
        }
        if (mutation == "duplicate")
        {
            // BCL CertificateRequest refuses duplicate OIDs; splice an additional extension and
            // re-sign the TBS below instead, with a real external ephemeral fixture key.
            byte[] original = Issue(request, key, mutation);
            Check(DuplicateExtension(original, key), Now, false);
            return;
        }
        Check(Issue(request, key, mutation), Now, valid);
    }

    [Fact]
    public void TamperingTrailingMalformedAndMetadataSubstitutionAreRejected()
    {
        using RootMaterial material = RootMaterial.Generate(Fleet, Controller, new CountingClock(Now));
        byte[] der = material.GetPublicCertificate();
        using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(der);
        using ECDsa key = cert.GetECDsaPublicKey()!;
        byte[] spkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        byte[] hash = SHA256.HashData(der);
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Guid.NewGuid(), Controller, hash, spkiHash, Now));
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Fleet, Guid.NewGuid(), hash, spkiHash, Now));
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Fleet, Controller, new byte[32], spkiHash, Now));
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Fleet, Controller, hash, new byte[32], Now));
        Check([.. der, 0], Now, false);
        Check(der[..^1], Now, false);
        Check([0x30, 0x80, 0, 0], Now, false);
        Check([], Now, false);
        der[^1] ^= 1;
        Check(der, Now, false);
    }

    [Fact]
    public void InternalReopenProvesKeyAndRejectsTrailingPrivateMaterial()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = Issue(RootProfile.Request(key, Fleet, Controller), key, "valid");
        byte[] hash = SHA256.HashData(der);
        byte[] spkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        byte[] privateBytes = key.ExportPkcs8PrivateKey();
        byte[] trailing = [.. privateBytes, 0];
        AsnReader privateReader = new(privateBytes, AsnEncodingRules.DER);
        ReadOnlyMemory<byte> privateContent = privateReader.PeekContentBytes();
        byte[] ber = [0x30, 0x80, .. privateContent.Span, 0, 0];
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] wrong = other.ExportPkcs8PrivateKey();
        try
        {
            using RootMaterial reopened = RootMaterial.Reopen(privateBytes, der, Fleet, Controller, hash, spkiHash, Now);
            Assert.Equal(der, reopened.GetPublicCertificate());
            Assert.Throws<CryptographicException>(() => RootMaterial.Reopen(wrong, der, Fleet, Controller, hash, spkiHash, Now));
            Assert.Throws<CryptographicException>(() => RootMaterial.Reopen(trailing, der, Fleet, Controller, hash, spkiHash, Now));
            Assert.Throws<CryptographicException>(() => RootMaterial.Reopen(ber, der, Fleet, Controller, hash, spkiHash, Now));
            Assert.Throws<CryptographicException>(() => RootMaterial.Reopen([], der, Fleet, Controller, hash, spkiHash, Now));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            CryptographicOperations.ZeroMemory(trailing);
            CryptographicOperations.ZeroMemory(ber);
            CryptographicOperations.ZeroMemory(wrong);
        }
        Assert.True(privateBytes.All(value => value == 0));
        Assert.True(trailing.All(value => value == 0));
        Assert.True(ber.All(value => value == 0));
        Assert.True(wrong.All(value => value == 0));
    }

    [Fact]
    public void SignatureOnlyCorruptionIsRejectedDespiteValidKeyAndPossession()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] original = Issue(RootProfile.Request(key, Fleet, Controller), key, "valid");
        byte[] changed = (byte[]) original.Clone();
        changed[^1] ^= 1;
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(spki, out _);
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] proof = key.SignData(nonce, HashAlgorithmName.SHA256);
        Assert.True(verifier.VerifyData(nonce, proof, HashAlgorithmName.SHA256));
        using X509Certificate2 corrupted = X509CertificateLoader.LoadCertificate(changed);
        using ECDsa unchangedKey = corrupted.GetECDsaPublicKey()!;
        Assert.Equal(spki, unchangedKey.ExportSubjectPublicKeyInfo());
        byte[] spkiHash = SHA256.HashData(spki);
        byte[] hash = SHA256.HashData(changed);
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(changed, Fleet, Controller, hash, spkiHash, Now));
        byte[] privateBytes = key.ExportPkcs8PrivateKey();
        try
        {
            Assert.Throws<CryptographicException>(() => RootMaterial.Reopen(privateBytes, changed, Fleet, Controller, hash, spkiHash, Now));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }
    }

    [Theory]
    [InlineData("innerAlgorithm")]
    [InlineData("outerAlgorithm")]
    [InlineData("negativeSerial")]
    [InlineData("zeroSerial")]
    [InlineData("issuer")]
    [InlineData("version")]
    [InlineData("extraTbs")]
    [InlineData("unusedBits")]
    public void ResignedStructuralFixturesAreRejected(string mutation)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] original = Issue(RootProfile.Request(key, Fleet, Controller), key, "valid");
        AsnReader doc = new(original, AsnEncodingRules.DER);
        AsnReader cert = doc.ReadSequence();
        AsnReader tbs = cert.ReadSequence();
        List<byte[]> fields = [];
        while (tbs.HasData)
        {
            fields.Add(tbs.ReadEncodedValue().ToArray());
        }
        if (mutation == "innerAlgorithm")
        {
            fields[2] = [0x30, 0x0c, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02, 0x05, 0];
        }
        if (mutation == "negativeSerial")
        {
            fields[1][2] |= 0x80;
        }
        if (mutation == "zeroSerial")
        {
            fields[1] = [2, 1, 0];
        }
        if (mutation == "issuer")
        {
            fields[3] = new X500DistinguishedName("CN=Other").RawData;
        }
        if (mutation == "version")
        {
            fields[0] = [0xa0, 3, 2, 1, 1];
        }
        AsnWriter changed = new(AsnEncodingRules.DER);
        changed.PushSequence();
        foreach (byte[] field in fields)
        {
            changed.WriteEncodedValue(field);
        }
        if (mutation == "extraTbs")
        {
            changed.WriteNull();
        }
        changed.PopSequence();
        byte[] tbsBytes = changed.Encode();
        byte[] signature = key.SignData(tbsBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        AsnWriter output = new(AsnEncodingRules.DER);
        output.PushSequence();
        output.WriteEncodedValue(tbsBytes);
        if (mutation == "outerAlgorithm")
        {
            output.WriteEncodedValue([0x30, 0x0c, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02, 0x05, 0]);
        }
        else
        {
            output.WriteEncodedValue(cert.ReadEncodedValue().Span);
        }
        if (mutation == "unusedBits")
        {
            signature[^1] &= 0xfe;
        }
        output.WriteBitString(signature, mutation == "unusedBits" ? 1 : 0);
        output.PopSequence();
        byte[] der = output.Encode();
        byte[] hash = SHA256.HashData(der);
        byte[] spkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Fleet, Controller, hash, spkiHash, Now));
    }

    private static byte[] Issue(CertificateRequest request, ECDsa key, string mutation)
    {
        byte[] serial = Enumerable.Repeat((byte) 1, mutation == "serial" ? 15 : 16).ToArray();
        using X509Certificate2 cert = request.Create(request.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
            Now.AddMinutes(-5), Now.AddYears(mutation == "lifetime" ? 4 : 5), serial);
        return cert.RawData;
    }

    private static byte[] DuplicateExtension(byte[] der, ECDsa key)
    {
        AsnReader reader = new(der, AsnEncodingRules.DER);
        AsnReader cert = reader.ReadSequence();
        AsnReader tbs = cert.ReadSequence();
        AsnWriter changed = new(AsnEncodingRules.DER);
        changed.PushSequence();
        for (int i = 0; i < 7; i++)
        {
            changed.WriteEncodedValue(tbs.ReadEncodedValue().Span);
        }
        Asn1Tag tag = new(TagClass.ContextSpecific, 3, true);
        AsnReader extensions = tbs.ReadSequence(tag).ReadSequence();
        changed.PushSequence(tag);
        changed.PushSequence();
        byte[] first = extensions.PeekEncodedValue().ToArray();
        while (extensions.HasData)
        {
            changed.WriteEncodedValue(extensions.ReadEncodedValue().Span);
        }
        changed.WriteEncodedValue(first);
        changed.PopSequence();
        changed.PopSequence(tag);
        changed.PopSequence();
        byte[] tbsBytes = changed.Encode();
        byte[] signature = key.SignData(tbsBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        AsnWriter output = new(AsnEncodingRules.DER);
        output.PushSequence();
        output.WriteEncodedValue(tbsBytes);
        output.WriteEncodedValue(cert.ReadEncodedValue().Span);
        output.WriteBitString(signature);
        output.PopSequence();
        return output.Encode();
    }

    private static void Check(byte[] der, DateTimeOffset now, bool valid)
    {
        byte[] spkiHash = new byte[32];
        try
        {
            using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(der);
            using ECDsa key = cert.GetECDsaPublicKey()!;
            spkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        }
        catch (CryptographicException)
        {
            // Even where the platform parser refuses a fixture, exercise the strict validator.
        }
        if (valid)
        {
            RootProfile.Validate(der, Fleet, Controller, SHA256.HashData(der), spkiHash, now);
        }
        else
        {
            Assert.Throws<CryptographicException>(() => RootProfile.Validate(der, Fleet, Controller, SHA256.HashData(der), spkiHash, now));
        }
    }

    private sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        internal int Reads
        {
            get;
            private set;
        }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return now;
        }
    }
}

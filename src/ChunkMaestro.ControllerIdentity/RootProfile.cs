using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChunkMaestro.ControllerIdentity;

// Candidate profile only. Internal until integrated with authorized application/vault boundaries.
internal static class RootProfile
{
    internal const int Version = 1;
    internal const string Subject = "CN=ChunkMaestro Fleet Controller CA";
    private const string SignatureOid = "1.2.840.10045.4.3.2";

    internal static string IdentityUri(Guid fleet, Guid controller)
    {
        if (fleet == Guid.Empty || controller == Guid.Empty)
        {
            throw new CryptographicException("Invalid identity scope.");
        }
        return $"https://identity.chunkmaestro.invalid/v1/fleets/{fleet:D}/controllers/{controller:D}";
    }

    internal static CertificateRequest Request(ECDsa key, Guid fleet, Guid controller)
    {
        CertificateRequest request = new(Subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        SubjectAlternativeNameBuilder san = new();
        san.AddUri(new Uri(IdentityUri(fleet, controller)));
        request.CertificateExtensions.Add(san.Build(false));
        return request;
    }

    internal static byte[] Validate(ReadOnlyMemory<byte> der, Guid fleet, Guid controller,
        ReadOnlySpan<byte> certificateHash, ReadOnlySpan<byte> spkiHash, DateTimeOffset now)
    {
        try
        {
            Require(der.Length <= 4096);
            return ValidateCore(der.ToArray(), fleet, controller, certificateHash, spkiHash, now);
        }
        catch (AsnContentException)
        {
            throw new CryptographicException("Invalid root encoding.");
        }
    }

    private static byte[] ValidateCore(ReadOnlyMemory<byte> der, Guid fleet, Guid controller,
        ReadOnlySpan<byte> certificateHash, ReadOnlySpan<byte> spkiHash, DateTimeOffset now)
    {
        Require(der.Length <= 4096 && certificateHash.Length == 32 && spkiHash.Length == 32);
        Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(der.Span), certificateHash));
        AsnReader document = new(der, AsnEncodingRules.DER);
        AsnReader certificate = document.ReadSequence();
        document.ThrowIfNotEmpty();
        ReadOnlyMemory<byte> tbsBytes = certificate.ReadEncodedValue();
        ReadSignatureAlgorithm(certificate);
        byte[] signature = certificate.ReadBitString(out int unused);
        Require(unused == 0);
        certificate.ThrowIfNotEmpty();
        AsnReader signatureDocument = new(signature, AsnEncodingRules.DER);
        AsnReader integers = signatureDocument.ReadSequence();
        Require(integers.ReadInteger() > 0 && integers.ReadInteger() > 0);
        integers.ThrowIfNotEmpty();
        signatureDocument.ThrowIfNotEmpty();

        AsnReader tbsDocument = new(tbsBytes, AsnEncodingRules.DER);
        AsnReader tbs = tbsDocument.ReadSequence();
        tbsDocument.ThrowIfNotEmpty();
        AsnReader version = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        Require(version.ReadInteger() == 2);
        version.ThrowIfNotEmpty();
        ReadOnlyMemory<byte> serial = tbs.ReadIntegerBytes();
        Require(serial.Length == 16 && serial.Span[0] is > 0 and < 128);
        ReadSignatureAlgorithm(tbs);
        byte[] name = new X500DistinguishedName(Subject).RawData;
        Require(tbs.ReadEncodedValue().Span.SequenceEqual(name));
        AsnReader validity = tbs.ReadSequence();
        DateTimeOffset before = ReadTime(validity);
        DateTimeOffset after = ReadTime(validity);
        validity.ThrowIfNotEmpty();
        Require(before < after && now >= before && now < after);
        // This profile has exact issuance arithmetic, including leap-day AddYears behavior.
        try
        {
            Require(before.AddMinutes(5).AddYears(5) == after);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new CryptographicException("Invalid root validity.");
        }
        Require(tbs.ReadEncodedValue().Span.SequenceEqual(name));
        byte[] spki = tbs.ReadEncodedValue().ToArray();
        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out int consumed);
        Require(consumed == spki.Length);
        ECParameters parameters = key.ExportParameters(false);
        Require(parameters.Curve.Oid.Value == "1.2.840.10045.3.1.7");
        Require(key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki));
        Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(spki), spkiHash));
        CertificateRequest expected = Request(key, fleet, controller);
        AsnReader extensionWrapper = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3, true));
        AsnReader extensions = extensionWrapper.ReadSequence();
        extensionWrapper.ThrowIfNotEmpty();
        // Fixed order and exact DER values reject duplicates, unknowns and noncanonical SANs.
        foreach (X509Extension extension in expected.CertificateExtensions)
        {
            AsnReader actual = extensions.ReadSequence();
            Require(actual.ReadObjectIdentifier() == extension.Oid!.Value);
            if (extension.Critical)
            {
                Require(actual.ReadBoolean());
            }
            Require(actual.ReadOctetString().AsSpan().SequenceEqual(extension.RawData));
            actual.ThrowIfNotEmpty();
        }
        extensions.ThrowIfNotEmpty();
        tbs.ThrowIfNotEmpty();
        Require(key.VerifyData(tbsBytes.Span, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return spki;
    }

    private static DateTimeOffset ReadTime(AsnReader reader)
    {
        ReadOnlyMemory<byte> encoded = reader.PeekEncodedValue();
        DateTimeOffset instant = reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime)
            ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
        AsnWriter canonical = new(AsnEncodingRules.DER);
        if (instant.Year < 2050)
        {
            canonical.WriteUtcTime(instant);
        }
        else
        {
            canonical.WriteGeneralizedTime(instant, omitFractionalSeconds: true);
        }
        Require(encoded.Span.SequenceEqual(canonical.Encode()));
        Require(instant.Ticks % TimeSpan.TicksPerSecond == 0 && instant.Year >= 1950);
        return instant;
    }

    private static void ReadSignatureAlgorithm(AsnReader reader)
    {
        AsnReader algorithm = reader.ReadSequence();
        Require(algorithm.ReadObjectIdentifier() == SignatureOid);
        algorithm.ThrowIfNotEmpty();
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new CryptographicException("Root profile or binding mismatch.");
        }
    }
}

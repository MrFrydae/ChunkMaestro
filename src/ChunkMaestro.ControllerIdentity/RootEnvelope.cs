using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ChunkMaestro.ControllerIdentity;

// Fixed-width versioned header, little-endian GUIDs/integers, followed by canonical PKCS8.
// Returned encoding is owned by the caller and MUST be zeroed, including on cancellation.
internal static class RootEnvelope
{
    private const int Header = 144;

    internal static byte[] Encode(RootMaterial material, Guid fleet, Guid controller, Guid attempt,
        Guid locator, ReadOnlySpan<byte> certificateHash, ReadOnlySpan<byte> spkiHash)
    {
        byte[] owned = new byte[2560];
        try
        {
            "CMROOT01"u8.CopyTo(owned);
            BinaryPrimitives.WriteInt32LittleEndian(owned.AsSpan(8), RootProfile.Version);
            fleet.TryWriteBytes(owned.AsSpan(12, 16));
            controller.TryWriteBytes(owned.AsSpan(28, 16));
            attempt.TryWriteBytes(owned.AsSpan(44, 16));
            locator.TryWriteBytes(owned.AsSpan(60, 16));
            if (certificateHash.Length != 32 || spkiHash.Length != 32 ||
                fleet == Guid.Empty || controller == Guid.Empty || attempt == Guid.Empty || locator == Guid.Empty)
            {
                throw new CryptographicException("Invalid envelope scope.");
            }
            certificateHash.CopyTo(owned.AsSpan(76, 32));
            spkiHash.CopyTo(owned.AsSpan(108, 32));
            int length = 0;
            material.WithPrivateKey(pkcs8 =>
            {
                length = pkcs8.Length;
                if (length > owned.Length - Header)
                {
                    throw new CryptographicException("Private envelope exceeds native limit.");
                }
                BinaryPrimitives.WriteInt32LittleEndian(owned.AsSpan(140), length);
                pkcs8.CopyTo(owned.AsSpan(Header));
            });
            return owned.AsSpan(0, Header + length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(owned);
        }
    }

    // Input is borrowed from CredentialStore, which owns and clears its native read buffer.
    internal static void Validate(ReadOnlySpan<byte> envelope, ReadOnlyMemory<byte> certificate,
        Guid fleet, Guid controller, Guid attempt, Guid locator, ReadOnlySpan<byte> certificateHash,
        ReadOnlySpan<byte> spkiHash, DateTimeOffset now)
    {
        if (envelope.Length is <= Header or > 2560 || !envelope[..8].SequenceEqual("CMROOT01"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(envelope[8..]) != RootProfile.Version ||
            new Guid(envelope.Slice(12, 16)) != fleet || new Guid(envelope.Slice(28, 16)) != controller ||
            new Guid(envelope.Slice(44, 16)) != attempt || new Guid(envelope.Slice(60, 16)) != locator ||
            !CryptographicOperations.FixedTimeEquals(envelope.Slice(76, 32), certificateHash) ||
            !CryptographicOperations.FixedTimeEquals(envelope.Slice(108, 32), spkiHash) ||
            BinaryPrimitives.ReadInt32LittleEndian(envelope[140..]) != envelope.Length - Header)
        {
            throw new CryptographicException("Private envelope binding mismatch.");
        }
        using RootMaterial reopened = RootMaterial.Reopen(envelope[Header..], certificate,
            fleet, controller, certificateHash, spkiHash, now);
    }
}

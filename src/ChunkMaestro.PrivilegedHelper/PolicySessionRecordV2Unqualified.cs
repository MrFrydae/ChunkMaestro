using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ChunkMaestro.PrivilegedHelper;

// Offline correlation claims only. No journal, IO, admission, proof or runtime consumer.
internal sealed record PolicySessionRecordV2Unqualified(Guid Boot, Guid Epoch, ulong Revision, PolicySessionPhase Phase,
    PolicySessionIdentity Agent, PolicySessionIdentity Worker, bool AttemptConsumed, bool EverArmed,
    PolicySessionFault FirstFailure, ulong FaultRevision, ulong ProofRevision, ushort ProofSummary,
    PolicySessionNativeMetadataV2Unqualified NativeMetadata)
{
    internal const int Length = 288;

    internal byte[] Encode()
    {
        Validate();
        byte[] bytes = new byte[Length];
        "CMPJ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        Boot.TryWriteBytes(bytes.AsSpan(8, 16));
        Epoch.TryWriteBytes(bytes.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), Revision);
        bytes[48] = (byte) Phase;
        bytes[49] = AttemptConsumed ? (byte) 1 : (byte) 0;
        bytes[50] = EverArmed ? (byte) 1 : (byte) 0;
        bytes[51] = (byte) FirstFailure;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), FaultRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), ProofRevision);
        WriteIdentity(bytes.AsSpan(72, 64), Agent);
        WriteIdentity(bytes.AsSpan(136, 64), Worker);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(200), ProofSummary);
        bytes[202] = NativeMetadata.Presence;
        bytes[203] = NativeMetadata.Architecture;
        bytes[204] = NativeMetadata.Normalization;
        bytes[205] = NativeMetadata.Source;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(208), NativeMetadata.AgentFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(212), NativeMetadata.WorkerFlags);
        SHA256.HashData(bytes.AsSpan(0, 256), bytes.AsSpan(256));
        return bytes;
    }

    internal static PolicySessionRecordV2Unqualified Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length || !bytes[..4].SequenceEqual("CMPJ"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes[..256]), bytes[256..]) ||
            bytes[49] > 1 || bytes[50] > 1 || bytes.Slice(52, 4).ContainsAnyExcept((byte) 0) ||
            bytes.Slice(206, 2).ContainsAnyExcept((byte) 0) ||
            bytes.Slice(216, 40).ContainsAnyExcept((byte) 0))
        {
            throw new InvalidDataException("Invalid or unsupported session journal.");
        }
        PolicySessionRecordV2Unqualified record = new(new Guid(bytes.Slice(8, 16)), new Guid(bytes.Slice(24, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), (PolicySessionPhase) bytes[48],
            ReadIdentity(bytes.Slice(72, 64)), ReadIdentity(bytes.Slice(136, 64)), bytes[49] == 1,
            bytes[50] == 1, (PolicySessionFault) bytes[51],
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[200..]),
            new(bytes[202], bytes[203], bytes[204], bytes[205],
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[208..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[212..])));
        record.Validate();
        return record;
    }

    // Pure comparisons of caller-supplied claims; no OS authentication or admission.
    internal PolicySessionExpectedNativePairV2Unqualified ExpectedNativePair(Guid expectedBoot, byte expectedArchitecture)
    {
        Validate();
        if (Boot != expectedBoot || NativeMetadata.Architecture != expectedArchitecture || NativeMetadata.Presence != 3)
        {
            throw new InvalidDataException("Session journal comparison refused.");
        }
        return new(new(Worker.PidfsDevice, Worker.PidfsInode, NativeMetadata.WorkerFlags),
            new(Agent.PidfsDevice, Agent.PidfsInode, NativeMetadata.AgentFlags));
    }

    private void Validate()
    {
        NativeMetadata.Validate();
        byte expectedPresence = Epoch == Guid.Empty && Phase == PolicySessionPhase.Empty
            ? (byte) 0 : Worker == default ? (byte) 1 : (byte) 3;
        // The existing conservative inode distinction also rejects every equal (device,inode)
        // pair, independently of flags, invocation, start time, namespace or domain labels.
        if (NativeMetadata.Presence != expectedPresence ||
            (Worker != default && (Worker.Invocation == Agent.Invocation || Worker.PidfsInode == Agent.PidfsInode)))
        {
            throw new InvalidDataException("Inconsistent session journal metadata.");
        }
        PolicyRetirementObservations common = PolicyRetirementObservations.GateRevoked |
            PolicyRetirementObservations.SoleJobTerminal | PolicyRetirementObservations.NoPendingJobs |
            PolicyRetirementObservations.ProtectedDomainEmpty | PolicyRetirementObservations.OwnedReferencesClosed |
            PolicyRetirementObservations.OriginalAgentReferencesClosed;
        PolicyRetirementObservations required = common | (EverArmed
            ? PolicyRetirementObservations.OriginalWorkerHup | PolicyRetirementObservations.SameWorkerGenerationTerminal |
                PolicyRetirementObservations.AllThreadDescendantAndAsyncClosure
            : PolicyRetirementObservations.StartupNoInputRightsChildrenOrAsyncRoutes);
        bool bound = Phase is PolicySessionPhase.Bound or PolicySessionPhase.Armed || EverArmed;
        if (!Enum.IsDefined(Phase) || !Enum.IsDefined(FirstFailure) || Boot == Guid.Empty || Revision == 0 ||
            (ProofSummary & ~1023) != 0 || (ProofRevision == 0 && ProofSummary != 0) ||
            (ProofRevision != 0 && ((PolicyRetirementObservations) ProofSummary & required) != required) ||
            (Phase == PolicySessionPhase.Empty && Epoch == Guid.Empty &&
                (Agent != default || Worker != default || AttemptConsumed || EverArmed || FirstFailure != PolicySessionFault.None || ProofRevision != 0)) ||
            (Phase == PolicySessionPhase.Empty && Epoch != Guid.Empty && (!Agent.IsComplete || ProofRevision == 0)) ||
            (Phase is PolicySessionPhase.Occupied or PolicySessionPhase.StartAttempted && Worker != default) ||
            (Phase is PolicySessionPhase.Occupied or PolicySessionPhase.StartAttempted or PolicySessionPhase.Bound && EverArmed) ||
            (FirstFailure != PolicySessionFault.None && Phase is not (PolicySessionPhase.Retiring or PolicySessionPhase.Retired or PolicySessionPhase.Empty)) ||
            FaultRevision > Revision || ProofRevision > Revision ||
            (FirstFailure == PolicySessionFault.None) != (FaultRevision == 0) ||
            (ProofRevision != 0 && (Phase is not (PolicySessionPhase.Retired or PolicySessionPhase.Empty) || ProofRevision <= FaultRevision)) ||
            (Phase == PolicySessionPhase.Retired && ProofRevision == 0) ||
            (Phase != PolicySessionPhase.Empty && (Epoch == Guid.Empty || !Agent.IsComplete)) ||
            (Phase == PolicySessionPhase.Occupied && AttemptConsumed) ||
            (Phase is PolicySessionPhase.StartAttempted or PolicySessionPhase.Bound or PolicySessionPhase.Armed && !AttemptConsumed) ||
            (EverArmed && (!AttemptConsumed || !Worker.IsComplete)) ||
            (Phase == PolicySessionPhase.Armed && !EverArmed) || (bound && !Worker.IsComplete) ||
            (!bound && Worker != default && !Worker.IsComplete))
        {
            throw new InvalidDataException("Inconsistent session journal correlation.");
        }
    }

    private static void WriteIdentity(Span<byte> bytes, PolicySessionIdentity identity)
    {
        identity.Invocation.TryWriteBytes(bytes[..16]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], identity.PidfsDevice);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], identity.PidfsInode);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], identity.StartTime);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], identity.NamespaceInode);
        identity.Domain.TryWriteBytes(bytes[48..]);
    }

    private static PolicySessionIdentity ReadIdentity(ReadOnlySpan<byte> bytes)
    {
        return new(new Guid(bytes[..16]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), new Guid(bytes[48..]));
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ChunkMaestro.PrivilegedHelper;

internal enum PolicySessionPhase : byte
{
    Empty,
    Occupied,
    StartAttempted,
    Bound,
    Armed,
    Retiring,
    Retired
}

internal enum PolicySessionFault : byte
{
    None,
    Cancelled,
    Deadline,
    Worker,
    Agent,
    Holder,
    Store,
    Cleanup,
    Storage
}

// Identity labels are correlation data, never authentication or PolicyFacts.
internal readonly record struct PolicySessionIdentity(Guid Invocation, ulong PidfsDevice,
    ulong PidfsInode, ulong StartTime, ulong NamespaceInode, Guid Domain)
{
    internal bool IsComplete => Invocation != Guid.Empty && PidfsDevice != 0 && PidfsInode != 0 &&
        StartTime != 0 && NamespaceInode != 0 && Domain != Guid.Empty;
}

internal sealed record PolicySessionRecord(Guid Boot, Guid Epoch, ulong Revision, PolicySessionPhase Phase,
    PolicySessionIdentity Agent, PolicySessionIdentity Worker, bool AttemptConsumed, bool EverArmed,
    PolicySessionFault FirstFailure, ulong FaultRevision, ulong ProofRevision, ushort ProofSummary = 0)
{
    internal const int Length = 288;
    internal string WorkerEscrowName => $"cm-worker-{Epoch:N}";
    internal string AgentEscrowName => $"cm-agent-{Epoch:N}";

    internal byte[] Encode()
    {
        Validate();
        byte[] bytes = new byte[Length];
        "CMPJ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
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
        SHA256.HashData(bytes.AsSpan(0, 256), bytes.AsSpan(256));
        return bytes;
    }

    internal static PolicySessionRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length || !bytes[..4].SequenceEqual("CMPJ"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes[..256]), bytes[256..]) ||
            bytes[49] > 1 || bytes[50] > 1 || bytes.Slice(52, 4).ContainsAnyExcept((byte) 0) ||
            bytes.Slice(202, 54).ContainsAnyExcept((byte) 0))
        {
            throw new InvalidDataException("Invalid or unsupported session journal.");
        }
        PolicySessionRecord record = new(new Guid(bytes.Slice(8, 16)), new Guid(bytes.Slice(24, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), (PolicySessionPhase) bytes[48],
            ReadIdentity(bytes.Slice(72, 64)), ReadIdentity(bytes.Slice(136, 64)), bytes[49] == 1,
            bytes[50] == 1, (PolicySessionFault) bytes[51],
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[200..]));
        record.Validate();
        return record;
    }

    private void Validate()
    {
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

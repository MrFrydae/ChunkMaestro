using System.Buffers.Binary;

namespace ChunkMaestro.PrivilegedHelper;

internal enum PolicyWorkerDirection
{
    CoordinatorToWorker,
    WorkerToCoordinator
}

internal enum PolicyWorkerOpcode : ushort
{
    Hello = 1,
    Start,
    Acquired,
    Check,
    Checked,
    Refused,
    Release
}

internal enum PolicyWorkerFailure
{
    None,
    Envelope,
    Ancillary,
    Facts,
    Phase,
    Correlation,
    Revision,
    NativeRefusal,
    Deadline,
    Lease,
    Clock,
    Cancelled,
    Released,
    Crash,
    Cleanup,
    Retirement
}

internal sealed class PolicyWorkerFrame
{
    private readonly byte[] session;
    private readonly byte[] payload;

    internal PolicyWorkerFrame(PolicyWorkerOpcode opcode, ReadOnlySpan<byte> session, ulong sequence,
        uint status, ReadOnlySpan<byte> payload)
    {
        Opcode = opcode;
        this.session = session.ToArray();
        Sequence = sequence;
        Status = status;
        this.payload = payload.ToArray();
    }

    internal PolicyWorkerOpcode Opcode
    {
        get;
    }
    internal ulong Sequence
    {
        get;
    }
    internal uint Status
    {
        get;
    }
    internal ReadOnlySpan<byte> Session => session;
    internal ReadOnlySpan<byte> Payload => payload;
}

internal static class PolicyWorkerProtocol
{
    internal const int MaximumLength = 208;

    // Counts/flags are symbolic transport observations, never verified SCM_RIGHTS.
    internal static PolicyWorkerFailure Decode(ReadOnlySpan<byte> input, PolicyWorkerDirection direction,
        int symbolicRights, bool payloadTruncated, bool controlTruncated, out PolicyWorkerFrame? frame, bool unexpectedAncillary = false)
    {
        frame = null;
        if (input.Length is < 48 or > MaximumLength)
        {
            return PolicyWorkerFailure.Envelope;
        }
        Span<byte> snapshot = stackalloc byte[MaximumLength];
        input.CopyTo(snapshot);
        ReadOnlySpan<byte> bytes = snapshot[..input.Length];
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (!bytes[..4].SequenceEqual("CMPW"u8) || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 ||
            opcode is < 1 or > 7 || U32(bytes, 8) != bytes.Length || U32(bytes, 12) != 0 ||
            U32(bytes, 44) != bytes.Length - 48)
        {
            return PolicyWorkerFailure.Envelope;
        }
        PolicyWorkerOpcode operation = (PolicyWorkerOpcode) opcode;
        bool command = operation is PolicyWorkerOpcode.Start or PolicyWorkerOpcode.Check or PolicyWorkerOpcode.Release;
        if (direction != (command ? PolicyWorkerDirection.CoordinatorToWorker : PolicyWorkerDirection.WorkerToCoordinator))
        {
            return PolicyWorkerFailure.Envelope;
        }
        int length = operation switch
        {
            PolicyWorkerOpcode.Acquired => 208,
            PolicyWorkerOpcode.Checked => 80,
            _ => 48
        };
        uint status = U32(bytes, 40);
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
        bool zeroSession = bytes.Slice(16, 16).IndexOfAnyExcept((byte) 0) < 0;
        if (bytes.Length != length || (operation == PolicyWorkerOpcode.Refused ? status is < 1 or > 8 : status != 0) ||
            (operation == PolicyWorkerOpcode.Hello ? !zeroSession || sequence != 0 : zeroSession || sequence == 0) ||
            (operation is PolicyWorkerOpcode.Start or PolicyWorkerOpcode.Acquired && sequence != 1))
        {
            return PolicyWorkerFailure.Envelope;
        }
        if (unexpectedAncillary || payloadTruncated || controlTruncated || symbolicRights != (operation == PolicyWorkerOpcode.Start ? 2 : 0))
        {
            return PolicyWorkerFailure.Ancillary;
        }
        if (operation == PolicyWorkerOpcode.Acquired && !ValidFacts(bytes.Slice(48, 128)))
        {
            return PolicyWorkerFailure.Facts;
        }
        frame = new PolicyWorkerFrame(operation, bytes.Slice(16, 16), sequence, status, bytes[48..]);
        return PolicyWorkerFailure.None;
    }

    // Equivalent to the unchanged 128-byte DecodeFacts ABI, without initializing
    // the native interop type or invoking its resolver. No native call or DATA.
    private static bool ValidFacts(ReadOnlySpan<byte> facts)
    {
        if (U32(facts, 0) != 1 || U32(facts, 4) != 128 || U32(facts, 124) != 0 ||
            U32(facts, 120) is < 1 or > 16384 || !facts.Slice(8, 8).SequenceEqual(facts.Slice(64, 8)) ||
            (U32(facts, 32) & 0xF000) != 0x4000 || (U32(facts, 88) & 0xF000) != 0x8000 || U32(facts, 92) != 1)
        {
            return false;
        }
        for (int offset = 8; offset <= 64; offset += 56)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(facts[(offset + 8)..]) == 0 || U32(facts, offset + 28) == 0 ||
                U32(facts, offset + 48) >= 1000000000 || U32(facts, offset + 52) >= 1000000000 ||
                (U32(facts, offset + 24) & ~0xFFFFu) != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }
}

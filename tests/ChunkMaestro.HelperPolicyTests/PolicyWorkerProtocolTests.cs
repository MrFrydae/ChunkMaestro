using ChunkMaestro.PrivilegedHelper;
using System.Buffers.Binary;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicyWorkerProtocolTests
{
    [Fact]
    public void IndependentVectorsAcceptAllSevenOpcodesAndEveryRefusalStatus()
    {
        for (int opcode = 1; opcode <= 7; opcode++)
        {
            byte[] bytes = Wire(opcode);
            Assert.Equal(PolicyWorkerFailure.None, Decode(bytes, opcode));
            for (uint status = 1; opcode == 6 && status <= 8; status++)
            {
                Put32(bytes, 40, status);
                Assert.Equal(PolicyWorkerFailure.None, Decode(bytes, opcode));
            }
        }
    }

    [Fact]
    public void EverySizeThrough209RejectsExceptExactAcquiredLength()
    {
        byte[] original = Wire(3);
        for (int size = 0; size <= 209; size++)
        {
            byte[] bytes = new byte[size];
            original.AsSpan(0, Math.Min(size, original.Length)).CopyTo(bytes);
            Assert.Equal(size == 208 ? PolicyWorkerFailure.None : PolicyWorkerFailure.Envelope, Decode(bytes, 3));
        }
    }

    [Fact]
    public void AllOpcodesRejectWrongDirectionFlagsRightsStatusSessionSequenceAndLengths()
    {
        for (int opcode = 1; opcode <= 7; opcode++)
        {
            byte[] bytes = Wire(opcode);
            PolicyWorkerDirection direction = Direction(opcode);
            PolicyWorkerDirection reverse = direction == PolicyWorkerDirection.WorkerToCoordinator ?
                PolicyWorkerDirection.CoordinatorToWorker : PolicyWorkerDirection.WorkerToCoordinator;
            Assert.Equal(PolicyWorkerFailure.Envelope, PolicyWorkerProtocol.Decode(bytes, reverse, opcode == 2 ? 2 : 0, false, false, out _));
            Assert.Equal(PolicyWorkerFailure.Ancillary, PolicyWorkerProtocol.Decode(bytes, direction, opcode == 2 ? 2 : 0, false, false, out _, true));
            for (int rights = -1; rights <= 3; rights++)
            {
                Assert.Equal(rights == (opcode == 2 ? 2 : 0) ? PolicyWorkerFailure.None : PolicyWorkerFailure.Ancillary,
                    PolicyWorkerProtocol.Decode(bytes, direction, rights, false, false, out _));
            }
            Assert.Equal(PolicyWorkerFailure.Ancillary, PolicyWorkerProtocol.Decode(bytes, direction, opcode == 2 ? 2 : 0, true, false, out _));
            Assert.Equal(PolicyWorkerFailure.Ancillary, PolicyWorkerProtocol.Decode(bytes, direction, opcode == 2 ? 2 : 0, false, true, out _));
            foreach (uint status in new uint[]
            {
                0, 1, 8, 9, uint.MaxValue
            })
            {
                byte[] altered = (byte[]) bytes.Clone();
                Put32(altered, 40, status);
                bool valid = opcode == 6 ? status is >= 1 and <= 8 : status == 0;
                Assert.Equal(valid ? PolicyWorkerFailure.None : PolicyWorkerFailure.Envelope, Decode(altered, opcode));
            }
            byte[] wrongSession = (byte[]) bytes.Clone();
            wrongSession.AsSpan(16, 16).Clear();
            wrongSession[16] = opcode == 1 ? (byte) 1 : (byte) 0;
            Assert.Equal(PolicyWorkerFailure.Envelope, Decode(wrongSession, opcode));
            byte[] wrongSequence = (byte[]) bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(wrongSequence.AsSpan(32), opcode == 1 ? 1UL : 0UL);
            Assert.Equal(PolicyWorkerFailure.Envelope, Decode(wrongSequence, opcode));
            foreach (int offset in new int[]
            {
                0, 4, 6, 8, 12, 44
            })
            {
                byte[] altered = (byte[]) bytes.Clone();
                altered[offset] = 255;
                Assert.Equal(PolicyWorkerFailure.Envelope, Decode(altered, opcode));
            }
            foreach (int offset in new int[]
            {
                8, 44
            })
            {
                byte[] altered = (byte[]) bytes.Clone();
                Put32(altered, offset, uint.MaxValue);
                Assert.Equal(PolicyWorkerFailure.Envelope, Decode(altered, opcode));
            }
        }
    }

    [Fact]
    public void FactsAbiRejectsReservedCountsByteOrderAndSemanticCorruption()
    {
        foreach (int offset in new int[]
            {
                0, 4, 8, 32, 56, 60, 64, 88, 92, 112, 116, 120, 124
            })
        {
            byte[] bytes = Wire(3);
            Put32(bytes, 48 + offset, uint.MaxValue);
            Assert.Equal(PolicyWorkerFailure.Facts, Decode(bytes, 3));
        }
        foreach (uint count in new uint[]
            {
                0, 16385, 0x01000000
            })
        {
            byte[] bytes = Wire(3);
            Put32(bytes, 168, count);
            Assert.Equal(PolicyWorkerFailure.Facts, Decode(bytes, 3));
        }
    }

    [Fact]
    public void ParserAndReceiptDoNotBorrowCallerArrays()
    {
        byte[] bytes = Wire(3);
        Assert.Equal(PolicyWorkerFailure.None, PolicyWorkerProtocol.Decode(bytes, Direction(3), 0, false, false, out PolicyWorkerFrame? frame));
        Assert.NotNull(frame);
        bytes.AsSpan().Clear();
        Assert.Equal((byte) 7, frame.Session[0]);
        PolicyWorkerReceipt receipt = new(frame.Payload[..128], frame.Payload[128..]);
        byte[] facts = receipt.CopyFacts();
        byte[] revision = receipt.CopyRevision();
        facts.AsSpan().Clear();
        revision.AsSpan().Clear();
        Assert.Equal((byte) 1, receipt.CopyFacts()[0]);
        Assert.Equal((byte) 42, receipt.CopyRevision()[0]);
    }

    internal static PolicyWorkerDirection Direction(int opcode)
    {
        return opcode is 2 or 4 or 7 ? PolicyWorkerDirection.CoordinatorToWorker : PolicyWorkerDirection.WorkerToCoordinator;
    }

    internal static PolicyWorkerFailure Decode(byte[] bytes, int opcode)
    {
        return PolicyWorkerProtocol.Decode(bytes, Direction(opcode), opcode == 2 ? 2 : 0, false, false, out _);
    }

    // Independent literal wire fixture, deliberately no product encoder.
    internal static byte[] Wire(int opcode, ulong sequence = 1)
    {
        int length = opcode == 3 ? 208 : opcode == 5 ? 80 : 48;
        byte[] bytes = new byte[length];
        "CMPW"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[6] = (byte) opcode;
        Put32(bytes, 8, (uint) length);
        bytes[16] = opcode == 1 ? (byte) 0 : (byte) 7;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), opcode == 1 ? 0 : sequence);
        Put32(bytes, 40, opcode == 6 ? 1u : 0u);
        Put32(bytes, 44, (uint) length - 48);
        if (opcode == 3)
        {
            Put32(bytes, 48, 1);
            Put32(bytes, 52, 128);
            Put32(bytes, 64, 11);
            Put32(bytes, 80, 0x41ED);
            Put32(bytes, 84, 2);
            Put32(bytes, 120, 12);
            Put32(bytes, 136, 0x81A4);
            Put32(bytes, 140, 1);
            Put32(bytes, 168, 1);
            bytes.AsSpan(176, 32).Fill(42);
        }
        if (opcode == 5)
        {
            bytes.AsSpan(48, 32).Fill(42);
        }
        return bytes;
    }

    internal static void Put32(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}

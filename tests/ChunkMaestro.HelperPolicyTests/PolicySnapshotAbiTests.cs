using ChunkMaestro.PrivilegedHelper;
using System.Buffers.Binary;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySnapshotAbiTests
{
    // Independently assembled LE vector; synthetic UID0 is only a numeric shape.
    internal static byte[] Vector()
    {
        byte[] bytes = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 128);
        foreach (int offset in new[]
        {
            8, 64
        })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), 0x12345678);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), 0x9ABCDEF0);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), 0xFEDCBA9876543210);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 20), 0x87654321);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), offset == 8 ? 0x41EDu : 0x81A4u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 28), offset == 8 ? 2u : 1u);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 32), -1234567890123);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 40), long.MinValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 48), 999999999);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 52), 123456789);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(120), 16384);
        return bytes;
    }

    [Fact]
    public void IndependentLittleEndianVectorPreservesWidthsAndSignedSeconds()
    {
        PolicySnapshotNative.SnapshotFacts facts = PolicySnapshotNative.DecodeFacts(Vector());
        Assert.Equal(0xFEDCBA9876543210, facts.Leaf.Inode);
        Assert.Equal(0x12345678u, facts.Parent.DeviceMajor);
        Assert.Equal(0x9ABCDEF0u, facts.Leaf.DeviceMinor);
        Assert.Equal(0x87654321u, facts.Leaf.Gid);
        Assert.Equal(-1234567890123, facts.Parent.ChangeSeconds);
        Assert.Equal(long.MinValue, facts.Leaf.ModifySeconds);
        Assert.Equal(999999999u, facts.Parent.ChangeNanos);
        Assert.Equal(123456789u, facts.Leaf.ModifyNanos);
        Assert.Equal(16384u, facts.ByteCount);
        Assert.True(facts.HasRootNumericShape);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 127)]
    [InlineData(124, 1)]
    [InlineData(120, 0)]
    [InlineData(120, 16385)]
    [InlineData(56, 1000000000)]
    [InlineData(116, 1000000000)]
    [InlineData(92, 2)]
    [InlineData(88, 0x41ED)]
    [InlineData(8, 1)]
    public void MalformedEnvelopeCannotBecomeFacts(int offset, uint value)
    {
        byte[] bytes = Vector();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.Throws<InvalidOperationException>(() => PolicySnapshotNative.DecodeFacts(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(129)]
    public void ExactEnvelopeLengthRequired(int length)
    {
        Assert.Throws<InvalidOperationException>(() => PolicySnapshotNative.DecodeFacts(new byte[length]));
    }

    [Theory]
    [InlineData(24, 1000)]
    [InlineData(80, 1000)]
    [InlineData(32, 0x41FF)]
    [InlineData(32, 0x4180)]
    [InlineData(88, 0x81ED)]
    [InlineData(88, 0x89A4)]
    public void RootNumericShapeIsNecessaryFilterOnly(int offset, uint value)
    {
        byte[] bytes = Vector();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.False(PolicySnapshotNative.DecodeFacts(bytes).HasRootNumericShape);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void UnknownStatusIsAbiDefect(int status)
    {
        Assert.Equal("InvalidSnapshotAbi", Assert.Throws<InvalidOperationException>(() => PolicySnapshotNative.CheckStatus(status)).Message);
    }
}

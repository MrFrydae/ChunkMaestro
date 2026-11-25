using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

internal static partial class PolicySnapshotNative
{
    private const string Library = InspectorNativeLibrary.Snapshot;

    static PolicySnapshotNative()
    {
        InspectorNativeLibrary.Initialize();
    }

    internal static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
    }

    [LibraryImport(Library, EntryPoint = "cm_policy_snapshot_v1_take")]
    internal static partial int Take(ref int parent, ref int file, out nint snapshot);
    [LibraryImport(Library, EntryPoint = "cm_policy_snapshot_v1_copy")]
    internal static partial int Copy(SnapshotHandle snapshot, [Out] byte[] bytes, uint capacity, out uint written);
    [LibraryImport(Library, EntryPoint = "cm_policy_snapshot_v1_facts")]
    internal static partial int Facts(SnapshotHandle snapshot, [Out] byte[] bytes, uint capacity, out uint written);
    [LibraryImport(Library, EntryPoint = "cm_policy_snapshot_v1_check")]
    internal static partial int Check(SnapshotHandle snapshot);
    [LibraryImport(Library, EntryPoint = "cm_policy_snapshot_v1_destroy")]
    private static partial void Destroy(ref nint snapshot);

    internal sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SnapshotHandle(nint pointer) : base(true)
        {
            SetHandle(pointer);
        }

        internal void Adopt(nint pointer)
        {
            SetHandle(pointer);
        }

        protected override bool ReleaseHandle()
        {
            Destroy(ref handle);
            return true;
        }
    }

    internal static void CheckStatus(int status)
    {
        if (status is < 0 or > 6)
        {
            throw new InvalidOperationException("InvalidSnapshotAbi");
        }
        if (status != 0)
        {
            throw new InvalidOperationException("SnapshotRefusal" + status);
        }
    }

    internal readonly record struct ObjectFacts(uint DeviceMajor, uint DeviceMinor, ulong Inode, uint Uid, uint Gid,
        uint Mode, uint Links, long ChangeSeconds, long ModifySeconds, uint ChangeNanos, uint ModifyNanos);

    internal readonly record struct SnapshotFacts(ObjectFacts Parent, ObjectFacts Leaf, uint ByteCount)
    {
        internal bool HasRootNumericShape => Parent.Uid == 0 && Leaf.Uid == 0 &&
            (Parent.Mode & 0xF000) == 0x4000 && (Parent.Mode & 0xE12) == 0 && (Parent.Mode & 0x140) == 0x140 &&
            (Leaf.Mode & 0xF000) == 0x8000 && (Leaf.Mode & 0xFFF) is 0x1A4 or 0x1A0;
    }

    internal static SnapshotFacts DecodeFacts(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 128 || U32(bytes, 0) != 1 || U32(bytes, 4) != 128 || U32(bytes, 124) != 0)
        {
            throw new InvalidOperationException("InvalidSnapshotAbi");
        }
        ObjectFacts parent = DecodeObject(bytes.Slice(8, 56));
        ObjectFacts leaf = DecodeObject(bytes.Slice(64, 56));
        uint count = U32(bytes, 120);
        if (count is < 1 or > 16384 || parent.DeviceMajor != leaf.DeviceMajor || parent.DeviceMinor != leaf.DeviceMinor ||
            (parent.Mode & 0xF000) != 0x4000 || (leaf.Mode & 0xF000) != 0x8000 || leaf.Links != 1)
        {
            throw new InvalidOperationException("InvalidSnapshotAbi");
        }
        return new SnapshotFacts(parent, leaf, count);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static ObjectFacts DecodeObject(ReadOnlySpan<byte> bytes)
    {
        ObjectFacts facts = new(U32(bytes, 0), U32(bytes, 4), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            U32(bytes, 16), U32(bytes, 20), U32(bytes, 24), U32(bytes, 28),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]), U32(bytes, 48), U32(bytes, 52));
        if (facts.Inode == 0 || facts.Links == 0 || facts.ChangeNanos >= 1000000000 || facts.ModifyNanos >= 1000000000 ||
            (facts.Mode & ~0xFFFFu) != 0)
        {
            throw new InvalidOperationException("InvalidSnapshotAbi");
        }
        return facts;
    }
}

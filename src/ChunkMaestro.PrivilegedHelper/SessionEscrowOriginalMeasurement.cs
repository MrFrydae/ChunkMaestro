using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct OriginalMeasurementAbi
{
    [FieldOffset(0)] internal ulong Device;
    [FieldOffset(8)] internal ulong Inode;
    [FieldOffset(16)] internal uint Flags;
    [FieldOffset(20)] internal byte Architecture;
    [FieldOffset(21)] internal byte Normalization;
    [FieldOffset(22)] internal byte Source;
    [FieldOffset(23)] internal byte Reserved;
}

// Implemented by fixed native dispatch in production; fake implementations only in tests.
internal interface IOriginalMeasurementAbi
{
    int MeasureOriginal(int borrowed, out OriginalMeasurementAbi value);
}

// Snapshot facts only. No acquisition, lifetime, exclusive mutation or generation authority.
internal sealed record SessionEscrowOriginalMeasurement(
    ulong Device, ulong Inode, uint Flags, byte Architecture, byte Normalization, byte Source)
{
    internal static SessionEscrowOriginalMeasurement MeasureOriginalUnqualified(SafeHandle original,
        CancellationToken cancellationToken = default)
    {
        return MeasureOriginalUnqualified(original, SessionEscrowNative.Instance, cancellationToken);
    }

    // No shipping caller: a future owner must serialize non-reentrantly, retain first
    // failure/poison, exclude aliases and prevent next actions while closing/cancelled.
    internal static SessionEscrowOriginalMeasurement MeasureOriginalUnqualified(SafeHandle original,
        IOriginalMeasurementAbi abi, CancellationToken cancellationToken = default)
    {
        bool held = false;
        SessionEscrowOriginalMeasurement? result = null;
        bool refused = false;
        try
        {
            if (original is null || original.IsClosed || original.IsInvalid || abi is null)
            {
                throw new InvalidOperationException();
            }
            original.DangerousAddRef(ref held);
            long descriptor = original.DangerousGetHandle().ToInt64();
            if (descriptor is < 0 or > int.MaxValue)
            {
                throw new InvalidOperationException();
            }
            byte architecture = RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => 1,
                System.Runtime.InteropServices.Architecture.Arm64 => 2,
                _ => 0
            };
            if (architecture == 0)
            {
                throw new InvalidOperationException();
            }
            cancellationToken.ThrowIfCancellationRequested();
            int status = abi.MeasureOriginal((int) descriptor, out OriginalMeasurementAbi value);
            if (status != 0 || Marshal.SizeOf<OriginalMeasurementAbi>() != 24 ||
                value.Device == 0 || value.Inode == 0 || value.Flags is not (2 or 0x802) ||
                value.Architecture != architecture || value.Normalization != 1 || value.Source != 1 || value.Reserved != 0)
            {
                throw new InvalidOperationException();
            }
            result = new(value.Device, value.Inode, value.Flags, value.Architecture, value.Normalization, value.Source);
            // Dispatch is synchronous and must drain. Late cancellation yields no usable result.
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            refused = true;
        }
        finally
        {
            if (held)
            {
                try
                {
                    original!.DangerousRelease();
                }
                catch
                {
                    refused = true;
                }
            }
            GC.KeepAlive(original);
        }
        // Completion observes cancellation after pin cleanup. Later cancellation
        // belongs to the consuming owner; no atomic admission/send is supplied.
        if (refused || result is null || cancellationToken.IsCancellationRequested)
        {
            // Never expose native/loader/callback details or exception chains.
            throw new InvalidOperationException("EscrowOriginalMeasurementRefusedUnqualified");
        }
        return result;
    }
}

namespace ChunkMaestro.PrivilegedHelper;

internal static partial class RetainedPolicyAcquisition
{
    // Ownership transfers only after platform/cancellation preconditions. No runtime consumer.
    internal static RetainedPolicyDataUnqualified Acquire(ref int parent, ref int file, CancellationToken cancellationToken = default)
    {
        PolicySnapshotNative.RequireLinux();
        cancellationToken.ThrowIfCancellationRequested();
        return RetainedPolicyDataUnqualified.AcquireOwned(ref parent, ref file, cancellationToken);
    }
}

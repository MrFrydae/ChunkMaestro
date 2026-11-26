using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

// Native C ABI on supported Linux x64/arm64: sizeof(identity)=24, alignment=8.
// These labels have no authentication, manager acceptance or cleanup authority.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct SessionEscrowIdentityUnqualified
{
    [FieldOffset(0)] internal ulong Device;
    [FieldOffset(8)] internal ulong Inode;
    [FieldOffset(16)] internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SessionEscrowObservationUnqualified
{
    internal int Readable;
    internal int Hup;
}

// Internal dispatch seam for managed lifetime/fault tests; production always uses Native.
internal interface ISessionEscrowAbi
{
    int Bind(int worker, int agent, byte[] epoch, out nint session);
    int Handback(byte[] epoch, SessionEscrowIdentityUnqualified[] expected, out nint session);
    int Metadata(nint session, SessionEscrowIdentityUnqualified[] identities);
    int Upload(nint session, ulong deadline);
    int Observe(nint session, uint role, out SessionEscrowObservationUnqualified observation);
    int Close(ref nint session);
}

internal sealed partial class SessionEscrowNative : ISessionEscrowAbi, IOriginalMeasurementAbi, IAgentCandidateAbi
{
    internal static readonly SessionEscrowNative Instance = new();

    private SessionEscrowNative()
    {
        InspectorNativeLibrary.Initialize();
    }

    public int Bind(int worker, int agent, byte[] epoch, out nint session)
    {
        PolicySnapshotNative.RequireLinux();
        return BindNative(worker, agent, epoch, out session);
    }

    public int Handback(byte[] epoch, SessionEscrowIdentityUnqualified[] expected, out nint session)
    {
        PolicySnapshotNative.RequireLinux();
        return HandbackNative(epoch, expected, out session);
    }

    public int Metadata(nint session, SessionEscrowIdentityUnqualified[] identities)
    {
        return MetadataNative(session, identities);
    }

    public int Upload(nint session, ulong deadline)
    {
        return UploadNative(session, deadline);
    }

    public int Observe(nint session, uint role, out SessionEscrowObservationUnqualified observation)
    {
        return ObserveNative(session, role, out observation);
    }

    public int Close(ref nint session)
    {
        return CloseNative(ref session);
    }

    public int MeasureOriginal(int borrowed, out OriginalMeasurementAbi value)
    {
        PolicySnapshotNative.RequireLinux();
        return MeasureOriginalNative(borrowed, out value);
    }

    public int AcquireCandidate(int borrowed, out nint candidate, out AgentCandidateDiagnosticsAbi diagnostics)
    {
        PolicySnapshotNative.RequireLinux();
        return AcquireCandidateNative(borrowed, out candidate, out diagnostics);
    }

    public int MeasureCandidate(nint candidate, out OriginalMeasurementAbi value, out AgentCandidateDiagnosticsAbi diagnostics)
    {
        return MeasureCandidateNative(candidate, out value, out diagnostics);
    }

    public int CloseCandidate(ref nint candidate, out AgentCandidateDiagnosticsAbi diagnostics)
    {
        return CloseCandidateNative(ref candidate, out diagnostics);
    }

    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_agent_candidate_acquire_v1")]
    private static partial int AcquireCandidateNative(int borrowed, out nint candidate, out AgentCandidateDiagnosticsAbi diagnostics);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_agent_candidate_measure_unqualified_v1")]
    private static partial int MeasureCandidateNative(nint candidate, out OriginalMeasurementAbi value, out AgentCandidateDiagnosticsAbi diagnostics);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_agent_candidate_close_v1")]
    private static partial int CloseCandidateNative(ref nint candidate, out AgentCandidateDiagnosticsAbi diagnostics);

    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_measure_original_v1")]
    private static partial int MeasureOriginalNative(int borrowed, out OriginalMeasurementAbi value);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_bind")]
    private static partial int BindNative(int worker, int agent, byte[] epoch, out nint session);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_handback")]
    private static partial int HandbackNative(byte[] epoch, [In] SessionEscrowIdentityUnqualified[] expected, out nint session);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_metadata")]
    private static partial int MetadataNative(nint session, [Out] SessionEscrowIdentityUnqualified[] identities);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_upload")]
    private static partial int UploadNative(nint session, ulong deadline);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_observe")]
    private static partial int ObserveNative(nint session, uint role, out SessionEscrowObservationUnqualified observation);
    [LibraryImport(InspectorNativeLibrary.SessionEscrow, EntryPoint = "cm_escrow_close")]
    private static partial int CloseNative(ref nint session);
}

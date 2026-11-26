using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

[StructLayout(LayoutKind.Explicit, Size = 32, Pack = 4)]
internal struct AgentCandidateDiagnosticsAbi
{
    [FieldOffset(0)] internal uint Version;
    [FieldOffset(4)] internal uint FirstStage;
    [FieldOffset(8)] internal uint FirstCode;
    [FieldOffset(12)] internal int FirstErrno;
    [FieldOffset(16)] internal uint CleanupStage;
    [FieldOffset(20)] internal uint CleanupCode;
    [FieldOffset(24)] internal int CleanupErrno;
    [FieldOffset(28)] internal uint OwnershipFlags;
}

// Immutable local facts, never whole-original closure or admission proof.
internal sealed record AgentCandidateDiagnosticsUnqualified(uint FirstStage, uint FirstCode, int FirstErrno,
    uint CleanupStage, uint CleanupCode, int CleanupErrno, uint OwnershipFlags)
{
    internal bool Refused => FirstCode != 0 || CleanupCode != 0 || (OwnershipFlags & 6) != 0;

    internal static AgentCandidateDiagnosticsUnqualified Validate(int status, AgentCandidateDiagnosticsAbi d)
    {
        if (d.Version != 1 || d.FirstStage > 13 || d.FirstCode > 7 ||
            d.CleanupStage > 13 || d.CleanupCode is not (0 or 4) || d.FirstErrno is < 0 or > 4095 || d.CleanupErrno is < 0 or > 4095 ||
            (d.OwnershipFlags & ~7u) != 0 || status != d.FirstCode ||
            (d.FirstCode == 0 ? d.FirstStage != 0 || d.FirstErrno != 0 : d.FirstStage == 0) ||
            (d.CleanupCode == 0 ? d.CleanupStage != 0 || d.CleanupErrno != 0 :
                d.CleanupStage is not (11 or 12) || d.FirstCode == 0 || (d.OwnershipFlags & 2) == 0) ||
            ((d.OwnershipFlags & 2) != 0 && d.CleanupCode == 0) ||
            ((d.OwnershipFlags & 6) != 0 && d.FirstCode == 0))
        {
            throw new InvalidOperationException("AgentCandidateMalformedUnqualified");
        }
        return new(d.FirstStage, d.FirstCode, d.FirstErrno, d.CleanupStage, d.CleanupCode,
            d.CleanupErrno, d.OwnershipFlags);
    }
}

internal interface IAgentCandidateAbi
{
    int AcquireCandidate(int borrowed, out nint candidate, out AgentCandidateDiagnosticsAbi diagnostics);
    int MeasureCandidate(nint candidate, out OriginalMeasurementAbi value, out AgentCandidateDiagnosticsAbi diagnostics);
    int CloseCandidate(ref nint candidate, out AgentCandidateDiagnosticsAbi diagnostics);
}

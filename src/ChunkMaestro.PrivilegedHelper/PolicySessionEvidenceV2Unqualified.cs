namespace ChunkMaestro.PrivilegedHelper;

// Private immutable claims. No value here authenticates a manager, measures a descriptor,
// converts to production authority, or establishes native cancellation/send atomicity.
internal readonly record struct EvidenceKeyV2Unqualified(Guid Boot, Guid Epoch, ulong Revision,
    PolicySessionIdentity Agent, PolicySessionIdentity Worker, PolicySessionNativeMetadataV2Unqualified NativeMetadata)
{
    internal static EvidenceKeyV2Unqualified From(PolicySessionRecordV2Unqualified state)
    {
        return new(state.Boot, state.Epoch, state.Revision, state.Agent, state.Worker, state.NativeMetadata);
    }
}

internal sealed record RetirementEvidenceV2Unqualified(EvidenceKeyV2Unqualified Key, PolicyRetirementObservations Observations);
internal sealed record AcquiredAgentV2Unqualified(PolicySessionIdentity Agent, PolicySessionNativeMetadataV2Unqualified Metadata);
internal sealed record BoundOriginalsV2Unqualified(PolicySessionIdentity Worker,
    PolicySessionNativeTupleV2Unqualified WorkerNative, PolicySessionNativeTupleV2Unqualified AgentNative,
    PolicySessionNativeMetadataV2Unqualified Metadata);
internal sealed record EscrowEntryV2Unqualified(string Name, PolicySessionIdentity Original,
    PolicyEscrowRole Role, PolicyEscrowFlags Flags, PolicySessionNativeTupleV2Unqualified Native);

internal sealed class StoreSnapshotV2Unqualified
{
    internal EvidenceKeyV2Unqualified Key
    {
        get;
    }
    internal int ReportedCount
    {
        get;
    }
    internal IReadOnlyList<EscrowEntryV2Unqualified> Entries
    {
        get;
    }
    internal Guid HolderInvocation
    {
        get;
    }
    internal ulong ConfigurationRevision
    {
        get;
    }
    internal bool AuthenticatedClaim
    {
        get;
    }
    internal bool QualifiedClaim
    {
        get;
    }

    internal StoreSnapshotV2Unqualified(EvidenceKeyV2Unqualified key, int reportedCount,
        IEnumerable<EscrowEntryV2Unqualified> entries, Guid holderInvocation, ulong configurationRevision,
        bool authenticatedClaim, bool qualifiedClaim)
    {
        Key = key;
        ReportedCount = reportedCount;
        // Each nested entry is sealed and consists only of immutable scalar/value fields.
        Entries = Array.AsReadOnly(entries.ToArray());
        HolderInvocation = holderInvocation;
        ConfigurationRevision = configurationRevision;
        AuthenticatedClaim = authenticatedClaim;
        QualifiedClaim = qualifiedClaim;
    }
}

internal abstract class PolicySessionCoordinatorPortsV2Unqualified
{
    internal abstract long BoottimeUnqualified();
    // Inline prior-process/FD closure AND manager boundary; Empty bytes are insufficient.
    internal abstract void ObserveEmptyOwnerBoundaryUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract AcquiredAgentV2Unqualified AcquireOriginalAgentBeforeReserveUnqualified(PolicySessionIdentity requested);
    internal abstract void StartOriginalUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract BoundOriginalsV2Unqualified AcquireOriginalWorkerUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract void UploadOnceUnqualified(PolicySessionRecordV2Unqualified state, PolicySessionExpectedNativePairV2Unqualified expected);
    internal abstract StoreSnapshotV2Unqualified ReadStoreUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract void PublishOnceUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract RetirementEvidenceV2Unqualified ObserveRetirementUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract void RemoveExactNameUnqualified(PolicySessionRecordV2Unqualified state, EscrowEntryV2Unqualified entry);
    internal abstract void RemovalBarrierUnqualified(PolicySessionRecordV2Unqualified state);
    internal abstract void CloseLocalWitnessesOnceUnqualified();
}

namespace ChunkMaestro.PrivilegedHelper;

// Serialized claims only. This guard authenticates no acquisition, proof, owner or action.
internal static class PolicySessionTransitionV2Unqualified
{
    internal static void Require(PolicySessionRecordV2Unqualified previous, PolicySessionRecordV2Unqualified next)
    {
        // Validate both complete records before comparing transitions, including absent metadata.
        previous.Encode();
        next.Encode();
        Check(next.Revision == checked(previous.Revision + 1) && next.Boot == previous.Boot);
        if (previous.Phase == PolicySessionPhase.Empty)
        {
            // A future qualified owner must supply the missing prior-owner/store boundary.
            Check(next.Phase == PolicySessionPhase.Occupied && next.Epoch != previous.Epoch &&
                next.Epoch != Guid.Empty && next.Worker == default && !next.EverArmed &&
                next.FirstFailure == PolicySessionFault.None && next.FaultRevision == 0 && next.ProofRevision == 0);
            return;
        }

        bool bind = previous.Phase == PolicySessionPhase.StartAttempted && next.Phase == PolicySessionPhase.Bound;
        Check(next.Epoch == previous.Epoch && next.Agent == previous.Agent &&
            next.NativeMetadata.Architecture == previous.NativeMetadata.Architecture &&
            next.NativeMetadata.Normalization == previous.NativeMetadata.Normalization &&
            next.NativeMetadata.Source == previous.NativeMetadata.Source &&
            next.NativeMetadata.AgentFlags == previous.NativeMetadata.AgentFlags);
        Check(bind
            ? previous.Worker == default && next.Worker.IsComplete && next.NativeMetadata.Presence == 3
            : next.Worker == previous.Worker && next.NativeMetadata == previous.NativeMetadata);
        Check(next.AttemptConsumed == (previous.AttemptConsumed || next.Phase == PolicySessionPhase.StartAttempted));
        Check(next.EverArmed == (previous.EverArmed || next.Phase == PolicySessionPhase.Armed));
        Check(previous.FirstFailure == PolicySessionFault.None || next.FirstFailure == previous.FirstFailure);

        if (next.Phase == PolicySessionPhase.Retiring)
        {
            Check(next.ProofRevision == 0 && next.ProofSummary == 0 &&
                (next.FaultRevision == next.Revision ||
                    (next.FaultRevision == previous.FaultRevision && next.FirstFailure == previous.FirstFailure)));
            return;
        }
        Check(next.FirstFailure == previous.FirstFailure && next.FaultRevision == previous.FaultRevision);
        bool allowed = (previous.Phase, next.Phase) switch
        {
            (PolicySessionPhase.Occupied, PolicySessionPhase.StartAttempted) => true,
            (PolicySessionPhase.StartAttempted, PolicySessionPhase.Bound) => true,
            (PolicySessionPhase.Bound, PolicySessionPhase.Armed) => true,
            (PolicySessionPhase.Retiring, PolicySessionPhase.Retired) => true,
            (PolicySessionPhase.Retired, PolicySessionPhase.Empty) => true,
            _ => false
        };
        Check(allowed);
        if (next.Phase == PolicySessionPhase.Retired)
        {
            Check(next.ProofRevision == next.Revision);
        }
        else
        {
            Check(next.ProofRevision == previous.ProofRevision && next.ProofSummary == previous.ProofSummary);
        }
    }

    private static void Check(bool valid)
    {
        if (!valid)
        {
            throw new InvalidOperationException("Unqualified journal transition refused.");
        }
    }
}

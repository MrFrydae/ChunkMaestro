namespace ChunkMaestro.Contracts;

// Synthetic logic inputs only; neither a policy loader nor evidence of kernel authentication.
internal sealed record PolicyFacts(uint AgentUid, uint InspectorUid, uint GameUid, string InstanceId, string Revision,
    bool IdentityQualified, bool TargetBound, bool InputsStable);

internal static class InspectionDecisions
{
    internal static InspectReason? Refusal(uint peerUid, InspectRequest request, PolicyFacts policy)
    {
        bool validIdentity = policy.AgentUid != 0 && policy.InspectorUid != 0 && policy.GameUid != 0 &&
            policy.AgentUid != policy.GameUid && policy.AgentUid != policy.InspectorUid && policy.GameUid != policy.InspectorUid;
        if (!validIdentity || !policy.IdentityQualified || peerUid != policy.AgentUid || request.InstanceId != policy.InstanceId || request.PolicyRevision != policy.Revision)
        {
            return InspectReason.PolicyDenied;
        }
        if (!policy.TargetBound || !policy.InputsStable)
        {
            return InspectReason.TargetDrift;
        }
        return null;
    }

    internal static ServiceState MapActiveState(string state)
    {
        return state switch
        {
            "active" => ServiceState.Active,
            "inactive" => ServiceState.Inactive,
            "failed" => ServiceState.Failed,
            "activating" or "deactivating" or "reloading" or "refreshing" => ServiceState.Transitional,
            _ => ServiceState.Unknown
        };
    }
}

// Single outstanding read and local monotonic milliseconds. No scheduler, authority or I/O.
internal sealed class ObservationWindow(InspectRequest expected, long startedAt)
{
    private bool completed;
    private InspectResponse? observation;
    private long receivedAt;

    internal bool Accept(InspectResponse response, long now, string? previousEpoch)
    {
        if (completed)
        {
            observation = null;
            return false;
        }
        completed = true;
        if (startedAt < 0 || now < startedAt || now - startedAt > 3000 || response.Request.RequestId != expected.RequestId ||
            response.Request.InstanceId != expected.InstanceId || response.Request.PolicyRevision != expected.PolicyRevision ||
            (previousEpoch is not null && previousEpoch != response.HelperEpoch))
        {
            return false;
        }
        receivedAt = now;
        observation = response.Outcome == InspectOutcome.Observed ? response : null;
        return observation is not null;
    }

    internal bool IsFresh(long now)
    {
        // Age starts at request issuance, conservatively including query/transport time.
        return observation is not null && startedAt >= 0 && now >= receivedAt && now >= startedAt && now - startedAt <= 15000;
    }

    internal void Invalidate()
    {
        completed = true;
        observation = null;
    }

    internal void ConnectionClosed(bool normalOneShotCompletion)
    {
        if (!normalOneShotCompletion || !completed)
        {
            Invalidate();
        }
    }
}

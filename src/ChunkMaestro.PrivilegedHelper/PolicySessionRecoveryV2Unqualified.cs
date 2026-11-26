namespace ChunkMaestro.PrivilegedHelper;

internal sealed class PolicySessionRecoveryV2Unqualified
{
    private readonly PolicySessionJournalV2Unqualified journal;
    private readonly Guid currentBoot;
    private readonly byte architecture;
    private PolicySessionRecordV2Unqualified state;
    private bool freshReservation;
    private bool storageFailed;

    internal PolicySessionRecoveryV2Unqualified(PolicySessionJournalV2Unqualified journal, Guid currentBoot, byte architecture)
    {
        this.journal = journal;
        this.currentBoot = currentBoot;
        this.architecture = architecture;
        state = journal.Read();
        Require(state.Boot == currentBoot && architecture is 1 or 2 &&
            (state.NativeMetadata.Presence == 0 || state.NativeMetadata.Architecture == architecture), "Boot mismatch requires separately qualified reconciliation.");
    }

    internal PolicySessionRecordV2Unqualified State => state;
    internal bool StorageFailed => storageFailed;
    internal EvidenceKeyV2Unqualified Key => EvidenceKeyV2Unqualified.From(state);
    internal PolicySessionNextAction RecoveryAction => storageFailed ? PolicySessionNextAction.None : state.Phase switch
    {
        PolicySessionPhase.Empty => PolicySessionNextAction.EmptyUnqualified,
        PolicySessionPhase.Retired => PolicySessionNextAction.RemoveExactEscrowNamesUnqualified,
        _ => PolicySessionNextAction.ReconcileOriginalOnlyUnqualified
    };

    internal void Reserve(Guid epoch, AcquiredAgentV2Unqualified acquired)
    {
        PolicySessionIdentity agent = acquired.Agent;
        acquired.Metadata.Validate();
        Require(acquired.Metadata.Presence == 1 && acquired.Metadata.Architecture == architecture && agent.IsComplete, "Agent measurement refused.");
        Require(state.Phase == PolicySessionPhase.Empty && epoch != Guid.Empty && epoch != state.Epoch,
            "Only a distinct fresh request may reserve empty state.");
        Save(new(currentBoot, epoch, checked(state.Revision + 1), PolicySessionPhase.Occupied,
            agent, default, false, false, PolicySessionFault.None, 0, 0, 0, acquired.Metadata));
        freshReservation = true;
    }

    internal PolicySessionNextAction ConsumeStartAttempt()
    {
        Require(freshReservation && state.Phase == PolicySessionPhase.Occupied && !state.AttemptConsumed,
            "Recovery never starts or replays an original worker.");
        Save(state with
        {
            Revision = checked(state.Revision + 1),
            Phase = PolicySessionPhase.StartAttempted,
            AttemptConsumed = true
        });
        return PolicySessionNextAction.AttemptOriginalWorkerStartUnqualified;
    }

    internal void BindOriginalWorker(BoundOriginalsV2Unqualified bound)
    {
        PolicySessionIdentity worker = bound.Worker;
        Require(bound.Metadata.Architecture == state.NativeMetadata.Architecture &&
            bound.Metadata.Normalization == state.NativeMetadata.Normalization &&
            bound.Metadata.Source == state.NativeMetadata.Source && bound.Metadata.Presence == 3 &&
            bound.Metadata.AgentFlags == state.NativeMetadata.AgentFlags &&
            bound.AgentNative == new PolicySessionNativeTupleV2Unqualified(state.Agent.PidfsDevice, state.Agent.PidfsInode, state.NativeMetadata.AgentFlags) &&
            bound.WorkerNative == new PolicySessionNativeTupleV2Unqualified(worker.PidfsDevice, worker.PidfsInode, bound.Metadata.WorkerFlags), "Measured originals mismatch.");
        bound.Metadata.Validate();
        Require(freshReservation && state.Phase == PolicySessionPhase.StartAttempted && worker.IsComplete &&
            worker.Invocation != state.Agent.Invocation && worker.PidfsInode != state.Agent.PidfsInode,
            "Original worker binding refused.");
        Save(state with
        {
            Revision = checked(state.Revision + 1),
            Phase = PolicySessionPhase.Bound,
            Worker = worker,
            NativeMetadata = bound.Metadata
        });
    }

    internal PolicySessionNextAction Arm(RetirementEvidenceV2Unqualified evidence,
        IReadOnlyList<EscrowEntryV2Unqualified> entries)
    {
        Require(freshReservation && state.Phase == PolicySessionPhase.Bound && state.FirstFailure == PolicySessionFault.None,
            "Gate may only be authorized once in original startup.");
        Match(evidence.Key);
        Require(entries.Count == 2, "Exactly two original roles required.");
        ValidateInventory(entries, true);
        Save(state with
        {
            Revision = checked(state.Revision + 1),
            Phase = PolicySessionPhase.Armed,
            EverArmed = true
        });
        return PolicySessionNextAction.PublishGateOnceUnqualified;
    }

    internal void Retire(PolicySessionFault fault)
    {
        Require(state.Phase != PolicySessionPhase.Empty && Enum.IsDefined(fault), "Retirement refused.");
        ulong revision = checked(state.Revision + 1);
        Save(state with
        {
            Revision = revision,
            Phase = PolicySessionPhase.Retiring,
            FirstFailure = state.FirstFailure == PolicySessionFault.None ? fault : state.FirstFailure,
            FaultRevision = fault == PolicySessionFault.None ? state.FaultRevision : revision,
            ProofRevision = 0,
            ProofSummary = 0
        });
        freshReservation = false;
    }

    internal PolicySessionNextAction ConfirmRetirement(RetirementEvidenceV2Unqualified evidence)
    {
        Require(state.Phase == PolicySessionPhase.Retiring, "Retirement must precede proof.");
        Match(evidence.Key);
        PolicyRetirementObservations required = PolicyRetirementObservations.GateRevoked |
            PolicyRetirementObservations.SoleJobTerminal | PolicyRetirementObservations.NoPendingJobs |
            PolicyRetirementObservations.ProtectedDomainEmpty | PolicyRetirementObservations.OwnedReferencesClosed |
            PolicyRetirementObservations.OriginalAgentReferencesClosed;
        required |= state.EverArmed
            ? PolicyRetirementObservations.OriginalWorkerHup | PolicyRetirementObservations.SameWorkerGenerationTerminal |
                PolicyRetirementObservations.AllThreadDescendantAndAsyncClosure
            : PolicyRetirementObservations.StartupNoInputRightsChildrenOrAsyncRoutes;
        Require((evidence.Observations & required) == required && ((ushort) evidence.Observations & ~1023) == 0,
            "Incomplete retirement conjunction.");
        ulong revision = checked(state.Revision + 1);
        Save(state with
        {
            Revision = revision,
            Phase = PolicySessionPhase.Retired,
            ProofRevision = revision,
            ProofSummary = (ushort) evidence.Observations
        });
        return PolicySessionNextAction.RemoveExactEscrowNamesUnqualified;
    }

    // RETIRED recovery accepts only exact remaining originals. Never a generic fdstore clean.
    internal void ValidateRemainingEscrow(RetirementEvidenceV2Unqualified evidence,
        IReadOnlyList<EscrowEntryV2Unqualified> entries)
    {
        Require(state.Phase == PolicySessionPhase.Retired, "Durable retirement proof required before removal.");
        Match(evidence.Key);
        ValidateInventory(entries, false);
    }

    internal void ConfirmEmptyStoreReadback(RetirementEvidenceV2Unqualified evidence,
        IReadOnlyList<EscrowEntryV2Unqualified> entries)
    {
        ValidateRemainingEscrow(evidence, entries);
        Require(entries.Count == 0, "Exact empty store readback required.");
        Save(state with
        {
            Revision = checked(state.Revision + 1),
            Phase = PolicySessionPhase.Empty
        });
    }

    private void ValidateInventory(IReadOnlyList<EscrowEntryV2Unqualified> entries, bool complete)
    {
        Require(entries.Count <= (state.Worker.IsComplete ? 2 : 1), "Unknown escrow entries.");
        bool agent = false;
        bool worker = false;
        foreach (EscrowEntryV2Unqualified entry in entries)
        {
            Require(entry.Flags == (PolicyEscrowFlags.OriginalNonThreadPidfd | PolicyEscrowFlags.PollingDisabled |
                PolicyEscrowFlags.CloseOnExec), "Unqualified descriptor flags.");
            if (entry.Role == PolicyEscrowRole.Agent)
            {
                Require(!agent && entry.Name == $"cm-agent-{state.Epoch:N}" && entry.Original == state.Agent,
                    "Agent escrow mismatch.");
                Require(entry.Native == new PolicySessionNativeTupleV2Unqualified(state.Agent.PidfsDevice,
                    state.Agent.PidfsInode, state.NativeMetadata.AgentFlags), "Agent native mismatch.");
                agent = true;
            }
            else
            {
                Require(entry.Role == PolicyEscrowRole.Worker && !worker && entry.Name == $"cm-worker-{state.Epoch:N}" &&
                    entry.Original == state.Worker && state.Worker.IsComplete, "Worker escrow mismatch.");
                Require(entry.Native == state.ExpectedNativePair(currentBoot, architecture).Worker, "Worker native mismatch.");
                worker = true;
            }
        }
        Require(!complete || (agent && worker), "Both roles must be confirmed by exact authenticated readback.");
    }

    private void Match(EvidenceKeyV2Unqualified key)
    {
        Require(key == Key, "Stale or unrelated evidence.");
    }

    private void Save(PolicySessionRecordV2Unqualified next)
    {
        Require(!storageFailed, "Storage outcome unknown.");
        try
        {
            journal.Commit(state, next);
            state = next;
        }
        catch
        {
            storageFailed = true;
            throw;
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(reason);
        }
    }
}

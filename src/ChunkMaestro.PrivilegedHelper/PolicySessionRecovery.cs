namespace ChunkMaestro.PrivilegedHelper;

internal enum PolicySessionNextAction
{
    None,
    AttemptOriginalWorkerStartUnqualified,
    PublishGateOnceUnqualified,
    ReconcileOriginalOnlyUnqualified,
    RemoveExactEscrowNamesUnqualified,
    EmptyUnqualified
}

[Flags]
internal enum PolicyRetirementObservations : ushort
{
    None = 0,
    GateRevoked = 1,
    SoleJobTerminal = 2,
    NoPendingJobs = 4,
    ProtectedDomainEmpty = 8,
    OwnedReferencesClosed = 16,
    StartupNoInputRightsChildrenOrAsyncRoutes = 32,
    OriginalWorkerHup = 64,
    SameWorkerGenerationTerminal = 128,
    AllThreadDescendantAndAsyncClosure = 256,
    OriginalAgentReferencesClosed = 512
}

// All observations below are INTERNAL, UNQUALIFIED inputs for a future authenticated native
// adapter. Constructing these values is not a product success gateway or native proof.
internal sealed record PolicyEscrowEntryUnqualified(string Name, PolicySessionIdentity Original,
    PolicyEscrowRole Role, PolicyEscrowFlags Flags);

internal enum PolicyEscrowRole
{
    Agent,
    Worker
}

[Flags]
internal enum PolicyEscrowFlags
{
    None = 0,
    OriginalNonThreadPidfd = 1,
    PollingDisabled = 2,
    CloseOnExec = 4
}

internal sealed record PolicySessionEvidenceUnqualified(Guid Boot, Guid Epoch, ulong Revision,
    PolicySessionIdentity Agent, PolicySessionIdentity Worker, PolicyRetirementObservations Observations);

internal sealed class PolicySessionRecovery
{
    private readonly PolicySessionJournal journal;
    private readonly Guid currentBoot;
    private PolicySessionRecord state;
    private bool freshReservation;
    private bool storageFailed;

    internal PolicySessionRecovery(PolicySessionJournal journal, Guid currentBoot)
    {
        this.journal = journal;
        this.currentBoot = currentBoot;
        state = journal.Read();
        Require(state.Boot == currentBoot, "Boot mismatch requires separately qualified reconciliation.");
    }

    internal PolicySessionRecord State => state;
    internal PolicySessionNextAction RecoveryAction => storageFailed ? PolicySessionNextAction.None : state.Phase switch
    {
        PolicySessionPhase.Empty => PolicySessionNextAction.EmptyUnqualified,
        PolicySessionPhase.Retired => PolicySessionNextAction.RemoveExactEscrowNamesUnqualified,
        _ => PolicySessionNextAction.ReconcileOriginalOnlyUnqualified
    };

    internal void Reserve(Guid epoch, PolicySessionIdentity agent)
    {
        Require(state.Phase == PolicySessionPhase.Empty && epoch != Guid.Empty && epoch != state.Epoch,
            "Only a distinct fresh request may reserve empty state.");
        Save(new(currentBoot, epoch, checked(state.Revision + 1), PolicySessionPhase.Occupied,
            agent, default, false, false, PolicySessionFault.None, 0, 0));
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

    internal void BindOriginalWorker(PolicySessionIdentity worker)
    {
        Require(freshReservation && state.Phase == PolicySessionPhase.StartAttempted && worker.IsComplete &&
            worker.Invocation != state.Agent.Invocation && worker.PidfsInode != state.Agent.PidfsInode,
            "Original worker binding refused.");
        Save(state with
        {
            Revision = checked(state.Revision + 1),
            Phase = PolicySessionPhase.Bound,
            Worker = worker
        });
    }

    internal PolicySessionNextAction Arm(PolicySessionEvidenceUnqualified evidence,
        IReadOnlyList<PolicyEscrowEntryUnqualified> entries)
    {
        Require(freshReservation && state.Phase == PolicySessionPhase.Bound && state.FirstFailure == PolicySessionFault.None,
            "Gate may only be authorized once in original startup.");
        try
        {
            Match(evidence);
            Require(entries.Count == 2, "Exactly two original roles required.");
            ValidateInventory(entries, true);
        }
        catch (InvalidOperationException)
        {
            Retire(PolicySessionFault.Store);
            throw;
        }
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

    internal PolicySessionNextAction ConfirmRetirement(PolicySessionEvidenceUnqualified evidence)
    {
        Require(state.Phase == PolicySessionPhase.Retiring, "Retirement must precede proof.");
        Match(evidence);
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
    internal void ValidateRemainingEscrow(PolicySessionEvidenceUnqualified evidence,
        IReadOnlyList<PolicyEscrowEntryUnqualified> entries)
    {
        Require(state.Phase == PolicySessionPhase.Retired, "Durable retirement proof required before removal.");
        Match(evidence);
        ValidateInventory(entries, false);
    }

    internal void ConfirmEmptyStoreReadback(PolicySessionEvidenceUnqualified evidence,
        IReadOnlyList<PolicyEscrowEntryUnqualified> entries)
    {
        ValidateRemainingEscrow(evidence, entries);
        Require(entries.Count == 0, "Exact empty store readback required.");
        Save(state with
        {
            Revision = checked(state.Revision + 1),
            Phase = PolicySessionPhase.Empty
        });
    }

    private void ValidateInventory(IReadOnlyList<PolicyEscrowEntryUnqualified> entries, bool complete)
    {
        Require(entries.Count <= 2, "Unknown escrow entries.");
        bool agent = false;
        bool worker = false;
        foreach (PolicyEscrowEntryUnqualified entry in entries)
        {
            Require(entry.Flags == (PolicyEscrowFlags.OriginalNonThreadPidfd | PolicyEscrowFlags.PollingDisabled |
                PolicyEscrowFlags.CloseOnExec), "Unqualified descriptor flags.");
            if (entry.Role == PolicyEscrowRole.Agent)
            {
                Require(!agent && entry.Name == state.AgentEscrowName && entry.Original == state.Agent,
                    "Agent escrow mismatch.");
                agent = true;
            }
            else
            {
                Require(entry.Role == PolicyEscrowRole.Worker && !worker && entry.Name == state.WorkerEscrowName &&
                    entry.Original == state.Worker && state.Worker.IsComplete, "Worker escrow mismatch.");
                worker = true;
            }
        }
        Require(!complete || (agent && worker), "Both roles must be confirmed by exact authenticated readback.");
    }

    private void Match(PolicySessionEvidenceUnqualified evidence)
    {
        Require(evidence.Boot == state.Boot && evidence.Epoch == state.Epoch && evidence.Revision == state.Revision &&
            evidence.Agent == state.Agent && evidence.Worker == state.Worker, "Stale or unrelated evidence.");
    }

    private void Save(PolicySessionRecord next)
    {
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

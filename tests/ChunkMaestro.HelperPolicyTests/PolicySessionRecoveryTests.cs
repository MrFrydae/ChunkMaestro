using ChunkMaestro.PrivilegedHelper;
using Xunit;
using static ChunkMaestro.HelperPolicyTests.PolicySessionJournalTests;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionRecoveryTests
{
    private const PolicyRetirementObservations Common = PolicyRetirementObservations.GateRevoked |
        PolicyRetirementObservations.SoleJobTerminal | PolicyRetirementObservations.NoPendingJobs |
        PolicyRetirementObservations.ProtectedDomainEmpty | PolicyRetirementObservations.OwnedReferencesClosed |
        PolicyRetirementObservations.OriginalAgentReferencesClosed;
    private const PolicyRetirementObservations Early = Common | PolicyRetirementObservations.StartupNoInputRightsChildrenOrAsyncRoutes;
    private const PolicyRetirementObservations Armed = Common | PolicyRetirementObservations.OriginalWorkerHup |
        PolicyRetirementObservations.SameWorkerGenerationTerminal | PolicyRetirementObservations.AllThreadDescendantAndAsyncClosure;

    [Fact]
    public void OrderedOriginalSessionConsumesAttemptAndCommitsArmedBeforeReturningGate()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournal journal = fixture.Initialize();
        PolicySessionRecovery recovery = Bound(journal, fixture.Boot);
        Assert.Throws<InvalidOperationException>(() => recovery.ConsumeStartAttempt());
        Assert.Equal(PolicySessionNextAction.PublishGateOnceUnqualified, recovery.Arm(Evidence(recovery), Inventory(recovery)));
        Assert.Equal(PolicySessionPhase.Armed, journal.Read().Phase);
        Assert.Throws<InvalidOperationException>(() => recovery.Arm(Evidence(recovery), Inventory(recovery)));
    }

    [Fact]
    public void PartialAmbiguousDuplicateOrWrongRoleEscrowCannotArm()
    {
        for (int scenario = 0; scenario < 5; scenario++)
        {
            using JournalFixture fixture = new();
            using PolicySessionJournal journal = fixture.Initialize();
            PolicySessionRecovery recovery = Bound(journal, fixture.Boot);
            PolicyEscrowEntryUnqualified[] exact = Inventory(recovery);
            PolicyEscrowEntryUnqualified[] invalid = scenario switch
            {
                0 => [],
                1 => [exact[0]],
                2 => [exact[0], exact[0]],
                3 => [exact[0], exact[1] with
                {
                    Flags = PolicyEscrowFlags.OriginalNonThreadPidfd
                }],
                _ => [exact[0], exact[1] with
                {
                    Original = JournalFixture.Identity(8)
                }]
            };
            Assert.Throws<InvalidOperationException>(() => recovery.Arm(Evidence(recovery), invalid));
            Assert.Equal(PolicySessionPhase.Retiring, journal.Read().Phase);
            Assert.Equal(PolicySessionFault.Store, recovery.State.FirstFailure);
            Assert.Throws<InvalidOperationException>(() => recovery.Arm(Evidence(recovery), exact));
        }
    }

    [Fact]
    public void CancellationBeforeGateAndLaterCleanupFaultInvalidateOldProofAndKeepFirstFailure()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournal journal = fixture.Initialize();
        PolicySessionRecovery recovery = Bound(journal, fixture.Boot);
        recovery.Retire(PolicySessionFault.Cancelled);
        Assert.Throws<InvalidOperationException>(() => recovery.Arm(Evidence(recovery), Inventory(recovery)));
        recovery.ConfirmRetirement(Evidence(recovery, Early));
        PolicySessionEvidenceUnqualified oldProof = Evidence(recovery, Early);
        recovery.Retire(PolicySessionFault.Cleanup);
        Assert.Equal(PolicySessionFault.Cancelled, recovery.State.FirstFailure);
        Assert.Equal(0UL, recovery.State.ProofRevision);
        Assert.Throws<InvalidOperationException>(() => recovery.ConfirmEmptyStoreReadback(oldProof, []));
        Assert.Throws<InvalidOperationException>(() => recovery.ConfirmRetirement(oldProof));
        recovery.ConfirmRetirement(Evidence(recovery, Early));
        recovery.ConfirmEmptyStoreReadback(Evidence(recovery), []);
        Assert.Equal(PolicySessionPhase.Empty, journal.Read().Phase);
    }

    [Fact]
    public void EveryRequiredProofPredicateIndependentlyBlocksRetirement()
    {
        foreach (bool armed in (bool[]) [false, true])
        {
            using JournalFixture fixture = new();
            using PolicySessionJournal journal = fixture.Initialize();
            PolicySessionRecovery recovery = Bound(journal, fixture.Boot);
            if (armed)
            {
                recovery.Arm(Evidence(recovery), Inventory(recovery));
            }
            recovery.Retire(PolicySessionFault.Worker);
            PolicyRetirementObservations full = armed ? Armed : Early;
            foreach (PolicyRetirementObservations observation in Enum.GetValues<PolicyRetirementObservations>())
            {
                if (observation != 0 && (full & observation) != 0)
                {
                    Assert.Throws<InvalidOperationException>(() => recovery.ConfirmRetirement(Evidence(recovery, full & ~observation)));
                    Assert.Equal(PolicySessionPhase.Retiring, journal.Read().Phase);
                }
            }
            if (armed)
            {
                Assert.Throws<InvalidOperationException>(() => recovery.ConfirmRetirement(Evidence(recovery, Early)));
            }
            recovery.ConfirmRetirement(Evidence(recovery, full));
        }
    }

    [Fact]
    public void CrashReopenAtEachPhaseIsRetireOnlyAndPartialRemovalNeedsExactEmptyReadback()
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournal journal = fixture.Initialize())
        {
            PolicySessionRecovery recovery = Bound(journal, fixture.Boot);
            recovery.Arm(Evidence(recovery), Inventory(recovery));
        }
        using (PolicySessionJournal journal = fixture.Open())
        {
            PolicySessionRecovery recovery = new(journal, fixture.Boot);
            Assert.Equal(PolicySessionNextAction.ReconcileOriginalOnlyUnqualified, recovery.RecoveryAction);
            Assert.Throws<InvalidOperationException>(() => recovery.ConsumeStartAttempt());
            Assert.Throws<InvalidOperationException>(() => recovery.Arm(Evidence(recovery), Inventory(recovery)));
            recovery.Retire(PolicySessionFault.Holder);
            recovery.ConfirmRetirement(Evidence(recovery, Armed));
        }
        using (PolicySessionJournal journal = fixture.Open())
        {
            PolicySessionRecovery recovery = new(journal, fixture.Boot);
            Assert.Equal(PolicySessionNextAction.RemoveExactEscrowNamesUnqualified, recovery.RecoveryAction);
            PolicyEscrowEntryUnqualified[] exact = Inventory(recovery);
            recovery.ValidateRemainingEscrow(Evidence(recovery), [exact[1]]);
            Assert.Throws<InvalidOperationException>(() => recovery.ConfirmEmptyStoreReadback(Evidence(recovery), [exact[1]]));
            Assert.Throws<InvalidOperationException>(() => recovery.ValidateRemainingEscrow(Evidence(recovery), [exact[0] with
            {
                Name = "unknown"
            }]));
            recovery.ConfirmEmptyStoreReadback(Evidence(recovery), []);
            Assert.Throws<InvalidOperationException>(() => recovery.Reserve(recovery.State.Epoch, JournalFixture.Identity(1)));
            recovery.Reserve(Guid.NewGuid(), JournalFixture.Identity(1));
        }
    }

    [Fact]
    public void RecoveredOccupiedCannotSpendAnUnusedAttemptAndBootMismatchCannotClear()
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournal journal = fixture.Initialize())
        {
            PolicySessionRecovery recovery = new(journal, fixture.Boot);
            recovery.Reserve(Guid.NewGuid(), JournalFixture.Identity(1));
        }
        using PolicySessionJournal reopened = fixture.Open();
        Assert.Throws<InvalidOperationException>(() => new PolicySessionRecovery(reopened, Guid.NewGuid()));
        PolicySessionRecovery recovered = new(reopened, fixture.Boot);
        Assert.Throws<InvalidOperationException>(() => recovered.ConsumeStartAttempt());
        recovered.Retire(PolicySessionFault.Holder);
        recovered.ConfirmRetirement(Evidence(recovered, Early));
        Assert.Equal(PolicySessionPhase.Retired, reopened.Read().Phase);
    }

    [Fact]
    public void EveryDurableTransitionInterruptionLeavesOldOrNewRecordAndNeverRecoveryStartOrGate()
    {
        foreach (PolicyJournalBarrier stop in Enum.GetValues<PolicyJournalBarrier>())
        {
            for (int interrupted = 1; interrupted <= 7; interrupted++)
            {
                using JournalFixture fixture = new();
                using (PolicySessionJournal initial = fixture.Initialize())
                {
                }
                int transition = 0;
                PolicySessionPhase before;
                using (PolicySessionJournal journal = fixture.Open(barrier =>
                {
                    if (barrier == PolicyJournalBarrier.Write)
                    {
                        transition++;
                    }
                    if (transition == interrupted && barrier == stop)
                    {
                        throw new IOException("Injected persistence interruption.");
                    }
                }))
                {
                    PolicySessionRecovery recovery = new(journal, fixture.Boot);
                    before = recovery.State.Phase;
                    for (int step = 1; step <= interrupted; step++)
                    {
                        before = recovery.State.Phase;
                        if (step == interrupted)
                        {
                            Assert.Throws<IOException>(() => Transition(recovery, step));
                            Assert.Equal(PolicySessionNextAction.None, recovery.RecoveryAction);
                        }
                        else
                        {
                            Transition(recovery, step);
                        }
                    }
                }
                using PolicySessionJournal reopened = fixture.Open();
                PolicySessionRecovery recovered = new(reopened, fixture.Boot);
                Assert.Throws<InvalidOperationException>(() => recovered.ConsumeStartAttempt());
                Assert.Throws<InvalidOperationException>(() => recovered.Arm(Evidence(recovered), Inventory(recovered)));
                if (stop != PolicyJournalBarrier.DirectorySync)
                {
                    Assert.Equal(before, recovered.State.Phase);
                }
                else
                {
                    Assert.NotEqual(before, recovered.State.Phase);
                }
            }
        }
    }

    private static void Transition(PolicySessionRecovery recovery, int step)
    {
        switch (step)
        {
            case 1:
                recovery.Reserve(Guid.NewGuid(), JournalFixture.Identity(1));
                break;
            case 2:
                recovery.ConsumeStartAttempt();
                break;
            case 3:
                recovery.BindOriginalWorker(JournalFixture.Identity(2));
                break;
            case 4:
                recovery.Arm(Evidence(recovery), Inventory(recovery));
                break;
            case 5:
                recovery.Retire(PolicySessionFault.Worker);
                break;
            case 6:
                recovery.ConfirmRetirement(Evidence(recovery, Armed));
                break;
            case 7:
                recovery.ConfirmEmptyStoreReadback(Evidence(recovery), []);
                break;
        }
    }

    private static PolicySessionRecovery Bound(PolicySessionJournal journal, Guid boot)
    {
        PolicySessionRecovery recovery = new(journal, boot);
        recovery.Reserve(Guid.NewGuid(), JournalFixture.Identity(1));
        Assert.Equal(PolicySessionNextAction.AttemptOriginalWorkerStartUnqualified, recovery.ConsumeStartAttempt());
        recovery.BindOriginalWorker(JournalFixture.Identity(2));
        return recovery;
    }

    private static PolicySessionEvidenceUnqualified Evidence(PolicySessionRecovery recovery,
        PolicyRetirementObservations observations = PolicyRetirementObservations.None)
    {
        PolicySessionRecord state = recovery.State;
        return new(state.Boot, state.Epoch, state.Revision, state.Agent, state.Worker, observations);
    }

    private static PolicyEscrowEntryUnqualified[] Inventory(PolicySessionRecovery recovery)
    {
        PolicySessionRecord state = recovery.State;
        PolicyEscrowFlags flags = PolicyEscrowFlags.OriginalNonThreadPidfd | PolicyEscrowFlags.PollingDisabled | PolicyEscrowFlags.CloseOnExec;
        return [new(state.AgentEscrowName, state.Agent, PolicyEscrowRole.Agent, flags),
            new(state.WorkerEscrowName, state.Worker, PolicyEscrowRole.Worker, flags)];
    }
}

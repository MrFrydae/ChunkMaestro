using ChunkMaestro.PrivilegedHelper;
using Xunit;
using static ChunkMaestro.HelperPolicyTests.PolicyWorkerProtocolTests;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicyWorkerArbiterTests
{
    [Fact]
    public void FullParserPathMaintainsOriginalHistoricalReceiptAcrossChecks()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.ReadyUnqualified);
        PolicyWorkerReceipt? receipt = arbiter.AccessUnqualifiedHistory(3);
        Assert.NotNull(receipt);
        Send(arbiter, 4, 4, 2);
        Assert.Null(arbiter.AccessUnqualifiedHistory(4));
        Assert.Same(receipt, Send(arbiter, 5, 5, 2));
        Assert.Equal(PolicyWorkerState.ReadyUnqualified, arbiter.State);
        arbiter.Observe(PolicyWorkerEvent.CancellationRequested, 6);
        Assert.Null(arbiter.AccessUnqualifiedHistory(6));
        Assert.Equal((byte) 42, receipt.CopyRevision()[0]);
    }

    [Fact]
    public void EveryOpcodeHasAnExplicitPhaseBoundary()
    {
        foreach (PolicyWorkerState state in Enum.GetValues<PolicyWorkerState>())
        {
            for (int opcode = 1; opcode <= 7; opcode++)
            {
                PolicyWorkerArbiter arbiter = At(state);
                // Starting fixture is before HELLO; START must wait for it.
                bool accepted = (state == PolicyWorkerState.Starting && opcode == 1) ||
                    (state == PolicyWorkerState.Acquiring && opcode == 3) ||
                    (state == PolicyWorkerState.Checking && opcode == 5) ||
                    (state == PolicyWorkerState.ReadyUnqualified && opcode is 4 or 7);
                bool refusal = state is PolicyWorkerState.Acquiring or PolicyWorkerState.Checking && opcode == 6;
                Send(arbiter, opcode, 10, state == PolicyWorkerState.Checking || (state == PolicyWorkerState.ReadyUnqualified && opcode is 4 or 7) ? 2UL : 1UL);
                if (refusal)
                {
                    Assert.Equal(PolicyWorkerFailure.NativeRefusal, arbiter.FirstFailure);
                }
                else if (accepted)
                {
                    Assert.Equal(opcode == 7 ? PolicyWorkerFailure.Released : PolicyWorkerFailure.None, arbiter.FirstFailure);
                }
                else
                {
                    Assert.NotEqual(PolicyWorkerState.ReadyUnqualified, arbiter.State);
                    Assert.NotEqual(PolicyWorkerFailure.None, arbiter.FirstFailure);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationRequestWinsBeforePublicationEvenWithDelayedCallback(bool checking)
    {
        PolicyWorkerArbiter arbiter = At(checking ? PolicyWorkerState.Checking : PolicyWorkerState.Acquiring);
        arbiter.Observe(PolicyWorkerEvent.CancellationRequested, 10);
        Assert.Null(Send(arbiter, checking ? 5 : 3, 10, checking ? 2UL : 1UL));
        arbiter.Observe(PolicyWorkerEvent.CancellationCallback, 11);
        Assert.Equal(PolicyWorkerFailure.Cancelled, arbiter.FirstFailure);
    }

    [Fact]
    public void AbsoluteDeadlineDoesNotResetOnHandshakeAndEqualityRefuses()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.Starting);
        Send(arbiter, 1, 2998);
        Send(arbiter, 2, 2999);
        Assert.Null(Send(arbiter, 3, 3000));
        Assert.Equal(PolicyWorkerFailure.Deadline, arbiter.FirstFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleLeaseExpiryIsCheckedOnTimeEventsAndAccess(bool access)
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.ReadyUnqualified);
        if (access)
        {
            Assert.Null(arbiter.AccessUnqualifiedHistory(30000));
        }
        else
        {
            arbiter.Observe(PolicyWorkerEvent.TimeAdvanced, 30000);
        }
        Assert.Equal(PolicyWorkerFailure.Lease, arbiter.FirstFailure);
    }

    [Fact]
    public void CheckDeadlineIsCappedByOriginalLeaseAndNeverSlides()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.ReadyUnqualified);
        Send(arbiter, 4, 29999, 2);
        Assert.Null(Send(arbiter, 5, 30000, 2));
        Assert.Equal(PolicyWorkerFailure.Lease, arbiter.FirstFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseCheckSchedulesNeverPublishLate(bool checkFirst)
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.ReadyUnqualified);
        if (checkFirst)
        {
            Send(arbiter, 4, 10, 2);
        }
        arbiter.Observe(PolicyWorkerEvent.ReleaseRequested, 10);
        if (!checkFirst)
        {
            Send(arbiter, 4, 10, 2);
        }
        Assert.Null(Send(arbiter, 5, 11, 2));
        Assert.Equal(PolicyWorkerFailure.Released, arbiter.FirstFailure);
    }

    [Fact]
    public void WireReleaseWhilePendingIsNotAQueuedCommand()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.Checking);
        Send(arbiter, 7, 10, 3);
        Assert.Equal(PolicyWorkerState.Retiring, arbiter.State);
        Assert.Null(Send(arbiter, 5, 11, 2));
    }

    [Fact]
    public void CorrelationRevisionDuplicateAndMalformedResponsesInvalidate()
    {
        for (int mutation = 0; mutation < 5; mutation++)
        {
            PolicyWorkerArbiter arbiter = At(PolicyWorkerState.Checking);
            byte[] bytes = Wire(5, mutation == 0 ? 3UL : mutation == 1 ? ulong.MaxValue : 2UL);
            if (mutation == 2)
            {
                bytes[16] = 8;
            }
            if (mutation == 3)
            {
                bytes[48] = 99;
            }
            if (mutation == 4)
            {
                bytes[12] = 1;
            }
            Assert.Null(arbiter.PublishOrdered(bytes, Direction(5), 10));
            Assert.Null(arbiter.AccessUnqualifiedHistory(10));
        }
        PolicyWorkerArbiter duplicate = At(PolicyWorkerState.ReadyUnqualified);
        Assert.Null(Send(duplicate, 3, 10));
        Assert.Equal(PolicyWorkerState.Retiring, duplicate.State);
    }

    [Fact]
    public void ClockRegressionOverflowAndUnknownEventsFailClosed()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.Acquiring);
        arbiter.Observe(PolicyWorkerEvent.TimeAdvanced, 10);
        Assert.Null(Send(arbiter, 3, 9));
        Assert.Equal(PolicyWorkerFailure.Clock, arbiter.FirstFailure);
        PolicyWorkerArbiter overflow = new();
        overflow.Observe(PolicyWorkerEvent.AcceptSymbolicOwnership, long.MaxValue);
        Assert.Equal(PolicyWorkerFailure.Clock, overflow.FirstFailure);
        PolicyWorkerArbiter unknown = At(PolicyWorkerState.ReadyUnqualified);
        unknown.Observe((PolicyWorkerEvent) 999, 10);
        Assert.Equal(PolicyWorkerState.Retiring, unknown.State);
    }

    [Fact]
    public void RetirementRequiresSeparateModeledFlagsAndNeverAllowsReplacement()
    {
        for (int omitted = 0; omitted < 4; omitted++)
        {
            PolicyWorkerArbiter arbiter = At(PolicyWorkerState.ReadyUnqualified);
            arbiter.Observe(PolicyWorkerEvent.WorkerExited, 10);
            PolicyWorkerEvent[] flags = [PolicyWorkerEvent.LeaderReaped, PolicyWorkerEvent.DomainEmpty, PolicyWorkerEvent.CleanupFinished];
            for (int index = 0; index < flags.Length; index++)
            {
                if (index != omitted)
                {
                    arbiter.Observe(flags[index], 11);
                }
            }
            arbiter.Observe(PolicyWorkerEvent.RetirementObserved, 12);
            Assert.Equal(omitted < 3 ? PolicyWorkerState.Quarantined : PolicyWorkerState.Retired, arbiter.State);
            arbiter.Observe(PolicyWorkerEvent.AcceptSymbolicOwnership, 13);
            Assert.Equal(PolicyWorkerFailure.Crash, arbiter.FirstFailure);
            Assert.Null(arbiter.AccessUnqualifiedHistory(13));
            if (omitted < 3)
            {
                arbiter.Observe(flags[omitted], 14);
                arbiter.Observe(PolicyWorkerEvent.RetirementObserved, 15);
                Assert.Equal(PolicyWorkerState.Retired, arbiter.State);
            }
        }
    }

    [Fact]
    public void FirstFailureSurvivesCleanupFaultsAndQuarantineCutoff()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.Acquiring);
        Send(arbiter, 6, 10);
        arbiter.Observe(PolicyWorkerEvent.CleanupFailed, 11);
        Assert.Equal(PolicyWorkerFailure.NativeRefusal, arbiter.FirstFailure);
        Assert.Equal(PolicyWorkerFailure.Cleanup, arbiter.CleanupFailure);
        Assert.Equal(PolicyWorkerState.Quarantined, arbiter.State);
        PolicyWorkerArbiter timeout = At(PolicyWorkerState.Acquiring);
        timeout.Observe(PolicyWorkerEvent.TimeAdvanced, 3000);
        timeout.Observe(PolicyWorkerEvent.TimeAdvanced, 4000);
        Assert.Equal(PolicyWorkerState.Quarantined, timeout.State);
        Assert.Equal(PolicyWorkerFailure.Deadline, timeout.FirstFailure);
    }

    [Fact]
    public void CheckBudgetEqualityAndRepeatedProgressCannotExtendDeadline()
    {
        PolicyWorkerArbiter arbiter = At(PolicyWorkerState.Checking);
        for (long timestamp = 4; timestamp < 3003; timestamp += 31)
        {
            arbiter.Observe(PolicyWorkerEvent.TimeAdvanced, timestamp);
        }
        Assert.Null(Send(arbiter, 5, 3003, 2));
        Assert.Equal(PolicyWorkerFailure.Deadline, arbiter.FirstFailure);
        PolicyWorkerArbiter justBefore = At(PolicyWorkerState.Acquiring);
        Assert.NotNull(Send(justBefore, 3, 2999));
    }

    [Fact]
    public void CancelBeforeOwnershipDuplicateHelloAndSkippedSequenceCannotStartNewWork()
    {
        PolicyWorkerArbiter cancelled = new();
        cancelled.Observe(PolicyWorkerEvent.CancellationRequested, 0);
        cancelled.Observe(PolicyWorkerEvent.AcceptSymbolicOwnership, 0);
        Assert.Equal(PolicyWorkerFailure.Cancelled, cancelled.FirstFailure);
        Assert.Equal(PolicyWorkerState.Retiring, cancelled.State);
        PolicyWorkerArbiter hello = At(PolicyWorkerState.Starting);
        Send(hello, 1, 0);
        Send(hello, 1, 0);
        Assert.Equal(PolicyWorkerState.Retiring, hello.State);
        PolicyWorkerArbiter skipped = At(PolicyWorkerState.ReadyUnqualified);
        Send(skipped, 4, 10, ulong.MaxValue);
        Assert.Equal(PolicyWorkerFailure.Correlation, skipped.FirstFailure);
        Assert.Null(Send(skipped, 5, 11, ulong.MaxValue));
    }

    internal static PolicyWorkerArbiter At(PolicyWorkerState state)
    {
        PolicyWorkerArbiter arbiter = new();
        if (state == PolicyWorkerState.Idle)
        {
            return arbiter;
        }
        arbiter.Observe(PolicyWorkerEvent.AcceptSymbolicOwnership, 0);
        if (state == PolicyWorkerState.Starting)
        {
            return arbiter;
        }
        Send(arbiter, 1, 0);
        Send(arbiter, 2, 1);
        if (state == PolicyWorkerState.Acquiring)
        {
            return arbiter;
        }
        Assert.NotNull(Send(arbiter, 3, 2));
        if (state == PolicyWorkerState.Checking)
        {
            Send(arbiter, 4, 3, 2);
        }
        if (state is PolicyWorkerState.Retiring or PolicyWorkerState.Quarantined or PolicyWorkerState.Retired)
        {
            arbiter.Observe(PolicyWorkerEvent.ReleaseRequested, 3);
        }
        if (state == PolicyWorkerState.Quarantined)
        {
            arbiter.Observe(PolicyWorkerEvent.RetirementObserved, 4);
        }
        if (state == PolicyWorkerState.Retired)
        {
            arbiter.Observe(PolicyWorkerEvent.LeaderReaped, 4);
            arbiter.Observe(PolicyWorkerEvent.DomainEmpty, 4);
            arbiter.Observe(PolicyWorkerEvent.CleanupFinished, 4);
            arbiter.Observe(PolicyWorkerEvent.RetirementObserved, 4);
        }
        return arbiter;
    }

    internal static PolicyWorkerReceipt? Send(PolicyWorkerArbiter arbiter, int opcode, long timestamp, ulong sequence = 1)
    {
        return arbiter.PublishOrdered(Wire(opcode, sequence), Direction(opcode), timestamp, opcode == 2 ? 2 : 0);
    }
}

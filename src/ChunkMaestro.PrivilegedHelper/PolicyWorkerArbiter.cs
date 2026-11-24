namespace ChunkMaestro.PrivilegedHelper;

internal enum PolicyWorkerState
{
    Idle,
    Starting,
    Acquiring,
    ReadyUnqualified,
    Checking,
    Retiring,
    Quarantined,
    Retired
}

internal enum PolicyWorkerEvent
{
    AcceptSymbolicOwnership,
    TimeAdvanced,
    CancellationRequested,
    CancellationCallback,
    ReleaseRequested,
    WorkerExited,
    CleanupFailed,
    LeaderReaped,
    DomainEmpty,
    CleanupFinished,
    RetirementObserved
}

// All inputs, including time and retirement, are MODEL ONLY. One object represents
// one lifetime; replacement requires a separate external owner (not implemented).
internal sealed class PolicyWorkerArbiter
{
    internal const long OperationBudget = 3000;
    internal const long LeaseBudget = 30000;
    internal const long RetirementBudget = 1000;
    private readonly object gate = new();
    private PolicyWorkerState state;
    private PolicyWorkerFailure firstFailure;
    private PolicyWorkerFailure cleanupFailure;
    private long now;
    private long deadline;
    private long lease;
    private long retirementDeadline;
    private bool hasTime;
    private bool hello;
    private bool cancelled;
    private bool leaderReaped;
    private bool domainEmpty;
    private bool cleanupFinished;
    private byte[]? session;
    private byte[]? originalRevision;
    private ulong sequence;
    private PolicyWorkerReceipt? history;

    internal PolicyWorkerState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    internal PolicyWorkerFailure FirstFailure
    {
        get
        {
            lock (gate)
            {
                return firstFailure;
            }
        }
    }

    internal PolicyWorkerFailure CleanupFailure
    {
        get
        {
            lock (gate)
            {
                return cleanupFailure;
            }
        }
    }

    internal void Observe(PolicyWorkerEvent observation, long timestamp)
    {
        lock (gate)
        {
            if (!Advance(timestamp))
            {
                return;
            }
            switch (observation)
            {
                case PolicyWorkerEvent.AcceptSymbolicOwnership when state == PolicyWorkerState.Idle:
                    state = PolicyWorkerState.Starting;
                    if (timestamp > long.MaxValue - LeaseBudget)
                    {
                        Fail(PolicyWorkerFailure.Clock);
                        return;
                    }
                    deadline = timestamp + OperationBudget;
                    lease = timestamp + LeaseBudget;
                    break;
                case PolicyWorkerEvent.TimeAdvanced:
                case PolicyWorkerEvent.CancellationCallback:
                    break;
                case PolicyWorkerEvent.CancellationRequested:
                    cancelled = true;
                    Fail(PolicyWorkerFailure.Cancelled);
                    break;
                case PolicyWorkerEvent.ReleaseRequested:
                    Fail(PolicyWorkerFailure.Released);
                    break;
                case PolicyWorkerEvent.WorkerExited:
                    Fail(PolicyWorkerFailure.Crash);
                    break;
                case PolicyWorkerEvent.CleanupFailed:
                    RecordCleanupFailure(PolicyWorkerFailure.Cleanup);
                    Fail(PolicyWorkerFailure.Cleanup);
                    state = PolicyWorkerState.Quarantined;
                    break;
                case PolicyWorkerEvent.LeaderReaped when IsRetiring:
                    leaderReaped = true;
                    break;
                case PolicyWorkerEvent.DomainEmpty when IsRetiring:
                    domainEmpty = true;
                    break;
                case PolicyWorkerEvent.CleanupFinished when IsRetiring:
                    cleanupFinished = true;
                    break;
                case PolicyWorkerEvent.RetirementObserved when IsRetiring:
                    state = leaderReaped && domainEmpty && cleanupFinished ? PolicyWorkerState.Retired : PolicyWorkerState.Quarantined;
                    if (state == PolicyWorkerState.Quarantined)
                    {
                        RecordCleanupFailure(PolicyWorkerFailure.Retirement);
                    }
                    break;
                default:
                    Fail(PolicyWorkerFailure.Phase);
                    break;
            }
        }
    }

    // Named atomic publication transition: parse, time/cancellation admission,
    // correlation and state commit share this lock. No CanPublish/check-act API.
    internal PolicyWorkerReceipt? PublishOrdered(ReadOnlySpan<byte> bytes, PolicyWorkerDirection direction,
        long timestamp, int symbolicRights = 0, bool payloadTruncated = false, bool controlTruncated = false, bool unexpectedAncillary = false)
    {
        lock (gate)
        {
            if (!Advance(timestamp) || cancelled || state is PolicyWorkerState.Idle or PolicyWorkerState.Retiring or
                PolicyWorkerState.Quarantined or PolicyWorkerState.Retired)
            {
                Fail(PolicyWorkerFailure.Phase);
                return null;
            }
            PolicyWorkerFailure failure = PolicyWorkerProtocol.Decode(bytes, direction, symbolicRights,
                payloadTruncated, controlTruncated, out PolicyWorkerFrame? frame, unexpectedAncillary);
            if (failure != PolicyWorkerFailure.None || frame is null)
            {
                Fail(failure);
                return null;
            }
            if (frame.Opcode == PolicyWorkerOpcode.Hello && state == PolicyWorkerState.Starting && !hello)
            {
                hello = true;
                return null;
            }
            if (frame.Opcode == PolicyWorkerOpcode.Start && state == PolicyWorkerState.Starting && hello)
            {
                session = frame.Session.ToArray();
                sequence = 1;
                state = PolicyWorkerState.Acquiring;
                return null;
            }
            if (session is null || !frame.Session.SequenceEqual(session))
            {
                Fail(PolicyWorkerFailure.Correlation);
                return null;
            }
            if (frame.Opcode is PolicyWorkerOpcode.Check or PolicyWorkerOpcode.Release && state == PolicyWorkerState.ReadyUnqualified)
            {
                if (sequence == ulong.MaxValue || frame.Sequence != sequence + 1)
                {
                    Fail(PolicyWorkerFailure.Correlation);
                    return null;
                }
                sequence = frame.Sequence;
                if (frame.Opcode == PolicyWorkerOpcode.Release)
                {
                    Fail(PolicyWorkerFailure.Released);
                    return null;
                }
                deadline = Math.Min(lease, timestamp > long.MaxValue - OperationBudget ? long.MaxValue : timestamp + OperationBudget);
                state = PolicyWorkerState.Checking;
                return null;
            }
            if (frame.Sequence != sequence)
            {
                Fail(PolicyWorkerFailure.Correlation);
                return null;
            }
            if (frame.Opcode == PolicyWorkerOpcode.Refused && state is PolicyWorkerState.Acquiring or PolicyWorkerState.Checking)
            {
                Fail(PolicyWorkerFailure.NativeRefusal);
                return null;
            }
            if (frame.Opcode == PolicyWorkerOpcode.Acquired && state == PolicyWorkerState.Acquiring)
            {
                originalRevision = frame.Payload[128..].ToArray();
                history = new PolicyWorkerReceipt(frame.Payload[..128], originalRevision);
                state = PolicyWorkerState.ReadyUnqualified;
                return history;
            }
            if (frame.Opcode == PolicyWorkerOpcode.Checked && state == PolicyWorkerState.Checking)
            {
                if (!frame.Payload.SequenceEqual(originalRevision))
                {
                    Fail(PolicyWorkerFailure.Revision);
                    return null;
                }
                state = PolicyWorkerState.ReadyUnqualified;
                return history;
            }
            Fail(PolicyWorkerFailure.Phase);
            return null;
        }
    }

    internal PolicyWorkerReceipt? AccessUnqualifiedHistory(long timestamp)
    {
        lock (gate)
        {
            return Advance(timestamp) && state == PolicyWorkerState.ReadyUnqualified && !cancelled ? history : null;
        }
    }

    private bool IsRetiring => state is PolicyWorkerState.Retiring or PolicyWorkerState.Quarantined;

    private bool Advance(long timestamp)
    {
        if (timestamp < 0 || (hasTime && timestamp < now))
        {
            Fail(PolicyWorkerFailure.Clock);
            return false;
        }
        now = timestamp;
        hasTime = true;
        if (state is PolicyWorkerState.Starting or PolicyWorkerState.Acquiring or PolicyWorkerState.ReadyUnqualified or PolicyWorkerState.Checking)
        {
            if (timestamp >= lease)
            {
                Fail(PolicyWorkerFailure.Lease);
            }
            else if (state != PolicyWorkerState.ReadyUnqualified && timestamp >= deadline)
            {
                Fail(PolicyWorkerFailure.Deadline);
            }
        }
        if (state == PolicyWorkerState.Retiring && timestamp >= retirementDeadline)
        {
            state = PolicyWorkerState.Quarantined;
            RecordCleanupFailure(PolicyWorkerFailure.Retirement);
        }
        return true;
    }

    private void RecordCleanupFailure(PolicyWorkerFailure failure)
    {
        if (cleanupFailure == PolicyWorkerFailure.None)
        {
            cleanupFailure = failure;
        }
    }

    private void Fail(PolicyWorkerFailure failure)
    {
        if (firstFailure == PolicyWorkerFailure.None)
        {
            firstFailure = failure;
        }
        if (state is not (PolicyWorkerState.Retiring or PolicyWorkerState.Quarantined or PolicyWorkerState.Retired))
        {
            state = PolicyWorkerState.Retiring;
            retirementDeadline = now > long.MaxValue - RetirementBudget ? long.MaxValue : now + RetirementBudget;
        }
    }
}

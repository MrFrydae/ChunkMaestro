using System.Runtime.CompilerServices;

namespace ChunkMaestro.PrivilegedHelper;

// One synchronous serialized lifetime. Ports complete inline; no transferable permits or
// completion callbacks exist. Actual request-to-send atomicity needs separate qualification.
internal sealed class PolicySessionCoordinatorV2Unqualified
{
    private enum Lifetime
    {
        Initial,
        Active,
        Revoked,
        Finishing,
        Drained,
        Poisoned
    }

    private static readonly ConditionalWeakTable<PolicySessionJournalV2Unqualified, object> Owners = new();
    private readonly object order = new();
    private readonly PolicySessionRecoveryV2Unqualified recovery;
    private readonly PolicySessionCoordinatorPortsV2Unqualified ports;
    private Lifetime lifetime;
    private bool entered;
    private bool closeAttempted;
    private long deadline;
    private PolicySessionFault failureContext = PolicySessionFault.Holder;
    private bool cleanupFailed;
    internal Exception? FirstOperationFailure
    {
        get;
        private set;
    }
    internal Exception? CleanupFailure
    {
        get;
        private set;
    }
    private Guid holder;
    private ulong configuration;

    internal PolicySessionCoordinatorV2Unqualified(PolicySessionJournalV2Unqualified journal, Guid boot, byte architecture,
        PolicySessionCoordinatorPortsV2Unqualified ports)
    {
        lock (Owners)
        {
            Require(!Owners.TryGetValue(journal, out _));
            Owners.Add(journal, new object());
        }
        recovery = new(journal, boot, architecture);
        this.ports = ports;
        lifetime = recovery.State.Phase == PolicySessionPhase.Empty ? Lifetime.Initial : Lifetime.Revoked;
    }

    internal PolicySessionRecordV2Unqualified State
    {
        get
        {
            lock (order)
            {
                return recovery.State;
            }
        }
    }

    internal bool DrainedUnqualified
    {
        get
        {
            lock (order)
            {
                return lifetime == Lifetime.Drained;
            }
        }
    }

    internal bool CleanupFailedUnqualified
    {
        get
        {
            lock (order)
            {
                return cleanupFailed;
            }
        }
    }

    internal void ReserveUnqualified(Guid epoch, PolicySessionIdentity agent, long acceptedBoottime, long lease)
    {
        Execute(() =>
        {
            Require(lifetime == Lifetime.Initial && agent.IsComplete && lease > 0);
            deadline = checked(acceptedBoottime + lease);
            Require(acceptedBoottime >= 0 && Port(ports.BoottimeUnqualified) >= acceptedBoottime);
            Port(() => ports.ObserveEmptyOwnerBoundaryUnqualified(recovery.State));
            Require(Port(ports.BoottimeUnqualified) < deadline);
            AcquiredAgentV2Unqualified acquired = Port(() => ports.AcquireOriginalAgentBeforeReserveUnqualified(agent));
            Require(acquired.Agent == agent);
            Require(Port(ports.BoottimeUnqualified) < deadline);
            Port(() => recovery.Reserve(epoch, acquired));
            lifetime = Lifetime.Active;
            Admission();
        });
    }

    internal void StartBindUploadReleaseUnqualified()
    {
        Execute(() =>
        {
            failureContext = PolicySessionFault.Worker;
            Admission();
            Port(() => recovery.ConsumeStartAttempt());
            Admission();
            Port(() => ports.StartOriginalUnqualified(recovery.State));
            Admission();
            BoundOriginalsV2Unqualified worker = Port(() => ports.AcquireOriginalWorkerUnqualified(recovery.State));
            Admission();
            Port(() => recovery.BindOriginalWorker(worker));
            Admission();
            failureContext = PolicySessionFault.Store;
            Port(() => ports.UploadOnceUnqualified(recovery.State, recovery.State.ExpectedNativePair(recovery.State.Boot, recovery.State.NativeMetadata.Architecture)));
            Admission();
            StoreSnapshotV2Unqualified store = ReadStore();
            holder = store.HolderInvocation;
            configuration = store.ConfigurationRevision;
            Admission();
            Port(() => recovery.Arm(new(store.Key, PolicyRetirementObservations.None), store.Entries));
            Admission();
            failureContext = PolicySessionFault.Holder;
            Port(() => ports.PublishOnceUnqualified(recovery.State));
            Admission();
        });
    }

    // Cancellation is ordered by entry to this owner, not by an external token callback.
    internal void RevokeUnqualified(PolicySessionFault fault)
    {
        Execute(() =>
        {
            Require(lifetime is Lifetime.Active or Lifetime.Revoked);
            lifetime = Lifetime.Revoked;
            recovery.Retire(fault);
        });
    }

    internal void FinishRetirementUnqualified()
    {
        Execute(() =>
        {
            Require(lifetime == Lifetime.Revoked);
            failureContext = PolicySessionFault.Cleanup;
            if (recovery.State.Phase != PolicySessionPhase.Retired)
            {
                Require(recovery.State.Phase == PolicySessionPhase.Retiring);
                RetirementEvidenceV2Unqualified proof = Port(() => ports.ObserveRetirementUnqualified(recovery.State));
                Port(() => recovery.ConfirmRetirement(proof));
            }
            StoreSnapshotV2Unqualified remaining = ReadStore();
            Port(() => recovery.ValidateRemainingEscrow(new(remaining.Key, PolicyRetirementObservations.None), remaining.Entries));
            foreach (EscrowEntryV2Unqualified entry in remaining.Entries)
            {
                Port(() => ports.RemoveExactNameUnqualified(recovery.State, entry));
            }
            Port(() => ports.RemovalBarrierUnqualified(recovery.State));
            StoreSnapshotV2Unqualified empty = ReadStore();
            Port(() => recovery.ConfirmEmptyStoreReadback(new(empty.Key, PolicyRetirementObservations.None), empty.Entries));
            lifetime = Lifetime.Finishing;
            Require(!closeAttempted);
            closeAttempted = true;
            Port(() => ports.CloseLocalWitnessesOnceUnqualified());
            lifetime = Lifetime.Drained;
        });
    }

    private StoreSnapshotV2Unqualified ReadStore()
    {
        StoreSnapshotV2Unqualified observation = Port(() => ports.ReadStoreUnqualified(recovery.State));
        StoreSnapshotV2Unqualified store = observation;
        Require(lifetime != Lifetime.Poisoned && !recovery.StorageFailed);
        Require(store.Key == recovery.Key && store.AuthenticatedClaim && store.QualifiedClaim && store.ReportedCount == store.Entries.Count && store.HolderInvocation != Guid.Empty &&
            store.ConfigurationRevision != 0 && (holder == Guid.Empty ||
            (store.HolderInvocation == holder && store.ConfigurationRevision == configuration)));
        if (holder == Guid.Empty)
        {
            holder = store.HolderInvocation;
            configuration = store.ConfigurationRevision;
        }
        return store;
    }

    private void Port(Action effect)
    {
        Require(lifetime != Lifetime.Poisoned && !recovery.StorageFailed);
        effect();
        Require(lifetime != Lifetime.Poisoned && !recovery.StorageFailed);
    }

    private T Port<T>(Func<T> observation)
    {
        Require(lifetime != Lifetime.Poisoned && !recovery.StorageFailed);
        T result = observation();
        Require(lifetime != Lifetime.Poisoned && !recovery.StorageFailed);
        return result;
    }

    private void Admission()
    {
        Require(lifetime == Lifetime.Active);
        if (Port(ports.BoottimeUnqualified) >= deadline)
        {
            lifetime = Lifetime.Revoked;
            failureContext = PolicySessionFault.Deadline;
            throw new InvalidOperationException("Unqualified lease expired.");
        }
    }

    private void Execute(Action operation)
    {
        lock (order)
        {
            // Monitor is reentrant; forbid port reentry explicitly, poison even if a port
            // catches the rejection, and recheck after every port before the next effect.
            if (entered)
            {
                lifetime = Lifetime.Poisoned;
                throw new InvalidOperationException("Unqualified owner reentry refused.");
            }
            Require(lifetime is not (Lifetime.Poisoned or Lifetime.Drained or Lifetime.Finishing));
            entered = true;
            try
            {
                operation();
                Require(lifetime != Lifetime.Poisoned && !recovery.StorageFailed);
            }
            catch (Exception error)
            {
                FirstOperationFailure ??= error;
                cleanupFailed |= failureContext == PolicySessionFault.Cleanup;
                if (failureContext == PolicySessionFault.Cleanup)
                {
                    CleanupFailure ??= error;
                }
                Lifetime failed = lifetime;
                lifetime = Lifetime.Poisoned;
                if (recovery.State.Phase != PolicySessionPhase.Empty && failed != Lifetime.Poisoned && !recovery.StorageFailed)
                {
                    try
                    {
                        recovery.Retire(failureContext);
                        lifetime = Lifetime.Revoked;
                    }
                    catch (Exception cleanup)
                    {
                        CleanupFailure ??= cleanup;
                        cleanupFailed = true;
                    }
                }
                throw;
            }
            finally
            {
                entered = false;
            }
        }
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Unqualified coordinator observation or lifetime refused.");
        }
    }
}

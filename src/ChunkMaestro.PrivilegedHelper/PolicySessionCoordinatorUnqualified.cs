using System.Runtime.CompilerServices;

namespace ChunkMaestro.PrivilegedHelper;

// Private orchestration model only. No runtime adapter, authentication or product conversion.
internal sealed record PolicySessionStoreUnqualified(PolicySessionEvidenceUnqualified Evidence,
    int ReportedCount, IReadOnlyList<PolicyEscrowEntryUnqualified> Entries,
    Guid HolderInvocation, ulong ConfigurationRevision);

internal abstract class PolicySessionCoordinatorPortsUnqualified
{
    internal abstract long BoottimeUnqualified();
    // Must model prior owner death/FD closure AND actual manager/store qualification,
    // even when the journal says EMPTY. This is not an installed proof factory.
    internal abstract void ObserveEmptyOwnerBoundaryUnqualified(PolicySessionRecord state);
    internal abstract void StartOriginalUnqualified(PolicySessionRecord state);
    internal abstract PolicySessionIdentity AcquireOriginalWorkerUnqualified(PolicySessionRecord state);
    internal abstract void UploadOnceUnqualified(PolicySessionRecord state);
    internal abstract PolicySessionStoreUnqualified ReadStoreUnqualified(PolicySessionRecord state);
    internal abstract void PublishOnceUnqualified(PolicySessionRecord state);
    internal abstract PolicySessionEvidenceUnqualified ObserveRetirementUnqualified(PolicySessionRecord state);
    internal abstract void RemoveExactNameUnqualified(PolicySessionRecord state, string name);
    internal abstract void RemovalBarrierUnqualified(PolicySessionRecord state);
    internal abstract void CloseLocalWitnessesOnceUnqualified();
}

// One synchronous serialized lifetime. Ports complete inline; no transferable permits or
// completion callbacks exist. Actual request-to-send atomicity needs separate qualification.
internal sealed class PolicySessionCoordinatorUnqualified
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

    private static readonly ConditionalWeakTable<PolicySessionJournal, object> Owners = new();
    private readonly object order = new();
    private readonly PolicySessionRecovery recovery;
    private readonly PolicySessionCoordinatorPortsUnqualified ports;
    private Lifetime lifetime;
    private bool entered;
    private bool closeAttempted;
    private long deadline;
    private PolicySessionFault failureContext = PolicySessionFault.Holder;
    private bool cleanupFailed;
    private Guid holder;
    private ulong configuration;

    internal PolicySessionCoordinatorUnqualified(PolicySessionJournal journal, Guid boot,
        PolicySessionCoordinatorPortsUnqualified ports)
    {
        lock (Owners)
        {
            Require(!Owners.TryGetValue(journal, out _));
            Owners.Add(journal, new object());
        }
        recovery = new(journal, boot);
        this.ports = ports;
        lifetime = recovery.State.Phase == PolicySessionPhase.Empty ? Lifetime.Initial : Lifetime.Revoked;
    }

    internal PolicySessionRecord State
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
            Port(() => recovery.Reserve(epoch, agent));
            lifetime = Lifetime.Active;
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
            PolicySessionIdentity worker = Port(() => ports.AcquireOriginalWorkerUnqualified(recovery.State));
            Admission();
            Port(() => recovery.BindOriginalWorker(worker));
            Admission();
            failureContext = PolicySessionFault.Store;
            Port(() => ports.UploadOnceUnqualified(recovery.State));
            Admission();
            PolicySessionStoreUnqualified store = ReadStore();
            holder = store.HolderInvocation;
            configuration = store.ConfigurationRevision;
            Admission();
            Port(() => recovery.Arm(store.Evidence, store.Entries));
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
                PolicySessionEvidenceUnqualified proof = Port(() => ports.ObserveRetirementUnqualified(recovery.State));
                Port(() => recovery.ConfirmRetirement(proof));
            }
            PolicySessionStoreUnqualified remaining = ReadStore();
            Port(() => recovery.ValidateRemainingEscrow(remaining.Evidence, remaining.Entries));
            foreach (PolicyEscrowEntryUnqualified entry in remaining.Entries)
            {
                Port(() => ports.RemoveExactNameUnqualified(recovery.State, entry.Name));
            }
            Port(() => ports.RemovalBarrierUnqualified(recovery.State));
            PolicySessionStoreUnqualified empty = ReadStore();
            Port(() => recovery.ConfirmEmptyStoreReadback(empty.Evidence, empty.Entries));
            lifetime = Lifetime.Finishing;
            Require(!closeAttempted);
            closeAttempted = true;
            Port(() => ports.CloseLocalWitnessesOnceUnqualified());
            lifetime = Lifetime.Drained;
        });
    }

    private PolicySessionStoreUnqualified ReadStore()
    {
        PolicySessionStoreUnqualified observation = Port(() => ports.ReadStoreUnqualified(recovery.State));
        // Freeze caller-owned collection before comparison and use. Count and provenance
        // are model measurements, not authentication, descriptor equality or native flags.
        PolicySessionStoreUnqualified store = observation with
        {
            Entries = observation.Entries.ToArray()
        };
        Require(lifetime != Lifetime.Poisoned);
        Require(store.ReportedCount == store.Entries.Count && store.HolderInvocation != Guid.Empty &&
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
        Require(lifetime != Lifetime.Poisoned);
        effect();
        Require(lifetime != Lifetime.Poisoned);
    }

    private T Port<T>(Func<T> observation)
    {
        Require(lifetime != Lifetime.Poisoned);
        T result = observation();
        Require(lifetime != Lifetime.Poisoned);
        return result;
    }

    private void Admission()
    {
        Require(lifetime == Lifetime.Active);
        if (Port(ports.BoottimeUnqualified) >= deadline)
        {
            lifetime = Lifetime.Revoked;
            recovery.Retire(PolicySessionFault.Deadline);
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
                Require(lifetime != Lifetime.Poisoned);
            }
            catch
            {
                cleanupFailed |= failureContext == PolicySessionFault.Cleanup;
                Lifetime failed = lifetime;
                lifetime = Lifetime.Poisoned;
                if (recovery.State.Phase != PolicySessionPhase.Empty && failed != Lifetime.Poisoned)
                {
                    try
                    {
                        recovery.Retire(failureContext);
                        lifetime = Lifetime.Revoked;
                    }
                    catch
                    {
                        // Unknown storage remains occupied; no effect or close retry.
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

using System.Runtime.InteropServices;
using System.Text;

namespace ChunkMaestro.PrivilegedHelper;

internal enum SessionEscrowUploadUnqualified
{
    SendAndBarrierOnly
}

// Private implementation surface only: no journal, service, UI or authorization wiring.
// All callers still owe protected occupancy, original acquisition and manager qualification.
internal sealed class SessionEscrowBridge : IDisposable
{
    private static readonly object HandbackGate = new();
    private static bool handbackConsumed;
    private readonly object gate = new();
    private readonly ISessionEscrowAbi abi;
    private readonly OwnedSessionEscrow handle;
    private readonly bool retireOnly;
    private bool poisoned;
    private bool uploadConsumed;
    private bool closed;

    private SessionEscrowBridge(ISessionEscrowAbi abi, bool retireOnly)
    {
        this.abi = abi;
        this.retireOnly = retireOnly;
        handle = new OwnedSessionEscrow(abi);
    }

    internal static SessionEscrowBridge BindUnqualified(SafeHandle worker, SafeHandle agent, string epoch,
        CancellationToken cancellationToken = default)
    {
        return BindUnqualified(worker, agent, epoch, SessionEscrowNative.Instance, cancellationToken);
    }

    // Explicit ABI seam is for tests only; no existing product caller uses this class.
    internal static SessionEscrowBridge BindUnqualified(SafeHandle worker, SafeHandle agent, string epoch,
        ISessionEscrowAbi abi, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Epoch(epoch);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(agent);
        bool workerHeld = false;
        bool agentHeld = false;
        SessionEscrowBridge owner = new(abi, false);
        nint pointer = 0;
        try
        {
            worker.DangerousAddRef(ref workerHeld);
            agent.DangerousAddRef(ref agentHeld);
            int workerFd = Descriptor(worker);
            int agentFd = Descriptor(agent);
            cancellationToken.ThrowIfCancellationRequested();
            int status;
            try
            {
                status = abi.Bind(workerFd, agentFd, bytes, out pointer);
            }
            finally
            {
                owner.handle.Adopt(pointer);
            }
            owner.Check(status);
            owner.RequirePointer();
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        finally
        {
            if (agentHeld)
            {
                agent.DangerousRelease();
            }
            if (workerHeld)
            {
                worker.DangerousRelease();
            }
        }
    }

    internal static SessionEscrowBridge HandbackUnqualified(string epoch,
        SessionEscrowIdentityUnqualified worker, SessionEscrowIdentityUnqualified agent,
        CancellationToken cancellationToken = default)
    {
        return HandbackUnqualified(epoch, worker, agent, SessionEscrowNative.Instance, cancellationToken);
    }

    internal static SessionEscrowBridge HandbackUnqualified(string epoch,
        SessionEscrowIdentityUnqualified worker, SessionEscrowIdentityUnqualified agent, ISessionEscrowAbi abi,
        CancellationToken cancellationToken = default)
    {
        lock (HandbackGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handbackConsumed)
            {
                throw new InvalidOperationException("EscrowHandbackConsumedUnqualified");
            }
            handbackConsumed = true; // No reset, even after validation/load/native failure.
            byte[] bytes = Epoch(epoch);
            ValidateIdentities([worker, agent]);
            SessionEscrowBridge owner = new(abi, true);
            nint pointer = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                int status;
                try
                {
                    status = abi.Handback(bytes, [worker, agent], out pointer);
                }
                finally
                {
                    owner.handle.Adopt(pointer);
                }
                owner.Check(status);
                owner.RequirePointer();
                return owner;
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }
    }

    internal SessionEscrowIdentityUnqualified[] MetadataUnqualified(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RequireUsable(cancellationToken);
            try
            {
                SessionEscrowIdentityUnqualified[] identities = new SessionEscrowIdentityUnqualified[2];
                Check(abi.Metadata(handle.DangerousGetHandle(), identities));
                ValidateIdentities(identities);
                return identities;
            }
            catch
            {
                poisoned = true;
                throw;
            }
            finally
            {
                GC.KeepAlive(handle);
            }
        }
    }

    internal SessionEscrowObservationUnqualified ObserveUnqualified(uint role,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RequireUsable(cancellationToken);
            try
            {
                if (role > 1)
                {
                    throw new InvalidOperationException("EscrowRoleRangeUnqualified");
                }
                Check(abi.Observe(handle.DangerousGetHandle(), role, out SessionEscrowObservationUnqualified observation));
                if (observation.Readable is < 0 or > 1 || observation.Hup is < 0 or > 1)
                {
                    throw new InvalidOperationException("EscrowObservationAbiUnqualified");
                }
                return observation;
            }
            catch
            {
                poisoned = true;
                throw;
            }
            finally
            {
                GC.KeepAlive(handle);
            }
        }
    }

    internal SessionEscrowUploadUnqualified UploadUnqualified(ulong absoluteBootMicroseconds,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RequireUsable(cancellationToken);
            try
            {
                if (retireOnly || uploadConsumed)
                {
                    throw new InvalidOperationException("EscrowUploadConsumedUnqualified");
                }
                uploadConsumed = true;
                if (absoluteBootMicroseconds == 0)
                {
                    throw new InvalidOperationException("EscrowDeadlineUnqualified");
                }
                // Native CLOCK_BOOTTIME check enforces >now and <=now+30 seconds.
                Check(abi.Upload(handle.DangerousGetHandle(), absoluteBootMicroseconds));
                return SessionEscrowUploadUnqualified.SendAndBarrierOnly;
            }
            catch
            {
                poisoned = true;
                throw;
            }
            finally
            {
                GC.KeepAlive(handle);
            }
        }
    }

    // Blocking drain: waits for an in-flight synchronous operation. Never cancels cleanup.
    internal int CloseUnqualified()
    {
        lock (gate)
        {
            closed = true;
            handle.Dispose();
            int status = handle.CloseStatus;
            if (status is < 0 or > 7)
            {
                throw new InvalidOperationException("EscrowCloseAbiUnqualified");
            }
            return status; // Zero is only local close success, never retirement proof.
        }
    }

    public void Dispose()
    {
        CloseUnqualified();
    }

    private void RequireUsable(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (poisoned)
        {
            throw new InvalidOperationException("EscrowPoisonedUnqualified");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Check(int status)
    {
        if (status != 0)
        {
            poisoned = true;
            throw new InvalidOperationException(status is < 0 or > 7
                ? "EscrowStatusAbiUnqualified" : "EscrowRefusalUnqualified" + status);
        }
    }

    private void RequirePointer()
    {
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException("EscrowNullAbiUnqualified");
        }
    }

    private static int Descriptor(SafeHandle descriptor)
    {
        long value = descriptor.DangerousGetHandle().ToInt64();
        if (descriptor.IsInvalid || value is < 0 or > int.MaxValue)
        {
            throw new ArgumentException("EscrowDescriptorRangeUnqualified");
        }
        return (int) value;
    }

    private static byte[] Epoch(string epoch)
    {
        if (epoch is null || epoch.Length != 32 || epoch.Any(character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("EscrowEpochUnqualified");
        }
        return Encoding.ASCII.GetBytes(epoch + "\0");
    }

    private static void ValidateIdentities(SessionEscrowIdentityUnqualified[] identities)
    {
        foreach (SessionEscrowIdentityUnqualified identity in identities)
        {
            if (identity.Inode == 0 || (identity.Flags & ~0x802u) != 0 || (identity.Flags & 3) != 2)
            {
                throw new InvalidOperationException("EscrowIdentityAbiUnqualified");
            }
        }
        if (identities[0].Device == identities[1].Device && identities[0].Inode == identities[1].Inode)
        {
            throw new InvalidOperationException("EscrowDuplicateIdentityUnqualified");
        }
    }

    private sealed class OwnedSessionEscrow : SafeHandle
    {
        private readonly ISessionEscrowAbi abi;
        internal int CloseStatus
        {
            get;
            private set;
        }

        internal OwnedSessionEscrow(ISessionEscrowAbi abi) : base(0, true)
        {
            this.abi = abi;
        }

        public override bool IsInvalid => handle == 0;

        internal void Adopt(nint pointer)
        {
            SetHandle(pointer);
        }

        protected override bool ReleaseHandle()
        {
            nint pointer = handle;
            handle = 0; // No retry even if native close faults or reports failure.
            try
            {
                CloseStatus = abi.Close(ref pointer);
                if (pointer != 0)
                {
                    CloseStatus = -1;
                }
            }
            catch
            {
                CloseStatus = -1;
            }
            return CloseStatus == 0;
        }
    }
}

using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

// Diagnostic-only sole owner. No production port, raw getter or qualification conversion.
internal sealed class AgentCandidateUnqualified : IDisposable
{
    private readonly object gate = new();
    private readonly IAgentCandidateAbi abi;
    private CandidateHandle? candidate;
    private bool entered;
    private bool attempted;
    private int disposalRequested;
    private AgentCandidateDiagnosticsUnqualified diagnostics = new(0, 0, 0, 0, 0, 0, 0);

    internal AgentCandidateUnqualified(IAgentCandidateAbi abi)
    {
        this.abi = abi;
    }

    internal AgentCandidateDiagnosticsUnqualified Diagnostics
    {
        get
        {
            lock (gate)
            {
                return diagnostics;
            }
        }
    }

    internal bool IsDisposalRequested => Volatile.Read(ref disposalRequested) != 0;

    private void Refuse(bool unknown = false)
    {
        diagnostics = diagnostics with
        {
            FirstStage = diagnostics.FirstCode == 0 ? 13u : diagnostics.FirstStage,
            FirstCode = diagnostics.FirstCode == 0 ? 1u : diagnostics.FirstCode,
            OwnershipFlags = diagnostics.OwnershipFlags | (unknown ? 4u : 0u)
        };
    }

    private void Capture(AgentCandidateDiagnosticsUnqualified value)
    {
        diagnostics = new(diagnostics.FirstCode == 0 ? value.FirstStage : diagnostics.FirstStage,
            diagnostics.FirstCode == 0 ? value.FirstCode : diagnostics.FirstCode,
            diagnostics.FirstCode == 0 ? value.FirstErrno : diagnostics.FirstErrno,
            diagnostics.CleanupCode == 0 ? value.CleanupStage : diagnostics.CleanupStage,
            diagnostics.CleanupCode == 0 ? value.CleanupCode : diagnostics.CleanupCode,
            diagnostics.CleanupCode == 0 ? value.CleanupErrno : diagnostics.CleanupErrno,
            (diagnostics.OwnershipFlags & 6) | value.OwnershipFlags);
    }

    private void Enter()
    {
        if (entered || diagnostics.Refused || Volatile.Read(ref disposalRequested) != 0)
        {
            Refuse();
            throw Refusal();
        }
        entered = true;
    }

    private static InvalidOperationException Refusal()
    {
        return new("AgentCandidateRefusedUnqualified");
    }

    internal void AcquireUnqualified(SafeHandle accepted, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            Enter();
            bool held = false;
            try
            {
                if (attempted || accepted.IsClosed || accepted.IsInvalid)
                {
                    throw Refusal();
                }
                attempted = true;
                cancellationToken.ThrowIfCancellationRequested();
                accepted.DangerousAddRef(ref held);
                long descriptor = accepted.DangerousGetHandle().ToInt64();
                if (descriptor is < 0 or > int.MaxValue)
                {
                    throw Refusal();
                }
                int status;
                nint pointer;
                AgentCandidateDiagnosticsAbi record;
                try
                {
                    status = abi.AcquireCandidate((int) descriptor, out pointer, out record);
                }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
                {
                    Refuse();
                    throw Refusal();
                }
                catch
                {
                    Refuse(true);
                    throw Refusal();
                }
                // Unknown outputs are never installed into SafeHandle or blindly closed.
                try
                {
                    AgentCandidateDiagnosticsUnqualified value = AgentCandidateDiagnosticsUnqualified.Validate(status, record);
                    if (value.Refused ? pointer != 0 || (value.OwnershipFlags & 1) != 0 :
                        pointer == 0 || value.OwnershipFlags != 1)
                    {
                        throw Refusal();
                    }
                    Capture(value);
                    if (!value.Refused)
                    {
                        candidate = new CandidateHandle(this, pointer);
                    }
                }
                catch
                {
                    Refuse(true);
                    throw Refusal();
                }
            }
            catch
            {
                Refuse();
            }
            finally
            {
                if (held)
                {
                    try
                    {
                        accepted.DangerousRelease();
                    }
                    catch
                    {
                        Refuse(true);
                    }
                }
                entered = false;
            }
            if (diagnostics.Refused || cancellationToken.IsCancellationRequested || Volatile.Read(ref disposalRequested) != 0)
            {
                Refuse();
                candidate?.Dispose();
                throw Refusal();
            }
        }
    }

    internal SessionEscrowOriginalMeasurement MeasureUnqualified(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            Enter();
            bool held = false;
            SessionEscrowOriginalMeasurement? snapshot = null;
            try
            {
                if (candidate is null || candidate.IsClosed || candidate.IsInvalid)
                {
                    throw Refusal();
                }
                cancellationToken.ThrowIfCancellationRequested();
                candidate.DangerousAddRef(ref held);
                int status;
                OriginalMeasurementAbi value;
                AgentCandidateDiagnosticsAbi record;
                try
                {
                    status = abi.MeasureCandidate(candidate.DangerousGetHandle(), out value, out record);
                    AgentCandidateDiagnosticsUnqualified captured = AgentCandidateDiagnosticsUnqualified.Validate(status, record);
                    if ((captured.OwnershipFlags & 1) == 0)
                    {
                        throw Refusal();
                    }
                    Capture(captured);
                }
                catch
                {
                    Refuse(true);
                    candidate.SetHandleAsInvalid();
                    throw Refusal();
                }
                byte architecture = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => 1,
                    Architecture.Arm64 => 2,
                    _ => 0
                };
                if (diagnostics.Refused || value.Device == 0 || value.Inode == 0 || value.Flags is not (2 or 0x802) ||
                    architecture == 0 || value.Architecture != architecture || value.Normalization != 1 ||
                    value.Source != 1 || value.Reserved != 0)
                {
                    throw Refusal();
                }
                snapshot = new(value.Device, value.Inode, value.Flags, value.Architecture, value.Normalization, value.Source);
            }
            catch
            {
                Refuse();
            }
            finally
            {
                if (held)
                {
                    candidate!.DangerousRelease();
                }
                entered = false;
            }
            // ReleaseHandle can run on the final pin release; inspect retained record AFTER it.
            if (snapshot is null || diagnostics.Refused || cancellationToken.IsCancellationRequested ||
                Volatile.Read(ref disposalRequested) != 0 || candidate!.IsClosed)
            {
                Refuse();
                throw Refusal();
            }
            return snapshot;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposalRequested, 1);
        // SafeHandle drains a borrowed call before ReleaseHandle. No cancellation-by-close.
        candidate?.Dispose();
        lock (gate)
        {
            if (entered)
            {
                Refuse();
            }
            candidate?.Dispose();
        }
    }

    private sealed class CandidateHandle : SafeHandle
    {
        private readonly AgentCandidateUnqualified owner;

        internal CandidateHandle(AgentCandidateUnqualified owner, nint pointer) : base(0, true)
        {
            this.owner = owner;
            SetHandle(pointer);
        }

        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            lock (owner.gate)
            {
                nint pointer = handle;
                handle = 0;
                try
                {
                    int status = owner.abi.CloseCandidate(ref pointer, out AgentCandidateDiagnosticsAbi record);
                    AgentCandidateDiagnosticsUnqualified value = AgentCandidateDiagnosticsUnqualified.Validate(status, record);
                    if (pointer != 0 || (value.OwnershipFlags & 1) != 0)
                    {
                        throw Refusal();
                    }
                    owner.Capture(value);
                    return !owner.diagnostics.Refused;
                }
                catch
                {
                    owner.Refuse(true);
                    owner.diagnostics = owner.diagnostics with
                    {
                        OwnershipFlags = owner.diagnostics.OwnershipFlags & ~1u
                    };
                    return false;
                }
            }
        }
    }
}

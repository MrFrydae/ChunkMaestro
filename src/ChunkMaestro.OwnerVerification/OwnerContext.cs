using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ChunkMaestro.OwnerVerification;

// Allocation/preflight statuses do not confer authority.
internal enum OwnerProbeStatus
{
    ContextCreated = 0,
    Available = 1,
    Unavailable = 2,
    Unsupported = 3,
    InvalidInput = 4,
    Canceled = 5,
    Failure = 6,
    EvaluationQueued = 7,
    WindowCaptured = 8
}

// Diagnostic native context only. This is neither a session monitor nor CorePermission.
internal sealed class OwnerContext : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ulong generation;
    private readonly ulong request;
    private readonly byte[] nonce;
    private EvaluationCompletion? evaluation;
    private bool canceled;
    private bool disposed;

    private OwnerContext(ulong request, ulong generation, byte[] nonce)
    {
        this.request = request;
        this.generation = generation;
        this.nonce = (byte[]) nonce.Clone();
    }

    internal static OwnerProbeStatus Create(Guid session, ulong generation, out OwnerContext? context)
    {
        context = null;
        if (session == Guid.Empty || generation == 0)
        {
            return OwnerProbeStatus.InvalidInput;
        }
        if (!OperatingSystem.IsMacOS())
        {
            return OwnerProbeStatus.Unsupported;
        }
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] sessionBytes = session.ToByteArray();
        try
        {
            OwnerProbeStatus result = Native.cm_owner_create(2, nonce, (nuint) nonce.Length,
                sessionBytes, (nuint) sessionBytes.Length, generation, out ulong handle);
            if (result == OwnerProbeStatus.ContextCreated && handle != 0)
            {
                context = new OwnerContext(handle, generation, nonce);
                return result;
            }
            return result == OwnerProbeStatus.ContextCreated ? OwnerProbeStatus.Failure : result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    internal OwnerProbeStatus Probe()
    {
        lock (gate)
        {
            if (disposed || canceled)
            {
                return OwnerProbeStatus.Canceled;
            }
        }
        return Native.cm_owner_probe(request);
    }

    internal OwnerProbeStatus Prepare(nint window, ulong currentGeneration)
    {
        lock (gate)
        {
            if (disposed || canceled)
            {
                return OwnerProbeStatus.Canceled;
            }
        }
        return window == 0 || currentGeneration != generation ? OwnerProbeStatus.InvalidInput :
            Native.cm_owner_prepare(request, (nuint) window, currentGeneration);
    }

    internal OwnerProbeStatus CaptureWindowExplicitly(nint window, ulong currentGeneration)
    {
        lock (gate)
        {
            if (disposed || canceled)
            {
                return OwnerProbeStatus.Canceled;
            }
        }
        return Native.cm_owner_bind_window_explicit(request, (nuint) window, currentGeneration);
    }

    // No production caller. This explicit internal entry MAY prompt with a valid owned
    // window; default Create/Probe/Prepare cannot reach it. Never a CorePermission.
    internal Task<RawPolicyObservation> EvaluateExplicitlyAsync(nint window, ulong currentGeneration)
    {
        EvaluationCompletion pending;
        lock (gate)
        {
            if (evaluation is not null)
            {
                return evaluation.Task;
            }
            if (disposed || canceled || window == 0 || currentGeneration != generation)
            {
                return Task.FromResult(RawPolicyObservation.Unknown);
            }
            pending = new EvaluationCompletion(request, generation, (nuint) window, nonce);
            evaluation = pending;
        }
        try
        {
            OwnerProbeStatus status = Native.cm_owner_evaluate_explicit(request, (nuint) window,
                generation, EvaluationCompletion.Reply, pending.Cookie);
            if (status != OwnerProbeStatus.EvaluationQueued)
            {
                pending.RefuseBeforeTransfer();
            }
        }
        catch
        {
            // P/Invoke entry failure transfers nothing. Native catches ObjC exceptions.
            pending.RefuseBeforeTransfer();
            throw;
        }
        return pending.Task;
    }

    internal OwnerProbeStatus Cancel()
    {
        lock (gate)
        {
            canceled = true;
            evaluation?.Revoke();
            if (disposed)
            {
                return OwnerProbeStatus.Canceled;
            }
        }
        return Native.cm_owner_cancel(request);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            canceled = true;
            disposed = true;
            evaluation?.Revoke();
        }
        Native.cm_owner_release(request);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        GC.SuppressFinalize(this);
        Task? drain;
        lock (gate)
        {
            drain = evaluation?.Task;
        }
        if (drain is not null)
        {
            await drain.ConfigureAwait(false);
        }
    }

    ~OwnerContext()
    {
        Dispose();
    }
}

internal static class Native
{
    private const string Library = "chunkmaestro_owner";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReplyCallback(ulong request, ulong generation, nuint window,
        nint nonce, nuint nonceSize, int result, uint phase, nint cookie);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern OwnerProbeStatus cm_owner_evaluate_explicit(ulong request, nuint window,
        ulong generation, ReplyCallback reply, nint cookie);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern OwnerProbeStatus cm_owner_bind_window_explicit(ulong request, nuint window, ulong generation);

    [DllImport(Library)]
    internal static extern uint cm_owner_abi();

    [DllImport(Library)]
    internal static extern OwnerProbeStatus cm_owner_create(uint abi, byte[] nonce, nuint nonceSize,
        byte[] session, nuint sessionSize, ulong generation, out ulong request);

    [DllImport(Library)]
    internal static extern OwnerProbeStatus cm_owner_probe(ulong request);

    [DllImport(Library)]
    internal static extern OwnerProbeStatus cm_owner_cancel(ulong request);

    [DllImport(Library)]
    internal static extern OwnerProbeStatus cm_owner_prepare(ulong request, nuint window, ulong generation);

    [DllImport(Library)]
    internal static extern OwnerProbeStatus cm_owner_release(ulong request);
}

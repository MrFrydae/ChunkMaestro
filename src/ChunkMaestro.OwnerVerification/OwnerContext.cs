using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ChunkMaestro.OwnerVerification;

// Intentionally no NativeVerified status: evaluation is blocked on qualified monitoring.
internal enum OwnerProbeStatus
{
    ContextCreated = 0,
    Available = 1,
    Unavailable = 2,
    Unsupported = 3,
    InvalidInput = 4,
    Canceled = 5,
    Failure = 6
}

// Diagnostic native context only. This is neither a session monitor nor CorePermission.
internal sealed class OwnerContext : IDisposable
{
    private readonly object gate = new();
    private readonly ulong generation;
    private ulong request;
    private bool canceled;
    private bool disposed;

    private OwnerContext(ulong request, ulong generation)
    {
        this.request = request;
        this.generation = generation;
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
            OwnerProbeStatus result = Native.cm_owner_create(1, nonce, (nuint) nonce.Length,
                sessionBytes, (nuint) sessionBytes.Length, generation, out ulong handle);
            if (result == OwnerProbeStatus.ContextCreated && handle != 0)
            {
                context = new OwnerContext(handle, generation);
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
            return Native.cm_owner_probe(request);
        }
    }

    internal OwnerProbeStatus Prepare(nint window, ulong currentGeneration)
    {
        lock (gate)
        {
            if (disposed || canceled)
            {
                return OwnerProbeStatus.Canceled;
            }
            if (window == 0 || currentGeneration != generation)
            {
                return OwnerProbeStatus.InvalidInput;
            }
            return Native.cm_owner_prepare(request, (nuint) window, currentGeneration);
        }
    }

    internal OwnerProbeStatus Cancel()
    {
        lock (gate)
        {
            canceled = true;
            return disposed ? OwnerProbeStatus.Canceled : Native.cm_owner_cancel(request);
        }
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
            Native.cm_owner_release(request);
            request = 0;
        }
        GC.SuppressFinalize(this);
    }

    ~OwnerContext()
    {
        Dispose();
    }
}

internal static class Native
{
    private const string Library = "chunkmaestro_owner";

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

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ChunkMaestro.OwnerVerification;

internal enum RawPolicyObservation
{
    PolicySatisfied = 0,
    Denied = 1,
    Canceled = 2,
    Unavailable = 3,
    InvalidWindow = 4,
    Unknown = 5
}

// Rooted independently from OwnerContext, so finalization can revoke/release ingress.
// The native active record owns Cookie until one acknowledged terminal delivery.
internal sealed class EvaluationCompletion
{
    private readonly object gate = new();
    private readonly ulong request;
    private readonly ulong generation;
    private readonly nuint window;
    private readonly byte[] nonce;
    private readonly TaskCompletionSource<RawPolicyObservation> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle root;
    private bool revoked;
    private bool drained;
    private RawPolicyObservation? terminal;

    internal static readonly Native.ReplyCallback Reply = Receive;

    internal EvaluationCompletion(ulong request, ulong generation, nuint window, byte[] nonce)
    {
        this.request = request;
        this.generation = generation;
        this.window = window;
        this.nonce = (byte[]) nonce.Clone();
        root = GCHandle.Alloc(this);
        Cookie = GCHandle.ToIntPtr(root);
    }

    internal nint Cookie
    {
        get;
    }
    internal Task<RawPolicyObservation> Task => completion.Task;
    internal bool IsDrained
    {
        get
        {
            lock (gate)
            {
                return drained;
            }
        }
    }

    internal void Revoke()
    {
        lock (gate)
        {
            revoked = true;
        }
    }

    internal void RefuseBeforeTransfer()
    {
        Complete(RawPolicyObservation.Unknown);
    }

    private void Complete(RawPolicyObservation result)
    {
        lock (gate)
        {
            if (drained)
            {
                return;
            }
            drained = true;
            result = revoked ? RawPolicyObservation.Canceled : result;
            root.Free();
            CryptographicOperations.ZeroMemory(nonce);
        }
        completion.TrySetResult(result);
    }

    private static int Receive(ulong request, ulong generation, nuint window,
        nint nonce, nuint nonceSize, int result, uint phase, nint cookie)
    {
        try
        {
            if (GCHandle.FromIntPtr(cookie).Target is not EvaluationCompletion pending)
            {
                return 0;
            }
            if (pending.request != request || pending.generation != generation ||
                pending.window != window || nonce == 0 || nonceSize != 32)
            {
                return 0;
            }
            byte[] observed = new byte[32];
            Marshal.Copy(nonce, observed, 0, observed.Length);
            if (!CryptographicOperations.FixedTimeEquals(observed, pending.nonce))
            {
                return 0;
            }
            RawPolicyObservation mapped = Enum.IsDefined(typeof(RawPolicyObservation), result) ?
                (RawPolicyObservation) result : RawPolicyObservation.Unknown;
            lock (pending.gate)
            {
                if (phase == 0 && pending.terminal is null)
                {
                    pending.terminal = mapped;
                    return 1;
                }
                if (phase != 1 || pending.terminal != mapped)
                {
                    return 0;
                }
            }
            pending.Complete(mapped);
            return 1;
        }
        catch
        {
            // Failed delivery never authorizes freeing native ownership or a retry.
            return 0;
        }
    }
}

using System.Diagnostics;

namespace ChunkMaestro.Persistence;

internal enum OwnerEffect
{
    Begin,
    Create,
    Use,
    Ready
}

internal enum OwnerEffectCompletion
{
    Completed,
    Failed,
    Unknown
}

// A private ordering proof only, NOT connected to production effects. All scope fields are
// immutable exact request inputs. No callback, provider, wait or effect executes under sync.
internal readonly record struct OwnerRequestScope(Guid Fleet, Guid Controller, Guid Attempt, Guid Credential, long Revision)
{
    internal bool IsValid => Fleet != Guid.Empty && Controller != Guid.Empty && Attempt != Guid.Empty && Credential != Guid.Empty && Revision > 0;
}

internal sealed class OwnerAuthorizationRequest
{
    private readonly object sync = new();
    private readonly OwnerRequestScope scope;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly long deadline;
    private bool verified;
    private bool terminal;
    private int next;
    private Admission? inFlight;

    // This internal handle is only a reservation for one effect, not public authority.
    // Reference identity binds completion to the winning admission and request.
    internal sealed class Admission(OwnerEffect effect)
    {
        internal OwnerEffect Effect
        {
            get;
        } = effect;
    }

    // Fixed initialization plan: Begin -> Create -> Use -> Ready. Only the internal
    // winning worker receives its completion handle; no public permission token exists.
    internal OwnerAuthorizationRequest(OwnerRequestScope scope, long deadline)
    {
        this.scope = scope;
        this.deadline = deadline;
        terminal = !scope.IsValid;
    }

    internal bool Verified(OwnerRequestScope observed)
    {
        lock (sync)
        {
            if (!Eligible(observed) || verified)
            {
                terminal = true;
                return false;
            }
            verified = true;
            return true;
        }
    }

    // Only the running exact worker may call this after all external gates/rereads.
    internal Admission? Admit(OwnerEffect effect, OwnerRequestScope observed)
    {
        lock (sync)
        {
            if (!Eligible(observed) || !verified || inFlight != null || (int) effect != next || !Enum.IsDefined(effect))
            {
                terminal = true;
                return null;
            }
            inFlight = new Admission(effect);
            return inFlight;
        }
    }

    // Revoke cannot recall the exact admitted effect. Completion can drain it but never revive
    // revoked authority. Failed/unknown/invalid or duplicate completion terminalizes the request.
    internal bool Finish(Admission admission, OwnerEffectCompletion completion)
    {
        lock (sync)
        {
            if (inFlight is null || !ReferenceEquals(inFlight, admission))
            {
                terminal = true;
                return false;
            }
            inFlight = null;
            if (completion != OwnerEffectCompletion.Completed)
            {
                terminal = true;
                return false;
            }
            next++;
            terminal |= next == 4;
            return true;
        }
    }

    internal void Revoke()
    {
        lock (sync)
        {
            terminal = true;
        }
    }

    private bool Eligible(OwnerRequestScope observed)
    {
        long now = Stopwatch.GetTimestamp();
        return !terminal && observed == scope && observed.IsValid && now >= started && now < deadline;
    }
}

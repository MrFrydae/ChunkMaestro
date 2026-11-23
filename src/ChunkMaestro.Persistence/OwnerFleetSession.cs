using ChunkMaestro.Application;
using ChunkMaestro.Domain;

namespace ChunkMaestro.Persistence;

/// <summary>Denial-only production boundary borrowing an already retained store. The caller owns
/// that store and must keep it alive until session disposal and all calls drain. This session
/// never opens a store or constructs credentials. Dispose closes ingress, not the borrowed store.</summary>
public sealed class OwnerFleetSession : IOwnerIdentitySession
{
    private readonly LocalFleetStore store;
    private readonly object sync = new();
    private OwnerIdentityObservation? current;
    private bool closed;
    private int readers;
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Borrow a retained local inventory session. Opening it is not OS authorization.</summary>
    public OwnerFleetSession(LocalFleetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <inheritdoc/>
    public async Task<OwnerIdentityObservation> ObserveAsync(FleetId fleet, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (closed || cancellationToken.IsCancellationRequested)
            {
                return RefusedObservation(fleet);
            }
            readers++;
        }
        try
        {
            return await ObserveCoreAsync(fleet, cancellationToken);
        }
        finally
        {
            bool complete;
            lock (sync)
            {
                readers--;
                complete = closed && readers == 0;
            }
            if (complete)
            {
                drained.TrySetResult();
            }
        }
    }

    private async Task<OwnerIdentityObservation> ObserveCoreAsync(FleetId fleet, CancellationToken cancellationToken)
    {
        FleetInventory inventory;
        try
        {
            inventory = await store.ReadAsync(cancellationToken);
        }
        catch (Exception error) when (error is FleetStorageException or ObjectDisposedException or OperationCanceledException)
        {
            return RefusedObservation(fleet);
        }
        lock (sync)
        {
            if (closed || cancellationToken.IsCancellationRequested)
            {
                return RefusedObservation(fleet);
            }
            bool exists = inventory.Fleets.Any(item => item.Id == fleet);
            current = new(fleet, Guid.NewGuid(), exists, exists ? OwnerIdentityResult.Unsupported : OwnerIdentityResult.Refused);
            return current;
        }
    }

    /// <inheritdoc/>
    public Task<OwnerIdentityResult> InitializeAsync(OwnerIdentityObservation observation, CancellationToken cancellationToken = default)
    {
        return DenyAsync(observation, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<OwnerIdentityResult> RecoverExactAsync(OwnerIdentityObservation observation, CancellationToken cancellationToken = default)
    {
        return DenyAsync(observation, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<OwnerIdentityResult> VerifyExistingAsync(OwnerIdentityObservation observation, CancellationToken cancellationToken = default)
    {
        return DenyAsync(observation, cancellationToken);
    }

    private Task<OwnerIdentityResult> DenyAsync(OwnerIdentityObservation observation, CancellationToken token)
    {
        lock (sync)
        {
            return Task.FromResult(closed || token.IsCancellationRequested || !ReferenceEquals(current, observation) ||
                observation is null || !observation.FleetExists ? OwnerIdentityResult.Refused : OwnerIdentityResult.Unsupported);
        }
    }

    private static OwnerIdentityObservation RefusedObservation(FleetId fleet)
    {
        return new(fleet, Guid.Empty, false, OwnerIdentityResult.Refused);
    }

    /// <summary>Irreversibly close ingress. Wait for in-flight inventory reads before returning;
    /// they can only return refused after close. The borrowed store remains caller-owned.</summary>
    public ValueTask DisposeAsync()
    {
        bool complete;
        lock (sync)
        {
            closed = true;
            current = null;
            complete = readers == 0;
        }
        if (complete)
        {
            drained.TrySetResult();
        }
        return new ValueTask(drained.Task);
    }
}

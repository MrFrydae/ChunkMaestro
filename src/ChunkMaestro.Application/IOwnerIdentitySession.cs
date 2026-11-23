using ChunkMaestro.Domain;

namespace ChunkMaestro.Application;

/// <summary>Whole owner use cases. Observations carry no authorization or possession proof.</summary>
public interface IOwnerIdentitySession : IAsyncDisposable
{
    /// <summary>Observe fleet membership and unavailable owner capability without prompting.</summary>
    Task<OwnerIdentityObservation> ObserveAsync(FleetId fleet, CancellationToken cancellationToken = default);
    /// <summary>Explicit new-fleet initialization intent; currently unavailable.</summary>
    Task<OwnerIdentityResult> InitializeAsync(OwnerIdentityObservation observation, CancellationToken cancellationToken = default);
    /// <summary>Explicit recovery intent; never creates a replacement identity.</summary>
    Task<OwnerIdentityResult> RecoverExactAsync(OwnerIdentityObservation observation, CancellationToken cancellationToken = default);
    /// <summary>Explicit existing-identity verification intent; inventory is not proof.</summary>
    Task<OwnerIdentityResult> VerifyExistingAsync(OwnerIdentityObservation observation, CancellationToken cancellationToken = default);
}

/// <summary>No successful outcome is implemented in this boundary.</summary>
public enum OwnerIdentityResult
{
    /// <summary>No qualified OS owner verification is composed. No identity effect started.</summary>
    Unsupported,
    /// <summary>Invalid, stale, canceled, closed or unavailable observation. No identity effect started.</summary>
    Refused
}

/// <summary>Nonsecret inventory only. Revision is a freshness hint, never an authority credential.
/// Identity metadata, owner binding and native possession have not been assessed.</summary>
public sealed record OwnerIdentityObservation(FleetId Fleet, Guid Revision, bool FleetExists, OwnerIdentityResult Capability);

namespace ChunkMaestro.Domain;

/// <summary>Persistent fleet identity. Empty GUIDs are rejected by registration.</summary>
public readonly record struct FleetId(Guid Value);
/// <summary>Persistent manually registered host identity.</summary>
public readonly record struct HostId(Guid Value);
/// <summary>Persistent manually registered Minecraft service identity.</summary>
public readonly record struct InstanceId(Guid Value);
/// <summary>Edition determines transport; Bedrock metadata does not imply native BDS management.</summary>
public enum EndpointEdition
{
    /// <summary>Java edition status uses TCP.</summary>
    Java,
    /// <summary>Bedrock edition status uses UDP.</summary>
    Bedrock
}
/// <summary>Wire transport, distinct even when numerical ports match.</summary>
public enum EndpointTransport
{
    /// <summary>TCP transport.</summary>
    Tcp,
    /// <summary>UDP transport.</summary>
    Udp
}
/// <summary>Cached observation state; never inferred from registration.</summary>
public enum ObservationState
{
    /// <summary>No usable evidence.</summary>
    Unknown,
    /// <summary>Current evidence from a verified observer.</summary>
    Fresh,
    /// <summary>Cached evidence requiring refresh.</summary>
    Stale,
    /// <summary>Explicit disconnected state; not inferred from registration.</summary>
    Offline
}
/// <summary>Evidence timestamp and source, separate from registration and cache read time.</summary>
public sealed record Observation(ObservationState State, DateTimeOffset? ObservedAt,
    DateTimeOffset? LastSuccessfulContactAt, string? Source)
{
    /// <summary>No observation has been collected.</summary>
    public static Observation Unknown
    {
        get;
    } = new(ObservationState.Unknown, null, null, null);
    /// <summary>Cached evidence cannot establish current reachability after controller restart.</summary>
    public Observation AsCached() => State == ObservationState.Fresh ? this with
    {
        State = ObservationState.Stale
    }
: this;
}
/// <summary>Non-secret user-entered endpoint metadata. No probing is performed.</summary>
public sealed record Endpoint(EndpointEdition Edition, string Address, int Port)
{
    /// <summary>No endpoint probe is implemented in this slice; health evidence is unavailable.</summary>
    public Observation Health
    {
        get;
    } = Observation.Unknown;

    /// <summary>Java uses TCP, Bedrock uses UDP.</summary>
    public EndpointTransport Transport => Edition == EndpointEdition.Java ? EndpointTransport.Tcp : EndpointTransport.Udp;
}
/// <summary>Local grouping identity and non-secret display name.</summary>
public sealed record Fleet(FleetId Id, string Name);
/// <summary>Manual target metadata, never enrollment or proof of ownership.</summary>
public sealed record Host(HostId Id, FleetId FleetId, string Name, string Address, DateTimeOffset RegisteredAt, Observation Observation);
/// <summary>Manual service metadata and endpoints; conveys no remote execution capability.</summary>
public sealed record Instance(InstanceId Id, HostId HostId, string Name, string ServiceName,
    DateTimeOffset RegisteredAt, Observation Observation, IReadOnlyList<Endpoint> Endpoints);
/// <summary>A local cache read; RetrievedAt is not a host contact timestamp.</summary>
public sealed record FleetInventory(IReadOnlyList<Fleet> Fleets, IReadOnlyList<Host> Hosts,
    IReadOnlyList<Instance> Instances, DateTimeOffset RetrievedAt);

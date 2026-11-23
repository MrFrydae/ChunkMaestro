namespace ChunkMaestro.Contracts;

public enum InspectOutcome
{
    Observed,
    Denied,
    Unavailable,
    Unknown
}

public enum InspectReason
{
    None,
    PolicyDenied,
    TargetDrift,
    NotLoaded,
    UnsupportedState,
    ConcurrentChange,
    DependencyUnavailable,
    PermissionDenied,
    Timeout,
    Busy
}

public enum ServiceState
{
    Unknown,
    Active,
    Inactive,
    Failed,
    Transitional
}

public enum ManagerPresence
{
    Unresolved,
    Present,
    NotFound
}

public sealed class InspectResponse
{
    public InspectResponse(InspectRequest request, string helperEpoch, InspectOutcome outcome, InspectReason reason,
        DateTimeOffset observedAt, int durationMs, ServiceState serviceState, ManagerPresence managerPresence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidation.Identifier(helperEpoch);
        ContractValidation.Timestamp(observedAt);
        if (durationMs is < 0 or > 3000 || !IsCombinationValid(outcome, reason, serviceState, managerPresence))
        {
            throw new ArgumentException("Invalid observation combination.");
        }
        Request = request;
        HelperEpoch = helperEpoch;
        Outcome = outcome;
        Reason = reason;
        ObservedAt = observedAt;
        DurationMs = durationMs;
        ServiceState = serviceState;
        ManagerPresence = managerPresence;
    }

    public InspectRequest Request
    {
        get;
    }
    public string HelperEpoch
    {
        get;
    }
    public InspectOutcome Outcome
    {
        get;
    }
    public InspectReason Reason
    {
        get;
    }
    public DateTimeOffset ObservedAt
    {
        get;
    }
    public int DurationMs
    {
        get;
    }
    public ServiceState ServiceState
    {
        get;
    }
    public ManagerPresence ManagerPresence
    {
        get;
    }
    public string Source
    {
        get;
    } = "debian-systemd-system-bus";

    internal static bool IsCombinationValid(InspectOutcome outcome, InspectReason reason, ServiceState state, ManagerPresence presence)
    {
        if (!Enum.IsDefined(presence))
        {
            return false;
        }
        if (outcome == InspectOutcome.Observed)
        {
            return reason == InspectReason.None && presence == ManagerPresence.Present &&
                state is ServiceState.Active or ServiceState.Inactive or ServiceState.Failed or ServiceState.Transitional;
        }
        if (state != ServiceState.Unknown)
        {
            return false;
        }
        return outcome switch
        {
            InspectOutcome.Denied => reason == InspectReason.PolicyDenied && presence == ManagerPresence.Unresolved,
            InspectOutcome.Unknown => reason switch
            {
                InspectReason.TargetDrift => true,
                InspectReason.NotLoaded => presence == ManagerPresence.Present,
                InspectReason.UnsupportedState => presence == ManagerPresence.Present,
                InspectReason.ConcurrentChange => presence == ManagerPresence.Unresolved,
                _ => false
            },
            InspectOutcome.Unavailable => presence == ManagerPresence.Unresolved &&
                reason is InspectReason.DependencyUnavailable or InspectReason.PermissionDenied or InspectReason.Timeout or InspectReason.Busy,
            _ => false
        };
    }
}

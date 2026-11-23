namespace ChunkMaestro.Contracts;

public sealed class InspectRequest
{
    public InspectRequest(string requestId, string instanceId, string policyRevision)
    {
        ContractValidation.Identifier(requestId);
        ContractValidation.Identifier(instanceId);
        ContractValidation.Revision(policyRevision);
        RequestId = requestId;
        InstanceId = instanceId;
        PolicyRevision = policyRevision;
    }

    public int Version
    {
        get;
    } = 1;
    public string Action
    {
        get;
    } = "inspect";
    public string RequestId
    {
        get;
    }
    public string InstanceId
    {
        get;
    }
    public string PolicyRevision
    {
        get;
    }
}

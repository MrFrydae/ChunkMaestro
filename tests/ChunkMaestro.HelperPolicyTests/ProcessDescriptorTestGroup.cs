using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

// The retained-policy oracle snapshots the entire process FD table. Other test
// collections must not create or close unrelated handles during that observation.
// Parallel.For inside the ownership test remains concurrent.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessDescriptorTestGroup
{
    public const string Name = "Process descriptor ownership";
}

namespace ChunkMaestro.PrivilegedHelper;

// Historical, unqualified measurement only. Never a live access or policy grant.
internal sealed class PolicyWorkerReceipt
{
    private readonly byte[] facts;
    private readonly byte[] revision;

    internal PolicyWorkerReceipt(ReadOnlySpan<byte> facts, ReadOnlySpan<byte> revision)
    {
        this.facts = facts.ToArray();
        this.revision = revision.ToArray();
    }

    internal byte[] CopyFacts()
    {
        return (byte[]) facts.Clone();
    }

    internal byte[] CopyRevision()
    {
        return (byte[]) revision.Clone();
    }
}

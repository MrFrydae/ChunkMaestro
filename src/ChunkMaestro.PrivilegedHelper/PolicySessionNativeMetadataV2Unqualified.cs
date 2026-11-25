namespace ChunkMaestro.PrivilegedHelper;

// Explicit serialized claims, never a native measurement or provenance token.
internal readonly record struct PolicySessionNativeMetadataV2Unqualified(byte Presence, byte Architecture,
    byte Normalization, byte Source, uint AgentFlags, uint WorkerFlags)
{
    internal void Validate()
    {
        if (Presence == 0 && Architecture == 0 && Normalization == 0 && Source == 0 && AgentFlags == 0 && WorkerFlags == 0)
        {
            return;
        }
        if (Presence is not (1 or 3) || Architecture is not (1 or 2) || Normalization != 1 || Source != 1 ||
            AgentFlags is not (2 or 0x802) || (Presence == 1 ? WorkerFlags != 0 : WorkerFlags is not (2 or 0x802)))
        {
            throw new InvalidDataException("Invalid session journal metadata.");
        }
    }
}

internal readonly record struct PolicySessionNativeTupleV2Unqualified(ulong Device, ulong Inode, uint Flags);

// Named immutable fields avoid storage-order/enum-cast mapping and shared arrays.
internal readonly record struct PolicySessionExpectedNativePairV2Unqualified(
    PolicySessionNativeTupleV2Unqualified Worker, PolicySessionNativeTupleV2Unqualified Agent)
{
    internal PolicySessionNativeTupleV2Unqualified AtNativeIndex(int index)
    {
        return index switch
        {
            0 => Worker,
            1 => Agent,
            _ => throw new InvalidDataException("Invalid native role index.")
        };
    }

    internal void RequireMatch(PolicySessionNativeTupleV2Unqualified worker, PolicySessionNativeTupleV2Unqualified agent)
    {
        if (Worker != worker || Agent != agent)
        {
            throw new InvalidDataException("Session journal comparison refused.");
        }
    }
}

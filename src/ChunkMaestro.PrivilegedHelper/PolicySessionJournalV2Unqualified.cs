namespace ChunkMaestro.PrivilegedHelper;

// Private storage model: cooperative exclusion, not hostile path/installation authentication.
// No recovery actions, runtime factory, native admission or owner-finishing protocol.
internal sealed class PolicySessionJournalV2Unqualified : IDisposable
{
    private readonly object order = new();
    private readonly string directory;
    private readonly string path;
    private readonly FileStream exclusion;
    private readonly IPolicyJournalDurability durability;
    private readonly Action<PolicyJournalBarrier>? barrier;
    private bool entered;
    private bool uncertain;
    private bool disposed;

    internal PolicySessionJournalV2Unqualified(string directory, IPolicyJournalDurability? durability = null,
        Action<PolicyJournalBarrier>? barrier = null)
    {
        this.directory = Path.GetFullPath(directory);
        path = Path.Combine(this.directory, "session.journal");
        this.durability = durability ?? new LinuxPolicyJournalDurability();
        this.barrier = barrier;
        exclusion = new FileStream(Path.Combine(this.directory, "session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal PolicySessionRecordV2Unqualified Read()
    {
        return Execute(ReadCore);
    }

    private PolicySessionRecordV2Unqualified ReadCore()
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != PolicySessionRecordV2Unqualified.Length)
        {
            throw new InvalidDataException("Session journal size refused.");
        }
        byte[] bytes = new byte[PolicySessionRecordV2Unqualified.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || input.Length != bytes.Length)
        {
            throw new InvalidDataException("Session journal changed during read.");
        }
        // Every v1 (including Empty) and unknown version is refused without conversion.
        return PolicySessionRecordV2Unqualified.Decode(bytes);
    }

    internal void InitializeNewInstallation(PolicySessionInstallAdapter adapter, Guid boot)
    {
        Execute(() =>
        {
            // Open, rather than File.Exists (which hides access/IO errors). Only missing is eligible.
            try
            {
                ReadCore();
                throw new InvalidOperationException("Existing state must never be initialized.");
            }
            catch (FileNotFoundException)
            {
                // Absence alone grants no installation authority.
            }
            Port(() => adapter.RequireQualifiedNewInstallation(directory));
            Persist(new(boot, Guid.Empty, 1, PolicySessionPhase.Empty, default, default, false, false,
                PolicySessionFault.None, 0, 0, 0, default), false);
            return true;
        });
    }

    internal void Commit(PolicySessionRecordV2Unqualified expected, PolicySessionRecordV2Unqualified next)
    {
        Execute(() =>
        {
            if (ReadCore() != expected)
            {
                throw new InvalidOperationException("Stale journal transition.");
            }
            PolicySessionTransitionV2Unqualified.Require(expected, next);
            Persist(next, true);
            return true;
        });
    }

    private void Persist(PolicySessionRecordV2Unqualified record, bool replace)
    {
        byte[] bytes = record.Encode();
        Port(durability.RequireSupported);
        string temporary = Path.Combine(directory, $"session-{Guid.NewGuid():N}.tmp");
        using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes.AsSpan(0, bytes.Length / 2));
            Barrier(PolicyJournalBarrier.Write);
            output.Write(bytes.AsSpan(bytes.Length / 2));
            Barrier(PolicyJournalBarrier.Flush);
            output.Flush(true);
        }
        RequireUsable();
        Barrier(PolicyJournalBarrier.Rename);
        File.Move(temporary, path, replace);
        Barrier(PolicyJournalBarrier.DirectorySync);
        Port(() => durability.SynchronizeDirectory(directory));
        if (ReadCore() != record)
        {
            throw new IOException("Session journal readback mismatch.");
        }
        // Never delete interrupted temps or use them for recovery.
    }

    private void Barrier(PolicyJournalBarrier point)
    {
        Port(() => barrier?.Invoke(point));
    }

    private void Port(Action operation)
    {
        RequireUsable();
        operation();
        RequireUsable();
    }

    private T Execute<T>(Func<T> operation)
    {
        lock (order)
        {
            if (entered)
            {
                uncertain = true;
                throw new InvalidOperationException("Journal reentry refused.");
            }
            RequireUsable();
            entered = true;
            try
            {
                T result = operation();
                RequireUsable();
                return result;
            }
            catch
            {
                uncertain = true;
                throw;
            }
            finally
            {
                entered = false;
            }
        }
    }

    private void RequireUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (uncertain)
        {
            throw new IOException("Journal outcome unknown; explicit reopen/reconciliation required.");
        }
    }

    public void Dispose()
    {
        lock (order)
        {
            if (entered)
            {
                uncertain = true;
                throw new InvalidOperationException("Journal close during operation refused.");
            }
            if (disposed)
            {
                return;
            }
            // Close once, even when close throws. No retry using a possibly recycled descriptor.
            disposed = true;
            try
            {
                exclusion.Dispose();
            }
            catch
            {
                uncertain = true;
                throw;
            }
        }
    }
}

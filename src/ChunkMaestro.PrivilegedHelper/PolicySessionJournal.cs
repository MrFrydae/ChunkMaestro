namespace ChunkMaestro.PrivilegedHelper;

internal enum PolicyJournalBarrier
{
    Write,
    Flush,
    Rename,
    DirectorySync
}

// Future protected install adapter must qualify ancestry and pristine manager/store/worker state.
// This contract supplies no Boolean trust factory and is not implemented by product code here.
internal abstract class PolicySessionInstallAdapter
{
    internal abstract void RequireQualifiedNewInstallation(string directory);
}

internal interface IPolicyJournalDurability
{
    void RequireSupported();
    void SynchronizeDirectory(string directory);
}

// Cooperative-process exclusion only. Protected ancestry, no-follow acquisition and installation
// authenticity remain future native qualification; this API grants no authority.
internal sealed class PolicySessionJournal : IDisposable
{
    private readonly string directory;
    private readonly string path;
    private readonly FileStream exclusion;
    private readonly IPolicyJournalDurability durability;
    private readonly Action<PolicyJournalBarrier>? barrier;
    private bool uncertain;
    private bool disposed;

    internal PolicySessionJournal(string directory, IPolicyJournalDurability? durability = null,
        Action<PolicyJournalBarrier>? barrier = null)
    {
        this.directory = Path.GetFullPath(directory);
        path = Path.Combine(this.directory, "session.journal");
        this.durability = durability ?? new LinuxPolicyJournalDurability();
        this.barrier = barrier;
        // FileShare.None uses a nonblocking OS lock on Unix. Never unlink this lockfile.
        exclusion = new FileStream(Path.Combine(this.directory, "session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal PolicySessionRecord Read()
    {
        RequireUsable();
        try
        {
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != PolicySessionRecord.Length)
            {
                throw new InvalidDataException("Session journal size refused.");
            }
            byte[] bytes = new byte[PolicySessionRecord.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException("Session journal changed during read.");
            }
            return PolicySessionRecord.Decode(bytes);
        }
        catch
        {
            uncertain = true;
            throw;
        }
    }

    internal void InitializeNewInstallation(PolicySessionInstallAdapter adapter, Guid boot)
    {
        RequireUsable();
        adapter.RequireQualifiedNewInstallation(directory);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("Existing state must never be initialized.");
        }
        Persist(new(boot, Guid.Empty, 1, PolicySessionPhase.Empty, default, default, false, false,
            PolicySessionFault.None, 0, 0), false);
    }

    internal void Commit(PolicySessionRecord expected, PolicySessionRecord next)
    {
        RequireUsable();
        if (Read() != expected || next.Revision != checked(expected.Revision + 1))
        {
            throw new InvalidOperationException("Stale journal transition.");
        }
        Persist(next, true);
    }

    private void Persist(PolicySessionRecord record, bool replace)
    {
        byte[] bytes = record.Encode();
        string temporary = Path.Combine(directory, $"session-{Guid.NewGuid():N}.tmp");
        try
        {
            durability.RequireSupported();
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes.AsSpan(0, bytes.Length / 2));
                barrier?.Invoke(PolicyJournalBarrier.Write);
                output.Write(bytes.AsSpan(bytes.Length / 2));
                barrier?.Invoke(PolicyJournalBarrier.Flush);
                output.Flush(true);
            }
            barrier?.Invoke(PolicyJournalBarrier.Rename);
            File.Move(temporary, path, replace);
            barrier?.Invoke(PolicyJournalBarrier.DirectorySync);
            durability.SynchronizeDirectory(directory);
            // Exact readback is additional verification, not a replacement for directory fsync.
            if (Read() != record)
            {
                throw new IOException("Session journal readback mismatch.");
            }
        }
        catch
        {
            uncertain = true;
            throw;
        }
        // Interrupted temps are intentionally preserved; they are never recovery input.
    }

    private void RequireUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (uncertain)
        {
            throw new IOException("Session journal outcome unknown; explicit reopen/reconciliation required.");
        }
    }

    public void Dispose()
    {
        disposed = true;
        exclusion.Dispose();
    }
}

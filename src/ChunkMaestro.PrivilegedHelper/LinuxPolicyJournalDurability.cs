using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

// Synchronizes directory metadata only. This does not qualify ancestry, ownership,
// filesystem crash behavior or an installation for privileged product use.
internal sealed partial class LinuxPolicyJournalDurability : IPolicyJournalDurability
{
    public void RequireSupported()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("Journal directory synchronization requires Linux x64 or arm64.");
        }
    }

    public void SynchronizeDirectory(string directory)
    {
        RequireSupported();
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (directory.Contains('\0', StringComparison.Ordinal))
        {
            throw new IOException("Invalid journal directory.");
        }
        // Linux read-only O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW. Final symlinks refuse;
        // protected descriptor-relative ancestry acquisition remains a separate gate.
        int descriptor = Open(directory, 0x10000 | 0x80000 | 0x20000);
        if (descriptor < 0)
        {
            throw new IOException("Journal directory open failed.");
        }
        int syncResult = Fsync(descriptor);
        // Never retry close: Linux may already have released the descriptor on error.
        int closeResult = Close(descriptor);
        if (syncResult != 0)
        {
            throw new IOException("Journal directory synchronization failed.");
        }
        if (closeResult != 0)
        {
            throw new IOException("Journal directory close failed.");
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc.so.6", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc.so.6", EntryPoint = "fsync")]
    private static partial int Fsync(int descriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc.so.6", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}

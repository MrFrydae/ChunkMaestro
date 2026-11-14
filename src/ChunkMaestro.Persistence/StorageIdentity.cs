using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChunkMaestro.Persistence;

// Handles keep native identities alive; Windows also denies rename/delete while held.
internal sealed class StorageIdentity : IDisposable
{
    private readonly List<(string Path, SafeFileHandle Handle)> entries = [];
    private SafeFileHandle? controller;

    internal StorageIdentity(string directory)
    {
        try
        {
            Capture(directory);
            Verify();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void BindFiles(FileStream controllerLock)
    {
        controller = controllerLock.SafeFileHandle;
        string directory = entries[0].Path;
        Capture(Path.Combine(directory, "fleet.db"));
        if (OperatingSystem.IsMacOS())
        {
            Capture(Path.Combine(directory, "controller.lock"));
        }
        Verify();
    }

    private void Capture(string path) => entries.Add((path, Open(path)));

    private static SafeFileHandle Open(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(path, 0, FileShare.ReadWrite, 0, FileMode.Open, 0x02000000, 0);
        }
        else
        {
            handle = new SafeFileHandle((nint)MacOpen(path), true);
        }
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw StorageLocation.Invalid();
        }
        return handle;
    }

    internal void Verify()
    {
        if (OperatingSystem.IsMacOS() && controller != null &&
            MacMatches(controller, Path.Combine(entries[0].Path, "controller.lock")) != 1)
        {
            throw StorageLocation.Invalid();
        }
        foreach ((string path, SafeFileHandle handle) in entries)
        {
            bool matches;
            if (OperatingSystem.IsMacOS())
            {
                matches = MacMatches(handle, path) == 1;
            }
            else
            {
                using SafeFileHandle current = Open(path);
                matches = GetIdentity(handle, 18, out FileIdentity held, 24) &&
                    GetIdentity(current, 18, out FileIdentity found, 24) && held.Equals(found);
            }
            if (!matches)
            {
                throw StorageLocation.Invalid();
            }
        }
        string directory = entries[0].Path;
        for (DirectoryInfo? item = new DirectoryInfo(directory); item != null; item = item.Parent)
        {
            StorageLocation.CheckAncestor(item.FullName);
        }
        StorageLocation.CheckScope(directory, true);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            StorageLocation.CheckScope(path, Directory.Exists(path));
        }
    }

    public void Dispose()
    {
        foreach ((string _, SafeFileHandle handle) in entries)
        {
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int kind, out FileIdentity identity, uint size);
    [DllImport("chunkmaestro_storage", EntryPoint = "cm_open", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("chunkmaestro_storage", EntryPoint = "cm_matches", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int MacMatches(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
}

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using ChunkMaestro.Application;

namespace ChunkMaestro.Persistence;

/// <summary>Fixed user-local store. No remote input or arbitrary database filename is accepted.</summary>
public sealed class StorageLocation
{
    internal string DirectoryPath { get; }
    private StorageLocation(string path) => DirectoryPath = path;

    /// <summary>Use the OS user's local application-data directory. The native caller must first confirm
    /// this directory is on local, non-synchronized storage; portable .NET does not prove volume locality.</summary>
    public static StorageLocation ForCurrentUser(bool localStorageConfirmed)
    {
        if (!localStorageConfirmed) throw Invalid();
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) throw Invalid();
        return new StorageLocation(Path.Combine(root, "ChunkMaestro", "Fleet"));
    }

    internal static StorageLocation ForTests(string path) => new(path);

    internal string Prepare()
    {
        var path = Path.GetFullPath(DirectoryPath);
        if (!Path.IsPathFullyQualified(DirectoryPath) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw Invalid();
        // Reject links in every existing ancestor, including redirected application-data roots.
        for (var item = new DirectoryInfo(path); item != null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CheckScope(path, true);
        foreach (var file in Directory.EnumerateFileSystemEntries(path)) CheckScope(file, Directory.Exists(file));
        return path;
    }

    internal static void CheckScope(string path, bool directory = false)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        if (OperatingSystem.IsWindows())
        {
            var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null || !user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw Invalid();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow &&
                    !rule.IdentityReference.Equals(user) &&
                    rule.IdentityReference.Value != "S-1-5-18" &&
                    rule.IdentityReference.Value != "S-1-5-32-544") throw Invalid();
        }
        else
        {
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw Invalid();
            if (!OperatingSystem.IsMacOS()) throw Invalid();
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                if (Stat(path, buffer) != 0 || unchecked((uint)Marshal.ReadInt32(buffer, 16)) != GetEffectiveUser()) throw Invalid();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal static FileStream CreateOrOpen(string path)
    {
        if (File.Exists(path)) CheckScope(path);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static FleetStorageException Invalid() => new(StorageFailure.InvalidLocation,
        "Storage must be an owner-scoped, local directory without symbolic links or shared access.");
    [DllImport("libc", EntryPoint = "stat")] private static extern int Stat(string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUser();
}

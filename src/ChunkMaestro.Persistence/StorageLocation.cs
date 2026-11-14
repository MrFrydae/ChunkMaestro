using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using ChunkMaestro.Application;

namespace ChunkMaestro.Persistence;

/// <summary>Fixed user-local store. No remote input or arbitrary database filename is accepted.</summary>
public sealed class StorageLocation
{
    internal string DirectoryPath
    {
        get;
    }

    private StorageLocation(string path) => DirectoryPath = path;

    /// <summary>Use the OS user's local application-data directory. The native caller must first confirm
    /// this directory is on local, non-synchronized storage; portable .NET does not prove volume locality.</summary>
    public static StorageLocation ForCurrentUser(bool localStorageConfirmed)
    {
        EnsureSupportedPlatform();
        if (!localStorageConfirmed)
        {
            throw Invalid();
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw Invalid();
        }

        return new StorageLocation(Path.Combine(root, "ChunkMaestro", "Fleet"));
    }

    internal static StorageLocation ForTests(string path) => new(path);

    internal string Prepare()
    {
        EnsureSupportedPlatform();
        string path = Path.GetFullPath(DirectoryPath);
        if (!Path.IsPathFullyQualified(DirectoryPath) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw Invalid();
        }
        // Reject links in every existing ancestor, including redirected application-data roots.
        for (DirectoryInfo? item = new DirectoryInfo(path); item != null; item = item.Parent)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid();
            }
        }

        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(path))
            {
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw Invalid();
                DirectorySecurity security = new DirectorySecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).Create(security);
            }
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        CheckScope(path, true);
        foreach (string file in Directory.EnumerateFileSystemEntries(path))
        {
            CheckScope(file, Directory.Exists(file));
        }

        return path;
    }

    internal static void CheckScope(string path, bool directory = false)
    {
        EnsureSupportedPlatform();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid();
        }

        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            SecurityIdentifier? user = WindowsIdentity.GetCurrent().User;
            if (user == null || !user.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            {
                throw Invalid();
            }

            // A null DACL grants everyone access despite having no enumerable allow rules.
            RawSecurityDescriptor descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl is null)
            {
                throw Invalid();
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    !rule.IdentityReference.Equals(user) &&
                    rule.IdentityReference.Value != "S-1-5-18" &&
                    rule.IdentityReference.Value != "S-1-5-32-544")
                {
                    throw Invalid();
                }
            }
        }
        else
        {
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                throw Invalid();
            }

            // macOS mode bits do not describe extended ACL grants. Fail closed on any ACL.
            nint acl = GetAcl(path, 0x100);
            if (acl == 0)
            {
                // Darwin reports ENOENT when an existing inode has no extended ACL.
                if (Marshal.GetLastPInvokeError() != 2)
                {
                    throw Invalid();
                }
            }
            else
            {
                int entryResult = GetAclEntry(acl, 0, out nint _);
                int freeResult = FreeAcl(acl);
                if (entryResult == 0 || freeResult != 0)
                {
                    throw Invalid();
                }
            }

            nint buffer = Marshal.AllocHGlobal(512);
            try
            {
                if (Stat(path, buffer) != 0 || unchecked((uint)Marshal.ReadInt32(buffer, 16)) != GetEffectiveUser())
                {
                    throw Invalid();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

        }
    }

    internal static FileStream CreateOrOpen(string path)
    {
        EnsureSupportedPlatform();
        if (File.Exists(path))
        {
            CheckScope(path);
        }
        else if (OperatingSystem.IsWindows())
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw Invalid();
            FileSecurity security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, security);
        }

        FileStreamOptions options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Fleet controller storage requires Windows or macOS. Linux is reserved for the restricted agent.");
        }
    }

    [DllImport("libc", EntryPoint = "acl_get_file", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern nint GetAcl([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);
    [DllImport("libc", EntryPoint = "acl_get_entry")]
    private static extern int GetAclEntry(nint acl, int entryId, out nint entry);
    [DllImport("libc", EntryPoint = "acl_free")]
    private static extern int FreeAcl(nint acl);

    private static FleetStorageException Invalid() => new(StorageFailure.InvalidLocation,
        "Storage must be an owner-scoped, local directory without symbolic links or shared access.");
    [DllImport("libc", EntryPoint = "stat", BestFitMapping = false, ThrowOnUnmappableChar = true)] private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUser();
}

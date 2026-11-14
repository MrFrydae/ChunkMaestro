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
            if (item.Exists)
            {
                CheckAncestor(item.FullName);
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
                Stack<string> missing = new Stack<string>();
                for (DirectoryInfo? item = new DirectoryInfo(path); item != null && !item.Exists; item = item.Parent)
                {
                    missing.Push(item.FullName);
                }
                foreach (string component in missing)
                {
                    new DirectoryInfo(component).Create(security);
                    CheckScope(component, true);
                }
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

            if (MacScope(path, 0) != 1)
            {
                throw Invalid();
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

    // SQLite's Windows VFS uses the token's default owner for newly created files.
    // Elevated tokens may default to Administrators. Create absent WAL/SHM files
    // with our explicit owner before SQLite uses them; never repair existing files.
    internal static void PrepareSqliteSidecars(string databasePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] suffixes = ["-wal", "-shm"];
        foreach (string suffix in suffixes)
        {
            string path = databasePath + suffix;
            if (File.Exists(path))
            {
                CheckScope(path);
            }
        }
        foreach (string suffix in suffixes)
        {
            string path = databasePath + suffix;
            if (!File.Exists(path))
            {
                using FileStream created = CreateOrOpen(path);
            }
        }
    }

    internal static void CheckAncestor(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid();
        }
        if (OperatingSystem.IsMacOS())
        {
            if (MacScope(path, 1) != 1)
            {
                throw Invalid();
            }
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw Invalid();
        }
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw Invalid();
        string? owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        // TrustedInstaller owns system ancestors; this does not broaden leaf ownership.
        if (owner != user.Value && owner != "S-1-5-18" && owner != "S-1-5-32-544" &&
            owner != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
        {
            throw Invalid();
        }
        RawSecurityDescriptor descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw Invalid();
        }
        FileSystemRights dangerous = FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & dangerous) != 0 && rule.IdentityReference.Value != user.Value &&
                rule.IdentityReference.Value != "S-1-5-18" && rule.IdentityReference.Value != "S-1-5-32-544" &&
                rule.IdentityReference.Value != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
            {
                throw Invalid();
            }
        }
    }

    [DllImport("chunkmaestro_storage", EntryPoint = "cm_scope", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int MacScope([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int ancestor);

    internal static FleetStorageException Invalid() => new(StorageFailure.InvalidLocation,
        "Storage must be an owner-scoped, local directory without symbolic links or shared access.");
}

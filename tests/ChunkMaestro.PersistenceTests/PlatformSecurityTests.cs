using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ChunkMaestro.Application;
using ChunkMaestro.Persistence;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class PlatformSecurityTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-security-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewDirectoryAndFilesAreOwnerScopedAndReopenable()
    {
        StorageLocation location = StorageLocation.ForTests(root);
        location.Prepare();
        string file = Path.Combine(root, "fixture");
        using (StorageLocation.CreateOrOpen(file))
        {
        }
        StorageLocation.CheckScope(file);
        Assert.Equal(root, location.Prepare());
    }

    [Fact]
    public void ExistingSharedDirectoryIsRejectedWithoutRepair()
    {
        StorageLocation location = StorageLocation.ForTests(root);
        location.Prepare();
        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo info = new DirectoryInfo(root);
            DirectorySecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
            string before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Invalid(location);
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
            DirectorySecurity nullAcl = new DirectorySecurity();
            nullAcl.SetSecurityDescriptorSddlForm("O:" + user.Value + "D:NO_ACCESS_CONTROL");
            info.SetAccessControl(nullAcl);
            Invalid(location);
            Assert.Null(new RawSecurityDescriptor(info.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
        }
        else
        {
            UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
            File.SetUnixFileMode(root, shared);
            Invalid(location);
            Assert.Equal(shared, File.GetUnixFileMode(root));
        }
    }

    [Fact]
    public void ExistingUnsafeFileIsRejectedWithoutRepair()
    {
        StorageLocation location = StorageLocation.ForTests(root);
        location.Prepare();
        string path = Path.Combine(root, "fixture");
        using (StorageLocation.CreateOrOpen(path))
        {
        }
        if (OperatingSystem.IsWindows())
        {
            FileInfo info = new FileInfo(path);
            FileSecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
        Invalid(location);
        Assert.Throws<FleetStorageException>(() => StorageLocation.CreateOrOpen(path));
    }

    [Fact]
    public void LinkedAncestorAndDanglingEntryAreRejected()
    {
        StorageLocation location = StorageLocation.ForTests(root);
        location.Prepare();
        string link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, root);
        Invalid(StorageLocation.ForTests(Path.Combine(link, "new")));
        Directory.Delete(link);
        File.CreateSymbolicLink(link, Path.Combine(root, "missing"));
        Invalid(location);
        Assert.False(File.Exists(Path.Combine(root, "missing")));
    }

    [Fact]
    public void OwnerOrExtendedAclMismatchIsRejected()
    {
        StorageLocation location = StorageLocation.ForTests(root);
        location.Prepare();
        if (OperatingSystem.IsWindows())
        {
            // Hosted Windows CI runs elevated: exercise the exact default-owner failure from run 43561704499.
            DirectoryInfo info = new DirectoryInfo(root);
            DirectorySecurity original = info.GetAccessControl();
            DirectorySecurity changed = info.GetAccessControl();
            changed.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            info.SetAccessControl(changed);
            try
            {
                Invalid(location);
            }
            finally
            {
                info.SetAccessControl(original);
            }
        }
        else
        {
            ProcessStartInfo start = new ProcessStartInfo("/bin/chmod");
            start.ArgumentList.Add("+a");
            start.ArgumentList.Add("everyone allow read");
            start.ArgumentList.Add(root);
            using Process process = Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Invalid(location);
        }
    }

    [Fact]
    public void UntrustedAncestorIsRejectedBeforeCreatingAnyStorage()
    {
        StorageLocation.ForTests(root).Prepare();
        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo info = new DirectoryInfo(root);
            DirectorySecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            info.SetAccessControl(security);
            string before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Invalid(StorageLocation.ForTests(Path.Combine(root, "missing", "Fleet")));
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            DirectorySecurity nullAcl = new DirectorySecurity();
            nullAcl.SetSecurityDescriptorSddlForm("O:" + WindowsIdentity.GetCurrent().User!.Value + "D:NO_ACCESS_CONTROL");
            info.SetAccessControl(nullAcl);
            Invalid(StorageLocation.ForTests(Path.Combine(root, "missing", "Fleet")));
            Assert.Null(new RawSecurityDescriptor(info.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
        }
        else
        {
            File.SetUnixFileMode(root, (UnixFileMode)511);
            Invalid(StorageLocation.ForTests(Path.Combine(root, "missing", "Fleet")));
            Assert.Equal((UnixFileMode)511, File.GetUnixFileMode(root));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void NativeUserPathHasTrustedAncestors()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (DirectoryInfo? item = new DirectoryInfo(home); item != null; item = item.Parent)
        {
            StorageLocation.CheckAncestor(item.FullName);
        }
        // All other fixtures use the actual trusted sticky macOS temp directory or Windows user temp.
        Assert.Equal(root, StorageLocation.ForTests(root).Prepare());
    }

    private static void Invalid(StorageLocation location)
    {
        Assert.Equal(StorageFailure.InvalidLocation, Assert.Throws<FleetStorageException>(() => location.Prepare()).Failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

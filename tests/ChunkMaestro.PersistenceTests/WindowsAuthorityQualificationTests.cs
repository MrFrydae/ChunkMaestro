using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

// The parent must exclude this file on non-Windows hosts in both test projects.
// No runtime skip can substitute for executing these native Windows assertions.
[SupportedOSPlatform("windows")]
public sealed class WindowsAuthorityQualificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkmaestro-windows-authority-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(WellKnownSidType.LocalSystemSid, false)]
    [InlineData(WellKnownSidType.LocalSystemSid, true)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, false)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, true)]
    public async Task TrustedExplicitGrantsRetainOwnerAndPersistInventory(WellKnownSidType principal, bool directory)
    {
        string leaf = Path.Combine(root, "Fleet");
        StorageLocation location = StorageLocation.ForTests(leaf);
        FleetId retained = new FleetId(Guid.NewGuid());
        await using (LocalFleetStore initial = await LocalFleetStore.OpenAsync(location))
        {
            await initial.RegisterFleetAsync(retained, "retained");
        }
        string path = directory ? leaf : Path.Combine(leaf, "fleet.db");
        FileSystemSecurity original = Security(path, directory);
        FileSystemSecurity changed = Security(path, directory);
        SecurityIdentifier sid = new SecurityIdentifier(principal, null);
        FileSystemAccessRule grant = new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow);
        changed.AddAccessRule(grant);
        SetSecurity(path, directory, changed);
        try
        {
            AssertRule(path, directory, sid, AccessControlType.Allow, false);
            Assert.Equal(WindowsIdentity.GetCurrent().User, Security(path, directory).GetOwner(typeof(SecurityIdentifier)));
            byte[] acl = AccessBytes(path, directory);
            StorageLocation.CheckScope(path, directory);
            await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(location);
            Assert.Equal(retained, Assert.Single((await reopened.ReadAsync()).Fleets).Id);
            FleetId next = new FleetId(Guid.NewGuid());
            await reopened.RegisterFleetAsync(next, "explicit write");
            Assert.Contains((await reopened.ReadAsync()).Fleets, fleet => fleet.Id == next);
            Assert.Equal(acl, AccessBytes(path, directory));
        }
        finally
        {
            RestoreAccess(path, directory, original);
        }
    }

    [Theory]
    [InlineData(AccessControlType.Allow)]
    [InlineData(AccessControlType.Deny)]
    public async Task InheritedForeignRulesDistinguishExposureFromDenialAndRequireExplicitRecovery(AccessControlType access)
    {
        string leaf = Path.Combine(root, "Fleet");
        StorageLocation location = StorageLocation.ForTests(leaf);
        FleetId retained = new FleetId(Guid.NewGuid());
        await using (LocalFleetStore initial = await LocalFleetStore.OpenAsync(location))
        {
            await initial.RegisterFleetAsync(retained, "retained");
        }
        // ANONYMOUS LOGON is a real foreign principal, without provisioning a user.
        // Denying it cannot revoke the interactive runner's own access.
        SecurityIdentifier foreign = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        Assert.NotEqual(foreign, current.User);
        Assert.DoesNotContain(foreign, current.Groups!.Cast<IdentityReference>());
        DirectorySecurity parentOriginal = new DirectoryInfo(root).GetAccessControl();
        DirectorySecurity leafOriginal = new DirectoryInfo(leaf).GetAccessControl();
        string database = Path.Combine(leaf, "fleet.db");
        byte[] bytes = File.ReadAllBytes(database);
        string[] entries = Directory.GetFileSystemEntries(leaf).Order().ToArray();
        DirectorySecurity parentChanged = new DirectoryInfo(root).GetAccessControl();
        parentChanged.AddAccessRule(new FileSystemAccessRule(foreign, FileSystemRights.ReadData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, access));
        new DirectoryInfo(root).SetAccessControl(parentChanged);
        DirectorySecurity leafChanged = new DirectoryInfo(leaf).GetAccessControl();
        leafChanged.SetAccessRuleProtection(false, true);
        new DirectoryInfo(leaf).SetAccessControl(leafChanged);
        try
        {
            AssertRule(leaf, true, foreign, access, true);
            byte[] inheritedAccess = AccessBytes(leaf, true);
            StorageLocation.CheckAncestor(root);
            if (access == AccessControlType.Allow)
            {
                Invalid(Assert.Throws<FleetStorageException>(() => StorageLocation.CheckScope(leaf, true)));
                Invalid(await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(location)));
                Assert.Equal(bytes, File.ReadAllBytes(database));
                Assert.Equal(entries, Directory.GetFileSystemEntries(leaf).Order().ToArray());
            }
            else
            {
                StorageLocation.CheckScope(leaf, true);
                await using LocalFleetStore allowed = await LocalFleetStore.OpenAsync(location);
                Assert.Equal(retained, Assert.Single((await allowed.ReadAsync()).Fleets).Id);
                await allowed.RegisterFleetAsync(new FleetId(Guid.NewGuid()), "deny does not expose storage");
                Assert.Equal(2, (await allowed.ReadAsync()).Fleets.Count);
            }
            Assert.Equal(inheritedAccess, AccessBytes(leaf, true));
        }
        finally
        {
            RestoreAccess(root, true, parentOriginal);
            RestoreAccess(leaf, true, leafOriginal);
        }
        StorageLocation.CheckScope(leaf, true);
        await using LocalFleetStore retry = await LocalFleetStore.OpenAsync(location);
        Assert.Contains((await retry.ReadAsync()).Fleets, fleet => fleet.Id == retained);
        await retry.RegisterFleetAsync(new FleetId(Guid.NewGuid()), "explicit retry");
        Assert.Equal(access == AccessControlType.Allow ? 2 : 3, (await retry.ReadAsync()).Fleets.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafeAncestorGrantDoesNotLeakIntoNewOwnerProtectedComponents(bool inheritOnly)
    {
        StorageLocation.ForTests(root).Prepare();
        DirectorySecurity original = new DirectoryInfo(root).GetAccessControl();
        DirectorySecurity changed = new DirectoryInfo(root).GetAccessControl();
        SecurityIdentifier foreign = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        FileSystemRights rights = inheritOnly ? FileSystemRights.FullControl : FileSystemRights.ReadData;
        changed.AddAccessRule(new FileSystemAccessRule(foreign, rights,
            inheritOnly ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            inheritOnly ? PropagationFlags.InheritOnly : PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(changed);
        try
        {
            FileSystemAccessRule actual = AssertRule(root, true, foreign, AccessControlType.Allow, false);
            Assert.Equal(inheritOnly ? PropagationFlags.InheritOnly : PropagationFlags.None, actual.PropagationFlags);
            byte[] parentAccess = AccessBytes(root, true);
            StorageLocation.CheckAncestor(root);
            string intermediate = Path.Combine(root, "new");
            string leaf = Path.Combine(intermediate, "Fleet");
            Assert.Equal(leaf, StorageLocation.ForTests(leaf).Prepare());
            foreach (string path in new[]
            {
                intermediate,
                leaf
            })
            {
                DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                Assert.Equal(WindowsIdentity.GetCurrent().User, security.GetOwner(typeof(SecurityIdentifier)));
                FileSystemAccessRule owner = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                Assert.Equal(WindowsIdentity.GetCurrent().User, owner.IdentityReference);
                Assert.Equal(FileSystemRights.FullControl, owner.FileSystemRights);
                Assert.False(owner.IsInherited);
                StorageLocation.CheckScope(path, true);
            }
            string file = Path.Combine(leaf, "sentinel");
            using (FileStream created = StorageLocation.CreateOrOpen(file))
            {
                created.WriteByte(37);
            }
            FileSecurity fileSecurity = new FileInfo(file).GetAccessControl();
            Assert.True(fileSecurity.AreAccessRulesProtected);
            Assert.Equal(WindowsIdentity.GetCurrent().User, fileSecurity.GetOwner(typeof(SecurityIdentifier)));
            FileSystemAccessRule fileOwner = Assert.Single(fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(WindowsIdentity.GetCurrent().User, fileOwner.IdentityReference);
            Assert.Equal(FileSystemRights.FullControl, fileOwner.FileSystemRights);
            Assert.Equal(AccessControlType.Allow, fileOwner.AccessControlType);
            Assert.False(fileOwner.IsInherited);
            StorageLocation.CheckScope(file);
            using FileStream reopened = StorageLocation.CreateOrOpen(file);
            Assert.Equal(37, reopened.ReadByte());
            Assert.Equal(-1, reopened.ReadByte());
            Assert.Equal(parentAccess, AccessBytes(root, true));
        }
        finally
        {
            RestoreAccess(root, true, original);
        }
    }

    [Fact]
    public void ExclusiveDatabaseHandleRefusesBindingAndDisposalReleasesDirectoryForExplicitRetry()
    {
        StorageLocation.ForTests(root).Prepare();
        string database = Path.Combine(root, "fleet.db");
        using (FileStream created = StorageLocation.CreateOrOpen(database))
        {
            created.WriteByte(61);
        }
        using (FileStream controller = StorageLocation.CreateOrOpen(Path.Combine(root, "controller.lock")))
        {
            using (FileStream exclusive = StorageLocation.CreateOrOpen(database))
            {
                using StorageIdentity refused = new StorageIdentity(root);
                Invalid(Assert.Throws<FleetStorageException>(() => refused.BindFiles(controller)));
                Assert.Equal(61, exclusive.ReadByte());
                Assert.Equal(-1, exclusive.ReadByte());
            }
            // Failed BindFiles and its disposed identity must leave no capture handle behind.
            File.Move(database, database + "-released");
            File.Move(database + "-released", database);
            using (StorageIdentity retry = new StorageIdentity(root))
            {
                retry.BindFiles(controller);
                retry.Verify();
                Assert.Throws<IOException>(() => File.Move(database, database + "-blocked"));
                Assert.Throws<IOException>(() => Directory.Move(root, root + "-blocked"));
                Assert.False(File.Exists(database + "-blocked"));
                Assert.False(Directory.Exists(root + "-blocked"));
                retry.Verify();
            }
            // Caller retains the lock after identity disposal; it is not owned by StorageIdentity.
            controller.WriteByte(19);
            controller.Flush();
            Assert.Equal(1, controller.Length);
        }
        Directory.Move(root, root + "-released");
        Directory.Move(root + "-released", root);
        Assert.Equal(new byte[]
        {
            61
        }, File.ReadAllBytes(database));
    }

    private static FileSystemSecurity Security(string path, bool directory)
    {
        return directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
    }

    private static void SetSecurity(string path, bool directory, FileSystemSecurity security)
    {
        if (directory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity) security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity) security);
        }
    }

    private static FileSystemAccessRule AssertRule(string path, bool directory, SecurityIdentifier principal, AccessControlType access, bool inherited)
    {
        return Assert.Single(Security(path, directory).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(principal) && rule.AccessControlType == access && rule.IsInherited == inherited);
    }

    private static byte[] AccessBytes(string path, bool directory)
    {
        RawSecurityDescriptor descriptor = new RawSecurityDescriptor(Security(path, directory).GetSecurityDescriptorBinaryForm(), 0);
        Assert.NotNull(descriptor.DiscretionaryAcl);
        byte[] bytes = new byte[descriptor.DiscretionaryAcl.BinaryLength];
        descriptor.DiscretionaryAcl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void RestoreAccess(string path, bool directory, FileSystemSecurity original)
    {
        FileSystemSecurity restored = directory ? new DirectorySecurity() : new FileSecurity();
        restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        SetSecurity(path, directory, restored);
    }

    private void Invalid(FleetStorageException error)
    {
        Assert.Equal(StorageFailure.InvalidLocation, error.Failure);
        Assert.DoesNotContain(root, error.ToString());
        Assert.Null(error.BackupPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

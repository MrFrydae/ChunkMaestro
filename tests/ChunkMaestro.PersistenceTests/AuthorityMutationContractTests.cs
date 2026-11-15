using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class AuthorityMutationContractTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-authority-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfirmedProductionLocationUsesNativeLocalRootWithoutOpeningUserStorage()
    {
        // Resolve only: never Prepare/Open the real user's application-data directory.
        StorageLocation location = StorageLocation.ForCurrentUser(true);
        string nativeRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(nativeRoot));
        Assert.Equal(Path.Combine(nativeRoot, "ChunkMaestro", "Fleet"), location.DirectoryPath);
        Assert.True(Path.IsPathFullyQualified(location.DirectoryPath));
    }

    [Fact]
    public async Task RelativeLocationCannotCreateAStore()
    {
        // Temp and checkout can be on different Windows drives, where
        // GetRelativePath returns an absolute path instead of a relative one.
        string relative = Path.Combine(".", ".artifacts", "tests", Path.GetFileName(root));
        string target = Path.GetFullPath(relative);
        Assert.False(Path.IsPathFullyQualified(relative));
        Assert.False(Directory.Exists(target));
        try
        {
            FleetStorageException error = await Assert.ThrowsAsync<FleetStorageException>(() =>
                LocalFleetStore.OpenAsync(StorageLocation.ForTests(relative)));
            InvalidLocation(error, target);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
        }
    }

    [Fact]
    public void MissingNativeIdentityIsRefusedAndExplicitCreationAllowsRetry()
    {
        StorageLocation.ForTests(root).Prepare();
        string missing = Path.Combine(root, "missing");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FleetStorageException error = Assert.Throws<FleetStorageException>(() => new StorageIdentity(missing));
            InvalidLocation(error, missing);
            Assert.False(Directory.Exists(missing));
        }
        StorageLocation.ForTests(missing).Prepare();
        using (StorageIdentity identity = new StorageIdentity(missing))
        {
            identity.Verify();
        }
        // An abandoned native handle must not keep this directory pinned on Windows.
        Directory.Move(missing, missing + "-released");
        Assert.True(Directory.Exists(missing + "-released"));
    }

    [Fact]
    public void FailedFileBindingReleasesDirectoryAuthorityWhenDisposedAndCanRetry()
    {
        StorageLocation.ForTests(root).Prepare();
        string controllerPath = Path.Combine(root, "controller.lock");
        using (FileStream controller = StorageLocation.CreateOrOpen(controllerPath))
        {
            using StorageIdentity identity = new StorageIdentity(root);
            FleetStorageException error = Assert.Throws<FleetStorageException>(() => identity.BindFiles(controller));
            InvalidLocation(error, root);
            Assert.False(File.Exists(Path.Combine(root, "fleet.db")));
        }
        string moved = root + "-released";
        Directory.Move(root, moved);
        Directory.Move(moved, root);
        using (StorageLocation.CreateOrOpen(Path.Combine(root, "fleet.db")))
        {
        }
        using (FileStream controller = StorageLocation.CreateOrOpen(controllerPath))
        {
            using StorageIdentity identity = new StorageIdentity(root);
            identity.BindFiles(controller);
            identity.Verify();
        }
    }

    [Fact]
    public void BoundIdentityOwnsItsCaptureHandlesButLeavesCallerControllerUsable()
    {
        StorageLocation.ForTests(root).Prepare();
        string database = Path.Combine(root, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        using FileStream controller = StorageLocation.CreateOrOpen(Path.Combine(root, "controller.lock"));
        using (StorageIdentity identity = new StorageIdentity(root))
        {
            identity.BindFiles(controller);
            identity.Verify();
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.Move(database, database + "-held"));
            }
            else
            {
                File.Move(database, database + "-held");
                try
                {
                    InvalidLocation(Assert.Throws<FleetStorageException>(identity.Verify), database);
                }
                finally
                {
                    File.Move(database + "-held", database);
                }
                identity.Verify();
            }
        }
        File.Move(database, database + "-released");
        controller.WriteByte(42);
        controller.Flush();
        Assert.Equal(1, controller.Length);
    }

    [Fact]
    public void BindingAClosedControllerCannotEstablishAuthority()
    {
        StorageLocation.ForTests(root).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(root, "fleet.db")))
        {
        }
        string path = Path.Combine(root, "controller.lock");
        using FileStream controller = StorageLocation.CreateOrOpen(path);
        controller.Dispose();
        using StorageIdentity identity = new StorageIdentity(root);
        Assert.Throws<ObjectDisposedException>(() => identity.BindFiles(controller));
        using FileStream retry = StorageLocation.CreateOrOpen(path);
        identity.BindFiles(retry);
        identity.Verify();
    }

    [Fact]
    public void ClosingBoundControllerInvalidatesAuthorityUntilExplicitRebinding()
    {
        StorageLocation.ForTests(root).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(root, "fleet.db")))
        {
        }
        string path = Path.Combine(root, "controller.lock");
        using FileStream controller = StorageLocation.CreateOrOpen(path);
        using StorageIdentity identity = new StorageIdentity(root);
        identity.BindFiles(controller);
        identity.Verify();
        controller.Dispose();
        Assert.Throws<ObjectDisposedException>(identity.Verify);
        using FileStream retry = StorageLocation.CreateOrOpen(path);
        identity.BindFiles(retry);
        identity.Verify();
    }

    [Fact]
    public void ConstructorRefusesUnsafeScopeAndDoesNotPinTheRejectedDirectory()
    {
        StorageLocation.ForTests(root).Prepare();
        Action restore = ExpandPermission(root, true,
            OperatingSystem.IsWindows() ? (int) FileSystemRights.Read : (int) UnixFileMode.OtherRead);
        try
        {
            InvalidLocation(Assert.Throws<FleetStorageException>(() => new StorageIdentity(root)), root);
        }
        finally
        {
            restore();
        }
        string moved = root + "-rejected";
        Directory.Move(root, moved);
        Directory.Move(moved, root);
        using StorageIdentity retry = new StorageIdentity(root);
        retry.Verify();
    }

    [Fact]
    public void BindingMustValidateFilesAddedAfterDirectoryCaptureBeforeReturning()
    {
        StorageLocation.ForTests(root).Prepare();
        using StorageIdentity identity = new StorageIdentity(root);
        string database = Path.Combine(root, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        using FileStream controller = StorageLocation.CreateOrOpen(Path.Combine(root, "controller.lock"));
        Action restore = ExpandPermission(database, false,
            OperatingSystem.IsWindows() ? (int) FileSystemRights.Read : (int) UnixFileMode.OtherRead);
        try
        {
            InvalidLocation(Assert.Throws<FleetStorageException>(() => identity.BindFiles(controller)), root);
        }
        finally
        {
            restore();
        }
        identity.Verify();
    }

    [Fact]
    public void NativeFileAuthorityRefusesUnsafeExistingObjectWithoutChangingItsContents()
    {
        StorageLocation.ForTests(root).Prepare();
        string path = Path.Combine(root, "native-file");
        using (FileStream file = StorageLocation.CreateOrOpen(path))
        {
            file.WriteByte(73);
        }
        if (OperatingSystem.IsWindows())
        {
            FileInfo info = new FileInfo(path);
            byte[] original = info.GetAccessControl().GetSecurityDescriptorBinaryForm();
            FileSecurity unsafeSecurity = new FileSecurity();
            unsafeSecurity.SetSecurityDescriptorSddlForm("O:" + WindowsIdentity.GetCurrent().User!.Value + "D:NO_ACCESS_CONTROL");
            info.SetAccessControl(unsafeSecurity);
            try
            {
                InvalidLocation(Assert.Throws<FleetStorageException>(() => StorageLocation.CreateOrOpen(path)), path);
            }
            finally
            {
                FileSecurity restored = new FileSecurity();
                restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                info.SetAccessControl(restored);
            }
        }
        else
        {
            string alias = path + "-alias";
            ProcessStartInfo start = new ProcessStartInfo("/bin/ln")
            {
                UseShellExecute = false
            };
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(alias);
            using Process process = Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
            try
            {
                InvalidLocation(Assert.Throws<FleetStorageException>(() => StorageLocation.CreateOrOpen(path)), path);
                Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(alias));
            }
            finally
            {
                File.Delete(alias);
            }
        }
        using FileStream retry = StorageLocation.CreateOrOpen(path);
        Assert.Equal(73, retry.ReadByte());
        Assert.Equal(-1, retry.ReadByte());
    }

    public static IEnumerable<object[]> LeafPermissions()
    {
        int[] permissions = OperatingSystem.IsWindows() ?
        [
            (int) FileSystemRights.ReadData,
            (int) FileSystemRights.WriteData,
            (int) FileSystemRights.AppendData,
            (int) FileSystemRights.ExecuteFile,
            (int) FileSystemRights.WriteExtendedAttributes,
            (int) FileSystemRights.ChangePermissions
        ] :
        [
            (int) UnixFileMode.GroupRead,
            (int) UnixFileMode.GroupWrite,
            (int) UnixFileMode.GroupExecute,
            (int) UnixFileMode.OtherRead,
            (int) UnixFileMode.OtherWrite,
            (int) UnixFileMode.OtherExecute
        ];
        foreach (int permission in permissions)
        {
            foreach (bool directory in new[]
            {
                false,
                true
            })
            {
                yield return new object[]
                {
                    permission,
                    directory
                };
            }
        }
    }

    [Theory]
    [MemberData(nameof(LeafPermissions))]
    public async Task SharedLeafAccessIsRefusedWithoutRepairAndRestorationAllowsExplicitRetry(int permission, bool directory)
    {
        string leaf = Path.Combine(root, "Fleet");
        StorageLocation location = StorageLocation.ForTests(leaf);
        FleetId retained = new FleetId(Guid.NewGuid());
        FleetId retry = new FleetId(Guid.NewGuid());
        await using (LocalFleetStore initial = await LocalFleetStore.OpenAsync(location))
        {
            await initial.RegisterFleetAsync(retained, "retained");
        }
        string database = Path.Combine(leaf, "fleet.db");
        string path = directory ? leaf : database;
        byte[] content = File.ReadAllBytes(database);
        string[] entries = Directory.GetFileSystemEntries(leaf).Order().ToArray();
        byte[] originalPermissions = PermissionSnapshot(path, directory);
        Action restore = ExpandPermission(path, directory, permission);
        try
        {
            byte[] unsafePermissions = PermissionSnapshot(path, directory);
            Assert.False(originalPermissions.SequenceEqual(unsafePermissions));
            // This independent C# authority boundary must refuse before the native check can mask it.
            InvalidLocation(Assert.Throws<FleetStorageException>(() => StorageLocation.CheckScope(path, directory)), path);
            if (!directory)
            {
                InvalidLocation(Assert.Throws<FleetStorageException>(() => StorageLocation.CreateOrOpen(path)), path);
            }
            InvalidLocation(await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(location)), path);
            Assert.Equal(content, File.ReadAllBytes(database));
            Assert.Equal(entries, Directory.GetFileSystemEntries(leaf).Order().ToArray());
            Assert.Equal(unsafePermissions, PermissionSnapshot(path, directory));
        }
        finally
        {
            restore();
        }
        Assert.Equal(originalPermissions, PermissionSnapshot(path, directory));
        StorageLocation.CheckScope(path, directory);
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(location);
        Assert.Equal(retained, Assert.Single((await reopened.ReadAsync()).Fleets).Id);
        await reopened.RegisterFleetAsync(retry, "explicit retry");
        Assert.Equal(2, (await reopened.ReadAsync()).Fleets.Count);
    }

    private static byte[] PermissionSnapshot(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            RawSecurityDescriptor descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            Assert.NotNull(descriptor.DiscretionaryAcl);
            byte[] acl = new byte[descriptor.DiscretionaryAcl.BinaryLength];
            descriptor.DiscretionaryAcl.GetBinaryForm(acl, 0);
            // Windows can recompute auto-inheritance bookkeeping on restoration.
            // Compare ownership, protection and every actual access-control entry.
            return Encoding.UTF8.GetBytes($"{descriptor.Owner}|{descriptor.Group}|{descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected}|{Convert.ToHexString(acl)}");
        }
        return BitConverter.GetBytes((int) File.GetUnixFileMode(path));
    }

    public static IEnumerable<object[]> AncestorMutations()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (FileSystemRights right in new[]
            {
                FileSystemRights.WriteAttributes,
                FileSystemRights.WriteExtendedAttributes,
                FileSystemRights.Delete,
                FileSystemRights.DeleteSubdirectoriesAndFiles,
                FileSystemRights.ChangePermissions,
                FileSystemRights.TakeOwnership
            })
            {
                yield return new object[]
                {
                    (int) right
                };
            }
        }
        else
        {
            yield return new object[]
            {
                (int) UnixFileMode.GroupWrite
            };
            yield return new object[]
            {
                (int) UnixFileMode.OtherWrite
            };
        }
    }

    [Theory]
    [MemberData(nameof(AncestorMutations))]
    public async Task LiveAncestorRenameAuthorityRefusesOperationsAndRestorationAllowsExplicitRetry(int permission)
    {
        StorageLocation.ForTests(root).Prepare();
        string leaf = Path.Combine(root, "Fleet");
        FleetId retained = new FleetId(Guid.NewGuid());
        FleetId refused = new FleetId(Guid.NewGuid());
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(leaf));
        await store.RegisterFleetAsync(retained, "retained");
        Action restore = ExpandPermission(root, true, permission);
        try
        {
            InvalidLocation(await Assert.ThrowsAsync<FleetStorageException>(() => store.ReadAsync()), root);
            InvalidLocation(await Assert.ThrowsAsync<FleetStorageException>(() => store.RegisterFleetAsync(refused, "refused")), root);
        }
        finally
        {
            restore();
        }
        Assert.Equal(retained, Assert.Single((await store.ReadAsync()).Fleets).Id);
        await store.RegisterFleetAsync(refused, "explicit retry");
        Assert.Equal(2, (await store.ReadAsync()).Fleets.Count);
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void SidecarPreflightPreservesExistingBytesAndCreatesOnlyAbsentFiles(string existingSuffix)
    {
        StorageLocation.ForTests(root).Prepare();
        string database = Path.Combine(root, "fleet.db");
        string existing = database + existingSuffix;
        byte[] sentinel = [3, 1, 4, 1, 5];
        using (FileStream file = StorageLocation.CreateOrOpen(existing))
        {
            file.Write(sentinel);
        }
        StorageLocation.PrepareSqliteSidecars(database);
        Assert.Equal(sentinel, File.ReadAllBytes(existing));
        Assert.False(File.Exists(database));
        string absent = database + (existingSuffix == "-wal" ? "-shm" : "-wal");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(absent));
            StorageLocation.CheckScope(absent);
            using FileStream file = StorageLocation.CreateOrOpen(absent);
            Assert.Equal(0, file.Length);
        }
        else
        {
            Assert.False(File.Exists(absent));
        }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void UnsafeSidecarPreflightDoesNotCreateItsMissingPeerAndAllowsRetryAfterFixtureRestoration(string suffix)
    {
        StorageLocation.ForTests(root).Prepare();
        string database = Path.Combine(root, "fleet.db");
        string path = database + suffix;
        using (FileStream file = StorageLocation.CreateOrOpen(path))
        {
            file.WriteByte(71);
        }
        string peer = database + (suffix == "-wal" ? "-shm" : "-wal");
        Action restore = ExpandPermission(path, false,
            OperatingSystem.IsWindows() ? (int) FileSystemRights.Read : (int) UnixFileMode.OtherRead);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                InvalidLocation(Assert.Throws<FleetStorageException>(() => StorageLocation.PrepareSqliteSidecars(database)), path);
            }
            else
            {
                // macOS sidecar creation belongs to SQLite; its native scope check still refuses sharing.
                StorageLocation.PrepareSqliteSidecars(database);
                InvalidLocation(Assert.Throws<FleetStorageException>(() => StorageLocation.CheckScope(path)), path);
            }
            Assert.False(File.Exists(peer));
            Assert.Equal(new byte[]
            {
                71
            }, File.ReadAllBytes(path));
        }
        finally
        {
            restore();
        }
        StorageLocation.CheckScope(path);
        StorageLocation.PrepareSqliteSidecars(database);
        Assert.Equal(71, File.ReadAllBytes(path)[0]);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(peer));
        }
    }

    private static Action ExpandPermission(string path, bool directory, int permission)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            byte[] original = security.GetSecurityDescriptorBinaryForm();
            FileSystemAccessRule rule = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                (FileSystemRights) permission, AccessControlType.Allow);
            if (directory)
            {
                ((DirectorySecurity) security).AddAccessRule(rule);
                new DirectoryInfo(path).SetAccessControl((DirectorySecurity) security);
            }
            else
            {
                ((FileSecurity) security).AddAccessRule(rule);
                new FileInfo(path).SetAccessControl((FileSecurity) security);
            }
            return () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    FileSystemSecurity restored = directory ? new DirectorySecurity() : new FileSecurity();
                    restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                    if (directory)
                    {
                        new DirectoryInfo(path).SetAccessControl((DirectorySecurity) restored);
                    }
                    else
                    {
                        new FileInfo(path).SetAccessControl((FileSecurity) restored);
                    }
                }
            };
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | (UnixFileMode) permission);
        return () =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }
        };
    }

    private static void InvalidLocation(FleetStorageException error, string sensitivePath)
    {
        Assert.Equal(StorageFailure.InvalidLocation, error.Failure);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Matches(@"\b(owner|local|directory|symbolic|shared)\b", error.Message.ToLowerInvariant());
        Assert.DoesNotContain(sensitivePath, error.ToString());
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

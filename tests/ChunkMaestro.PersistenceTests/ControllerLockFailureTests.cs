using ChunkMaestro.Application;
using ChunkMaestro.Persistence;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class ControllerLockFailureTests
{
    [Theory]
    [InlineData(5)] // macOS EIO.
    [InlineData(28)] // macOS ENOSPC.
    [InlineData(30)] // macOS EROFS.
    [InlineData(69)] // macOS EDQUOT.
    [InlineData(unchecked((int) 0x80070027))] // Windows ERROR_HANDLE_DISK_FULL.
    [InlineData(unchecked((int) 0x80070070))] // Windows ERROR_DISK_FULL.
    [InlineData(unchecked((int) 0x8007045D))] // Windows ERROR_IO_DEVICE.
    [InlineData(unchecked((int) 0x80070718))] // Windows ERROR_NOT_ENOUGH_QUOTA.
    [InlineData(unchecked((int) 0x80131620))] // Generic managed IOException.
    [InlineData(0)]
    [InlineData(32)] // Raw errno is not a Windows HRESULT.
    [InlineData(33)]
    public void OtherIoErrorsAreNeverControllerContention(int hresult)
    {
        IOException error = new IOException("synthetic device failure", hresult);
        Assert.False(LocalFleetStore.IsControllerContention(error));
    }

    [Fact]
    public void ContentionCodesApplyOnlyToTheirNativePlatform()
    {
        Assert.Equal(OperatingSystem.IsMacOS(), LocalFleetStore.IsControllerContention(new IOException("macOS lock", 35)));
        Assert.Equal(OperatingSystem.IsWindows(), LocalFleetStore.IsControllerContention(new IOException("Windows sharing", unchecked((int) 0x80070020))));
        Assert.Equal(OperatingSystem.IsWindows(), LocalFleetStore.IsControllerContention(new IOException("Windows lock", unchecked((int) 0x80070021))));
    }

    [Fact]
    public async Task ActualExclusiveControllerHandleRetainsCategoryAndReleasesForExplicitReopen()
    {
        string root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        string directory = Path.Combine(root, "chunkmaestro-controller-failure-" + Guid.NewGuid().ToString("N"));
        StorageLocation location = StorageLocation.ForTests(directory);
        location.Prepare();
        string path = Path.Combine(directory, "controller.lock");
        try
        {
            using (FileStream holder = StorageLocation.CreateOrOpen(path))
            {
                IOException native = Assert.Throws<IOException>(() => StorageLocation.CreateOrOpen(path));
                Assert.True(LocalFleetStore.IsControllerContention(native));
                FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(location));
                Assert.Equal(StorageFailure.ControllerInUse, failure.Failure);
                Assert.Contains("Another controller", failure.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(directory, failure.Message, StringComparison.Ordinal);
                Assert.Null(failure.BackupPath);
                Assert.False(File.Exists(Path.Combine(directory, "fleet.db")));
            }

            await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(location);
            Assert.Empty((await reopened.ReadAsync()).Fleets);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

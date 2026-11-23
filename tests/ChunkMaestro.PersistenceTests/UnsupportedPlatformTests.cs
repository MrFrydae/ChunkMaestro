using ChunkMaestro.Persistence;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class UnsupportedPlatformTests
{
    [Fact]
    public async Task ControllerStorageRejectsUnsupportedOsBeforeFilesystemMutation()
    {
        string path = Path.Combine(Path.GetTempPath(), "chunkmaestro-unsupported-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<PlatformNotSupportedException>(() => StorageLocation.ForCurrentUser(true));
        Assert.Throws<PlatformNotSupportedException>(() => StorageLocation.ForTests(path).Prepare());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => LocalFleetStore.OpenAsync(StorageLocation.ForTests(path)));
        Assert.Throws<PlatformNotSupportedException>(() => StorageLocation.CreateOrOpen(path));
        Assert.False(Path.Exists(path));
    }
}

using ChunkMaestro.Credentials;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

internal sealed class OwnedIdentityKeychain : IDisposable
{
    private readonly nint keychain;
    private readonly string? directory;
    private readonly HashSet<string> ownedWindowsNames = [];
    private bool disposed;

    public OwnedIdentityKeychain()
    {
        if (OperatingSystem.IsMacOS())
        {
            directory = Path.Combine(Path.GetTempPath(), "ChunkMaestro-credentials-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            byte[] password = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Equal(0, MacCredentials.SecKeychainCopySearchList(out nint before));
                int status = MacCredentials.SecKeychainCreate(System.Text.Encoding.UTF8.GetBytes(Path.Combine(directory, "fixture.keychain") + "\0"), (uint) password.Length, password, 0, 0, out keychain);
                Assert.Equal(0, status);
                Assert.Equal(0, MacCredentials.SecKeychainCopySearchList(out nint after));
                try
                {
                    Assert.Equal(1, MacCredentials.CFEqual(before, after));
                }
                finally
                {
                    MacCredentials.CFRelease(before);
                    MacCredentials.CFRelease(after);
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(password);
            }
        }
    }

    public string KeychainPath => directory is null ? "-" : Path.Combine(directory, "fixture.keychain");

    public CredentialStore Open()
    {
        return new CredentialStore(Adapter());
    }

    internal CredentialStore Open(MacCredentials.NativeResources resources)
    {
        return new CredentialStore(new MacCredentials(keychain, resources));
    }

    public INativeCredentials Adapter()
    {
        return keychain != 0 ? new MacCredentials(keychain) : new TrackedWindows(this);
    }

    // A killed worker cannot share its in-memory create list with the parent.
    // Register only the exact locator read from this test's retained database.
    internal void OwnWorkerReference(CredentialReference reference)
    {
        if (OperatingSystem.IsWindows())
        {
            OwnWindowsName($"ChunkMaestro.credentials.v1/{reference.Scope.Fleet.Value:N}/{reference.Scope.Target:N}/{reference.Scope.Purpose}/{reference.Id:N}");
        }
    }

    private void OwnWindowsName(string name)
    {
        lock (ownedWindowsNames)
        {
            ownedWindowsNames.Add(name);
        }
    }

    public void Lock()
    {
        Assert.Equal(0, MacCredentials.SecKeychainLock(keychain));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (keychain != 0)
        {
            int status = MacCredentials.SecKeychainDelete(keychain);
            MacCredentials.CFRelease(keychain);
            Assert.Equal(0, status);
            Directory.Delete(directory!, true);
        }
        else if (OperatingSystem.IsWindows())
        {
            using WindowsCredentials cleanup = new();
            List<Exception> failures = [];
            foreach (string name in ownedWindowsNames)
            {
                try
                {
                    cleanup.Delete(name);
                }
                catch (CredentialException error) when (error.Failure == CredentialFailure.Absent)
                {
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            if (failures.Count != 0)
            {
                throw new AggregateException("Owned synthetic credential cleanup failed.", failures);
            }
        }
    }

    private sealed class TrackedWindows(OwnedIdentityKeychain owner) : INativeCredentials
    {
        private readonly WindowsCredentials inner = new();

        public void Create(string name, byte[] secret)
        {
            // Track before the effect, including uncertain native completion.
            owner.OwnWindowsName(name);
            inner.Create(name, secret);
        }

        public byte[] Read(string name) => inner.Read(name);
        public void Delete(string name) => inner.Delete(name);
        public void Dispose() => inner.Dispose();
    }
}

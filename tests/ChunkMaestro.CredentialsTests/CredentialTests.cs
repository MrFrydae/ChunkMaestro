using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using Xunit;

namespace ChunkMaestro.CredentialsTests;

public sealed class CredentialTests
{
    private static CredentialScope Scope => new(new FleetId(Guid.NewGuid()), Guid.NewGuid(), "integration-token");

    [Theory]
    [InlineData("INTEGRATION-TOKEN")]
    [InlineData("Integration-token")]
    public async Task NoncanonicalPurposeIsRefusedBeforeNativeAccess(string purpose)
    {
        Probe probe = new();
        using CredentialStore store = new(probe);
        CredentialScope canonical = Scope;
        CredentialReference reference = CredentialStore.Reserve(canonical);
        CredentialScope alias = canonical with
        {
            Purpose = purpose
        };
        CredentialReference aliasedReference = new(alias, reference.Id);

        Assert.Equal(CredentialFailure.WrongScope, Assert.Throws<CredentialException>(() => CredentialStore.Reserve(alias)).Failure);
        CredentialException create = await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(alias, aliasedReference, new byte[]
        {
            1
        }));
        Assert.Equal(CredentialFailure.WrongScope, create.Failure);
        Assert.Equal(CredentialCompletion.NotStarted, create.Completion);
        Assert.Equal(CredentialFailure.WrongScope, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(alias, aliasedReference, _ => throw new InvalidOperationException()))).Failure);
        Assert.Equal(CredentialFailure.WrongScope, (await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(alias, aliasedReference))).Failure);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task NativeLifecycleOrExplicitUnsupported()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Assert.Equal(CredentialFailure.Unsupported, Assert.Throws<CredentialException>(() => new CredentialStore()).Failure);
            return;
        }
        using NativeFixture fixture = new();
        CredentialScope scope = Scope;
        List<CredentialReference> owned = [];
        try
        {
            CredentialReference first;
            CredentialReference second;
            using (CredentialStore store = fixture.Open())
            {
                first = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
7, 2, 9
});
                owned.Add(first);
                second = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
4, 5
});
                owned.Add(second);
                Assert.NotEqual(first.Id, second.Id);
                CredentialScope wrong = scope with
                {
                    Purpose = "other"
                };
                Assert.Equal(CredentialFailure.WrongScope, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(wrong, first, _ => throw new InvalidOperationException()))).Failure);
                await store.UseAsync(scope, first, secret => Assert.True(secret.SequenceEqual(new byte[]
{
7, 2, 9
})));
            }
            using CredentialStore reopened = fixture.Open();
            await reopened.UseAsync(scope, first, secret => Assert.True(secret.SequenceEqual(new byte[]
{
7, 2, 9
})));
            await reopened.DeleteAsync(scope, first);
            owned.Remove(first);
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.UseAsync(scope, first, _ => throw new InvalidOperationException()))).Failure);
            await reopened.UseAsync(scope, second, secret => Assert.True(secret.SequenceEqual(new byte[]
{
4, 5
})));
        }
        finally
        {
            using CredentialStore cleanup = fixture.Open();
            foreach (CredentialReference reference in owned)
            {
                await cleanup.DeleteAsync(scope, reference);
            }
        }
    }

    [Fact]
    public void NativeCollisionPreservesExistingOrExplicitUnsupported()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Assert.Throws<CredentialException>(() => new CredentialStore());
            return;
        }
        using NativeFixture fixture = new();
        using INativeCredentials adapter = fixture.Adapter();
        string name = $"ChunkMaestro.credentials.v1/{Guid.NewGuid():N}/{Guid.NewGuid():N}/fixture/{Guid.NewGuid():N}";
        adapter.Create(name, [1, 2]);
        try
        {
            Assert.Equal(CredentialFailure.Conflict, Assert.Throws<CredentialException>(() => adapter.Create(name, [9])).Failure);
            byte[] read = adapter.Read(name);
            try
            {
                Assert.Equal(new byte[]
{
1, 2
}, read);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(read);
            }
        }
        finally
        {
            adapter.Delete(name);
        }
    }

    [Fact]
    public async Task MacNativeLockedFixtureRefusesWithoutPromptOrOtherPlatformContract()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Equal(CredentialFailure.Locked, MacCredentials.Map(-25308));
            return;
        }
        using NativeFixture fixture = new();
        using CredentialStore store = fixture.Open();
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
8
});
        fixture.Lock();
        Assert.Equal(CredentialFailure.Locked, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reference, _ => throw new InvalidOperationException()))).Failure);
        CredentialException create = await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
        {
            9
        }));
        Assert.Equal(CredentialFailure.Locked, create.Failure);
        Assert.Equal(CredentialCompletion.Unknown, create.Completion);
        Assert.Equal(CredentialFailure.Locked, (await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(scope, reference))).Failure);
    }

    [Fact]
    public async Task InjectedCallbackFailureClearsBufferAndReentryFails()
    {
        Probe probe = new();
        using CredentialStore store = new(probe);
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
1
});
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UseAsync(scope, reference, _ => store.Dispose()));
        Assert.All(probe.Last!, value => Assert.Equal(0, value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UseAsync(scope, reference, _ => throw new InvalidOperationException("Synthetic callback fault")));
        Assert.All(probe.Last!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task InjectedCancellationScopeAndDisposalNeverReachAdapter()
    {
        Probe probe = new();
        CredentialStore store = new(probe);
        CredentialScope scope = Scope;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
1
}, cancellation.Token));
        CredentialReference reference = new(scope, Guid.NewGuid());
        await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(scope with
        {
            Target = Guid.NewGuid()
        }, reference));
        Assert.Equal(0, probe.Calls);
        store.Dispose();
        store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
1
}));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task InjectedBuffersAreClearedAndDisposalWaitsForCallback()
    {
        Probe probe = new();
        using CredentialStore store = new(probe);
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
{
1, 2
});
        Assert.All(probe.Last!, value => Assert.Equal(0, value));
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task use = store.UseAsync(scope, reference, secret =>
        {
            Assert.Equal(9, secret[0]);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using CancellationTokenSource cancellation = new();
        Task waiting = store.DeleteAsync(scope, reference, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Task dispose = Task.Run(store.Dispose);
        Assert.False(dispose.IsCompleted);
        release.Set();
        await use;
        await dispose;
        Assert.All(probe.Last!, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(-25300, CredentialFailure.Absent)]
    [InlineData(-25293, CredentialFailure.Denied)]
    [InlineData(-25308, CredentialFailure.Locked)]
    [InlineData(-25291, CredentialFailure.Unavailable)]
    public void InjectedMacStatusesAreSanitized(int status, CredentialFailure expected)
    {
        Assert.Equal(expected, MacCredentials.Map(status));
        Assert.DoesNotContain(status.ToString(System.Globalization.CultureInfo.InvariantCulture), new CredentialException(expected).Message);
    }

    [Theory]
    [InlineData(1168, CredentialFailure.Absent)]
    [InlineData(5, CredentialFailure.Denied)]
    [InlineData(1312, CredentialFailure.Unavailable)]
    public void InjectedWindowsStatuses(int status, CredentialFailure expected)
    {
        Assert.Equal(expected, WindowsCredentials.Map(status));
    }

    [Fact]
    public async Task InjectedCancellationAfterNativeStartsReturnsCommittedReference()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Probe probe = new()
        {
            Entered = entered,
            Release = release
        };
        using CredentialStore store = new(probe);
        CredentialScope scope = Scope;
        CredentialReference reserved = CredentialStore.Reserve(scope);
        Assert.Equal(0, probe.Calls);
        using CancellationTokenSource cancellation = new();
        Task<CredentialReference> creating = store.CreateAsync(scope, reserved, new byte[]
        {
            1
        }, cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        cancellation.Cancel();
        release.Set();
        Assert.Equal(reserved, await creating);
        Assert.All(probe.Last!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task InjectedAmbiguousCreatePreservesReservedLocatorAndClearsBuffer()
    {
        Probe probe = new()
        {
            FailCreate = true
        };
        using CredentialStore store = new(probe);
        CredentialScope scope = Scope;
        CredentialReference reserved = CredentialStore.Reserve(scope);
        CredentialException error = await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, reserved, new byte[]
        {
            1
        }));
        Assert.Equal(CredentialCompletion.Unknown, error.Completion);
        Assert.NotEqual(Guid.Empty, reserved.Id);
        Assert.All(probe.Last!, value => Assert.Equal(0, value));
    }

    private sealed class Probe : INativeCredentials
    {
        public int Calls;
        public byte[]? Last;
        public ManualResetEventSlim? Entered;
        public ManualResetEventSlim? Release;
        public bool FailCreate;

        public void Create(string name, byte[] secret)
        {
            Calls++;
            Last = secret;
            Entered?.Set();
            if (Release is not null)
            {
                Assert.True(Release.Wait(TimeSpan.FromSeconds(10)));
            }
            if (FailCreate)
            {
                throw new CredentialException(CredentialFailure.Unavailable);
            }
        }

        public byte[] Read(string name)
        {
            Calls++;
            Last = [9];
            return Last;
        }

        public void Delete(string name)
        {
            Calls++;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task NativeSeparateProcessesContendForReservedReferenceOrUnsupported()
    {
        CredentialScope scope = Scope;
        CredentialReference reference = CredentialStore.Reserve(scope);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Assert.Equal(CredentialFailure.Unsupported, Assert.Throws<CredentialException>(() => new CredentialStore()).Failure);
            return;
        }
        using NativeFixture fixture = new();
        using CredentialStore store = fixture.Open();
        using System.Diagnostics.Process first = StartCreator("creator-a", fixture.KeychainPath, reference);
        using System.Diagnostics.Process second = StartCreator("creator-b", fixture.KeychainPath, reference);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            await Task.WhenAll(first.WaitForExitAsync(timeout.Token), second.WaitForExitAsync(timeout.Token));
            Assert.True((first.ExitCode == 0 && second.ExitCode == 20) || (first.ExitCode == 20 && second.ExitCode == 0));
            byte expected = first.ExitCode == 0 ? (byte) 1 : (byte) 2;
            await store.UseAsync(scope, reference, secret =>
            {
                Assert.Equal(1, secret.Length);
                Assert.Equal(expected, secret[0]);
            });
            using System.Diagnostics.Process retry = StartCreator("creator-b", fixture.KeychainPath, reference);
            await retry.WaitForExitAsync(timeout.Token);
            Assert.Equal(20, retry.ExitCode);
            await store.UseAsync(scope, reference, secret => Assert.Equal(expected, secret[0]));
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill(true);
                await first.WaitForExitAsync();
            }
            if (!second.HasExited)
            {
                second.Kill(true);
                await second.WaitForExitAsync();
            }
            try
            {
                await store.DeleteAsync(scope, reference);
            }
            catch (CredentialException error) when (error.Failure == CredentialFailure.Absent)
            {
                // Both child processes failed before creating; retain the original test failure.
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2560)]
    public async Task NativeExactReferenceRecoveryAndScopeIsolationOrUnsupported(int size)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Assert.Equal(CredentialFailure.Unsupported, Assert.Throws<CredentialException>(() => new CredentialStore()).Failure);
            return;
        }
        using NativeFixture fixture = new();
        CredentialScope scope = Scope with
        {
            Purpose = new string('a', 64)
        };
        CredentialReference reserved = CredentialStore.Reserve(scope);
        byte[] input = Enumerable.Repeat((byte) 47, size).ToArray();
        try
        {
            using (CredentialStore store = fixture.Open())
            {
                Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reserved, _ => Assert.Fail("Reservation created native data")))).Failure);
                Assert.Same(reserved, await store.CreateAsync(scope, reserved, input));
                CredentialException conflict = await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, reserved, new byte[]
                {
                    99
                }));
                Assert.Equal(CredentialFailure.Conflict, conflict.Failure);
                Assert.Equal(CredentialCompletion.NotCreated, conflict.Completion);
                await store.UseAsync(scope, reserved, secret => Assert.True(secret.SequenceEqual(input)));
            }
            // Reconstruct only from durable nonsecret components; no cached store state.
            CredentialScope recoveredScope = new(new FleetId(scope.Fleet.Value), scope.Target, scope.Purpose);
            CredentialReference recovered = new(recoveredScope, reserved.Id);
            using CredentialStore reopened = fixture.Open();
            foreach (CredentialScope other in new CredentialScope[]
            {
                scope with
                {
                    Fleet = new FleetId(Guid.NewGuid())
                },
                scope with
                {
                    Target = Guid.NewGuid()
                },
                scope with
                {
                    Purpose = "other"
                }
            })
            {
                CredentialException wrong = await Assert.ThrowsAsync<CredentialException>(() => reopened.UseAsync(other, recovered, _ => Assert.Fail("Mismatched scope reached callback")));
                Assert.Equal(CredentialFailure.WrongScope, wrong.Failure);
                CredentialReference otherReference = new(other, reserved.Id);
                Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.UseAsync(other, otherReference, _ => Assert.Fail("Native namespace aliased another scope")))).Failure);
                Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.DeleteAsync(other, otherReference))).Failure);
            }
            CredentialReference otherId = new(recoveredScope, Guid.NewGuid());
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.UseAsync(recoveredScope, otherId, _ => Assert.Fail("Native lookup ignored reference ID")))).Failure);
            await reopened.UseAsync(recoveredScope, recovered, secret => Assert.True(secret.SequenceEqual(input)));
            await reopened.DeleteAsync(recoveredScope, recovered);
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.DeleteAsync(recoveredScope, recovered))).Failure);
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.UseAsync(recoveredScope, recovered, _ => Assert.Fail("Deletion regenerated native secret")))).Failure);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(input);
            using CredentialStore cleanup = fixture.Open();
            try
            {
                await cleanup.DeleteAsync(scope, reserved);
            }
            catch (CredentialException error) when (error.Failure == CredentialFailure.Absent)
            {
                // Exact owned locator was already deleted, or creation never completed.
            }
        }
    }

    private static System.Diagnostics.Process StartCreator(string mode, string path, CredentialReference reference)
    {
        System.Diagnostics.ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
        {
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(CredentialTests).Assembly.Location);
        foreach (string argument in new string[]
        {
            mode, path, reference.Scope.Fleet.Value.ToString("D"), reference.Scope.Target.ToString("D"), reference.Scope.Purpose, reference.Id.ToString("D")
        })
        {
            start.ArgumentList.Add(argument);
        }
        return System.Diagnostics.Process.Start(start)!;
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly nint keychain;
        private readonly string? directory;

        public NativeFixture()
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

        public INativeCredentials Adapter()
        {
            return keychain != 0 ? new MacCredentials(keychain) : new WindowsCredentials();
        }

        public void Lock()
        {
            Assert.Equal(0, MacCredentials.SecKeychainLock(keychain));
        }

        public void Dispose()
        {
            if (keychain != 0)
            {
                int status = MacCredentials.SecKeychainDelete(keychain);
                MacCredentials.CFRelease(keychain);
                Assert.Equal(0, status);
                Directory.Delete(directory!, true);
            }
        }
    }
}

using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using Xunit;

namespace ChunkMaestro.CredentialsTests;

// Real owned Keychain effects with injected interaction/allocator faults. These
// do not claim natural OS restoration failures, global-flag changes or real OOM.
public sealed class MacNativeFailureTests
{
    private static CredentialScope Scope => new(new FleetId(Guid.NewGuid()), Guid.NewGuid(), "native-failure");

    [Fact]
    public async Task ReadRestorationFailureClearsUntransferredBuffer()
    {
        using CredentialTests.NativeFixture fixture = new();
        CredentialScope scope = Scope;
        CredentialReference reference;
        using (CredentialStore normal = fixture.Open())
        {
            reference = await normal.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
            {
                57, 81
            });
        }
        FaultResources resources = new()
        {
            FailRestore = true
        };
        using (CredentialStore failing = fixture.Open(resources))
        {
            CredentialException error = await Assert.ThrowsAsync<CredentialException>(() => failing.UseAsync(scope, reference, _ => Assert.Fail("Cleanup failure exposed a secret")));
            Assert.Equal(CredentialFailure.Unavailable, error.Failure);
            Assert.Equal("Credential operation failed: Unavailable.", error.Message);
        }
        Assert.NotNull(resources.LastRead);
        Assert.Equal(2, resources.LastRead.Length);
        Assert.All(resources.LastRead, value => Assert.Equal(0, value));
        Assert.Empty(resources.Owned);
        using CredentialStore recovered = fixture.Open();
        await recovered.UseAsync(scope, reference, secret => Assert.True(secret.SequenceEqual(new byte[]
        {
            57, 81
        })));
        await recovered.DeleteAsync(scope, reference);
    }

    [Fact]
    public async Task DeleteRestorationFailureReportsUnknownAndReconcilesAbsent()
    {
        using CredentialTests.NativeFixture fixture = new();
        CredentialScope scope = Scope;
        CredentialReference reference;
        using (CredentialStore normal = fixture.Open())
        {
            reference = await normal.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
            {
                63
            });
        }
        FaultResources resources = new()
        {
            FailRestore = true
        };
        using (CredentialStore failing = fixture.Open(resources))
        {
            CredentialException error = await Assert.ThrowsAsync<CredentialException>(() => failing.DeleteAsync(scope, reference));
            Assert.Equal(CredentialFailure.Unavailable, error.Failure);
            Assert.Equal(CredentialCompletion.Unknown, error.Completion);
        }
        Assert.Empty(resources.Owned);
        using CredentialStore recovered = fixture.Open();
        Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => recovered.UseAsync(scope, reference, _ => Assert.Fail("Deletion was replayed or replaced")))).Failure);
    }

    [Theory]
    [InlineData("dictionary")]
    [InlineData("account")]
    [InlineData("service")]
    [InlineData("search-list")]
    [InlineData("data")]
    public async Task NullNativeAllocationRefusesWithoutMutationAndReleasesEarlierHandles(string allocation)
    {
        using CredentialTests.NativeFixture fixture = new();
        CredentialScope scope = Scope;
        CredentialReference reference = CredentialStore.Reserve(scope);
        if (allocation == "search-list")
        {
            using CredentialStore normal = fixture.Open();
            await normal.CreateAsync(scope, reference, new byte[]
            {
                45
            });
        }
        FaultResources resources = new()
        {
            FailAllocation = allocation
        };
        using (CredentialStore failing = fixture.Open(resources))
        {
            CredentialException error = allocation == "search-list"
                ? await Assert.ThrowsAsync<CredentialException>(() => failing.UseAsync(scope, reference, _ => Assert.Fail("Allocation refusal exposed a secret")))
                : await Assert.ThrowsAsync<CredentialException>(() => failing.CreateAsync(scope, reference, new byte[]
                {
                    45
                }));
            Assert.Equal(CredentialFailure.Unavailable, error.Failure);
            Assert.Equal("Credential operation failed: Unavailable.", error.Message);
        }
        Assert.Empty(resources.Owned);
        Assert.Null(resources.LastRead);
        using CredentialStore recovered = fixture.Open();
        if (allocation == "search-list")
        {
            await recovered.UseAsync(scope, reference, secret => Assert.Equal(45, secret[0]));
            await recovered.DeleteAsync(scope, reference);
        }
        else
        {
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => recovered.UseAsync(scope, reference, _ => Assert.Fail("Allocation refusal created native data")))).Failure);
        }
    }

    private sealed class FaultResources : MacCredentials.NativeResources
    {
        private int sets;
        private int strings;
        internal bool FailRestore;
        internal string? FailAllocation;
        internal byte[]? LastRead;
        internal HashSet<nint> Owned = [];

        internal override int GetInteractionAllowed(out byte allowed)
        {
            // No actual process interaction state is modified by the fault fixture.
            allowed = 0;
            return 0;
        }

        internal override int SetInteractionAllowed(byte allowed)
        {
            sets++;
            return FailRestore && sets == 2 ? -25291 : 0;
        }

        private nint Track(nint handle)
        {
            Assert.NotEqual(0, handle);
            Assert.True(Owned.Add(handle));
            return handle;
        }

        internal override nint CreateDictionary()
        {
            return FailAllocation == "dictionary" ? 0 : Track(base.CreateDictionary());
        }

        internal override nint CreateString(byte[] utf8)
        {
            strings++;
            string stage = strings == 1 ? "account" : "service";
            return FailAllocation == stage ? 0 : Track(base.CreateString(utf8));
        }

        internal override nint CreateSearchList(nint handle)
        {
            return FailAllocation == "search-list" ? 0 : Track(base.CreateSearchList(handle));
        }

        internal override nint CreateData(nint bytes, int length)
        {
            return FailAllocation == "data" ? 0 : Track(base.CreateData(bytes, length));
        }

        internal override byte[] CopySecret(nint data)
        {
            Track(data);
            LastRead = base.CopySecret(data);
            return LastRead;
        }

        internal override void Release(nint handle)
        {
            Assert.NotEqual(0, handle);
            Assert.True(Owned.Remove(handle));
            base.Release(handle);
        }
    }
}

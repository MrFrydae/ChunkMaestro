using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.CredentialsTests;

[CollectionDefinition("Credential handoff contracts", DisableParallelization = true)]
public sealed class CredentialHandoffGroup
{
}

[Collection("Credential handoff contracts")]
public sealed class PurposeAndSerializationContractTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupBound = TimeSpan.FromSeconds(10);

    private static CredentialScope Scope(string purpose)
    {
        return new CredentialScope(new FleetId(Guid.NewGuid()), Guid.NewGuid(), purpose);
    }

    [Theory]
    [InlineData("z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-")]
    public async Task CanonicalPurposeRecoversExactLocatorAndIsolatesScopes(string purpose)
    {
        using RecordingAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope(purpose);
        CredentialReference reserved = CredentialStore.Reserve(scope);
        Assert.Empty(adapter.Effects);
        byte[] caller = [0, 9, 127, 255];
        Assert.Same(reserved, await store.CreateAsync(scope, reserved, caller).WaitAsync(Bound));
        Assert.NotSame(caller, adapter.LastWrite);
        AssertZero(adapter.LastWrite);
        Assert.Equal(new byte[]
        {
            0, 9, 127, 255
        }, caller);
        CredentialScope reconstructedScope = new(new FleetId(scope.Fleet.Value), scope.Target, new string(purpose.ToCharArray()));
        CredentialReference recovered = new(reconstructedScope, reserved.Id);
        await store.UseAsync(reconstructedScope, recovered, secret => Assert.True(secret.SequenceEqual(caller))).WaitAsync(Bound);
        AssertZero(adapter.LastRead);
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
                Purpose = purpose == "z" ? "0" : "z"
            }
        })
        {
            int before = adapter.Effects.Count;
            Assert.Equal(CredentialFailure.WrongScope, (await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(other, recovered))).Failure);
            Assert.Equal(before, adapter.Effects.Count);
            CredentialReference separate = new(other, reserved.Id);
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(other, separate, _ => Assert.Fail("Cross-scope read")))).Failure);
            await store.CreateAsync(other, separate, new byte[]
            {
                42
            }).WaitAsync(Bound);
            await store.DeleteAsync(other, separate).WaitAsync(Bound);
            await store.UseAsync(scope, recovered, secret => Assert.True(secret.SequenceEqual(caller))).WaitAsync(Bound);
        }
        Assert.Equal($"create:ChunkMaestro.credentials.v1/{scope.Fleet.Value:N}/{scope.Target:N}/{purpose}/{reserved.Id:N}", adapter.Effects[0]);
        await store.DeleteAsync(reconstructedScope, recovered).WaitAsync(Bound);
        Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reserved, _ => Assert.Fail("Deleted locator returned")))).Failure);
        Assert.Equal(new byte[]
        {
            0, 9, 127, 255
        }, caller);
    }

    [Theory]
    [InlineData("z09")]
    [InlineData("z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-z09-")]
    public async Task NativeStoreAcceptsCanonicalPurposeAcrossReopenOrRefusesUnsupported(string purpose)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Assert.Equal(CredentialFailure.Unsupported, Assert.Throws<CredentialException>(() => new CredentialStore()).Failure);
            return;
        }
        using CredentialTests.NativeFixture fixture = new();
        CredentialScope scope = Scope(purpose);
        CredentialReference reserved = CredentialStore.Reserve(scope);
        byte[] caller = [0, 9, 255];
        bool created = false;
        try
        {
            using (CredentialStore first = fixture.Open())
            {
                await first.CreateAsync(scope, reserved, caller).WaitAsync(Bound);
                created = true;
            }
            using CredentialStore reopened = fixture.Open();
            CredentialScope recoveredScope = new(new FleetId(scope.Fleet.Value), scope.Target, purpose);
            CredentialReference recovered = new(recoveredScope, reserved.Id);
            await reopened.UseAsync(recoveredScope, recovered, secret => Assert.True(secret.SequenceEqual(caller))).WaitAsync(Bound);
            await reopened.DeleteAsync(recoveredScope, recovered).WaitAsync(Bound);
            created = false;
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.UseAsync(scope, reserved, _ => Assert.Fail("Deleted native item returned")))).Failure);
            Assert.Equal(new byte[]
            {
                0, 9, 255
            }, caller);
        }
        finally
        {
            if (created)
            {
                using CredentialStore cleanup = fixture.Open();
                await cleanup.DeleteAsync(scope, reserved).WaitAsync(Bound);
            }
        }
    }

    [Theory]
    [InlineData("create-success")]
    [InlineData("create-exception")]
    [InlineData("read-exception")]
    [InlineData("callback-exception")]
    public async Task WaitingOperationRecoversAfterActiveOutcomeWithoutReplay(string outcome)
    {
        RecordingAdapter adapter = new();
        CredentialStore store = new(adapter);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CredentialScope scope = Scope("z09");
        CredentialReference reserved = CredentialStore.Reserve(scope);
        byte[] caller = [17, 0, 255];
        try
        {
            bool creating = outcome.StartsWith("create", StringComparison.Ordinal);
            if (!creating)
            {
                await store.CreateAsync(scope, reserved, caller).WaitAsync(Bound);
            }
            adapter.Enter = entered;
            adapter.Release = release;
            adapter.Fault = outcome.EndsWith("exception", StringComparison.Ordinal) && outcome != "callback-exception";
            Task active = creating ? store.CreateAsync(scope, reserved, caller) : store.UseAsync(scope, reserved, _ =>
            {
                if (outcome == "callback-exception")
                {
                    throw new InvalidOperationException("consumer failure");
                }
            });
            await entered.Task.WaitAsync(StartupBound);
            int before = adapter.Effects.Count;
            Task waiting = store.UseAsync(scope, new CredentialReference(scope, reserved.Id), secret => Assert.True(secret.SequenceEqual(caller)));
            Assert.False(waiting.IsCompleted);
            Assert.Equal(before, adapter.Effects.Count);
            release.Set();
            if (outcome is "create-exception" or "read-exception")
            {
                CredentialException error = await Assert.ThrowsAsync<CredentialException>(() => active.WaitAsync(Bound));
                Assert.Equal(CredentialFailure.Unavailable, error.Failure);
                Assert.Equal(creating ? CredentialCompletion.Unknown : CredentialCompletion.NotStarted, error.Completion);
            }
            else if (outcome == "callback-exception")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => active.WaitAsync(Bound));
            }
            else
            {
                await active.WaitAsync(Bound);
            }
            await waiting.WaitAsync(Bound);
            AssertZero(adapter.LastWrite);
            Assert.All(adapter.ReadBuffers, AssertZero);
            Assert.Equal(new byte[]
            {
                17, 0, 255
            }, caller);
            Assert.Equal(1, adapter.Effects.Count(effect => effect.StartsWith("create:", StringComparison.Ordinal)));
            Assert.Equal(creating ? 2 : 3, adapter.Effects.Count);
            await store.DeleteAsync(scope, reserved).WaitAsync(Bound);
            Assert.Empty(adapter.Items);
        }
        finally
        {
            release.Set();
            // An injected gate defect must fail boundedly, including cleanup.
            await Task.Run(store.Dispose).WaitAsync(Bound);
        }
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("use")]
    [InlineData("delete")]
    public async Task CanceledWaiterDoesNotConsumeHandoffOrReplay(string operation)
    {
        RecordingAdapter adapter = new();
        CredentialStore store = new(adapter);
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CredentialScope scope = Scope("z09");
        CredentialReference reserved = CredentialStore.Reserve(scope);
        CredentialReference canceledReference = CredentialStore.Reserve(scope);
        byte[] caller = [23, 0, 255];
        try
        {
            await store.CreateAsync(scope, reserved, caller).WaitAsync(Bound);
            adapter.Enter = entered;
            adapter.Release = release;
            Task active = store.UseAsync(scope, reserved, secret => Assert.True(secret.SequenceEqual(caller)));
            await entered.Task.WaitAsync(StartupBound);
            Task canceled = operation switch
            {
                "create" => store.CreateAsync(scope, canceledReference, caller, cancellation.Token),
                "use" => store.UseAsync(scope, reserved, _ => Assert.Fail("Canceled callback ran"), cancellation.Token),
                _ => store.DeleteAsync(scope, reserved, cancellation.Token)
            };
            Task successor = store.UseAsync(scope, reserved, secret => Assert.True(secret.SequenceEqual(caller)));
            Assert.False(canceled.IsCompleted);
            Assert.False(successor.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Bound));
            Assert.Equal(2, adapter.Effects.Count);
            release.Set();
            await active.WaitAsync(Bound);
            await successor.WaitAsync(Bound);
            Assert.Equal(3, adapter.Effects.Count);
            Assert.All(adapter.ReadBuffers, AssertZero);
            AssertZero(adapter.LastWrite);
            Assert.Equal(new byte[]
            {
                23, 0, 255
            }, caller);
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, canceledReference, _ => Assert.Fail("Canceled creation replayed")))).Failure);
            await store.DeleteAsync(scope, reserved).WaitAsync(Bound);
            Assert.Empty(adapter.Items);
        }
        finally
        {
            release.Set();
            await Task.Run(store.Dispose).WaitAsync(Bound);
        }
        Assert.Equal(1, adapter.Disposals);
    }

    private static void AssertZero(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        Assert.All(bytes, value => Assert.Equal(0, value));
    }

    private sealed class RecordingAdapter : INativeCredentials
    {
        public readonly Dictionary<string, byte[]> Items = [];
        public readonly List<string> Effects = [];
        public readonly List<byte[]> ReadBuffers = [];
        public byte[]? LastWrite;
        public TaskCompletionSource? Enter;
        public ManualResetEventSlim? Release;
        public bool Fault;
        public int Disposals;

        private void Dispatch(string operation, string name)
        {
            Effects.Add(operation + ":" + name);
            TaskCompletionSource? entered = Enter;
            Enter = null;
            if (entered is not null)
            {
                entered.SetResult();
                Assert.True(Release!.Wait(Bound));
            }
        }

        public void Create(string name, byte[] secret)
        {
            LastWrite = secret;
            Dispatch("create", name);
            Assert.True(Items.TryAdd(name, secret.ToArray()));
            FailOnce();
        }

        public byte[] Read(string name)
        {
            Dispatch("read", name);
            FailOnce();
            if (!Items.TryGetValue(name, out byte[]? bytes))
            {
                throw new CredentialException(CredentialFailure.Absent);
            }
            byte[] owned = bytes.ToArray();
            ReadBuffers.Add(owned);
            return owned;
        }

        private void FailOnce()
        {
            if (Fault)
            {
                Fault = false;
                throw new CredentialException(CredentialFailure.Unavailable);
            }
        }

        public byte[]? LastRead => ReadBuffers.LastOrDefault();

        public void Delete(string name)
        {
            Dispatch("delete", name);
            if (!Items.Remove(name, out byte[]? bytes))
            {
                throw new CredentialException(CredentialFailure.Absent);
            }
            CryptographicOperations.ZeroMemory(bytes);
        }

        public void Dispose()
        {
            Disposals++;
            foreach (byte[] bytes in Items.Values)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            Items.Clear();
        }
    }
}

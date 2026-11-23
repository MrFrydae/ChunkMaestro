using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using Xunit;

namespace ChunkMaestro.CredentialsTests;

// Stateful injected faults prove the trusted-core contract, not OS refusal behavior.
public sealed class FailureContractTests
{
    private static CredentialScope Scope => new(new FleetId(Guid.NewGuid()), Guid.NewGuid(), "integration-token");

    [Theory]
    [InlineData("fleet")]
    [InlineData("target")]
    [InlineData("purpose")]
    [InlineData("empty-reference")]
    public async Task MismatchedReferenceCannotReadReplaceOrDeleteOriginal(string mismatch)
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference original = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
        {
            42
        });
        CredentialScope other = mismatch switch
        {
            "fleet" => scope with
            {
                Fleet = new FleetId(Guid.NewGuid())
            },
            "target" => scope with
            {
                Target = Guid.NewGuid()
            },
            "purpose" => scope with
            {
                Purpose = "other"
            },
            _ => scope
        };
        CredentialReference reference = mismatch == "empty-reference" ? new(scope, Guid.Empty) : original;
        int before = adapter.Calls;
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(other, reference, new byte[]
        {
            99
        })), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(other, reference, _ => Assert.Fail("Rejected scope reached callback"))), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(other, reference)), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        Assert.Equal(before, adapter.Calls);
        await store.UseAsync(scope, original, secret => Assert.Equal(42, Assert.Single(secret.ToArray())));
    }

    [Theory]
    [InlineData("fleet")]
    [InlineData("target")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\0b")]
    [InlineData("tökén")]
    [InlineData("TOKEN")]
    [InlineData("too-long")]
    public async Task InvalidScopesCannotReserveOrDispatch(string invalid)
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        scope = invalid switch
        {
            "fleet" => scope with
            {
                Fleet = new FleetId(Guid.Empty)
            },
            "target" => scope with
            {
                Target = Guid.Empty
            },
            _ => scope with
            {
                Purpose = invalid == "null" ? null! : invalid == "too-long" ? new string('a', 65) : invalid
            }
        };
        CredentialReference reference = new(scope, Guid.NewGuid());
        AssertRefusal(Assert.Throws<CredentialException>(() => CredentialStore.Reserve(scope)), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, reference, new byte[]
        {
            1
        })), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reference, _ => Assert.Fail("Invalid scope reached callback"))), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(scope, reference)), CredentialFailure.WrongScope, CredentialCompletion.NotStarted);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task NullArgumentsRefuseWithoutDispatchAndStoreRemainsUsable()
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = CredentialStore.Reserve(scope);
        Assert.Throws<ArgumentNullException>(() => CredentialStore.Reserve(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CreateAsync(null!, reference, new byte[]
        {
            1
        }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CreateAsync(scope, null!, new byte[]
        {
            1
        }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UseAsync(scope, null!, _ => Assert.Fail("Null reference reached callback")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UseAsync(scope, reference, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.DeleteAsync(scope, null!));
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(reference, await store.CreateAsync(scope, reference, new byte[]
        {
            7
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2561)]
    public async Task InvalidPayloadSizesAreRefusedWithoutCreating(int size)
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = CredentialStore.Reserve(scope);
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(scope, reference, new byte[size]));
        Assert.Equal("secret", error.ParamName);
        Assert.Equal(0, adapter.Calls);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reference, _ => Assert.Fail("Refused create persisted"))), CredentialFailure.Absent, CredentialCompletion.NotStarted);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2560, 64)]
    public async Task InclusiveSizeAndPurposeBoundariesRoundTripWithoutClearingCaller(int size, int purposeLength)
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope with
        {
            Purpose = new string('a', purposeLength)
        };
        byte[] input = Enumerable.Repeat((byte) 37, size).ToArray();
        CredentialReference reserved = CredentialStore.Reserve(scope);
        Assert.Equal(0, adapter.Calls);
        Assert.Same(reserved, await store.CreateAsync(scope, reserved, input));
        Assert.NotSame(input, adapter.LastWrite);
        AssertCleared(adapter.LastWrite);
        Assert.All(input, value => Assert.Equal(37, value));
        await store.UseAsync(scope, reserved, secret => Assert.True(secret.SequenceEqual(input)));
        AssertCleared(adapter.LastRead);
        await store.DeleteAsync(scope, reserved);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reserved, _ => Assert.Fail("Deleted item returned"))), CredentialFailure.Absent, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(scope, reserved)), CredentialFailure.Absent, CredentialCompletion.NotStarted);
    }

    [Theory]
    [InlineData(CredentialFailure.Denied, false)]
    [InlineData(CredentialFailure.Locked, false)]
    [InlineData(CredentialFailure.Unavailable, false)]
    [InlineData(CredentialFailure.Unavailable, true)]
    public async Task AmbiguousCreateRecoversOnlyAtExactReservedReference(CredentialFailure failure, bool committed)
    {
        MemoryAdapter adapter = new()
        {
            CreateFault = new CredentialException(failure),
            CommitBeforeFault = committed
        };
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = CredentialStore.Reserve(scope);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, reference, new byte[]
        {
            73
        })), failure, CredentialCompletion.Unknown);
        Assert.Equal(1, adapter.Calls);
        AssertCleared(adapter.LastWrite);
        adapter.CreateFault = null;
        if (committed)
        {
            await store.UseAsync(scope, reference, secret => Assert.Equal(73, Assert.Single(secret.ToArray())));
            AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, reference, new byte[]
            {
                99
            })), CredentialFailure.Conflict, CredentialCompletion.NotCreated);
            AssertCleared(adapter.LastWrite);
            await store.UseAsync(scope, reference, secret => Assert.Equal(73, Assert.Single(secret.ToArray())));
        }
        else
        {
            AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reference, _ => Assert.Fail("Failure regenerated a secret"))), CredentialFailure.Absent, CredentialCompletion.NotStarted);
            Assert.Equal(reference, await store.CreateAsync(scope, reference, new byte[]
            {
                73
            }));
        }
        CredentialReference unrelated = CredentialStore.Reserve(scope);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, unrelated, _ => Assert.Fail("Recovery searched another locator"))), CredentialFailure.Absent, CredentialCompletion.NotStarted);
        await store.DeleteAsync(scope, reference);
    }

    [Fact]
    public async Task UnauthorizedCreateIsSanitizedUnknownAndClearsOwnedCopy()
    {
        MemoryAdapter adapter = new()
        {
            CreateFault = new UnauthorizedAccessException("synthetic native detail must not escape")
        };
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        byte[] input = [19];
        CredentialException error = await Assert.ThrowsAsync<CredentialException>(() => store.CreateAsync(scope, CredentialStore.Reserve(scope), input));
        AssertRefusal(error, CredentialFailure.Denied, CredentialCompletion.Unknown);
        AssertCleared(adapter.LastWrite);
        Assert.Equal(19, input[0]);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("use")]
    [InlineData("delete")]
    public async Task CancellationBeforeDispatchAndWhileQueuedHasNoSideEffect(string operation)
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
        {
            61
        });
        using CancellationTokenSource early = new();
        early.Cancel();
        int calls = adapter.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(store, operation, scope, reference, early.Token));
        Assert.Equal(calls, adapter.Calls);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task active = store.UseAsync(scope, reference, _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            calls = adapter.Calls;
            using CancellationTokenSource queued = new();
            Task waiting = Invoke(store, operation, scope, reference, queued.Token);
            Assert.False(waiting.IsCompleted);
            queued.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(calls, adapter.Calls);
        }
        finally
        {
            release.Set();
            await active;
        }
        await store.UseAsync(scope, reference, secret => Assert.Equal(61, Assert.Single(secret.ToArray())));
        // Also catches a canceled create that runs later after the gate opens.
        Assert.Equal(calls + 1, adapter.Calls);
    }

    [Fact]
    public async Task CallbackFailuresRejectEveryReentryAndLeaveStoreUsable()
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
        {
            8
        });
        InvalidOperationException original = new("synthetic consumer failure");
        List<Task> rejected = [];
        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UseAsync(scope, reference, _ =>
        {
            Assert.Throws<InvalidOperationException>(store.Dispose);
            rejected.Add(store.CreateAsync(scope, reference, new byte[]
            {
                9
            }));
            rejected.Add(store.UseAsync(scope, reference, _ => Assert.Fail("Nested callback ran")));
            rejected.Add(store.DeleteAsync(scope, reference));
            throw original;
        }));
        Assert.Same(original, observed);
        foreach (Task task in rejected)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
        AssertCleared(adapter.LastRead);
        Assert.Equal(2, adapter.Calls);
        await store.UseAsync(scope, reference, secret => Assert.Equal(8, Assert.Single(secret.ToArray())));
        AssertCleared(adapter.LastRead);
        await store.DeleteAsync(scope, reference);
    }

    [Fact]
    public async Task DisposeWaitsForActiveCallbackReleasesAdapterOnceAndRefusesAllOperations()
    {
        MemoryAdapter adapter = new();
        CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
        {
            5
        });
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim disposing = new();
        Task active = store.UseAsync(scope, reference, _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, adapter.DisposeCalls);
        });
        Task disposal = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            disposal = Task.Run(() =>
            {
                disposing.Set();
                store.Dispose();
            });
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(10)));
            // Observe a bounded interval with the callback deliberately blocked.
            await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, adapter.DisposeCalls);
        }
        finally
        {
            release.Set();
            await active;
            await disposal;
            store.Dispose();
        }
        AssertCleared(adapter.LastRead);
        Assert.Equal(1, adapter.DisposeCalls);
        foreach (string operation in new string[]
        {
            "create", "use", "delete"
        })
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Invoke(store, operation, scope, reference));
        }
        Assert.Equal(2, adapter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterCreateDispatchDoesNotHideNativeOutcome(bool failAfterCommit)
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        MemoryAdapter adapter = new()
        {
            CreateEntered = entered,
            CreateRelease = release,
            CreateFault = failAfterCommit ? new CredentialException(CredentialFailure.Unavailable) : null,
            CommitBeforeFault = true
        };
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reserved = CredentialStore.Reserve(scope);
        using CancellationTokenSource cancellation = new();
        byte[] input = [21];
        Task<CredentialReference> creating = store.CreateAsync(scope, reserved, input, cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            Assert.False(creating.IsCompleted);
            Assert.NotSame(input, adapter.LastWrite);
            Assert.Equal(21, adapter.LastWrite![0]);
        }
        finally
        {
            release.Set();
        }
        if (failAfterCommit)
        {
            AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => creating), CredentialFailure.Unavailable, CredentialCompletion.Unknown);
        }
        else
        {
            Assert.Same(reserved, await creating);
        }
        AssertCleared(adapter.LastWrite);
        Assert.Equal(21, input[0]);
        await store.UseAsync(scope, reserved, secret => Assert.Equal(21, Assert.Single(secret.ToArray())));
        await store.DeleteAsync(scope, reserved);
    }

    [Theory]
    [InlineData(CredentialFailure.Denied)]
    [InlineData(CredentialFailure.Locked)]
    [InlineData(CredentialFailure.Unavailable)]
    public async Task ReadAndDeleteRefusalsDoNotInvokeConsumerOrPoisonRecovery(CredentialFailure failure)
    {
        MemoryAdapter adapter = new();
        using CredentialStore store = new(adapter);
        CredentialScope scope = Scope;
        CredentialReference reference = await store.CreateAsync(scope, CredentialStore.Reserve(scope), new byte[]
        {
            31
        });
        adapter.AccessFault = new CredentialException(failure);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reference, _ => Assert.Fail("Native read failure invoked consumer"))), failure, CredentialCompletion.NotStarted);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.DeleteAsync(scope, reference)), failure, CredentialCompletion.NotStarted);
        adapter.AccessFault = null;
        await store.UseAsync(scope, reference, secret => Assert.Equal(31, Assert.Single(secret.ToArray())));
        await store.DeleteAsync(scope, reference);
        AssertRefusal(await Assert.ThrowsAsync<CredentialException>(() => store.UseAsync(scope, reference, _ => Assert.Fail("Deleted item returned"))), CredentialFailure.Absent, CredentialCompletion.NotStarted);
    }

    private static Task Invoke(CredentialStore store, string operation, CredentialScope scope, CredentialReference reference, CancellationToken cancellation = default)
    {
        return operation switch
        {
            "create" => store.CreateAsync(scope, reference, new byte[]
            {
                99
            }, cancellation),
            "use" => store.UseAsync(scope, reference, _ => Assert.Fail("Refused use reached callback"), cancellation),
            _ => store.DeleteAsync(scope, reference, cancellation)
        };
    }

    private static void AssertCleared(byte[]? buffer)
    {
        Assert.NotNull(buffer);
        Assert.NotEmpty(buffer);
        Assert.All(buffer, value => Assert.Equal(0, value));
    }

    private static void AssertRefusal(CredentialException error, CredentialFailure failure, CredentialCompletion completion)
    {
        Assert.Equal(failure, error.Failure);
        Assert.Equal(completion, error.Completion);
        Assert.Equal($"Credential operation failed: {failure}.", error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class MemoryAdapter : INativeCredentials
    {
        private readonly Dictionary<string, byte[]> items = [];
        public int Calls;
        public int DisposeCalls;
        public byte[]? LastWrite;
        public byte[]? LastRead;
        public Exception? CreateFault;
        public bool CommitBeforeFault;
        public CredentialException? AccessFault;
        public ManualResetEventSlim? CreateEntered;
        public ManualResetEventSlim? CreateRelease;

        public void Create(string name, byte[] secret)
        {
            Calls++;
            LastWrite = secret;
            CreateEntered?.Set();
            if (CreateRelease is not null)
            {
                Assert.True(CreateRelease.Wait(TimeSpan.FromSeconds(10)));
            }
            if (CreateFault is not null && !CommitBeforeFault)
            {
                throw CreateFault;
            }
            if (!items.TryAdd(name, secret.ToArray()))
            {
                throw new CredentialException(CredentialFailure.Conflict);
            }
            if (CreateFault is not null)
            {
                throw CreateFault;
            }
        }

        public byte[] Read(string name)
        {
            Calls++;
            if (AccessFault is not null)
            {
                throw AccessFault;
            }
            if (!items.TryGetValue(name, out byte[]? value))
            {
                throw new CredentialException(CredentialFailure.Absent);
            }
            LastRead = value.ToArray();
            return LastRead;
        }

        public void Delete(string name)
        {
            Calls++;
            if (AccessFault is not null)
            {
                throw AccessFault;
            }
            if (!items.Remove(name, out byte[]? value))
            {
                throw new CredentialException(CredentialFailure.Absent);
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
        }

        public void Dispose()
        {
            DisposeCalls++;
            foreach (byte[] value in items.Values)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
            }
            items.Clear();
        }
    }
}

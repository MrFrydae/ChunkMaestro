using ChunkMaestro.Application;
using ChunkMaestro.ControllerIdentity;
using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class ControllerIdentityLifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "cm-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly Guid fleet = Guid.NewGuid();
    private readonly Guid controller = Guid.NewGuid();
    private readonly Clock clock = new();
    private StorageLocation Location => StorageLocation.ForTests(directory);

    [Fact]
    public async Task CreateReconstructReopenAndNativeDeletionNeverGrantsAuthorityFromReady()
    {
        using OwnedIdentityKeychain keychain = new();
        RootIdentityLocator locator;
        await using (LocalFleetStore store = await Seed())
        {
            using CredentialStore credentials = keychain.Open();
            locator = await store.CreateRootIdentityAsync(fleet, controller, credentials, clock);
            Assert.Equal(ControllerIdentityRow.Ready, (await store.ReadIdentityMetadataAsync(fleet))!.State);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        using CredentialStore native = keychain.Open();
        RootIdentityLocator reconstructed = new(locator.Fleet, locator.Controller, locator.Attempt, locator.Credential);
        Assert.Equal(locator, await reopened.ReopenRootIdentityAsync(reconstructed, native, clock));
        Assert.Equal(locator, await reopened.ResumeRootIdentityAsync(reconstructed, native, clock));
        ControllerIdentityRow row = (await reopened.ReadIdentityMetadataAsync(fleet))!;
        await native.DeleteAsync(row.Reference.Scope, row.Reference);
        CredentialException absent = await Assert.ThrowsAsync<CredentialException>(() => reopened.ReopenRootIdentityAsync(locator, native, clock));
        Assert.Equal(CredentialFailure.Absent, absent.Failure);
        Assert.Equal(ControllerIdentityRow.Ready, (await reopened.ReadIdentityMetadataAsync(fleet))!.State);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("matching")]
    [InlineData("wrong-key")]
    [InlineData("wrong-attempt")]
    [InlineData("trailing")]
    [InlineData("expired")]
    [InlineData("wrong-locator")]
    [InlineData("wrong-scope")]
    public async Task PendingResumeOnlyAcceptsExactExistingCurrentMaterial(string mode)
    {
        using OwnedIdentityKeychain keychain = new();
        using CredentialStore credentials = keychain.Open();
        await using LocalFleetStore store = await Seed();
        using RootMaterial material = RootMaterial.Generate(fleet, controller, clock);
        ControllerIdentityRow row = Row(material);
        await store.BeginIdentityMetadataAsync(row);
        byte[] envelope = RootEnvelope.Encode(material, fleet, controller, row.AttemptId, row.CredentialId, row.CertificateHash, row.SpkiHash);
        try
        {
            switch (mode)
            {
                case "wrong-key":
                    using (RootMaterial wrong = RootMaterial.Generate(fleet, controller, clock))
                    {
                        CryptographicOperations.ZeroMemory(envelope);
                        envelope = RootEnvelope.Encode(wrong, fleet, controller, row.AttemptId, row.CredentialId, row.CertificateHash, row.SpkiHash);
                    }
                    break;
                case "wrong-attempt":
                    envelope[44] ^= 1;
                    break;
                case "wrong-locator":
                    envelope[60] ^= 1;
                    break;
                case "wrong-scope":
                    envelope[12] ^= 1;
                    break;
                case "trailing":
                    byte[] appended = new byte[envelope.Length + 1];
                    envelope.CopyTo(appended, 0);
                    CryptographicOperations.ZeroMemory(envelope);
                    envelope = appended;
                    break;
                case "expired":
                    clock.Now = clock.Now.AddYears(5);
                    break;
            }
            if (mode != "absent")
            {
                await credentials.CreateAsync(row.Reference.Scope, row.Reference, envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
        RootIdentityLocator locator = new(fleet, controller, row.AttemptId, row.CredentialId);
        if (mode == "matching")
        {
            Assert.Equal(locator, await store.ResumeRootIdentityAsync(locator, credentials, clock));
            Assert.Equal(ControllerIdentityRow.Ready, (await store.ReadIdentityMetadataAsync(fleet))!.State);
        }
        else
        {
            if (mode == "absent")
            {
                Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => store.ResumeRootIdentityAsync(locator, credentials, clock))).Failure);
            }
            else
            {
                await Assert.ThrowsAsync<CryptographicException>(() => store.ResumeRootIdentityAsync(locator, credentials, clock));
            }
            ControllerIdentityRow preserved = (await store.ReadIdentityMetadataAsync(fleet))!;
            Assert.Equal(row.AttemptId, preserved.AttemptId);
            Assert.Equal(row.CertificateDer, preserved.CertificateDer);
            Assert.Equal(ControllerIdentityRow.Pending, preserved.State);
        }
    }

    [Fact]
    public async Task UncertainNativeCreatePreservesExactAttemptAndClearsBorrowedWriteBuffer()
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        adapter.AfterCreate = () => throw new CredentialException(CredentialFailure.Unavailable);
        CredentialException error = await Assert.ThrowsAsync<CredentialException>(() => store.CreateRootIdentityAsync(fleet, controller, credentials, clock));
        Assert.Equal(CredentialCompletion.Unknown, error.Completion);
        Assert.All(adapter.Borrowed!, value => Assert.Equal(0, value));
        ControllerIdentityRow pending = (await store.ReadIdentityMetadataAsync(fleet))!;
        Assert.Equal(ControllerIdentityRow.Pending, pending.State);
        RootIdentityLocator locator = new(fleet, controller, pending.AttemptId, pending.CredentialId);
        Assert.Equal(locator, await store.ResumeRootIdentityAsync(locator, credentials, clock));
        Assert.Equal(1, adapter.Creates);
    }

    [Fact]
    public async Task CancellationAfterNativeCreatePreservesItemAndDisposalWaitsForNativeEffect()
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        LocalFleetStore store = await Seed();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        adapter.AfterCreate = () =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(20));
            cancellation.Cancel();
        };
        Task<RootIdentityLocator> create = store.CreateRootIdentityAsync(fleet, controller, credentials, clock, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Task dispose = store.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        await dispose;
        Assert.All(adapter.Borrowed!, value => Assert.Equal(0, value));
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        ControllerIdentityRow pending = (await reopened.ReadIdentityMetadataAsync(fleet))!;
        Assert.Equal(ControllerIdentityRow.Pending, pending.State);
        await reopened.ResumeRootIdentityAsync(new(fleet, controller, pending.AttemptId, pending.CredentialId), credentials, clock);
    }

    [Fact]
    public async Task DuplicateInitializationAndPrecancelNeverWriteNativeOrReplaceOriginal()
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateRootIdentityAsync(fleet, controller, credentials, clock, cancellation.Token));
        Assert.Equal(0, adapter.Creates);
        RootIdentityLocator original = await store.CreateRootIdentityAsync(fleet, controller, credentials, clock);
        await Assert.ThrowsAsync<FleetStorageException>(() => store.CreateRootIdentityAsync(fleet, controller, credentials, clock));
        Assert.Equal(1, adapter.Creates);
        Assert.Equal(original, await store.ReopenRootIdentityAsync(original, credentials, clock));
        await Assert.ThrowsAsync<FleetStorageException>(() => store.ReopenRootIdentityAsync(original with
        {
            Attempt = Guid.NewGuid()
        }, credentials, clock));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("native")]
    [InlineData("ready")]
    public async Task KilledProcessRetainsDurableAttemptWithoutMutationReplay(string phase)
    {
        using OwnedIdentityKeychain keychain = new();
        System.Diagnostics.ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new string[]
        {
            typeof(ControllerIdentityLifecycleTests).Assembly.Location, "--identity-phase", directory,
            keychain.KeychainPath, fleet.ToString("D"), controller.ToString("D"), phase
        })
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.Equal("DURABLE", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        // Observe only native lock release after death before reopening SQLite.
        await StorageTests.WaitForControllerReleaseAsync(directory);
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        ControllerIdentityRow row = (await reopened.ReadIdentityMetadataAsync(fleet))!;
        keychain.OwnWorkerReference(row.Reference);
        Assert.Equal(phase == "ready" ? ControllerIdentityRow.Ready : ControllerIdentityRow.Pending, row.State);
        using CredentialStore native = keychain.Open();
        RootIdentityLocator locator = new(fleet, controller, row.AttemptId, row.CredentialId);
        if (phase == "pending")
        {
            Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => reopened.ResumeRootIdentityAsync(locator, native, clock))).Failure);
        }
        else
        {
            Assert.Equal(locator, await reopened.ResumeRootIdentityAsync(locator, native, clock));
            Assert.Equal(locator, await reopened.ReopenRootIdentityAsync(locator, native, clock));
        }
    }

    // Actual process death at injected native adapter boundaries; not random commit interruption.
    internal static async Task<int> RunPhaseWorker(string[] args)
    {
        Guid workerFleet = Guid.Parse(args[3]);
        Guid workerController = Guid.Parse(args[4]);
        string phase = args[5];
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(args[1]));
        await store.RegisterFleetAsync(new FleetId(workerFleet), "Interrupted fixture");
        FaultNative adapter = new(OperatingSystem.IsMacOS() ? MacCredentials.OpenFixture(args[2]) : new WindowsCredentials());
        if (phase == "pending")
        {
            adapter.BeforeCreate = Pause;
        }
        if (phase == "native")
        {
            adapter.AfterCreate = Pause;
        }
        using CredentialStore credentials = new(adapter);
        await store.CreateRootIdentityAsync(workerFleet, workerController, credentials, new Clock());
        Pause();
        return 0;
    }

    private static void Pause()
    {
        Console.WriteLine("DURABLE");
        Console.Out.Flush();
        Console.In.ReadLine();
    }

    [Fact]
    public async Task PublicationRefusalPreservesNativeItemThenExplicitResumeReconciles()
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        await ExecuteSql("CREATE TRIGGER lifecycle_fault AFTER UPDATE ON ControllerIdentities BEGIN SELECT RAISE(ABORT, 'synthetic fault'); END");
        await Assert.ThrowsAsync<FleetStorageException>(() => store.CreateRootIdentityAsync(fleet, controller, credentials, clock));
        ControllerIdentityRow row = (await store.ReadIdentityMetadataAsync(fleet))!;
        Assert.Equal(ControllerIdentityRow.Pending, row.State);
        await ExecuteSql("DROP TRIGGER lifecycle_fault");
        RootIdentityLocator locator = new(fleet, controller, row.AttemptId, row.CredentialId);
        await store.ResumeRootIdentityAsync(locator, credentials, clock);
        long? published = (await store.ReadIdentityMetadataAsync(fleet))!.PublishedAt;
        await store.ResumeRootIdentityAsync(locator, credentials, clock);
        Assert.Equal(published, (await store.ReadIdentityMetadataAsync(fleet))!.PublishedAt);
        Assert.Equal(1, adapter.Creates);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("backwards")]
    [InlineData("expired")]
    public async Task ReadyReopenRechecksNativeAndCurrentClock(string mode)
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        RootIdentityLocator locator = await store.CreateRootIdentityAsync(fleet, controller, credentials, clock);
        if (mode == "denied")
        {
            adapter.BeforeRead = () => throw new CredentialException(CredentialFailure.Denied);
            Assert.Equal(CredentialFailure.Denied, (await Assert.ThrowsAsync<CredentialException>(() => store.ReopenRootIdentityAsync(locator, credentials, clock))).Failure);
        }
        else
        {
            clock.Now = mode == "backwards" ? clock.Now.AddSeconds(-1) : clock.Now.AddYears(5);
            await Assert.ThrowsAsync<CryptographicException>(() => store.ReopenRootIdentityAsync(locator, credentials, clock));
        }
        Assert.Equal(1, adapter.Creates);
        Assert.Equal(ControllerIdentityRow.Ready, (await store.ReadIdentityMetadataAsync(fleet))!.State);
    }

    private async Task ExecuteSql(string sql)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={Path.Combine(directory, "fleet.db")};Pooling=False");
        await connection.OpenAsync();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ConcurrentInitializationCreatesOnlyOneNativeIdentity()
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        Task<RootIdentityLocator> first = store.CreateRootIdentityAsync(fleet, controller, credentials, clock);
        Task<RootIdentityLocator> second = store.CreateRootIdentityAsync(fleet, controller, credentials, clock);
        await Assert.ThrowsAsync<FleetStorageException>(() => Task.WhenAll(first, second));
        Assert.Equal(1, adapter.Creates);
        RootIdentityLocator winner = first.IsCompletedSuccessfully ? await first : await second;
        Assert.Equal(winner, await store.ReopenRootIdentityAsync(winner, credentials, clock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockChangeDuringNativeReadRefusesPublicationAndClearsReadBuffer(bool expired)
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        adapter.BeforeRead = () => clock.Now = expired ? clock.Now.AddYears(5) : clock.Now.AddSeconds(-1);
        await Assert.ThrowsAsync<CryptographicException>(() => store.CreateRootIdentityAsync(fleet, controller, credentials, clock));
        Assert.All(adapter.BorrowedRead!, value => Assert.Equal(0, value));
        ControllerIdentityRow row = (await store.ReadIdentityMetadataAsync(fleet))!;
        Assert.Equal(ControllerIdentityRow.Pending, row.State);
        Assert.Equal(1, adapter.Creates);
    }

    [Fact]
    public async Task NativeDeniedBeforeEffectLeavesPendingAndExplicitResumeDoesNotRetryCreate()
    {
        using OwnedIdentityKeychain keychain = new();
        FaultNative adapter = new(keychain.Adapter());
        using CredentialStore credentials = new(adapter);
        await using LocalFleetStore store = await Seed();
        adapter.BeforeCreate = () => throw new CredentialException(CredentialFailure.Denied);
        await Assert.ThrowsAsync<CredentialException>(() => store.CreateRootIdentityAsync(fleet, controller, credentials, clock));
        Assert.All(adapter.Borrowed!, value => Assert.Equal(0, value));
        ControllerIdentityRow row = (await store.ReadIdentityMetadataAsync(fleet))!;
        Assert.Equal(ControllerIdentityRow.Pending, row.State);
        Assert.Equal(CredentialFailure.Absent, (await Assert.ThrowsAsync<CredentialException>(() => store.ResumeRootIdentityAsync(new(fleet, controller, row.AttemptId, row.CredentialId), credentials, clock))).Failure);
        Assert.Equal(1, adapter.Creates);
    }

    private async Task<LocalFleetStore> Seed()
    {
        LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await store.RegisterFleetAsync(new FleetId(fleet), "Lifecycle fixture");
        return store;
    }

    private ControllerIdentityRow Row(RootMaterial material)
    {
        byte[] der = material.GetPublicCertificate();
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(der);
        using ECDsa key = certificate.GetECDsaPublicKey()!;
        return new ControllerIdentityRow
        {
            FleetId = fleet,
            ControllerId = controller,
            AttemptId = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(),
            ProfileVersion = 1,
            Purpose = ControllerIdentityRow.RootPurpose,
            CertificateDer = der,
            CertificateHash = SHA256.HashData(der),
            SpkiHash = SHA256.HashData(key.ExportSubjectPublicKeyInfo()),
            CreatedAt = clock.Now.ToUnixTimeMilliseconds()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FaultNative(INativeCredentials inner) : INativeCredentials
    {
        internal Action? BeforeCreate;
        internal Action? AfterCreate;
        internal Action? BeforeRead;
        internal byte[]? Borrowed;
        internal byte[]? BorrowedRead;
        internal int Creates;
        public void Create(string name, byte[] secret)
        {
            Creates++;
            Borrowed = secret;
            BeforeCreate?.Invoke();
            inner.Create(name, secret);
            AfterCreate?.Invoke();
        }
        public byte[] Read(string name)
        {
            BeforeRead?.Invoke();
            BorrowedRead = inner.Read(name);
            return BorrowedRead;
        }
        public void Delete(string name) => inner.Delete(name);
        public void Dispose() => inner.Dispose();
    }
}

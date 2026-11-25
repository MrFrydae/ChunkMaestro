using ChunkMaestro.PrivilegedHelper;
using System.Runtime.InteropServices;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class SessionEscrowBridgeTests
{
    private const string Epoch = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void SnapshotAndEscrowShareOneResolverUnderConcurrentInitialization()
    {
        Parallel.For(0, 32, index =>
        {
            if (index % 2 == 0)
            {
                Assert.NotNull(SessionEscrowNative.Instance);
            }
            else
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PolicySnapshotNative).TypeHandle);
            }
            InspectorNativeLibrary.Initialize();
        });
    }

    [Fact]
    public void LayoutMatchesNativeLp64IncludingIdentityArrayStride()
    {
        Assert.Equal(24, Marshal.SizeOf<SessionEscrowIdentityUnqualified>());
        Assert.Equal(0, Marshal.OffsetOf<SessionEscrowIdentityUnqualified>("Device").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<SessionEscrowIdentityUnqualified>("Inode").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<SessionEscrowIdentityUnqualified>("Flags").ToInt32());
        Assert.Equal(8, Marshal.SizeOf<SessionEscrowObservationUnqualified>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef\0")]
    public void InvalidEpochNeverCallsNative(string epoch)
    {
        FakeAbi abi = new();
        using BorrowedDescriptor worker = new(3);
        using BorrowedDescriptor agent = new(4);
        Assert.Throws<ArgumentException>(() => SessionEscrowBridge.BindUnqualified(worker, agent, epoch, abi, TestContext.Current.CancellationToken));
        Assert.Equal(0, abi.BindCalls);
    }

    [Fact]
    public void BindPinsBorrowedHandlesUntilNativeDuplicationReturns()
    {
        FakeAbi abi = new();
        BorrowedDescriptor worker = new(3);
        BorrowedDescriptor agent = new(4);
        abi.DuringBind = () =>
        {
            worker.Dispose();
            agent.Dispose();
            Assert.Equal(0, worker.Releases);
            Assert.Equal(0, agent.Releases);
        };
        using SessionEscrowBridge owner = SessionEscrowBridge.BindUnqualified(worker, agent, Epoch, abi, TestContext.Current.CancellationToken);
        Assert.Equal(1, worker.Releases);
        Assert.Equal(1, agent.Releases);
        Assert.Equal(33, abi.EpochBytes!.Length);
        Assert.Equal(0, abi.EpochBytes[32]);
        Assert.Equal(2, owner.MetadataUnqualified(TestContext.Current.CancellationToken).Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void BindFailureClosesUnexpectedReturnedPointerOnce(int status)
    {
        FakeAbi abi = new()
        {
            BindStatus = status
        };
        using BorrowedDescriptor worker = new(3);
        using BorrowedDescriptor agent = new(4);
        Assert.Throws<InvalidOperationException>(() => SessionEscrowBridge.BindUnqualified(worker, agent, Epoch, abi, TestContext.Current.CancellationToken));
        Assert.Equal(1, abi.CloseCalls);
    }

    [Fact]
    public void ExceptionAfterOutputAssignmentStillClosesOnce()
    {
        FakeAbi abi = new()
        {
            ThrowBind = true
        };
        using BorrowedDescriptor worker = new(3);
        using BorrowedDescriptor agent = new(4);
        Assert.Throws<IOException>(() => SessionEscrowBridge.BindUnqualified(worker, agent, Epoch, abi, TestContext.Current.CancellationToken));
        Assert.Equal(1, abi.CloseCalls);
    }

    [Fact]
    public void CancelBeforeEffectDoesNotConsumeUpload()
    {
        FakeAbi abi = new();
        using SessionEscrowBridge owner = Create(abi);
        Assert.Throws<OperationCanceledException>(() => owner.UploadUnqualified(1, new CancellationToken(true)));
        Assert.Equal(0, abi.UploadCalls);
        Assert.Equal(SessionEscrowUploadUnqualified.SendAndBarrierOnly, owner.UploadUnqualified(1, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => owner.UploadUnqualified(1, TestContext.Current.CancellationToken));
        Assert.Equal(1, abi.UploadCalls);
        Assert.Throws<InvalidOperationException>(() => owner.MetadataUnqualified(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(999)]
    public void UncertainUploadPoisonsAllFurtherOperations(int status)
    {
        FakeAbi abi = new()
        {
            OperationStatus = status
        };
        using SessionEscrowBridge owner = Create(abi);
        Assert.Throws<InvalidOperationException>(() => owner.UploadUnqualified(1, TestContext.Current.CancellationToken));
        abi.OperationStatus = 0;
        Assert.Throws<InvalidOperationException>(() => owner.ObserveUnqualified(0, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => owner.MetadataUnqualified(TestContext.Current.CancellationToken));
        Assert.Equal(1, abi.UploadCalls);
        Assert.Equal(0, abi.ObserveCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedMetadataPoisonsEvenWhenNativeReportsOk(int mode)
    {
        FakeAbi abi = new()
        {
            BadMetadata = mode
        };
        using SessionEscrowBridge owner = Create(abi);
        Assert.Throws<InvalidOperationException>(() => owner.MetadataUnqualified(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => owner.UploadUnqualified(1, TestContext.Current.CancellationToken));
        Assert.Equal(0, abi.UploadCalls);
    }

    [Fact]
    public void InvalidRoleAndNonBooleanObservationRefuse()
    {
        FakeAbi abi = new();
        using SessionEscrowBridge owner = Create(abi);
        Assert.Throws<InvalidOperationException>(() => owner.ObserveUnqualified(2, TestContext.Current.CancellationToken));
        Assert.Equal(0, abi.ObserveCalls);
        using SessionEscrowBridge other = Create(abi);
        abi.BadObservation = true;
        Assert.Throws<InvalidOperationException>(() => other.ObserveUnqualified(0, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => other.UploadUnqualified(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CloseDrainsInFlightEffectAndCancellationWhileQueuedPreventsNextEffect()
    {
        FakeAbi abi = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim closing = new();
        using CancellationTokenSource cancellation = new();
        abi.DuringUpload = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        };
        using SessionEscrowBridge owner = Create(abi);
        Task effect = Task.Run(() => owner.UploadUnqualified(1, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Task queued = Task.Run(() => Assert.ThrowsAny<Exception>(() => owner.ObserveUnqualified(0, cancellation.Token)), TestContext.Current.CancellationToken);
        Task close = Task.Run(() =>
        {
            closing.Set();
            return owner.CloseUnqualified();
        }, TestContext.Current.CancellationToken);
        try
        {
            cancellation.Cancel();
            Assert.True(closing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(close.IsCompleted);
            Assert.Equal(0, abi.CloseCalls);
        }
        finally
        {
            release.Set();
        }
        await Task.WhenAll(effect, queued, close).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        owner.Dispose();
        Assert.Equal(1, abi.CloseCalls);
        Assert.Equal(0, abi.ObserveCalls);
        Assert.Throws<ObjectDisposedException>(() => owner.MetadataUnqualified(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(99)]
    public void CloseFailureStillConsumesHandleAndNeverRetries(int status)
    {
        FakeAbi abi = new()
        {
            CloseResult = status
        };
        SessionEscrowBridge owner = Create(abi);
        if (status > 7)
        {
            Assert.Throws<InvalidOperationException>(() => owner.CloseUnqualified());
            Assert.Throws<InvalidOperationException>(() => owner.CloseUnqualified());
        }
        else
        {
            Assert.Equal(status, owner.CloseUnqualified());
            Assert.Equal(status, owner.CloseUnqualified());
        }
        Assert.Equal(1, abi.CloseCalls);
        Assert.Throws<ObjectDisposedException>(() => owner.ObserveUnqualified(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PreCancelledBindAndZeroDeadlineHaveNoNativeEffect()
    {
        FakeAbi abi = new();
        using BorrowedDescriptor worker = new(3);
        using BorrowedDescriptor agent = new(4);
        Assert.Throws<OperationCanceledException>(() => SessionEscrowBridge.BindUnqualified(worker, agent, Epoch, abi,
            new CancellationToken(true)));
        Assert.Equal(0, abi.BindCalls);
        using SessionEscrowBridge owner = Create(abi);
        Assert.Throws<InvalidOperationException>(() => owner.UploadUnqualified(0, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => owner.UploadUnqualified(1, TestContext.Current.CancellationToken));
        Assert.Equal(0, abi.UploadCalls);
    }

    [Fact]
    public void HandbackIsRetireOnlyAndConsumedAcrossAllAbiInstances()
    {
        FakeAbi abi = new();
        SessionEscrowIdentityUnqualified[] expected = Identities();
        Assert.Throws<OperationCanceledException>(() => SessionEscrowBridge.HandbackUnqualified(Epoch,
            expected[0], expected[1], abi, new CancellationToken(true)));
        using SessionEscrowBridge owner = SessionEscrowBridge.HandbackUnqualified(Epoch, expected[0], expected[1], abi, TestContext.Current.CancellationToken);
        Assert.Equal(1, abi.HandbackCalls);
        Assert.Equal(2, owner.MetadataUnqualified(TestContext.Current.CancellationToken).Length);
        Assert.Throws<InvalidOperationException>(() => owner.UploadUnqualified(1, TestContext.Current.CancellationToken));
        FakeAbi other = new();
        Assert.Throws<InvalidOperationException>(() => SessionEscrowBridge.HandbackUnqualified(Epoch, expected[0], expected[1], other, TestContext.Current.CancellationToken));
        Assert.Equal(0, other.HandbackCalls);
    }

    private static SessionEscrowBridge Create(FakeAbi abi)
    {
        using BorrowedDescriptor worker = new(3);
        using BorrowedDescriptor agent = new(4);
        return SessionEscrowBridge.BindUnqualified(worker, agent, Epoch, abi, TestContext.Current.CancellationToken);
    }

    private static SessionEscrowIdentityUnqualified[] Identities()
    {
        return
        [
            new()
            {
                Device = 12, Inode = 100, Flags = 2
            },
            new()
            {
                Device = 12, Inode = 101, Flags = 0x802
            }
        ];
    }

    private sealed class BorrowedDescriptor : SafeHandle
    {
        internal int Releases
        {
            get; private set;
        }
        internal BorrowedDescriptor(int fd) : base(fd, true)
        {
        }
        public override bool IsInvalid => handle == -1;
        protected override bool ReleaseHandle()
        {
            Releases++;
            return true;
        }
    }

    private sealed class FakeAbi : ISessionEscrowAbi
    {
        internal int BindStatus;
        internal int OperationStatus;
        internal int BadMetadata = -1;
        internal bool ThrowBind;
        internal bool BadObservation;
        internal int BindCalls;
        internal int UploadCalls;
        internal int ObserveCalls;
        internal int CloseCalls;
        internal int CloseResult;
        internal int HandbackCalls;
        internal byte[]? EpochBytes;
        internal Action? DuringBind;
        internal Action? DuringUpload;

        public int Bind(int worker, int agent, byte[] epoch, out nint session)
        {
            BindCalls++;
            EpochBytes = epoch;
            session = 123;
            DuringBind?.Invoke();
            if (ThrowBind)
            {
                throw new IOException("Synthetic uncertain output");
            }
            return BindStatus;
        }
        public int Handback(byte[] epoch, SessionEscrowIdentityUnqualified[] expected, out nint session)
        {
            HandbackCalls++;
            session = 123;
            return 0;
        }
        public int Metadata(nint session, SessionEscrowIdentityUnqualified[] identities)
        {
            Identities().CopyTo(identities, 0);
            if (BadMetadata == 0)
            {
                identities[1].Inode = 0;
            }
            if (BadMetadata == 1)
            {
                identities[1].Inode = identities[0].Inode;
            }
            if (BadMetadata == 2)
            {
                identities[1].Flags = 0x82;
            }
            return OperationStatus;
        }
        public int Upload(nint session, ulong deadline)
        {
            UploadCalls++;
            DuringUpload?.Invoke();
            return OperationStatus;
        }
        public int Observe(nint session, uint role, out SessionEscrowObservationUnqualified observation)
        {
            ObserveCalls++;
            observation = new()
            {
                Readable = BadObservation ? 2 : 1,
                Hup = 1
            };
            return OperationStatus;
        }
        public int Close(ref nint session)
        {
            CloseCalls++;
            session = 0;
            return CloseResult;
        }
    }
}

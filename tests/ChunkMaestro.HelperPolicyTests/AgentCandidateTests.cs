using ChunkMaestro.PrivilegedHelper;
using System.Runtime.InteropServices;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class AgentCandidateTests
{
    [Fact]
    public void DiagnosticsHaveExactFixedLayout()
    {
        Assert.Equal(32, Marshal.SizeOf<AgentCandidateDiagnosticsAbi>());
        string[] fields = ["Version", "FirstStage", "FirstCode", "FirstErrno", "CleanupStage", "CleanupCode", "CleanupErrno", "OwnershipFlags"];
        for (int index = 0; index < fields.Length; index++)
        {
            Assert.Equal(index * 4, Marshal.OffsetOf<AgentCandidateDiagnosticsAbi>(fields[index]).ToInt32());
        }
    }

    [Fact]
    public void SuccessRetainsCandidateUntilCloseOnceAndProducesOnlySnapshot()
    {
        FakeAbi abi = new();
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        Assert.Equal(17ul, owner.MeasureUnqualified(TestContext.Current.CancellationToken).Inode);
        Assert.Equal(1u, owner.Diagnostics.OwnershipFlags);
        owner.Dispose();
        owner.Dispose();
        Assert.Equal(1, abi.Closes);
        Assert.Equal(0u, owner.Diagnostics.OwnershipFlags);
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NullAcquireFailureRetainsBothChannelsWithoutClose()
    {
        FakeAbi abi = new()
        {
            AcquireRecord = Failure(7, 3, 0, 12, 4, 5, 2)
        };
        using AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        Assert.Throws<InvalidOperationException>(() => owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken));
        Assert.Equal(3u, owner.Diagnostics.FirstCode);
        Assert.Equal(5, owner.Diagnostics.CleanupErrno);
        Assert.Equal(0, abi.Closes);
        Assert.Throws<InvalidOperationException>(() => owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken));
        Assert.Equal(1, abi.Acquires);
    }

    [Fact]
    public void FirstMeasurementAndFirstCleanupSurviveLaterCloseFailure()
    {
        FakeAbi abi = new()
        {
            MeasureRecord = Failure(10, 3, 0, 11, 4, 4, 3),
            CloseRecord = Failure(12, 4, 5, 12, 4, 5, 2)
        };
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
        owner.Dispose();
        Assert.Equal(10u, owner.Diagnostics.FirstStage);
        Assert.Equal(11u, owner.Diagnostics.CleanupStage);
        Assert.Equal(4, owner.Diagnostics.CleanupErrno);
        Assert.Equal(2u, owner.Diagnostics.OwnershipFlags);
        Assert.Equal(1, abi.Closes);
    }

    [Fact]
    public void FinalPinReleaseCloseFailurePreventsSnapshotAndRemainsObservable()
    {
        FakeAbi abi = new()
        {
            CloseRecord = Failure(12, 4, 4, 12, 4, 4, 2)
        };
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.DuringMeasure = owner.Dispose;
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
        Assert.Equal(1, abi.Closes);
        Assert.Equal(12u, owner.Diagnostics.CleanupStage);
        Assert.Equal(4, owner.Diagnostics.CleanupErrno);
        owner.Dispose();
        Assert.Equal(1, abi.Closes);
    }

    [Fact]
    public async Task ConcurrentDisposeRevokesAdmissionAndDrainsBorrow()
    {
        FakeAbi abi = new();
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        using ManualResetEventSlim inside = new();
        using ManualResetEventSlim finish = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.DuringMeasure = () =>
        {
            inside.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(0, abi.Closes);
        };
        Task measurement = Task.Run(() => Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Task disposal = Task.Run(owner.Dispose, TestContext.Current.CancellationToken);
        // Completion of Dispose waits for the borrow; release is explicit, not a timed race.
        Assert.True(SpinWait.SpinUntil(() => owner.IsDisposalRequested, TimeSpan.FromSeconds(10)));
        finish.Set();
        await Task.WhenAll(measurement, disposal);
        Assert.Equal(1, abi.Closes);
    }

    [Fact]
    public void CancellationAtNativeCompletionPreventsSnapshot()
    {
        FakeAbi abi = new();
        using AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        using CancellationTokenSource cancellation = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.DuringMeasure = cancellation.Cancel;
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(cancellation.Token));
        Assert.True(owner.Diagnostics.Refused);
    }

    [Fact]
    public void SwallowedReentryStillPoisonsOwner()
    {
        FakeAbi abi = new();
        using AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.DuringMeasure = () => Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
        Assert.True(owner.Diagnostics.Refused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MalformedCompletedAcquirePointerIsQuarantinedWithoutBlindClose(int fault)
    {
        AgentCandidateDiagnosticsAbi record = Success(1);
        switch (fault)
        {
            case 0:
                record.Version = 2;
                break;
            case 1:
                record.FirstStage = 14;
                break;
            case 2:
                record.FirstCode = 8;
                break;
            case 3:
                record.OwnershipFlags = 8;
                break;
            case 4:
                record.FirstErrno = -1;
                break;
            case 5:
                record.CleanupCode = 1;
                break;
            case 6:
                record.FirstErrno = 5;
                break;
        }
        FakeAbi abi = new()
        {
            AcquireRecord = record,
            ForcePointer = true
        };
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        Assert.Throws<InvalidOperationException>(() => owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken));
        owner.Dispose();
        Assert.Equal(4u, owner.Diagnostics.OwnershipFlags & 4);
        Assert.Equal(0, abi.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchExceptionsRefuseWithoutRetry(bool knownMissingEntryPoint)
    {
        FakeAbi abi = new()
        {
            AcquireError = knownMissingEntryPoint ? new EntryPointNotFoundException() : new InvalidOperationException()
        };
        using AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        Assert.Throws<InvalidOperationException>(() => owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken));
        Assert.Equal(knownMissingEntryPoint ? 0u : 4u, owner.Diagnostics.OwnershipFlags & 4);
        Assert.Equal(0, abi.Closes);
    }

    [Fact]
    public void UnknownMeasurementCompletionNeverClosesUntrustedObject()
    {
        FakeAbi abi = new();
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.DuringMeasure = () => throw new InvalidOperationException();
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
        owner.Dispose();
        Assert.Equal(4u, owner.Diagnostics.OwnershipFlags & 4);
        Assert.Equal(0, abi.Closes);
    }

    [Fact]
    public void MalformedCloseIsObservableAfterFreeAndNeverRetried()
    {
        FakeAbi abi = new();
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.CloseRecord.Version = 0;
        owner.Dispose();
        Assert.True(owner.Diagnostics.Refused);
        Assert.Equal(4u, owner.Diagnostics.OwnershipFlags & 4);
        owner.Dispose();
        Assert.Equal(1, abi.Closes);
    }

    [Fact]
    public void MalformedMeasurementPoisonsAndQuarantinesWithoutClose()
    {
        FakeAbi abi = new();
        AgentCandidateUnqualified owner = new(abi);
        using SocketHandle socket = new();
        owner.AcquireUnqualified(socket, TestContext.Current.CancellationToken);
        abi.MeasureRecord.OwnershipFlags = 9;
        Assert.Throws<InvalidOperationException>(() => owner.MeasureUnqualified(TestContext.Current.CancellationToken));
        owner.Dispose();
        Assert.Equal(4u, owner.Diagnostics.OwnershipFlags & 4);
        Assert.Equal(0, abi.Closes);
    }

    private static AgentCandidateDiagnosticsAbi Success(uint flags)
    {
        return new()
        {
            Version = 1,
            OwnershipFlags = flags
        };
    }

    private static AgentCandidateDiagnosticsAbi Failure(uint stage, uint code, int error,
        uint cleanupStage, uint cleanupCode, int cleanupError, uint flags)
    {
        return new()
        {
            Version = 1,
            FirstStage = stage,
            FirstCode = code,
            FirstErrno = error,
            CleanupStage = cleanupStage,
            CleanupCode = cleanupCode,
            CleanupErrno = cleanupError,
            OwnershipFlags = flags
        };
    }

    private sealed class SocketHandle() : SafeHandle(19, true)
    {
        public override bool IsInvalid => false;

        protected override bool ReleaseHandle()
        {
            return true;
        }
    }

    private sealed class FakeAbi : IAgentCandidateAbi
    {
        internal AgentCandidateDiagnosticsAbi AcquireRecord = Success(1);
        internal AgentCandidateDiagnosticsAbi MeasureRecord = Success(1);
        internal AgentCandidateDiagnosticsAbi CloseRecord = Success(0);
        internal Action? DuringMeasure;
        internal Exception? AcquireError;
        internal bool ForcePointer;
        internal int Acquires;
        internal int Closes;

        public int AcquireCandidate(int borrowed, out nint candidate, out AgentCandidateDiagnosticsAbi diagnostics)
        {
            Acquires++;
            if (AcquireError is not null)
            {
                throw AcquireError;
            }
            diagnostics = AcquireRecord;
            candidate = diagnostics.FirstCode == 0 || ForcePointer ? 123 : 0;
            return (int) diagnostics.FirstCode;
        }

        public int MeasureCandidate(nint candidate, out OriginalMeasurementAbi value, out AgentCandidateDiagnosticsAbi diagnostics)
        {
            DuringMeasure?.Invoke();
            diagnostics = MeasureRecord;
            value = new()
            {
                Device = 1,
                Inode = 17,
                Flags = 2,
                Architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (byte) 2 : (byte) 1,
                Normalization = 1,
                Source = 1
            };
            return (int) diagnostics.FirstCode;
        }

        public int CloseCandidate(ref nint candidate, out AgentCandidateDiagnosticsAbi diagnostics)
        {
            Closes++;
            candidate = 0;
            diagnostics = CloseRecord;
            return (int) diagnostics.FirstCode;
        }
    }
}

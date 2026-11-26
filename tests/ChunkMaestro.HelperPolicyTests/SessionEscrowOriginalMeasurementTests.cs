using ChunkMaestro.PrivilegedHelper;
using System.Runtime.InteropServices;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class SessionEscrowOriginalMeasurementTests
{
    [Fact]
    public void LayoutHasDistinctProfileBytes()
    {
        Assert.Equal(24, Marshal.SizeOf<OriginalMeasurementAbi>());
        string[] fields = ["Device", "Inode", "Flags", "Architecture", "Normalization", "Source", "Reserved"];
        int[] offsets = [0, 8, 16, 20, 21, 22, 23];
        for (int index = 0; index < fields.Length; index++)
        {
            Assert.Equal(offsets[index], Marshal.OffsetOf<OriginalMeasurementAbi>(fields[index]).ToInt32());
        }
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(0x802u)]
    public void CopiesFactsWithoutDisposingOriginal(uint flags)
    {
        FakeAbi abi = new();
        abi.Value.Flags = flags;
        using Original original = new(42);
        SessionEscrowOriginalMeasurement result = Measure(original, abi);
        Assert.Equal(17ul, result.Device);
        Assert.Equal(99ul, result.Inode);
        Assert.Equal(flags, result.Flags);
        Assert.Equal(1, abi.Calls);
        Assert.Equal(0, original.Releases);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void EveryNonzeroStatusRefusesWithoutRetryOrExceptionDetails(int status)
    {
        FakeAbi abi = new()
        {
            Status = status
        };
        using Original original = new(42);
        Refused(() => Measure(original, abi));
        Assert.Equal(1, abi.Calls);
        Assert.Equal(0, original.Releases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void MalformedSuccessRefuses(int fault)
    {
        FakeAbi abi = new();
        switch (fault)
        {
            case 0:
                abi.Value.Device = 0;
                break;
            case 1:
                abi.Value.Inode = 0;
                break;
            case 2:
                abi.Value.Flags = 0;
                break;
            case 3:
                abi.Value.Flags = 0x8002;
                break;
            case 4:
                abi.Value.Architecture = abi.Value.Architecture == 1 ? (byte) 2 : (byte) 1;
                break;
            case 5:
                abi.Value.Normalization = 0;
                break;
            case 6:
                abi.Value.Source = 0;
                break;
            case 7:
                abi.Value.Reserved = 1;
                break;
            case 8:
                abi.Value = default;
                break;
        }
        using Original original = new(42);
        Refused(() => Measure(original, abi));
        Assert.Equal(1, abi.Calls);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483648L)]
    public void InvalidRangeNeverDispatches(long fd)
    {
        FakeAbi abi = new();
        using Original original = new((nint) fd);
        Refused(() => Measure(original, abi));
        Assert.Equal(0, abi.Calls);
    }

    [Fact]
    public void NullClosedAndPrecancelledNeverDispatch()
    {
        FakeAbi abi = new();
        Refused(() => Measure(null!, abi));
        using Original original = new(42);
        Refused(() => SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original, abi, new CancellationToken(true)));
        original.Dispose();
        Refused(() => Measure(original, abi));
        Assert.Equal(0, abi.Calls);
        Assert.Equal(1, original.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDisposeIsPinnedThroughReturnAndExceptionalUnwind(bool fail)
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using Original original = new(42);
        FakeAbi abi = new()
        {
            During = () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                Assert.Equal(0, original.Releases);
                if (fail)
                {
                    throw new EntryPointNotFoundException("sensitive loader details");
                }
            }
        };
        Task operation = Task.Run(() =>
        {
            if (fail)
            {
                Refused(() => Measure(original, abi));
            }
            else
            {
                Measure(original, abi);
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            original.Dispose();
            Assert.Equal(0, original.Releases);
        }
        finally
        {
            release.Set();
        }
        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, original.Releases);
        Assert.Equal(1, abi.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DispatchExceptionsAreSanitizedWithoutFallback(int fault)
    {
        FakeAbi abi = new()
        {
            During = () => throw (fault switch
            {
                0 => new DllNotFoundException("sensitive path"),
                1 => new EntryPointNotFoundException("old DSO"),
                2 => new SyntheticDispatchException(),
                _ => new OperationCanceledException("dispatch already entered")
            })
        };
        using Original original = new(42);
        Refused(() => Measure(original, abi));
        Assert.Equal(1, abi.Calls);
        original.Dispose();
        Assert.Equal(1, original.Releases);
    }

    [Fact]
    public void LateCancellationDrainsAndReturnsNoResult()
    {
        using CancellationTokenSource cancellation = new();
        using Original original = new(42);
        FakeAbi abi = new()
        {
            During = cancellation.Cancel
        };
        Refused(() => SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original, abi, cancellation.Token));
        Assert.Equal(1, abi.Calls);
        Assert.Equal(0, original.Releases);
        original.Dispose();
        Assert.Equal(1, original.Releases);
    }

    private static SessionEscrowOriginalMeasurement Measure(Original original, FakeAbi abi)
    {
        return SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original, abi, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalReleaseCancellationIsObservedBeforeCompletion(bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        using Original original = new(42)
        {
            OnRelease = () =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
            }
        };
        FakeAbi abi = new()
        {
            During = () =>
            {
                original.Dispose();
                Assert.Equal(0, original.Releases);
            }
        };
        if (cancel)
        {
            Refused(() => SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original, abi, cancellation.Token));
        }
        else
        {
            SessionEscrowOriginalMeasurement facts = SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original, abi, cancellation.Token);
            Assert.Equal(99ul, facts.Inode);
        }
        Assert.Equal(cancel, cancellation.IsCancellationRequested);
        Assert.Equal(1, abi.Calls);
        Assert.Equal(1, original.Releases);
    }

    private static void Refused(Action action)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("EscrowOriginalMeasurementRefusedUnqualified", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private sealed class SyntheticDispatchException() : Exception("unknown sensitive failure")
    {
    }

    private sealed class Original(nint descriptor) : SafeHandle(descriptor, true)
    {
        internal int Releases;
        internal Action? OnRelease;
        public override bool IsInvalid => handle == -1;
        protected override bool ReleaseHandle()
        {
            Interlocked.Increment(ref Releases);
            OnRelease?.Invoke();
            return true;
        }
    }

    private sealed class FakeAbi : IOriginalMeasurementAbi
    {
        internal int Calls;
        internal int Status;
        internal Action? During;
        internal OriginalMeasurementAbi Value = new()
        {
            Device = 17,
            Inode = 99,
            Flags = 2,
            Architecture = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? (byte) 1 : (byte) 2,
            Normalization = 1,
            Source = 1
        };
        public int MeasureOriginal(int borrowed, out OriginalMeasurementAbi value)
        {
            Assert.Equal(42, borrowed);
            Calls++;
            value = Value;
            During?.Invoke();
            return Status;
        }
    }
}

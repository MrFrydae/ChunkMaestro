using ChunkMaestro.PrivilegedHelper;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
using Lease = ChunkMaestro.PrivilegedHelper.RetainedPolicyAcquisition.RetainedPolicyDataUnqualified;

namespace ChunkMaestro.HelperPolicyTests;

[Collection(ProcessDescriptorTestGroup.Name)]
public sealed class RetainedPolicyAcquisitionTests
{
    [Fact]
    public void UnsupportedPlatformRefusesBeforeDescriptorTransferOrNativeLoad()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }
        int parent = 123;
        int file = 456;
        Assert.Throws<PlatformNotSupportedException>(() => RetainedPolicyAcquisition.Acquire(ref parent, ref file, TestContext.Current.CancellationToken));
        Assert.Equal(123, parent);
        Assert.Equal(456, file);
    }

    [Fact]
    public void LeaseHasNoDataCombinationConstructorOrPublicAuthority()
    {
        Assert.True(typeof(Lease).IsSealed);
        Assert.Empty(typeof(Lease).GetConstructors());
        Assert.All(typeof(Lease).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic), constructor => Assert.True(constructor.IsPrivate));
        Assert.DoesNotContain(typeof(Lease).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), property => property.PropertyType == typeof(PolicyDecodeResult.ParsedInspectionPolicyData));
        Assert.All(typeof(Lease).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(Lease).GetMethods(BindingFlags.Public | BindingFlags.Static));
    }

    [Fact(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    public void LinuxPreCancellationLeavesExclusiveInputsWithCaller()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using CancellationTokenSource source = new();
        source.Cancel();
        int parent = 123;
        int file = 456;
        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() => RetainedPolicyAcquisition.Acquire(ref parent, ref file, source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Equal(123, parent);
        Assert.Equal(456, file);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    public void LinuxRealCurrentUserRawBridgeRoundtripAndProductionShapeRefusal()
    {
        Bridge.Configure(0, 0, 0);
        int parent = -1;
        int file = -1;
        byte[] bytes = InstalledPolicyCodecTests.Seed();
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        try
        {
            PolicySnapshotNative.CheckStatus(PolicySnapshotNative.Take(ref parent, ref file, out nint pointer));
            using PolicySnapshotNative.SnapshotHandle handle = new(pointer);
            byte[] facts = new byte[128];
            PolicySnapshotNative.CheckStatus(PolicySnapshotNative.Facts(handle, facts, 128, out uint written));
            Assert.Equal(128u, written);
            PolicySnapshotNative.SnapshotFacts measured = PolicySnapshotNative.DecodeFacts(facts);
            Assert.False(measured.HasRootNumericShape);
            Assert.Equal((uint) bytes.Length, measured.ByteCount);
            byte[] copy = new byte[bytes.Length];
            PolicySnapshotNative.CheckStatus(PolicySnapshotNative.Copy(handle, copy, (uint) copy.Length, out written));
            Assert.Equal((uint) bytes.Length, written);
            Assert.Equal(bytes, copy);
        }
        finally
        {
            Bridge.Cleanup();
        }
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        try
        {
            Assert.Equal("ProtectedShapeRefusal", Assert.Throws<InvalidOperationException>(() => RetainedPolicyAcquisition.Acquire(ref parent, ref file, TestContext.Current.CancellationToken)).Message);
            Assert.Equal(-1, parent);
            Assert.Equal(-1, file);
        }
        finally
        {
            Bridge.Cleanup();
        }
    }

    [Fact(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    public void SyntheticRootOnlyHandoffOwnsHandleAndDisposeIsIdempotent()
    {
        Bridge.Configure(1, 0, 0);
        byte[] bytes = InstalledPolicyCodecTests.Seed();
        int parent = -1;
        int file = -1;
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        try
        {
            Lease lease = RetainedPolicyAcquisition.Acquire(ref parent, ref file, TestContext.Current.CancellationToken);
            Assert.Equal(-1, parent);
            Assert.Equal(-1, file);
            Assert.Equal(System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), lease.PolicyRevision);
            lease.Check(TestContext.Current.CancellationToken);
            lease.Dispose();
            int closed = Bridge.CloseCount();
            lease.Dispose();
            Assert.Equal(closed, Bridge.CloseCount());
            Assert.Throws<ObjectDisposedException>(() => lease.PolicyRevision);
            Assert.Throws<ObjectDisposedException>(() => lease.Check(TestContext.Current.CancellationToken));
        }
        finally
        {
            Bridge.Cleanup();
        }
    }

    [Theory(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SyntheticRootCancellationDuringBlockedNativeCallPublishesNothing(int stage)
    {
        Bridge.Configure(1, 0, stage);
        byte[] bytes = InstalledPolicyCodecTests.Seed();
        int parent = -1;
        int file = -1;
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        using CancellationTokenSource source = new();
        Task<Lease> acquisition = Task.Run(() => RetainedPolicyAcquisition.Acquire(ref parent, ref file, source.Token), CancellationToken.None);
        try
        {
            await Task.Run(() => Bridge.Wait(), CancellationToken.None);
            int closed = Bridge.CloseCount();
            source.Cancel();
            Assert.False(acquisition.IsCompleted);
            Assert.Equal(closed, Bridge.CloseCount());
        }
        finally
        {
            Bridge.Release();
        }
        try
        {
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await acquisition);
            Assert.Equal(source.Token, exception.CancellationToken);
            Assert.Equal(-1, parent);
            Assert.Equal(-1, file);
        }
        finally
        {
            Bridge.Cleanup();
        }
    }

    [Fact(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    public async Task SyntheticRootDisposeWaitsForCompleteCheck()
    {
        Bridge.Configure(1, 0, 0);
        byte[] bytes = InstalledPolicyCodecTests.Seed();
        int parent = -1;
        int file = -1;
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        Lease lease = RetainedPolicyAcquisition.Acquire(ref parent, ref file, TestContext.Current.CancellationToken);
        Bridge.Configure(1, 0, 1);
        Task check = Task.Run(() => lease.Check(TestContext.Current.CancellationToken), CancellationToken.None);
        await Task.Run(() => Bridge.Wait(), CancellationToken.None);
        int closed = Bridge.CloseCount();
        using ManualResetEventSlim started = new();
        Task disposal = Task.Run(() =>
        {
            started.Set();
            lease.Dispose();
        }, CancellationToken.None);
        started.Wait(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(disposal.IsCompleted);
            Assert.Equal(closed, Bridge.CloseCount());
        }
        finally
        {
            Bridge.Release();
        }
        try
        {
            await Task.WhenAll(check, disposal);
            Assert.Throws<ObjectDisposedException>(() => lease.PolicyRevision);
        }
        finally
        {
            lease.Dispose();
            Bridge.Cleanup();
        }
    }

    [Theory(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    [InlineData(101, "InvalidSnapshotAbi")]
    [InlineData(102, "InvalidSnapshotAbi")]
    [InlineData(103, "InvalidSnapshotAbi")]
    [InlineData(104, "SnapshotRefusal3")]
    [InlineData(105, "SnapshotRefusal2")]
    [InlineData(106, "PolicyParseRefusal:")]
    public void SyntheticRootMalformedBridgeNeverPublishesLease(int fault, string expected)
    {
        Bridge.Configure(1, fault, 0);
        byte[] bytes = InstalledPolicyCodecTests.Seed();
        int parent = -1;
        int file = -1;
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => RetainedPolicyAcquisition.Acquire(ref parent, ref file, TestContext.Current.CancellationToken));
            Assert.StartsWith(expected, exception.Message, StringComparison.Ordinal);
            Assert.Equal(-1, parent);
            Assert.Equal(-1, file);
        }
        finally
        {
            Bridge.Cleanup();
        }
    }

    [Fact(Skip = "Linux native ABI requires an ordinary-user Linux runner", SkipUnless = nameof(IsLinux))]
    public void SyntheticRootParallelIndependentLeasesAndFinalizerRestoreCompleteFdSet()
    {
        Bridge.Configure(1, 0, 0);
        byte[] bytes = InstalledPolicyCodecTests.Seed();
        int parent = -1;
        int file = -1;
        Assert.Equal(0, Bridge.Prepare(bytes, (uint) bytes.Length, ref parent, ref file));
        byte[] before = new byte[65536];
        byte[] after = new byte[65536];
        try
        {
            // Warm managed worker infrastructure before taking the complete descriptor set.
            Parallel.For(0, 16, _ => Thread.Yield());
            // Drain unrelated finalizers before the baseline as well as after the owned work.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, Bridge.FdSet(before, 65536));
            Parallel.For(0, 16, _ =>
            {
                int p = -1;
                int f = -1;
                Assert.Equal(0, Bridge.Duplicate(parent, file, ref p, ref f));
                using Lease lease = RetainedPolicyAcquisition.Acquire(ref p, ref f, TestContext.Current.CancellationToken);
                lease.Check(TestContext.Current.CancellationToken);
                Assert.Equal(-1, p);
                Assert.Equal(-1, f);
            });
            WeakReference abandoned = AbandonSyntheticLease(parent, file);
            for (int attempt = 0; attempt < 8 && abandoned.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(abandoned.IsAlive);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, Bridge.FdSet(after, 65536));
            Assert.Equal(before, after);
        }
        finally
        {
            Bridge.CloseInputs(parent, file);
            Bridge.Cleanup();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonSyntheticLease(int parent, int file)
    {
        int p = -1;
        int f = -1;
        Assert.Equal(0, Bridge.Duplicate(parent, file, ref p, ref f));
        Lease lease = RetainedPolicyAcquisition.Acquire(ref p, ref f, TestContext.Current.CancellationToken);
        return new WeakReference(lease);
    }

    // Only freshly built test-output exports, no native test hook in production.
    private static class Bridge
    {
        private static readonly nint Library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libchunkmaestro_policy_snapshot_v1.so"));
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int PrepareCall([In] byte[] bytes, uint length, ref int parent, ref int file);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ConfigureCall(int root, int fault, int block);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SignalCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int CountCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DuplicateCall(int parent, int file, ref int p, ref int f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void CloseInputsCall(int parent, int file);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int FdSetCall([Out] byte[] bytes, uint capacity);
        internal static readonly DuplicateCall Duplicate = Marshal.GetDelegateForFunctionPointer<DuplicateCall>(NativeLibrary.GetExport(Library, "cm_policy_test_duplicate"));
        internal static readonly CloseInputsCall CloseInputs = Marshal.GetDelegateForFunctionPointer<CloseInputsCall>(NativeLibrary.GetExport(Library, "cm_policy_test_close_inputs"));
        internal static readonly FdSetCall FdSet = Marshal.GetDelegateForFunctionPointer<FdSetCall>(NativeLibrary.GetExport(Library, "cm_policy_test_fdset"));
        internal static readonly PrepareCall Prepare = Marshal.GetDelegateForFunctionPointer<PrepareCall>(NativeLibrary.GetExport(Library, "cm_policy_test_prepare"));
        internal static readonly ConfigureCall Configure = Marshal.GetDelegateForFunctionPointer<ConfigureCall>(NativeLibrary.GetExport(Library, "cm_policy_test_configure"));
        internal static readonly SignalCall Cleanup = Marshal.GetDelegateForFunctionPointer<SignalCall>(NativeLibrary.GetExport(Library, "cm_policy_test_cleanup"));
        internal static readonly SignalCall Wait = Marshal.GetDelegateForFunctionPointer<SignalCall>(NativeLibrary.GetExport(Library, "cm_policy_test_wait"));
        internal static readonly SignalCall Release = Marshal.GetDelegateForFunctionPointer<SignalCall>(NativeLibrary.GetExport(Library, "cm_policy_test_release"));
        internal static readonly CountCall CloseCount = Marshal.GetDelegateForFunctionPointer<CountCall>(NativeLibrary.GetExport(Library, "cm_policy_test_close_count"));
    }
}

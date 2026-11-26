using ChunkMaestro.PrivilegedHelper;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

[Collection(ProcessDescriptorTestGroup.Name)]
public sealed class SessionEscrowLinuxAbiTests
{
    [Fact(Skip = "Real Linux pidfs ABI required; no native proof on macOS", SkipUnless = nameof(IsLinux))]
    public void OriginalMeasurementUsesRealAbiAndLeavesBorrowedOriginalUsable()
    {
        Assert.NotEqual(0u, GetUid());
        using SafeFileHandle original = Pidfd(Environment.ProcessId);
        SessionEscrowOriginalMeasurement measured = SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(0ul, measured.Device);
        Assert.NotEqual(0ul, measured.Inode);
        Assert.Equal(2u, measured.Flags);
        Assert.Equal(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? (byte) 1 : (byte) 2, measured.Architecture);
        Assert.Equal(1, measured.Normalization);
        Assert.Equal(1, measured.Source);
        Assert.False(original.IsClosed);
        Assert.Equal(measured, SessionEscrowOriginalMeasurement.MeasureOriginalUnqualified(original,
            TestContext.Current.CancellationToken));
    }

    private const string Epoch = "0123456789abcdef0123456789abcdef";
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "Real Linux pidfs ABI required; no native proof on macOS", SkipUnless = nameof(IsLinux))]
    public void OwnedPidfdsRetainIdentityAfterBorrowedCloseAndObserveOwnedChildDeath()
    {
        Assert.NotEqual(0u, GetUid());
        using Process child = Process.Start(new ProcessStartInfo("/bin/sleep", "60")
        {
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Owned child did not start");
        try
        {
            using SafeFileHandle worker = Pidfd(child.Id);
            using SafeFileHandle agent = Pidfd(Environment.ProcessId);
            using SessionEscrowBridge owner = SessionEscrowBridge.BindUnqualified(worker, agent, Epoch,
                TestContext.Current.CancellationToken);
            SessionEscrowIdentityUnqualified[] before = owner.MetadataUnqualified(TestContext.Current.CancellationToken);
            Assert.NotEqual(before[0].Inode, before[1].Inode);
            worker.Dispose();
            agent.Dispose();
            Assert.Equal(0, owner.ObserveUnqualified(0, TestContext.Current.CancellationToken).Readable);
            child.Kill();
            Assert.True(child.WaitForExit(10000));
            SessionEscrowObservationUnqualified observation = owner.ObserveUnqualified(0, TestContext.Current.CancellationToken);
            Assert.Equal(1, observation.Readable);
            Assert.Equal(1, observation.Hup);
            Assert.Equal(before[0].Inode, owner.MetadataUnqualified(TestContext.Current.CancellationToken)[0].Inode);
            Assert.Equal(0, owner.CloseUnqualified());
            Assert.Equal(0, owner.CloseUnqualified());
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                Assert.True(child.WaitForExit(10000));
            }
        }
    }

    [Fact(Skip = "Real Linux pidfs ABI required; no native proof on macOS", SkipUnless = nameof(IsLinux))]
    public void RealAbiRefusesSameTaskAndOrdinaryDescriptorsWithoutNotifyOrHandback()
    {
        Assert.NotEqual(0u, GetUid());
        using SafeFileHandle first = Pidfd(Environment.ProcessId);
        using SafeFileHandle second = Pidfd(Environment.ProcessId);
        Assert.Throws<InvalidOperationException>(() => SessionEscrowBridge.BindUnqualified(first, second, Epoch,
            TestContext.Current.CancellationToken));
        using SafeFileHandle ordinary = File.OpenHandle("/dev/null");
        Assert.Throws<InvalidOperationException>(() => SessionEscrowBridge.BindUnqualified(ordinary, first, Epoch,
            TestContext.Current.CancellationToken));
        Assert.False(first.IsClosed);
        Assert.False(second.IsClosed);
    }

    private static SafeFileHandle Pidfd(int pid)
    {
        // Test-only acquisition of this process and its owned child. Production has no PID reconstruction API.
        long descriptor = PidfdOpen(434, pid, 0);
        Assert.True(descriptor >= 0, "pidfd_open prerequisite failed");
        return new SafeFileHandle((nint) descriptor, true);
    }

    [DllImport("libc.so.6", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PidfdOpen(long number, int pid, uint flags);
    [DllImport("libc.so.6", EntryPoint = "getuid")]
    private static extern uint GetUid();
}

using ChunkMaestro.PrivilegedHelper;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionJournalTests
{
    [Fact]
    public void MissingStateNeverInitializesAndExistingStateCannotBeOverwritten()
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournal missing = fixture.Open())
        {
            Assert.Throws<FileNotFoundException>(() => missing.Read());
            Assert.Throws<IOException>(() => missing.InitializeNewInstallation(new TestInstall(), fixture.Boot));
        }
        using PolicySessionJournal journal = fixture.Initialize();
        byte[] original = File.ReadAllBytes(fixture.Path);
        Assert.Throws<InvalidOperationException>(() => journal.InitializeNewInstallation(new TestInstall(), fixture.Boot));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData((int) PolicyJournalBarrier.Write)]
    [InlineData((int) PolicyJournalBarrier.Flush)]
    [InlineData((int) PolicyJournalBarrier.Rename)]
    [InlineData((int) PolicyJournalBarrier.DirectorySync)]
    public void InterruptedCommitReturnsNoActionAndReopenNeverReplays(int stopValue)
    {
        PolicyJournalBarrier stop = (PolicyJournalBarrier) stopValue;
        using JournalFixture fixture = new();
        using (PolicySessionJournal initial = fixture.Initialize())
        {
        }
        byte[] original = File.ReadAllBytes(fixture.Path);
        using (PolicySessionJournal journal = fixture.Open(barrier =>
        {
            if (barrier == stop)
            {
                throw new IOException("Injected disk-full/interruption.");
            }
        }))
        {
            PolicySessionRecovery recovery = new(journal, fixture.Boot);
            Assert.Throws<IOException>(() => recovery.Reserve(Guid.NewGuid(), JournalFixture.Identity(1)));
            Assert.Throws<IOException>(() => journal.Read());
        }
        using PolicySessionJournal reopened = fixture.Open();
        PolicySessionRecovery recovered = new(reopened, fixture.Boot);
        if (stop != PolicyJournalBarrier.DirectorySync)
        {
            Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        }
        else
        {
            Assert.Equal(PolicySessionPhase.Occupied, recovered.State.Phase);
        }
        Assert.Throws<InvalidOperationException>(() => recovered.ConsumeStartAttempt());
    }

    [Fact]
    public void CooperatingReservationsAreExcludedWithoutRemovingLockOrOriginal()
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournal journal = fixture.Initialize())
        {
            Assert.Throws<IOException>(() => fixture.Open());
            PolicySessionRecovery recovery = new(journal, fixture.Boot);
            recovery.Reserve(Guid.NewGuid(), JournalFixture.Identity(1));
        }
        using PolicySessionJournal reopened = fixture.Open();
        Assert.Equal(PolicySessionPhase.Occupied, reopened.Read().Phase);
        Assert.True(File.Exists(System.IO.Path.Combine(fixture.Directory, "session.lock")));
    }

    [Fact]
    public async Task SeparateProcessCannotAcquireHeldJournalLock()
    {
        const string probeVariable = "CHUNKMAESTRO_JOURNAL_LOCK_PROBE";
        string? probeDirectory = Environment.GetEnvironmentVariable(probeVariable);
        if (probeDirectory is not null)
        {
            string candidate = System.IO.Path.GetFullPath(probeDirectory);
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), candidate, StringComparison.Ordinal);
            Assert.StartsWith("cm-journal-", System.IO.Path.GetFileName(candidate), StringComparison.Ordinal);
            Assert.Throws<IOException>(() => new PolicySessionJournal(candidate));
            return;
        }
        using JournalFixture fixture = new();
        using (PolicySessionJournal journal = fixture.Initialize())
        {
            byte[] original = File.ReadAllBytes(fixture.Path);
            string host = System.IO.Path.GetFullPath(System.IO.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            ProcessStartInfo start = new(host)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(PolicySessionJournalTests).Assembly.Location);
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("ChunkMaestro.HelperPolicyTests.PolicySessionJournalTests.SeparateProcessCannotAcquireHeldJournalLock");
            start.ArgumentList.Add("-noColor");
            start.ArgumentList.Add("-noAutoReporters");
            start.Environment[probeVariable] = fixture.Directory;
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Lock probe did not start.");
            Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> errors = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            bool exited = child.WaitForExit(15000);
            if (!exited)
            {
                child.Kill(true);
                Assert.True(child.WaitForExit(5000), "Lock probe cleanup timed out.");
            }
            Assert.True(exited, "Lock probe timed out.");
            Assert.Equal(0, child.ExitCode);
            Assert.True((await output).Length < 16384);
            Assert.True((await errors).Length < 16384);
            Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        }
        using PolicySessionJournal reopened = fixture.Open();
        Assert.Equal(PolicySessionPhase.Empty, reopened.Read().Phase);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(51)]
    [InlineData(52)]
    [InlineData(200)]
    [InlineData(256)]
    public void InvalidFieldsEvenWithRecomputedChecksumAreRejected(int offset)
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournal journal = fixture.Initialize())
        {
        }
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        bytes[offset] = 255;
        if (offset < 256)
        {
            SHA256.HashData(bytes.AsSpan(0, 256), bytes.AsSpan(256));
        }
        File.WriteAllBytes(fixture.Path, bytes);
        using PolicySessionJournal damaged = fixture.Open();
        Assert.Throws<InvalidDataException>(() => damaged.Read());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(287)]
    [InlineData(289)]
    [InlineData(1048576)]
    public void SizeIsBoundedBeforeAllocation(int length)
    {
        using JournalFixture fixture = new();
        File.WriteAllBytes(fixture.Path, new byte[length]);
        using PolicySessionJournal journal = fixture.Open();
        Assert.Throws<InvalidDataException>(() => journal.Read());
    }

    [Fact]
    public void ImpossiblePhaseCorrelationIsRejectedWithValidChecksum()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournal journal = fixture.Initialize();
        byte[] bytes = journal.Read().Encode();
        bytes[48] = (byte) PolicySessionPhase.Armed;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), ulong.MaxValue);
        SHA256.HashData(bytes.AsSpan(0, 256), bytes.AsSpan(256));
        Assert.Throws<InvalidDataException>(() => PolicySessionRecord.Decode(bytes));
    }

    [Fact]
    public void ProductionDurabilityCommitsOnLinuxAndRefusesUnsupportedPlatforms()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournal journal = new(fixture.Directory);
        if (OperatingSystem.IsLinux())
        {
            // Force the assembly's shared resolver to initialize before libc calls.
            PolicySnapshotNative.RequireLinux();
            journal.InitializeNewInstallation(new TestInstall(), fixture.Boot);
            PolicySessionRecovery recovery = new(journal, fixture.Boot);
            Guid epoch = Guid.NewGuid();
            recovery.Reserve(epoch, JournalFixture.Identity(1));
            Assert.Equal(PolicySessionPhase.Occupied, journal.Read().Phase);
            Assert.Equal(epoch, journal.Read().Epoch);
            return;
        }
        Assert.Throws<PlatformNotSupportedException>(() => journal.InitializeNewInstallation(new TestInstall(), fixture.Boot));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public void LinuxDirectorySyncRejectsMissingFileAndSymlinkTargets()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxPolicyJournalDurability().RequireSupported());
            return;
        }
        using JournalFixture fixture = new();
        LinuxPolicyJournalDurability durability = new();
        string file = System.IO.Path.Combine(fixture.Directory, "ordinary-file");
        File.WriteAllText(file, "unchanged sentinel");
        string link = System.IO.Path.Combine(fixture.Directory, "directory-link");
        System.IO.Directory.CreateSymbolicLink(link, fixture.Directory);
        Assert.Throws<IOException>(() => durability.SynchronizeDirectory(file));
        Assert.Throws<IOException>(() => durability.SynchronizeDirectory(link));
        Assert.Throws<IOException>(() => durability.SynchronizeDirectory(System.IO.Path.Combine(fixture.Directory, "missing")));
        Assert.Throws<IOException>(() => durability.SynchronizeDirectory(fixture.Directory + "\0ignored"));
        Assert.Equal("unchanged sentinel", File.ReadAllText(file));
        durability.SynchronizeDirectory(fixture.Directory);
    }

    internal sealed class TestInstall : PolicySessionInstallAdapter
    {
        internal override void RequireQualifiedNewInstallation(string directory)
        {
            Assert.True(System.IO.Directory.Exists(directory));
        }
    }

    internal sealed class TestDurability : IPolicyJournalDurability
    {
        public void RequireSupported()
        {
        }

        public void SynchronizeDirectory(string directory)
        {
            Assert.True(System.IO.Directory.Exists(directory));
        }
    }

    internal sealed class JournalFixture : IDisposable
    {
        internal string Directory
        {
            get;
        } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cm-journal-{Guid.NewGuid():N}");
        internal string Path => System.IO.Path.Combine(Directory, "session.journal");
        internal Guid Boot
        {
            get;
        } = Guid.NewGuid();

        internal JournalFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
        }

        internal PolicySessionJournal Open(Action<PolicyJournalBarrier>? barrier = null)
        {
            return new(Directory, new TestDurability(), barrier);
        }

        internal PolicySessionJournal Initialize()
        {
            PolicySessionJournal journal = Open();
            journal.InitializeNewInstallation(new TestInstall(), Boot);
            return journal;
        }

        internal static PolicySessionIdentity Identity(ulong inode)
        {
            return new(Guid.NewGuid(), 1, inode, 1, 1, Guid.NewGuid());
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, true);
        }
    }
}

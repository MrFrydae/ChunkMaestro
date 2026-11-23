using ChunkMaestro.OwnerVerification;
using System.Diagnostics;
using Xunit;

namespace ChunkMaestro.OwnerVerificationTests;

public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS native execution requires a macOS runner.";
        }
    }
}

public sealed class OwnerContextTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void MissingBindingRefusesBeforeNativeLoad(bool missingSession, ulong generation)
    {
        Assert.Equal(OwnerProbeStatus.InvalidInput,
            OwnerContext.Create(missingSession ? Guid.Empty : Guid.NewGuid(), generation, out OwnerContext? context));
        Assert.Null(context);
    }

    [Fact]
    public void PlatformFactoryHasHonestCapability()
    {
        OwnerProbeStatus status = OwnerContext.Create(Guid.NewGuid(), 1, out OwnerContext? context);
        using (context)
        {
            Assert.Equal(OperatingSystem.IsMacOS() ? OwnerProbeStatus.ContextCreated : OwnerProbeStatus.Unsupported, status);
            Assert.Equal(OperatingSystem.IsMacOS(), context is not null);
        }
    }

    [MacFact]
    public void RealContextCancelBeforeStartIsIrreversibleAndDisposalIdempotent()
    {
        using OwnerContext context = Create();
        Assert.Equal(OwnerProbeStatus.Canceled, context.Cancel());
        Assert.Equal(OwnerProbeStatus.Canceled, context.Cancel());
        Assert.Equal(OwnerProbeStatus.Canceled, context.Probe());
        Assert.Equal(OwnerProbeStatus.Canceled, context.Prepare(1, 1));
        context.Dispose();
        context.Dispose();
        Assert.Equal(OwnerProbeStatus.Canceled, context.Probe());
        Assert.Equal(OwnerProbeStatus.Canceled, context.Cancel());
    }

    [MacFact]
    public void RealAvailabilityIsOnlyAProbeAndCannotEnablePreparation()
    {
        using OwnerContext context = Create();
        OwnerProbeStatus status = context.Probe();
        OwnerProbeStatus[] allowed = [OwnerProbeStatus.Available, OwnerProbeStatus.Unavailable];
        Assert.Contains(status, allowed);
        Assert.Equal(OwnerProbeStatus.InvalidInput, context.Prepare(0, 1));
        Assert.Equal(OwnerProbeStatus.InvalidInput, context.Prepare(1, 2));
        // xUnit runs on a worker thread. No AppKit object may be messaged there.
        Assert.Equal(OwnerProbeStatus.Unsupported, context.Prepare(1, 1));
    }

    [MacFact]
    public async Task CancelAndDisposeRaceKeepsContextOwned()
    {
        for (int index = 0; index < 100; index++)
        {
            OwnerContext context = Create();
            Task first = Task.Run(() => context.Cancel());
            Task second = Task.Run(context.Dispose);
            await Task.WhenAll(first, second);
            Assert.Equal(OwnerProbeStatus.Canceled, context.Probe());
        }
    }

    [MacFact]
    public void NativeAbiAndMainThreadRefusalHarness()
    {
        ProcessStartInfo info = new(Path.Combine(AppContext.BaseDirectory, "owner-native-tests"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(info)!;
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("native assertions: 325\n", process.StandardOutput.ReadToEnd());
        Assert.Equal("", process.StandardError.ReadToEnd());
    }

    [MacFact]
    public async Task DisposedEvaluationRefusesWithoutQueueAndAsyncDisposalIsIdempotent()
    {
        OwnerContext context = Create();
        await context.DisposeAsync();
        Assert.Equal(RawPolicyObservation.Unknown, await context.EvaluateExplicitlyAsync(nint.MaxValue, 1));
        await context.DisposeAsync();
    }

    [MacFact]
    public void SyntheticNativeCallbackHarnessCannotInvokeOSPolicy()
    {
        ProcessStartInfo info = new(Path.Combine(AppContext.BaseDirectory, "owner-callback-tests"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(info)!;
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("callback simulation assertions: 93\n", process.StandardOutput.ReadToEnd());
        Assert.Equal("", process.StandardError.ReadToEnd());
    }

    [MacFact]
    public void RefusedDrainKeepsReplacementBlockedDuringAndAfterCallback()
    {
        ProcessStartInfo info = new(Path.Combine(AppContext.BaseDirectory, "owner-callback-tests"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--refuse-drain");
        using Process process = Process.Start(info)!;
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("refused drain assertions: 21\n", process.StandardOutput.ReadToEnd());
        Assert.Equal("", process.StandardError.ReadToEnd());
    }

    private static OwnerContext Create()
    {
        Assert.Equal(OwnerProbeStatus.ContextCreated, OwnerContext.Create(Guid.NewGuid(), 1, out OwnerContext? context));
        return Assert.IsType<OwnerContext>(context);
    }
}

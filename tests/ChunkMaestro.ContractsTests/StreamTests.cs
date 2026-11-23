using ChunkMaestro.Contracts;
using Xunit;

namespace ChunkMaestro.ContractsTests;

public sealed class StreamTests
{
    [Fact]
    public async Task MissingHalfCloseCancellationIsAwaitedAndLeavesCallerOwnership()
    {
        using CancellationTokenSource cancellation = new();
        using EndBehaviorStream stream = new(InspectCodec.EncodeRequest(CodecTests.Request), cancellation, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InspectCodec.ReadRequestAsync(stream, cancellation.Token));
        Assert.True(stream.SawDeadlineToken);
        Assert.Equal(0, stream.Pending);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task UnexpectedIoFailurePropagatesWithoutDisposalOrDetachedRead()
    {
        using CancellationTokenSource cancellation = new();
        using EndBehaviorStream stream = new(InspectCodec.EncodeRequest(CodecTests.Request), cancellation, true);
        await Assert.ThrowsAsync<IOException>(() => InspectCodec.ReadRequestAsync(stream, cancellation.Token));
        Assert.True(stream.SawDeadlineToken);
        Assert.Equal(0, stream.Pending);
        Assert.True(stream.CanRead);
    }
}

internal sealed class EndBehaviorStream(byte[] bytes, CancellationTokenSource cancellation, bool fault) : MemoryStream(bytes)
{
    internal bool SawDeadlineToken
    {
        get;
        private set;
    }
    internal int Pending
    {
        get;
        private set;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        SawDeadlineToken |= cancellationToken.CanBeCanceled;
        if (Position < Length)
        {
            return await base.ReadAsync(buffer, cancellationToken);
        }
        if (fault)
        {
            throw new IOException("Synthetic transport fault.");
        }
        Pending++;
        try
        {
            cancellation.Cancel();
            TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
            });
            return await completion.Task;
        }
        finally
        {
            Pending--;
        }
    }
}

using ChunkMaestro.OwnerVerification;
using System.Runtime.InteropServices;
using Xunit;

namespace ChunkMaestro.OwnerVerificationTests;

public sealed class EvaluationCompletionTests
{
    [Fact]
    public async Task RevokeRetainsCookieUntilMatchingTerminalDrain()
    {
        byte[] nonce = new byte[32];
        nonce[0] = 1;
        EvaluationCompletion completion = new(1, 2, 3, nonce);
        completion.Revoke();
        GC.Collect();
        Assert.False(completion.IsDrained);
        Assert.False(completion.Task.IsCompleted);
        GCHandle pinned = GCHandle.Alloc(nonce, GCHandleType.Pinned);
        try
        {
            Assert.Equal(0, EvaluationCompletion.Reply(99, 2, 3, pinned.AddrOfPinnedObject(), 32, 0, 0, completion.Cookie));
            Assert.False(completion.IsDrained);
            Assert.Equal(1, EvaluationCompletion.Reply(1, 2, 3, pinned.AddrOfPinnedObject(), 32, 0, 0, completion.Cookie));
            Assert.False(completion.IsDrained);
            Assert.False(completion.Task.IsCompleted);
            Assert.Equal(1, EvaluationCompletion.Reply(1, 2, 3, pinned.AddrOfPinnedObject(), 32, 0, 1, completion.Cookie));
            Assert.True(completion.IsDrained);
            Assert.Equal(RawPolicyObservation.Canceled, await completion.Task);
            completion.RefuseBeforeTransfer();
            Assert.Equal(RawPolicyObservation.Canceled, await completion.Task);
        }
        finally
        {
            pinned.Free();
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12345, 5)]
    public async Task SimulationChecksNonceAndMapsUnknownBeforeExactDrain(int result, int expected)
    {
        byte[] nonce = new byte[32];
        nonce[0] = 1;
        EvaluationCompletion completion = new(1, 2, 3, nonce);
        GCHandle pinned = GCHandle.Alloc(nonce, GCHandleType.Pinned);
        try
        {
            nonce[0] = 2;
            Assert.Equal(0, EvaluationCompletion.Reply(1, 2, 3, pinned.AddrOfPinnedObject(), 32, result, 0, completion.Cookie));
            Assert.False(completion.Task.IsCompleted);
            nonce[0] = 1;
            Assert.Equal(1, EvaluationCompletion.Reply(1, 2, 3, pinned.AddrOfPinnedObject(), 32, result, 0, completion.Cookie));
            Assert.False(completion.Task.IsCompleted);
            Assert.Equal(1, EvaluationCompletion.Reply(1, 2, 3, pinned.AddrOfPinnedObject(), 32, result, 1, completion.Cookie));
            Assert.Equal((RawPolicyObservation) expected, await completion.Task);
            Assert.True(completion.IsDrained);
        }
        finally
        {
            pinned.Free();
        }
    }
}

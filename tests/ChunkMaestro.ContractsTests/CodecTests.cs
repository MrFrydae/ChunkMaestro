using ChunkMaestro.Contracts;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ChunkMaestro.ContractsTests;

public sealed class CodecTests
{
    internal const string RequestId = "11111111-1111-1111-1111-111111111111";
    internal const string InstanceId = "22222222-2222-2222-2222-222222222222";
    internal const string Epoch = "33333333-3333-3333-3333-333333333333";
    internal static readonly string Revision = new('a', 64);
    internal static InspectRequest Request => new(RequestId, InstanceId, Revision);
    internal static InspectResponse Response => new(Request, Epoch, InspectOutcome.Observed, InspectReason.None,
        DateTimeOffset.Parse("2026-11-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 7, ServiceState.Active, ManagerPresence.Present);
    private static string Json => "{\"version\":1,\"action\":\"inspect\",\"requestId\":\"" + RequestId + "\",\"instanceId\":\"" + InstanceId + "\",\"policyRevision\":\"" + Revision + "\"}";

    [Fact]
    public async Task GoldenFrameAndAllFragmentSizesRoundTrip()
    {
        byte[] expectedPayload = Encoding.UTF8.GetBytes(Json);
        byte[] frame = InspectCodec.EncodeRequest(Request);
        Assert.Equal((uint) expectedPayload.Length, BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.Equal(expectedPayload, frame[4..]);
        for (int chunk = 1; chunk <= frame.Length; chunk++)
        {
            using FragmentStream stream = new(frame, chunk);
            InspectRequest decoded = await InspectCodec.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
            Assert.Equal(RequestId, decoded.RequestId);
            Assert.Equal(InstanceId, decoded.InstanceId);
            Assert.Equal(Revision, decoded.PolicyRevision);
            Assert.True(stream.CanRead);
            Assert.True(stream.LargestRead <= InspectCodec.RequestLimit);
        }
        byte[] response = InspectCodec.EncodeResponse(Response);
        using MemoryStream responseStream = new(response);
        InspectResponse result = await InspectCodec.ReadResponseAsync(responseStream, Request, TestContext.Current.CancellationToken);
        Assert.Equal(Response.ObservedAt, result.ObservedAt);
        Assert.Equal(ServiceState.Active, result.ServiceState);
        Assert.Equal(Epoch, result.HelperEpoch);
    }

    [Fact]
    public async Task ResponseFragmentationAndLengthCaps()
    {
        byte[] frame = InspectCodec.EncodeResponse(Response);
        for (int fragment = 1; fragment <= frame.Length; fragment++)
        {
            using FragmentStream stream = new(frame, fragment);
            InspectResponse result = await InspectCodec.ReadResponseAsync(stream, Request, TestContext.Current.CancellationToken);
            Assert.Equal(ServiceState.Active, result.ServiceState);
            Assert.True(stream.LargestRead <= InspectCodec.ResponseLimit);
        }
        foreach (uint length in new uint[]
        {
            0, 4097, uint.MaxValue
        })
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, length);
            using FragmentStream stream = new(header, 4);
            await Assert.ThrowsAsync<InvalidDataException>(() => InspectCodec.ReadResponseAsync(stream, Request, TestContext.Current.CancellationToken));
            Assert.Equal(1, stream.Reads);
        }
    }

    [Fact]
    public async Task EveryHeaderAndPayloadTruncationRefuses()
    {
        foreach (byte[] frame in new byte[][]
        {
            InspectCodec.EncodeRequest(Request), InspectCodec.EncodeResponse(Response)
        })
        {
            for (int length = 0; length < frame.Length; length++)
            {
                using MemoryStream stream = new(frame[..length]);
                if (frame[4..].AsSpan().SequenceEqual(InspectCodec.EncodeRequest(Request).AsSpan(4)))
                {
                    await Assert.ThrowsAsync<EndOfStreamException>(() => InspectCodec.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
                }
                else
                {
                    await Assert.ThrowsAsync<EndOfStreamException>(() => InspectCodec.ReadResponseAsync(stream, Request, TestContext.Current.CancellationToken));
                }
                Assert.True(stream.CanRead);
            }
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1025u)]
    [InlineData(uint.MaxValue)]
    public async Task OversizeRefusesBeforePayloadRead(uint length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        using FragmentStream stream = new(header, 4);
        await Assert.ThrowsAsync<InvalidDataException>(() => InspectCodec.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(1, stream.Reads);
        Assert.Equal(4, stream.LargestRead);
    }

    [Fact]
    public async Task ExtraByteAndSecondFrameRefuse()
    {
        byte[] frame = InspectCodec.EncodeRequest(Request);
        foreach (byte[] tail in new byte[][]
        {
            [0], frame
        })
        {
            using MemoryStream stream = new([.. frame, .. tail]);
            await Assert.ThrowsAsync<InvalidDataException>(() => InspectCodec.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void StrictRequestGrammarRefusesInjectionAndEquivalentDuplicates()
    {
        string[] invalid =
        [
            Json.Replace("\"version\":1", "\"version\":1.0", StringComparison.Ordinal),
            Json.Replace("\"version\":1", "\"version\":1e0", StringComparison.Ordinal),
            Json.Replace("\"version\":1", "\"version\":2147483648", StringComparison.Ordinal),
            Json.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            Json.Replace("inspect", "execute", StringComparison.Ordinal),
            Json.Replace("\"action\"", "\"Action\"", StringComparison.Ordinal),
            Json.Replace("\"action\":\"inspect\",", "", StringComparison.Ordinal),
            Json.Replace("\"inspect\"", "null", StringComparison.Ordinal),
            Json.Replace("\"inspect\"", "[1]", StringComparison.Ordinal),
            Json.Replace("\"inspect\"", "{\"x\":{\"y\":{\"z\":{\"a\":1}}}}", StringComparison.Ordinal),
            Json.Replace("{", "{\"\\u0076ersion\":1,", StringComparison.Ordinal),
            Json[..^1] + ",\"unit\":\"ssh.service\"}",
            Json[..^1] + ",\"env\":\"LD_PRELOAD=x\"}",
            Json[..^1] + ",}", "/*comment*/" + Json,
            Json.Replace(Revision, Revision.ToUpperInvariant(), StringComparison.Ordinal),
            Json.Replace(RequestId, Guid.Empty.ToString("D"), StringComparison.Ordinal),
            Json.Replace(RequestId, "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", StringComparison.Ordinal),
            Json.Replace(RequestId, "\\uD800", StringComparison.Ordinal)
        ];
        foreach (string input in invalid)
        {
            AssertRejected(Encoding.UTF8.GetBytes(input));
        }
        AssertRejected([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Json)]);
        AssertRejected([.. Encoding.UTF8.GetBytes(Json[..^2]), 0xff, 0x22, 0x7d]);
    }

    [Fact]
    public void TypedInputsAndResponseGrammarRefuseInvalidData()
    {
        Assert.Throws<ArgumentException>(() => new InspectRequest("", InstanceId, Revision));
        Assert.Throws<ArgumentException>(() => new InspectRequest(RequestId, InstanceId, "latest"));
        Assert.Throws<ArgumentException>(() => new InspectResponse(Request, Epoch, InspectOutcome.Observed, InspectReason.None, DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1)), 0, ServiceState.Active, ManagerPresence.Present));
        string json = Encoding.UTF8.GetString(InspectCodec.EncodeResponse(Response)[4..]);
        foreach (string invalid in new string[]
        {
            json.Replace("Observed", "observed", StringComparison.Ordinal),
            json.Replace("Observed", "0", StringComparison.Ordinal),
            json.Replace("Active", "Unknown", StringComparison.Ordinal),
            json.Replace("None", "NotLoaded", StringComparison.Ordinal),
            json.Replace("Present", "NotFound", StringComparison.Ordinal),
            json.Replace(".0000000Z", "Z", StringComparison.Ordinal),
            json.Replace(".0000000Z", ".0000000+00:00", StringComparison.Ordinal),
            json.Replace("\"durationMs\":7", "\"durationMs\":7.0", StringComparison.Ordinal),
            json.Replace("\"durationMs\":7", "\"durationMs\":3001", StringComparison.Ordinal),
            json.Replace("debian-systemd-system-bus", "fake", StringComparison.Ordinal),
            json.Replace(RequestId, Epoch, StringComparison.Ordinal),
            json[..^1] + ",\"\\u0072eason\":\"None\"}"
        })
        {
            Exception? error = Record.Exception(() => InspectCodec.DecodeResponse(Encoding.UTF8.GetBytes(invalid), Request));
            Assert.True(error is ArgumentException or InvalidDataException or JsonException or InvalidOperationException);
        }
    }

    [Theory]
    [InlineData(14)]
    [InlineData(205)]
    [InlineData(4048216)]
    public void BoundedDeterministicFuzzHasOnlyValidationFailures(int seed)
    {
        Random random = new(seed);
        byte[] valid = Encoding.UTF8.GetBytes(Json);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            byte[] candidate = iteration % 2 == 0 ? (byte[]) valid.Clone() : new byte[random.Next(0, 1100)];
            if (iteration % 2 == 0)
            {
                candidate[random.Next(candidate.Length)] ^= (byte) random.Next(1, 256);
            }
            else
            {
                random.NextBytes(candidate);
            }
            try
            {
                InspectRequest result = InspectCodec.DecodeRequest(candidate);
                Assert.Equal("inspect", result.Action);
                Assert.Equal(1, result.Version);
                Assert.InRange(InspectCodec.EncodeRequest(result).Length, 5, 1028);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException or InvalidOperationException)
            {
                Assert.NotEmpty(error.Message);
            }
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 1, 32_000_000);
    }

    private static void AssertRejected(byte[] input)
    {
        Exception? error = Record.Exception(() => InspectCodec.DecodeRequest(input));
        Assert.True(error is ArgumentException or InvalidDataException or JsonException or InvalidOperationException);
    }
}

internal sealed class FragmentStream(byte[] bytes, int fragment) : MemoryStream(bytes)
{
    internal int Reads
    {
        get;
        private set;
    }
    internal int LargestRead
    {
        get;
        private set;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reads++;
        LargestRead = Math.Max(LargestRead, buffer.Length);
        return base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], cancellationToken);
    }
}

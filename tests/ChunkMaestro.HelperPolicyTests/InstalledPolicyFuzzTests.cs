using ChunkMaestro.PrivilegedHelper;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ParsedData = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.ParsedInspectionPolicyData;
using Refusal = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.PolicyParseRefusal;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class InstalledPolicyFuzzTests(ITestOutputHelper output)
{
    private static string Outcome(byte[] bytes)
    {
        PolicyDecodeResult result = InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken);
        if (result is Refusal refusal)
        {
            Assert.True(Enum.IsDefined(refusal.Code));
            return refusal.Code.ToString();
        }
        ParsedData data = Assert.IsType<ParsedData>(result);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), data.PolicyRevision);
        Assert.Equal(1ul, data.Schema);
        Assert.NotEqual(data.AgentUid, data.GameUid);
        Assert.NotEqual(data.InspectorUid, data.GameUid);
        Assert.NotEqual(data.AgentUid, data.InspectorUid);
        Assert.InRange(data.DropIns.Count, 0, 8);
        Assert.Equal("/usr/lib/chunkmaestro/inspect/manifest.json", data.ManifestPath);
        Assert.StartsWith("/", data.GameRootPath);
        Assert.Equal(320u, data.Mode & 320u);
        Assert.Equal(0u, data.Mode & 18u);
        return data.PolicyRevision;
    }

    [Fact]
    public void DeterministicTenThousandByteCasesAndStructuralMutations()
    {
        Random random = new(14032026);
        for (int index = 0; index < 10000; index++)
        {
            byte[] bytes = new byte[random.Next(0, 16386)];
            random.NextBytes(bytes);
            Assert.Equal(Outcome(bytes), Outcome(bytes));
        }
        byte[] seed = InstalledPolicyCodecTests.Seed();
        int structural = 0;
        for (int index = 0; index < seed.Length; index++)
        {
            foreach (byte change in new byte[]
        {
            0, 0xff, (byte)'"', (byte)'0', (byte)'{'
        })
            {
                byte[] mutated = seed.ToArray();
                mutated[index] ^= change;
                Assert.Equal(Outcome(mutated), Outcome(mutated));
                structural++;
            }
            byte[] deleted = seed.Where((value, position) => position != index).ToArray();
            Assert.Equal(Outcome(deleted), Outcome(deleted));
            structural++;
            byte[] inserted = new byte[seed.Length + 1];
            seed.AsSpan(0, index).CopyTo(inserted);
            inserted[index] = 0xff;
            seed.AsSpan(index).CopyTo(inserted.AsSpan(index + 1));
            Assert.Equal(Outcome(inserted), Outcome(inserted));
            structural++;
        }
        output.WriteLine($"seed=14032026 arbitrary=10000 structural={structural}");
    }

    [Fact]
    public void ResourceDiagnosticsHaveNoArbitraryThreshold()
    {
        byte[] seed = InstalledPolicyCodecTests.Seed();
        byte[] max = Enumerable.Repeat((byte) ' ', 16384).ToArray();
        seed.CopyTo(max, 0);
        byte[][] cases = [seed, max, Encoding.UTF8.GetBytes(new string('[', 16384)), Encoding.UTF8.GetBytes("{\"x\":\"" + new string('a', 16370))];
        foreach (byte[] bytes in cases)
        {
            for (int index = 0; index < 10; index++)
            {
                Outcome(bytes);
            }
            long baseline = GC.GetTotalMemory(true);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            Stopwatch watch = Stopwatch.StartNew();
            for (int index = 0; index < 1000; index++)
            {
                InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken);
            }
            watch.Stop();
            long allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TimeSpan used = process.TotalProcessorTime - cpu;
            long retained = GC.GetTotalMemory(true) - baseline;
            Parallel.For(0, 1000, index =>
            {
                InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken);
            });
            long parallelRetained = GC.GetTotalMemory(true) - baseline;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bytes={bytes.Length} calls=1000 allocated/call={allocation / 1000.0:F1} wallMs={watch.Elapsed.TotalMilliseconds:F3} cpuMs={used.TotalMilliseconds:F3} retainedDelta={retained} parallelRetainedDelta={parallelRetained}"));
        }
    }
}

using ChunkMaestro.PrivilegedHelper;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using ParsedData = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.ParsedInspectionPolicyData;
using Refusal = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.PolicyParseRefusal;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class InstalledPolicySecurityTests
{
    [Fact]
    public void AllPermissionBitsAreDataWithExactRestrictionAndNoPrivilegedClaims()
    {
        for (int mode = 0; mode <= 512; mode++)
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["gameRoot"]!["mode"] = mode;
            PolicyDecodeResult result = InstalledPolicyCodecTests.Decode(tree);
            bool allowed = mode <= 511 && (mode & 256) != 0 && (mode & 64) != 0 && (mode & 16) == 0 && (mode & 2) == 0;
            if (allowed)
            {
                Assert.Equal((uint) mode, Assert.IsType<ParsedData>(result).Mode);
            }
            else
            {
                Assert.Equal(PolicyParseCode.InvalidBinding, Assert.IsType<Refusal>(result).Code);
            }
        }
    }

    [Fact]
    public void GeneratedValidClaimsAndFieldPermutationsStayImmutableData()
    {
        Random random = new(14019);
        for (int index = 0; index < 256; index++)
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            uint game = (uint) random.Next(2000, 100000);
            ulong inode = ((ulong) (uint) random.Next() << 32) | (uint) random.Next();
            tree["identities"]!["gameUid"] = game;
            tree["target"]!["gameRoot"]!["ownerUid"] = game;
            tree["target"]!["gameRoot"]!["inode"] = inode;
            JsonNode schema = tree["schema"]!;
            tree.Remove("schema");
            tree.Add("schema", schema);
            byte[] bytes = Encoding.UTF8.GetBytes(tree.ToJsonString());
            ParsedData data = Assert.IsType<ParsedData>(InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken));
            Assert.Equal(game, data.GameUid);
            Assert.Equal(game, data.OwnerUid);
            Assert.Equal(inode, data.Inode);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), data.PolicyRevision);
            tree["identities"]!["agentUid"] = game;
            Assert.Equal(PolicyParseCode.IdentityCollision, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
            Assert.Equal(game, data.GameUid);
        }
    }

    [Fact]
    public void PairwisePathBindingsAndCrossFieldPrecedenceAreFixed()
    {
        foreach (string path in new[]
        {
            "/etc/systemd/system/minecraft.service", "/usr/lib/chunkmaestro/inspect/manifest.json"
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["gameRoot"]!["path"] = path;
            Assert.Equal(PolicyParseCode.DuplicateBinding, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
        }
        JsonObject collision = InstalledPolicyCodecTests.Tree();
        collision["identities"]!["agentUid"] = 1003;
        collision["target"]!["gameRoot"]!["mode"] = 0;
        Assert.Equal(PolicyParseCode.IdentityCollision, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(collision)).Code);
        collision["schema"] = 2;
        Assert.Equal(PolicyParseCode.UnsupportedSchema, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(collision)).Code);
        JsonObject root = InstalledPolicyCodecTests.Tree();
        root["target"]!["gameRoot"]!["ownerUid"] = 0;
        root["target"]!["gameRoot"]!["ownerGid"] = 0;
        ParsedData data = Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(root));
        Assert.Equal(0u, data.OwnerUid);
        Assert.Equal(0u, data.OwnerGid);
        root["target"]!["gameRoot"]!["inode"] = 0;
        Assert.Equal(PolicyParseCode.NumberOutOfRange, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(root)).Code);
    }

    [Theory]
    [InlineData("target.fragment.sha256", "InvalidDigest")]
    [InlineData("target.unitName", "InvalidUnitName")]
    [InlineData("host.machineId", "InvalidIdentifier")]
    public void ValidSurrogatePairsReachFieldCategory(string path, string expected)
    {
        JsonObject tree = InstalledPolicyCodecTests.Tree();
        InstalledPolicyCodecTests.Parent(tree, path, out string name)[name] = "\U00010000";
        Assert.Equal(expected, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code.ToString());
    }

    [Fact]
    public void CancellationPrecedesSizeAndNeverReturnsARefusalOrPriorResult()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        foreach (int size in new[]
        {
            0, 1, 16384, 16385
        })
        {
            OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() => InstalledPolicyCodec.Decode(new byte[size], source.Token));
            Assert.Equal(source.Token, exception.CancellationToken);
        }
    }
}

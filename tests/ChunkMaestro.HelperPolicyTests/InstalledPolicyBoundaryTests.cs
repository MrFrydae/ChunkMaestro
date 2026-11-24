using ChunkMaestro.PrivilegedHelper;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using ParsedData = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.ParsedInspectionPolicyData;
using Refusal = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.PolicyParseRefusal;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class InstalledPolicyBoundaryTests
{
    public static IEnumerable<object[]> Scalars()
    {
        foreach (object[] item in InstalledPolicyCodecTests.FieldPaths())
        {
            string path = (string) item[0];
            JsonObject parent = InstalledPolicyCodecTests.Parent(InstalledPolicyCodecTests.Tree(), path, out string name);
            if (parent[name] is JsonValue scalar && scalar.TryGetValue<ulong>(out ulong number))
            {
                Assert.True(number <= ulong.MaxValue);
                foreach (string literal in new[]
        {
            "-0", "-1", "1e0", "1.0", "\"1\"", "+1", "01", "18446744073709551616", "111111111111111111111"
        })
                {
                    yield return new object[]
                    {
                        path, literal, literal.StartsWith('"') ? "WrongType" : literal is "+1" or "01" ? "MalformedJson" : literal.Length > 20 || literal == "18446744073709551616" ? "NumberOutOfRange" : "InvalidNumberLiteral"
                    };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scalars))]
    public void EveryNumericFieldUsesUnsignedLexicalGrammar(string path, string literal, string expected)
    {
        JsonObject tree = InstalledPolicyCodecTests.Tree();
        JsonObject parent = InstalledPolicyCodecTests.Parent(tree, path, out string name);
        string old = parent.ToJsonString();
        string changed = old.Replace("\"" + name + "\":" + parent[name]!.ToJsonString(), "\"" + name + "\":" + literal, StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(tree.ToJsonString().Replace(old, changed, StringComparison.Ordinal));
        InstalledPolicyCodecTests.Refuses(bytes, Enum.Parse<PolicyParseCode>(expected));
    }

    [Theory]
    [InlineData("host.machineId", "00000000000000000000000000000000", "InvalidIdentifier")]
    [InlineData("host.machineId", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "InvalidIdentifier")]
    [InlineData("instanceId", "00000000-0000-0000-0000-000000000000", "InvalidIdentifier")]
    [InlineData("instanceId", "11111111111141118111111111111111", "InvalidIdentifier")]
    [InlineData("host.deploymentId", "ABCDEFAB-1111-4111-8111-111111111111", "InvalidIdentifier")]
    [InlineData("target.unitName", "x@x.service", "InvalidUnitName")]
    [InlineData("target.unitName", "x.x.service", "InvalidUnitName")]
    [InlineData("target.unitName", "x service", "InvalidUnitName")]
    [InlineData("target.unitName", "é.service", "InvalidUnitName")]
    [InlineData("target.fragment.sha256", "ABC", "InvalidDigest")]
    [InlineData("target.fragment.path", "/etc/other.service", "InvalidBinding")]
    [InlineData("packageManifest.path", "/tmp/manifest.json", "InvalidBinding")]
    public void FieldGrammarRefusalsAreExact(string path, string value, string expected)
    {
        JsonObject tree = InstalledPolicyCodecTests.Tree();
        InstalledPolicyCodecTests.Parent(tree, path, out string name)[name] = value;
        Assert.Equal(expected, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code.ToString());
    }

    [Fact]
    public void NumericBoundariesPreservePrecisionAndCrossFieldRestrictions()
    {
        foreach (ulong inode in new[]
        {
            1ul, 9007199254740993ul, ulong.MaxValue
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["gameRoot"]!["inode"] = inode;
            tree["target"]!["gameRoot"]!["deviceMajor"] = uint.MaxValue;
            Assert.Equal(inode, Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(tree)).Inode);
        }
        foreach (string path in new[]
        {
            "identities.agentUid", "identities.inspectorUid", "identities.gameUid", "identities.agentGid", "identities.inspectorGid", "identities.gameGid", "target.gameRoot.ownerGid", "target.gameRoot.deviceMajor", "target.gameRoot.deviceMinor", "target.gameRoot.ownerUid"
        })
        {
            foreach (ulong value in new[]
        {
            0ul, 1ul, 4294967294ul, 4294967295ul, 4294967296ul
        })
            {
                JsonObject tree = InstalledPolicyCodecTests.Tree();
                InstalledPolicyCodecTests.Parent(tree, path, out string name)[name] = value;
                if (path.StartsWith("identities.", StringComparison.Ordinal))
                {
                    tree["target"]!["gameRoot"]!["ownerUid"] = 0;
                }
                PolicyDecodeResult result = InstalledPolicyCodecTests.Decode(tree);
                bool role = path.StartsWith("identities.", StringComparison.Ordinal);
                bool range = value > uint.MaxValue || (role && (value == 0 || value == uint.MaxValue)) || (path.EndsWith("ownerGid", StringComparison.Ordinal) && value == uint.MaxValue);
                if (range)
                {
                    Assert.Equal(PolicyParseCode.NumberOutOfRange, Assert.IsType<Refusal>(result).Code);
                }
                else if (path.EndsWith("ownerUid", StringComparison.Ordinal) && value != 0)
                {
                    Assert.Equal(PolicyParseCode.InvalidBinding, Assert.IsType<Refusal>(result).Code);
                }
                else
                {
                    Assert.IsType<ParsedData>(result);
                }
            }
        }
        foreach (string uid in new[]
        {
            "agentUid", "inspectorUid", "gameUid"
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["identities"]![uid] = uid == "agentUid" ? 1002 : 1001;
            Assert.Equal(PolicyParseCode.IdentityCollision, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
        }
        JsonObject shared = InstalledPolicyCodecTests.Tree();
        shared["identities"]!["agentGid"] = 1103;
        shared["identities"]!["inspectorGid"] = 1103;
        Assert.Equal(1103u, Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(shared)).AgentGid);
        foreach (uint mode in new[]
        {
            0u, 256u, 64u, 338u, 322u, 512u, 4095u, 320u, 493u
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["gameRoot"]!["mode"] = mode;
            if (mode is 320 or 493)
            {
                Assert.Equal(mode, Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(tree)).Mode);
            }
            else
            {
                Assert.Equal(PolicyParseCode.InvalidBinding, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
            }
        }
        foreach (ulong schema in new[]
        {
            2ul, ulong.MaxValue
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["schema"] = schema;
            Assert.Equal(PolicyParseCode.UnsupportedSchema, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
        }
    }

    [Fact]
    public void PathsUnitsDigestsAndBindingsRespectExactBounds()
    {
        List<string> invalid = ["", "/", "relative", "/a//b", "/a/./b", "/a/../b", "/a/", "/a:b", "/a\\b", "/a%b", "/~a", "/a\0b", "/a b", "/a\nb", "/é", "/" + new string('a', 256), "/" + string.Join('/', Enumerable.Repeat("a", 17))];
        string maxPath = "/" + string.Join('/', new[]
        {
            new string('a', 255), new string('b', 255), new string('c', 255), new string('d', 255)
        });
        Assert.Equal(1024, maxPath.Length);
        invalid.Add(maxPath + "a");
        foreach (string path in invalid)
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["gameRoot"]!["path"] = path;
            Assert.Equal(PolicyParseCode.InvalidPath, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
        }
        foreach (string path in new[]
        {
            maxPath, "/" + new string('a', 255), "/" + string.Join('/', Enumerable.Repeat("a", 16))
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["gameRoot"]!["path"] = path;
            Assert.Equal(path, Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(tree)).GameRootPath);
        }
        foreach (int length in new[]
        {
            1, 63, 64
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            string unit = new string('X', length) + ".service";
            tree["target"]!["unitName"] = unit;
            tree["target"]!["fragment"]!["path"] = "/etc/" + unit;
            if (length == 64)
            {
                Assert.Equal(PolicyParseCode.InvalidUnitName, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
            }
            else
            {
                Assert.Equal(unit, Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(tree)).UnitName);
            }
        }
        foreach (string digest in new[]
        {
            new string('0', 64), new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64)
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["fragment"]!["sha256"] = digest;
            if (digest == new string('0', 64))
            {
                Assert.Equal(digest, Assert.IsType<ParsedData>(InstalledPolicyCodecTests.Decode(tree)).FragmentSha256);
            }
            else
            {
                Assert.Equal(PolicyParseCode.InvalidDigest, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
            }
        }
        foreach (string second in new[]
        {
            "/etc/a.conf", "/etc/0.conf", "/etc/.conf"
        })
        {
            JsonObject tree = InstalledPolicyCodecTests.Tree();
            tree["target"]!["dropIns"] = new JsonArray
            {
                new JsonObject
                {
                    ["path"] = "/etc/a.conf", ["sha256"] = new string('0', 64)
                },
                new JsonObject
                {
                    ["path"] = second, ["sha256"] = new string('0', 64)
                }
            };
            Assert.Equal(second == "/etc/a.conf" ? PolicyParseCode.DuplicateBinding : second == "/etc/0.conf" ? PolicyParseCode.UnorderedDropIns : PolicyParseCode.InvalidBinding, Assert.IsType<Refusal>(InstalledPolicyCodecTests.Decode(tree)).Code);
        }
    }

    [Fact]
    public void SizeTruncationAndSchemaPrecedenceAreDeterministic()
    {
        byte[] seed = InstalledPolicyCodecTests.Seed();
        for (int length = 1; length < seed.Length; length++)
        {
            InstalledPolicyCodecTests.Refuses(seed[..length], PolicyParseCode.MalformedJson);
        }
        byte[] max = new byte[16384];
        Array.Fill(max, (byte) ' ');
        seed.CopyTo(max, 0);
        Assert.IsType<ParsedData>(InstalledPolicyCodec.Decode(max, TestContext.Current.CancellationToken));
        InstalledPolicyCodecTests.Refuses(new byte[16385], PolicyParseCode.InputTooLarge);
        InstalledPolicyCodecTests.Refuses(new byte[1000000], PolicyParseCode.InputTooLarge);
        foreach (string json in new[]
        {
            "null", "true", "1", "[]", "\"ok\""
        })
        {
            InstalledPolicyCodecTests.Refuses(Encoding.UTF8.GetBytes(json), PolicyParseCode.WrongType);
        }
        foreach (string json in new[]
        {
            "{\"x\":1,}", "{/*x*/}", "{\"schema\":01}"
        })
        {
            InstalledPolicyCodecTests.Refuses(Encoding.UTF8.GetBytes(json), PolicyParseCode.MalformedJson);
        }
        InstalledPolicyCodecTests.Refuses("{\"schema\":2,\"unknown\":1}"u8.ToArray(), PolicyParseCode.UnknownField);
        InstalledPolicyCodecTests.Refuses("{\"schema\":2}"u8.ToArray(), PolicyParseCode.MissingField);
    }
}

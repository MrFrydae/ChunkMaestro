using ChunkMaestro.Contracts;
using ChunkMaestro.PrivilegedHelper;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using ParsedData = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.ParsedInspectionPolicyData;
using Refusal = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.PolicyParseRefusal;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class InstalledPolicyCodecTests
{
    internal static byte[] Seed()
    {
        using Stream stream = typeof(InstalledPolicyCodecTests).Assembly.GetManifestResourceStream("ChunkMaestro.HelperPolicyTests.classification-cases.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        return Convert.FromHexString(document.RootElement.GetProperty("cases")[0].GetProperty("utf8Hex").GetString()!);
    }

    public static IEnumerable<object[]> Vectors()
    {
        using Stream stream = typeof(InstalledPolicyCodecTests).Assembly.GetManifestResourceStream("ChunkMaestro.HelperPolicyTests.classification-cases.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[]
            {
                item.GetProperty("id").GetString()!, item.GetProperty("utf8Hex").GetString()!, item.GetProperty("expected").GetString()!
            };
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void FrozenDesignVectorsExecuteExactBytes(string id, string hex, string expected)
    {
        Assert.NotEmpty(id);
        byte[] bytes = Convert.FromHexString(hex);
        PolicyDecodeResult result = InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken);
        if (expected == "ParsedInspectionPolicyData")
        {
            ParsedData parsed = Assert.IsType<ParsedData>(result);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), parsed.PolicyRevision);
            Assert.Equal("11111111-1111-4111-8111-111111111111", parsed.InstanceId);
        }
        else
        {
            Assert.Equal(expected, Assert.IsType<Refusal>(result).Code.ToString());
        }
    }

    internal static JsonObject Tree()
    {
        return JsonNode.Parse(Seed())!.AsObject();
    }

    internal static PolicyDecodeResult Decode(JsonObject tree)
    {
        return InstalledPolicyCodec.Decode(Encoding.UTF8.GetBytes(tree.ToJsonString()), TestContext.Current.CancellationToken);
    }

    internal static void Refuses(byte[] bytes, PolicyParseCode expected)
    {
        Assert.Equal(expected, Assert.IsType<Refusal>(InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken)).Code);
    }

    internal static JsonObject Parent(JsonObject tree, string path, out string name)
    {
        string[] parts = path.Split('.');
        JsonObject current = tree;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            current = parts[index] == "0" ? current : current[parts[index]]!.AsObject();
        }
        name = parts[^1];
        return current;
    }

    public static IEnumerable<object[]> FieldPaths()
    {
        return Walk(Tree(), "");
    }

    private static IEnumerable<object[]> Walk(JsonObject tree, string prefix)
    {
        foreach (KeyValuePair<string, JsonNode?> entry in tree)
        {
            string path = prefix + entry.Key;
            yield return new object[]
            {
                path
            };
            if (entry.Value is JsonObject child)
            {
                foreach (object[] item in Walk(child, path + "."))
                {
                    yield return item;
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(FieldPaths))]
    public void EveryClosedFieldMissingDuplicateEscapedDuplicateUnknownCaseNullAndWrongKind(string path)
    {
        JsonObject tree = Tree();
        JsonObject parent = Parent(tree, path, out string name);
        string original = parent.ToJsonString();
        parent.Remove(name);
        Assert.Equal(PolicyParseCode.MissingField, Assert.IsType<Refusal>(Decode(tree)).Code);
        foreach (string replacement in new[]
        {
            "null", "true", "[]", "{}", "1", "\"text\""
        })
        {
            tree = Tree();
            parent = Parent(tree, path, out name);
            if (replacement[0] == parent[name]!.ToJsonString()[0] || (replacement == "1" && char.IsAsciiDigit(parent[name]!.ToJsonString()[0])))
            {
                continue;
            }
            parent[name] = JsonNode.Parse(replacement);
            Assert.Equal(PolicyParseCode.WrongType, Assert.IsType<Refusal>(Decode(tree)).Code);
        }
        foreach (string key in new[]
        {
            name, "\\u" + ((int)name[0]).ToString("x4", CultureInfo.InvariantCulture) + name[1..], name.ToUpperInvariant(), "unknown"
        })
        {
            tree = Tree();
            parent = Parent(tree, path, out name);
            string changed = original.Insert(1, "\"" + key + "\":" + parent[name]!.ToJsonString() + ",");
            string full = tree.ToJsonString().Replace(original, changed, StringComparison.Ordinal);
            Refuses(Encoding.UTF8.GetBytes(full), key == name || key.StartsWith("\\u", StringComparison.Ordinal) ? PolicyParseCode.DuplicateField : PolicyParseCode.UnknownField);
        }
    }

    [Fact]
    public void DropInObjectsAreClosedAndCollectionLimitsAreExact()
    {
        foreach (int count in new[]
        {
            0, 8, 9
        })
        {
            JsonObject tree = Tree();
            JsonArray array = tree["target"]!["dropIns"]!.AsArray();
            for (int index = 0; index < count; index++)
            {
                array.Add(new JsonObject
                {
                    ["path"] = $"/etc/unit/{index:D2}.conf",
                    ["sha256"] = new string('a', 64)
                });
            }
            if (count == 9)
            {
                Assert.Equal(PolicyParseCode.CollectionLimit, Assert.IsType<Refusal>(Decode(tree)).Code);
            }
            else
            {
                ParsedData parsed = Assert.IsType<ParsedData>(Decode(tree));
                Assert.Equal(count, parsed.DropIns.Count);
                for (int index = 0; index < count; index++)
                {
                    Assert.Equal($"/etc/unit/{index:D2}.conf", parsed.DropIns[index].Key);
                    Assert.Equal(new string('a', 64), parsed.DropIns[index].Value);
                }
                Assert.Throws<NotSupportedException>(() => ((IList) parsed.DropIns).Clear());
            }
        }
        foreach (string json in new[]
        {
            "{}", "{\"path\":\"/x/a.conf\"}", "{\"sha256\":\"" + new string('a', 64) + "\"}", "null", "true"
        })
        {
            JsonObject tree = Tree();
            tree["target"]!["dropIns"] = JsonNode.Parse("[" + json + "]");
            Assert.Equal(json is "null" or "true" ? PolicyParseCode.WrongType : PolicyParseCode.MissingField, Assert.IsType<Refusal>(Decode(tree)).Code);
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("sha256")]
    public void DropInFieldsRejectMissingDuplicatesEscapedDuplicatesUnknownNullAndWrongKinds(string field)
    {
        string binding = "{\"path\":\"/etc/a.conf\",\"sha256\":\"" + new string('a', 64) + "\"}";
        JsonObject item = JsonNode.Parse(binding)!.AsObject();
        string value = item[field]!.ToJsonString();
        foreach (string replacement in new[]
        {
            "null", "true", "{}", "[]", "1"
        })
        {
            JsonObject tree = Tree();
            item = JsonNode.Parse(binding)!.AsObject();
            item[field] = JsonNode.Parse(replacement);
            tree["target"]!["dropIns"] = new JsonArray(item);
            Assert.Equal(replacement is "{}" or "[]" ? PolicyParseCode.DepthExceeded : PolicyParseCode.WrongType, Assert.IsType<Refusal>(Decode(tree)).Code);
        }
        foreach (string name in new[]
        {
            field, "\\u" + ((int)field[0]).ToString("x4", CultureInfo.InvariantCulture) + field[1..], "unknown", field.ToUpperInvariant()
        })
        {
            string changed = binding.Insert(1, "\"" + name + "\":" + value + ",");
            string json = Encoding.UTF8.GetString(Seed()).Replace("\"dropIns\":[]", "\"dropIns\":[" + changed + "]", StringComparison.Ordinal);
            Refuses(Encoding.UTF8.GetBytes(json), name == field || name.StartsWith("\\u", StringComparison.Ordinal) ? PolicyParseCode.DuplicateField : PolicyParseCode.UnknownField);
        }
    }

    [Fact]
    public async Task BoundedCancellationStressPublishesOnlyCompleteDataOrCallerCancellation()
    {
        byte[] seed = Seed();
        for (int index = 0; index < 128; index++)
        {
            using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            Task<PolicyDecodeResult> pending = Task.Run(() => InstalledPolicyCodec.Decode(seed, source.Token), TestContext.Current.CancellationToken);
            source.Cancel();
            try
            {
                ParsedData parsed = Assert.IsType<ParsedData>(await pending);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(seed)), parsed.PolicyRevision);
            }
            catch (OperationCanceledException exception)
            {
                Assert.Equal(source.Token, exception.CancellationToken);
            }
        }
    }

    [Fact]
    public void EveryScalarAndExactRevisionSurvivesCallerMutationWithoutPreviousResultReuse()
    {
        byte[] bytes = Seed();
        ParsedData parsed = Assert.IsType<ParsedData>(InstalledPolicyCodec.Decode(bytes, TestContext.Current.CancellationToken));
        Assert.Equal(1ul, parsed.Schema);
        Assert.Equal(new string('1', 32), parsed.MachineId);
        Assert.Equal("33333333-3333-4333-8333-333333333333", parsed.DeploymentId);
        Assert.Equal("11111111-1111-4111-8111-111111111111", parsed.InstanceId);
        Assert.Equal(1001u, parsed.AgentUid);
        Assert.Equal(1002u, parsed.InspectorUid);
        Assert.Equal(1003u, parsed.GameUid);
        Assert.Equal(1101u, parsed.AgentGid);
        Assert.Equal(1102u, parsed.InspectorGid);
        Assert.Equal(1103u, parsed.GameGid);
        Assert.Equal("minecraft.service", parsed.UnitName);
        Assert.Equal("/srv/minecraft", parsed.GameRootPath);
        Assert.Equal(1003u, parsed.OwnerUid);
        Assert.Equal(1103u, parsed.OwnerGid);
        Assert.Equal(8u, parsed.DeviceMajor);
        Assert.Equal(1u, parsed.DeviceMinor);
        Assert.Equal(123ul, parsed.Inode);
        Assert.Equal(493u, parsed.Mode);
        Assert.Equal("/etc/systemd/system/minecraft.service", parsed.FragmentPath);
        Assert.Equal(new string('a', 64), parsed.FragmentSha256);
        Assert.Equal("/usr/lib/chunkmaestro/inspect/manifest.json", parsed.ManifestPath);
        Assert.Equal(new string('b', 64), parsed.ManifestSha256);
        Assert.Empty(parsed.DropIns);
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal("663578efd25eb009f3a6900cd447686f3a4825025824e78b462ecf8625e576d9", parsed.PolicyRevision);
        Array.Fill(bytes, (byte) 0);
        Refuses(bytes, PolicyParseCode.MalformedJson);
        Assert.Equal(digest, parsed.PolicyRevision);
        Assert.Equal("/srv/minecraft", parsed.GameRootPath);
        Assert.NotSame(parsed, InstalledPolicyCodec.Decode(Seed(), TestContext.Current.CancellationToken));
        foreach (string spelling in new[]
        {
            Encoding.UTF8.GetString(Seed()) + "\n", Encoding.UTF8.GetString(Seed()).Replace("schema", "sch\\u0065ma", StringComparison.Ordinal), Tree().ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        })
        })
        {
            byte[] variant = Encoding.UTF8.GetBytes(spelling);
            ParsedData other = Assert.IsType<ParsedData>(InstalledPolicyCodec.Decode(variant, TestContext.Current.CancellationToken));
            Assert.Equal(parsed.InstanceId, other.InstanceId);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(variant)), other.PolicyRevision);
            Assert.NotEqual(digest, other.PolicyRevision);
        }
    }

    [Fact]
    public async Task CancellationParallelAndRequestCompatibilityRemainDataOnly()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() => InstalledPolicyCodec.Decode(Seed(), cancelled.Token));
        Assert.Equal(cancelled.Token, exception.CancellationToken);
        Parallel.For(0, 256, index =>
        {
            ParsedData data = Assert.IsType<ParsedData>(InstalledPolicyCodec.Decode(Seed(), TestContext.Current.CancellationToken));
            Assert.Equal(1003u, data.GameUid);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Seed())), data.PolicyRevision);
            Refuses([0], PolicyParseCode.MalformedJson);
        });
        ParsedData parsed = Assert.IsType<ParsedData>(InstalledPolicyCodec.Decode(Seed(), TestContext.Current.CancellationToken));
        InspectRequest request = new("22222222-2222-4222-8222-222222222222", parsed.InstanceId, parsed.PolicyRevision);
        using MemoryStream stream = new(InspectCodec.EncodeRequest(request));
        InspectRequest roundTrip = await InspectCodec.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(parsed.InstanceId, roundTrip.InstanceId);
        Assert.Equal(parsed.PolicyRevision, roundTrip.PolicyRevision);
        Assert.Equal(request.RequestId, roundTrip.RequestId);
    }

    [Fact]
    public void ExportAndDependencySurfaceHasNoPublicPolicyOrNativeOrIoInput()
    {
        Assembly assembly = typeof(InstalledPolicyCodec).Assembly;
        Assert.Empty(assembly.GetExportedTypes());
        Assert.All(assembly.GetReferencedAssemblies(), reference => Assert.True(reference.Name == "netstandard" || reference.Name!.StartsWith("System.", StringComparison.Ordinal)));
        InternalsVisibleToAttribute friend = Assert.Single(assembly.GetCustomAttributes<InternalsVisibleToAttribute>());
        Assert.Equal("ChunkMaestro.HelperPolicyTests", friend.AssemblyName);
        HashSet<string> journalImports = new(StringComparer.Ordinal);
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    if (type == typeof(LinuxPolicyJournalDurability) || type.DeclaringType == typeof(LinuxPolicyJournalDurability))
                    {
                        Assert.False(type.IsPublic);
                        DllImportAttribute journalImport = Assert.IsType<DllImportAttribute>(method.GetCustomAttribute<DllImportAttribute>());
                        Assert.Equal("libc.so.6", journalImport.Value);
                        Assert.True(journalImport.EntryPoint is "open" or "fsync" or "close");
                        Assert.True(journalImports.Add(journalImport.EntryPoint!));
                        Assert.Equal(DllImportSearchPath.SafeDirectories,
                            Assert.IsType<DefaultDllImportSearchPathsAttribute>(method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()).Paths);
                        continue;
                    }
                    Assert.True(type == typeof(PolicySnapshotNative) || type.DeclaringType == typeof(PolicySnapshotNative));
                    Assert.False(type.IsPublic);
                    DllImportAttribute import = Assert.IsType<DllImportAttribute>(method.GetCustomAttribute<DllImportAttribute>());
                    Assert.Equal("chunkmaestro_policy_snapshot_v1", import.Value);
                    Assert.StartsWith("cm_policy_snapshot_v1_", import.EntryPoint, StringComparison.Ordinal);
                }
            }
        }
        Assert.Equal(3, journalImports.Count);
        Assert.All(typeof(ParsedData).GetProperties(BindingFlags.NonPublic | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        foreach (string proof in new[]
        {
            "trusted", "qualified", "profileQualified", "expectedUid", "success", "operations", "pathOverride", "policyRevision"
        })
        {
            JsonObject tree = Tree();
            tree[proof] = false;
            Assert.Equal(PolicyParseCode.UnknownField, Assert.IsType<Refusal>(Decode(tree)).Code);
        }
    }
}

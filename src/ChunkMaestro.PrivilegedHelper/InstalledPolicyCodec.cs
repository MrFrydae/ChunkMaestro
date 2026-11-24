using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChunkMaestro.PrivilegedHelper;

internal abstract partial class PolicyDecodeResult
{
    internal sealed partial class ParsedInspectionPolicyData
    {
        // Temporary schema nodes never escape, retain input, or become trusted facts.
        private sealed class Node
        {
            internal Dictionary<string, Node> Fields
            {
                get;
            } = new(StringComparer.Ordinal);
            internal List<Node> Items
            {
                get;
            } = [];
            internal string? Text
            {
                get;
                set;
            }
            internal ulong Number
            {
                get;
                set;
            }
        }

        internal static PolicyDecodeResult Decode(ReadOnlySpan<byte> input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (input.Length == 0)
            {
                return PolicyParseRefusal.Create(PolicyParseCode.EmptyInput);
            }
            if (input.Length > 16384)
            {
                return PolicyParseRefusal.Create(PolicyParseCode.InputTooLarge);
            }
            byte[] snapshot = input.ToArray();
            token.ThrowIfCancellationRequested();
            if (snapshot.Length >= 3 && snapshot[0] == 0xef && snapshot[1] == 0xbb && snapshot[2] == 0xbf)
            {
                return PolicyParseRefusal.Create(PolicyParseCode.InvalidEncoding);
            }
            try
            {
                new UTF8Encoding(false, true).GetCharCount(snapshot);
            }
            catch (DecoderFallbackException)
            {
                return PolicyParseRefusal.Create(PolicyParseCode.InvalidEncoding);
            }
            PolicyParseCode? syntax = ValidateSyntax(snapshot, token);
            if (syntax.HasValue)
            {
                return PolicyParseRefusal.Create(syntax.Value);
            }
            Utf8JsonReader reader = new(snapshot, new JsonReaderOptions
            {
                MaxDepth = 5
            });
            Read(ref reader, token);
            PolicyParseCode? error = ReadValue(ref reader, "root", token, out Node root);
            if (error.HasValue)
            {
                return PolicyParseRefusal.Create(error.Value);
            }
            error = CrossCheck(root);
            if (error.HasValue)
            {
                return PolicyParseRefusal.Create(error.Value);
            }
            token.ThrowIfCancellationRequested();
            string revision = Convert.ToHexStringLower(SHA256.HashData(snapshot));
            ParsedInspectionPolicyData result = new(root, revision);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private static PolicyParseCode? ValidateSyntax(byte[] snapshot, CancellationToken token)
        {
            Utf8JsonReader reader = new(snapshot, new JsonReaderOptions
            {
                MaxDepth = 5
            });
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool read;
                try
                {
                    read = reader.Read();
                }
                catch (JsonException)
                {
                    return PolicyParseCode.MalformedJson;
                }
                if (!read)
                {
                    return PolicyParseCode.MalformedJson;
                }
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth == 4)
                {
                    return PolicyParseCode.DepthExceeded;
                }
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                {
                    // The only InvalidOperationException mapping: guarded string accessor.
                    try
                    {
                        reader.GetString();
                    }
                    catch (InvalidOperationException)
                    {
                        return PolicyParseCode.MalformedJson;
                    }
                }
                if (reader.CurrentDepth == 0 && reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.PropertyName))
                {
                    break;
                }
            }
            for (int index = (int) reader.BytesConsumed; index < snapshot.Length; index++)
            {
                if (snapshot[index] is not (0x20 or 0x09 or 0x0a or 0x0d))
                {
                    return PolicyParseCode.TrailingData;
                }
            }
            return null;
        }

        private static void Read(ref Utf8JsonReader reader, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // The complete syntax pass guarantees this immutable snapshot is valid.
            if (!reader.Read())
            {
                throw new InvalidOperationException("Validated syntax invariant failed.");
            }
        }

        private static string[]? Fields(string kind)
        {
            return kind switch
            {
                "root" => ["schema", "host", "identities", "instanceId", "target", "packageManifest"],
                "host" => ["machineId", "deploymentId"],
                "identities" => ["agentUid", "inspectorUid", "gameUid", "agentGid", "inspectorGid", "gameGid"],
                "target" => ["unitName", "gameRoot", "fragment", "dropIns"],
                "gameRoot" => ["path", "ownerUid", "ownerGid", "deviceMajor", "deviceMinor", "inode", "mode"],
                "fragment" or "dropIn" or "packageManifest" => ["path", "sha256"],
                _ => null
            };
        }

        private static PolicyParseCode? ReadValue(ref Utf8JsonReader reader, string kind, CancellationToken token, out Node node)
        {
            node = new Node();
            Stack<(Node Value, string[]? Names)> stack = new();
            PolicyParseCode? error = StartValue(ref reader, kind, node, stack);
            if (error.HasValue || stack.Count == 0)
            {
                return error;
            }
            Read(ref reader, token);
            while (stack.Count > 0)
            {
                (Node parent, string[]? names) = stack.Peek();
                bool array = names is null;
                if (reader.TokenType == (array ? JsonTokenType.EndArray : JsonTokenType.EndObject))
                {
                    if (!array && parent.Fields.Count != names!.Length)
                    {
                        return PolicyParseCode.MissingField;
                    }
                    stack.Pop();
                    if (stack.Count != 0)
                    {
                        Read(ref reader, token);
                    }
                    continue;
                }
                string childKind;
                if (array)
                {
                    if (parent.Items.Count == 8)
                    {
                        return PolicyParseCode.CollectionLimit;
                    }
                    childKind = "dropIn";
                }
                else
                {
                    childKind = reader.GetString()!;
                    if (!names!.Contains(childKind, StringComparer.Ordinal))
                    {
                        return PolicyParseCode.UnknownField;
                    }
                    if (parent.Fields.ContainsKey(childKind))
                    {
                        return PolicyParseCode.DuplicateField;
                    }
                    Read(ref reader, token);
                }
                Node child = new();
                error = StartValue(ref reader, childKind, child, stack);
                if (error.HasValue)
                {
                    return error;
                }
                if (array)
                {
                    parent.Items.Add(child);
                }
                else
                {
                    parent.Fields.Add(childKind, child);
                }
                Read(ref reader, token);
            }
            return null;
        }

        private static PolicyParseCode? StartValue(ref Utf8JsonReader reader, string kind, Node node, Stack<(Node Value, string[]? Names)> stack)
        {
            string[]? fields = Fields(kind);
            if (fields is not null || kind == "dropIns")
            {
                if (reader.TokenType != (fields is null ? JsonTokenType.StartArray : JsonTokenType.StartObject))
                {
                    return PolicyParseCode.WrongType;
                }
                stack.Push((node, fields));
                return null;
            }
            if (kind is "schema" or "agentUid" or "inspectorUid" or "gameUid" or "agentGid" or "inspectorGid" or "gameGid" or "ownerUid" or "ownerGid" or "deviceMajor" or "deviceMinor" or "inode" or "mode")
            {
                if (reader.TokenType != JsonTokenType.Number)
                {
                    return PolicyParseCode.WrongType;
                }
                ReadOnlySpan<byte> digits = reader.ValueSpan;
                foreach (byte digit in digits)
                {
                    if (digit < '0' || digit > '9')
                    {
                        return PolicyParseCode.InvalidNumberLiteral;
                    }
                }
                if (digits.Length > 20 || !reader.TryGetUInt64(out ulong number))
                {
                    return PolicyParseCode.NumberOutOfRange;
                }
                ulong maximum = kind is "schema" or "inode" ? ulong.MaxValue : uint.MaxValue;
                if (number > maximum || (kind == "inode" && number == 0) || (kind.EndsWith("Gid", StringComparison.Ordinal) && number == uint.MaxValue) || (kind is "agentUid" or "inspectorUid" or "gameUid" or "agentGid" or "inspectorGid" or "gameGid" && (number == 0 || number == uint.MaxValue)))
                {
                    return PolicyParseCode.NumberOutOfRange;
                }
                node.Number = number;
                return null;
            }
            if (reader.TokenType != JsonTokenType.String)
            {
                return PolicyParseCode.WrongType;
            }
            node.Text = reader.GetString()!;
            return kind switch
            {
                "instanceId" or "deploymentId" => IsUuid(node.Text) ? null : PolicyParseCode.InvalidIdentifier,
                "machineId" => IsHex(node.Text, 32) && node.Text.Any(character => character != '0') ? null : PolicyParseCode.InvalidIdentifier,
                "sha256" => IsHex(node.Text, 64) ? null : PolicyParseCode.InvalidDigest,
                "path" => IsPath(node.Text) ? null : PolicyParseCode.InvalidPath,
                "unitName" => IsUnit(node.Text) ? null : PolicyParseCode.InvalidUnitName,
                _ => throw new InvalidOperationException("Closed schema invariant failed.")
            };
        }

        private static bool IsUuid(string text)
        {
            return Guid.TryParseExact(text, "D", out Guid id) && id != Guid.Empty && id.ToString("D", CultureInfo.InvariantCulture) == text;
        }

        private static bool IsHex(string text, int length)
        {
            return text.Length == length && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }

        private static bool IsNameCharacter(char character)
        {
            return character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
        }

        private static bool IsUnit(string text)
        {
            return text.Length is >= 9 and <= 71 && text.EndsWith(".service", StringComparison.Ordinal) && char.IsAsciiLetterOrDigit(text[0]) && text.AsSpan(0, text.Length - 8).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-".AsSpan()) == false;
        }

        private static bool IsPath(string text)
        {
            if (text.Length is < 2 or > 1024 || text[0] != '/')
            {
                return false;
            }
            string[] components = text[1..].Split('/');
            return components.Length <= 16 && components.All(component => component.Length is >= 1 and <= 255 && component is not ("." or "..") && component.All(character => IsNameCharacter(character) || character == '.'));
        }

        private static PolicyParseCode? CrossCheck(Node root)
        {
            if (root.Fields["schema"].Number != 1)
            {
                return PolicyParseCode.UnsupportedSchema;
            }
            Dictionary<string, Node> ids = root.Fields["identities"].Fields;
            if (ids["agentUid"].Number == ids["inspectorUid"].Number || ids["agentUid"].Number == ids["gameUid"].Number || ids["inspectorUid"].Number == ids["gameUid"].Number)
            {
                return PolicyParseCode.IdentityCollision;
            }
            Dictionary<string, Node> target = root.Fields["target"].Fields;
            Dictionary<string, Node> game = target["gameRoot"].Fields;
            ulong owner = game["ownerUid"].Number;
            ulong mode = game["mode"].Number;
            if ((owner != 0 && owner != ids["gameUid"].Number) || mode > 511 || (mode & 320) != 320 || (mode & 18) != 0)
            {
                return PolicyParseCode.InvalidBinding;
            }
            string fragment = target["fragment"].Fields["path"].Text!;
            if (fragment[(fragment.LastIndexOf('/') + 1)..] != target["unitName"].Text)
            {
                return PolicyParseCode.InvalidBinding;
            }
            string manifest = root.Fields["packageManifest"].Fields["path"].Text!;
            if (manifest != "/usr/lib/chunkmaestro/inspect/manifest.json")
            {
                return PolicyParseCode.InvalidBinding;
            }
            HashSet<string> paths = new(StringComparer.Ordinal);
            foreach (string path in new[]
            {
                game["path"].Text!, fragment, manifest
            })
            {
                if (!paths.Add(path))
                {
                    return PolicyParseCode.DuplicateBinding;
                }
            }
            string? previous = null;
            bool unordered = false;
            foreach (Node binding in target["dropIns"].Items)
            {
                string path = binding.Fields["path"].Text!;
                string basename = path[(path.LastIndexOf('/') + 1)..];
                if (basename.Length <= 5 || !basename.EndsWith(".conf", StringComparison.Ordinal))
                {
                    return PolicyParseCode.InvalidBinding;
                }
                if (!paths.Add(path))
                {
                    return PolicyParseCode.DuplicateBinding;
                }
                unordered |= previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0;
                previous = path;
            }
            return unordered ? PolicyParseCode.UnorderedDropIns : null;
        }
    }
}

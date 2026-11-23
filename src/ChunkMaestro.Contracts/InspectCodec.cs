using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ChunkMaestro.Contracts;

/// <summary>Data codec only. Callers own streams, authentication and write-half shutdown.</summary>
public static class InspectCodec
{
    public const int RequestLimit = 1024;
    public const int ResponseLimit = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequestFields = ["version", "action", "requestId", "instanceId", "policyRevision"];
    private static readonly string[] ResponseFields = ["version", "requestId", "instanceId", "policyRevision", "helperEpoch", "outcome", "reason", "observedAt", "durationMs", "serviceState", "source", "managerPresence"];

    public static byte[] EncodeRequest(InspectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Encode(writer =>
        {
            writer.WriteNumber("version", request.Version);
            writer.WriteString("action", request.Action);
            WriteCorrelation(writer, request);
        }, RequestLimit);
    }

    public static byte[] EncodeResponse(InspectResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Encode(writer =>
        {
            writer.WriteNumber("version", response.Request.Version);
            WriteCorrelation(writer, response.Request);
            writer.WriteString("helperEpoch", response.HelperEpoch);
            writer.WriteString("outcome", response.Outcome.ToString());
            writer.WriteString("reason", response.Reason.ToString());
            writer.WriteString("observedAt", ContractValidation.Timestamp(response.ObservedAt));
            writer.WriteNumber("durationMs", response.DurationMs);
            writer.WriteString("serviceState", response.ServiceState.ToString());
            writer.WriteString("source", response.Source);
            writer.WriteString("managerPresence", response.ManagerPresence.ToString());
        }, ResponseLimit);
    }

    public static InspectRequest DecodeRequest(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = Parse(payload, RequestLimit);
        Dictionary<string, JsonElement> fields = Fields(document, RequestFields);
        Integer(fields["version"], 1, 1);
        if (String(fields["action"]) != "inspect")
        {
            throw new InvalidDataException("Invalid action.");
        }
        return Request(fields);
    }

    public static InspectResponse DecodeResponse(ReadOnlyMemory<byte> payload, InspectRequest expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using JsonDocument document = Parse(payload, ResponseLimit);
        Dictionary<string, JsonElement> fields = Fields(document, ResponseFields);
        Integer(fields["version"], 1, 1);
        InspectRequest request = Request(fields);
        if (request.RequestId != expected.RequestId || request.InstanceId != expected.InstanceId || request.PolicyRevision != expected.PolicyRevision ||
            String(fields["source"]) != "debian-systemd-system-bus")
        {
            throw new InvalidDataException("Invalid correlation or source.");
        }
        string timestamp = String(fields["observedAt"]);
        if (!DateTimeOffset.TryParseExact(timestamp, ContractValidation.TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset observedAt) || ContractValidation.Timestamp(observedAt) != timestamp)
        {
            throw new InvalidDataException("Invalid UTC timestamp.");
        }
        return new InspectResponse(request, String(fields["helperEpoch"]), EnumValue<InspectOutcome>(fields["outcome"]),
            EnumValue<InspectReason>(fields["reason"]), observedAt, Integer(fields["durationMs"], 0, 3000),
            EnumValue<ServiceState>(fields["serviceState"]), EnumValue<ManagerPresence>(fields["managerPresence"]));
    }

    public static async Task<InspectRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] payload = await ReadPayloadAsync(stream, RequestLimit, cancellationToken).ConfigureAwait(false);
        return DecodeRequest(payload);
    }

    public static async Task<InspectResponse> ReadResponseAsync(Stream stream, InspectRequest expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        byte[] payload = await ReadPayloadAsync(stream, ResponseLimit, cancellationToken).ConfigureAwait(false);
        return DecodeResponse(payload, expected);
    }

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > limit)
        {
            throw new InvalidDataException("Invalid frame length.");
        }
        byte[] payload = new byte[(int) length];
        await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
        byte[] tail = new byte[1];
        if (await stream.ReadAsync(tail, deadline.Token).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Trailing frame data.");
        }
        deadline.Token.ThrowIfCancellationRequested();
        return payload;
    }

    private static byte[] Encode(Action<Utf8JsonWriter> write, int limit)
    {
        using MemoryStream payload = new();
        using (Utf8JsonWriter writer = new(payload))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        if (payload.Length == 0 || payload.Length > limit)
        {
            throw new InvalidDataException("Invalid encoded length.");
        }
        byte[] frame = new byte[4 + (int) payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint) payload.Length);
        payload.GetBuffer().AsSpan(0, (int) payload.Length).CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static void WriteCorrelation(Utf8JsonWriter writer, InspectRequest request)
    {
        writer.WriteString("requestId", request.RequestId);
        writer.WriteString("instanceId", request.InstanceId);
        writer.WriteString("policyRevision", request.PolicyRevision);
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> payload, int limit)
    {
        if (payload.Length == 0 || payload.Length > limit || payload.Span.StartsWith(new byte[]
        {
            0xef, 0xbb, 0xbf
        }))
        {
            throw new InvalidDataException("Invalid payload length or BOM.");
        }
        // Validate even JSON string bytes that the parser might otherwise replace on decoding.
        StrictUtf8.GetCharCount(payload.Span);
        return JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            MaxDepth = 4,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
    }

    private static Dictionary<string, JsonElement> Fields(JsonDocument document, string[] names)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Object required.");
        }
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("Unknown or duplicate field.");
            }
        }
        if (fields.Count != names.Length)
        {
            throw new InvalidDataException("Missing field.");
        }
        return fields;
    }

    private static InspectRequest Request(Dictionary<string, JsonElement> fields)
    {
        return new InspectRequest(String(fields["requestId"]), String(fields["instanceId"]), String(fields["policyRevision"]));
    }

    private static string String(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("String required.");
        }
        string text = value.GetString()!;
        // Reject unpaired escaped UTF-16 surrogates rather than normalizing to replacement characters.
        StrictUtf8.GetByteCount(text);
        return text;
    }

    private static int Integer(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || value.GetRawText().Any(character => character is < '0' or > '9') ||
            !value.TryGetInt32(out int number) || number < minimum || number > maximum)
        {
            throw new InvalidDataException("Invalid integer literal.");
        }
        return number;
    }

    private static T EnumValue<T>(JsonElement value) where T : struct, Enum
    {
        string text = String(value);
        if (!Enum.TryParse(text, false, out T parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != text)
        {
            throw new InvalidDataException("Unknown enum.");
        }
        return parsed;
    }
}

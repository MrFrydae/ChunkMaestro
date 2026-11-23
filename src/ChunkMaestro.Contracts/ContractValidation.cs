using System.Globalization;

namespace ChunkMaestro.Contracts;

internal static class ContractValidation
{
    internal const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static void Identifier(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty || value != parsed.ToString("D"))
        {
            throw new ArgumentException("Invalid canonical identifier.");
        }
    }

    internal static void Revision(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Invalid policy revision.");
        }
    }

    internal static string Timestamp(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC required.");
        }
        return value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }
}

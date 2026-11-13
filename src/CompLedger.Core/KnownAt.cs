using System.Globalization;
using System.Text.RegularExpressions;

namespace CompLedger.Core;

/// <summary>
/// Instants of knowledge time (PRP-002 3.2 and 3.5): UTC, written with <c>Z</c>, with microsecond precision.
/// The upper bound of a <c>knownAt</c> is the request time on the database clock, so it is not checked here.
/// </summary>
public static partial class KnownAt
{
    /// <summary>The earliest accepted instant, inclusive.</summary>
    public static readonly DateTime Min = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string SecondsFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const int SecondsLength = 19;
    private const int MaxFractionDigits = 6;
    private const long TicksPerMicrosecond = 10;

    /// <summary>
    /// Parses the raw value, without trimming. True for a real UTC instant at or after <see cref="Min"/> written as
    /// <c>yyyy-MM-ddTHH:mm:ssZ</c> with up to six fractional digits; <paramref name="instant"/> then has
    /// <see cref="DateTimeKind.Utc"/>. More digits are refused, never rounded.
    /// </summary>
    public static bool TryParse(string? value, out DateTime instant)
    {
        instant = default;
        if (value is null || !Pattern().IsMatch(value))
        {
            return false;
        }

        // The pattern fixed the shape; this rejects what is not a real instant: month 13, February 30, hour 24,
        // second 60 (no leap seconds), year 0000.
        if (!DateTime.TryParseExact(
                value.AsSpan(0, SecondsLength),
                SecondsFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var seconds))
        {
            return false;
        }

        // The digits between the dot and the Z, read as microseconds: ".5" is 500000.
        long microseconds = 0;
        var fractionDigits = value.Length - SecondsLength - 2;
        for (var i = 0; i < MaxFractionDigits; i++)
        {
            microseconds = (microseconds * 10) + (i < fractionDigits ? value[SecondsLength + 1 + i] - '0' : 0);
        }

        var parsed = seconds.AddTicks(microseconds * TicksPerMicrosecond);
        if (parsed < Min)
        {
            return false;
        }

        instant = parsed;
        return true;
    }

    /// <summary>Writes a UTC instant with exactly six fractional digits, as in <c>2026-11-13T14:22:31.123456Z</c>.</summary>
    public static string Format(DateTime instant)
    {
        if (instant.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("The instant must be UTC.", nameof(instant));
        }

        return instant.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }

    // ASCII classes with \A and \z only (PRP-001 3.3 Patterns). The T and the Z are uppercase; no offsets.
    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

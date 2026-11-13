using CompLedger.Core;
using Microsoft.Net.Http.Headers;

namespace CompLedger.Api.Endpoints;

internal static class RequestChecks
{
    /// <summary>
    /// True when the request's Content-Type has exactly this media type (case-insensitive). Parameters are allowed,
    /// but a charset other than utf-8 is not. A missing or unparseable Content-Type is false.
    /// </summary>
    public static bool HasMediaType(HttpRequest request, string mediaType)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var charset = HeaderUtilities.RemoveQuotes(contentType.Charset);
        return !charset.HasValue || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the optional <c>knownAt</c> query parameter (PRP-002 3.2) from the raw query. False when it is given more
    /// than once, is empty or is not an accepted instant. The upper bound, the request time, is checked later on the
    /// database clock.
    /// </summary>
    public static bool TryReadKnownAt(HttpRequest request, out DateTime? knownAt)
    {
        knownAt = null;
        var values = request.Query["knownAt"];
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count > 1 || !KnownAt.TryParse(values[0], out var instant))
        {
            return false;
        }

        knownAt = instant;
        return true;
    }

    /// <summary>The instant in the response format, or null when there is none.</summary>
    public static string? FormatKnownAt(DateTime? knownAt) => knownAt is { } instant ? KnownAt.Format(instant) : null;

    public static IResult InvalidKnownAt() =>
        Problem(
            StatusCodes.Status400BadRequest,
            "knownAt must be given at most once, as a UTC instant such as 2026-11-13T14:22:31.123456Z with at most six fractional digits, "
            + "from 1900-01-01T00:00:00Z up to the time of the request.");

    public static IResult EmployeeNotFound() => Problem(StatusCodes.Status404NotFound, "The employee has no compensation values.");

    public static IResult Problem(int statusCode, string detail) => Results.Problem(statusCode: statusCode, detail: detail);
}

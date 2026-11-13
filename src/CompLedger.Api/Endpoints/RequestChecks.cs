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

    public static IResult Problem(int statusCode, string detail) => Results.Problem(statusCode: statusCode, detail: detail);
}

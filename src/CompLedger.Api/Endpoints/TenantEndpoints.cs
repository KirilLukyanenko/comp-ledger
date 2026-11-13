using System.Text.Json;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Db;

namespace CompLedger.Api.Endpoints;

public static class TenantEndpoints
{
    private const int MaxNameLength = 200;

    /// <summary>Mapped only when <c>TenantAdminApi:Enabled</c> is true; otherwise the path is unknown.</summary>
    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/tenants", CreateAsync);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, TenantStore tenants, CancellationToken cancellationToken)
    {
        if (!RequestChecks.HasMediaType(context.Request, "application/json"))
        {
            return RequestChecks.Problem(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json (UTF-8).");
        }

        string? name;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            name = ReadName(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string that cannot be decoded, such as an unpaired surrogate escape.
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }

        // Same trimming set as CSV values: space, tab, CR, LF.
        name = name is null ? null : RowParser.Trim(name);
        if (name is null || name.Length is < 1 or > MaxNameLength)
        {
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "name must be a string of 1 to 200 characters after trimming.");
        }

        var tenantId = await tenants.CreateAsync(name, cancellationToken);
        return Results.Json(new TenantResponse(tenantId, name), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The string value of the property named <c>name</c> (case-insensitive), or null when there is none.</summary>
    private static string? ReadName(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? name = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return name;
    }

    private sealed record TenantResponse(Guid Id, string Name);
}

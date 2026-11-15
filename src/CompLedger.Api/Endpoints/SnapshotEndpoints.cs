using System.Text.Json;
using CompLedger.Api.Operations;
using CompLedger.Api.Tenancy;

namespace CompLedger.Api.Endpoints;

/// <summary>The REST front door of <see cref="SnapshotOperations"/> (PRP-002 3.5).</summary>
public static class SnapshotEndpoints
{
    private const string SnapshotsPath = SnapshotOperations.SnapshotsPath;

    public static void MapSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(SnapshotsPath, CreateAsync).RequireTenant();
        app.MapGet(SnapshotsPath, ListAsync).RequireTenant();

        // The id is bound as a string: a malformed one is a failed lookup (404) after the tenant and query checks,
        // not an unknown route, and a wrong method on these paths is still 405.
        app.MapGet(SnapshotsPath + "/{snapshotId}", GetAsync).RequireTenant();
        app.MapGet(SnapshotsPath + "/{snapshotId}/employees/{employeeId}/compensation", GetEmployeeAsync).RequireTenant();
    }

    // Order of checks: tenant (401, middleware), content type (415), body (400), then the name conflict (409).
    private static async Task<IResult> CreateAsync(HttpContext context, SnapshotOperations operations, CancellationToken cancellationToken)
    {
        if (!RequestChecks.HasMediaType(context.Request, "application/json"))
        {
            return RequestChecks.Problem(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json (UTF-8).");
        }

        string? name;
        string? asOfText;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            name = ReadString(document.RootElement, "name");
            asOfText = ReadString(document.RootElement, "asOf");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string that cannot be decoded, such as an unpaired surrogate escape.
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }

        return (await operations.CreateAsync(context.GetTenantId(), name, asOfText, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> ListAsync(HttpContext context, SnapshotOperations operations, CancellationToken cancellationToken) =>
        (await operations.ListAsync(context.GetTenantId(), cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetAsync(string snapshotId, HttpContext context, SnapshotOperations operations, CancellationToken cancellationToken) =>
        (await operations.GetAsync(context.GetTenantId(), snapshotId, cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetEmployeeAsync(
        string snapshotId, string employeeId, HttpContext context, SnapshotOperations operations, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        return (await operations.GetEmployeeAsync(
                context.GetTenantId(), snapshotId, employeeId, query["asOf"], query["knownAt"], query["include"], cancellationToken))
            .ToHttpResult();
    }

    /// <summary>The string value of the property with this name (case-insensitive), or null when there is none.</summary>
    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? value = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return value;
    }
}

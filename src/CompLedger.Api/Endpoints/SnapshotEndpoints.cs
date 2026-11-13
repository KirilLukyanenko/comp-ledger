using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompLedger.Api.Tenancy;
using CompLedger.Core;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Queries;
using CompLedger.Infrastructure.Snapshots;

namespace CompLedger.Api.Endpoints;

/// <summary>Snapshots (PRP-002 3.5): create, list, read, and read one employee through a snapshot. No update, no delete.</summary>
public static partial class SnapshotEndpoints
{
    private const string SnapshotsPath = "/api/v1/snapshots";
    private const int MaxNameLength = 200;

    public static void MapSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(SnapshotsPath, (HttpContext context, SnapshotStore snapshots, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
                CreateAsync(context, snapshots, loggerFactory.CreateLogger("CompLedger.Snapshots"), cancellationToken))
            .RequireTenant();
        app.MapGet(SnapshotsPath, ListAsync).RequireTenant();

        // The id is bound as a string: a malformed one is a failed lookup (404) after the tenant and query checks,
        // not an unknown route, and a wrong method on these paths is still 405.
        app.MapGet(SnapshotsPath + "/{snapshotId}", GetAsync).RequireTenant();
        app.MapGet(SnapshotsPath + "/{snapshotId}/employees/{employeeId}/compensation", GetEmployeeAsync).RequireTenant();
    }

    // Order of checks: tenant (401, middleware), content type (415), body (400), then the name conflict (409).
    private static async Task<IResult> CreateAsync(HttpContext context, SnapshotStore snapshots, ILogger logger, CancellationToken cancellationToken)
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

        // Same trimming set as CSV values: space, tab, CR, LF.
        name = name is null ? null : RowParser.Trim(name);
        if (name is null || name.Length is < 1 or > MaxNameLength)
        {
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "name must be a string of 1 to 200 characters after trimming.");
        }

        if (name.Contains('\0'))
        {
            // PostgreSQL text cannot store U+0000.
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "name must not contain the character U+0000.");
        }

        // The PRP-001 date rules, on the value as written: not trimmed.
        if (asOfText is null || RowParser.CheckDate(asOfText, out var asOf) != DateCheck.Valid)
        {
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "asOf must be a string holding a real yyyy-MM-dd date from 1900-01-01 to 2999-12-31.");
        }

        var tenantId = context.GetTenantId();
        var stopwatch = Stopwatch.StartNew();
        var snapshot = await snapshots.CreateAsync(tenantId, name, asOf, cancellationToken);
        if (snapshot is null)
        {
            return RequestChecks.Problem(StatusCodes.Status409Conflict, "A snapshot with this name already exists.");
        }

        // The one log entry of the snapshot. Ids and the two coordinates only: never the name.
        var response = ToResponse(snapshot);
        LogSnapshot(
            logger,
            tenantId.ToString("D"),
            snapshot.SnapshotId.ToString("D"),
            snapshot.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            response.KnownAt,
            stopwatch.ElapsedMilliseconds);

        return Results.Created($"{SnapshotsPath}/{snapshot.SnapshotId:D}", response);
    }

    // The list and the resource take no parameters and ignore all of them.

    private static async Task<IResult> ListAsync(HttpContext context, SnapshotStore snapshots, CancellationToken cancellationToken)
    {
        var list = await snapshots.ListAsync(context.GetTenantId(), cancellationToken);
        return Results.Json(new SnapshotListResponse([.. list.Select(ToResponse)]));
    }

    private static async Task<IResult> GetAsync(string snapshotId, HttpContext context, SnapshotStore snapshots, CancellationToken cancellationToken)
    {
        var snapshot = await FindAsync(snapshotId, context, snapshots, cancellationToken);
        return snapshot is null ? SnapshotNotFound() : Results.Json(ToResponse(snapshot));
    }

    // Order of checks: tenant (401, middleware), query parameters (400), the snapshot (404), then the employee (404).
    private static async Task<IResult> GetEmployeeAsync(
        string snapshotId, string employeeId, HttpContext context, SnapshotStore snapshots, CompensationQueries queries, CancellationToken cancellationToken)
    {
        // The snapshot fixes both coordinates, so the read names these two parameters only to refuse them.
        var query = context.Request.Query;
        if (query["asOf"].Count > 0 || query["knownAt"].Count > 0)
        {
            return RequestChecks.Problem(StatusCodes.Status400BadRequest, "A snapshot read takes no asOf and no knownAt: the snapshot fixes both.");
        }

        var snapshot = await FindAsync(snapshotId, context, snapshots, cancellationToken);
        if (snapshot is null)
        {
            return SnapshotNotFound();
        }

        // The as-known-at as-of query for the snapshot's pair; like any read with an instant, it waits for a batch
        // of the tenant that is being applied.
        var result = await queries.GetAsOfAsync(context.GetTenantId(), employeeId, snapshot.AsOf, snapshot.KnownAt, cancellationToken);
        return result.Outcome switch
        {
            CompensationOutcome.Found => Results.Json(
                new SnapshotEmployeeResponse(snapshot.SnapshotId, employeeId, snapshot.AsOf, KnownAt.Format(snapshot.KnownAt), result.Periods)),
            CompensationOutcome.EmployeeNotFound => RequestChecks.EmployeeNotFound(),

            // The instant came from this database's clock when the snapshot was created.
            _ => throw new InvalidOperationException("A snapshot's instant is later than the database clock."),
        };
    }

    /// <summary>
    /// The snapshot of this tenant, or null. Only the hyphenated 8-4-4-4-12 form is an id, in either case; an id of
    /// another tenant must look the same as a missing one.
    /// </summary>
    private static async Task<Snapshot?> FindAsync(string snapshotId, HttpContext context, SnapshotStore snapshots, CancellationToken cancellationToken) =>
        HyphenatedUuid().IsMatch(snapshotId) && Guid.TryParseExact(snapshotId, "D", out var id)
            ? await snapshots.FindAsync(context.GetTenantId(), id, cancellationToken)
            : null;

    // Guid.TryParseExact alone would accept surrounding whitespace; any spelling but this one is a failed lookup.
    [GeneratedRegex(@"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedUuid();

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

    private static SnapshotResponse ToResponse(Snapshot snapshot) =>
        new(snapshot.SnapshotId, snapshot.Name, snapshot.AsOf, KnownAt.Format(snapshot.KnownAt));

    private static IResult SnapshotNotFound() => RequestChecks.Problem(StatusCodes.Status404NotFound, "The snapshot does not exist.");

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Snapshot {SnapshotId} of tenant {TenantId} created: as of {AsOf}, known at {KnownAt}, {DurationMs} ms")]
    private static partial void LogSnapshot(ILogger logger, string tenantId, string snapshotId, string asOf, string knownAt, long durationMs);

    private sealed record SnapshotResponse(Guid SnapshotId, string Name, DateOnly AsOf, string KnownAt);

    private sealed record SnapshotListResponse(IReadOnlyList<SnapshotResponse> Snapshots);

    private sealed record SnapshotEmployeeResponse(
        Guid SnapshotId, string EmployeeId, DateOnly AsOf, string KnownAt, IReadOnlyList<CompensationPeriod> Components);
}

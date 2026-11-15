using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CompLedger.Core;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Queries;
using CompLedger.Infrastructure.Snapshots;
using Microsoft.Extensions.Primitives;

namespace CompLedger.Api.Operations;

/// <summary>Snapshots (PRP-002 3.5): create, list, read, and read one employee through a snapshot. No update, no delete.</summary>
public sealed partial class SnapshotOperations(SnapshotStore snapshots, CompensationQueries queries, ILoggerFactory loggerFactory)
{
    public const string SnapshotsPath = "/api/v1/snapshots";

    private const int MaxNameLength = 200;

    private readonly ILogger _logger = loggerFactory.CreateLogger("CompLedger.Snapshots");

    /// <summary>
    /// Creates a snapshot from the two body values as written; null when the body has no string of that name.
    /// Order of checks: the values (400), then the name conflict (409).
    /// </summary>
    public async Task<OperationResult> CreateAsync(Guid tenantId, string? name, string? asOfText, CancellationToken cancellationToken)
    {
        // Same trimming set as CSV values: space, tab, CR, LF.
        name = name is null ? null : RowParser.Trim(name);
        if (name is null || name.Length is < 1 or > MaxNameLength)
        {
            return OperationResult.Problem(StatusCodes.Status400BadRequest, "name must be a string of 1 to 200 characters after trimming.");
        }

        if (name.Contains('\0'))
        {
            // PostgreSQL text cannot store U+0000.
            return OperationResult.Problem(StatusCodes.Status400BadRequest, "name must not contain the character U+0000.");
        }

        // The PRP-001 date rules, on the value as written: not trimmed.
        if (asOfText is null || RowParser.CheckDate(asOfText, out var asOf) != DateCheck.Valid)
        {
            return OperationResult.Problem(
                StatusCodes.Status400BadRequest, "asOf must be a string holding a real yyyy-MM-dd date from 1900-01-01 to 2999-12-31.");
        }

        var stopwatch = Stopwatch.StartNew();
        var snapshot = await snapshots.CreateAsync(tenantId, name, asOf, cancellationToken);
        if (snapshot is null)
        {
            return OperationResult.Problem(StatusCodes.Status409Conflict, "A snapshot with this name already exists.");
        }

        // The one log entry of the snapshot. Ids and the two coordinates only: never the name.
        var response = ToResponse(snapshot);
        LogSnapshot(
            _logger,
            tenantId.ToString("D"),
            snapshot.SnapshotId.ToString("D"),
            snapshot.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            response.KnownAt,
            stopwatch.ElapsedMilliseconds);

        return OperationResult.Created(response, $"{SnapshotsPath}/{snapshot.SnapshotId:D}");
    }

    // The list and the resource take no parameters and ignore all of them.

    public async Task<OperationResult> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var list = await snapshots.ListAsync(tenantId, cancellationToken);
        return OperationResult.Ok(new SnapshotListResponse([.. list.Select(ToResponse)]));
    }

    public async Task<OperationResult> GetAsync(Guid tenantId, string snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await FindAsync(tenantId, snapshotId, cancellationToken);
        return snapshot is null ? SnapshotNotFound() : OperationResult.Ok(ToResponse(snapshot));
    }

    /// <summary>
    /// Order of checks: parameters (400), the snapshot (404), then the employee (404). <paramref name="includeValues"/>
    /// is the PRP-003 <c>include</c> parameter; the MCP tool always passes it as not given.
    /// </summary>
    public async Task<OperationResult> GetEmployeeAsync(
        Guid tenantId,
        string snapshotId,
        string employeeId,
        StringValues asOfValues,
        StringValues knownAtValues,
        StringValues includeValues,
        CancellationToken cancellationToken)
    {
        // The snapshot fixes both coordinates, so the read names these two parameters only to refuse them.
        if (asOfValues.Count > 0 || knownAtValues.Count > 0)
        {
            return OperationResult.Problem(StatusCodes.Status400BadRequest, "A snapshot read takes no asOf and no knownAt: the snapshot fixes both.");
        }

        if (!CompensationOperations.TryReadInclude(includeValues, out var withComputed))
        {
            return CompensationOperations.InvalidInclude();
        }

        var snapshot = await FindAsync(tenantId, snapshotId, cancellationToken);
        if (snapshot is null)
        {
            return SnapshotNotFound();
        }

        // The as-known-at as-of query for the snapshot's pair; like any read with an instant, it waits for a batch
        // of the tenant that is being applied.
        var result = await queries.GetAsOfAsync(tenantId, employeeId, snapshot.AsOf, snapshot.KnownAt, withComputed, cancellationToken);
        return result.Outcome switch
        {
            CompensationOutcome.Found => OperationResult.Ok(new SnapshotEmployeeResponse(
                snapshot.SnapshotId,
                employeeId,
                snapshot.AsOf,
                KnownAt.Format(snapshot.KnownAt),
                result.Periods,
                withComputed ? CompensationOperations.Computed(snapshot.AsOf, result) : null)),
            CompensationOutcome.EmployeeNotFound => CompensationOperations.EmployeeNotFound(),

            // The instant came from this database's clock when the snapshot was created.
            _ => throw new InvalidOperationException("A snapshot's instant is later than the database clock."),
        };
    }

    /// <summary>
    /// The snapshot of this tenant, or null. Only the hyphenated 8-4-4-4-12 form is an id, in either case; an id of
    /// another tenant must look the same as a missing one.
    /// </summary>
    private async Task<Snapshot?> FindAsync(Guid tenantId, string snapshotId, CancellationToken cancellationToken) =>
        HyphenatedUuid().IsMatch(snapshotId) && Guid.TryParseExact(snapshotId, "D", out var id)
            ? await snapshots.FindAsync(tenantId, id, cancellationToken)
            : null;

    // Guid.TryParseExact alone would accept surrounding whitespace; any spelling but this one is a failed lookup.
    [GeneratedRegex(@"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedUuid();

    private static SnapshotResponse ToResponse(Snapshot snapshot) =>
        new(snapshot.SnapshotId, snapshot.Name, snapshot.AsOf, KnownAt.Format(snapshot.KnownAt));

    private static OperationResult SnapshotNotFound() => OperationResult.Problem(StatusCodes.Status404NotFound, "The snapshot does not exist.");

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Snapshot {SnapshotId} of tenant {TenantId} created: as of {AsOf}, known at {KnownAt}, {DurationMs} ms")]
    private static partial void LogSnapshot(ILogger logger, string tenantId, string snapshotId, string asOf, string knownAt, long durationMs);

    private sealed record SnapshotResponse(Guid SnapshotId, string Name, DateOnly AsOf, string KnownAt);

    private sealed record SnapshotListResponse(IReadOnlyList<SnapshotResponse> Snapshots);

    /// <summary><see cref="Computed"/> is left out entirely, not written as null, without <c>include=computed</c>.</summary>
    private sealed record SnapshotEmployeeResponse(
        Guid SnapshotId,
        string EmployeeId,
        DateOnly AsOf,
        string KnownAt,
        IReadOnlyList<CompensationPeriod> Components,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ComputedField>? Computed);
}

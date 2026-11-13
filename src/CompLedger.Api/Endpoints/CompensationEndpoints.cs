using CompLedger.Api.Tenancy;
using CompLedger.Core;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Queries;

namespace CompLedger.Api.Endpoints;

public static class CompensationEndpoints
{
    public static void MapCompensationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/employees/{employeeId}/compensation", GetAsOfAsync).RequireTenant();
        app.MapGet("/api/v1/employees/{employeeId}/compensation/history", GetHistoryAsync).RequireTenant();
    }

    // Order of checks on both endpoints: tenant (401, middleware), query parameters (400), then the lookup (404).
    // Whether knownAt is later than the request time is known only on the database clock, after the wait for a batch
    // being applied; it is still decided before the lookup.

    private static async Task<IResult> GetAsOfAsync(string employeeId, HttpContext context, CompensationQueries queries, CancellationToken cancellationToken)
    {
        // Read from the raw query: parameter binding would accept a repeated parameter.
        var values = context.Request.Query["asOf"];
        DateOnly asOf;
        if (values.Count == 0)
        {
            asOf = DateOnly.FromDateTime(DateTime.UtcNow);
        }
        else if (values.Count > 1 || RowParser.CheckDate(values[0] ?? "", out asOf) != DateCheck.Valid)
        {
            return RequestChecks.Problem(
                StatusCodes.Status400BadRequest, "asOf must be given at most once, as a real yyyy-MM-dd date from 1900-01-01 to 2999-12-31.");
        }

        if (!RequestChecks.TryReadKnownAt(context.Request, out var knownAt))
        {
            return RequestChecks.InvalidKnownAt();
        }

        var result = await queries.GetAsOfAsync(context.GetTenantId(), employeeId, asOf, knownAt, cancellationToken);
        return result.Outcome switch
        {
            CompensationOutcome.KnownAtAfterRequestTime => RequestChecks.InvalidKnownAt(),
            CompensationOutcome.EmployeeNotFound => RequestChecks.EmployeeNotFound(),
            _ => Results.Json(new AsOfResponse(employeeId, asOf, RequestChecks.FormatKnownAt(knownAt), result.Periods)),
        };
    }

    private static async Task<IResult> GetHistoryAsync(string employeeId, HttpContext context, CompensationQueries queries, CancellationToken cancellationToken)
    {
        var values = context.Request.Query["component"];
        string? component = null;
        if (values.Count > 0)
        {
            component = values.Count == 1 ? Components.Find(values[0] ?? "")?.Code : null;
            if (component is null)
            {
                return RequestChecks.Problem(
                    StatusCodes.Status400BadRequest, "component must be given at most once, as an exact code from the component catalogue.");
            }
        }

        if (!RequestChecks.TryReadKnownAt(context.Request, out var knownAt))
        {
            return RequestChecks.InvalidKnownAt();
        }

        var result = await queries.GetHistoryAsync(context.GetTenantId(), employeeId, component, knownAt, cancellationToken);
        return result.Outcome switch
        {
            CompensationOutcome.KnownAtAfterRequestTime => RequestChecks.InvalidKnownAt(),
            CompensationOutcome.EmployeeNotFound => RequestChecks.EmployeeNotFound(),
            _ => Results.Json(new HistoryResponse(employeeId, RequestChecks.FormatKnownAt(knownAt), result.Periods)),
        };
    }

    /// <summary><see cref="KnownAt"/> is null when the parameter was not given, otherwise the requested instant.</summary>
    private sealed record AsOfResponse(string EmployeeId, DateOnly AsOf, string? KnownAt, IReadOnlyList<CompensationPeriod> Components);

    private sealed record HistoryResponse(string EmployeeId, string? KnownAt, IReadOnlyList<CompensationPeriod> Periods);
}

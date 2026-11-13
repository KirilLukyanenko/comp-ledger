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

        var components = await queries.GetAsOfAsync(context.GetTenantId(), employeeId, asOf, cancellationToken);
        return components is null
            ? EmployeeNotFound()
            : Results.Json(new AsOfResponse(employeeId, asOf, components));
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

        var periods = await queries.GetHistoryAsync(context.GetTenantId(), employeeId, component, cancellationToken);
        return periods is null
            ? EmployeeNotFound()
            : Results.Json(new HistoryResponse(employeeId, periods));
    }

    private static IResult EmployeeNotFound() =>
        RequestChecks.Problem(StatusCodes.Status404NotFound, "The employee has no compensation values.");

    private sealed record AsOfResponse(string EmployeeId, DateOnly AsOf, IReadOnlyList<CompensationPeriod> Components);

    private sealed record HistoryResponse(string EmployeeId, IReadOnlyList<CompensationPeriod> Periods);
}

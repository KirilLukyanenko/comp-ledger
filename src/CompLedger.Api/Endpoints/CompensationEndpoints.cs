using CompLedger.Api.Operations;
using CompLedger.Api.Tenancy;

namespace CompLedger.Api.Endpoints;

public static class CompensationEndpoints
{
    public static void MapCompensationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/employees/{employeeId}/compensation", GetAsOfAsync).RequireTenant();
        app.MapGet("/api/v1/employees/{employeeId}/compensation/history", GetHistoryAsync).RequireTenant();
    }

    // The tenant was checked by the middleware (401). Parameters come from the raw query: parameter binding would
    // accept a repeated one.

    private static async Task<IResult> GetAsOfAsync(string employeeId, HttpContext context, CompensationOperations operations, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        return (await operations.GetAsOfAsync(context.GetTenantId(), employeeId, query["asOf"], query["knownAt"], query["include"], cancellationToken))
            .ToHttpResult();
    }

    private static async Task<IResult> GetHistoryAsync(string employeeId, HttpContext context, CompensationOperations operations, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        return (await operations.GetHistoryAsync(context.GetTenantId(), employeeId, query["component"], query["knownAt"], cancellationToken)).ToHttpResult();
    }
}

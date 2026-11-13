namespace CompLedger.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness only: it must not touch the database.
        app.MapGet("/health", () => Results.Json(new HealthResponse("ok")));
    }

    private sealed record HealthResponse(string Status);
}

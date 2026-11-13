using System.Text.RegularExpressions;
using CompLedger.Infrastructure.Db;

namespace CompLedger.Api.Tenancy;

/// <summary>The tenant of the current request, set by <see cref="TenantResolutionMiddleware"/>.</summary>
public sealed record TenantContext(Guid TenantId);

/// <summary>Marks an endpoint that works on tenant-owned data.</summary>
public sealed class RequiresTenantMetadata;

public static class TenantEndpointExtensions
{
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresTenantMetadata());

    public static Guid GetTenantId(this HttpContext context) =>
        context.Features.Get<TenantContext>()?.TenantId
        ?? throw new InvalidOperationException("The endpoint was not mapped with RequireTenant().");
}

/// <summary>
/// Resolves the tenant from <c>X-Tenant-Id</c> (a stand-in for real authentication) for the endpoints marked with
/// <see cref="RequiresTenantMetadata"/>, and answers <c>401</c> when there is no known tenant. Other endpoints,
/// unknown routes and wrong-method requests pass through untouched, whatever the header holds.
/// </summary>
public sealed partial class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresTenantMetadata>() is null)
        {
            await next(context);
            return;
        }

        // Resolved only here, so requests that need no tenant (such as /health) never touch the database.
        var tenants = context.RequestServices.GetRequiredService<TenantStore>();
        var header = context.Request.Headers[HeaderName];
        if (header.Count == 1
            && header[0] is { } value
            && HyphenatedUuid().IsMatch(value)
            && Guid.TryParseExact(value, "D", out var tenantId)
            && tenantId != Guid.Empty
            && await tenants.ExistsAsync(tenantId, context.RequestAborted))
        {
            context.Features.Set(new TenantContext(tenantId));
            await next(context);
            return;
        }

        await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: $"{HeaderName} must hold the id of a known tenant.")
            .ExecuteAsync(context);
    }

    [GeneratedRegex(@"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedUuid();
}

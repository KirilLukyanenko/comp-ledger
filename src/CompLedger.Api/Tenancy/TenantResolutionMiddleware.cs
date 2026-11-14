using System.Text.RegularExpressions;
using CompLedger.Infrastructure.Db;
using Microsoft.AspNetCore.Diagnostics;

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
/// <see cref="RequiresTenantMetadata"/> and for every request to <c>/mcp</c>, and answers <c>401</c> when there is no
/// known tenant. Other endpoints, unknown routes and wrong-method requests pass through untouched, whatever the
/// header holds.
/// </summary>
public sealed partial class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant-Id";

    /// <summary>Where the MCP server is mapped (PRP-005 3.1).</summary>
    public const string McpPath = "/mcp";

    public async Task InvokeAsync(HttpContext context)
    {
        // /mcp needs the tenant for every method, mapped or not (PRP-005 3.2), so it is recognised by its path too.
        var endpoint = context.GetEndpoint();
        var isMcp = IsMcpPath(context.Request.Path);
        if (endpoint?.Metadata.GetMetadata<RequiresTenantMetadata>() is null && !isMcp)
        {
            await next(context);
            return;
        }

        if (isMcp && endpoint is not null && context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
        {
            // What the MCP SDK answers stays as the SDK wrote it: no ProblemDetails page over a bodyless status.
            statusCodePages.Enabled = false;
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

    /// <summary>True for the MCP endpoint's path as routing matches it: any case, with or without a trailing slash.</summary>
    private static bool IsMcpPath(PathString path) =>
        path.StartsWithSegments(McpPath, StringComparison.OrdinalIgnoreCase, out var rest) && (!rest.HasValue || rest.Value == "/");

    [GeneratedRegex(@"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedUuid();
}

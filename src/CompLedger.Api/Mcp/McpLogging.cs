namespace CompLedger.Api.Mcp;

/// <summary>
/// The one log entry of a <c>tools/call</c> (PRP-005 3.6): the tool, the tenant, the status and the duration. Never
/// an argument value: arguments hold employee ids and snapshot names.
/// </summary>
internal static partial class McpLogging
{
    public const string Category = "CompLedger.Mcp";

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool {Tool} of tenant {TenantId} answered {Status} in {DurationMs} ms")]
    public static partial void LogToolCall(ILogger logger, string tool, string tenantId, int status, long durationMs);
}

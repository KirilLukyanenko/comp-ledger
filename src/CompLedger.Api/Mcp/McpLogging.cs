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

    /// <summary>
    /// The entry of a call that ended in an exception caught inside the tool (the <c>500</c>): the exception's type
    /// name and nothing else of it. Its message, stack trace and inner exceptions can quote argument values, so the
    /// exception itself is not passed to the logger.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "MCP tool {Tool} of tenant {TenantId} answered {Status} in {DurationMs} ms after {ExceptionType}")]
    public static partial void LogToolCallFailed(ILogger logger, string tool, string tenantId, int status, long durationMs, string exceptionType);
}

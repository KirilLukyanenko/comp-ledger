using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompLedger.Api.Operations;
using CompLedger.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CompLedger.Api.Mcp;

/// <summary>A string argument of a tool. Every tool argument is a JSON string (PRP-005 3.2).</summary>
internal sealed record ToolArgument(string Name, bool Required, string Description);

/// <summary>Calls the operation's handler: the same call the REST endpoint makes.</summary>
internal delegate Task<OperationResult> ToolHandler(IServiceProvider services, Guid tenantId, ToolArguments arguments, CancellationToken cancellationToken);

/// <summary>
/// The named arguments of one call, bound from the raw JSON by the tool itself: the SDK would convert or reject
/// values before the rules of PRP-005 3.2 run. Values are used exactly as given: not trimmed, decoded or normalised.
/// </summary>
internal sealed class ToolArguments
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    /// <summary>Why the arguments are refused with <c>400</c>, or null. Names the argument, never its value.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Reads the arguments the tool names; all others are ignored, whatever their kind. Absent and JSON null are the
    /// same: not given. An empty string is given.
    /// </summary>
    public static ToolArguments Bind(IReadOnlyList<ToolArgument> definitions, IDictionary<string, JsonElement>? raw)
    {
        var arguments = new ToolArguments();
        foreach (var definition in definitions)
        {
            string? value = null;
            if (raw is not null && raw.TryGetValue(definition.Name, out var element) && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    arguments.Error ??= $"The argument {definition.Name} must be a JSON string.";
                    continue;
                }

                try
                {
                    value = element.GetString();
                }
                catch (InvalidOperationException)
                {
                    // A string that cannot be decoded, such as an unpaired surrogate escape.
                    arguments.Error ??= $"The argument {definition.Name} must be a JSON string.";
                    continue;
                }
            }

            if (value is null && definition.Required)
            {
                arguments.Error ??= $"The argument {definition.Name} is required and must be a JSON string.";
            }

            arguments._values[definition.Name] = value;
        }

        return arguments;
    }

    /// <summary>The value of a required argument. Only valid when <see cref="Error"/> is null.</summary>
    public string Required(string name) => _values[name] ?? throw new InvalidOperationException("A required argument was not bound.");

    /// <summary>An optional argument as a query parameter: no values when it was not given, one otherwise.</summary>
    public StringValues Optional(string name) => _values[name] is { } value ? new StringValues(value) : StringValues.Empty;
}

/// <summary>
/// One MCP tool over one operation (PRP-005 3.3). Thin: it checks the argument kinds, calls the handler the REST
/// endpoint calls, and turns the status and body into a tool result (3.4). The tenant is the one the middleware
/// resolved for the HTTP request that carries the call; nothing is kept between calls.
/// </summary>
internal sealed class CompLedgerTool : McpServerTool
{
    private readonly IReadOnlyList<ToolArgument> _arguments;
    private readonly ToolHandler _handler;

    public CompLedgerTool(string name, string title, string description, bool readOnly, IReadOnlyList<ToolArgument> arguments, ToolHandler handler)
    {
        _arguments = arguments;
        _handler = handler;

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var argument in arguments)
        {
            properties[argument.Name] = new JsonObject { ["type"] = "string", ["description"] = argument.Description };
            if (argument.Required)
            {
                required.Add(argument.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        ProtocolTool = new Tool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = JsonSerializer.SerializeToElement(schema),
            Annotations = readOnly
                ? new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false }
                : new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false },
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services ?? throw new InvalidOperationException("The tool call has no service provider.");
        var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("The tool call is not inside an HTTP request.");
        var tenantId = httpContext.GetTenantId();
        var stopwatch = Stopwatch.StartNew();

        OperationResult result;
        try
        {
            var arguments = ToolArguments.Bind(_arguments, request.Params?.Arguments);
            result = arguments.Error is { } detail
                ? OperationResult.Problem(StatusCodes.Status400BadRequest, detail)
                : await _handler(services, tenantId, arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The endpoint's 500: nothing of the exception reaches the client (the SDK would send its message).
            result = OperationResult.Problem(StatusCodes.Status500InternalServerError, null);
        }

        var toolResult = ToToolResult(result, httpContext, services);
        McpLogging.LogToolCall(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(McpLogging.Category),
            ProtocolTool.Name,
            tenantId.ToString("D"),
            result.Status,
            stopwatch.ElapsedMilliseconds);
        return toolResult;
    }

    /// <summary>
    /// The body, or the ProblemDetails, serialised with the API's JSON options, as structured content and as text.
    /// </summary>
    private static CallToolResult ToToolResult(OperationResult result, HttpContext httpContext, IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        JsonElement json;
        if (result.IsError)
        {
            // Results.Problem is what the endpoint returns: the same defaults for type and title, and the trace id
            // the problem details writer adds.
            var problem = TypedResults.Problem(statusCode: result.Status, detail: result.Detail).ProblemDetails;
            problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            json = JsonSerializer.SerializeToElement(problem, typeof(ProblemDetails), options);
        }
        else
        {
            var body = result.Body ?? throw new InvalidOperationException("A successful operation has no body.");
            json = JsonSerializer.SerializeToElement(body, body.GetType(), options);
        }

        return new CallToolResult
        {
            IsError = result.IsError,
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
        };
    }
}

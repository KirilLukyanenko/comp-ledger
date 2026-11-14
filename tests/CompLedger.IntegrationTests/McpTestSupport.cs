using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CompLedger.IntegrationTests;

/// <summary>An MCP client over the in-process API (PRP-005 6.5 #10), and helpers to read tool results.</summary>
public static class McpTestSupport
{
    /// <summary>The revision the SDK client speaks by default: <c>server/discover</c>, no <c>initialize</c>.</summary>
    public const string NewRevision = "2026-07-28";

    /// <summary>The revision with the <c>initialize</c> handshake.</summary>
    public const string OldRevision = "2025-06-18";

    public const string McpPath = "/mcp";

    public static readonly string[] ToolNames =
    [
        "get_compensation", "get_compensation_history", "get_ingestion", "list_snapshots", "get_snapshot", "get_snapshot_compensation", "create_snapshot",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Connects as a tenant. Without a revision the client uses its default; with one it is pinned to it.</summary>
    public static async Task<McpClient> ConnectMcpAsync(this ApiFactory api, Guid tenant, string? revision = null)
    {
        var http = api.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, McpPath),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Tenant-Id"] = tenant.ToString() },
            },
            http,
            loggerFactory: null,
            ownsHttpClient: true);

        var options = new McpClientOptions();
        if (revision is not null)
        {
            options.ProtocolVersion = revision;
        }

        var client = await McpClient.CreateAsync(transport, options, loggerFactory: null, Ct);
        Assert.Equal(revision ?? NewRevision, client.NegotiatedProtocolVersion);
        return client;
    }

    /// <summary>Calls a tool with the arguments written as a JSON object, so every JSON kind arrives as written.</summary>
    public static async Task<CallToolResult> CallAsync(this McpClient client, string tool, string argumentsJson = "{}")
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
    }

    public static string Arguments(params (string Name, string? Value)[] arguments) =>
        JsonSerializer.Serialize(arguments.ToDictionary(a => a.Name, a => a.Value));

    /// <summary>The structured content, after checking the shape of PRP-005 3.4: one text item holding the same JSON.</summary>
    public static JsonElement Structured(this CallToolResult result)
    {
        var structured = Assert.NotNull(result.StructuredContent);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("text", text.Type);
        using var parsed = JsonDocument.Parse(text.Text);
        Assert.True(JsonElement.DeepEquals(structured, parsed.RootElement), "The text item must hold the same JSON as structuredContent.");
        return structured;
    }

    public static JsonElement AssertSuccess(this CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return result.Structured();
    }

    /// <summary>A tool error holding ProblemDetails with this status, a title and a type.</summary>
    public static JsonElement AssertToolError(this CallToolResult result, HttpStatusCode expected)
    {
        Assert.True(result.IsError, "The call must be a tool error.");
        var problem = result.Structured();
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("type").GetString()));
        return problem;
    }

    /// <summary>The status of a result as PRP-005 3.4 compares it; a success is given as the caller expects it.</summary>
    public static int Status(this CallToolResult result, int successStatus = 200) =>
        result.IsError == true ? result.Structured().GetProperty("status").GetInt32() : successStatus;

    /// <summary>Sends one hand-built JSON-RPC message to <c>/mcp</c>, for the cases the client cannot produce.</summary>
    public static async Task<HttpResponseMessage> PostMcpAsync(
        this HttpClient client, string body, string? tenantHeader, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (tenantHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantHeader);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The JSON-RPC message of a response, whichever media type the SDK chose; null when there is none.</summary>
    public static async Task<JsonElement?> JsonRpcAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            text = string.Concat(text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim()).TakeLast(1));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

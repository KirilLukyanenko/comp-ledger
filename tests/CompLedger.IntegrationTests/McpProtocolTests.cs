using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CompLedger.IntegrationTests;

/// <summary>
/// PRP-005 T2: the MCP endpoint, the tenant check, server info and capabilities in both revisions, the tool list,
/// argument kinds (3.2, 3.3, 3.5; M1, M4, M7, M8).
/// </summary>
public sealed class McpProtocolTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private const string Discover =
        """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"test","version":"1"}}}}""";

    private static readonly (string Name, string Value)[] DiscoverHeaders = [("MCP-Protocol-Version", McpTestSupport.NewRevision), ("Mcp-Method", "server/discover")];

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The arguments of each tool (PRP-005 3.3) and whether they are required.</summary>
    public static readonly Dictionary<string, (string Name, bool Required)[]> ToolArguments = new()
    {
        ["get_compensation"] = [("employeeId", true), ("asOf", false), ("knownAt", false)],
        ["get_compensation_history"] = [("employeeId", true), ("component", false), ("knownAt", false)],
        ["get_ingestion"] = [("batchId", true)],
        ["list_snapshots"] = [],
        ["get_snapshot"] = [("snapshotId", true)],
        ["get_snapshot_compensation"] = [("snapshotId", true), ("employeeId", true)],
        ["create_snapshot"] = [("name", true), ("asOf", true)],
    };

    /// <summary>Arguments that pass the kind check for every tool.</summary>
    private static Dictionary<string, string> ValidArguments(string tool) =>
        ToolArguments[tool].ToDictionary(a => a.Name, a => a.Name == "asOf" ? "2026-03-31" : "x");

    // ---------- M1: the tool list ----------

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task M1_tools_list_returns_exactly_the_seven_tools_with_string_arguments_and_annotations(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        var page = await mcp.ListToolsAsync(new ListToolsRequestParams(), Ct);

        Assert.Null(page.NextCursor);
        Assert.Equal(McpTestSupport.ToolNames.Order(), page.Tools.Select(t => t.Name).Order());
        foreach (var tool in page.Tools)
        {
            var expected = ToolArguments[tool.Name];
            var schema = tool.InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());

            var properties = schema.GetProperty("properties").EnumerateObject().ToList();
            Assert.Equal(expected.Select(a => a.Name).Order(), properties.Select(p => p.Name).Order());
            foreach (var property in properties)
            {
                Assert.Equal(JsonValueKind.String, property.Value.GetProperty("type").ValueKind);
                Assert.Equal("string", property.Value.GetProperty("type").GetString());
                Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()));
            }

            var required = schema.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
            Assert.Equal(expected.Where(a => a.Required).Select(a => a.Name).Order(), required.Order());

            Assert.False(string.IsNullOrWhiteSpace(tool.Title ?? tool.Annotations?.Title));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Null(tool.OutputSchema);

            var annotations = tool.Annotations;
            Assert.NotNull(annotations);
            Assert.False(annotations.OpenWorldHint);
            if (tool.Name == "create_snapshot")
            {
                Assert.False(annotations.ReadOnlyHint);
                Assert.False(annotations.DestructiveHint);
                Assert.False(annotations.IdempotentHint);
            }
            else
            {
                Assert.True(annotations.ReadOnlyHint);
            }
        }
    }

    // ---------- Server info and capabilities (3.5) ----------

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task Server_info_and_capabilities_are_the_same_in_both_revisions(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        var serverInfo = mcp.ServerInfo;
        Assert.NotNull(serverInfo);
        Assert.Equal("comp-ledger", serverInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.Version));

        var capabilities = mcp.ServerCapabilities;
        Assert.NotNull(capabilities.Tools);
        Assert.Null(capabilities.Resources);
        Assert.Null(capabilities.Prompts);
        Assert.Null(capabilities.Completions);
        if (revision is null)
        {
            // Under 2025-06-18 the SDK always adds logging; under 2026-07-28 the capabilities are tools only.
#pragma warning disable MCP9005 // Logging is deprecated in the SDK; the test is that it is absent.
            Assert.Null(capabilities.Logging);
#pragma warning restore MCP9005
        }

        // No MCP session: stateless.
        Assert.Null(mcp.SessionId);
    }

    [Fact]
    public async Task Initialize_puts_the_server_info_in_serverInfo()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.PostMcpAsync(Initialize, tenant.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        var result = Assert.NotNull(await response.JsonRpcAsync()).GetProperty("result");
        Assert.Equal(McpTestSupport.OldRevision, result.GetProperty("protocolVersion").GetString());
        Assert.Equal("comp-ledger", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("serverInfo").GetProperty("version").GetString()));

        var capabilities = result.GetProperty("capabilities").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("tools", capabilities);
        Assert.Empty(capabilities.Except(["tools", "logging"]));
    }

    [Fact]
    public async Task Server_discover_puts_the_server_info_in_meta_and_has_the_tools_capability_only()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.PostMcpAsync(Discover, tenant.ToString(), DiscoverHeaders);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        var result = Assert.NotNull(await response.JsonRpcAsync()).GetProperty("result");
        var serverInfo = result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo");
        Assert.Equal("comp-ledger", serverInfo.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()));
        Assert.Equal(["tools"], result.GetProperty("capabilities").EnumerateObject().Select(p => p.Name));
        Assert.Contains(McpTestSupport.NewRevision, result.GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()));
    }

    // ---------- M7: the tenant check comes before any MCP processing ----------

    public static TheoryData<string> Messages =>
    [
        Initialize,
        Discover,
        """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_snapshots","arguments":{}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
        """{"jsonrpc":"2.0","id":4,"method":"no/such/method"}""",
        "{ not json",
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public async Task M7_any_message_without_a_known_tenant_is_401_with_ProblemDetails(string message)
    {
        using var missing = await _client.PostMcpAsync(message, tenantHeader: null);
        using var unknown = await _client.PostMcpAsync(message, Guid.NewGuid().ToString());
        using var malformed = await _client.PostMcpAsync(message, "not-a-uuid");
        using var newRevision = await _client.PostMcpAsync(message, tenantHeader: null, DiscoverHeaders);

        foreach (var response in new[] { missing, unknown, malformed, newRevision })
        {
            await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
            Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Other_methods_check_the_tenant_first_and_return_no_session_and_no_tenant_data(string method)
    {
        var tenant = await api.NewTenantAsync(_client, "Hidden-Tenant-Name");
        foreach (var path in new[] { "/mcp", "/mcp/", "/MCP" })
        {
            using var withoutTenant = await _client.SendAsync(new HttpMethod(method), path, tenant: null);
            await withoutTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);

            using var withTenant = await _client.SendAsync(new HttpMethod(method), path, tenant);
            Assert.NotEqual(HttpStatusCode.Unauthorized, withTenant.StatusCode);
            Assert.False(withTenant.Headers.Contains("Mcp-Session-Id"));
            Assert.DoesNotContain("Hidden-Tenant-Name", await withTenant.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_tenant_check_on_mcp_leaves_other_unknown_paths_alone()
    {
        // /mcp-like paths that are not the endpoint stay what they were: unknown routes, 404 without a tenant.
        using var longer = await _client.GetAsync("/mcpx", tenant: null);
        using var nested = await _client.GetAsync("/mcp/other", tenant: null);

        await longer.AssertProblemAsync(HttpStatusCode.NotFound);
        await nested.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- M8 and the other protocol errors ----------

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task M8_an_unknown_tool_is_a_protocol_error_not_a_tool_result(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        await Assert.ThrowsAnyAsync<McpException>(() => mcp.CallAsync("delete_snapshot", """{"snapshotId": "x"}"""));
        await Assert.ThrowsAnyAsync<McpException>(() => mcp.CallAsync("Get_Compensation", """{"employeeId": "E-1001"}"""));
        await Assert.ThrowsAnyAsync<McpException>(() => mcp.CallAsync("upload_csv"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task An_unknown_method_is_a_protocol_error(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        // Under 2026-07-28 the SDK answers with an HTTP error status carrying the JSON-RPC error, which its client
        // reports as a failed HTTP request; under 2025-06-18 as a protocol exception.
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => mcp.SendRequestAsync(new JsonRpcRequest { Method = "snapshots/delete", Id = new RequestId("r-1") }, Ct));
        Assert.True(
            exception is McpException || (exception is HttpRequestException && exception.Message.Contains("\"error\"", StringComparison.Ordinal)),
            exception.ToString());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"id":1,"method":"tools/list"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("[]")]
    public async Task A_malformed_message_is_refused_by_the_protocol_layer_without_a_result(string body)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.PostMcpAsync(body, tenant.ToString());

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        var message = await response.JsonRpcAsync();
        Assert.True(
            !response.IsSuccessStatusCode || (message is { } json && json.TryGetProperty("error", out _)),
            "A malformed message must give an HTTP error or a JSON-RPC error.");
        Assert.False(message is { ValueKind: JsonValueKind.Object } answered && answered.TryGetProperty("result", out _));
    }

    // ---------- M4: argument kinds (3.2) ----------

    public static TheoryData<string, string, string> WrongKinds()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (tool, arguments) in ToolArguments)
        {
            foreach (var argument in arguments)
            {
                foreach (var value in new[] { "1001", "12.5", "true", "false", "{}", """{"value": "E-1001"}""", "[]", """["E-1001"]""" })
                {
                    data.Add(tool, argument.Name, value);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WrongKinds))]
    public async Task M4_an_argument_that_is_not_a_JSON_string_is_a_400_tool_error(string tool, string argument, string value)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);
        var arguments = ValidArguments(tool).ToDictionary(a => a.Key, a => JsonSerializer.Serialize(a.Value));
        arguments[argument] = value;

        var result = await mcp.CallAsync(tool, "{" + string.Join(",", arguments.Select(a => $"{JsonSerializer.Serialize(a.Key)}: {a.Value}")) + "}");

        var problem = result.AssertToolError(HttpStatusCode.BadRequest);

        // The detail names the argument, never its value.
        Assert.DoesNotContain("E-1001", problem.GetRawText(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> RequiredArguments()
    {
        var data = new TheoryData<string, string>();
        foreach (var (tool, arguments) in ToolArguments)
        {
            foreach (var argument in arguments.Where(a => a.Required))
            {
                data.Add(tool, argument.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RequiredArguments))]
    public async Task M4_a_required_argument_that_is_absent_or_null_is_a_400_tool_error(string tool, string argument)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);
        var others = ValidArguments(tool).Where(a => a.Key != argument).ToDictionary(a => a.Key, a => (string?)a.Value);
        var withNull = new Dictionary<string, string?>(others) { [argument] = null };

        // A differently cased name is another argument: names are case-sensitive.
        var wrongCase = new Dictionary<string, string?>(others) { [argument.ToUpperInvariant()] = "x" };

        (await mcp.CallAsync(tool, JsonSerializer.Serialize(others))).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync(tool, JsonSerializer.Serialize(withNull))).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync(tool, JsonSerializer.Serialize(wrongCase))).AssertToolError(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task M4_wrong_kinds_are_tool_errors_in_the_initialize_revision_and_without_arguments_at_all()
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant, McpTestSupport.OldRevision);

        (await mcp.CallAsync("get_compensation", """{"employeeId": 1001}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation")).AssertToolError(HttpStatusCode.BadRequest);

        // tools/call with no arguments member at all.
        using var response = await _client.PostMcpAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_compensation"}}""", tenant.ToString());
        var result = Assert.NotNull(await response.JsonRpcAsync()).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(400, result.GetProperty("structuredContent").GetProperty("status").GetInt32());
    }

    // ---------- An undecodable string argument (3.2, 3.5; since 2.0) ----------

    public static TheoryData<string, string, string> UndecodableArguments()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (tool, arguments) in ToolArguments)
        {
            foreach (var argument in arguments)
            {
                data.Add(tool, argument.Name, McpTestSupport.NewRevision);
                data.Add(tool, argument.Name, McpTestSupport.OldRevision);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UndecodableArguments))]
    public async Task An_undecodable_string_argument_is_a_protocol_error_and_writes_no_entry(string tool, string argument, string revision)
    {
        var tenant = await api.NewTenantAsync(_client);

        // An unpaired surrogate escape: the JSON grammar allows it, .NET cannot decode it.
        var arguments = ValidArguments(tool).ToDictionary(a => a.Key, a => JsonSerializer.Serialize(a.Value));
        arguments[argument] = "\"\\ud800\"";
        var argumentsJson = "{" + string.Join(",", arguments.Select(a => $"{JsonSerializer.Serialize(a.Key)}:{a.Value}")) + "}";

        var meta = revision == McpTestSupport.NewRevision
            ? ""","_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"test","version":"1"}}"""
            : string.Empty;
        var body = $$$"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argumentsJson}}}{{{meta}}}}}""";
        (string Name, string Value)[] headers = revision == McpTestSupport.NewRevision
            ? [("MCP-Protocol-Version", revision), ("Mcp-Method", "tools/call"), ("Mcp-Name", tool)]
            : [];

        using var response = await _client.PostMcpAsync(body, tenant.ToString(), headers);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var message = Assert.NotNull(await response.JsonRpcAsync());
        Assert.True(message.TryGetProperty("error", out _), message.GetRawText());
        Assert.False(message.TryGetProperty("result", out _), message.GetRawText());
        Assert.DoesNotContain(
            api.Logs.Entries,
            e => e.Category == "CompLedger.Mcp" && e.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D"))));
    }

    // ---------- SDK log levels (6.5 #6) ----------

    [Fact]
    public async Task The_SDK_logs_nothing_below_Warning()
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);
        await mcp.ListToolsAsync(cancellationToken: Ct);
        await mcp.CallAsync("get_compensation", """{"employeeId": 1001}""");

        Assert.DoesNotContain(
            api.Logs.Entries, e => e.Category.StartsWith("ModelContextProtocol", StringComparison.Ordinal) && e.Level < LogLevel.Warning);
    }
}

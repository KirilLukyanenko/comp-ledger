using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CompLedger.IntegrationTests;

/// <summary>
/// PRP-005 T3: the six read tools answer what their REST endpoints answer (3.2, 3.4; M2, M3, M6). The core is
/// equivalence: the same inputs through both doors, on the same data, compared.
/// </summary>
public sealed class McpReadToolTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;

    /// <summary>Each read tool's endpoint (3.3): path segments in braces, then its query parameters.</summary>
    private static readonly Dictionary<string, (string Path, string[] Query)> Endpoints = new()
    {
        ["get_compensation"] = ("/api/v1/employees/{employeeId}/compensation", ["asOf", "knownAt"]),
        ["get_compensation_history"] = ("/api/v1/employees/{employeeId}/compensation/history", ["component", "knownAt"]),
        ["get_ingestion"] = ("/api/v1/ingestions/{batchId}", []),
        ["list_snapshots"] = ("/api/v1/snapshots", []),
        ["get_snapshot"] = ("/api/v1/snapshots/{snapshotId}", []),
        ["get_snapshot_compensation"] = ("/api/v1/snapshots/{snapshotId}/employees/{employeeId}/compensation", []),
    };

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Row(string amount, string employee = "E-1001") => Header + $"{employee},BASE_SALARY,2026-01-01,{amount},EUR\n";

    private sealed record Scenario(Guid Tenant, string T1, string T2, string Batch, string RejectedBatch, string Snapshot)
    {
        /// <summary>Replaces the placeholders a test case uses for values known only at run time.</summary>
        public string Fill(string text) => text
            .Replace("<t1>", T1, StringComparison.Ordinal)
            .Replace("<t2>", T2, StringComparison.Ordinal)
            .Replace("<batch>", Batch, StringComparison.Ordinal)
            .Replace("<BATCH>", Batch.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("<rejected>", RejectedBatch, StringComparison.Ordinal)
            .Replace("<snapshot>", Snapshot, StringComparison.Ordinal)
            .Replace("<SNAPSHOT>", Snapshot.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("<random>", Guid.NewGuid().ToString(), StringComparison.Ordinal);
    }

    /// <summary>The data of PRP-002 A1 (50000 at t1, corrected to 51000 at t2), a rejected batch and a snapshot.</summary>
    private async Task<Scenario> NewScenarioAsync()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, Row("50000"));
        using var second = await _client.UploadAsync(tenant, Row("51000"));
        var batch = await second.JsonAsync();
        using var rejected = await _client.UploadAsync(tenant, Header + "E-1001,NOT_A_COMPONENT,2026-01-01,1,EUR\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var snapshot = await _client.SendAsync(
            HttpMethod.Post, "/api/v1/snapshots", tenant, new StringContent("""{"name": "Q1 review", "asOf": "2026-03-31"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);

        return new Scenario(
            tenant,
            t1,
            batch.GetProperty("appliedAt").GetString()!,
            batch.GetProperty("batchId").GetString()!,
            (await rejected.JsonAsync()).GetProperty("batchId").GetString()!,
            (await snapshot.JsonAsync()).GetProperty("snapshotId").GetString()!);
    }

    /// <summary>The equivalent request (3.1): the string arguments the tool names, in the path and the query.</summary>
    private static string EquivalentUrl(string tool, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var (path, query) = Endpoints[tool];
        var url = new StringBuilder(path);
        var separator = '?';
        foreach (var argument in document.RootElement.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.String))
        {
            var value = Uri.EscapeDataString(argument.Value.GetString()!);
            if (path.Contains("{" + argument.Name + "}", StringComparison.Ordinal))
            {
                url.Replace("{" + argument.Name + "}", value);
            }
            else if (query.Contains(argument.Name))
            {
                url.Append(separator).Append(argument.Name).Append('=').Append(value);
                separator = '&';
            }
        }

        return url.ToString();
    }

    /// <summary>
    /// Calls the tool and its equivalent request and compares them as 3.4 says: the status, and the body or the
    /// ProblemDetails status, title and type. Returns the status.
    /// </summary>
    private async Task<int> AssertEquivalentAsync(McpClient mcp, Guid tenant, string tool, string argumentsJson)
    {
        var result = await mcp.CallAsync(tool, argumentsJson);
        using var response = await _client.GetAsync(EquivalentUrl(tool, argumentsJson), tenant);
        var body = await response.JsonAsync();

        if (response.IsSuccessStatusCode)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var structured = result.AssertSuccess();
            Assert.True(JsonElement.DeepEquals(body, structured), $"REST: {body.GetRawText()}\nTool: {structured.GetRawText()}");
            Assert.Equal(body.EnumerateObject().Select(p => p.Name), structured.EnumerateObject().Select(p => p.Name));
        }
        else
        {
            var problem = result.AssertToolError(response.StatusCode);
            Assert.Equal(body.GetProperty("status").GetInt32(), problem.GetProperty("status").GetInt32());
            Assert.Equal(body.GetProperty("title").GetString(), problem.GetProperty("title").GetString());
            Assert.Equal(body.GetProperty("type").GetString(), problem.GetProperty("type").GetString());
            Assert.Equal(body.TryGetProperty("detail", out _), problem.TryGetProperty("detail", out _));
            Assert.True(problem.TryGetProperty("traceId", out _));
        }

        return (int)response.StatusCode;
    }

    // ---------- M2, M3, M6 ----------

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task M2_get_compensation_gives_the_REST_body_as_structured_content_and_as_text(string? revision)
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant, revision);
        var arguments = McpTestSupport.Arguments(("employeeId", "E-1001"), ("asOf", "2026-03-01"), ("knownAt", scenario.T1));

        var result = await mcp.CallAsync("get_compensation", arguments);

        var structured = result.AssertSuccess();
        Assert.Equal(["employeeId", "asOf", "knownAt", "components"], structured.EnumerateObject().Select(p => p.Name));
        Assert.Equal("E-1001", structured.GetProperty("employeeId").GetString());
        Assert.Equal("2026-03-01", structured.GetProperty("asOf").GetString());
        Assert.Equal(scenario.T1, structured.GetProperty("knownAt").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", structured.GetProperty("knownAt").GetString());
        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], structured.GetProperty("components").Periods());
        Assert.Equal(JsonValueKind.Number, structured.GetProperty("components")[0].GetProperty("amount").ValueKind);

        using var rest = await _client.GetAsync($"/api/v1/employees/E-1001/compensation?asOf=2026-03-01&knownAt={scenario.T1}", scenario.Tenant);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(await rest.Content.ReadAsStringAsync(Ct), text);
        Assert.Equal(200, await AssertEquivalentAsync(mcp, scenario.Tenant, "get_compensation", arguments));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task M3_errors_are_tool_errors_with_the_REST_status(string? revision)
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant, revision);

        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-1001", "knownAt": "2026-11-13T10:00:00+01:00"}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-9999"}""")).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-1001", "asOf": " 2026-03-01"}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-1001", "knownAt": " 2026-11-13T10:00:00Z"}""")).AssertToolError(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task M6_another_tenant_sees_nothing_and_gets_404_for_ids_that_exist_elsewhere()
    {
        var scenario = await NewScenarioAsync();
        var other = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(other);

        (await mcp.CallAsync("get_snapshot", McpTestSupport.Arguments(("snapshotId", scenario.Snapshot)))).AssertToolError(HttpStatusCode.NotFound);
        Assert.Empty((await mcp.CallAsync("list_snapshots")).AssertSuccess().GetProperty("snapshots").EnumerateArray());
        (await mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", scenario.Snapshot), ("employeeId", "E-1001"))))
            .AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_ingestion", McpTestSupport.Arguments(("batchId", scenario.Batch)))).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-1001"}""")).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation_history", """{"employeeId": "E-1001"}""")).AssertToolError(HttpStatusCode.NotFound);

        // The same ids for their own tenant, to show the 404s are isolation.
        await using var own = await api.ConnectMcpAsync(scenario.Tenant);
        (await own.CallAsync("get_snapshot", McpTestSupport.Arguments(("snapshotId", scenario.Snapshot)))).AssertSuccess();
        (await own.CallAsync("get_ingestion", McpTestSupport.Arguments(("batchId", scenario.Batch)))).AssertSuccess();
    }

    [Fact]
    public async Task The_tenant_of_a_call_is_the_tenant_of_its_HTTP_request_never_an_earlier_one()
    {
        var first = await NewScenarioAsync();
        var second = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(second, Row("77000"));
        await using var mcpFirst = await api.ConnectMcpAsync(first.Tenant);
        await using var mcpSecond = await api.ConnectMcpAsync(second);

        for (var i = 0; i < 3; i++)
        {
            var a = mcpFirst.CallAsync("get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01"}""");
            var b = mcpSecond.CallAsync("get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01"}""");
            Assert.Equal(51000m, (await a).AssertSuccess().GetProperty("components")[0].GetProperty("amount").GetDecimal());
            Assert.Equal(77000m, (await b).AssertSuccess().GetProperty("components")[0].GetProperty("amount").GetDecimal());
        }
    }

    // ---------- Equivalence (3.4) ----------

    public static TheoryData<string, string, int> Cases => new()
    {
        // get_compensation: success, with each optional argument given, absent and null.
        { "get_compensation", """{"employeeId": "E-1001"}""", 200 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01"}""", 200 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "1999-01-01"}""", 200 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01", "knownAt": "<t1>"}""", 200 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01", "knownAt": "<t2>"}""", 200 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": null, "knownAt": null}""", 200 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01", "knownAt": null}""", 200 },

        // Arguments the tool does not name are ignored, whatever their kind; names are case-sensitive.
        { "get_compensation", """{"employeeId": "E-1001", "page": 2, "ASOF": "garbage", "KnownAt": 5, "extra": {"a": [1]}, "component": "x"}""", 200 },

        // 400: the endpoint's validation on the string exactly as given.
        { "get_compensation", """{"employeeId": "E-1001", "asOf": ""}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": " 2026-03-01"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01 "}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-3-1"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-02-30"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "1899-12-31"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "3000-01-01"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01T00:00:00Z"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": ""}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": " <t1>"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "<t1> "}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "2026-11-13T10:00:00+01:00"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "2026-11-13T10:00:00z"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "2026-11-13T10:00:00.1234567Z"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "2026-11-13"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "2999-01-01T00:00:00Z"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "knownAt": "1899-12-31T23:59:59Z"}""", 400 },
        { "get_compensation", """{"employeeId": "E-1001", "asOf": "bad", "knownAt": "bad"}""", 400 },

        // 404: no values, and ids that are no employee ids; a value is used as written, not trimmed or decoded.
        { "get_compensation", """{"employeeId": "E-9999"}""", 404 },
        { "get_compensation", """{"employeeId": "e-1001"}""", 404 },
        { "get_compensation", """{"employeeId": " E-1001"}""", 404 },
        { "get_compensation", """{"employeeId": "E-1001 "}""", 404 },
        { "get_compensation", """{"employeeId": "E 1001"}""", 404 },
        { "get_compensation", """{"employeeId": "E%2D1001"}""", 404 },
        { "get_compensation", """{"employeeId": "É-1001"}""", 404 },
        { "get_compensation", """{"employeeId": "-E-1001"}""", 404 },
        { "get_compensation", """{"employeeId": "E-1001;DROP"}""", 404 },
        { "get_compensation", """{"employeeId": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""", 404 },
        { "get_compensation", """{"employeeId": "E-9999", "knownAt": "<t2>"}""", 404 },
        { "get_compensation", """{"employeeId": "E 1001", "knownAt": "<t2>"}""", 404 },

        // The other checks come before the lookup, also for an id that is no employee id.
        { "get_compensation", """{"employeeId": "E-9999", "asOf": "bad"}""", 400 },
        { "get_compensation", """{"employeeId": "E 1001", "asOf": "bad"}""", 400 },
        { "get_compensation", """{"employeeId": "E 1001", "knownAt": "2999-01-01T00:00:00Z"}""", 400 },
        { "get_compensation", """{"employeeId": "E-9999", "knownAt": "2999-01-01T00:00:00Z"}""", 400 },

        // get_compensation_history.
        { "get_compensation_history", """{"employeeId": "E-1001"}""", 200 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": "BASE_SALARY"}""", 200 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": "ALLOWANCE"}""", 200 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": "BASE_SALARY", "knownAt": "<t1>"}""", 200 },
        { "get_compensation_history", """{"employeeId": "E-1001", "knownAt": "<t2>"}""", 200 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": null, "knownAt": null, "asOf": "ignored"}""", 200 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": ""}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": "base_salary"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": " BASE_SALARY"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": "BASE_SALARY "}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "component": "SHARES"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "knownAt": ""}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "knownAt": "2026-11-13T10:00:00+01:00"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-1001", "knownAt": "2999-01-01T00:00:00Z"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E 1001", "knownAt": "2999-01-01T00:00:00Z"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E 1001", "component": "nope"}""", 400 },
        { "get_compensation_history", """{"employeeId": "E-9999"}""", 404 },
        { "get_compensation_history", """{"employeeId": "E-9999", "component": "BASE_SALARY", "knownAt": "<t1>"}""", 404 },
        { "get_compensation_history", """{"employeeId": " E-1001"}""", 404 },
        { "get_compensation_history", """{"employeeId": "E 1001", "knownAt": "<t1>"}""", 404 },

        // get_ingestion: the REST spelling rules, whatever they are for a spelling.
        { "get_ingestion", """{"batchId": "<batch>"}""", 200 },
        { "get_ingestion", """{"batchId": "<BATCH>"}""", 200 },
        { "get_ingestion", """{"batchId": "<rejected>"}""", 200 },
        { "get_ingestion", """{"batchId": "<batch>", "employeeId": 7}""", 200 },
        { "get_ingestion", """{"batchId": " <batch>"}""", 200 },
        { "get_ingestion", """{"batchId": "<batch> "}""", 200 },
        { "get_ingestion", """{"batchId": "<random>"}""", 404 },
        { "get_ingestion", """{"batchId": "not-a-uuid"}""", 404 },
        { "get_ingestion", """{"batchId": "{<batch>}"}""", 404 },
        { "get_ingestion", """{"batchId": "(<batch>)"}""", 404 },
        { "get_ingestion", """{"batchId": "<batch>x"}""", 404 },
        { "get_ingestion", """{"batchId": "00000000000000000000000000000000"}""", 404 },
        { "get_ingestion", """{"batchId": "<snapshot>"}""", 404 },

        // list_snapshots takes no arguments and ignores all of them.
        { "list_snapshots", "{}", 200 },
        { "list_snapshots", """{"name": "x", "limit": 1, "asOf": null}""", 200 },

        // get_snapshot: the hyphenated UUID in either case; anything else is a failed lookup.
        { "get_snapshot", """{"snapshotId": "<snapshot>"}""", 200 },
        { "get_snapshot", """{"snapshotId": "<SNAPSHOT>"}""", 200 },
        { "get_snapshot", """{"snapshotId": "<random>"}""", 404 },
        { "get_snapshot", """{"snapshotId": "Q1 review"}""", 404 },
        { "get_snapshot", """{"snapshotId": " <snapshot>"}""", 404 },
        { "get_snapshot", """{"snapshotId": "<snapshot> "}""", 404 },
        { "get_snapshot", """{"snapshotId": "{<snapshot>}"}""", 404 },
        { "get_snapshot", """{"snapshotId": "<batch>"}""", 404 },

        // get_snapshot_compensation: the snapshot first, then the employee. It has no asOf and no knownAt.
        { "get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "E-1001"}""", 200 },
        { "get_snapshot_compensation", """{"snapshotId": "<SNAPSHOT>", "employeeId": "E-1001"}""", 200 },
        { "get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "E-1001", "asOf": "2020-01-01", "knownAt": "bad"}""", 200 },
        { "get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "E-9999"}""", 404 },
        { "get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "e-1001"}""", 404 },
        { "get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "E 1001"}""", 404 },
        { "get_snapshot_compensation", """{"snapshotId": "<random>", "employeeId": "E-1001"}""", 404 },
        { "get_snapshot_compensation", """{"snapshotId": "nope", "employeeId": "E-1001"}""", 404 },
        { "get_snapshot_compensation", """{"snapshotId": " <snapshot>", "employeeId": "E-1001"}""", 404 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_read_tool_answers_what_its_endpoint_answers(string tool, string arguments, int expectedStatus)
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant);

        Assert.Equal(expectedStatus, await AssertEquivalentAsync(mcp, scenario.Tenant, tool, scenario.Fill(arguments)));
    }

    [Fact]
    public async Task Equivalence_holds_in_the_initialize_revision_for_each_tool_and_status()
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant, McpTestSupport.OldRevision);

        foreach (var (tool, arguments, expected) in new[]
        {
            ("get_compensation", """{"employeeId": "E-1001", "asOf": "2026-03-01", "knownAt": "<t1>"}""", 200),
            ("get_compensation", """{"employeeId": "E-1001", "asOf": ""}""", 400),
            ("get_compensation_history", """{"employeeId": "E-1001", "component": "BASE_SALARY"}""", 200),
            ("get_compensation_history", """{"employeeId": "E-9999"}""", 404),
            ("get_ingestion", """{"batchId": "<batch>"}""", 200),
            ("get_ingestion", """{"batchId": "<random>"}""", 404),
            ("list_snapshots", "{}", 200),
            ("get_snapshot", """{"snapshotId": "<snapshot>"}""", 200),
            ("get_snapshot", """{"snapshotId": "x"}""", 404),
            ("get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "E-1001"}""", 200),
            ("get_snapshot_compensation", """{"snapshotId": "<snapshot>", "employeeId": "E-9999"}""", 404),
        })
        {
            Assert.Equal(expected, await AssertEquivalentAsync(mcp, scenario.Tenant, tool, scenario.Fill(arguments)));
        }
    }

    [Fact]
    public async Task Snapshot_reads_show_the_snapshot_as_REST_does()
    {
        var scenario = await NewScenarioAsync();
        await _client.AppliedAtAsync(scenario.Tenant, Row("52000"));
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant);

        var read = (await mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", scenario.Snapshot), ("employeeId", "E-1001"))))
            .AssertSuccess();
        var list = (await mcp.CallAsync("list_snapshots")).AssertSuccess();

        // The snapshot was taken after the correction to 51000 and before 52000.
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], read.GetProperty("components").Periods());
        Assert.Equal(scenario.Snapshot, read.GetProperty("snapshotId").GetString());
        var listed = Assert.Single(list.GetProperty("snapshots").EnumerateArray());
        Assert.Equal(["snapshotId", "name", "asOf", "knownAt"], listed.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Q1 review", listed.GetProperty("name").GetString());
    }

    // ---------- Path arguments that cannot travel in a URL (3.2) ----------

    public static TheoryData<string> UntravellableValues => ["", "/", "E-1001/compensation", "a/b", "E-1\u00001", "\u0000", "\t", "\r\n", "..", "."];

    [Theory]
    [MemberData(nameof(UntravellableValues))]
    public async Task A_path_argument_no_URL_could_carry_is_404_at_the_lookup_step(string value)
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant);
        var future = "2999-01-01T00:00:00Z";

        // An empty value is given, not absent: never the 400 of a missing required argument.
        (await mcp.CallAsync("get_compensation", McpTestSupport.Arguments(("employeeId", value)))).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation", McpTestSupport.Arguments(("employeeId", value), ("knownAt", scenario.T2)))).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation_history", McpTestSupport.Arguments(("employeeId", value)))).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation_history", McpTestSupport.Arguments(("employeeId", value), ("component", "BASE_SALARY"), ("knownAt", scenario.T1))))
            .AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", scenario.Snapshot), ("employeeId", value))))
            .AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", value), ("employeeId", "E-1001"))))
            .AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_snapshot", McpTestSupport.Arguments(("snapshotId", value)))).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_ingestion", McpTestSupport.Arguments(("batchId", value)))).AssertToolError(HttpStatusCode.NotFound);

        // Every other check still comes first: the parameters, and knownAt against the request time.
        (await mcp.CallAsync("get_compensation", McpTestSupport.Arguments(("employeeId", value), ("asOf", "bad")))).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation", McpTestSupport.Arguments(("employeeId", value), ("knownAt", future)))).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation_history", McpTestSupport.Arguments(("employeeId", value), ("knownAt", future)))).AssertToolError(HttpStatusCode.BadRequest);
    }

    // ---------- Waiting (3.4, PRP-002 3.4 item 4) ----------

    [Fact]
    public async Task Calls_with_an_instant_wait_for_a_batch_being_applied_even_for_an_id_that_is_no_employee_id()
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant);

        // A batch of the tenant being applied: its exclusive lock, held until the test commits.
        await using var applying = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        await using var transaction = await applying.BeginTransactionAsync(Ct);
        var tenantText = scenario.Tenant.ToString("D");
        await applying.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@tenantText, 0))", new { tenantText }, transaction);

        var withInstant = mcp.CallAsync("get_compensation", McpTestSupport.Arguments(("employeeId", "E-1001"), ("knownAt", scenario.T2)));
        var history = mcp.CallAsync("get_compensation_history", McpTestSupport.Arguments(("employeeId", "E-1001"), ("knownAt", scenario.T2)));
        var throughSnapshot = mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", scenario.Snapshot), ("employeeId", "E-1001")));
        var invalidId = mcp.CallAsync("get_compensation", McpTestSupport.Arguments(("employeeId", "no such id"), ("knownAt", scenario.T2)));

        // Calls without an instant take no lock.
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-1001"}""").WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();
        (await mcp.CallAsync("list_snapshots").WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();

        await Task.Delay(TimeSpan.FromMilliseconds(700), Ct);
        Assert.False(withInstant.IsCompleted, "get_compensation with knownAt must wait.");
        Assert.False(history.IsCompleted, "get_compensation_history with knownAt must wait.");
        Assert.False(throughSnapshot.IsCompleted, "get_snapshot_compensation must wait.");
        Assert.False(invalidId.IsCompleted, "The request-time check, and so the wait, comes before the lookup of an invalid id.");

        await transaction.CommitAsync(Ct);
        (await withInstant.WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();
        (await history.WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();
        (await throughSnapshot.WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();
        (await invalidId.WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertToolError(HttpStatusCode.NotFound);
    }

    // ---------- Unexpected exceptions (3.4, 6.5 #2) ----------

    [Fact]
    public async Task An_unexpected_exception_is_the_endpoints_500_and_says_nothing_about_the_exception()
    {
        var scenario = await NewScenarioAsync();
        await using var mcp = await api.ConnectMcpAsync(scenario.Tenant);

        // A snapshot whose instant is in the future cannot be created through the API; reading through it throws.
        var broken = Guid.NewGuid();
        await using (var connection = await api.Database.AppDataSource.OpenConnectionAsync(Ct))
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            var tenantText = scenario.Tenant.ToString("D");
            await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantText, true)", new { tenantText }, transaction);
            await connection.ExecuteAsync(
                "INSERT INTO snapshots (id, tenant_id, name, as_of, known_at) VALUES (@broken, @tenant, 'From the future', DATE '2026-03-31', now() + interval '1 day')",
                new { broken, tenant = scenario.Tenant },
                transaction);
            await transaction.CommitAsync(Ct);
        }

        var arguments = McpTestSupport.Arguments(("snapshotId", broken.ToString()), ("employeeId", "E-1001"));
        var result = await mcp.CallAsync("get_snapshot_compensation", arguments);

        Assert.Equal(500, await AssertEquivalentAsync(mcp, scenario.Tenant, "get_snapshot_compensation", arguments));
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain("clock", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }
}

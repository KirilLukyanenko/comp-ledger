using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace CompLedger.IntegrationTests;

/// <summary>PRP-005 T4: the per-call log entry and what no log line may hold (3.6, 6.5 #6).</summary>
public sealed class McpLoggingTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string McpCategory = "CompLedger.Mcp";
    private const string SnapshotCategory = "CompLedger.Snapshots";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsFramework(CapturedLogs.Entry entry) =>
        entry.Category.StartsWith("Microsoft.", StringComparison.Ordinal) || entry.Category.StartsWith("System.", StringComparison.Ordinal);

    private static string Text(CapturedLogs.Entry entry) =>
        $"{entry.Category} {entry.Message} {string.Join(' ', entry.State.Select(p => $"{p.Key}={p.Value}"))}";

    private List<CapturedLogs.Entry> EntriesOf(Guid tenant, string category) =>
        [.. api.Logs.Entries.Where(e => e.Category == category && e.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D"))))];

    private static Dictionary<string, object?> State(CapturedLogs.Entry entry) =>
        entry.State.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task Each_tools_call_writes_one_entry_with_the_tool_the_tenant_the_status_and_the_duration(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, ApiFactory.A1);
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        // In order: 200, 400 from the endpoint's rules, 400 from the argument kinds, 404, 201, 409, 400, 200, 404.
        (string Tool, string Arguments, int Status)[] calls =
        [
            ("get_compensation", """{"employeeId": "E-1001"}""", 200),
            ("get_compensation", """{"employeeId": "E-1001", "asOf": "bad"}""", 400),
            ("get_compensation_history", """{"employeeId": 1001}""", 400),
            ("get_compensation_history", """{"employeeId": "E-9999"}""", 404),
            ("create_snapshot", """{"name": "Logged", "asOf": "2026-03-31"}""", 201),
            ("create_snapshot", """{"name": "Logged", "asOf": "2026-03-31"}""", 409),
            ("create_snapshot", """{"name": "", "asOf": "2026-03-31"}""", 400),
            ("list_snapshots", "{}", 200),
            ("get_ingestion", """{"batchId": "nope"}""", 404),
            ("get_snapshot", """{"snapshotId": "nope"}""", 404),
            ("get_snapshot_compensation", """{"snapshotId": "nope", "employeeId": "E-1001"}""", 404),
        ];
        foreach (var (tool, arguments, status) in calls)
        {
            Assert.Equal(status, (await mcp.CallAsync(tool, arguments)).Status(successStatus: status));
        }

        var entries = EntriesOf(tenant, McpCategory);
        Assert.Equal(calls.Length, entries.Count);
        foreach (var (entry, call) in entries.Zip(calls))
        {
            var state = State(entry);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Equal(["DurationMs", "Status", "TenantId", "Tool"], state.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(call.Tool, Assert.IsType<string>(state["Tool"]));
            Assert.Equal(tenant.ToString("D"), Assert.IsType<string>(state["TenantId"]));
            Assert.Equal(call.Status, Assert.IsType<int>(state["Status"]));
            Assert.InRange(Assert.IsType<long>(state["DurationMs"]), 0L, 60_000L);
        }

        // The snapshot entry of PRP-002 is written by the shared handler: once, for the one snapshot created.
        var snapshotEntry = Assert.Single(EntriesOf(tenant, SnapshotCategory));
        Assert.Equal(["AsOf", "DurationMs", "KnownAt", "SnapshotId", "TenantId"], State(snapshotEntry).Keys.Order(StringComparer.Ordinal));
        Assert.Equal("2026-03-31", State(snapshotEntry)["AsOf"]);
    }

    [Fact]
    public async Task Nothing_but_a_tools_call_writes_an_entry()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, ApiFactory.A1);
        // Below Warning: what the application writes of its own accord. The SDK may warn about a protocol error.
        var before = api.Logs.Entries.Count(e => !IsFramework(e) && e.Level < LogLevel.Warning);

        // server/discover and initialize (each client's handshake), tools/list, an unknown tool, an unknown method,
        // a notification, a malformed message, other HTTP methods, and requests without a tenant.
        await using (var mcp = await api.ConnectMcpAsync(tenant))
        await using (var old = await api.ConnectMcpAsync(tenant, McpTestSupport.OldRevision))
        {
            await mcp.ListToolsAsync(cancellationToken: Ct);
            await old.ListToolsAsync(cancellationToken: Ct);
            await Assert.ThrowsAnyAsync<McpException>(() => mcp.CallAsync("delete_snapshot", """{"snapshotId": "E-1001"}"""));
            await Assert.ThrowsAnyAsync<McpException>(() => old.CallAsync("delete_snapshot", """{"snapshotId": "E-1001"}"""));
            await Assert.ThrowsAnyAsync<Exception>(
                () => old.SendRequestAsync(new ModelContextProtocol.Protocol.JsonRpcRequest { Method = "no/such", Id = new ModelContextProtocol.Protocol.RequestId("x") }, Ct));
        }

        foreach (var body in new[] { """{"jsonrpc":"2.0","method":"notifications/initialized"}""", "{ not json" })
        {
            using var response = await _client.PostMcpAsync(body, tenant.ToString());
        }

        using (var noTenant = await _client.PostMcpAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_snapshots","arguments":{}}}""", null))
        using (var get = await _client.GetAsync("/mcp", tenant))
        using (var delete = await _client.SendAsync(HttpMethod.Delete, "/mcp", tenant))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noTenant.StatusCode);
        }

        // REST reads still log nothing of their own.
        using (var rest = await _client.GetAsync("/api/v1/employees/E-1001/compensation", tenant))
        using (var restList = await _client.GetAsync("/api/v1/snapshots", tenant))
        {
            Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        }

        Assert.Empty(EntriesOf(tenant, McpCategory));
        Assert.DoesNotContain(api.Logs.Entries, e => e.Category == McpCategory && e.State.Any(p => p.Key == "Tool" && Equals(p.Value, "delete_snapshot")));
        Assert.Equal(before, api.Logs.Entries.Count(e => !IsFramework(e) && e.Level < LogLevel.Warning));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task No_log_line_of_a_tool_call_holds_an_argument_value_an_employee_id_a_name_or_an_amount(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, ApiFactory.Header + "E-SECRET-77,BASE_SALARY,2026-01-01,54321.1234,EUR\n");
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        // Successes, which carry the id and the amount in their results.
        Assert.Contains("54321.1234", (await mcp.CallAsync("get_compensation", """{"employeeId": "E-SECRET-77", "asOf": "2026-04-05"}""")).AssertSuccess().GetRawText());
        (await mcp.CallAsync("get_compensation_history", """{"employeeId": "E-SECRET-77", "component": "BASE_SALARY"}""")).AssertSuccess();
        var snapshot = (await mcp.CallAsync("create_snapshot", """{"name": "Zebra-Quokka review", "asOf": "2026-04-05"}""")).AssertSuccess();
        var snapshotId = snapshot.GetProperty("snapshotId").GetString()!;
        (await mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", snapshotId), ("employeeId", "E-SECRET-77")))).AssertSuccess();
        (await mcp.CallAsync("list_snapshots", """{"ignored": "IGNORED-ARGUMENT-VALUE"}""")).AssertSuccess();

        // Errors of every kind, each with a recognisable argument value.
        (await mcp.CallAsync("create_snapshot", """{"name": "Zebra-Quokka review", "asOf": "2026-04-05"}""")).AssertToolError(HttpStatusCode.Conflict);
        (await mcp.CallAsync("create_snapshot", """{"name": "Okapi-Refused name", "asOf": "ASOF-SECRET"}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-MISSING-88", "knownAt": "KNOWNAT-SECRET"}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-MISSING-88"}""")).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation", """{"employeeId": "E-INVALID 99\u0000"}""")).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_compensation", """{"employeeId": 918273645}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_compensation_history", """{"employeeId": "E-SECRET-77", "component": "COMPONENT-SECRET"}""")).AssertToolError(HttpStatusCode.BadRequest);
        (await mcp.CallAsync("get_ingestion", """{"batchId": "BATCH-SECRET"}""")).AssertToolError(HttpStatusCode.NotFound);
        (await mcp.CallAsync("get_snapshot", """{"snapshotId": "SNAPSHOT-SECRET"}""")).AssertToolError(HttpStatusCode.NotFound);
        await Assert.ThrowsAnyAsync<McpException>(() => mcp.CallAsync("delete_snapshot", """{"snapshotId": "UNKNOWN-TOOL-SECRET"}"""));

        string[] secrets =
        [
            "E-SECRET-77", "54321", "Zebra", "Quokka", "IGNORED-ARGUMENT-VALUE", "Okapi", "ASOF-SECRET", "E-MISSING-88", "KNOWNAT-SECRET", "E-INVALID",
            "918273645", "COMPONENT-SECRET", "BATCH-SECRET", "SNAPSHOT-SECRET", "UNKNOWN-TOOL-SECRET",
        ];
        var application = api.Logs.Entries.Where(e => !IsFramework(e)).ToList();
        var everything = string.Join('\n', application.Select(Text));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }

        // Argument values that another entry may hold for its own reasons (the snapshot entry has SnapshotId and
        // AsOf) are still in no MCP entry: not the tool's, not the SDK's.
        var mcpEntries = string.Join('\n', application.Where(e => e.Category == McpCategory || e.Category.StartsWith("ModelContextProtocol", StringComparison.Ordinal)).Select(Text));
        Assert.DoesNotContain("2026-04-05", mcpEntries, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshotId, mcpEntries, StringComparison.Ordinal);
        Assert.DoesNotContain("BASE_SALARY", mcpEntries, StringComparison.Ordinal);

        // The calls were logged, and the SDK wrote nothing below Warning.
        Assert.Equal(14, EntriesOf(tenant, McpCategory).Count);
        Assert.DoesNotContain(application, e => e.Category.StartsWith("ModelContextProtocol", StringComparison.Ordinal) && e.Level < LogLevel.Warning);
        Assert.Single(EntriesOf(tenant, SnapshotCategory));
    }

    [Fact]
    public async Task A_snapshot_created_through_REST_still_writes_only_its_own_entry()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var created = await _client.SendAsync(
            HttpMethod.Post, "/api/v1/snapshots", tenant, new StringContent("""{"name": "REST only", "asOf": "2026-03-31"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Single(EntriesOf(tenant, SnapshotCategory));
        Assert.Empty(EntriesOf(tenant, McpCategory));
    }
}

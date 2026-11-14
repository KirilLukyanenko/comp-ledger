using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;

namespace CompLedger.IntegrationTests;

/// <summary>PRP-005 T4: <c>create_snapshot</c> is <c>POST /api/v1/snapshots</c> with a body built from its two arguments (3.3, 3.4; M5).</summary>
public sealed class McpCreateSnapshotTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Snapshots = "/api/v1/snapshots";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Row(string amount) => ApiFactory.Header + $"E-1001,BASE_SALARY,2026-01-01,{amount},EUR\n";

    private Task<HttpResponseMessage> PostAsync(Guid tenant, string? name, string? asOf) =>
        _client.SendAsync(HttpMethod.Post, Snapshots, tenant, new StringContent(JsonSerializer.Serialize(new { name, asOf }), Encoding.UTF8, "application/json"));

    private static string Arguments(string? name, string? asOf) => McpTestSupport.Arguments(("name", name), ("asOf", asOf));

    [Theory]
    [InlineData(null)]
    [InlineData(McpTestSupport.OldRevision)]
    public async Task M5_a_snapshot_is_created_once_and_read_identically_through_both_doors(string? revision)
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Row("50000"));
        var t2 = await _client.AppliedAtAsync(tenant, Row("51000"));
        await using var mcp = await api.ConnectMcpAsync(tenant, revision);

        var created = await mcp.CallAsync("create_snapshot", """{"name": "Q1 review", "asOf": "2026-03-31"}""");
        var again = await mcp.CallAsync("create_snapshot", """{"name": "Q1 review", "asOf": "2026-03-31"}""");

        // The snapshot body, with its id: there is no Location.
        var snapshot = created.AssertSuccess();
        Assert.Equal(["snapshotId", "name", "asOf", "knownAt"], snapshot.EnumerateObject().Select(p => p.Name));
        var snapshotId = snapshot.GetProperty("snapshotId").GetString()!;
        Assert.True(Guid.TryParseExact(snapshotId, "D", out _));
        Assert.Equal("Q1 review", snapshot.GetProperty("name").GetString());
        Assert.Equal("2026-03-31", snapshot.GetProperty("asOf").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", snapshot.GetProperty("knownAt").GetString());
        Assert.True(string.CompareOrdinal(snapshot.GetProperty("knownAt").GetString(), t2) >= 0);

        again.AssertToolError(HttpStatusCode.Conflict);

        // Listed and read identically by the tools and by REST.
        using var restList = await _client.GetAsync(Snapshots, tenant);
        using var restSnapshot = await _client.GetAsync($"{Snapshots}/{snapshotId}", tenant);
        var list = (await mcp.CallAsync("list_snapshots")).AssertSuccess();
        Assert.True(JsonElement.DeepEquals(await restList.JsonAsync(), list));
        Assert.True(JsonElement.DeepEquals(Assert.Single(list.GetProperty("snapshots").EnumerateArray()), snapshot));
        Assert.True(JsonElement.DeepEquals(await restSnapshot.JsonAsync(), snapshot));
        Assert.True(JsonElement.DeepEquals(
            (await mcp.CallAsync("get_snapshot", McpTestSupport.Arguments(("snapshotId", snapshotId)))).AssertSuccess(), snapshot));

        // It does not move: a later correction is not in it.
        await _client.AppliedAtAsync(tenant, Row("52000"));
        var read = (await mcp.CallAsync("get_snapshot_compensation", McpTestSupport.Arguments(("snapshotId", snapshotId), ("employeeId", "E-1001"))))
            .AssertSuccess();
        Assert.Equal(snapshotId, read.GetProperty("snapshotId").GetString());
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], read.GetProperty("components").Periods());
        using var restRead = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1001/compensation", tenant);
        Assert.True(JsonElement.DeepEquals(await restRead.JsonAsync(), read));
    }

    [Fact]
    public async Task A_snapshot_created_by_the_tool_has_the_shape_of_one_created_by_REST()
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);

        var byTool = (await mcp.CallAsync("create_snapshot", Arguments("  By tool \t", "2026-06-30"))).AssertSuccess();
        using var response = await PostAsync(tenant, "  By REST \t", "2026-06-30");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var byRest = await response.JsonAsync();

        // The name is trimmed by the shared handler, as on REST; the date is used as written.
        Assert.Equal("By tool", byTool.GetProperty("name").GetString());
        Assert.Equal("By REST", byRest.GetProperty("name").GetString());
        Assert.Equal(byRest.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind)), byTool.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind)));
        Assert.Equal(byRest.GetProperty("asOf").GetString(), byTool.GetProperty("asOf").GetString());
    }

    public static TheoryData<string?, string?> RefusedBodies => new()
    {
        { "", "2026-03-31" },
        { "   ", "2026-03-31" },
        { "\t\r\n", "2026-03-31" },
        { new string('n', 201), "2026-03-31" },
        { "Nul\u0000name", "2026-03-31" },
        { "Good name", "" },
        { "Good name", " 2026-03-31" },
        { "Good name", "2026-03-31 " },
        { "Good name", "2026-02-30" },
        { "Good name", "2026-3-31" },
        { "Good name", "1899-12-31" },
        { "Good name", "3000-01-01" },
        { "Good name", "today" },
        { "", "" },
    };

    [Theory]
    [MemberData(nameof(RefusedBodies))]
    public async Task Values_the_endpoint_refuses_are_refused_by_the_tool_with_the_same_problem(string? name, string? asOf)
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);

        var result = await mcp.CallAsync("create_snapshot", Arguments(name, asOf));
        using var response = await PostAsync(tenant, name, asOf);

        await AssertSameProblemAsync(HttpStatusCode.BadRequest, response, result);
        Assert.Empty((await mcp.CallAsync("list_snapshots")).AssertSuccess().GetProperty("snapshots").EnumerateArray());
    }

    [Fact]
    public async Task A_name_conflict_is_409_through_either_door_whichever_created_the_snapshot()
    {
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);
        await using var mcpOther = await api.ConnectMcpAsync(other);

        (await mcp.CallAsync("create_snapshot", Arguments("By tool", "2026-03-31"))).AssertSuccess();
        using (var created = await PostAsync(tenant, "By REST", "2026-03-31"))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // Created by the tool, refused on REST; created by REST, refused by the tool; after trimming too.
        using var restConflict = await PostAsync(tenant, "By tool", "2026-06-30");
        var toolConflict = await mcp.CallAsync("create_snapshot", Arguments("By REST", "2026-06-30"));
        var padded = await mcp.CallAsync("create_snapshot", Arguments("  By tool\n", "2026-06-30"));

        await AssertSameProblemAsync(HttpStatusCode.Conflict, restConflict, toolConflict);
        padded.AssertToolError(HttpStatusCode.Conflict);

        // Names are unique within a tenant only, and case-sensitive.
        (await mcpOther.CallAsync("create_snapshot", Arguments("By tool", "2026-03-31"))).AssertSuccess();
        (await mcp.CallAsync("create_snapshot", Arguments("by tool", "2026-03-31"))).AssertSuccess();
        Assert.Equal(3, (await mcp.CallAsync("list_snapshots")).AssertSuccess().GetProperty("snapshots").GetArrayLength());
        Assert.Equal(1, (await mcpOther.CallAsync("list_snapshots")).AssertSuccess().GetProperty("snapshots").GetArrayLength());
    }

    [Fact]
    public async Task Only_the_two_named_arguments_count()
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);

        // Unlike the REST body, argument names are case-sensitive: "Name" and "ASOF" are other arguments, ignored.
        var created = await mcp.CallAsync(
            "create_snapshot", """{"name": "Exact", "asOf": "2026-03-31", "Name": "Other", "ASOF": "2020-01-01", "snapshotId": 5, "extra": [1]}""");
        var wrongCaseOnly = await mcp.CallAsync("create_snapshot", """{"Name": "Other", "AsOf": "2026-03-31"}""");

        var snapshot = created.AssertSuccess();
        Assert.Equal("Exact", snapshot.GetProperty("name").GetString());
        Assert.Equal("2026-03-31", snapshot.GetProperty("asOf").GetString());
        wrongCaseOnly.AssertToolError(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_name_of_200_characters_after_trimming_is_accepted()
    {
        var tenant = await api.NewTenantAsync(_client);
        await using var mcp = await api.ConnectMcpAsync(tenant);

        var created = await mcp.CallAsync("create_snapshot", Arguments(" " + new string('n', 200) + " ", "2026-03-31"));

        Assert.Equal(200, created.AssertSuccess().GetProperty("name").GetString()!.Length);
    }

    [Fact]
    public async Task create_snapshot_waits_for_a_batch_being_applied_and_is_taken_after_it()
    {
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        var applied = await _client.AppliedAtAsync(tenant, Row("50000"));
        await using var mcp = await api.ConnectMcpAsync(tenant);
        await using var mcpOther = await api.ConnectMcpAsync(other);

        // A batch of the tenant being applied (PRP-002 3.4 item 4): its exclusive lock, held until the test commits.
        await using var applying = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        await using var transaction = await applying.BeginTransactionAsync(Ct);
        var tenantText = tenant.ToString("D");
        await applying.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@tenantText, 0))", new { tenantText }, transaction);

        var waiting = mcp.CallAsync("create_snapshot", Arguments("During a batch", "2026-03-31"));

        // Another tenant is not held up, and a refused value is refused without waiting.
        (await mcpOther.CallAsync("create_snapshot", Arguments("During a batch", "2026-03-31")).WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();
        (await mcp.CallAsync("create_snapshot", Arguments("During a batch", "bad")).WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertToolError(HttpStatusCode.BadRequest);

        await Task.Delay(TimeSpan.FromMilliseconds(700), Ct);
        Assert.False(waiting.IsCompleted, "create_snapshot must wait for the batch being applied.");

        var committedAfter = await Instants.DatabaseNowAsync(api.Database);
        await transaction.CommitAsync(Ct);

        var snapshot = (await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct)).AssertSuccess();
        var knownAt = snapshot.GetProperty("knownAt").GetString();
        Assert.True(string.CompareOrdinal(knownAt, committedAfter) > 0, "The snapshot's instant is read after the wait.");
        Assert.True(string.CompareOrdinal(knownAt, applied) > 0);
    }

    private static async Task AssertSameProblemAsync(HttpStatusCode expected, HttpResponseMessage response, ModelContextProtocol.Protocol.CallToolResult result)
    {
        await response.AssertProblemAsync(expected);
        var rest = await response.JsonAsync();
        var problem = result.AssertToolError(expected);
        Assert.Equal(rest.GetProperty("title").GetString(), problem.GetProperty("title").GetString());
        Assert.Equal(rest.GetProperty("type").GetString(), problem.GetProperty("type").GetString());
        Assert.Equal(rest.GetProperty("detail").GetString(), problem.GetProperty("detail").GetString());
    }
}

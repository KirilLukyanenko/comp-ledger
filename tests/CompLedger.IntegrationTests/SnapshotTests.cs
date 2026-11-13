using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompLedger.Infrastructure.Db;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CompLedger.IntegrationTests;

/// <summary>PRP-002 T4: snapshots (3.2 to 3.5, the logging budget of 3.6; A3, A4, A5, A6).</summary>
public sealed partial class SnapshotTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string Snapshots = "/api/v1/snapshots";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Row(string amount, string from = "2026-01-01", string employee = "E-1001") =>
        Header + $"{employee},BASE_SALARY,{from},{amount},EUR\n";

    private static StringContent Json(string body, string? contentType = "application/json")
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.Remove("Content-Type");
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return content;
    }

    private Task<HttpResponseMessage> PostAsync(Guid? tenant, string body, string? contentType = "application/json") =>
        _client.SendAsync(HttpMethod.Post, Snapshots, tenant, Json(body, contentType));

    private async Task<JsonElement> CreateAsync(Guid tenant, string name, string asOf = "2026-03-31")
    {
        using var response = await PostAsync(tenant, JsonSerializer.Serialize(new { name, asOf }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<JsonElement> OkAsync(Guid tenant, string url)
    {
        using var response = await _client.GetAsync(url, tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<List<JsonElement>> ListAsync(Guid tenant, string query = "")
    {
        var body = await OkAsync(tenant, Snapshots + query);
        Assert.Equal(["snapshots"], body.EnumerateObject().Select(p => p.Name));
        return [.. body.GetProperty("snapshots").EnumerateArray()];
    }

    private static string EmployeeUrl(JsonElement snapshot, string employee = "E-1001") =>
        $"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()}/employees/{employee}/compensation";

    /// <summary>A1: 50000 applied at t1, corrected to 51000 at t2.</summary>
    private async Task<(Guid Tenant, string T1, string T2)> TenantWithCorrectionAsync()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, Row("50000"));
        var t2 = await _client.AppliedAtAsync(tenant, Row("51000"));
        return (tenant, t1, t2);
    }

    // ---------- A3, A4, A6 ----------

    [Fact]
    public async Task A3_a_snapshot_does_not_move()
    {
        var (tenant, _, t2) = await TenantWithCorrectionAsync();

        using var created = await PostAsync(tenant, """{"name": "Q1 review", "asOf": "2026-03-31"}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var snapshot = await created.JsonAsync();
        var snapshotId = snapshot.GetProperty("snapshotId").GetString()!;
        Assert.Equal($"{Snapshots}/{snapshotId}", created.Headers.Location?.OriginalString);
        var knownAt = snapshot.GetProperty("knownAt").GetString()!;
        Assert.Matches(SixDigitInstant(), knownAt);
        Assert.True(string.CompareOrdinal(knownAt, t2) >= 0, "The snapshot's knownAt must be at or after the last batch.");

        var before = await OkAsync(tenant, EmployeeUrl(snapshot));
        await _client.AppliedAtAsync(tenant, Row("52000"));
        var after = await OkAsync(tenant, EmployeeUrl(snapshot));

        // The snapshot still shows 51000, with the same body as before; the current view shows 52000.
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], after.GetProperty("components").Periods());
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(["snapshotId", "employeeId", "asOf", "knownAt", "components"], after.EnumerateObject().Select(p => p.Name));
        Assert.Equal(snapshotId, after.GetProperty("snapshotId").GetString());
        Assert.Equal("E-1001", after.GetProperty("employeeId").GetString());
        Assert.Equal("2026-03-31", after.GetProperty("asOf").GetString());
        Assert.Equal(knownAt, after.GetProperty("knownAt").GetString());

        var current = await OkAsync(tenant, "/api/v1/employees/E-1001/compensation?asOf=2026-03-31");
        Assert.Equal([("BASE_SALARY", 52000m, "EUR", "2026-01-01", null)], current.GetProperty("components").Periods());

        // The snapshot read is the as-known-at as-of query for its pair.
        var same = await OkAsync(tenant, $"/api/v1/employees/E-1001/compensation?asOf=2026-03-31&knownAt={knownAt}");
        Assert.Equal(after.GetProperty("components").GetRawText(), same.GetProperty("components").GetRawText());
    }

    [Fact]
    public async Task A_value_back_dated_after_the_snapshot_does_not_split_its_period()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, ApiFactory.A1);
        var snapshot = await CreateAsync(tenant, "Mid-year", "2026-05-15");

        await _client.AppliedAtAsync(
            tenant, Header + "E-1001,BASE_SALARY,2026-04-01,52000,EUR\nE-1001,BASE_SALARY,2026-06-01,53000,EUR\nE-1001,ALLOWANCE,2026-01-01,900,EUR\n");

        // As known at the snapshot: the A1 file only, by component code, with the periods as they were then.
        Assert.Equal(
            [("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            (await OkAsync(tenant, EmployeeUrl(snapshot))).GetProperty("components").Periods());
    }

    [Fact]
    public async Task A4_snapshot_names_are_unique_within_a_tenant_after_trimming()
    {
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        var first = await CreateAsync(tenant, "Q1 review");

        using var again = await PostAsync(tenant, """{"name": "Q1 review", "asOf": "2026-03-31"}""");
        using var padded = await PostAsync(tenant, """{"name": "  Q1 review ", "asOf": "2026-06-30"}""");
        using var paddedWithControls = await PostAsync(tenant, """{"name": "\t\r\nQ1 review\n", "asOf": "2026-06-30"}""");
        using var withoutAsOf = await PostAsync(tenant, """{"name": "Q1 review 2"}""");

        await again.AssertProblemAsync(HttpStatusCode.Conflict);
        await padded.AssertProblemAsync(HttpStatusCode.Conflict);
        await paddedWithControls.AssertProblemAsync(HttpStatusCode.Conflict);
        await withoutAsOf.AssertProblemAsync(HttpStatusCode.BadRequest);
        Assert.Null(again.Headers.Location);

        // Another tenant may use the name, and the first tenant still has exactly its one snapshot.
        var foreign = await CreateAsync(other, "Q1 review");
        Assert.NotEqual(first.GetProperty("snapshotId").GetString(), foreign.GetProperty("snapshotId").GetString());
        Assert.Equal([first.GetRawText()], (await ListAsync(tenant)).Select(s => s.GetRawText()));
    }

    [Fact]
    public async Task Names_are_compared_ordinally_so_case_and_other_whitespace_make_different_names()
    {
        var tenant = await api.NewTenantAsync(_client);

        // A no-break space is not trimmed, so it is part of the name.
        var names = new[] { "Q1 review", "q1 review", "Q1  review", "Q1 review ", "Q1 review" };
        foreach (var name in names)
        {
            Assert.Equal(name, (await CreateAsync(tenant, name)).GetProperty("name").GetString());
        }

        Assert.Equal(names.Order(StringComparer.Ordinal), (await ListAsync(tenant)).Select(s => s.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A6_another_tenant_sees_no_snapshot_of_this_one()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var other = await api.NewTenantAsync(_client);
        var snapshot = await CreateAsync(tenant, "Q1 review");
        var snapshotId = snapshot.GetProperty("snapshotId").GetString();

        using var resource = await _client.GetAsync($"{Snapshots}/{snapshotId}", other);
        using var employee = await _client.GetAsync(EmployeeUrl(snapshot), other);

        await resource.AssertProblemAsync(HttpStatusCode.NotFound);
        await employee.AssertProblemAsync(HttpStatusCode.NotFound);
        Assert.Empty(await ListAsync(other));

        // The answer is the same as for an id that does not exist anywhere (but for the trace id of the request).
        using var unknown = await _client.GetAsync($"{Snapshots}/{Guid.CreateVersion7()}", other);
        static IEnumerable<string> Shown(JsonElement problem) =>
            problem.EnumerateObject().Where(p => p.Name != "traceId").Select(p => $"{p.Name}={p.Value.GetRawText()}");
        Assert.Equal(Shown(await unknown.JsonAsync()), Shown(await resource.JsonAsync()));
        Assert.Equal(unknown.Headers.Select(h => h.Key), resource.Headers.Select(h => h.Key));

        // The owner still reads it, and the other tenant's own snapshots stay its own.
        var foreign = await CreateAsync(other, "Other");
        Assert.Equal([snapshot.GetRawText()], (await ListAsync(tenant)).Select(s => s.GetRawText()));
        Assert.Equal([foreign.GetRawText()], (await ListAsync(other)).Select(s => s.GetRawText()));
        using var foreignRead = await _client.GetAsync($"{Snapshots}/{foreign.GetProperty("snapshotId").GetString()}", tenant);
        await foreignRead.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- creation: 3.2, 3.4 item 5, 3.5 ----------

    [Fact]
    public async Task A_created_snapshot_has_a_v7_id_the_trimmed_name_the_database_clock_and_reads_back_identically()
    {
        var tenant = await api.NewTenantAsync(_client);
        var before = await Instants.DatabaseNowAsync(api.Database);

        using var response = await PostAsync(tenant, """{"asOf": "2026-12-31", "name": " \tFY2026 review\r\n", "knownAt": "2001-01-01T00:00:00Z", "extra": {"a": [1]}}""");

        var after = await Instants.DatabaseNowAsync(api.Database);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var snapshot = await response.JsonAsync();
        Assert.Equal(["snapshotId", "name", "asOf", "knownAt"], snapshot.EnumerateObject().Select(p => p.Name));

        var snapshotId = snapshot.GetProperty("snapshotId").GetString()!;
        Assert.True(Guid.TryParseExact(snapshotId, "D", out var id));
        Assert.Equal(7, id.Version);
        Assert.Equal(snapshotId.ToLowerInvariant(), snapshotId);
        Assert.Equal($"{Snapshots}/{snapshotId}", response.Headers.Location?.OriginalString);
        Assert.Equal("FY2026 review", snapshot.GetProperty("name").GetString());
        Assert.Equal("2026-12-31", snapshot.GetProperty("asOf").GetString());

        // The client does not choose knownAt: the service sets it from the database clock during the request.
        var knownAt = snapshot.GetProperty("knownAt").GetString()!;
        Assert.Matches(SixDigitInstant(), knownAt);
        Assert.True(string.CompareOrdinal(before, knownAt) < 0 && string.CompareOrdinal(knownAt, after) < 0, $"{before} < {knownAt} < {after}");

        using var stored = await _client.GetAsync(response.Headers.Location!.OriginalString, tenant);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(text, await stored.Content.ReadAsStringAsync(Ct));
        Assert.Equal([text], (await ListAsync(tenant)).Select(s => s.GetRawText()));

        // The table holds the two coordinates and nothing else of the snapshot: no copy of any value.
        await using var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, Ct);
        var row = await scope.Connection.QuerySingleAsync<(Guid Id, Guid TenantId, string Name, DateTime AsOf, DateTime KnownAt)>(
            "SELECT id, tenant_id, name, as_of, known_at FROM snapshots", transaction: scope.Transaction);
        Assert.Equal((id, tenant, "FY2026 review", new DateTime(2026, 12, 31)), (row.Id, row.TenantId, row.Name, row.AsOf));
        Assert.Equal(knownAt, Instants.Write(row.KnownAt));
        Assert.Equal(
            ["as_of", "id", "known_at", "name", "tenant_id"],
            await scope.Connection.QueryAsync<string>(
                "SELECT column_name::text FROM information_schema.columns WHERE table_name = 'snapshots' ORDER BY 1", transaction: scope.Transaction));
    }

    [Theory]
    [InlineData("""{"NAME": "Upper", "ASOF": "2026-03-31"}""", "Upper", "2026-03-31")]
    [InlineData("""{"Name": "Mixed", "AsOf": "1900-01-01"}""", "Mixed", "1900-01-01")]
    [InlineData("""{"name": "Last day", "asOf": "2999-12-31"}""", "Last day", "2999-12-31")]
    [InlineData("""{"name": "Leap", "asOf": "2028-02-29"}""", "Leap", "2028-02-29")]
    [InlineData("""{"name": "x", "asOf": "2026-03-31", "snapshotId": "0193f1c2-0000-7000-8000-000000000000", "tenantId": 7}""", "x", "2026-03-31")]
    [InlineData("""{"name": " kept ", "asOf": "2026-03-31"}""", " kept ", "2026-03-31")]
    [InlineData("""{"name": "Ünïcödé 📸 name", "asOf": "2026-03-31"}""", "Ünïcödé 📸 name", "2026-03-31")]
    public async Task Accepted_bodies(string body, string name, string asOf)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await PostAsync(tenant, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var snapshot = await response.JsonAsync();
        Assert.Equal(name, snapshot.GetProperty("name").GetString());
        Assert.Equal(asOf, snapshot.GetProperty("asOf").GetString());
        var stored = Assert.Single(await ListAsync(tenant));
        Assert.Equal(snapshot.GetRawText(), stored.GetRawText());
    }

    [Theory]
    [InlineData(1, HttpStatusCode.Created)]
    [InlineData(200, HttpStatusCode.Created)]
    [InlineData(201, HttpStatusCode.BadRequest)]
    public async Task The_name_is_1_to_200_utf16_code_units_after_trimming(int length, HttpStatusCode expected)
    {
        var tenant = await api.NewTenantAsync(_client);
        var name = new string('n', length);

        using var plain = await PostAsync(tenant, JsonSerializer.Serialize(new { name = "  " + name + "\t\n", asOf = "2026-03-31" }));

        Assert.Equal(expected, plain.StatusCode);
        if (expected == HttpStatusCode.Created)
        {
            Assert.Equal(name, (await plain.JsonAsync()).GetProperty("name").GetString());
        }

        // A character outside the BMP counts as two code units: 100 of them fit, 100 and one more unit do not.
        var emoji = string.Concat(Enumerable.Repeat("📸", 100));
        using var surrogates = await PostAsync(tenant, JsonSerializer.Serialize(new { name = emoji + (length > 200 ? "x" : ""), asOf = "2026-03-31" }));
        Assert.Equal(length > 200 ? HttpStatusCode.BadRequest : HttpStatusCode.Created, surrogates.StatusCode);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"name": "x"}""")]
    [InlineData("""{"asOf": "2026-03-31"}""")]
    [InlineData("""{"name": null, "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": 5, "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": ["x"], "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": {"value": "x"}, "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": true, "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": "", "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": " \t\r\n ", "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": "a\u0000b", "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": "\u0000", "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": "\ud800", "asOf": "2026-03-31"}""")]
    [InlineData("""{"name": "x", "asOf": null}""")]
    [InlineData("""{"name": "x", "asOf": 20260331}""")]
    [InlineData("""{"name": "x", "asOf": ""}""")]
    [InlineData("""{"name": "x", "asOf": " 2026-03-31"}""")]
    [InlineData("""{"name": "x", "asOf": "2026-03-31 "}""")]
    [InlineData("""{"name": "x", "asOf": "2026-03-31\n"}""")]
    [InlineData("""{"name": "x", "asOf": "2026-02-30"}""")]
    [InlineData("""{"name": "x", "asOf": "2026-3-31"}""")]
    [InlineData("""{"name": "x", "asOf": "31.03.2026"}""")]
    [InlineData("""{"name": "x", "asOf": "2026-03-31T00:00:00Z"}""")]
    [InlineData("""{"name": "x", "asOf": "1899-12-31"}""")]
    [InlineData("""{"name": "x", "asOf": "3000-01-01"}""")]
    [InlineData("""{"name": "x", "asOf": "0000-01-01"}""")]
    [InlineData("""{"name": "x", "asOf": "2026-03-31\u0000"}""")]
    [InlineData("""{"name": "x", "asOf": "2026-03-31" """)]
    [InlineData("""{"name": "x", "asOf": "2026-03-31",}""")]
    [InlineData("""{name: "x", asOf: "2026-03-31"}""")]
    [InlineData("""[{"name": "x", "asOf": "2026-03-31"}]""")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("name=x&asOf=2026-03-31")]
    public async Task An_invalid_body_is_400_and_creates_nothing(string body)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await PostAsync(tenant, body);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
        Assert.Null(response.Headers.Location);
        Assert.Empty(await ListAsync(tenant));
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.Created)]
    [InlineData("APPLICATION/JSON", HttpStatusCode.Created)]
    [InlineData("application/json; charset=utf-8", HttpStatusCode.Created)]
    [InlineData("application/json;charset=\"UTF-8\"", HttpStatusCode.Created)]
    [InlineData("application/json; version=1", HttpStatusCode.Created)]
    [InlineData("application/json; charset=iso-8859-1", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json; charset=utf-16", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/jsonx", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/problem+json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/csv", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("garbage", HttpStatusCode.UnsupportedMediaType)]
    [InlineData(null, HttpStatusCode.UnsupportedMediaType)]
    public async Task The_content_type_follows_the_tenant_creation_rules(string? contentType, HttpStatusCode expected)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await PostAsync(tenant, """{"name": "x", "asOf": "2026-03-31"}""", contentType);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.UnsupportedMediaType)
        {
            await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
            Assert.Empty(await ListAsync(tenant));
        }
    }

    [Fact]
    public async Task Creation_checks_run_in_order_tenant_content_type_body_conflict()
    {
        var tenant = await api.NewTenantAsync(_client);
        await CreateAsync(tenant, "Taken");

        // 401 first: whatever the content type and the body are.
        using var noTenant = await PostAsync(null, "{", "text/plain");
        using var unknownTenant = await PostAsync(Guid.CreateVersion7(), """{"name": "Taken", "asOf": "2026-03-31"}""");
        await noTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await unknownTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);

        // 415 before 400 and 409.
        using var wrongTypeBadBody = await PostAsync(tenant, "{", "text/plain");
        using var wrongTypeTakenName = await PostAsync(tenant, """{"name": "Taken", "asOf": "2026-03-31"}""", "text/plain");
        await wrongTypeBadBody.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
        await wrongTypeTakenName.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);

        // 400 before 409: a taken name with an invalid or missing asOf.
        using var takenBadDate = await PostAsync(tenant, """{"name": "Taken", "asOf": "2026-02-30"}""");
        using var takenNoDate = await PostAsync(tenant, """{"name": " Taken "}""");
        await takenBadDate.AssertProblemAsync(HttpStatusCode.BadRequest);
        await takenNoDate.AssertProblemAsync(HttpStatusCode.BadRequest);

        using var taken = await PostAsync(tenant, """{"name": " Taken ", "asOf": "2027-01-01"}""");
        await taken.AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Single(await ListAsync(tenant));
    }

    [Fact]
    public async Task Of_concurrent_creations_with_one_name_exactly_one_succeeds()
    {
        var tenant = await api.NewTenantAsync(_client);

        for (var round = 0; round < 5; round++)
        {
            var body = JsonSerializer.Serialize(new { name = $"Race {round}", asOf = "2026-03-31" });
            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => PostAsync(tenant, body), Ct)));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                await response.AssertProblemAsync(HttpStatusCode.Conflict);
            }

            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(5, (await ListAsync(tenant)).Count);
    }

    [Fact]
    public async Task Two_concurrent_creations_with_one_name_give_one_201_and_one_409()
    {
        var tenant = await api.NewTenantAsync(_client);
        const string Body = """{"name": "Same", "asOf": "2026-03-31"}""";

        var pair = await Task.WhenAll(Task.Run(() => PostAsync(tenant, Body), Ct), Task.Run(() => PostAsync(tenant, Body), Ct));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], pair.Select(r => r.StatusCode).Order());
        Assert.Single(await ListAsync(tenant));
        foreach (var response in pair)
        {
            response.Dispose();
        }
    }

    // ---------- list and resource ----------

    [Fact]
    public async Task The_list_is_empty_without_snapshots_and_ordered_by_known_at_then_id()
    {
        var tenant = await api.NewTenantAsync(_client);
        using var empty = await _client.GetAsync(Snapshots, tenant);
        Assert.Equal("""{"snapshots":[]}""", await empty.Content.ReadAsStringAsync(Ct));

        // Created in an order that differs from the order of their names and dates.
        var created = new List<JsonElement>();
        foreach (var (name, asOf) in new[] { ("zulu", "2026-12-31"), ("alpha", "2026-01-31"), ("mike", "2026-06-30"), ("Bravo", "2025-06-30") })
        {
            created.Add(await CreateAsync(tenant, name, asOf));
        }

        var list = await ListAsync(tenant);

        Assert.Equal(created.Select(s => s.GetRawText()), list.Select(s => s.GetRawText()));
        var keys = list.Select(s => (s.GetProperty("knownAt").GetString()!, s.GetProperty("snapshotId").GetString()!)).ToList();
        Assert.Equal(keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal), keys);
    }

    [Fact]
    public async Task Snapshots_with_one_known_at_are_ordered_by_id()
    {
        // Two snapshots cannot be given one instant through the API, so the rows are written directly.
        var tenant = await api.Database.CreateTenantAsync();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToList();
        await using (var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, Ct))
        {
            foreach (var (id, index) in ids.AsEnumerable().Reverse().Select((id, index) => (id, index)))
            {
                await scope.Connection.ExecuteAsync(
                    "INSERT INTO snapshots (id, tenant_id, name, as_of, known_at) VALUES (@id, @tenant, @name, DATE '2026-03-31', TIMESTAMPTZ '2026-05-01T10:00:00.123456Z')",
                    new { id, tenant, name = $"n{index}" },
                    scope.Transaction);
            }

            await scope.CommitAsync(Ct);
        }

        var list = await ListAsync(tenant);

        Assert.Equal(ids.Select(id => id.ToString("D")).Order(StringComparer.Ordinal), list.Select(s => s.GetProperty("snapshotId").GetString()!));
        Assert.All(list, s => Assert.Equal("2026-05-01T10:00:00.123456Z", s.GetProperty("knownAt").GetString()));
    }

    [Theory]
    [InlineData("?asOf=garbage&knownAt=garbage")]
    [InlineData("?asOf=2026-01-01&asOf=2026-01-01&knownAt=&knownAt=")]
    [InlineData("?knownAt=2999-01-01T00:00:00Z&component=NOPE&name=x&x=%00")]
    public async Task The_list_and_the_resource_ignore_every_query_parameter(string query)
    {
        var tenant = await api.NewTenantAsync(_client);
        var snapshot = await CreateAsync(tenant, "Only");

        var list = await ListAsync(tenant, query);
        var resource = await OkAsync(tenant, $"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()}{query}");

        Assert.Equal([snapshot.GetRawText()], list.Select(s => s.GetRawText()));
        Assert.Equal(snapshot.GetRawText(), resource.GetRawText());
    }

    // ---------- snapshotId ----------

    [Fact]
    public async Task An_uppercase_snapshot_id_is_the_same_snapshot()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var snapshot = await CreateAsync(tenant, "Case");
        var upper = snapshot.GetProperty("snapshotId").GetString()!.ToUpperInvariant();
        Assert.NotEqual(upper, snapshot.GetProperty("snapshotId").GetString());

        var resource = await OkAsync(tenant, $"{Snapshots}/{upper}");
        var employee = await OkAsync(tenant, $"{Snapshots}/{upper}/employees/E-1001/compensation");

        // The answers carry the id as the service writes it.
        Assert.Equal(snapshot.GetRawText(), resource.GetRawText());
        Assert.Equal(snapshot.GetProperty("snapshotId").GetString(), employee.GetProperty("snapshotId").GetString());
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], employee.GetProperty("components").Periods());
    }

    [Theory]
    [InlineData("{0}", "B")] // braces
    [InlineData("{0}", "P")] // parentheses
    [InlineData("{0}", "N")] // 32 digits
    [InlineData("{0}", "X")]
    [InlineData("%20{0}", "D")]
    [InlineData("{0}%20", "D")]
    [InlineData("{0}x", "D")]
    [InlineData("x{0}", "D")]
    [InlineData("not-a-uuid", "D")]
    [InlineData("1", "D")]
    [InlineData("00000000-0000-0000-0000-000000000000", "D")]
    public async Task Any_other_spelling_of_a_snapshot_id_is_404(string template, string format)
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var snapshot = await CreateAsync(tenant, "Spelling");
        var id = Uri.EscapeDataString(snapshot.GetProperty("snapshotId").GetGuid().ToString(format));
        var spelled = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, id);

        using var resource = await _client.GetAsync($"{Snapshots}/{spelled}", tenant);
        using var employee = await _client.GetAsync($"{Snapshots}/{spelled}/employees/E-1001/compensation", tenant);

        await resource.AssertProblemAsync(HttpStatusCode.NotFound);
        await employee.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("0193f1c2000070008000000000000000")]
    [InlineData("0193f1c2-0000-7000-8000-000000000000")]
    public async Task For_a_snapshot_id_the_tenant_and_the_query_are_checked_before_the_lookup(string snapshotId)
    {
        var tenant = await api.NewTenantAsync(_client);

        // 401 before anything else, also for a malformed id: it is a failed lookup, not an unknown route.
        using var noTenant = await _client.GetAsync($"{Snapshots}/{snapshotId}", (Guid?)null);
        using var noTenantEmployee = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1/compensation", (Guid?)null);
        using var unknownTenant = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1/compensation?asOf=2026-01-01", Guid.CreateVersion7());
        await noTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await noTenantEmployee.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await unknownTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);

        // 400 before 404.
        using var withAsOf = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1/compensation?asOf=2026-01-01", tenant);
        using var withKnownAt = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1/compensation?knownAt=2026-01-01T00:00:00Z", tenant);
        await withAsOf.AssertProblemAsync(HttpStatusCode.BadRequest);
        await withKnownAt.AssertProblemAsync(HttpStatusCode.BadRequest);

        using var lookup = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1/compensation", tenant);
        using var resource = await _client.GetAsync($"{Snapshots}/{snapshotId}", tenant);
        await lookup.AssertProblemAsync(HttpStatusCode.NotFound);
        await resource.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("DELETE", "/api/v1/snapshots", "GET", "POST")]
    [InlineData("PUT", "/api/v1/snapshots", "GET", "POST")]
    [InlineData("PATCH", "/api/v1/snapshots", "GET", "POST")]
    [InlineData("DELETE", "/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000", "GET", null)]
    [InlineData("PUT", "/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000", "GET", null)]
    [InlineData("PATCH", "/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000", "GET", null)]
    [InlineData("POST", "/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000", "GET", null)]
    [InlineData("DELETE", "/api/v1/snapshots/not-a-uuid", "GET", null)]
    [InlineData("POST", "/api/v1/snapshots/%7B0193f1c2-0000-7000-8000-000000000000%7D", "GET", null)]
    [InlineData("POST", "/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/employees/E-1001/compensation", "GET", null)]
    [InlineData("DELETE", "/api/v1/snapshots/not-a-uuid/employees/E-1001/compensation", "GET", null)]
    [InlineData("PUT", "/api/v1/snapshots/anything/employees/E-1001/compensation", "GET", null)]
    public async Task A_snapshot_path_with_the_wrong_method_is_405_with_allow_and_no_tenant_check(string method, string url, string allowed, string? alsoAllowed)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var noHeader = await _client.SendAsync(new HttpMethod(method), url, null);
        using var unknownTenant = await _client.SendAsync(new HttpMethod(method), url, Guid.CreateVersion7());
        using var knownTenant = await _client.SendAsync(new HttpMethod(method), url, tenant);

        foreach (var response in new[] { noHeader, unknownTenant, knownTenant })
        {
            await response.AssertProblemAsync(HttpStatusCode.MethodNotAllowed);
            Assert.Contains(allowed, response.Content.Headers.Allow);
            if (alsoAllowed is not null)
            {
                Assert.Contains(alsoAllowed, response.Content.Headers.Allow);
            }
        }
    }

    [Theory]
    [InlineData("/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/employees")]
    [InlineData("/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/employees/E-1001")]
    [InlineData("/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/employees/E-1001/compensation/history")]
    [InlineData("/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/compensation")]
    [InlineData("/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/values")]
    [InlineData("/api/v1/snapshot")]
    public async Task Paths_this_prp_does_not_list_are_unknown_routes(string url)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var noHeader = await _client.GetAsync(url, (Guid?)null);
        using var withTenant = await _client.GetAsync(url, tenant);

        await noHeader.AssertProblemAsync(HttpStatusCode.NotFound);
        await withTenant.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- snapshot reads: 3.4 items 3 and 6 ----------

    [Theory]
    [InlineData("asOf=2026-01-01")]
    [InlineData("asOf=2026-03-31")]
    [InlineData("asOf=")]
    [InlineData("asOf=garbage")]
    [InlineData("asOf=%00")]
    [InlineData("knownAt=2026-01-01T00:00:00Z")]
    [InlineData("knownAt=")]
    [InlineData("knownAt=garbage")]
    [InlineData("asOf=2026-03-31&knownAt=2026-01-01T00:00:00Z")]
    [InlineData("component=BASE_SALARY&asOf=2026-03-31")]
    public async Task A_snapshot_read_with_as_of_or_known_at_is_400(string query)
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var snapshot = await CreateAsync(tenant, "Fixed");

        // Even its own coordinates are refused.
        using var response = await _client.GetAsync(EmployeeUrl(snapshot) + "?" + query, tenant);
        using var own = await _client.GetAsync(EmployeeUrl(snapshot) + $"?knownAt={snapshot.GetProperty("knownAt").GetString()}", tenant);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
        await own.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("?component=NOPE")]
    [InlineData("?component=ALLOWANCE")]
    [InlineData("?component=&component=&x=1&snapshotId=other&employeeId=E-9")]
    public async Task A_snapshot_read_ignores_component_and_every_other_parameter(string query)
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, ApiFactory.A1);
        var snapshot = await CreateAsync(tenant, "All components", "2026-03-15");

        var body = await OkAsync(tenant, EmployeeUrl(snapshot) + query);

        Assert.Equal(
            [("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            body.GetProperty("components").Periods());
    }

    [Fact]
    public async Task Through_a_snapshot_existence_is_as_known_at_its_instant()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var early = await CreateAsync(tenant, "Before any date", "2025-12-31");
        var snapshot = await CreateAsync(tenant, "Q1");
        await _client.AppliedAtAsync(tenant, Row("60000", employee: "E-2002"));
        var later = await CreateAsync(tenant, "Later");

        // E-2002 was uploaded after the first two snapshots: 404 through them, 200 through the later one.
        using var unknownThen = await _client.GetAsync(EmployeeUrl(snapshot, "E-2002"), tenant);
        using var unknownEarly = await _client.GetAsync(EmployeeUrl(early, "E-2002"), tenant);
        using var never = await _client.GetAsync(EmployeeUrl(later, "E-NONE"), tenant);
        await unknownThen.AssertProblemAsync(HttpStatusCode.NotFound);
        await unknownEarly.AssertProblemAsync(HttpStatusCode.NotFound);
        await never.AssertProblemAsync(HttpStatusCode.NotFound);
        Assert.Equal(
            [("BASE_SALARY", 60000m, "EUR", "2026-01-01", null)],
            (await OkAsync(tenant, EmployeeUrl(later, "E-2002"))).GetProperty("components").Periods());

        // E-1001 had values known at the early snapshot, but none in effect on its date: 200 and an empty list.
        var empty = await OkAsync(tenant, EmployeeUrl(early));
        Assert.Equal(0, empty.GetProperty("components").GetArrayLength());
        Assert.Equal("2025-12-31", empty.GetProperty("asOf").GetString());
        Assert.Equal(early.GetProperty("knownAt").GetString(), empty.GetProperty("knownAt").GetString());
    }

    [Fact]
    public async Task A_snapshot_of_an_empty_tenant_is_created_and_stays_empty()
    {
        var tenant = await api.NewTenantAsync(_client);
        var snapshot = await CreateAsync(tenant, "Nothing yet");

        await _client.AppliedAtAsync(tenant, Row("50000"));

        using var response = await _client.GetAsync(EmployeeUrl(snapshot), tenant);
        await response.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_rejected_batch_changes_no_snapshot_read()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var snapshot = await CreateAsync(tenant, "Before the rejection");
        var before = await OkAsync(tenant, EmployeeUrl(snapshot));

        using var rejected = await _client.UploadAsync(tenant, Row("99999") + "E-1001,BASE_SALARY,2026-02-01,x,EUR\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var afterwards = await CreateAsync(tenant, "After the rejection");

        Assert.Equal(before.GetRawText(), (await OkAsync(tenant, EmployeeUrl(snapshot))).GetRawText());
        Assert.Equal(
            before.GetProperty("components").GetRawText(), (await OkAsync(tenant, EmployeeUrl(afterwards))).GetProperty("components").GetRawText());
    }

    // ---------- 3.4 item 4: waiting for a batch being applied ----------

    [Fact]
    public async Task Snapshot_creation_and_snapshot_reads_wait_for_a_batch_being_applied()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var other = await api.NewTenantAsync(_client);
        var existing = await CreateAsync(tenant, "Before the batch");

        // A batch being applied, as PRP-001 7.2 does it: the tenant's exclusive lock, the apply time, the writes.
        await using var applying = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        await using var transaction = await applying.BeginTransactionAsync(Ct);
        var tenantText = tenant.ToString("D");
        await applying.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantText, true)", new { tenantText }, transaction);
        await applying.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@tenantText, 0))", new { tenantText }, transaction);
        var appliedAt = await applying.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()", transaction: transaction);
        var batch = Guid.CreateVersion7();
        await applying.ExecuteAsync(
            """
            INSERT INTO ingestion_batches (id, tenant_id, status, received_at, completed_at, applied_at, total_rows, inserted_rows, corrected_rows, unchanged_rows, error_count)
            VALUES (@batch, @tenant, 'Applied', @appliedAt, @appliedAt, @appliedAt, 1, 0, 1, 0, 0);
            UPDATE compensation_values SET superseded_at = @appliedAt, superseded_by_batch_id = @batch WHERE superseded_at IS NULL;
            INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at)
            VALUES (@tenant, 'E-1001', 'BASE_SALARY', DATE '2026-01-01', 52000, 'EUR', @batch, @appliedAt);
            """,
            new { batch, tenant, appliedAt },
            transaction);

        var creation = PostAsync(tenant, """{"name": "During the batch", "asOf": "2026-03-31"}""");
        var read = _client.GetAsync(EmployeeUrl(existing), tenant);

        // Another tenant creates a snapshot meanwhile; the list and the resource of this tenant take no lock.
        using (var elsewhere = await PostAsync(other, """{"name": "During the batch", "asOf": "2026-03-31"}""").WaitAsync(TimeSpan.FromSeconds(20), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
        }

        Assert.Single(await ListAsync(tenant).WaitAsync(TimeSpan.FromSeconds(20), Ct));
        await OkAsync(tenant, $"{Snapshots}/{existing.GetProperty("snapshotId").GetString()}").WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);
        Assert.False(creation.IsCompleted, "Snapshot creation must wait for the batch being applied.");
        Assert.False(read.IsCompleted, "A snapshot read must wait for the batch being applied.");

        await transaction.CommitAsync(Ct);

        // The new snapshot's instant is after the batch's apply time, so it shows the batch; the old one does not.
        using var created = await creation.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var readResponse = await read.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var snapshot = await created.JsonAsync();
        Assert.True(string.CompareOrdinal(Instants.Write(appliedAt), snapshot.GetProperty("knownAt").GetString()) < 0);
        Assert.Equal([("BASE_SALARY", 52000m, "EUR", "2026-01-01", null)], (await OkAsync(tenant, EmployeeUrl(snapshot))).GetProperty("components").Periods());
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], (await readResponse.JsonAsync()).GetProperty("components").Periods());
    }

    // ---------- logging: 3.6 ----------

    private static bool IsFramework(CapturedLogs.Entry entry) =>
        entry.Category.StartsWith("Microsoft.", StringComparison.Ordinal) || entry.Category.StartsWith("System.", StringComparison.Ordinal);

    private List<CapturedLogs.Entry> ApplicationEntries(Guid tenant) =>
        [.. api.Logs.Entries.Where(e => !IsFramework(e) && e.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D"))))];

    [Fact]
    public async Task Each_created_snapshot_writes_exactly_one_log_entry_with_ids_and_coordinates_only()
    {
        var tenant = await api.Database.CreateTenantAsync();
        var first = await CreateAsync(tenant, "Zebra-crossing secret name", "2026-12-31");
        var second = await CreateAsync(tenant, "  Quokka second name  ", "2025-06-30");

        var entries = ApplicationEntries(tenant);

        Assert.Equal(2, entries.Count);
        foreach (var snapshot in new[] { first, second })
        {
            var snapshotId = snapshot.GetProperty("snapshotId").GetString()!;
            var entry = Assert.Single(entries, e => e.State.Any(p => p.Key == "SnapshotId" && Equals(p.Value, snapshotId)));
            var state = entry.State.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value);

            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Equal(["AsOf", "DurationMs", "KnownAt", "SnapshotId", "TenantId"], state.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(tenant.ToString("D"), Assert.IsType<string>(state["TenantId"]));
            Assert.Equal(snapshotId, Assert.IsType<string>(state["SnapshotId"]));
            Assert.Equal(snapshot.GetProperty("asOf").GetString(), Assert.IsType<string>(state["AsOf"]));
            Assert.Equal(snapshot.GetProperty("knownAt").GetString(), Assert.IsType<string>(state["KnownAt"]));
            Assert.InRange(Assert.IsType<long>(state["DurationMs"]), 0L, 60_000L);
        }

        // No snapshot name in anything the application logged.
        var everything = string.Join('\n', api.Logs.Entries.Where(e => !IsFramework(e)).Select(e => e.Message + " " + string.Join(' ', e.State.Select(p => $"{p.Key}={p.Value}"))));
        Assert.DoesNotContain("Zebra", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Quokka", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_creations_and_reads_write_no_log_entry_of_the_application()
    {
        var tenant = await api.Database.CreateTenantAsync();
        using var upload = await _client.UploadAsync(tenant, Row("54321.1234", employee: "E-5005"));
        var snapshot = await CreateAsync(tenant, "Logged once");
        Assert.Equal(2, ApplicationEntries(tenant).Count); // the batch and the snapshot

        using var badBody = await PostAsync(tenant, """{"name": "Refused-400"}""");
        using var conflict = await PostAsync(tenant, """{"name": "Logged once", "asOf": "2026-03-31"}""");
        using var wrongType = await PostAsync(tenant, """{"name": "Refused-415", "asOf": "2026-03-31"}""", "text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);

        await ListAsync(tenant);
        await OkAsync(tenant, $"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()}");
        await OkAsync(tenant, EmployeeUrl(snapshot, "E-5005"));
        using var missing = await _client.GetAsync(EmployeeUrl(snapshot, "E-NONE"), tenant);
        using var refusedRead = await _client.GetAsync(EmployeeUrl(snapshot, "E-5005") + "?asOf=2026-01-01", tenant);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedRead.StatusCode);

        Assert.Equal(2, ApplicationEntries(tenant).Count);

        // Other tests of this class log their own entries meanwhile, but none of them carries these values.
        var everything = string.Join('\n', api.Logs.Entries.Where(e => !IsFramework(e)).Select(e => e.Message + " " + string.Join(' ', e.State.Select(p => $"{p.Key}={p.Value}"))));
        foreach (var secret in new[] { "Refused-400", "Refused-415", "Logged once", "E-5005", "54321.1234" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    // ---------- the table: 3.4 items 7 and 9 ----------

    [Fact]
    public async Task Row_level_security_is_enabled_and_forced_on_snapshots_and_the_table_is_not_owned_by_the_app()
    {
        await using var connection = await api.Database.AppDataSource.OpenConnectionAsync(Ct);

        var table = await connection.QuerySingleAsync<(bool Enabled, bool Forced)>(
            "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'snapshots'");
        var owner = await connection.QuerySingleAsync<string>("SELECT tableowner::text FROM pg_tables WHERE tablename = 'snapshots'");
        var policies = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_policies WHERE tablename = 'snapshots'");
        var index = await connection.QuerySingleAsync<string>("SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_snapshots_tenant_name'");

        Assert.True(table.Enabled && table.Forced);
        Assert.NotEqual(PostgresFixture.AppLogin, owner);
        Assert.NotEqual("comp_app", owner);
        Assert.Equal(1, policies);
        Assert.Contains("UNIQUE", index, StringComparison.Ordinal);
        Assert.Contains("(tenant_id, name)", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_hides_snapshots_without_a_tenant_and_from_other_tenants()
    {
        var tenantA = await api.Database.CreateTenantAsync("A");
        var tenantB = await api.Database.CreateTenantAsync("B");
        var snapshotA = (await CreateAsync(tenantA, "A's")).GetProperty("snapshotId").GetGuid();
        var snapshotB = (await CreateAsync(tenantB, "B's")).GetProperty("snapshotId").GetGuid();

        // No tenant filter in the SQL: only row-level security stands between the tenants.
        await using (var connection = await api.Database.AppDataSource.OpenConnectionAsync(Ct))
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM snapshots"));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                "INSERT INTO snapshots (id, tenant_id, name, as_of, known_at) VALUES (@id, @tenantA, 'x', DATE '2026-01-01', now())",
                new { id = Guid.CreateVersion7(), tenantA }));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenantA, Ct))
        {
            Assert.Equal([snapshotA], await scope.Connection.QueryAsync<Guid>("SELECT id FROM snapshots", transaction: scope.Transaction));
            Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM snapshots WHERE id = @snapshotB", new { snapshotB }, scope.Transaction));
        }

        await using (var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenantA, Ct))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => scope.Connection.ExecuteAsync(
                "INSERT INTO snapshots (id, tenant_id, name, as_of, known_at) VALUES (@id, @tenantB, 'x', DATE '2026-01-01', now())",
                new { id = Guid.CreateVersion7(), tenantB },
                scope.Transaction));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE snapshots SET name = 'renamed'")]
    [InlineData("UPDATE snapshots SET known_at = now()")]
    [InlineData("DELETE FROM snapshots")]
    [InlineData("TRUNCATE snapshots")]
    public async Task Snapshots_are_immutable_for_the_app_role(string sql)
    {
        var tenant = await api.Database.CreateTenantAsync();
        var snapshot = await CreateAsync(tenant, "Immutable");

        await using (var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, Ct))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => scope.Connection.ExecuteAsync(sql, transaction: scope.Transaction));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.Equal([snapshot.GetRawText()], (await ListAsync(tenant)).Select(s => s.GetRawText()));
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigitInstant();
}

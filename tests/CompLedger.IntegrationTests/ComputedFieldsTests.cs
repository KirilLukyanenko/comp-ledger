using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CompLedger.IntegrationTests;

/// <summary>PRP-003 T2: computed fields on the as-of and snapshot reads (3.2 to 3.6; A1 to A8).</summary>
public sealed class ComputedFieldsTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string Snapshots = "/api/v1/snapshots";

    private static readonly string[] EntryProperties = ["amount", "currency", "effectiveFrom", "effectiveTo", "field", "reason"];

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Employee(string employee = "E-1001") => $"/api/v1/employees/{employee}/compensation";

    private const string A1 = Header
        + "E-1001,BASE_SALARY,2026-01-01,60000,EUR\n"
        + "E-1001,ALLOWANCE,2026-01-01,6000,EUR\n"
        + "E-1001,BONUS_TARGET_PCT,2026-01-01,10,\n";

    private const string A2Inputs = Header
        + "E-2001,BASE_SALARY,2026-04-01,60000,EUR\n"
        + "E-2001,BONUS_TARGET_PCT,2026-01-01,10,\n";

    private const string A2Allowance = Header + "E-2001,ALLOWANCE,2026-07-01,3000,EUR\n";

    private async Task<JsonElement> OkAsync(Guid tenant, string url)
    {
        using var response = await _client.GetAsync(url, tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<Guid> TenantWithAsync(params string[] csvs)
    {
        var tenant = await api.NewTenantAsync(_client);
        foreach (var csv in csvs)
        {
            await _client.AppliedAtAsync(tenant, csv);
        }

        return tenant;
    }

    private async Task<JsonElement> CreateSnapshotAsync(Guid tenant, string name, string asOf)
    {
        using var response = await _client.SendAsync(
            HttpMethod.Post, Snapshots, tenant, new StringContent(JsonSerializer.Serialize(new { name, asOf }), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string SnapshotEmployee(JsonElement snapshot, string employee = "E-1001") =>
        $"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()}/employees/{employee}/compensation";

    private sealed record Field(string Code, decimal? Amount, string? Currency, string? From, string? To, string? Reason);

    /// <summary>The <c>computed</c> array, after checking its shape: three entries in catalogue order, six properties each.</summary>
    private static List<Field> Computed(JsonElement body)
    {
        var entries = body.GetProperty("computed").EnumerateArray().ToList();
        Assert.Equal(["TARGET_BONUS", "TOTAL_FIXED_PAY", "TOTAL_TARGET_CASH"], entries.Select(e => e.GetProperty("field").GetString()));
        foreach (var entry in entries)
        {
            Assert.Equal(EntryProperties, entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

            // reason is null exactly when amount is not.
            Assert.NotEqual(entry.GetProperty("amount").ValueKind == JsonValueKind.Null, entry.GetProperty("reason").ValueKind == JsonValueKind.Null);
        }

        return entries.ConvertAll(e => new Field(
            e.GetProperty("field").GetString()!,
            e.GetProperty("amount").ValueKind == JsonValueKind.Null ? null : e.GetProperty("amount").GetDecimal(),
            e.GetProperty("currency").GetString(),
            e.GetProperty("effectiveFrom").GetString(),
            e.GetProperty("effectiveTo").GetString(),
            e.GetProperty("reason").GetString()));
    }

    private async Task<List<Field>> ComputedAsync(Guid tenant, string asOf, string employee = "E-1001", string? knownAt = null) =>
        Computed(await OkAsync(tenant, $"{Employee(employee)}?asOf={asOf}&include=computed" + (knownAt is null ? "" : $"&knownAt={knownAt}")));

    private static Field Ok(string code, decimal amount, string currency, string from, string? to) => new(code, amount, currency, from, to, null);

    private static Field Not(string code, string reason) => new(code, null, null, null, null, reason);

    // ---------- A1 to A5 ----------

    [Fact]
    public async Task A1_everything_in_effect()
    {
        var tenant = await TenantWithAsync(A1);

        Assert.Equal(
            [
                Ok("TARGET_BONUS", 6000m, "EUR", "2026-01-01", null),
                Ok("TOTAL_FIXED_PAY", 66000m, "EUR", "2026-01-01", null),
                Ok("TOTAL_TARGET_CASH", 72000m, "EUR", "2026-01-01", null),
            ],
            await ComputedAsync(tenant, "2026-05-15"));
    }

    [Fact]
    public async Task A2_no_allowance_yet_and_a_later_one_ends_the_totals()
    {
        var tenant = await TenantWithAsync(A2Inputs + A2Allowance[Header.Length..]);

        var body = await OkAsync(tenant, Employee("E-2001") + "?asOf=2026-05-15&include=computed");
        Assert.Equal("E-2001", body.GetProperty("employeeId").GetString());
        Assert.Equal("2026-05-15", body.GetProperty("asOf").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("knownAt").ValueKind);
        Assert.Equal(["asOf", "components", "computed", "employeeId", "knownAt"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            [("BASE_SALARY", 60000m, "EUR", "2026-04-01", null), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            body.GetProperty("components").Periods());
        Assert.Equal(
            [
                Ok("TARGET_BONUS", 6000m, "EUR", "2026-04-01", null),
                Ok("TOTAL_FIXED_PAY", 60000m, "EUR", "2026-04-01", "2026-06-30"),
                Ok("TOTAL_TARGET_CASH", 66000m, "EUR", "2026-04-01", "2026-06-30"),
            ],
            Computed(body));

        Assert.Equal(
            [
                Ok("TARGET_BONUS", 6000m, "EUR", "2026-04-01", null),
                Ok("TOTAL_FIXED_PAY", 63000m, "EUR", "2026-07-01", null),
                Ok("TOTAL_TARGET_CASH", 69000m, "EUR", "2026-07-01", null),
            ],
            await ComputedAsync(tenant, "2026-07-01", "E-2001"));
    }

    [Fact]
    public async Task A3_a_raise_splits_the_period()
    {
        var tenant = await TenantWithAsync(A1, Header + "E-1001,BASE_SALARY,2026-04-01,64000,EUR\n");

        Assert.Equal(Ok("TARGET_BONUS", 6000m, "EUR", "2026-01-01", "2026-03-31"), (await ComputedAsync(tenant, "2026-03-31"))[0]);
        Assert.Equal(Ok("TARGET_BONUS", 6400m, "EUR", "2026-04-01", null), (await ComputedAsync(tenant, "2026-04-01"))[0]);
    }

    [Fact]
    public async Task A4_the_bonus_rounds_half_away_from_zero()
    {
        var tenant = await TenantWithAsync(Header + "E-3001,BASE_SALARY,2026-01-01,1000.0001,EUR\nE-3001,BONUS_TARGET_PCT,2026-01-01,50,\n");

        var fields = await ComputedAsync(tenant, "2026-05-15", "E-3001");
        Assert.Equal([500.0001m, 1000.0001m, 1500.0002m], fields.Select(f => f.Amount));

        // The scale is not part of the contract, but the value must be exact: never a binary floating-point number.
        var body = await OkAsync(tenant, Employee("E-3001") + "?asOf=2026-05-15&include=computed");
        Assert.Equal(500.0001m, body.GetProperty("computed")[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task A5_missing_and_mismatched_inputs()
    {
        var tenant = await TenantWithAsync(
            Header
            + "E-4001,BASE_SALARY,2026-01-01,50000,EUR\n"
            + "E-4002,BASE_SALARY,2026-01-01,50000,EUR\n"
            + "E-4002,ALLOWANCE,2026-01-01,5000,USD\n"
            + "E-4002,BONUS_TARGET_PCT,2026-01-01,10,\n"
            + "E-4003,ALLOWANCE,2026-01-01,5000,EUR\n"
            + "E-4004,BASE_SALARY,2026-01-01,50000,EUR\n"
            + "E-4004,ALLOWANCE,2026-01-01,0,USD\n");

        Assert.Equal(
            [Not("TARGET_BONUS", "MISSING_INPUT"), Ok("TOTAL_FIXED_PAY", 50000m, "EUR", "2026-01-01", null), Not("TOTAL_TARGET_CASH", "MISSING_INPUT")],
            await ComputedAsync(tenant, "2026-05-15", "E-4001"));
        Assert.Equal(
            [Ok("TARGET_BONUS", 5000m, "EUR", "2026-01-01", null), Not("TOTAL_FIXED_PAY", "CURRENCY_MISMATCH"), Not("TOTAL_TARGET_CASH", "CURRENCY_MISMATCH")],
            await ComputedAsync(tenant, "2026-05-15", "E-4002"));
        Assert.All(await ComputedAsync(tenant, "2026-05-15", "E-4003"), f => Assert.Equal("MISSING_INPUT", f.Reason));

        // An ALLOWANCE of 0 in another currency is still a mismatch; MISSING_INPUT comes first for the cash.
        Assert.Equal(
            [Not("TARGET_BONUS", "MISSING_INPUT"), Not("TOTAL_FIXED_PAY", "CURRENCY_MISMATCH"), Not("TOTAL_TARGET_CASH", "MISSING_INPUT")],
            await ComputedAsync(tenant, "2026-05-15", "E-4004"));

        // Values, but none in effect: 200, empty components, three MISSING_INPUT.
        var before = await OkAsync(tenant, Employee("E-4001") + "?asOf=2025-12-31&include=computed");
        Assert.Empty(before.GetProperty("components").EnumerateArray());
        Assert.All(Computed(before), f => Assert.Equal(Not(f.Code, "MISSING_INPUT"), f));
    }

    [Fact]
    public async Task Zero_percent_is_an_ordinary_input()
    {
        var tenant = await TenantWithAsync(Header + "E-5001,BASE_SALARY,2026-01-01,40000,EUR\nE-5001,BONUS_TARGET_PCT,2026-01-01,0,\n");

        Assert.Equal(
            [
                Ok("TARGET_BONUS", 0m, "EUR", "2026-01-01", null),
                Ok("TOTAL_FIXED_PAY", 40000m, "EUR", "2026-01-01", null),
                Ok("TOTAL_TARGET_CASH", 40000m, "EUR", "2026-01-01", null),
            ],
            await ComputedAsync(tenant, "2026-05-15", "E-5001"));
    }

    [Fact]
    public async Task An_allowance_before_the_date_is_in_effect_and_an_earlier_one_does_not_matter()
    {
        var tenant = await TenantWithAsync(
            Header
            + "E-6001,BASE_SALARY,2026-01-01,50000,EUR\n"
            + "E-6001,BONUS_TARGET_PCT,2026-01-01,10,\n"
            + "E-6001,ALLOWANCE,2026-02-01,1000,EUR\n"
            + "E-6001,ALLOWANCE,2026-03-01,2000,EUR\n"
            + "E-6001,ALLOWANCE,2026-08-01,3000,EUR\n"
            + "E-6001,ALLOWANCE,2026-10-01,4000,EUR\n");

        // On 2026-05-15 the 2026-03-01 allowance is in effect until 2026-07-31.
        Assert.Equal(
            [
                Ok("TARGET_BONUS", 5000m, "EUR", "2026-01-01", null),
                Ok("TOTAL_FIXED_PAY", 52000m, "EUR", "2026-03-01", "2026-07-31"),
                Ok("TOTAL_TARGET_CASH", 57000m, "EUR", "2026-03-01", "2026-07-31"),
            ],
            await ComputedAsync(tenant, "2026-05-15", "E-6001"));

        // Before the first allowance, the earliest later one ends the totals.
        Assert.Equal(
            Ok("TOTAL_FIXED_PAY", 50000m, "EUR", "2026-01-01", "2026-01-31"),
            (await ComputedAsync(tenant, "2026-01-15", "E-6001"))[1]);
    }

    [Fact]
    public async Task Another_employees_later_allowance_does_not_end_the_period()
    {
        var tenant = await TenantWithAsync(A2Inputs + "E-2002,ALLOWANCE,2026-07-01,3000,EUR\n");

        Assert.Equal(Ok("TOTAL_FIXED_PAY", 60000m, "EUR", "2026-04-01", null), (await ComputedAsync(tenant, "2026-05-15", "E-2001"))[1]);
    }

    // ---------- A6: known at and snapshots ----------

    [Fact]
    public async Task A6_known_at_and_snapshots_use_their_own_values()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, A1);
        var snapshot = await CreateSnapshotAsync(tenant, "Review", "2026-05-15");
        await _client.AppliedAtAsync(tenant, Header + "E-1001,BONUS_TARGET_PCT,2026-01-01,15,\n");

        var snapshotBody = await OkAsync(tenant, SnapshotEmployee(snapshot) + "?include=computed");
        Assert.Equal(6000m, Computed(snapshotBody)[0].Amount);
        Assert.Equal(snapshot.GetProperty("snapshotId").GetString(), snapshotBody.GetProperty("snapshotId").GetString());
        Assert.Equal(snapshot.GetProperty("knownAt").GetString(), snapshotBody.GetProperty("knownAt").GetString());

        Assert.Equal(9000m, (await ComputedAsync(tenant, "2026-05-15"))[0].Amount);
        Assert.Equal(6000m, (await ComputedAsync(tenant, "2026-05-15", knownAt: t1))[0].Amount);
    }

    [Fact]
    public async Task A6_an_allowance_recorded_after_known_at_does_not_end_the_period()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, A2Inputs);
        var snapshot = await CreateSnapshotAsync(tenant, "Before the allowance", "2026-05-15");
        var t2 = await _client.AppliedAtAsync(tenant, A2Allowance);

        var atT1 = await ComputedAsync(tenant, "2026-05-15", "E-2001", t1);
        Assert.Equal([null, null], atT1.Skip(1).Select(f => f.To));
        var atT2 = await ComputedAsync(tenant, "2026-05-15", "E-2001", t2);
        Assert.Equal(["2026-06-30", "2026-06-30"], atT2.Skip(1).Select(f => f.To));
        Assert.Equal(["2026-06-30", "2026-06-30"], (await ComputedAsync(tenant, "2026-05-15", "E-2001")).Skip(1).Select(f => f.To));

        // The snapshot was taken before the allowance was recorded.
        var fromSnapshot = Computed(await OkAsync(tenant, SnapshotEmployee(snapshot, "E-2001") + "?include=computed"));
        Assert.Equal(atT1, fromSnapshot);
    }

    [Fact]
    public async Task A_superseded_later_allowance_does_not_end_a_current_period()
    {
        // The later allowance is corrected in place by key; current reads use only the current value.
        var tenant = await TenantWithAsync(A2Inputs + A2Allowance[Header.Length..], Header + "E-2001,ALLOWANCE,2026-07-01,3500,EUR\n");

        Assert.Equal(Ok("TOTAL_FIXED_PAY", 60000m, "EUR", "2026-04-01", "2026-06-30"), (await ComputedAsync(tenant, "2026-05-15", "E-2001"))[1]);
        Assert.Equal(Ok("TOTAL_FIXED_PAY", 63500m, "EUR", "2026-07-01", null), (await ComputedAsync(tenant, "2026-07-01", "E-2001"))[1]);
    }

    // ---------- A7: the parameter ----------

    public static TheoryData<string> InvalidIncludes => new()
    {
        "include=Computed",
        "include=COMPUTED",
        "include=",
        "include",
        "include=computed,other",
        "include=computed%2Cother",
        "include=%20computed",
        "include=computed%20",
        "include=+computed",
        "include=comp%20uted",
        "include=x",
        "include=computed&include=computed",
        "include=computed&include=other",
        "include=&include=computed",
    };

    [Theory]
    [MemberData(nameof(InvalidIncludes))]
    public async Task An_invalid_include_is_400_on_the_as_of_read(string query)
    {
        var tenant = await TenantWithAsync(A1);

        using var response = await _client.GetAsync($"{Employee()}?asOf=2026-05-15&{query}", tenant);
        await response.AssertProblemAsync(HttpStatusCode.BadRequest);

        using var withKnownAt = await _client.GetAsync($"{Employee()}?knownAt={await Instants.DatabaseNowAsync(api.Database)}&{query}", tenant);
        await withKnownAt.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(InvalidIncludes))]
    public async Task An_invalid_include_is_400_on_the_snapshot_read(string query)
    {
        var tenant = await TenantWithAsync(A1);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        using var response = await _client.GetAsync($"{SnapshotEmployee(snapshot)}?{query}", tenant);
        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(InvalidIncludes))]
    public async Task An_invalid_include_is_400_before_the_lookup(string query)
    {
        var tenant = await TenantWithAsync(A1);

        using var unknownEmployee = await _client.GetAsync($"{Employee("E-9999")}?{query}", tenant);
        await unknownEmployee.AssertProblemAsync(HttpStatusCode.BadRequest);

        foreach (var snapshotId in new[] { Guid.CreateVersion7().ToString("D"), "not-a-uuid" })
        {
            using var unknownSnapshot = await _client.GetAsync($"{Snapshots}/{snapshotId}/employees/E-1001/compensation?{query}", tenant);
            await unknownSnapshot.AssertProblemAsync(HttpStatusCode.BadRequest);
        }

        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");
        using var unknownInSnapshot = await _client.GetAsync($"{SnapshotEmployee(snapshot, "E-9999")}?{query}", tenant);
        await unknownInSnapshot.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Include_is_matched_after_url_decoding()
    {
        var tenant = await TenantWithAsync(A1);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        Assert.Equal(72000m, Computed(await OkAsync(tenant, $"{Employee()}?asOf=2026-05-15&include=%63omputed"))[2].Amount);
        Assert.Equal(72000m, Computed(await OkAsync(tenant, $"{SnapshotEmployee(snapshot)}?include=%63%6Fmputed"))[2].Amount);
    }

    [Fact]
    public async Task Without_include_neither_read_has_a_computed_property()
    {
        var tenant = await TenantWithAsync(A1);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        var asOf = await OkAsync(tenant, $"{Employee()}?asOf=2026-05-15");
        Assert.Equal(["asOf", "components", "employeeId", "knownAt"], asOf.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, asOf.GetProperty("knownAt").ValueKind);

        var known = await OkAsync(tenant, $"{Employee()}?asOf=2026-05-15&knownAt={await Instants.DatabaseNowAsync(api.Database)}");
        Assert.False(known.TryGetProperty("computed", out _));

        var fromSnapshot = await OkAsync(tenant, SnapshotEmployee(snapshot));
        Assert.Equal(
            ["asOf", "components", "employeeId", "knownAt", "snapshotId"], fromSnapshot.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // With include the rest of the response is the same.
        var withComputed = await OkAsync(tenant, $"{Employee()}?asOf=2026-05-15&include=computed");
        Assert.Equal(asOf.GetProperty("components").Periods(), withComputed.GetProperty("components").Periods());
        var snapshotWithComputed = await OkAsync(tenant, SnapshotEmployee(snapshot) + "?include=computed");
        foreach (var name in new[] { "snapshotId", "employeeId", "asOf", "knownAt" })
        {
            Assert.Equal(fromSnapshot.GetProperty(name).GetString(), snapshotWithComputed.GetProperty(name).GetString());
        }
    }

    [Theory]
    [InlineData("include=computed")]
    [InlineData("include=anything")]
    [InlineData("include=")]
    [InlineData("include=computed&include=computed")]
    public async Task The_history_read_ignores_include(string query)
    {
        var tenant = await TenantWithAsync(A1);

        var without = await OkAsync(tenant, $"{Employee()}/history");
        var with = await OkAsync(tenant, $"{Employee()}/history?{query}");
        Assert.True(JsonElement.DeepEquals(without, with));
        Assert.False(with.TryGetProperty("computed", out _));
    }

    [Fact]
    public async Task An_employee_with_no_values_is_404_with_include()
    {
        var tenant = await TenantWithAsync(A1);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        using var asOf = await _client.GetAsync($"{Employee("E-9999")}?include=computed", tenant);
        await asOf.AssertProblemAsync(HttpStatusCode.NotFound);

        using var fromSnapshot = await _client.GetAsync($"{SnapshotEmployee(snapshot, "E-9999")}?include=computed", tenant);
        await fromSnapshot.AssertProblemAsync(HttpStatusCode.NotFound);

        // Values recorded only after knownAt: none known then, so 404.
        var before = await Instants.DatabaseNowAsync(api.Database);
        await _client.AppliedAtAsync(tenant, Header + "E-7001,BASE_SALARY,2026-01-01,1,EUR\n");
        using var notYetKnown = await _client.GetAsync($"{Employee("E-7001")}?include=computed&knownAt={before}", tenant);
        await notYetKnown.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("asOf=2026-05-15&include=computed")]
    [InlineData("knownAt=2026-01-01T00:00:00Z&include=computed")]
    public async Task The_snapshot_read_still_refuses_as_of_and_known_at_with_include(string query)
    {
        var tenant = await TenantWithAsync(A1);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        using var response = await _client.GetAsync($"{SnapshotEmployee(snapshot)}?{query}", tenant);
        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Other_query_checks_still_come_first_or_together()
    {
        var tenant = await TenantWithAsync(A1);

        using var badAsOf = await _client.GetAsync($"{Employee()}?asOf=2026-13-01&include=computed", tenant);
        await badAsOf.AssertProblemAsync(HttpStatusCode.BadRequest);

        using var noTenant = await _client.GetAsync($"{Employee()}?include=bad", tenant: null);
        Assert.Equal(HttpStatusCode.Unauthorized, noTenant.StatusCode);
    }

    // ---------- 3.5 item 9: tenant isolation ----------

    [Fact]
    public async Task Another_tenant_gets_404_and_never_these_inputs()
    {
        var tenant = await TenantWithAsync(A1);
        var other = await TenantWithAsync(Header + "E-1001,BASE_SALARY,2026-01-01,10,USD\n");
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        Assert.Equal(
            [Not("TARGET_BONUS", "MISSING_INPUT"), Ok("TOTAL_FIXED_PAY", 10m, "USD", "2026-01-01", null), Not("TOTAL_TARGET_CASH", "MISSING_INPUT")],
            await ComputedAsync(other, "2026-05-15"));

        using var throughSnapshot = await _client.GetAsync(SnapshotEmployee(snapshot) + "?include=computed", other);
        await throughSnapshot.AssertProblemAsync(HttpStatusCode.NotFound);

        var empty = await api.NewTenantAsync(_client);
        using var none = await _client.GetAsync($"{Employee()}?include=computed", empty);
        await none.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- A8: MCP unchanged ----------

    [Fact]
    public async Task A8_mcp_tools_never_return_computed_fields()
    {
        var tenant = await TenantWithAsync(A1);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");
        await using var mcp = await api.ConnectMcpAsync(tenant);

        var rest = await OkAsync(tenant, $"{Employee()}?asOf=2026-05-15");
        foreach (var arguments in new[]
        {
            """{"employeeId": "E-1001", "asOf": "2026-05-15"}""",
            """{"employeeId": "E-1001", "asOf": "2026-05-15", "include": "computed"}""",
            """{"employeeId": "E-1001", "asOf": "2026-05-15", "include": "Computed"}""",
        })
        {
            var result = (await mcp.CallAsync("get_compensation", arguments)).AssertSuccess();
            Assert.True(JsonElement.DeepEquals(rest, result), arguments);
        }

        var restSnapshot = await OkAsync(tenant, SnapshotEmployee(snapshot));
        var snapshotId = snapshot.GetProperty("snapshotId").GetString();
        foreach (var include in new[] { "computed", "nonsense" })
        {
            var result = (await mcp.CallAsync(
                "get_snapshot_compensation", $$"""{"snapshotId": "{{snapshotId}}", "employeeId": "E-1001", "include": "{{include}}"}""")).AssertSuccess();
            Assert.True(JsonElement.DeepEquals(restSnapshot, result));
        }
    }

    // ---------- 3.5 item 4: the wait for a batch being applied ----------

    [Fact]
    public async Task A_read_with_known_at_and_include_waits_for_a_batch_being_applied()
    {
        var tenant = await TenantWithAsync(A1);
        var knownAt = await Instants.DatabaseNowAsync(api.Database);
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");

        await using var applying = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        await using var transaction = await applying.BeginTransactionAsync(Ct);
        var tenantText = tenant.ToString("D");
        await applying.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantText, true)", new { tenantText }, transaction);
        await applying.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@tenantText, 0))", new { tenantText }, transaction);

        var read = _client.GetAsync($"{Employee()}?asOf=2026-05-15&include=computed&knownAt={knownAt}", tenant);
        var snapshotRead = _client.GetAsync(SnapshotEmployee(snapshot) + "?include=computed", tenant);
        await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);
        Assert.False(read.IsCompleted, "A read with knownAt and include must wait for the batch being applied.");
        Assert.False(snapshotRead.IsCompleted, "A snapshot read with include must wait for the batch being applied.");

        await transaction.RollbackAsync(Ct);
        using var readResponse = await read.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var snapshotResponse = await snapshotRead.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(72000m, Computed(await readResponse.JsonAsync())[2].Amount);
        Assert.Equal(72000m, Computed(await snapshotResponse.JsonAsync())[2].Amount);
    }

    // ---------- 3.5 item 5, 6.5 #11: one view of the data for a read of current values ----------

    [Fact]
    public async Task A_current_read_never_mixes_inputs_and_the_later_allowance_of_different_commits()
    {
        // Two states, switched atomically and repeatedly while current reads run:
        //   old: BONUS_TARGET_PCT 10 and no ALLOWANCE      -> bonus 6000,  totals open-ended
        //   new: BONUS_TARGET_PCT 20 and ALLOWANCE from 07-01 -> bonus 12000, totals end 2026-06-30
        // A read that took its inputs and its later ALLOWANCE from different commits would pair 6000 with 2026-06-30 or
        // 12000 with an open end.
        var tenant = await TenantWithAsync(A2Inputs);
        var tenantText = tenant.ToString("D");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        async Task SwitchAsync()
        {
            var toNew = true;
            while (!stop.IsCancellationRequested)
            {
                await using var connection = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
                await using var transaction = await connection.BeginTransactionAsync(Ct);
                await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantText, true)", new { tenantText }, transaction);
                await connection.ExecuteAsync(
                    """
                    INSERT INTO ingestion_batches (id, tenant_id, status, received_at, completed_at, applied_at, total_rows, inserted_rows, corrected_rows, unchanged_rows, error_count)
                    VALUES (@batch, @tenant, 'Applied', clock_timestamp(), clock_timestamp(), clock_timestamp(), 1, 0, 1, 0, 0);
                    UPDATE compensation_values SET superseded_at = clock_timestamp(), superseded_by_batch_id = @batch
                    WHERE superseded_at IS NULL AND employee_id = 'E-2001' AND component IN ('BONUS_TARGET_PCT', 'ALLOWANCE');
                    INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at)
                    VALUES (@tenant, 'E-2001', 'BONUS_TARGET_PCT', DATE '2026-01-01', @pct, NULL, @batch, clock_timestamp());
                    INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at)
                    SELECT @tenant, 'E-2001', 'ALLOWANCE', DATE '2026-07-01', 3000, 'EUR', @batch, clock_timestamp() WHERE @toNew;
                    """,
                    new { batch = Guid.CreateVersion7(), tenant, pct = toNew ? 20m : 10m, toNew },
                    transaction);
                await transaction.CommitAsync(Ct);
                toNew = !toNew;
            }
        }

        var switching = Task.Run(SwitchAsync, Ct);
        var seen = new HashSet<(decimal?, string?)>();
        try
        {
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
                async () =>
                {
                    var observed = new List<(decimal?, string?)>();
                    for (var i = 0; i < 100; i++)
                    {
                        var fields = await ComputedAsync(tenant, "2026-05-15", "E-2001");
                        observed.Add((fields[0].Amount, fields[1].To));
                        Assert.Equal(fields[1].To, fields[2].To);
                    }

                    return observed;
                },
                Ct)).ToList();
            foreach (var observed in await Task.WhenAll(readers))
            {
                seen.UnionWith(observed);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await switching;
        }

        Assert.Subset(new HashSet<(decimal?, string?)> { (6000m, null), (12000m, "2026-06-30") }, seen);
    }

    // ---------- 3.7: logging ----------

    [Fact]
    public async Task Reads_with_include_write_no_entry_of_their_own_and_no_computed_amount()
    {
        var tenant = await TenantWithAsync(Header + "E-8001,BASE_SALARY,2026-01-01,12345.6789,EUR\nE-8001,BONUS_TARGET_PCT,2026-01-01,7,\n");
        var snapshot = await CreateSnapshotAsync(tenant, "S", "2026-05-15");
        var before = api.Logs.Entries.Count;

        await ComputedAsync(tenant, "2026-05-15", "E-8001");
        await OkAsync(tenant, SnapshotEmployee(snapshot, "E-8001") + "?include=computed");
        using (await _client.GetAsync($"{Employee("E-8001")}?include=bad", tenant))
        {
        }

        // 12345.6789 x 7 / 100 = 864.197523 -> 864.1975; the totals 12345.6789 and 13209.8764.
        var entries = api.Logs.Entries.Skip(before).ToList();
        Assert.DoesNotContain(entries, e => e.Message.Contains("864.1975", StringComparison.Ordinal) || e.Message.Contains("13209.8764", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            e => !e.Category.StartsWith("Microsoft.", StringComparison.Ordinal) && !e.Category.StartsWith("System.", StringComparison.Ordinal)
                && e.Level >= LogLevel.Information && e.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D"))));
    }
}

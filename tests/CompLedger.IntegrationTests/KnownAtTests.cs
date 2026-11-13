using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;

namespace CompLedger.IntegrationTests;

/// <summary>Instants as the API writes them, handled here without the product's own parser.</summary>
public static class Instants
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static DateTime Parse(string instant) => DateTime.ParseExact(
        instant, Format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static string Write(DateTime instant) => instant.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>The instant moved by a number of microseconds, in the response format.</summary>
    public static string Shift(string instant, long microseconds) => Write(Parse(instant).AddTicks(microseconds * 10));

    /// <summary>The database clock: the clock that sets apply times and bounds <c>knownAt</c>. The host's may differ.</summary>
    public static async Task<string> DatabaseNowAsync(PostgresFixture database)
    {
        await using var connection = await database.AppDataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        return Write(await connection.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()"));
    }

    /// <summary>Uploads the CSV, expects <c>201</c> and returns the batch's <c>appliedAt</c>.</summary>
    public static async Task<string> AppliedAtAsync(this HttpClient client, Guid tenant, string csv)
    {
        using var response = await client.UploadAsync(tenant, csv);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("appliedAt").GetString()!;
    }
}

/// <summary>PRP-002 T3: <c>knownAt</c> on the as-of and history endpoints (3.2, 3.4 items 1 to 4 and 8; A1, A2, A5, A7, A8).</summary>
public sealed class KnownAtTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string Employee = "/api/v1/employees/E-1001/compensation";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Row(string amount, string from = "2026-01-01", string employee = "E-1001") =>
        Header + $"{employee},BASE_SALARY,{from},{amount},EUR\n";

    /// <summary>A1: 50000 applied at t1, corrected to 51000 at t2.</summary>
    private async Task<(Guid Tenant, string T1, string T2)> TenantWithCorrectionAsync()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, Row("50000"));
        var t2 = await _client.AppliedAtAsync(tenant, Row("51000"));
        return (tenant, t1, t2);
    }

    private async Task<JsonElement> OkAsync(Guid tenant, string url)
    {
        using var response = await _client.GetAsync(url, tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<List<(string Component, decimal Amount, string? Currency, string From, string? To)>> AsOfAsync(
        Guid tenant, string asOf, string? knownAt, string employee = "E-1001")
    {
        var body = await OkAsync(tenant, $"/api/v1/employees/{employee}/compensation?asOf={asOf}" + (knownAt is null ? "" : $"&knownAt={knownAt}"));
        Assert.Equal(asOf, body.GetProperty("asOf").GetString());
        Assert.Equal(knownAt, body.GetProperty("knownAt").GetString());
        return body.GetProperty("components").Periods();
    }

    private async Task<List<(string Component, decimal Amount, string? Currency, string From, string? To)>> HistoryAsync(
        Guid tenant, string? knownAt, string? component = null, string employee = "E-1001")
    {
        var query = string.Join('&', new[] { knownAt is null ? null : $"knownAt={knownAt}", component is null ? null : $"component={component}" }.Where(p => p is not null));
        var body = await OkAsync(tenant, $"/api/v1/employees/{employee}/compensation/history?{query}");
        Assert.Equal(knownAt, body.GetProperty("knownAt").GetString());
        return body.GetProperty("periods").Periods();
    }

    // ---------- A1, A2, A7: the values known at an instant ----------

    [Fact]
    public async Task A1_a_correction_seen_before_and_after()
    {
        var (tenant, t1, t2) = await TenantWithCorrectionAsync();

        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", t1));
        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", Instants.Shift(t2, -1)));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", t2));

        // Without knownAt: the current value and "knownAt": null.
        var current = await OkAsync(tenant, Employee + "?asOf=2026-03-01");
        Assert.Equal(JsonValueKind.Null, current.GetProperty("knownAt").ValueKind);
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], current.GetProperty("components").Periods());

        // One microsecond before the first batch, nothing of this employee was known.
        using var before = await _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={Instants.Shift(t1, -1)}", tenant);
        await before.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task At_a_batchs_apply_time_its_values_are_known_and_the_ones_it_superseded_are_not()
    {
        var (tenant, t1, t2) = await TenantWithCorrectionAsync();

        // Exactly one value per instant in the history: never both the old and the new one, never neither.
        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, t1));
        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, Instants.Shift(t2, -1)));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, t2));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, Instants.Shift(t2, 1)));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, null));

        using var before = await _client.GetAsync(Employee + $"/history?knownAt={Instants.Shift(t1, -1)}", tenant);
        await before.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A2_a_back_dated_value_does_not_change_the_past_view()
    {
        var (tenant, _, t2) = await TenantWithCorrectionAsync();
        var t3 = await _client.AppliedAtAsync(tenant, Row("53000", "2026-04-01"));

        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, t2));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, Instants.Shift(t3, -1)));
        Assert.Equal(
            [("BASE_SALARY", 51000m, "EUR", "2026-01-01", "2026-03-31"), ("BASE_SALARY", 53000m, "EUR", "2026-04-01", null)],
            await HistoryAsync(tenant, t3));

        // The as-of view follows: before t3 the first period is open-ended, also on a date after the later start.
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-05-01", t2));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", "2026-03-31")], await AsOfAsync(tenant, "2026-03-01", t3));
        Assert.Equal([("BASE_SALARY", 53000m, "EUR", "2026-04-01", null)], await AsOfAsync(tenant, "2026-05-01", t3));
    }

    [Fact]
    public async Task Periods_ordering_and_the_component_filter_apply_to_the_values_known_at_the_instant()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, ApiFactory.A1);
        var t2 = await _client.AppliedAtAsync(
            tenant,
            Header
            + "E-1001,BASE_SALARY,2026-04-01,52000,EUR\n"
            + "E-1001,BONUS_TARGET_PCT,2026-01-01,12,\n"
            + "E-1001,ALLOWANCE,2026-02-01,1200,CHF\n");

        // As known at t1: the A1 file only, by component code, then start.
        Assert.Equal(
            [
                ("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"),
                ("BASE_SALARY", 55000m, "EUR", "2026-07-01", null),
                ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null),
            ],
            await HistoryAsync(tenant, t1));
        Assert.Equal(
            [("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            await AsOfAsync(tenant, "2026-05-15", t1));
        Assert.Equal([("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)], await HistoryAsync(tenant, t1, "BONUS_TARGET_PCT"));

        // A known component with no value known at t1 is an empty list, not 404: the employee had values then.
        Assert.Empty(await HistoryAsync(tenant, t1, "ALLOWANCE"));

        // As known at t2: the split period, the corrected percentage and the new component.
        Assert.Equal(
            [
                ("ALLOWANCE", 1200m, "CHF", "2026-02-01", null),
                ("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-03-31"),
                ("BASE_SALARY", 52000m, "EUR", "2026-04-01", "2026-06-30"),
                ("BASE_SALARY", 55000m, "EUR", "2026-07-01", null),
                ("BONUS_TARGET_PCT", 12m, null, "2026-01-01", null),
            ],
            await HistoryAsync(tenant, t2));
        Assert.Equal(
            [
                ("ALLOWANCE", 1200m, "CHF", "2026-02-01", null),
                ("BASE_SALARY", 52000m, "EUR", "2026-04-01", "2026-06-30"),
                ("BONUS_TARGET_PCT", 12m, null, "2026-01-01", null),
            ],
            await AsOfAsync(tenant, "2026-05-15", t2));
        Assert.Equal([("ALLOWANCE", 1200m, "CHF", "2026-02-01", null)], await HistoryAsync(tenant, t2, "ALLOWANCE"));
    }

    [Fact]
    public async Task Existence_is_as_known_404_before_the_first_value_200_empty_when_none_is_in_effect()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, Row("50000"));
        var t2 = await _client.AppliedAtAsync(tenant, Row("60000", employee: "E-2002"));

        // E-2002 exists now, but was not known at t1: 404 on both endpoints, with any date or component.
        foreach (var url in new[]
                 {
                     $"/api/v1/employees/E-2002/compensation?asOf=2026-03-01&knownAt={t1}",
                     $"/api/v1/employees/E-2002/compensation?knownAt={t1}",
                     $"/api/v1/employees/E-2002/compensation/history?knownAt={t1}",
                     $"/api/v1/employees/E-2002/compensation/history?component=ALLOWANCE&knownAt={t1}",
                 })
        {
            using var response = await _client.GetAsync(url, tenant);
            await response.AssertProblemAsync(HttpStatusCode.NotFound);
        }

        // Known at t2. With no value in effect on the date, or none for the component: 200 and an empty list.
        Assert.Equal([("BASE_SALARY", 60000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", t2, "E-2002"));
        Assert.Empty(await AsOfAsync(tenant, "2025-12-31", t2, "E-2002"));
        Assert.Empty(await HistoryAsync(tenant, t2, "ALLOWANCE", "E-2002"));
        Assert.Empty(await AsOfAsync(tenant, "2025-12-31", t1));
    }

    [Fact]
    public async Task Without_known_at_the_answers_are_the_current_ones_and_known_at_is_null()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();

        var asOf = await OkAsync(tenant, Employee + "?asOf=2026-03-01");
        var history = await OkAsync(tenant, Employee + "/history");

        Assert.Equal(["employeeId", "asOf", "knownAt", "components"], asOf.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["employeeId", "knownAt", "periods"], history.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, asOf.GetProperty("knownAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("knownAt").ValueKind);
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], asOf.GetProperty("components").Periods());
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], history.GetProperty("periods").Periods());
    }

    [Fact]
    public async Task A7_a_rejected_batch_changes_no_as_known_at_answer()
    {
        var (tenant, t1, t2) = await TenantWithCorrectionAsync();
        var before = await Instants.DatabaseNowAsync(api.Database);

        using var rejected = await _client.UploadAsync(tenant, Row("99999") + "E-3003,BASE_SALARY,2026-01-01,-1,EUR\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await rejected.JsonAsync()).GetProperty("appliedAt").ValueKind);
        var after = await Instants.DatabaseNowAsync(api.Database);

        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, t1));
        foreach (var knownAt in new[] { t2, before, after, null })
        {
            Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, knownAt));
            Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", knownAt));
        }

        // The valid row of the rejected file was not applied either.
        using var unknown = await _client.GetAsync($"/api/v1/employees/E-3003/compensation?knownAt={after}", tenant);
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_tenants_employee_is_404_at_any_instant()
    {
        var (tenant, t1, t2) = await TenantWithCorrectionAsync();
        var other = await api.NewTenantAsync(_client);
        var otherApplied = await _client.AppliedAtAsync(other, Row("70000", employee: "E-7007"));

        foreach (var knownAt in new[] { t1, t2, otherApplied })
        {
            using var asOf = await _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={knownAt}", other);
            using var history = await _client.GetAsync(Employee + $"/history?knownAt={knownAt}", other);
            await asOf.AssertProblemAsync(HttpStatusCode.NotFound);
            await history.AssertProblemAsync(HttpStatusCode.NotFound);
        }

        // And the first tenant still sees its own values at the other tenant's apply time.
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", otherApplied));
        using var foreign = await _client.GetAsync($"/api/v1/employees/E-7007/compensation?knownAt={otherApplied}", tenant);
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- 3.2, 3.5, A5: the parameter ----------

    [Theory]
    [InlineData("2026-11-13T10:00:00+01:00")]
    [InlineData("2026-11-13T10:00:00%2B01:00")]
    [InlineData("2026-11-13t10:00:00Z")]
    [InlineData("2026-11-13T10:00:00z")]
    [InlineData("2026-11-13T10:00:00.1234567Z")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-11-13T24:00:00Z")]
    [InlineData("2026-11-13T10:00:60Z")]
    [InlineData("2026-11-13T10:00:00")]
    [InlineData("2026-11-13")]
    [InlineData("%202026-11-13T10:00:00Z")]
    [InlineData("2026-11-13T10:00:00Z%20")]
    [InlineData("2026-11-13T10:00:00Z%0A")]
    [InlineData("2026-11-13T10:00:00Z%00")]
    [InlineData("%00")]
    [InlineData("1899-12-31T23:59:59.999999Z")]
    [InlineData("")]
    public async Task An_invalid_known_at_is_400_on_both_endpoints(string value)
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();

        using var asOf = await _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={value}", tenant);
        using var history = await _client.GetAsync(Employee + $"/history?knownAt={value}", tenant);

        await asOf.AssertProblemAsync(HttpStatusCode.BadRequest);
        await history.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Known_at_given_twice_is_400_even_with_equal_values()
    {
        var (tenant, t1, t2) = await TenantWithCorrectionAsync();

        foreach (var query in new[] { $"knownAt={t1}&knownAt={t1}", $"knownAt={t1}&knownAt={t2}", $"knownAt={t1}&asOf=2026-03-01&knownAt={t1}" })
        {
            using var asOf = await _client.GetAsync(Employee + "?" + query, tenant);
            using var history = await _client.GetAsync(Employee + "/history?" + query, tenant);
            await asOf.AssertProblemAsync(HttpStatusCode.BadRequest);
            await history.AssertProblemAsync(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task A_known_at_after_the_request_time_is_400_and_one_at_the_database_clock_is_accepted()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();
        var now = await Instants.DatabaseNowAsync(api.Database);

        foreach (var future in new[] { Instants.Shift(now, 3_600_000_000), Instants.Shift(now, 60_000_000), "2999-12-31T23:59:59Z", "9999-12-31T23:59:59.999999Z" })
        {
            using var asOf = await _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={future}", tenant);
            using var history = await _client.GetAsync(Employee + $"/history?knownAt={future}", tenant);
            await asOf.AssertProblemAsync(HttpStatusCode.BadRequest);
            await history.AssertProblemAsync(HttpStatusCode.BadRequest);
        }

        // An instant read from the database clock before the request is never in the future for it.
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", now));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await HistoryAsync(tenant, await Instants.DatabaseNowAsync(api.Database)));
    }

    [Fact]
    public async Task An_applied_at_sent_back_unchanged_is_accepted_at_once()
    {
        var tenant = await api.NewTenantAsync(_client);

        // Straight after each upload, with no pause: the service's own instant is never later than its request time.
        for (var i = 0; i < 10; i++)
        {
            var amount = (40000 + i).ToString(CultureInfo.InvariantCulture);
            var appliedAt = await _client.AppliedAtAsync(tenant, Row(amount));

            Assert.Equal(
                [("BASE_SALARY", decimal.Parse(amount, CultureInfo.InvariantCulture), "EUR", "2026-01-01", null)],
                await AsOfAsync(tenant, "2026-03-01", appliedAt));
        }
    }

    [Fact]
    public async Task The_response_echoes_the_requested_instant_with_six_fractional_digits()
    {
        var tenant = await api.NewTenantAsync(_client);
        var appliedAt = Instants.Parse(await _client.AppliedAtAsync(tenant, Row("50000")));

        // The first whole second after the upload, once the database clock has passed it.
        var second = new DateTime(appliedAt.Ticks - (appliedAt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddSeconds(1);
        while (string.CompareOrdinal(await Instants.DatabaseNowAsync(api.Database), Instants.Write(second)) < 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        // Shorter spellings of that second, and the microsecond before it, each with the six digits it must come back with.
        foreach (var (requested, expected) in new[]
                 {
                     (second.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), Instants.Write(second)),
                     (second.ToString("yyyy-MM-dd'T'HH:mm:ss.f'Z'", CultureInfo.InvariantCulture), Instants.Write(second)),
                     (second.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), Instants.Write(second)),
                     (Instants.Write(second), Instants.Write(second)),
                     (Instants.Write(second.AddTicks(-10)), Instants.Write(second.AddTicks(-10))),
                 })
        {
            var asOf = await OkAsync(tenant, Employee + $"?asOf=2026-03-01&knownAt={requested}");
            var history = await OkAsync(tenant, Employee + $"/history?knownAt={requested}");

            Assert.Equal(expected, asOf.GetProperty("knownAt").GetString());
            Assert.Equal(expected, history.GetProperty("knownAt").GetString());
            Assert.Equal(27, expected.Length);
            Assert.Equal(["employeeId", "asOf", "knownAt", "components"], asOf.EnumerateObject().Select(p => p.Name));
            Assert.Equal(["employeeId", "knownAt", "periods"], history.EnumerateObject().Select(p => p.Name));
            Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], asOf.GetProperty("components").Periods());
        }
    }

    [Fact]
    public async Task The_lower_bound_1900_is_accepted_and_nothing_was_known_then()
    {
        var (tenant, _, _) = await TenantWithCorrectionAsync();

        using var asOf = await _client.GetAsync(Employee + "?knownAt=1900-01-01T00:00:00Z", tenant);
        using var history = await _client.GetAsync(Employee + "/history?knownAt=1900-01-01T00:00:00.000000Z", tenant);

        await asOf.AssertProblemAsync(HttpStatusCode.NotFound);
        await history.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Checks_run_in_order_tenant_then_query_then_lookup()
    {
        var (tenant, t1, _) = await TenantWithCorrectionAsync();
        var future = Instants.Shift(await Instants.DatabaseNowAsync(api.Database), 3_600_000_000);

        // 401 before 400: no tenant and an invalid knownAt.
        using var noTenant = await _client.GetAsync(Employee + "?knownAt=garbage", (Guid?)null);
        using var unknownTenant = await _client.GetAsync(Employee + "/history?knownAt=garbage", Guid.CreateVersion7());
        await noTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await unknownTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);

        // 400 before 404: an employee that does not exist, with an invalid or a future knownAt, or another bad parameter.
        foreach (var url in new[]
                 {
                     "/api/v1/employees/E-NONE/compensation?knownAt=garbage",
                     $"/api/v1/employees/E-NONE/compensation?knownAt={future}",
                     $"/api/v1/employees/E-NONE/compensation/history?knownAt={future}",
                     $"/api/v1/employees/E-NONE/compensation?asOf=2026-02-30&knownAt={t1}",
                     $"/api/v1/employees/E-NONE/compensation/history?component=NOPE&knownAt={t1}",
                     $"/api/v1/employees/E-NONE/compensation/history?component=BASE_SALARY&knownAt=",
                 })
        {
            using var response = await _client.GetAsync(url, tenant);
            await response.AssertProblemAsync(HttpStatusCode.BadRequest);
        }

        using var missing = await _client.GetAsync($"/api/v1/employees/E-NONE/compensation?knownAt={t1}", tenant);
        await missing.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Other_query_parameters_are_ignored_next_to_known_at()
    {
        var (tenant, t1, _) = await TenantWithCorrectionAsync();

        var asOf = await OkAsync(tenant, Employee + $"?asOf=2026-03-01&knownAt={t1}&component=NOPE&snapshotId=x&x=1&x=2");
        var history = await OkAsync(tenant, Employee + $"/history?knownAt={t1}&asOf=garbage&asOf=again&y=");

        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], asOf.GetProperty("components").Periods());
        Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], history.GetProperty("periods").Periods());
    }

    [Fact]
    public async Task Reads_with_known_at_write_no_log_entry_of_the_application()
    {
        var (tenant, t1, _) = await TenantWithCorrectionAsync();
        var before = api.Logs.Entries.Count(e => IsApplicationEntryFor(e, tenant));

        await AsOfAsync(tenant, "2026-03-01", t1);
        await HistoryAsync(tenant, t1);

        // Only the two batch entries of the uploads mention this tenant.
        Assert.Equal(2, before);
        Assert.Equal(2, api.Logs.Entries.Count(e => IsApplicationEntryFor(e, tenant)));
        Assert.DoesNotContain(api.Logs.Entries, e => !IsFramework(e) && e.Message.Contains("E-1001", StringComparison.Ordinal));
    }

    private static bool IsFramework(CapturedLogs.Entry entry) =>
        entry.Category.StartsWith("Microsoft.", StringComparison.Ordinal) || entry.Category.StartsWith("System.", StringComparison.Ordinal);

    private static bool IsApplicationEntryFor(CapturedLogs.Entry entry, Guid tenant) =>
        !IsFramework(entry) && entry.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D")));

    // ---------- 3.4 item 4, A8: waiting for a batch being applied ----------

    /// <summary>
    /// A batch being applied, reproduced step by step as PRP-001 7.2 does it: the tenant's exclusive lock, the apply
    /// time from the clock, the writes, and the commit when the test says so.
    /// </summary>
    [Fact]
    public async Task A8_a_read_with_known_at_waits_for_a_batch_being_applied_and_then_includes_it()
    {
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Row("50000"));
        await _client.AppliedAtAsync(other, Row("70000"));

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
            VALUES (@batch, @tenant, 'Applied', @appliedAt, @appliedAt, @appliedAt, 2, 1, 1, 0, 0);
            UPDATE compensation_values SET superseded_at = @appliedAt, superseded_by_batch_id = @batch WHERE employee_id = 'E-1001';
            INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at)
            VALUES (@tenant, 'E-1001', 'BASE_SALARY', DATE '2026-01-01', 51000, 'EUR', @batch, @appliedAt),
                   (@tenant, 'E-4004', 'BASE_SALARY', DATE '2026-01-01', 64000, 'EUR', @batch, @appliedAt);
            """,
            new { batch, tenant, appliedAt },
            transaction);

        // "Now" is after the apply time, and the batch has not committed.
        var now = await Instants.DatabaseNowAsync(api.Database);
        Assert.True(string.CompareOrdinal(Instants.Write(appliedAt), now) < 0);

        var asOf = _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={now}", tenant);
        var history = _client.GetAsync(Employee + $"/history?knownAt={now}", tenant);
        var newEmployee = _client.GetAsync($"/api/v1/employees/E-4004/compensation?asOf=2026-03-01&knownAt={now}", tenant);

        // Another tenant is not held up, and neither is a read of this tenant without knownAt (it takes no lock and
        // sees the values committed so far).
        using (var otherTenant = await _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={now}", other).WaitAsync(TimeSpan.FromSeconds(20), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
            Assert.Equal([("BASE_SALARY", 70000m, "EUR", "2026-01-01", null)], (await otherTenant.JsonAsync()).GetProperty("components").Periods());
        }

        using (var current = await _client.GetAsync(Employee + "?asOf=2026-03-01", tenant).WaitAsync(TimeSpan.FromSeconds(20), Ct))
        {
            Assert.Equal([("BASE_SALARY", 50000m, "EUR", "2026-01-01", null)], (await current.JsonAsync()).GetProperty("components").Periods());
        }

        await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);
        Assert.False(asOf.IsCompleted, "An as-of read with knownAt must wait for the batch being applied.");
        Assert.False(history.IsCompleted, "A history read with knownAt must wait for the batch being applied.");
        Assert.False(newEmployee.IsCompleted, "An as-of read with knownAt must wait for the batch being applied.");

        await transaction.CommitAsync(Ct);

        // After the commit the answers include the batch: its apply time is before the requested instant.
        using var asOfResponse = await asOf.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var historyResponse = await history.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var newEmployeeResponse = await newEmployee.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(HttpStatusCode.OK, asOfResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newEmployeeResponse.StatusCode);
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], (await asOfResponse.JsonAsync()).GetProperty("components").Periods());
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], (await historyResponse.JsonAsync()).GetProperty("periods").Periods());
        Assert.Equal([("BASE_SALARY", 64000m, "EUR", "2026-01-01", null)], (await newEmployeeResponse.JsonAsync()).GetProperty("components").Periods());
    }

    /// <summary>A8 as written: a real upload of one million rows, caught while it holds the tenant's lock.</summary>
    [Fact]
    public async Task A8_while_a_one_million_row_batch_is_applied_its_tenant_waits_and_another_tenant_answers_at_once()
    {
        const int rows = 1_000_000;
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(other, Row("70000"));

        var csv = new StringBuilder(Header, rows * 44);
        for (var i = 1; i <= rows; i++)
        {
            csv.Append("E-").Append(i.ToString("D7", CultureInfo.InvariantCulture)).Append(",BASE_SALARY,2026-01-01,").Append(30000 + i).Append(",EUR\n");
        }

        var body = Encoding.ASCII.GetBytes(csv.ToString());
        csv.Clear();

        var upload = Task.Run(() => _client.UploadAsync(tenant, body), Ct);

        // Wait until the upload holds the tenant's exclusive lock: from then to its commit it is "being applied".
        await using var monitor = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!await monitor.ExecuteScalarAsync<bool>(
                   """
                   SELECT EXISTS (
                     SELECT 1 FROM pg_locks
                     WHERE locktype = 'advisory' AND mode = 'ExclusiveLock' AND granted AND objsubid = 1
                       AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@tenant, 0))
                   """,
                   new { tenant = tenant.ToString("D") }))
        {
            Assert.False(upload.IsCompleted, "The upload finished before its lock was seen.");
            Assert.True(DateTime.UtcNow < deadline, "The upload never took its lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(2), Ct);
        }

        var now = Instants.Write(await monitor.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()"));
        var waiting = _client.GetAsync($"/api/v1/employees/E-0500000/compensation?asOf=2026-03-01&knownAt={now}", tenant);

        // The other tenant answers while the batch is still being applied.
        using (var otherTenant = await _client.GetAsync(Employee + $"?asOf=2026-03-01&knownAt={now}", other).WaitAsync(TimeSpan.FromSeconds(20), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
            Assert.False(upload.IsCompleted, "The batch was expected to be still applying when the other tenant got its answer.");
        }

        // The batch's own tenant gets its answer only after the commit, and the answer includes the batch. Had the
        // read not waited, it would have seen no committed value of this employee: 404.
        using var response = await waiting.WaitAsync(TimeSpan.FromMinutes(3), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("BASE_SALARY", 530000m, "EUR", "2026-01-01", null)], (await response.JsonAsync()).GetProperty("components").Periods());

        using var uploaded = await upload.WaitAsync(TimeSpan.FromMinutes(3), Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var batch = await uploaded.JsonAsync();
        Assert.Equal((rows, rows, 0, 0), batch.Rows());
        Assert.True(string.CompareOrdinal(batch.GetProperty("appliedAt").GetString(), now) < 0);
    }

    [Fact]
    public async Task A_read_with_known_at_does_not_wait_for_a_batch_that_is_still_being_uploaded()
    {
        var (tenant, _, t2) = await TenantWithCorrectionAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new GatedContent(Encoding.UTF8.GetBytes(Row("52000")), release.Task);

        // The upload is stuck in the middle of its body: nothing is being applied yet.
        var upload = Task.Run(() => _client.SendAsync(HttpMethod.Post, "/api/v1/ingestions", tenant, content), Ct);
        await content.Blocked.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var now = await Instants.DatabaseNowAsync(api.Database);
        var read = await AsOfAsync(tenant, "2026-03-01", now).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], read);
        Assert.False(upload.IsCompleted);

        release.SetResult();
        using var response = await upload.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var t3 = (await response.JsonAsync()).GetProperty("appliedAt").GetString()!;

        // The batch got a later apply time than the instant that was answered, so that answer still holds.
        Assert.True(string.CompareOrdinal(now, t3) < 0);
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", now));
        Assert.Equal([("BASE_SALARY", 51000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", t2));
        Assert.Equal([("BASE_SALARY", 52000m, "EUR", "2026-01-01", null)], await AsOfAsync(tenant, "2026-03-01", t3));
    }

    /// <summary>A CSV body that sends its first half, then waits for the gate before sending the rest.</summary>
    private sealed class GatedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly Task _gate;
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedContent(byte[] bytes, Task gate)
        {
            (_bytes, _gate) = (bytes, gate);
            Headers.TryAddWithoutValidation("Content-Type", "text/csv");
        }

        public Task Blocked => _blocked.Task;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var half = _bytes.Length / 2;
            await stream.WriteAsync(_bytes.AsMemory(0, half));
            await stream.FlushAsync();
            _blocked.TrySetResult();
            await _gate;
            await stream.WriteAsync(_bytes.AsMemory(half));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }
}

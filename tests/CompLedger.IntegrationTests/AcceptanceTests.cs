using System.Net;
using CompLedger.Infrastructure.Db;
using Dapper;

namespace CompLedger.IntegrationTests;

/// <summary>The public acceptance examples A1 to A10 of PRP-001 section 4, over HTTP.</summary>
public sealed class AcceptanceTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string A1 = ApiFactory.A1;

    private readonly HttpClient _client = api.CreateClient();

    private async Task<Guid> TenantWithA1Async()
    {
        var tenant = await api.NewTenantAsync(_client);
        using var response = await _client.UploadAsync(tenant, A1);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return tenant;
    }

    private async Task<List<(string Component, decimal Amount, string? Currency, string From, string? To)>> AsOfAsync(Guid tenant, string employee, string asOf)
    {
        using var response = await _client.GetAsync($"/api/v1/employees/{employee}/compensation?asOf={asOf}", tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(employee, body.GetProperty("employeeId").GetString());
        Assert.Equal(asOf, body.GetProperty("asOf").GetString());
        return body.GetProperty("components").Periods();
    }

    private async Task<List<(string Component, decimal Amount, string? Currency, string From, string? To)>> HistoryAsync(Guid tenant, string employee, string component)
    {
        using var response = await _client.GetAsync($"/api/v1/employees/{employee}/compensation/history?component={component}", tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("periods").Periods();
    }

    [Fact]
    public async Task A1_basic_ingest_and_as_of()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(tenant, A1);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = await response.JsonAsync();
        Assert.Equal("Applied", batch.GetProperty("status").GetString());
        Assert.Equal((3, 3, 0, 0), batch.Rows());

        Assert.Equal(
            [("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            await AsOfAsync(tenant, "E-1001", "2026-03-15"));
        Assert.Equal(
            [("BASE_SALARY", 55000m, "EUR", "2026-07-01", null), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            await AsOfAsync(tenant, "E-1001", "2026-07-01"));
        Assert.Empty(await AsOfAsync(tenant, "E-1001", "2025-12-31"));
    }

    [Fact]
    public async Task A2_a_back_dated_value_splits_a_period()
    {
        var tenant = await TenantWithA1Async();

        using var response = await _client.UploadAsync(tenant, Header + "E-1001,BASE_SALARY,2026-04-01,52000,EUR\n");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((1, 1, 0, 0), (await response.JsonAsync()).Rows());
        Assert.Equal(
            [
                ("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-03-31"),
                ("BASE_SALARY", 52000m, "EUR", "2026-04-01", "2026-06-30"),
                ("BASE_SALARY", 55000m, "EUR", "2026-07-01", null),
            ],
            await HistoryAsync(tenant, "E-1001", "BASE_SALARY"));
    }

    [Fact]
    public async Task A3_re_upload_is_a_no_op()
    {
        var tenant = await TenantWithA1Async();
        var before = await HistoryAsync(tenant, "E-1001", "BASE_SALARY");

        using var response = await _client.UploadAsync(tenant, A1.Replace("50000,", "50000.00,"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((3, 0, 0, 3), (await response.JsonAsync()).Rows());
        Assert.Equal(before, await HistoryAsync(tenant, "E-1001", "BASE_SALARY"));
    }

    [Fact]
    public async Task A4_correction_keeps_the_superseded_row()
    {
        var tenant = await TenantWithA1Async();

        using var response = await _client.UploadAsync(tenant, Header + "E-1001,BASE_SALARY,2026-01-01,51000,EUR\n");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = await response.JsonAsync();
        Assert.Equal((1, 0, 1, 0), batch.Rows());
        Assert.Equal(("BASE_SALARY", 51000m, "EUR", "2026-01-01", "2026-06-30"), (await AsOfAsync(tenant, "E-1001", "2026-02-01"))[0]);

        await using var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, TestContext.Current.CancellationToken);
        var old = await scope.Connection.QuerySingleAsync<(DateTime? SupersededAt, Guid? SupersededBy)>(
            "SELECT superseded_at, superseded_by_batch_id FROM compensation_values WHERE amount = 50000", transaction: scope.Transaction);
        Assert.NotNull(old.SupersededAt);
        Assert.Equal(batch.GetProperty("batchId").GetGuid(), old.SupersededBy);
    }

    [Fact]
    public async Task A5_one_bad_row_rejects_the_whole_file()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(
            tenant,
            Header
            + "E-2001,BASE_SALARY,2026-01-01,40000,EUR\n"
            + "E-2002,BASE_SALARY,2026-01-01,41000,EUR\n"
            + "E-2003,BASE_SALARY,2026-01-01,\"5,000\",EUR\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var batch = await response.JsonAsync();
        Assert.Equal("Rejected", batch.GetProperty("status").GetString());
        Assert.Equal([(4, "amount", "INVALID_AMOUNT")], batch.Errors());
        Assert.Equal(1, batch.GetProperty("errorCount").GetInt32());

        foreach (var employee in new[] { "E-2001", "E-2002" })
        {
            using var lookup = await _client.GetAsync($"/api/v1/employees/{employee}/compensation", tenant);
            await lookup.AssertProblemAsync(HttpStatusCode.NotFound);
        }

        using var stored = await _client.GetAsync($"/api/v1/ingestions/{batch.GetProperty("batchId").GetGuid()}", tenant);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var storedBatch = await stored.JsonAsync();
        Assert.Equal("Rejected", storedBatch.GetProperty("status").GetString());
        Assert.Equal(1, storedBatch.GetProperty("errorCount").GetInt32());
        Assert.Equal([(4, "amount", "INVALID_AMOUNT")], storedBatch.Errors());
    }

    [Fact]
    public async Task A6_duplicate_key_in_the_file()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(
            tenant,
            Header
            + "E-3001,BASE_SALARY,2026-01-01,100,EUR\n"
            + "E-3002,BASE_SALARY,2026-01-01,100,EUR\n"
            + "E-3003,BASE_SALARY,2026-01-01,100,EUR\n"
            + "E-3001,BASE_SALARY,2026-01-01,200,EUR\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal([(2, null, "DUPLICATE_KEY_IN_FILE"), (5, null, "DUPLICATE_KEY_IN_FILE")], (await response.JsonAsync()).Errors());
    }

    [Fact]
    public async Task A7_currency_rules_per_component()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var notAllowed = await _client.UploadAsync(tenant, Header + "E-4001,BONUS_TARGET_PCT,2026-01-01,15,EUR\n");
        using var required = await _client.UploadAsync(tenant, Header + "E-4001,BASE_SALARY,2026-01-01,45000,\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAllowed.StatusCode);
        Assert.Equal([(2, "currency", "CURRENCY_NOT_ALLOWED")], (await notAllowed.JsonAsync()).Errors());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, required.StatusCode);
        Assert.Equal([(2, "currency", "CURRENCY_REQUIRED")], (await required.JsonAsync()).Errors());
    }

    [Fact]
    public async Task A8_tenant_isolation()
    {
        var t1 = await api.NewTenantAsync(_client, "T1");
        var t2 = await api.NewTenantAsync(_client, "T2");
        using var a1 = await _client.UploadAsync(t1, A1);
        var t1Batch = (await a1.JsonAsync()).GetProperty("batchId").GetGuid();

        using var foreignEmployee = await _client.GetAsync("/api/v1/employees/E-1001/compensation", t2);
        await foreignEmployee.AssertProblemAsync(HttpStatusCode.NotFound);
        using var foreignHistory = await _client.GetAsync("/api/v1/employees/E-1001/compensation/history", t2);
        await foreignHistory.AssertProblemAsync(HttpStatusCode.NotFound);
        using var foreignBatch = await _client.GetAsync($"/api/v1/ingestions/{t1Batch}", t2);
        await foreignBatch.AssertProblemAsync(HttpStatusCode.NotFound);

        using var upload = await _client.UploadAsync(t2, Header + "E-1001,BASE_SALARY,2026-01-01,70000,USD\n");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.Equal((1, 1, 0, 0), (await upload.JsonAsync()).Rows());

        Assert.Equal(("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"), (await AsOfAsync(t1, "E-1001", "2026-03-15"))[0]);
        Assert.Equal([("BASE_SALARY", 70000m, "USD", "2026-01-01", null)], await AsOfAsync(t2, "E-1001", "2026-03-15"));
    }

    [Fact]
    public async Task A9_tenant_resolution()
    {
        using var missing = await _client.GetAsync("/api/v1/employees/E-1001/compensation", tenant: null);
        await missing.AssertProblemAsync(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/employees/E-1001/compensation");
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", "not-a-uuid");
        using var notUuid = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        await notUuid.AssertProblemAsync(HttpStatusCode.Unauthorized);

        using var unknown = await _client.GetAsync("/api/v1/employees/E-1001/compensation", Guid.CreateVersion7());
        await unknown.AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A10_header_and_transport_errors()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var noCurrency = await _client.UploadAsync(tenant, "employee_id,component,effective_from,amount\nE-1,NOPE,bad,bad\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCurrency.StatusCode);
        var batch = await noCurrency.JsonAsync();
        Assert.Equal([(1, "currency", "MISSING_COLUMN")], batch.Errors());
        Assert.Equal((0, 0, 0, 0), batch.Rows());

        using var json = await _client.UploadAsync(tenant, A1, "application/json");
        await json.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
    }
}

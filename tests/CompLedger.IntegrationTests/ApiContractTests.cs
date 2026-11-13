using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CompLedger.IntegrationTests;

/// <summary>The API contract of PRP-001 3.6 beyond the acceptance examples, and the batch log entry of 3.7.</summary>
public sealed class ApiContractTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string A1 = ApiFactory.A1;
    private const string EmployeeUrl = "/api/v1/employees/E-1001/compensation";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> TenantWithA1Async()
    {
        var tenant = await api.NewTenantAsync(_client);
        using var response = await _client.UploadAsync(tenant, A1);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return tenant;
    }

    private async Task<HttpResponseMessage> SendWithTenantHeaderAsync(HttpMethod method, string url, string? headerValue, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (headerValue is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", headerValue);
        }

        return await _client.SendAsync(request, Ct);
    }

    private static StringContent Json(string body, string contentType = "application/json")
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return content;
    }

    // ---------- tenant resolution ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // nil UUID
    [InlineData("{TENANT:B}")] // braces
    [InlineData("{TENANT:N}")] // no hyphens
    [InlineData("{TENANT:P}")] // parentheses
    [InlineData("{TENANT:D}x")]
    [InlineData("{TENANT:D},{TENANT:D}")]
    public async Task Anything_but_a_known_hyphenated_uuid_is_401_on_every_tenant_endpoint(string? headerTemplate)
    {
        var tenant = await TenantWithA1Async();
        var header = headerTemplate?
            .Replace("{TENANT:B}", tenant.ToString("B")).Replace("{TENANT:N}", tenant.ToString("N"))
            .Replace("{TENANT:P}", tenant.ToString("P")).Replace("{TENANT:D}", tenant.ToString("D"));

        foreach (var url in new[] { EmployeeUrl, EmployeeUrl + "/history", $"/api/v1/ingestions/{Guid.CreateVersion7()}" })
        {
            using var response = await SendWithTenantHeaderAsync(HttpMethod.Get, url, header);
            await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
        }

        using var upload = await SendWithTenantHeaderAsync(HttpMethod.Post, "/api/v1/ingestions", header, new StringContent(A1, Encoding.UTF8, "text/csv"));
        await upload.AssertProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(1, await api.CountBatchesAsync(tenant));
    }

    [Fact]
    public async Task Tenant_id_hex_digits_are_case_insensitive()
    {
        var tenant = await TenantWithA1Async();

        using var upper = await SendWithTenantHeaderAsync(HttpMethod.Get, EmployeeUrl, tenant.ToString("D").ToUpperInvariant());
        using var lower = await SendWithTenantHeaderAsync(HttpMethod.Get, EmployeeUrl, tenant.ToString("D"));

        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Health_and_tenant_creation_ignore_the_tenant_header(string header)
    {
        using var health = await SendWithTenantHeaderAsync(HttpMethod.Get, "/health", header);
        using var create = await SendWithTenantHeaderAsync(HttpMethod.Post, "/api/v1/tenants", header, Json("""{"name":"Acme"}"""));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await health.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    // ---------- POST /api/v1/tenants ----------

    [Theory]
    [InlineData("""{"name":"Acme"}""", "Acme")]
    [InlineData("""{"Name":"Acme"}""", "Acme")] // property names are case-insensitive
    [InlineData("""{"NAME":"Acme","plan":"gold","nested":{"a":[1]}}""", "Acme")] // unknown properties are ignored
    [InlineData("""{"name":" \t Acme Corp \r\n"}""", "Acme Corp")] // trimmed with the CSV set
    [InlineData("""{"name":" Acme "}""", " Acme ")] // a no-break space is kept
    [InlineData("""{"name":"x"}""", "x")]
    public async Task Creating_a_tenant_returns_201_with_id_and_trimmed_name_and_no_location(string body, string expectedName)
    {
        using var response = await _client.PostAsync("/api/v1/tenants", Json(body), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var tenant = await response.JsonAsync();
        Assert.Equal(expectedName, tenant.GetProperty("name").GetString());
        Assert.Equal(2, tenant.EnumerateObject().Count());

        var id = tenant.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParseExact(id, "D", out var tenantId));
        Assert.Equal(7, tenantId.Version);

        // The new tenant is known: an unknown employee is 404 rather than 401.
        using var lookup = await _client.GetAsync(EmployeeUrl, tenantId);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.Created)]
    [InlineData(200, HttpStatusCode.Created)]
    [InlineData(201, HttpStatusCode.BadRequest)]
    public async Task Tenant_name_length_is_1_to_200_utf16_code_units_after_trimming(int length, HttpStatusCode expected)
    {
        var body = JsonSerializer.Serialize(new { name = "  " + new string('n', length) + "  " });

        using var response = await _client.PostAsync("/api/v1/tenants", Json(body), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_name_length_counts_utf16_code_units()
    {
        // 100 emoji are 200 code units; 101 are 202.
        using var fits = await _client.PostAsync("/api/v1/tenants", Json(JsonSerializer.Serialize(new { name = string.Concat(Enumerable.Repeat("😀", 100)) })), Ct);
        using var tooLong = await _client.PostAsync("/api/v1/tenants", Json(JsonSerializer.Serialize(new { name = string.Concat(Enumerable.Repeat("😀", 101)) })), Ct);

        Assert.Equal(HttpStatusCode.Created, fits.StatusCode);
        await tooLong.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":" \t\r\n "}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("""{"name":42}""")]
    [InlineData("""{"name":["Acme"]}""")]
    [InlineData("""{"name":{"value":"Acme"}}""")]
    [InlineData("""{"title":"Acme"}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"Acme\"")]
    [InlineData("null")]
    [InlineData("{\"name\":\"Acme")] // malformed JSON
    [InlineData("{name:'Acme'}")]
    [InlineData("")]
    public async Task Invalid_tenant_bodies_are_400(string body)
    {
        using var response = await _client.PostAsync("/api/v1/tenants", Json(body), Ct);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.Created)]
    [InlineData("APPLICATION/JSON", HttpStatusCode.Created)]
    [InlineData("application/json; charset=utf-8", HttpStatusCode.Created)]
    [InlineData("application/json; charset=UTF-8", HttpStatusCode.Created)]
    [InlineData("application/json; charset=\"utf-8\"", HttpStatusCode.Created)]
    [InlineData("application/json; version=1", HttpStatusCode.Created)]
    [InlineData("application/json; charset=utf-16", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json; charset=iso-8859-1", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/jsonx", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/problem+json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/csv", HttpStatusCode.UnsupportedMediaType)]
    public async Task Tenant_creation_content_type_rules(string contentType, HttpStatusCode expected)
    {
        // The body is malformed on purpose for the 415 cases: the content type is checked first.
        var body = expected == HttpStatusCode.Created ? """{"name":"Acme"}""" : "{";

        using var response = await _client.PostAsync("/api/v1/tenants", Json(body, contentType), Ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created)
        {
            await response.AssertProblemAsync(expected);
        }
    }

    [Fact]
    public async Task Tenant_creation_without_a_content_type_is_415()
    {
        var content = new ByteArrayContent("""{"name":"Acme"}"""u8.ToArray());

        using var response = await _client.PostAsync("/api/v1/tenants", content, Ct);

        await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
    }

    // ---------- POST /api/v1/ingestions: request checks ----------

    [Theory]
    [InlineData("text/csv")]
    [InlineData("TEXT/CSV")]
    [InlineData("Text/Csv; charset=utf-8")]
    [InlineData("text/csv;charset=UTF-8")]
    [InlineData("text/csv; charset=\"utf-8\"")]
    [InlineData("text/csv; header=present")]
    [InlineData("text/csv; header=present; charset=Utf-8")]
    public async Task Csv_content_types_are_accepted(string contentType)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(tenant, A1, contentType);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("text/csv-schema")]
    [InlineData("text/csvx")]
    [InlineData("application/csv")]
    [InlineData("text/csv; charset=iso-8859-1")]
    [InlineData("text/csv; charset=utf-16")]
    [InlineData("text/csv; header=present; charset=windows-1252")]
    [InlineData("multipart/form-data; boundary=x")]
    public async Task Other_content_types_are_415_and_leave_no_batch(string? contentType)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(tenant, A1, contentType);

        await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(0, await api.CountBatchesAsync(tenant));
        using var lookup = await _client.GetAsync(EmployeeUrl, tenant);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task The_tenant_check_comes_before_the_content_type_check()
    {
        using var response = await _client.UploadAsync(tenant: null, A1, "application/json");

        await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    // ---------- batch resource ----------

    [Fact]
    public async Task An_applied_batch_has_a_location_and_reads_back_identically()
    {
        var tenant = await api.NewTenantAsync(_client);
        var before = DateTime.UtcNow.AddSeconds(-1);

        using var response = await _client.UploadAsync(tenant, A1);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var batch = await response.JsonAsync();
        var batchId = batch.GetProperty("batchId").GetString()!;
        Assert.True(Guid.TryParseExact(batchId, "D", out var parsedId));
        Assert.Equal(7, parsedId.Version);
        Assert.Equal($"/api/v1/ingestions/{batchId}", response.Headers.Location?.OriginalString);

        Assert.Equal(["batchId", "status", "receivedAt", "completedAt", "rows", "errorCount", "errors"], batch.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["total", "inserted", "corrected", "unchanged"], batch.GetProperty("rows").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Applied", batch.GetProperty("status").GetString());
        Assert.Equal(0, batch.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, batch.GetProperty("errors").GetArrayLength());

        var receivedAt = batch.GetProperty("receivedAt").GetString()!;
        var completedAt = batch.GetProperty("completedAt").GetString()!;
        Assert.EndsWith("Z", receivedAt, StringComparison.Ordinal);
        Assert.EndsWith("Z", completedAt, StringComparison.Ordinal);
        Assert.InRange(batch.GetProperty("receivedAt").GetDateTime(), before, DateTime.UtcNow.AddSeconds(1));
        Assert.True(batch.GetProperty("completedAt").GetDateTime() >= batch.GetProperty("receivedAt").GetDateTime());

        using var stored = await _client.GetAsync(response.Headers.Location!.OriginalString, tenant);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(text, await stored.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_rejected_batch_has_no_location_and_reads_back_identically()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(tenant, Header + "E-1,BASE_SALARY,2026-01-01,1,EUR\n.,NOPE,2026-01-01,1,EUR\n\nx\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var batch = await response.JsonAsync();
        Assert.Equal("Rejected", batch.GetProperty("status").GetString());
        Assert.Equal((4, 0, 0, 0), batch.Rows());
        Assert.Equal(4, batch.GetProperty("errorCount").GetInt32());
        Assert.Equal(
            [(3, "employee_id", "INVALID_EMPLOYEE_ID"), (3, "component", "UNKNOWN_COMPONENT"), (4, null, "MALFORMED_CSV"), (5, null, "MALFORMED_CSV")],
            batch.Errors());
        Assert.All(batch.GetProperty("errors").EnumerateArray(), e => Assert.Equal(["row", "column", "code", "message"], e.EnumerateObject().Select(p => p.Name)));

        using var stored = await _client.GetAsync($"/api/v1/ingestions/{batch.GetProperty("batchId").GetString()}", tenant);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(text, await stored.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task File_level_errors_have_null_row_and_column()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var empty = await _client.UploadAsync(tenant, "");
        using var badBytes = await _client.UploadAsync(tenant, [.. Encoding.UTF8.GetBytes(Header), 0xFF, 0xFE]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal([(null, null, "EMPTY_FILE")], (await empty.JsonAsync()).Errors());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badBytes.StatusCode);
        Assert.Equal([(null, null, "INVALID_ENCODING")], (await badBytes.JsonAsync()).Errors());
        Assert.Equal(2, await api.CountBatchesAsync(tenant));
    }

    [Fact]
    public async Task The_response_holds_the_first_100_errors_and_the_full_count()
    {
        var tenant = await api.NewTenantAsync(_client);
        var csv = new StringBuilder(Header);
        for (var i = 0; i < 130; i++)
        {
            csv.Append("E-1,BASE_SALARY,2026-01-01,x,EUR\n");
        }

        using var response = await _client.UploadAsync(tenant, csv.ToString());

        var batch = await response.JsonAsync();
        Assert.Equal(130, batch.GetProperty("errorCount").GetInt32());
        Assert.Equal(Enumerable.Range(2, 100).Select(row => ((int?)row, (string?)"amount", "INVALID_AMOUNT")), batch.Errors());
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("123")]
    [InlineData("0193f1c2")]
    public async Task A_batch_id_that_is_not_a_uuid_or_does_not_exist_is_404(string batchId)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var notUuid = await _client.GetAsync($"/api/v1/ingestions/{batchId}", tenant);
        using var missing = await _client.GetAsync($"/api/v1/ingestions/{Guid.CreateVersion7()}", tenant);

        await notUuid.AssertProblemAsync(HttpStatusCode.NotFound);
        await missing.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- as-of ----------

    [Fact]
    public async Task As_of_defaults_to_today_in_utc()
    {
        var tenant = await TenantWithA1Async();
        var before = DateOnly.FromDateTime(DateTime.UtcNow);

        using var response = await _client.GetAsync(EmployeeUrl, tenant);

        var after = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Contains(body.GetProperty("asOf").GetString(), new[] { before.ToString("yyyy-MM-dd"), after.ToString("yyyy-MM-dd") });
        Assert.Equal(["employeeId", "asOf", "components"], body.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task As_of_returns_one_value_per_component_ordered_by_code_with_json_numbers_and_inclusive_end()
    {
        var tenant = await api.NewTenantAsync(_client);
        using var upload = await _client.UploadAsync(
            tenant,
            Header
            + "E-7,BONUS_TARGET_PCT,2026-01-01,12.5,\n"
            + "E-7,BASE_SALARY,2026-01-01,1000.10,EUR\n"
            + "E-7,ALLOWANCE,2026-01-01,300,USD\n"
            + "E-7,ALLOWANCE,2026-03-01,350,USD\n"
            + "E-8,BASE_SALARY,2026-01-01,9,GBP\n");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        using var response = await _client.GetAsync("/api/v1/employees/E-7/compensation?asOf=2026-02-28&other=1", tenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(
            [
                ("ALLOWANCE", 300m, "USD", "2026-01-01", "2026-02-28"),
                ("BASE_SALARY", 1000.10m, "EUR", "2026-01-01", null),
                ("BONUS_TARGET_PCT", 12.5m, null, "2026-01-01", null),
            ],
            body.GetProperty("components").Periods());
        var first = body.GetProperty("components")[0];
        Assert.Equal(["component", "amount", "currency", "effectiveFrom", "effectiveTo"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Number, first.GetProperty("amount").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("components")[2].GetProperty("currency").ValueKind);

        // The day after the period's inclusive end, the next value is in effect.
        using var nextDay = await _client.GetAsync("/api/v1/employees/E-7/compensation?asOf=2026-03-01", tenant);
        Assert.Equal(("ALLOWANCE", 350m, "USD", "2026-03-01", null), (await nextDay.JsonAsync()).GetProperty("components").Periods()[0]);
    }

    [Fact]
    public async Task As_of_uses_only_current_values_after_a_correction()
    {
        var tenant = await TenantWithA1Async();
        using var correction = await _client.UploadAsync(tenant, Header + "E-1001,BASE_SALARY,2026-01-01,51000,USD\n");
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);

        using var asOf = await _client.GetAsync(EmployeeUrl + "?asOf=2026-02-01", tenant);
        using var history = await _client.GetAsync(EmployeeUrl + "/history?component=BASE_SALARY", tenant);

        Assert.Equal(("BASE_SALARY", 51000m, "USD", "2026-01-01", "2026-06-30"), (await asOf.JsonAsync()).GetProperty("components").Periods()[0]);
        Assert.Equal(
            [("BASE_SALARY", 51000m, "USD", "2026-01-01", "2026-06-30"), ("BASE_SALARY", 55000m, "EUR", "2026-07-01", null)],
            (await history.JsonAsync()).GetProperty("periods").Periods());
    }

    [Theory]
    [InlineData("asOf=1900-01-01", HttpStatusCode.OK)]
    [InlineData("asOf=2999-12-31", HttpStatusCode.OK)]
    [InlineData("asOf=2024-02-29", HttpStatusCode.OK)]
    [InlineData("asOf=", HttpStatusCode.BadRequest)]
    [InlineData("asOf", HttpStatusCode.BadRequest)]
    [InlineData("asOf=%202031-01-01", HttpStatusCode.BadRequest)] // not trimmed
    [InlineData("asOf=2031-01-01%20", HttpStatusCode.BadRequest)]
    [InlineData("asOf=2031-01-01%0A", HttpStatusCode.BadRequest)]
    [InlineData("asOf=1899-12-31", HttpStatusCode.BadRequest)]
    [InlineData("asOf=3000-01-01", HttpStatusCode.BadRequest)]
    [InlineData("asOf=0000-01-01", HttpStatusCode.BadRequest)]
    [InlineData("asOf=2026-02-30", HttpStatusCode.BadRequest)]
    [InlineData("asOf=2026-1-1", HttpStatusCode.BadRequest)]
    [InlineData("asOf=2026-01-01T00:00:00", HttpStatusCode.BadRequest)]
    [InlineData("asOf=01/02/2026", HttpStatusCode.BadRequest)]
    [InlineData("asOf=today", HttpStatusCode.BadRequest)]
    [InlineData("asOf=2026-01-01&asOf=2026-01-01", HttpStatusCode.BadRequest)] // repeated, even when equal
    [InlineData("asOf=2026-01-01&asOf=", HttpStatusCode.BadRequest)]
    public async Task As_of_validation(string query, HttpStatusCode expected)
    {
        var tenant = await TenantWithA1Async();

        using var response = await _client.GetAsync($"{EmployeeUrl}?{query}", tenant);
        using var unknownEmployee = await _client.GetAsync($"/api/v1/employees/NOBODY/compensation?{query}", tenant);
        using var noTenant = await _client.GetAsync($"{EmployeeUrl}?{query}", tenant: null);

        // Order of checks: tenant (401), then the query (400), then the lookup (404).
        Assert.Equal(expected, response.StatusCode);
        await noTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);
        if (expected == HttpStatusCode.BadRequest)
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest);
            await unknownEmployee.AssertProblemAsync(HttpStatusCode.BadRequest);
        }
        else
        {
            await unknownEmployee.AssertProblemAsync(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Employee_ids_are_case_sensitive_in_lookups()
    {
        var tenant = await TenantWithA1Async();

        using var lower = await _client.GetAsync("/api/v1/employees/e-1001/compensation", tenant);
        using var lowerHistory = await _client.GetAsync("/api/v1/employees/e-1001/compensation/history", tenant);

        await lower.AssertProblemAsync(HttpStatusCode.NotFound);
        await lowerHistory.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- history ----------

    [Fact]
    public async Task History_lists_all_current_periods_by_component_then_start()
    {
        var tenant = await TenantWithA1Async();
        using var more = await _client.UploadAsync(tenant, Header + "E-1001,ALLOWANCE,2027-01-01,5,EUR\nE-1001,ALLOWANCE,2025-01-01,4,EUR\n");
        Assert.Equal(HttpStatusCode.Created, more.StatusCode);

        using var response = await _client.GetAsync(EmployeeUrl + "/history", tenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(["employeeId", "periods"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("E-1001", body.GetProperty("employeeId").GetString());
        Assert.Equal(
            [
                ("ALLOWANCE", 4m, "EUR", "2025-01-01", "2026-12-31"),
                ("ALLOWANCE", 5m, "EUR", "2027-01-01", null),
                ("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"),
                ("BASE_SALARY", 55000m, "EUR", "2026-07-01", null),
                ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null),
            ],
            body.GetProperty("periods").Periods());
        Assert.Equal(["component", "amount", "currency", "effectiveFrom", "effectiveTo"], body.GetProperty("periods")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task History_for_a_known_component_without_values_is_200_with_empty_periods()
    {
        var tenant = await TenantWithA1Async();

        using var response = await _client.GetAsync(EmployeeUrl + "/history?component=ALLOWANCE", tenant);
        using var unknownEmployee = await _client.GetAsync("/api/v1/employees/NOBODY/compensation/history?component=ALLOWANCE", tenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.JsonAsync()).GetProperty("periods").GetArrayLength());
        await unknownEmployee.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("component=")]
    [InlineData("component")]
    [InlineData("component=base_salary")]
    [InlineData("component=BASE_SALARY%20")]
    [InlineData("component=SALARY")]
    [InlineData("component=BASE_SALARY&component=BASE_SALARY")] // repeated, even when equal
    [InlineData("component=BASE_SALARY&component=ALLOWANCE")]
    public async Task History_component_validation_comes_after_the_tenant_and_before_the_lookup(string query)
    {
        var tenant = await TenantWithA1Async();

        using var response = await _client.GetAsync($"{EmployeeUrl}/history?{query}", tenant);
        using var unknownEmployee = await _client.GetAsync($"/api/v1/employees/NOBODY/compensation/history?{query}", tenant);
        using var noTenant = await _client.GetAsync($"{EmployeeUrl}/history?{query}", tenant: null);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
        await unknownEmployee.AssertProblemAsync(HttpStatusCode.BadRequest);
        await noTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    // ---------- unknown routes and wrong methods ----------

    [Theory]
    [InlineData("/")]
    [InlineData("/nope")]
    [InlineData("/api/v1")]
    [InlineData("/api/v1/employees")]
    [InlineData("/api/v1/employees/E-1001")]
    [InlineData("/api/v1/employees/E-1001/compensation/history/extra")]
    [InlineData("/api/v2/ingestions")]
    public async Task Unknown_routes_are_404_problem_details_with_or_without_a_tenant(string url)
    {
        var tenant = await api.NewTenantAsync(_client);

        using var noHeader = await SendWithTenantHeaderAsync(HttpMethod.Get, url, null);
        using var badHeader = await SendWithTenantHeaderAsync(HttpMethod.Get, url, "garbage");
        using var validHeader = await _client.GetAsync(url, tenant);

        await noHeader.AssertProblemAsync(HttpStatusCode.NotFound);
        await badHeader.AssertProblemAsync(HttpStatusCode.NotFound);
        await validHeader.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("DELETE", "/api/v1/tenants", "POST")]
    [InlineData("GET", "/api/v1/tenants", "POST")]
    [InlineData("GET", "/api/v1/ingestions", "POST")]
    [InlineData("PUT", "/api/v1/ingestions", "POST")]
    [InlineData("POST", "/api/v1/ingestions/0193f1c2-0000-7000-8000-000000000000", "GET")]
    [InlineData("POST", "/api/v1/employees/E-1001/compensation", "GET")]
    [InlineData("DELETE", "/api/v1/employees/E-1001/compensation/history", "GET")]
    [InlineData("POST", "/health", "GET")]
    public async Task A_known_path_with_the_wrong_method_is_405_with_allow_and_no_tenant_check(string method, string url, string allowed)
    {
        using var noHeader = await SendWithTenantHeaderAsync(new HttpMethod(method), url, null);
        using var badHeader = await SendWithTenantHeaderAsync(new HttpMethod(method), url, "garbage");

        foreach (var response in new[] { noHeader, badHeader })
        {
            await response.AssertProblemAsync(HttpStatusCode.MethodNotAllowed);
            Assert.Contains(allowed, response.Content.Headers.Allow);
        }
    }

    // ---------- logging ----------

    [Fact]
    public async Task Each_batch_writes_exactly_one_log_entry_with_ids_and_counts_only()
    {
        var tenant = await TenantWithA1Async();
        using var appliedResponse = await _client.UploadAsync(tenant, Header + "E-1001,BASE_SALARY,2026-01-01,51234.5678,EUR\nE-77,ALLOWANCE,2026-01-01,4321.1234,CHF\n");
        using var rejectedResponse = await _client.UploadAsync(tenant, Header + "E-88,BASE_SALARY,2026-01-01,98765.4321,EUR\nE-89,BASE_SALARY,2026-01-01,-5,EUR\n");
        var applied = await appliedResponse.JsonAsync();
        var rejected = await rejectedResponse.JsonAsync();

        foreach (var (batch, status, counts, errorCount) in new[]
                 {
                     (applied, "Applied", (Total: 2, Inserted: 1, Corrected: 1, Unchanged: 0), 0),
                     (rejected, "Rejected", (Total: 2, Inserted: 0, Corrected: 0, Unchanged: 0), 1),
                 })
        {
            var batchId = batch.GetProperty("batchId").GetString()!;
            var entry = Assert.Single(api.Logs.Entries, e => e.State.Any(p => p.Key == "BatchId" && Equals(p.Value, batchId)));
            var state = entry.State.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value);

            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Equal(
                ["BatchId", "CorrectedRows", "DurationMs", "ErrorCount", "InsertedRows", "Status", "TenantId", "TotalRows", "UnchangedRows"],
                state.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(tenant.ToString("D"), Assert.IsType<string>(state["TenantId"]));
            Assert.Equal(batchId, Assert.IsType<string>(state["BatchId"]));
            Assert.Equal(batchId.ToLowerInvariant(), batchId);
            Assert.Equal(status, Assert.IsType<string>(state["Status"]));
            Assert.Equal(counts.Total, Assert.IsType<int>(state["TotalRows"]));
            Assert.Equal(counts.Inserted, Assert.IsType<int>(state["InsertedRows"]));
            Assert.Equal(counts.Corrected, Assert.IsType<int>(state["CorrectedRows"]));
            Assert.Equal(counts.Unchanged, Assert.IsType<int>(state["UnchangedRows"]));
            Assert.Equal(errorCount, Assert.IsType<int>(state["ErrorCount"]));
            Assert.InRange(Assert.IsType<long>(state["DurationMs"]), 0L, 60_000L);
        }

        // Nothing the app logged for this tenant's uploads carries row contents: no employee id, no amount.
        var everything = string.Join('\n', api.Logs.Entries.Select(e => e.Message + " " + string.Join(' ', e.State.Select(p => $"{p.Key}={p.Value}"))));
        foreach (var secret in new[] { "51234", "4321.1234", "98765", "E-77", "E-88", "E-89", "CHF" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Requests_refused_before_ingestion_write_no_batch_log_entry()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var unsupported = await _client.UploadAsync(tenant, A1, "text/plain");
        using var unauthorized = await _client.UploadAsync(Guid.CreateVersion7(), A1);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain(api.Logs.Entries, e => e.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D"))));
    }

    [Fact]
    public void Console_logging_is_json_by_default()
    {
        var configuration = api.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("json", configuration["Logging:Console:FormatterName"]);
    }
}

/// <summary>With <c>TenantAdminApi:Enabled</c> off, the tenant path is unknown.</summary>
public sealed class TenantAdminDisabledTests(TenantAdminDisabledFactory api) : IClassFixture<TenantAdminDisabledFactory>
{
    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Any_method_on_the_tenant_path_is_404(string method)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/tenants");
        if (method == "POST")
        {
            request.Content = new StringContent("""{"name":"Acme"}""", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await response.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tenant_endpoints_still_work()
    {
        using var client = api.CreateClient();
        var tenant = await api.Database.CreateTenantAsync();

        using var response = await client.UploadAsync(tenant, ApiFactory.A1);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

/// <summary><c>Ingestion:MaxBodyBytes</c> and the declared-size check.</summary>
public sealed class BodyLimitTests(SmallBodyLimitFactory api) : IClassFixture<SmallBodyLimitFactory>
{
    private static byte[] CsvOfLength(int length)
    {
        var csv = ApiFactory.Header + "E-1,BASE_SALARY,2026-01-01,1,EUR\n";
        return Encoding.UTF8.GetBytes(csv + new string('\n', length - csv.Length));
    }

    [Fact]
    public async Task A_body_at_the_limit_is_accepted()
    {
        using var client = api.CreateClient();
        var tenant = await api.Database.CreateTenantAsync();

        using var response = await client.UploadAsync(tenant, CsvOfLength(SmallBodyLimitFactory.Limit));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_413_and_leaves_no_batch()
    {
        using var client = api.CreateClient();
        var tenant = await api.Database.CreateTenantAsync();

        using var response = await client.UploadAsync(tenant, CsvOfLength(SmallBodyLimitFactory.Limit + 1));

        await response.AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(0, await api.CountBatchesAsync(tenant));
    }

    [Fact]
    public async Task Tenant_then_content_type_then_size()
    {
        using var client = api.CreateClient();
        var tenant = await api.Database.CreateTenantAsync();
        var tooLarge = CsvOfLength(SmallBodyLimitFactory.Limit + 1);

        using var noTenant = await client.UploadAsync(tenant: null, tooLarge, "application/json");
        using var wrongType = await client.UploadAsync(tenant, tooLarge, "application/json");

        await noTenant.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await wrongType.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(0, await api.CountBatchesAsync(tenant));
    }
}

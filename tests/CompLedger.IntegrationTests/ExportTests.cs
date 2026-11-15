using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CompLedger.IntegrationTests;

/// <summary>PRP-004 T3: the two export endpoints (3.2 to 3.6; A1 to A8).</summary>
[Collection(ExportCollection.Name)]
public sealed class ExportTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string H = "employee_id,component,effective_from,effective_to,amount,currency\n";
    private const string Export = "/api/v1/compensation/export";
    private const string Snapshots = "/api/v1/snapshots";

    /// <summary>A1's upload.</summary>
    private const string A1Rows = Header
        + "E-1001,BASE_SALARY,2026-01-01,50000,EUR\n"
        + "E-1001,BASE_SALARY,2026-04-01,52000.50,EUR\n"
        + "E-1001,BASE_SALARY,2026-07-01,54000,EUR\n"
        + "E-1001,BONUS_TARGET_PCT,2026-01-01,10,\n"
        + "E-1001,ALLOWANCE,2026-01-01,6000.0000,EUR\n"
        + "E-1002,BASE_SALARY,2025-07-01,48000,USD\n";

    /// <summary>The file of 3.5: A1 as of 2026-05-15.</summary>
    private const string A1File = H
        + "E-1001,ALLOWANCE,2026-01-01,,6000,EUR\n"
        + "E-1001,BASE_SALARY,2026-04-01,2026-06-30,52000.5,EUR\n"
        + "E-1001,BONUS_TARGET_PCT,2026-01-01,,10,\n"
        + "E-1002,BASE_SALARY,2025-07-01,,48000,USD\n";

    /// <summary>A text unique to the streaming statement, to find its backend.</summary>
    private const string ExportStatementMarker = "/* comp-ledger export rows */";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ExportFile(HttpStatusCode Status, string? AsOf, string? KnownAt, string Body, byte[] Bytes);

    /// <summary>Sends the request and returns as soon as the headers are in, without reading the body.</summary>
    private Task<HttpResponseMessage> OpenAsync(string url, Guid? tenant, string method = "GET", string? accept = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (tenant is { } id)
        {
            request.Headers.Add("X-Tenant-Id", id.ToString("D"));
        }

        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    private static string? Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) ? Assert.Single(values) : null;

    private async Task<ExportFile> GetFileAsync(Guid tenant, string url, string? accept = null)
    {
        using var response = await OpenAsync(url, tenant, accept: accept);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        return new ExportFile(response.StatusCode, Single(response, "X-As-Of"), Single(response, "X-Known-At"), Encoding.UTF8.GetString(bytes), bytes);
    }

    /// <summary>A 200 with the file headers of 3.5; returns the file.</summary>
    private async Task<ExportFile> OkFileAsync(Guid tenant, string url, string? accept = null)
    {
        using var response = await OpenAsync(url, tenant, accept: accept);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Null(response.Content.Headers.ContentLength);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        var asOf = Single(response, "X-As-Of");
        var knownAt = Single(response, "X-Known-At");
        Assert.Matches(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", asOf);
        Assert.Matches(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z\z", knownAt);

        // UTF-8 without a BOM, LF endings, never a CR, a header line, and a line break after the last line.
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "The file must not start with a BOM.");
        Assert.DoesNotContain((byte)'\r', bytes);
        var body = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith(H, body, StringComparison.Ordinal);
        Assert.EndsWith("\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", body, StringComparison.Ordinal);
        return new ExportFile(response.StatusCode, asOf, knownAt, body, bytes);
    }

    private async Task<JsonElement> CreateSnapshotAsync(Guid tenant, string name, string asOf)
    {
        using var response = await _client.SendAsync(
            HttpMethod.Post, Snapshots, tenant, new StringContent(JsonSerializer.Serialize(new { name, asOf }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string SnapshotExport(JsonElement snapshot) => $"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()}/export";

    // ---------- A1, A2: the file ----------

    [Fact]
    public async Task A1_a_small_export_is_the_file_of_3_5_with_its_headers()
    {
        var tenant = await api.NewTenantAsync(_client);
        var appliedAt = await _client.AppliedAtAsync(tenant, A1Rows);

        var file = await OkFileAsync(tenant, Export + "?asOf=2026-05-15");

        Assert.Equal(A1File, file.Body);
        Assert.Equal(Encoding.UTF8.GetBytes(A1File), file.Bytes);
        Assert.Equal("2026-05-15", file.AsOf);
        Assert.True(string.CompareOrdinal(appliedAt, file.KnownAt) <= 0, $"X-Known-At {file.KnownAt} is before appliedAt {appliedAt}.");
    }

    [Fact]
    public async Task A2_rows_are_in_ordinal_order_of_employee_id()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Header
            + "a-1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E-9,BASE_SALARY,2026-01-01,1,EUR\n"
            + "Z-1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E-10,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E.1,BASE_SALARY,2026-01-01,1,EUR\n");

        var file = await OkFileAsync(tenant, Export + "?asOf=2026-02-01");

        Assert.Equal(
            H
            + "E-10,BASE_SALARY,2026-01-01,,1,EUR\n"
            + "E-9,BASE_SALARY,2026-01-01,,1,EUR\n"
            + "E.1,BASE_SALARY,2026-01-01,,1,EUR\n"
            + "Z-1,BASE_SALARY,2026-01-01,,1,EUR\n"
            + "a-1,BASE_SALARY,2026-01-01,,1,EUR\n",
            file.Body);
    }

    [Fact]
    public async Task Amounts_are_written_in_their_shortest_form()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Header
            + "E-1,BASE_SALARY,2026-01-01,50000.0000,EUR\n"
            + "E-2,BASE_SALARY,2026-01-01,52000.5000,EUR\n"
            + "E-3,BASE_SALARY,2026-01-01,0.0001,EUR\n"
            + "E-4,BASE_SALARY,2026-01-01,0,EUR\n"
            + "E-5,BASE_SALARY,2026-01-01,999999999999999.9999,EUR\n"
            + "E-6,BONUS_TARGET_PCT,2026-01-01,500.0,\n"
            + "E-7,ALLOWANCE,2026-01-01,1234.5600,JPY\n");

        var file = await OkFileAsync(tenant, Export + "?asOf=2026-01-01");

        Assert.Equal(
            H
            + "E-1,BASE_SALARY,2026-01-01,,50000,EUR\n"
            + "E-2,BASE_SALARY,2026-01-01,,52000.5,EUR\n"
            + "E-3,BASE_SALARY,2026-01-01,,0.0001,EUR\n"
            + "E-4,BASE_SALARY,2026-01-01,,0,EUR\n"
            + "E-5,BASE_SALARY,2026-01-01,,999999999999999.9999,EUR\n"
            + "E-6,BONUS_TARGET_PCT,2026-01-01,,500,\n"
            + "E-7,ALLOWANCE,2026-01-01,,1234.56,JPY\n",
            file.Body);
    }

    [Fact]
    public async Task Without_asOf_the_export_is_as_of_today_utc_and_says_so()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Header + "E-1,BASE_SALARY,2020-01-01,1,EUR\n");

        var before = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var file = await OkFileAsync(tenant, Export);
        var after = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Assert.Contains(file.AsOf, new[] { before, after });
        Assert.Equal(H + "E-1,BASE_SALARY,2020-01-01,,1,EUR\n", file.Body);
        Assert.Equal(file.Body, (await OkFileAsync(tenant, Export + $"?asOf={file.AsOf}&knownAt={file.KnownAt}")).Body);
    }

    [Fact]
    public async Task A_given_knownAt_is_echoed_with_six_fractional_digits()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);

        var seconds = await OkFileAsync(tenant, Export + "?asOf=2026-05-15&knownAt=2020-01-01T00:00:09Z");
        var fraction = await OkFileAsync(tenant, Export + "?asOf=2026-05-15&knownAt=2020-01-01T00:00:09.5Z");

        Assert.Equal("2020-01-01T00:00:09.000000Z", seconds.KnownAt);
        Assert.Equal("2020-01-01T00:00:09.500000Z", fraction.KnownAt);
        Assert.Equal(H, seconds.Body);
    }

    [Fact]
    public async Task Accept_other_query_parameters_and_include_are_ignored()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);
        var plain = await OkFileAsync(tenant, Export + "?asOf=2026-05-15");

        foreach (var query in new[] { "&include=computed", "&include=nonsense&include=", "&foo=bar&page=2", "&employeeId=E-1002" })
        {
            var file = await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={plain.KnownAt}" + query);
            Assert.Equal(A1File, file.Body);
        }

        Assert.Equal(A1File, (await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={plain.KnownAt}", accept: "application/json")).Body);
        Assert.Equal(A1File, (await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={plain.KnownAt}", accept: "text/html")).Body);
    }

    // ---------- A3: the same file again ----------

    [Fact]
    public async Task A3_the_returned_instant_gives_the_same_file_byte_for_byte()
    {
        var tenant = await api.NewTenantAsync(_client);
        var appliedAt = await _client.AppliedAtAsync(tenant, A1Rows);

        var first = await OkFileAsync(tenant, Export + "?asOf=2026-05-15");
        await _client.AppliedAtAsync(tenant, Header + "E-1001,BONUS_TARGET_PCT,2026-01-01,15,\n");

        var again = await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={first.KnownAt}");
        Assert.Equal(first.Bytes, again.Bytes);
        Assert.Equal(first.KnownAt, again.KnownAt);
        Assert.Equal("2026-05-15", again.AsOf);

        var now = await OkFileAsync(tenant, Export + "?asOf=2026-05-15");
        Assert.Contains("E-1001,BONUS_TARGET_PCT,2026-01-01,,15,\n", now.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("E-1001,BONUS_TARGET_PCT,2026-01-01,,10,\n", now.Body, StringComparison.Ordinal);

        var beforeA1 = await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={Instants.Shift(appliedAt, -1)}");
        Assert.Equal(H, beforeA1.Body);

        var atA1 = await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={appliedAt}");
        Assert.Equal(A1File, atA1.Body);
    }

    // ---------- A4: snapshots ----------

    [Fact]
    public async Task A4_a_snapshot_export_is_the_export_at_the_snapshots_pair()
    {
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);
        var snapshot = await CreateSnapshotAsync(tenant, "Review", "2026-05-15");
        await _client.AppliedAtAsync(tenant, Header + "E-1001,ALLOWANCE,2026-01-01,7000,EUR\nE-1003,BASE_SALARY,2026-01-01,1,EUR\n");

        var knownAt = snapshot.GetProperty("knownAt").GetString();
        var file = await OkFileAsync(tenant, SnapshotExport(snapshot));
        var direct = await OkFileAsync(tenant, Export + $"?asOf=2026-05-15&knownAt={knownAt}");

        Assert.Equal(direct.Bytes, file.Bytes);
        Assert.Equal(A1File, file.Body);
        Assert.Equal("2026-05-15", file.AsOf);
        Assert.Equal(knownAt, file.KnownAt);

        // The id in upper case is the same snapshot; include is ignored.
        var upper = $"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()!.ToUpperInvariant()}/export";
        Assert.Equal(A1File, (await OkFileAsync(tenant, upper)).Body);
        Assert.Equal(A1File, (await OkFileAsync(tenant, SnapshotExport(snapshot) + "?include=computed")).Body);

        // Each employee's rows are the snapshot read of that employee.
        foreach (var employee in new[] { "E-1001", "E-1002" })
        {
            using var read = await _client.GetAsync($"{Snapshots}/{snapshot.GetProperty("snapshotId").GetString()}/employees/{employee}/compensation", tenant);
            var periods = (await read.JsonAsync()).GetProperty("components").Periods();
            var lines = file.Body.Split('\n').Where(l => l.StartsWith(employee + ",", StringComparison.Ordinal)).ToList();
            Assert.Equal(
                periods.Select(p => $"{employee},{p.Component},{p.From},{p.To},{p.Amount.ToString("0.####", CultureInfo.InvariantCulture)},{p.Currency}"),
                lines);
        }

        foreach (var query in new[] { "?asOf=2026-01-01", "?knownAt=", "?asOf=", "?asOf=2026-05-15", $"?knownAt={knownAt}", "?asOf=x&knownAt=y" })
        {
            using var refused = await _client.GetAsync(SnapshotExport(snapshot) + query, tenant);
            await refused.AssertProblemAsync(HttpStatusCode.BadRequest);
        }

        foreach (var id in new[] { Guid.CreateVersion7().ToString("D"), "not-a-uuid", Guid.CreateVersion7().ToString("N"), "{" + Guid.CreateVersion7() + "}" })
        {
            using var missing = await _client.GetAsync($"{Snapshots}/{id}/export", tenant);
            await missing.AssertProblemAsync(HttpStatusCode.NotFound);
        }

        using var foreign = await _client.GetAsync(SnapshotExport(snapshot), other);
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound);
    }

    // ---------- A5: validation and empty exports ----------

    [Theory]
    [InlineData("asOf=2026-02-30")]
    [InlineData("asOf=")]
    [InlineData("asOf=%202026-01-01")]
    [InlineData("asOf=2026-01-01%20")]
    [InlineData("asOf=2026-1-1")]
    [InlineData("asOf=1899-12-31")]
    [InlineData("asOf=3000-01-01")]
    [InlineData("asOf=2026-01-01&asOf=2026-01-01")]
    [InlineData("asOf=2026-01-01&asOf=2026-02-01")]
    [InlineData("knownAt=2026-11-13T10:00:00+01:00")]
    [InlineData("knownAt=2026-11-13T10:00:00%2B01:00")]
    [InlineData("knownAt=")]
    [InlineData("knownAt=%202026-11-13T10:00:00Z")]
    [InlineData("knownAt=2026-11-13T10:00:00.1234567Z")]
    [InlineData("knownAt=2026-11-13")]
    [InlineData("knownAt=1899-12-31T23:59:59.999999Z")]
    [InlineData("knownAt=2026-11-13T10:00:00Z&knownAt=2026-11-13T10:00:00Z")]
    [InlineData("knownAt=9999-12-31T00:00:00Z")]
    public async Task A5_an_invalid_or_repeated_parameter_is_400(string query)
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);

        using var response = await _client.GetAsync($"{Export}?{query}", tenant);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A5_knownAt_after_the_request_time_is_400()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);
        var hourLater = Instants.Shift(await Instants.DatabaseNowAsync(api.Database), 3_600_000_000);
        var secondLater = Instants.Shift(await Instants.DatabaseNowAsync(api.Database), 1_000_000);

        using var hour = await _client.GetAsync($"{Export}?asOf=2026-05-15&knownAt={hourLater}", tenant);
        using var second = await _client.GetAsync($"{Export}?knownAt={secondLater}", tenant);

        await hour.AssertProblemAsync(HttpStatusCode.BadRequest);
        await second.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A5_an_empty_tenant_and_a_date_before_every_value_give_the_header_only()
    {
        var empty = await api.NewTenantAsync(_client);
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);

        Assert.Equal(H, (await OkFileAsync(empty, Export)).Body);
        Assert.Equal(H, (await OkFileAsync(empty, Export + "?asOf=2026-05-15")).Body);
        Assert.Equal(H, (await OkFileAsync(tenant, Export + "?asOf=2025-06-30")).Body);
        Assert.Equal(H, (await OkFileAsync(tenant, Export + "?asOf=1900-01-01")).Body);
        Assert.Equal(H + "E-1002,BASE_SALARY,2025-07-01,,48000,USD\n", (await OkFileAsync(tenant, Export + "?asOf=2025-07-01")).Body);
    }

    // ---------- 3.3: order of checks, 401, 405 ----------

    [Fact]
    public async Task The_tenant_is_checked_before_the_query_and_the_query_before_the_snapshot()
    {
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);
        var snapshot = await CreateSnapshotAsync(tenant, "Order", "2026-05-15");

        foreach (Guid? caller in new Guid?[] { null, Guid.CreateVersion7() })
        {
            foreach (var url in new[] { $"{Export}?asOf=bad", Export, $"{Snapshots}/not-a-uuid/export?asOf=x", SnapshotExport(snapshot) })
            {
                using var unauthorized = await _client.GetAsync(url, caller);
                await unauthorized.AssertProblemAsync(HttpStatusCode.Unauthorized);
            }
        }

        // 400 before 404: refused parameters on a malformed, unknown or foreign snapshot.
        foreach (var url in new[] { $"{Snapshots}/not-a-uuid/export?asOf=2026-01-01", $"{Snapshots}/{Guid.CreateVersion7()}/export?knownAt=" })
        {
            using var refused = await _client.GetAsync(url, tenant);
            await refused.AssertProblemAsync(HttpStatusCode.BadRequest);
        }

        using var foreignRefused = await _client.GetAsync(SnapshotExport(snapshot) + "?asOf=2026-01-01", other);
        await foreignRefused.AssertProblemAsync(HttpStatusCode.BadRequest);

        // An invalid asOf is refused before the knownAt bound is checked, and both are 400.
        using var both = await _client.GetAsync($"{Export}?asOf=bad&knownAt=9999-01-01T00:00:00Z", tenant);
        await both.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("POST", "/api/v1/compensation/export")]
    [InlineData("PUT", "/api/v1/compensation/export")]
    [InlineData("DELETE", "/api/v1/compensation/export?asOf=2026-01-01")]
    [InlineData("POST", "/api/v1/snapshots/0193f1c2-0000-7000-8000-000000000000/export")]
    [InlineData("DELETE", "/api/v1/snapshots/not-a-uuid/export")]
    [InlineData("PATCH", "/api/v1/snapshots/anything/export?asOf=x")]
    public async Task A_wrong_method_on_an_export_path_is_405_with_allow_get(string method, string url)
    {
        var tenant = await api.NewTenantAsync(_client);

        foreach (Guid? caller in new Guid?[] { null, Guid.CreateVersion7(), tenant })
        {
            using var response = await _client.SendAsync(new HttpMethod(method), url, caller);
            await response.AssertProblemAsync(HttpStatusCode.MethodNotAllowed);
            Assert.Contains("GET", response.Content.Headers.Allow);
        }
    }

    // ---------- A6: isolation ----------

    [Fact]
    public async Task A6_each_tenant_exports_only_its_own_values()
    {
        var t1 = await api.NewTenantAsync(_client);
        var t2 = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(t1, A1Rows);
        await _client.AppliedAtAsync(t2, Header + "a-1,BASE_SALARY,2026-01-01,1,EUR\nE-9,BASE_SALARY,2026-01-01,1,EUR\n");

        var first = await OkFileAsync(t1, Export + "?asOf=2026-05-15");
        var second = await OkFileAsync(t2, Export + "?asOf=2026-05-15");

        Assert.Equal(A1File, first.Body);
        Assert.Equal(H + "E-9,BASE_SALARY,2026-01-01,,1,EUR\na-1,BASE_SALARY,2026-01-01,,1,EUR\n", second.Body);
    }

    // ---------- A7: a slow download does not hold up uploads ----------

    [Fact]
    public async Task A7_a_client_that_stops_reading_does_not_hold_up_an_upload_of_the_same_tenant()
    {
        const int rows = 100_000;
        var tenant = await api.NewTenantAsync(_client);
        var csv = new StringBuilder(Header, rows * 44);
        for (var i = 1; i <= rows; i++)
        {
            csv.Append("E-").Append(i.ToString("D6", CultureInfo.InvariantCulture)).Append(",BASE_SALARY,2026-01-01,").Append(30000 + i).Append(",EUR\n");
        }

        await _client.AppliedAtAsync(tenant, csv.ToString());

        using var response = await OpenAsync(Export + "?asOf=2026-02-01", tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64);
        Assert.Equal(H.TrimEnd('\n'), await reader.ReadLineAsync(Ct));

        // The client has read the header line and stops: the upload still takes the lock, applies and commits.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using (var upload = await _client.UploadAsync(tenant, Header + "E-9999999,BASE_SALARY,2026-01-01,1,EUR\n").WaitAsync(TimeSpan.FromSeconds(10), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        var count = 0;
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            Assert.False(line.StartsWith("E-9999999,", StringComparison.Ordinal), "A batch committed during the download is in the file.");
            count++;
        }

        Assert.Equal(rows, count);
    }

    // ---------- A8: waiting for a batch being applied ----------

    [Fact]
    public async Task A8_an_export_waits_for_a_one_million_row_batch_of_its_tenant_and_another_tenant_answers_at_once()
    {
        const int rows = 1_000_000;
        var tenant = await api.NewTenantAsync(_client);
        var other = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(other, Header + "E-1,BASE_SALARY,2026-01-01,70000,EUR\n");

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
        async Task<bool> LockHeldAsync() => await monitor.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM pg_locks
              WHERE locktype = 'advisory' AND mode = 'ExclusiveLock' AND granted AND objsubid = 1
                AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@tenant, 0))
            """,
            new { tenant = tenant.ToString("D") });

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!await LockHeldAsync())
        {
            Assert.False(upload.IsCompleted, "The upload finished before its lock was seen.");
            Assert.True(DateTime.UtcNow < deadline, "The upload never took its lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(2), Ct);
        }

        var waiting = OpenAsync(Export + "?asOf=2026-03-01", tenant);

        // The other tenant answers while the batch is still being applied.
        using (var otherExport = await OpenAsync(Export + "?asOf=2026-03-01", other).WaitAsync(TimeSpan.FromSeconds(20), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, otherExport.StatusCode);
            Assert.Equal(H + "E-1,BASE_SALARY,2026-01-01,,70000,EUR\n", await otherExport.Content.ReadAsStringAsync(Ct));
            Assert.False(upload.IsCompleted, "The batch was expected to be still applying when the other tenant got its file.");
        }

        // No first byte while the lock is held: sampled in this order, an answer seen before a held lock is a failure.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        var answered = waiting.IsCompleted;
        Assert.False(answered && await LockHeldAsync(), "The export sent its headers while the batch was being applied.");

        using var uploaded = await upload.WaitAsync(TimeSpan.FromMinutes(3), Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var appliedAt = (await uploaded.JsonAsync()).GetProperty("appliedAt").GetString();

        using var response = await waiting.WaitAsync(TimeSpan.FromMinutes(3), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var knownAt = Single(response, "X-Known-At");
        Assert.True(string.CompareOrdinal(appliedAt, knownAt) <= 0, $"X-Known-At {knownAt} is before appliedAt {appliedAt}.");

        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        Assert.Equal(H.TrimEnd('\n'), await reader.ReadLineAsync(Ct));
        var count = 0;
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            count++;
            if (count == 500_000)
            {
                Assert.Equal("E-0500000,BASE_SALARY,2026-01-01,,530000,EUR", line);
            }
        }

        Assert.Equal(rows, count);
    }

    /// <summary>
    /// 3.4 item 3 and the order of 3.3: an export with <c>knownAt</c> and a snapshot export wait for the batch too;
    /// the <c>knownAt</c> bound is the request time after the wait; a missing snapshot is 404 before the wait.
    /// </summary>
    [Fact]
    public async Task An_export_with_knownAt_and_a_snapshot_export_wait_too_and_a_missing_snapshot_answers_at_once()
    {
        const int rows = 1_000_000;
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Header + "E-0000001,BASE_SALARY,2026-01-01,1,EUR\n");
        var snapshot = await CreateSnapshotAsync(tenant, "Before the batch", "2026-03-01");

        var csv = new StringBuilder(Header, rows * 44);
        for (var i = 1; i <= rows; i++)
        {
            csv.Append("E-").Append(i.ToString("D7", CultureInfo.InvariantCulture)).Append(",BASE_SALARY,2026-01-01,").Append(30000 + i).Append(",EUR\n");
        }

        var body = Encoding.ASCII.GetBytes(csv.ToString());
        csv.Clear();
        var upload = Task.Run(() => _client.UploadAsync(tenant, body), Ct);

        await using var monitor = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        const string HolderSql =
            """
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory' AND mode = 'ExclusiveLock' AND granted AND objsubid = 1
              AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@tenant, 0)
            """;
        async Task<int?> HolderAsync() => await monitor.ExecuteScalarAsync<int?>(HolderSql, new { tenant = tenant.ToString("D") });

        // Wait until the upload holds the lock and has fixed its apply time (as the PRP-002 A8 test does).
        var deadline = DateTime.UtcNow.AddMinutes(3);
        int? holder;
        while ((holder = await HolderAsync()) is null
               || !await monitor.ExecuteScalarAsync<bool>(
                   """
                   SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                                  WHERE pid = @holder AND query NOT LIKE '%pg_advisory_xact_lock%' AND query NOT LIKE '%clock_timestamp()%')
                   """,
                   new { holder }))
        {
            Assert.False(upload.IsCompleted, "The upload finished before its apply time was seen to be fixed.");
            Assert.True(DateTime.UtcNow < deadline, "The upload never got past its apply time.");
            await Task.Delay(TimeSpan.FromMilliseconds(2), Ct);
        }

        var now = Instants.Write(await monitor.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()"));

        // A little later than the arrival of the request: valid only against the request time after the wait.
        var soon = Instants.Shift(now, 300_000);

        var withKnownAt = OpenAsync(Export + $"?asOf=2026-03-01&knownAt={now}", tenant);
        var afterArrival = OpenAsync(Export + $"?asOf=2026-03-01&knownAt={soon}", tenant);
        var throughSnapshot = OpenAsync(SnapshotExport(snapshot), tenant);

        // The lookup comes before the wait: an unknown snapshot is 404 while the batch is still being applied.
        using (var missing = await _client.GetAsync($"{Snapshots}/{Guid.CreateVersion7()}/export", tenant).WaitAsync(TimeSpan.FromSeconds(20), Ct))
        {
            await missing.AssertProblemAsync(HttpStatusCode.NotFound);
            Assert.False(upload.IsCompleted, "The batch was expected to be still applying when the 404 came back.");
        }

        // No headers while the lock is held: an answer seen before a held lock is a failure.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        var answered = withKnownAt.IsCompleted || afterArrival.IsCompleted || throughSnapshot.IsCompleted;
        var stillHeld = await HolderAsync() is not null;
        Assert.False(answered && stillHeld, "An export sent its headers while the batch was being applied.");

        // The instant soon is reached while the lock is still held, so the request time after the wait is later.
        var heldAtSoon = false;
        while (true)
        {
            var dbNow = Instants.Write(await monitor.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()"));
            var held = await HolderAsync() is not null;
            if (string.CompareOrdinal(dbNow, soon) >= 0)
            {
                heldAtSoon = held;
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }

        using var uploaded = await upload.WaitAsync(TimeSpan.FromMinutes(3), Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var appliedAt = (await uploaded.JsonAsync()).GetProperty("appliedAt").GetString();
        Assert.True(string.CompareOrdinal(appliedAt, now) < 0);

        async Task<long> CountRowsAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[1 << 16];
            long lines = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, Ct)) > 0)
            {
                lines += buffer.AsSpan(0, n).Count((byte)'\n');
            }

            return lines - 1;
        }

        // Both include the batch: it was applied before the instant, and the export waited for its commit.
        using (var response = await withKnownAt.WaitAsync(TimeSpan.FromMinutes(3), Ct))
        {
            Assert.Equal(now, Single(response, "X-Known-At"));
            Assert.Equal(rows, await CountRowsAsync(response));
        }

        using (var response = await afterArrival.WaitAsync(TimeSpan.FromMinutes(3), Ct))
        {
            if (heldAtSoon)
            {
                Assert.Equal(soon, Single(response, "X-Known-At"));
                Assert.Equal(rows, await CountRowsAsync(response));
            }
        }

        Assert.True(heldAtSoon, "The batch committed before the instant soon was reached; the bound after the wait was not checked.");

        // The snapshot was taken before the batch: its file does not change.
        using (var response = await throughSnapshot.WaitAsync(TimeSpan.FromMinutes(3), Ct))
        {
            Assert.Equal(snapshot.GetProperty("knownAt").GetString(), Single(response, "X-Known-At"));
            Assert.Equal(H + "E-0000001,BASE_SALARY,2026-01-01,,1,EUR\n", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    // ---------- 3.3 after the first byte, and the logging budget of 3.6 ----------

    private static bool IsFramework(CapturedLogs.Entry entry) =>
        entry.Category.StartsWith("Microsoft.", StringComparison.Ordinal) || entry.Category.StartsWith("System.", StringComparison.Ordinal);

    private List<CapturedLogs.Entry> ApplicationEntries(Guid tenant) =>
        [.. api.Logs.Entries.Where(e => !IsFramework(e) && e.State.Any(p => p.Key == "TenantId" && Equals(p.Value, tenant.ToString("D"))))];

    private List<CapturedLogs.Entry> ExportEntries(Guid tenant) => [.. ApplicationEntries(tenant).Where(e => e.Category == "CompLedger.Export")];

    private static Dictionary<string, object?> StateOf(CapturedLogs.Entry entry) =>
        entry.State.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value);

    private async Task<CapturedLogs.Entry> WaitForExportEntryAsync(Guid tenant, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (ExportEntries(tenant).Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "The export wrote no log entry.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }

        return ExportEntries(tenant)[count - 1];
    }

    [Fact]
    public async Task Each_export_that_sent_a_200_writes_one_log_entry_with_ids_coordinates_and_a_count_only()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);
        var snapshot = await CreateSnapshotAsync(tenant, "Logged", "2026-05-15");
        var entriesBefore = ApplicationEntries(tenant).Count;

        var file = await OkFileAsync(tenant, Export + "?asOf=2026-05-15");
        var entry = await WaitForExportEntryAsync(tenant);
        var state = StateOf(entry);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(
            ["AsOf", "DurationMs", "KnownAt", "Outcome", "Rows", "SnapshotId", "TenantId"],
            state.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(tenant.ToString("D"), state["TenantId"]);
        Assert.Null(state["SnapshotId"]);
        Assert.Equal("2026-05-15", state["AsOf"]);
        Assert.Equal(file.KnownAt, state["KnownAt"]);
        Assert.Equal(4L, Assert.IsType<long>(state["Rows"]));
        Assert.Equal("Completed", state["Outcome"]);
        Assert.InRange(Assert.IsType<long>(state["DurationMs"]), 0L, 60_000L);

        var snapshotFile = await OkFileAsync(tenant, SnapshotExport(snapshot));
        var snapshotState = StateOf(await WaitForExportEntryAsync(tenant, 2));
        Assert.Equal(snapshot.GetProperty("snapshotId").GetString(), snapshotState["SnapshotId"]);
        Assert.Equal("2026-05-15", snapshotState["AsOf"]);
        Assert.Equal(snapshotFile.KnownAt, snapshotState["KnownAt"]);
        Assert.Equal(4L, snapshotState["Rows"]);
        Assert.Equal("Completed", snapshotState["Outcome"]);

        var empty = await OkFileAsync(tenant, Export + "?asOf=1950-01-01");
        Assert.Equal(H, empty.Body);
        Assert.Equal(0L, StateOf(await WaitForExportEntryAsync(tenant, 3))["Rows"]);

        Assert.Equal(entriesBefore + 3, ApplicationEntries(tenant).Count);

        // No employee id, component, date or amount of any row in anything the export logged.
        var everything = string.Join('\n', api.Logs.Entries.Where(e => e.Category == "CompLedger.Export")
            .Select(e => e.Message + " " + string.Join(' ', e.State.Select(p => $"{p.Key}={p.Value}"))));
        foreach (var secret in new[] { "E-1001", "E-1002", "BASE_SALARY", "ALLOWANCE", "52000", "48000", "6000", "2026-04-01", "2026-06-30", "2025-07-01" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Refused_exports_write_no_log_entry_of_the_application()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, A1Rows);
        var snapshot = await CreateSnapshotAsync(tenant, "Refused", "2026-05-15");
        var entriesBefore = ApplicationEntries(tenant).Count;

        string[] refused =
        [
            $"{Export}?asOf=2026-02-30",
            $"{Export}?knownAt=",
            $"{Export}?knownAt=9999-01-01T00:00:00Z",
            $"{Export}?knownAt={Instants.Shift(await Instants.DatabaseNowAsync(api.Database), 3_600_000_000)}",
            SnapshotExport(snapshot) + "?asOf=2026-05-15",
            $"{Snapshots}/{Guid.CreateVersion7()}/export",
            $"{Snapshots}/not-a-uuid/export",
        ];
        foreach (var url in refused)
        {
            using var response = await _client.GetAsync(url, tenant);
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, url);
        }

        using (var wrongMethod = await _client.SendAsync(HttpMethod.Post, Export, tenant))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        Assert.Equal(entriesBefore, ApplicationEntries(tenant).Count);
    }

    /// <summary>A tenant with enough rows that the file cannot all sit in the transport's buffers.</summary>
    private async Task<Guid> LargeTenantAsync(int rows)
    {
        var tenant = await api.NewTenantAsync(_client);
        var csv = new StringBuilder(Header, rows * 44);
        for (var i = 1; i <= rows; i++)
        {
            csv.Append("E-").Append(i.ToString("D6", CultureInfo.InvariantCulture)).Append(",BASE_SALARY,2026-01-01,").Append(30000 + i).Append(",EUR\n");
        }

        await _client.AppliedAtAsync(tenant, csv.ToString());
        return tenant;
    }

    [Fact]
    public async Task A_client_that_cancels_ends_the_export_which_logs_Aborted_and_no_error()
    {
        const int rows = 100_000;
        var tenant = await LargeTenantAsync(rows);

        var response = await OpenAsync(Export + "?asOf=2026-02-01", tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[H.Length];
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(H, Encoding.UTF8.GetString(buffer));

        // The client goes away after the header line.
        await stream.DisposeAsync();
        response.Dispose();

        var entry = await WaitForExportEntryAsync(tenant);
        var state = StateOf(entry);
        Assert.Equal("Aborted", state["Outcome"]);
        Assert.InRange(Assert.IsType<long>(state["Rows"]), 0L, rows - 1L);
        Assert.Null(entry.Exception);
        Assert.True(entry.Level < LogLevel.Warning);

        // Nothing of the application is logged as an error for it, and the statement does not keep running.
        Assert.DoesNotContain(ApplicationEntries(tenant), e => e.Level >= LogLevel.Error);
        await using var monitor = await api.Database.AppDataSource.OpenConnectionAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await monitor.ExecuteScalarAsync<bool>(
                   "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND state = 'active' AND query LIKE '%' || @marker || '%')",
                   new { marker = ExportStatementMarker }))
        {
            Assert.True(DateTime.UtcNow < deadline, "The export statement kept running after the client went away.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        }
    }

    [Fact]
    public async Task A_failure_after_the_first_byte_aborts_the_connection_without_a_problem_body_and_logs_Aborted()
    {
        const int rows = 100_000;
        var tenant = await LargeTenantAsync(rows);

        using var response = await OpenAsync(Export + "?asOf=2026-02-01", tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[H.Length];
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(H, Encoding.UTF8.GetString(buffer));

        // The client has read the header only, so the export is still in its statement: end that backend.
        await using (var monitor = await api.Database.AppDataSource.OpenConnectionAsync(Ct))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await monitor.ExecuteScalarAsync<int>(
                       "SELECT count(pg_terminate_backend(pid))::int FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND state = 'active' AND query LIKE '%' || @marker || '%'",
                       new { marker = ExportStatementMarker }) == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The export statement was not found.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
            }
        }

        // The rest of the file never arrives: the connection is aborted, and nothing after the rows looks like an error.
        var received = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(async () => await stream.CopyToAsync(received, Ct));
        var text = Encoding.UTF8.GetString(received.ToArray());
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.True(text.Count(c => c == '\n') < rows, "The whole file arrived.");

        var entry = await WaitForExportEntryAsync(tenant);
        var state = StateOf(entry);
        Assert.Equal("Aborted", state["Outcome"]);
        Assert.InRange(Assert.IsType<long>(state["Rows"]), 0L, rows - 1L);
        Assert.Single(ExportEntries(tenant));
    }
}

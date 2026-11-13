using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CompLedger.Infrastructure.Db;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace CompLedger.IntegrationTests;

/// <summary>
/// The API in-process, connected to the fixture's database as the non-superuser app login, with the tenant admin
/// API enabled. Subclasses change one setting.
/// </summary>
public class ApiFactory(PostgresFixture fixture) : WebApplicationFactory<Program>
{
    public const string Header = "employee_id,component,effective_from,amount,currency\n";

    public const string A1 = Header
        + "E-1001,BASE_SALARY,2026-01-01,50000,EUR\n"
        + "E-1001,BASE_SALARY,2026-07-01,55000,EUR\n"
        + "E-1001,BONUS_TARGET_PCT,2026-01-01,10,\n";

    public PostgresFixture Database { get; } = fixture;

    public CapturedLogs Logs { get; } = new();

    protected virtual bool TenantAdminEnabled => true;

    protected virtual long? MaxBodyBytes => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:App", Database.AppConnectionString);
        builder.UseSetting("TenantAdminApi:Enabled", TenantAdminEnabled ? "true" : "false");
        if (MaxBodyBytes is { } maxBodyBytes)
        {
            builder.UseSetting("Ingestion:MaxBodyBytes", maxBodyBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    /// <summary>Creates a tenant through <c>POST /api/v1/tenants</c>, as the acceptance examples do.</summary>
    public async Task<Guid> NewTenantAsync(HttpClient client, string name = "Acme")
    {
        using var response = await client.PostAsync(
            "/api/v1/tenants", new StringContent(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public async Task<long> CountBatchesAsync(Guid tenant)
    {
        await using var scope = await TenantScope.BeginAsync(Database.AppDataSource, tenant, TestContext.Current.CancellationToken);
        return await scope.Connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ingestion_batches", transaction: scope.Transaction);
    }
}

public sealed class TenantAdminDisabledFactory(PostgresFixture fixture) : ApiFactory(fixture)
{
    protected override bool TenantAdminEnabled => false;
}

public sealed class SmallBodyLimitFactory(PostgresFixture fixture) : ApiFactory(fixture)
{
    public const int Limit = 1024;

    protected override long? MaxBodyBytes => Limit;
}

public static class HttpTestExtensions
{
    public static async Task<HttpResponseMessage> SendAsync(this HttpClient client, HttpMethod method, string url, Guid? tenant, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (tenant is { } id)
        {
            request.Headers.Add("X-Tenant-Id", id.ToString());
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> GetAsync(this HttpClient client, string url, Guid? tenant) =>
        client.SendAsync(HttpMethod.Get, url, tenant);

    public static Task<HttpResponseMessage> UploadAsync(this HttpClient client, Guid? tenant, string csv, string? contentType = "text/csv") =>
        client.UploadAsync(tenant, Encoding.UTF8.GetBytes(csv), contentType);

    public static Task<HttpResponseMessage> UploadAsync(this HttpClient client, Guid? tenant, byte[] body, string? contentType = "text/csv")
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null)
        {
            // Without validation, so that deliberately odd values reach the server as written.
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return client.SendAsync(HttpMethod.Post, "/api/v1/ingestions", tenant, content);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts the status code and that the body is RFC 7807 ProblemDetails carrying the same status.</summary>
    public static async Task AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(new MediaTypeHeaderValue("application/problem+json").MediaType, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.JsonAsync();
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
    }

    public static (int Total, int Inserted, int Corrected, int Unchanged) Rows(this JsonElement batch)
    {
        var rows = batch.GetProperty("rows");
        return (rows.GetProperty("total").GetInt32(), rows.GetProperty("inserted").GetInt32(),
            rows.GetProperty("corrected").GetInt32(), rows.GetProperty("unchanged").GetInt32());
    }

    public static List<(int? Row, string? Column, string Code)> Errors(this JsonElement batch) =>
        batch.GetProperty("errors").EnumerateArray()
            .Select(e => (
                e.GetProperty("row").ValueKind == JsonValueKind.Null ? (int?)null : e.GetProperty("row").GetInt32(),
                e.GetProperty("column").GetString(),
                e.GetProperty("code").GetString()!))
            .ToList();

    /// <summary>Periods as (component, amount, currency, effectiveFrom, effectiveTo) from a <c>components</c> or <c>periods</c> array.</summary>
    public static List<(string Component, decimal Amount, string? Currency, string From, string? To)> Periods(this JsonElement array) =>
        array.EnumerateArray()
            .Select(p => (
                p.GetProperty("component").GetString()!,
                p.GetProperty("amount").GetDecimal(),
                p.GetProperty("currency").GetString(),
                p.GetProperty("effectiveFrom").GetString()!,
                p.GetProperty("effectiveTo").GetString()))
            .ToList();
}

/// <summary>Collects every log entry the app writes, with its structured state.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    public IReadOnlyCollection<Entry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), [.. values]));
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace CompLedger.IntegrationTests;

/// <summary>
/// Builds the image from the repository's Dockerfile and runs it against an empty database on the fixture's server,
/// configured only through the environment variables of PRP-001 6.4.
/// </summary>
public sealed class ApiContainer(PostgresFixture fixture) : IAsyncLifetime
{
    public const long MaxBodyBytes = 40L * 1024 * 1024;

    private IFutureDockerImage? _image;
    private IContainer? _container;

    public IContainer Container => _container ?? throw new InvalidOperationException("The container is not started.");

    public HttpClient Client { get; private set; } = new();

    public async ValueTask InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(FindRepositoryRoot())
            .WithDockerfile("Dockerfile")
            .Build();
        await _image.CreateAsync();

        var (owner, app) = await fixture.CreateEmptyDatabaseForContainersAsync("container_tests");
        _container = new ContainerBuilder(_image)
            .WithNetwork(fixture.Network)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithEnvironment("ConnectionStrings__App", app)
            .WithEnvironment("ConnectionStrings__Migrations", owner)
            .WithEnvironment("Database__MigrateOnStartup", "true")
            .WithEnvironment("TenantAdminApi__Enabled", "true")
            .WithEnvironment("Ingestion__MaxBodyBytes", MaxBodyBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/health")))
            .Build();
        await _container.StartAsync();

        Client = new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}"),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_image is not null)
        {
            await _image.DisposeAsync();
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CompLedger.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("CompLedger.slnx was not found above the test directory.");
    }
}

public sealed class ContainerTests(ApiContainer api) : IClassFixture<ApiContainer>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_returns_200_once_the_container_is_up()
    {
        using var response = await api.Client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_api_process_is_pid_1_and_the_image_has_cat()
    {
        var result = await api.Container.ExecAsync(["cat", "/proc/1/status"], Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"(?m)^Name:\s+dotnet\s*$", result.Stdout);
        Assert.Contains("VmHWM:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_migrations_ran_and_a_batch_is_ingested_queried_and_logged_as_json()
    {
        var tenant = await api.Client.NewTenantInContainerAsync();

        using var upload = await api.Client.UploadAsync(tenant, ApiFactory.A1);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var batch = await upload.JsonAsync();
        var batchId = batch.GetProperty("batchId").GetString()!;
        Assert.Equal((3, 3, 0, 0), batch.Rows());

        using var asOf = await api.Client.GetAsync("/api/v1/employees/E-1001/compensation?asOf=2026-03-15", tenant);
        Assert.Equal(HttpStatusCode.OK, asOf.StatusCode);
        Assert.Equal(
            [("BASE_SALARY", 50000m, "EUR", "2026-01-01", "2026-06-30"), ("BONUS_TARGET_PCT", 10m, null, "2026-01-01", null)],
            (await asOf.JsonAsync()).GetProperty("components").Periods());

        // Console logs are JSON with no extra configuration; the batch has exactly one entry, properties under State.
        var entries = new List<JsonElement>();
        for (var attempt = 0; attempt < 50 && entries.Count == 0; attempt++)
        {
            await Task.Delay(100, Ct);
            var (stdout, _) = await api.Container.GetLogsAsync(timestampsEnabled: false, ct: Ct);
            entries = stdout.Split('\n')
                .Where(line => line.Contains(batchId, StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
        }

        var state = Assert.Single(entries).GetProperty("State");
        Assert.Equal(tenant.ToString("D"), state.GetProperty("TenantId").GetString());
        Assert.Equal(batchId, state.GetProperty("BatchId").GetString());
        Assert.Equal("Applied", state.GetProperty("Status").GetString());
        Assert.Equal(3, state.GetProperty("TotalRows").GetInt32());
        Assert.Equal(3, state.GetProperty("InsertedRows").GetInt32());
        Assert.Equal(0, state.GetProperty("CorrectedRows").GetInt32());
        Assert.Equal(0, state.GetProperty("UnchangedRows").GetInt32());
        Assert.Equal(0, state.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(JsonValueKind.Number, state.GetProperty("DurationMs").ValueKind);
        Assert.True(state.GetProperty("DurationMs").TryGetInt64(out _));
    }

    [Fact]
    public async Task A_body_over_kestrels_default_limit_is_read_and_one_over_the_configured_limit_is_refused()
    {
        var tenant = await api.Client.NewTenantInContainerAsync();

        // 31 MiB, above Kestrel's default of about 28.6 MiB and below Ingestion__MaxBodyBytes. The rows are malformed,
        // so the whole body must be read to count them and nothing is applied.
        const int rows = 31 * 1024;
        var line = Encoding.ASCII.GetBytes(new string('x', 1023) + "\n");
        using var accepted = await api.Client.SendAsync(
            HttpMethod.Post, "/api/v1/ingestions", tenant, new RepeatedLineContent(Encoding.ASCII.GetBytes(ApiFactory.Header), line, rows, declareLength: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, accepted.StatusCode);
        var batch = await accepted.JsonAsync();
        Assert.Equal(rows, batch.GetProperty("rows").GetProperty("total").GetInt32());
        Assert.Equal(rows, batch.GetProperty("errorCount").GetInt32());

        // A chunked body that goes over the limit while streaming: 413 on a best-effort basis. The server may close
        // the connection first, and then the client sees a transport error instead (PRP-001 3.6, check 4).
        var valid = Encoding.ASCII.GetBytes("E-1,BASE_SALARY,2026-01-01,1,EUR\n");
        var tooMany = (int)(ApiContainer.MaxBodyBytes / valid.Length) + 1000;
        HttpStatusCode? status;
        try
        {
            using var refused = await api.Client.SendAsync(
                HttpMethod.Post, "/api/v1/ingestions", tenant, new RepeatedLineContent(Encoding.ASCII.GetBytes(ApiFactory.Header), valid, tooMany, declareLength: false));
            status = refused.StatusCode;
        }
        catch (HttpRequestException)
        {
            status = null;
        }

        Assert.True(status is null or HttpStatusCode.RequestEntityTooLarge, $"Expected 413 or a closed connection, got {status}.");

        // Either way nothing was applied, and the server is still healthy.
        using var lookup = await api.Client.GetAsync("/api/v1/employees/E-1/compensation", tenant);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    /// <summary>A header followed by the same line many times, produced while it is sent.</summary>
    private sealed class RepeatedLineContent : HttpContent
    {
        private readonly byte[] _header;
        private readonly byte[] _line;
        private readonly int _count;
        private readonly bool _declareLength;

        public RepeatedLineContent(byte[] header, byte[] line, int count, bool declareLength)
        {
            (_header, _line, _count, _declareLength) = (header, line, count, declareLength);
            Headers.TryAddWithoutValidation("Content-Type", "text/csv");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_header);
            var block = new byte[_line.Length * 64];
            for (var i = 0; i < 64; i++)
            {
                _line.CopyTo(block, i * _line.Length);
            }

            var remaining = _count;
            while (remaining > 0)
            {
                var lines = Math.Min(64, remaining);
                await stream.WriteAsync(block.AsMemory(0, lines * _line.Length));
                remaining -= lines;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            // Without a length, HttpClient sends the body chunked.
            length = _header.Length + ((long)_line.Length * _count);
            return _declareLength;
        }
    }
}

internal static class ContainerClientExtensions
{
    public static async Task<Guid> NewTenantInContainerAsync(this HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/v1/tenants", new StringContent("""{"name":"Container tenant"}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }
}

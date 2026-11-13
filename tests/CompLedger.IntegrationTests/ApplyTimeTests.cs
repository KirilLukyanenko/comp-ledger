using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CompLedger.Infrastructure.Db;
using Dapper;
using Npgsql;

namespace CompLedger.IntegrationTests;

/// <summary>PRP-002 T1: the apply time of a batch in the batch resource, and migration 0006 with its backfill.</summary>
public sealed partial class ApplyTimeTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    private readonly HttpClient _client = api.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Format(DateTime instant) => instant.ToString(InstantFormat, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Applied_at_is_the_recorded_at_of_the_values_the_batch_inserted_and_the_superseded_at_of_those_it_replaced()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var firstResponse = await _client.UploadAsync(tenant, ApiFactory.A1);
        using var secondResponse = await _client.UploadAsync(
            tenant, Header + "E-1001,BASE_SALARY,2026-01-01,51000,EUR\nE-1002,BASE_SALARY,2026-01-01,40000,EUR\n");
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var first = await firstResponse.JsonAsync();
        var second = await secondResponse.JsonAsync();
        var firstAppliedAt = first.GetProperty("appliedAt").GetString()!;
        var secondAppliedAt = second.GetProperty("appliedAt").GetString()!;

        // UTC with exactly six fractional digits, and in the order the batches were applied.
        Assert.Matches(SixDigitInstant(), firstAppliedAt);
        Assert.Matches(SixDigitInstant(), secondAppliedAt);
        Assert.True(string.CompareOrdinal(firstAppliedAt, secondAppliedAt) < 0);

        await using var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, Ct);
        var values = (await scope.Connection.QueryAsync<(Guid BatchId, DateTime RecordedAt, DateTime? SupersededAt, Guid? SupersededBy)>(
            "SELECT batch_id, recorded_at, superseded_at, superseded_by_batch_id FROM compensation_values", transaction: scope.Transaction)).ToList();
        var firstId = first.GetProperty("batchId").GetGuid();
        var secondId = second.GetProperty("batchId").GetGuid();

        Assert.Equal(5, values.Count);
        Assert.All(values.Where(v => v.BatchId == firstId), v => Assert.Equal(firstAppliedAt, Format(v.RecordedAt)));
        Assert.All(values.Where(v => v.BatchId == secondId), v => Assert.Equal(secondAppliedAt, Format(v.RecordedAt)));
        var superseded = Assert.Single(values, v => v.SupersededAt is not null);
        Assert.Equal(secondId, superseded.SupersededBy);
        Assert.Equal(secondAppliedAt, Format(superseded.SupersededAt!.Value));

        // The stored batch carries the same instant.
        var stored = await scope.Connection.QuerySingleAsync<DateTime>(
            "SELECT applied_at FROM ingestion_batches WHERE id = @secondId", new { secondId }, scope.Transaction);
        Assert.Equal(secondAppliedAt, Format(stored));
        using var read = await _client.GetAsync($"/api/v1/ingestions/{secondId}", tenant);
        Assert.Equal(secondAppliedAt, (await read.JsonAsync()).GetProperty("appliedAt").GetString());
    }

    [Fact]
    public async Task A_batch_that_changes_nothing_still_has_an_apply_time_after_the_previous_one()
    {
        var tenant = await api.NewTenantAsync(_client);
        using var firstResponse = await _client.UploadAsync(tenant, ApiFactory.A1);

        using var againResponse = await _client.UploadAsync(tenant, ApiFactory.A1);

        var again = await againResponse.JsonAsync();
        Assert.Equal((3, 0, 0, 3), again.Rows());
        var appliedAt = again.GetProperty("appliedAt").GetString()!;
        Assert.Matches(SixDigitInstant(), appliedAt);
        Assert.True(string.CompareOrdinal((await firstResponse.JsonAsync()).GetProperty("appliedAt").GetString(), appliedAt) < 0);
    }

    [Fact]
    public async Task A_rejected_batch_has_a_null_applied_at_in_the_response_the_resource_and_the_table()
    {
        var tenant = await api.NewTenantAsync(_client);

        using var response = await _client.UploadAsync(tenant, Header + "E-1,BASE_SALARY,2026-01-01,-1,EUR\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var batch = await response.JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, batch.GetProperty("appliedAt").ValueKind);

        var batchId = batch.GetProperty("batchId").GetGuid();
        using var read = await _client.GetAsync($"/api/v1/ingestions/{batchId}", tenant);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await read.JsonAsync()).GetProperty("appliedAt").ValueKind);

        await using var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, Ct);
        Assert.Null(await scope.Connection.QuerySingleAsync<DateTime?>(
            "SELECT applied_at FROM ingestion_batches WHERE id = @batchId", new { batchId }, scope.Transaction));
    }

    [Fact]
    public async Task The_table_refuses_an_applied_batch_without_an_apply_time_and_a_rejected_one_with_it()
    {
        var tenant = await api.Database.CreateTenantAsync();

        foreach (var (status, appliedAt) in new[] { ("Applied", "NULL"), ("Rejected", "now()") })
        {
            await using var scope = await TenantScope.BeginAsync(api.Database.AppDataSource, tenant, Ct);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => scope.Connection.ExecuteAsync(
                $"""
                INSERT INTO ingestion_batches (id, tenant_id, status, received_at, completed_at, applied_at, total_rows, inserted_rows, corrected_rows, unchanged_rows, error_count)
                VALUES (@id, @tenant, '{status}', now(), now(), {appliedAt}, 0, 0, 0, 0, 0)
                """,
                new { id = Guid.CreateVersion7(), tenant },
                scope.Transaction));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
            Assert.Equal("ck_ingestion_batches_applied_at", ex.ConstraintName);
        }
    }

    [Fact]
    public async Task The_value_index_is_not_partial_and_covers_tenant_employee_component_and_start()
    {
        await using var connection = await api.Database.AppDataSource.OpenConnectionAsync(Ct);

        var definition = await connection.QuerySingleAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'compensation_values' AND indexname = 'ix_compensation_values_employee'");

        Assert.Contains("(tenant_id, employee_id, component, effective_from)", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE", definition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Migrations 0001 to 0006 run as an owner that is not a superuser, so FORCE ROW LEVEL SECURITY applies to it as
    /// it does in production (PRP-002 6.5 #9). The data of two tenants is written before 0006, through the app login.
    /// </summary>
    [Fact]
    public async Task Migration_0006_backfills_apply_times_for_every_tenant_when_run_by_a_non_superuser_owner()
    {
        var fixture = api.Database;
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var ownerRole = $"mig_owner_{suffix}";
        var database = $"mig_backfill_{suffix}";
        var ownerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        await using (var super = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await super.OpenAsync(Ct);

            // Random hex and fixed prefixes only, so inlining is safe; CREATE ROLE and CREATE DATABASE take no parameters.
            await super.ExecuteAsync($"CREATE ROLE {ownerRole} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{ownerPassword}'");
            await super.ExecuteAsync($"CREATE DATABASE {database} OWNER {ownerRole}");
        }

        await using var ownerSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = database, Username = ownerRole, Password = ownerPassword }.ConnectionString);
        await using var appSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Database = database }.ConnectionString);

        await using (var owner = await ownerSource.OpenConnectionAsync(Ct))
        {
            Assert.False(await owner.ExecuteScalarAsync<bool>("SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user"));
        }

        var scripts = MigrationScripts();
        Assert.Equal("0006_batch_apply_time_and_value_index.sql", Path.GetFileName(scripts[5]));
        foreach (var script in scripts.Take(5))
        {
            await RunScriptAsync(ownerSource, script);
        }

        // Two tenants. Per tenant: a batch that inserted values, one that corrected a value, one that only superseded
        // a value, one that changed nothing, and a rejected one. Every instant is distinct, so a mix-up shows.
        var start = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var tenants = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var expected = new Dictionary<Guid, DateTime?>();
        await using (var app = await appSource.OpenConnectionAsync(Ct))
        {
            foreach (var tenant in tenants)
            {
                await app.ExecuteAsync("INSERT INTO tenants (id, name) VALUES (@tenant, 'Backfill')", new { tenant });
            }
        }

        for (var t = 0; t < tenants.Length; t++)
        {
            var tenant = tenants[t];
            var day = start.AddDays(t);
            var (inserting, correcting, supersedingOnly, unchanged, rejected) =
                (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (insertedAt, correctedAt, supersededOnlyAt, unchangedCompletedAt) =
                (day.AddMinutes(1).AddTicks(1234560), day.AddMinutes(2).AddTicks(10), day.AddMinutes(3), day.AddMinutes(4));

            await using var scope = await TenantScope.BeginAsync(appSource, tenant, Ct);
            foreach (var (id, status, completedAt) in new[]
                     {
                         (inserting, "Applied", day.AddMinutes(1).AddSeconds(30)),
                         (correcting, "Applied", day.AddMinutes(2).AddSeconds(30)),
                         (supersedingOnly, "Applied", day.AddMinutes(3).AddSeconds(30)),
                         (unchanged, "Applied", unchangedCompletedAt),
                         (rejected, "Rejected", day.AddMinutes(5)),
                     })
            {
                await scope.Connection.ExecuteAsync(
                    """
                    INSERT INTO ingestion_batches (id, tenant_id, status, received_at, completed_at, total_rows, inserted_rows, corrected_rows, unchanged_rows, error_count)
                    VALUES (@id, @tenant, @status, @completedAt, @completedAt, 0, 0, 0, 0, 0)
                    """,
                    new { id, tenant, status, completedAt },
                    scope.Transaction);
            }

            // E-1 was inserted, then corrected. E-2 was inserted, then superseded by a batch that inserted nothing.
            const string InsertValue =
                """
                INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at, superseded_at, superseded_by_batch_id)
                VALUES (@tenant, @employee, 'BASE_SALARY', DATE '2026-01-01', @amount, 'EUR', @batch, @recordedAt, @supersededAt, @supersededBy)
                """;
            await scope.Connection.ExecuteAsync(
                InsertValue,
                new[]
                {
                    new { tenant, employee = "E-1", amount = 1m, batch = inserting, recordedAt = insertedAt, supersededAt = (DateTime?)correctedAt, supersededBy = (Guid?)correcting },
                    new { tenant, employee = "E-2", amount = 2m, batch = inserting, recordedAt = insertedAt, supersededAt = (DateTime?)supersededOnlyAt, supersededBy = (Guid?)supersedingOnly },
                    new { tenant, employee = "E-1", amount = 3m, batch = correcting, recordedAt = correctedAt, supersededAt = (DateTime?)null, supersededBy = (Guid?)null },
                },
                scope.Transaction);
            await scope.CommitAsync(Ct);

            expected[inserting] = insertedAt;
            expected[correcting] = correctedAt;
            expected[supersedingOnly] = supersededOnlyAt;
            expected[unchanged] = unchangedCompletedAt;
            expected[rejected] = null;
        }

        await RunScriptAsync(ownerSource, scripts[5]);

        foreach (var tenant in tenants)
        {
            await using var scope = await TenantScope.BeginAsync(appSource, tenant, Ct);
            var batches = (await scope.Connection.QueryAsync<(Guid Id, DateTime? AppliedAt)>(
                "SELECT id, applied_at FROM ingestion_batches", transaction: scope.Transaction)).ToList();

            Assert.Equal(5, batches.Count);
            Assert.All(batches, b => Assert.Equal(expected[b.Id], b.AppliedAt));
        }

        // Row-level security is forced again on both tables, and the owner sees nothing without a tenant.
        await using (var owner = await ownerSource.OpenConnectionAsync(Ct))
        {
            var forced = (await owner.QueryAsync<bool>(
                "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname IN ('ingestion_batches', 'compensation_values')")).ToList();
            Assert.Equal([true, true], forced);
            Assert.Equal(0, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM ingestion_batches"));
            Assert.Equal(0, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM compensation_values"));
            Assert.Equal(1, await owner.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_ingestion_batches_applied_at' AND convalidated"));
            Assert.Equal(1, await owner.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_compensation_values_employee'"));
        }
    }

    /// <summary>The files of <c>db/migrations</c> in name order.</summary>
    private static List<string> MigrationScripts()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var migrations = Path.Combine(directory.FullName, "db", "migrations");
            if (File.Exists(Path.Combine(directory.FullName, "CompLedger.slnx")) && Directory.Exists(migrations))
            {
                return [.. Directory.GetFiles(migrations, "*.sql").Order(StringComparer.Ordinal)];
            }
        }

        throw new InvalidOperationException("db/migrations was not found above the test directory.");
    }

    /// <summary>One transaction per script, as the migrator runs them.</summary>
    private static async Task RunScriptAsync(NpgsqlDataSource owner, string path)
    {
        await using var connection = await owner.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await connection.ExecuteAsync(await File.ReadAllTextAsync(path, Ct), transaction: transaction);
        await transaction.CommitAsync(Ct);
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigitInstant();
}

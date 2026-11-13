using CompLedger.Infrastructure.Db;
using Dapper;
using Npgsql;

namespace CompLedger.IntegrationTests;

/// <summary>Isolation is enforced by the database: these tests use raw SQL through the app login, with no API code.</summary>
public sealed class RowLevelSecurityTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task App_login_is_not_superuser_not_bypassrls_and_not_table_owner()
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync(Ct);

        var role = await connection.QuerySingleAsync<(string Name, bool Super, bool BypassRls)>(
            "SELECT rolname::text, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user");
        var owners = (await connection.QueryAsync<string>(
            "SELECT tableowner::text FROM pg_tables WHERE tablename IN ('tenants', 'ingestion_batches', 'compensation_values')")).ToList();

        Assert.Equal(PostgresFixture.AppLogin, role.Name);
        Assert.False(role.Super);
        Assert.False(role.BypassRls);
        Assert.Equal(3, owners.Count);
        Assert.DoesNotContain(PostgresFixture.AppLogin, owners);
        Assert.DoesNotContain("comp_app", owners);
    }

    [Fact]
    public async Task Row_level_security_is_enabled_and_forced_on_tenant_owned_tables()
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync(Ct);

        var tables = (await connection.QueryAsync<(string Name, bool Enabled, bool Forced)>(
            "SELECT relname::text, relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname IN ('ingestion_batches', 'compensation_values') ORDER BY relname")).ToList();

        Assert.Equal(2, tables.Count);
        Assert.All(tables, t => Assert.True(t.Enabled && t.Forced, t.Name));
    }

    [Fact]
    public async Task With_no_tenant_set_no_rows_are_visible()
    {
        var tenant = await fixture.CreateTenantAsync();
        await SeedAsync(tenant, "E-1");

        await using var connection = await fixture.AppDataSource.OpenConnectionAsync(Ct);

        // Outside any tenant scope. The setting is NULL on a fresh session and '' after a scope ended on a pooled
        // connection; both must give zero rows, not an error.
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ingestion_batches"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM compensation_values"));

        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @t, true)", new { t = tenant.ToString() }, tx);
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM compensation_values", transaction: tx));
            await tx.CommitAsync(Ct);
        }

        Assert.Equal("", await connection.ExecuteScalarAsync<string>("SELECT current_setting('app.tenant_id', true)"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ingestion_batches"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM compensation_values"));
    }

    [Fact]
    public async Task With_no_tenant_set_inserts_are_refused()
    {
        var tenant = await fixture.CreateTenantAsync();
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync(Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertBatchAsync(connection, null, Guid.CreateVersion7(), tenant));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_rows()
    {
        var tenantA = await fixture.CreateTenantAsync("A");
        var tenantB = await fixture.CreateTenantAsync("B");
        await SeedAsync(tenantA, "E-A");
        var batchB = await SeedAsync(tenantB, "E-B");

        await using var scope = await TenantScope.BeginAsync(fixture.AppDataSource, tenantA, Ct);
        var employees = (await scope.Connection.QueryAsync<string>(
            "SELECT employee_id FROM compensation_values", transaction: scope.Transaction)).ToList();
        var batchTenants = (await scope.Connection.QueryAsync<Guid>(
            "SELECT tenant_id FROM ingestion_batches", transaction: scope.Transaction)).ToList();

        // No tenant filter in the SQL: only row-level security stands between A and B's rows.
        Assert.Equal(["E-A"], employees);
        Assert.Equal([tenantA], batchTenants);
        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM ingestion_batches WHERE id = @batchB", new { batchB }, scope.Transaction));
        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM compensation_values WHERE tenant_id = @tenantB", new { tenantB }, scope.Transaction));
    }

    [Fact]
    public async Task Tenant_cannot_insert_or_update_another_tenants_rows()
    {
        var tenantA = await fixture.CreateTenantAsync("A");
        var tenantB = await fixture.CreateTenantAsync("B");
        var batchA = await SeedAsync(tenantA, "E-A");
        await SeedAsync(tenantB, "E-B");

        await using (var scope = await TenantScope.BeginAsync(fixture.AppDataSource, tenantA, Ct))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => InsertBatchAsync(scope.Connection, scope.Transaction, Guid.CreateVersion7(), tenantB));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var scope = await TenantScope.BeginAsync(fixture.AppDataSource, tenantA, Ct))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => InsertValueAsync(scope.Connection, scope.Transaction, tenantB, batchA, "E-X"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var scope = await TenantScope.BeginAsync(fixture.AppDataSource, tenantA, Ct))
        {
            // B's rows are invisible to A, so an unfiltered UPDATE touches only A's row.
            var updated = await scope.Connection.ExecuteAsync(
                "UPDATE compensation_values SET amount = amount", transaction: scope.Transaction);
            Assert.Equal(1, updated);

            // Moving a row to another tenant is refused by WITH CHECK.
            var ex = await Assert.ThrowsAsync<PostgresException>(() => scope.Connection.ExecuteAsync(
                "UPDATE compensation_values SET tenant_id = @tenantB", new { tenantB }, scope.Transaction));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task App_role_cannot_change_or_delete_tenants()
    {
        var tenant = await fixture.CreateTenantAsync();
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync(Ct);

        var update = await Assert.ThrowsAsync<PostgresException>(
            () => connection.ExecuteAsync("UPDATE tenants SET name = 'x' WHERE id = @tenant", new { tenant }));
        var delete = await Assert.ThrowsAsync<PostgresException>(
            () => connection.ExecuteAsync("DELETE FROM tenants WHERE id = @tenant", new { tenant }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        Migrator.Migrate(fixture.OwnerConnectionString);

        await using var connection = await fixture.AppDataSource.OpenConnectionAsync(Ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_policies WHERE tablename = 'compensation_values'"));
    }

    /// <summary>Inserts one batch and one value for the tenant, inside its own tenant scope.</summary>
    private async Task<Guid> SeedAsync(Guid tenant, string employeeId)
    {
        var batchId = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(fixture.AppDataSource, tenant, Ct);
        await InsertBatchAsync(scope.Connection, scope.Transaction, batchId, tenant);
        await InsertValueAsync(scope.Connection, scope.Transaction, tenant, batchId, employeeId);
        await scope.CommitAsync(Ct);
        return batchId;
    }

    private static Task<int> InsertBatchAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid batchId, Guid tenant) =>
        connection.ExecuteAsync(
            """
            INSERT INTO ingestion_batches (id, tenant_id, status, received_at, completed_at, applied_at, total_rows, inserted_rows, corrected_rows, unchanged_rows, error_count)
            VALUES (@batchId, @tenant, 'Applied', now(), now(), now(), 1, 1, 0, 0, 0)
            """,
            new { batchId, tenant },
            transaction);

    private static Task<int> InsertValueAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenant, Guid batchId, string employeeId) =>
        connection.ExecuteAsync(
            """
            INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at)
            VALUES (@tenant, @employeeId, 'BASE_SALARY', DATE '2026-01-01', 1, 'EUR', @batchId, now())
            """,
            new { tenant, employeeId, batchId },
            transaction);
}

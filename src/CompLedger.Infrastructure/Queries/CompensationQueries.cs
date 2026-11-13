using System.Globalization;
using CompLedger.Infrastructure.Db;
using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Queries;

/// <summary>A current value and the period it is in effect. <see cref="EffectiveTo"/> is inclusive; null means open-ended.</summary>
public sealed record CompensationPeriod(string Component, decimal Amount, string? Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>Reads current (not superseded) values of one employee inside a tenant scope.</summary>
public sealed class CompensationQueries(NpgsqlDataSource dataSource)
{
    // A period ends the day before the next later value of the same component starts.
    private const string Periods =
        """
        WITH periods AS (
          SELECT component, amount, currency::text AS currency, effective_from,
                 lead(effective_from) OVER (PARTITION BY component ORDER BY effective_from) - 1 AS effective_to
          FROM compensation_values
          WHERE tenant_id = @TenantId AND employee_id = @EmployeeId AND superseded_at IS NULL
        )
        SELECT component AS Component, amount AS Amount, currency AS Currency,
               effective_from AS EffectiveFrom, effective_to AS EffectiveTo
        FROM periods

        """;

    /// <summary>Values in effect on the date, by component code. Null when the employee has no values in this tenant.</summary>
    public Task<IReadOnlyList<CompensationPeriod>?> GetAsOfAsync(Guid tenantId, string employeeId, DateOnly asOf, CancellationToken cancellationToken) =>
        QueryAsync(
            tenantId,
            employeeId,
            Periods
            + """
              WHERE effective_from <= @AsOf::date AND (effective_to IS NULL OR effective_to >= @AsOf::date)
              ORDER BY component COLLATE "C"
              """,
            new { TenantId = tenantId, EmployeeId = employeeId, AsOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            cancellationToken);

    /// <summary>
    /// All current periods, by component code and start date, optionally for one component.
    /// Null when the employee has no values in this tenant.
    /// </summary>
    public Task<IReadOnlyList<CompensationPeriod>?> GetHistoryAsync(Guid tenantId, string employeeId, string? component, CancellationToken cancellationToken) =>
        QueryAsync(
            tenantId,
            employeeId,
            Periods
            + """
              WHERE @Component::text IS NULL OR component = @Component
              ORDER BY component COLLATE "C", effective_from
              """,
            new { TenantId = tenantId, EmployeeId = employeeId, Component = component },
            cancellationToken);

    private async Task<IReadOnlyList<CompensationPeriod>?> QueryAsync(
        Guid tenantId, string employeeId, string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);

        var rows = (await scope.Connection.QueryAsync<PeriodRow>(
            new CommandDefinition(sql, parameters, scope.Transaction, cancellationToken: cancellationToken))).AsList();

        if (rows.Count == 0)
        {
            var employeeExists = await scope.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM compensation_values WHERE tenant_id = @TenantId AND employee_id = @EmployeeId)",
                new { TenantId = tenantId, EmployeeId = employeeId },
                scope.Transaction,
                cancellationToken: cancellationToken));
            if (!employeeExists)
            {
                return null;
            }
        }

        await scope.CommitAsync(cancellationToken);
        return rows.ConvertAll(row => new CompensationPeriod(
            row.Component,
            row.Amount,
            row.Currency,
            DateOnly.FromDateTime(row.EffectiveFrom),
            row.EffectiveTo is { } effectiveTo ? DateOnly.FromDateTime(effectiveTo) : null));
    }

    private sealed class PeriodRow
    {
        public string Component { get; set; } = "";

        public decimal Amount { get; set; }

        public string? Currency { get; set; }

        public DateTime EffectiveFrom { get; set; }

        public DateTime? EffectiveTo { get; set; }
    }
}

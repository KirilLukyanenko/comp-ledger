using System.Globalization;
using CompLedger.Infrastructure.Db;
using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Queries;

/// <summary>A value and the period it is in effect. <see cref="EffectiveTo"/> is inclusive; null means open-ended.</summary>
public sealed record CompensationPeriod(string Component, decimal Amount, string? Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public enum CompensationOutcome
{
    /// <summary>The employee has values in the set that was asked for; the list may still be empty.</summary>
    Found,

    /// <summary>The employee has no values in this tenant, or none known at the requested instant.</summary>
    EmployeeNotFound,

    /// <summary>The requested instant is later than the request time on the database clock.</summary>
    KnownAtAfterRequestTime,
}

public sealed record CompensationResult(CompensationOutcome Outcome, IReadOnlyList<CompensationPeriod> Periods);

/// <summary>
/// Reads the values of one employee inside a tenant scope: the current ones (PRP-001), or the ones known at an
/// instant (PRP-002 3.1), in which case the read first waits for a batch of the tenant that is being applied.
/// </summary>
public sealed class CompensationQueries(NpgsqlDataSource dataSource)
{
    private const string Current = "superseded_at IS NULL";

    // Both bounds matter: at a batch's apply time its new values are known and the ones it superseded are not.
    private const string KnownAtInstant = "recorded_at <= @KnownAt AND (superseded_at IS NULL OR superseded_at > @KnownAt)";

    /// <summary>
    /// Values in effect on the date, by component code. With <paramref name="knownAt"/>, among the values known at
    /// that instant; without it, among the current ones.
    /// </summary>
    public Task<CompensationResult> GetAsOfAsync(Guid tenantId, string employeeId, DateOnly asOf, DateTime? knownAt, CancellationToken cancellationToken) =>
        QueryAsync(
            tenantId,
            employeeId,
            knownAt,
            """
            WHERE effective_from <= @AsOf::date AND (effective_to IS NULL OR effective_to >= @AsOf::date)
            ORDER BY component COLLATE "C"
            """,
            new { TenantId = tenantId, EmployeeId = employeeId, KnownAt = knownAt, AsOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            cancellationToken);

    /// <summary>
    /// All periods, by component code and start date, optionally for one component. With <paramref name="knownAt"/>,
    /// the periods of the values known at that instant; without it, of the current ones.
    /// </summary>
    public Task<CompensationResult> GetHistoryAsync(Guid tenantId, string employeeId, string? component, DateTime? knownAt, CancellationToken cancellationToken) =>
        QueryAsync(
            tenantId,
            employeeId,
            knownAt,
            """
            WHERE @Component::text IS NULL OR component = @Component
            ORDER BY component COLLATE "C", effective_from
            """,
            new { TenantId = tenantId, EmployeeId = employeeId, KnownAt = knownAt, Component = component },
            cancellationToken);

    /// <summary>
    /// The periods of one employee among the values the filter selects. A period ends the day before the next later
    /// value of the same component starts. The filter runs before the window function, so a value outside the set
    /// (superseded, or recorded after the instant) never splits a period.
    /// </summary>
    private static string Periods(string valueFilter) =>
        $"""
        WITH periods AS (
          SELECT component, amount, currency::text AS currency, effective_from,
                 lead(effective_from) OVER (PARTITION BY component ORDER BY effective_from) - 1 AS effective_to
          FROM compensation_values
          WHERE tenant_id = @TenantId AND employee_id = @EmployeeId AND {valueFilter}
        )
        SELECT component AS Component, amount AS Amount, currency AS Currency,
               effective_from AS EffectiveFrom, effective_to AS EffectiveTo
        FROM periods

        """;

    private async Task<CompensationResult> QueryAsync(
        Guid tenantId, string employeeId, DateTime? knownAt, string filterAndOrder, object parameters, CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);

        if (knownAt is { } instant)
        {
            // Lock first, read in later statements. An instant after the request time is refused: a batch applied
            // after this request could still get an earlier apply time, so the answer could change.
            var requestTime = await scope.WaitForAppliesAsync(cancellationToken);
            if (instant > requestTime)
            {
                return new CompensationResult(CompensationOutcome.KnownAtAfterRequestTime, []);
            }
        }

        var rows = (await scope.Connection.QueryAsync<PeriodRow>(new CommandDefinition(
            Periods(knownAt is null ? Current : KnownAtInstant) + filterAndOrder,
            parameters,
            scope.Transaction,
            cancellationToken: cancellationToken))).AsList();

        if (rows.Count == 0)
        {
            // Existence is decided over the same set: any value at all, or any value known at the instant.
            var employeeExists = await scope.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                knownAt is null
                    ? "SELECT EXISTS (SELECT 1 FROM compensation_values WHERE tenant_id = @TenantId AND employee_id = @EmployeeId)"
                    : $"SELECT EXISTS (SELECT 1 FROM compensation_values WHERE tenant_id = @TenantId AND employee_id = @EmployeeId AND {KnownAtInstant})",
                new { TenantId = tenantId, EmployeeId = employeeId, KnownAt = knownAt },
                scope.Transaction,
                cancellationToken: cancellationToken));
            if (!employeeExists)
            {
                return new CompensationResult(CompensationOutcome.EmployeeNotFound, []);
            }
        }

        await scope.CommitAsync(cancellationToken);
        return new CompensationResult(
            CompensationOutcome.Found,
            rows.ConvertAll(row => new CompensationPeriod(
                row.Component,
                row.Amount,
                row.Currency,
                DateOnly.FromDateTime(row.EffectiveFrom),
                row.EffectiveTo is { } effectiveTo ? DateOnly.FromDateTime(effectiveTo) : null)));
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

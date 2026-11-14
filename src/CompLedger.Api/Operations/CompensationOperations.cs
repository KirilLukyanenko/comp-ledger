using CompLedger.Core;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Queries;
using Microsoft.Extensions.Primitives;

namespace CompLedger.Api.Operations;

/// <summary>
/// The two employee reads (PRP-001 3.5, PRP-002 3.3). Inputs are the raw strings as given: a parameter with no
/// values was not given, one with several was given more than once.
/// </summary>
public sealed class CompensationOperations(CompensationQueries queries)
{
    // Order of checks on both reads: parameters (400), then the lookup (404). Whether knownAt is later than the
    // request time is known only on the database clock, after the wait for a batch being applied; it is still decided
    // before the lookup.

    public async Task<OperationResult> GetAsOfAsync(
        Guid tenantId, string employeeId, StringValues asOfValues, StringValues knownAtValues, CancellationToken cancellationToken)
    {
        DateOnly asOf;
        if (asOfValues.Count == 0)
        {
            asOf = DateOnly.FromDateTime(DateTime.UtcNow);
        }
        else if (asOfValues.Count > 1 || RowParser.CheckDate(asOfValues[0] ?? "", out asOf) != DateCheck.Valid)
        {
            return OperationResult.Problem(
                StatusCodes.Status400BadRequest, "asOf must be given at most once, as a real yyyy-MM-dd date from 1900-01-01 to 2999-12-31.");
        }

        if (!TryReadKnownAt(knownAtValues, out var knownAt))
        {
            return InvalidKnownAt();
        }

        var result = await queries.GetAsOfAsync(tenantId, employeeId, asOf, knownAt, cancellationToken);
        return result.Outcome switch
        {
            CompensationOutcome.KnownAtAfterRequestTime => InvalidKnownAt(),
            CompensationOutcome.EmployeeNotFound => EmployeeNotFound(),
            _ => OperationResult.Ok(new AsOfResponse(employeeId, asOf, FormatKnownAt(knownAt), result.Periods)),
        };
    }

    public async Task<OperationResult> GetHistoryAsync(
        Guid tenantId, string employeeId, StringValues componentValues, StringValues knownAtValues, CancellationToken cancellationToken)
    {
        string? component = null;
        if (componentValues.Count > 0)
        {
            component = componentValues.Count == 1 ? Components.Find(componentValues[0] ?? "")?.Code : null;
            if (component is null)
            {
                return OperationResult.Problem(
                    StatusCodes.Status400BadRequest, "component must be given at most once, as an exact code from the component catalogue.");
            }
        }

        if (!TryReadKnownAt(knownAtValues, out var knownAt))
        {
            return InvalidKnownAt();
        }

        var result = await queries.GetHistoryAsync(tenantId, employeeId, component, knownAt, cancellationToken);
        return result.Outcome switch
        {
            CompensationOutcome.KnownAtAfterRequestTime => InvalidKnownAt(),
            CompensationOutcome.EmployeeNotFound => EmployeeNotFound(),
            _ => OperationResult.Ok(new HistoryResponse(employeeId, FormatKnownAt(knownAt), result.Periods)),
        };
    }

    internal static OperationResult EmployeeNotFound() =>
        OperationResult.Problem(StatusCodes.Status404NotFound, "The employee has no compensation values.");

    /// <summary>
    /// Reads the optional <c>knownAt</c> parameter (PRP-002 3.2). False when it is given more than once, is empty or
    /// is not an accepted instant. The upper bound, the request time, is checked later on the database clock.
    /// </summary>
    private static bool TryReadKnownAt(StringValues values, out DateTime? knownAt)
    {
        knownAt = null;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count > 1 || !KnownAt.TryParse(values[0], out var instant))
        {
            return false;
        }

        knownAt = instant;
        return true;
    }

    /// <summary>The instant in the response format, or null when there is none.</summary>
    private static string? FormatKnownAt(DateTime? knownAt) => knownAt is { } instant ? KnownAt.Format(instant) : null;

    private static OperationResult InvalidKnownAt() =>
        OperationResult.Problem(
            StatusCodes.Status400BadRequest,
            "knownAt must be given at most once, as a UTC instant such as 2026-11-13T14:22:31.123456Z with at most six fractional digits, "
            + "from 1900-01-01T00:00:00Z up to the time of the request.");

    /// <summary><see cref="KnownAt"/> is null when the parameter was not given, otherwise the requested instant.</summary>
    private sealed record AsOfResponse(string EmployeeId, DateOnly AsOf, string? KnownAt, IReadOnlyList<CompensationPeriod> Components);

    private sealed record HistoryResponse(string EmployeeId, string? KnownAt, IReadOnlyList<CompensationPeriod> Periods);
}

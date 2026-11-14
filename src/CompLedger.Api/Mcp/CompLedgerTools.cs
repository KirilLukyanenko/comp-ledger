using CompLedger.Api.Operations;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Server;

namespace CompLedger.Api.Mcp;

/// <summary>
/// The seven tools of PRP-005 3.3, each over the handler of its REST endpoint. There are no others: nothing uploads
/// a file, creates a tenant or deletes anything.
/// </summary>
internal static class CompLedgerTools
{
    private const string DateFormat = "a calendar date as yyyy-MM-dd (for example 2026-03-31), from 1900-01-01 to 2999-12-31";

    private const string InstantFormat =
        "a UTC instant ending in Z with up to six fractional digits (for example 2026-11-13T14:22:31.123456Z), not later than now; offsets such as +01:00 are refused";

    private const string Errors =
        "Errors are returned as a tool error holding a ProblemDetails object: status 400 for an argument in the wrong format, 404 when nothing is found.";

    private static readonly ToolArgument EmployeeId = new(
        "employeeId", Required: true, "The employee id exactly as stored, case-sensitive, for example E-1001: 1 to 64 letters, digits, '.', '_' or '-'.");

    private static readonly ToolArgument SnapshotId = new(
        "snapshotId", Required: true, "The snapshot id: a hyphenated UUID as returned by create_snapshot or list_snapshots.");

    private static readonly ToolArgument KnownAt = new(
        "knownAt",
        Required: false,
        $"Optional. Answer from the values as they were known at this instant, ignoring later corrections: {InstantFormat}. Without it, the current values.");

    public static IReadOnlyList<McpServerTool> All { get; } =
    [
        new CompLedgerTool(
            "get_compensation",
            "Get an employee's compensation",
            "Returns the compensation components (base salary, allowance, bonus target) in effect for one employee on a date, "
            + $"optionally as they were known at an earlier instant. Dates are {DateFormat}; instants are {InstantFormat}. {Errors}",
            readOnly: true,
            [
                EmployeeId,
                new ToolArgument("asOf", Required: false, $"Optional. The date the values must be in effect on: {DateFormat}. Without it, today (UTC)."),
                KnownAt,
            ],
            (services, tenantId, arguments, cancellationToken) => services.GetRequiredService<CompensationOperations>().GetAsOfAsync(
                tenantId, arguments.Required("employeeId"), arguments.Optional("asOf"), arguments.Optional("knownAt"), cancellationToken)),

        new CompLedgerTool(
            "get_compensation_history",
            "Get an employee's compensation history",
            "Returns every period of every compensation component of one employee, by component and start date, optionally for one component "
            + $"and optionally as known at an earlier instant. Component codes are case-sensitive: BASE_SALARY, ALLOWANCE, BONUS_TARGET_PCT. Instants are {InstantFormat}. {Errors}",
            readOnly: true,
            [
                EmployeeId,
                new ToolArgument("component", Required: false, "Optional. One exact, case-sensitive component code: BASE_SALARY, ALLOWANCE or BONUS_TARGET_PCT. Without it, all components."),
                KnownAt,
            ],
            (services, tenantId, arguments, cancellationToken) => services.GetRequiredService<CompensationOperations>().GetHistoryAsync(
                tenantId, arguments.Required("employeeId"), arguments.Optional("component"), arguments.Optional("knownAt"), cancellationToken)),

        new CompLedgerTool(
            "get_ingestion",
            "Get an ingestion batch",
            "Returns one uploaded batch: its status (applied or rejected), row counts, apply time and validation errors. "
            + "Status 404 as a tool error when the batch does not exist.",
            readOnly: true,
            [new ToolArgument("batchId", Required: true, "The batch id: a hyphenated UUID as returned when the file was uploaded.")],
            (services, tenantId, arguments, cancellationToken) =>
                services.GetRequiredService<IngestionOperations>().GetAsync(tenantId, arguments.Required("batchId"), cancellationToken)),

        new CompLedgerTool(
            "list_snapshots",
            "List snapshots",
            "Returns every snapshot of the tenant: its id, name, as-of date (yyyy-MM-dd) and the UTC instant it was taken at. Takes no arguments.",
            readOnly: true,
            [],
            (services, tenantId, _, cancellationToken) => services.GetRequiredService<SnapshotOperations>().ListAsync(tenantId, cancellationToken)),

        new CompLedgerTool(
            "get_snapshot",
            "Get a snapshot",
            "Returns one snapshot: its id, name, as-of date (yyyy-MM-dd) and the UTC instant it was taken at. Status 404 as a tool error when it does not exist.",
            readOnly: true,
            [SnapshotId],
            (services, tenantId, arguments, cancellationToken) =>
                services.GetRequiredService<SnapshotOperations>().GetAsync(tenantId, arguments.Required("snapshotId"), cancellationToken)),

        new CompLedgerTool(
            "get_snapshot_compensation",
            "Get an employee's compensation in a snapshot",
            "Returns the compensation components of one employee as frozen by a snapshot: in effect on the snapshot's date, as known at the snapshot's instant. "
            + "Later corrections do not change the answer. Status 404 as a tool error when the snapshot does not exist or the employee had no values then.",
            readOnly: true,
            [SnapshotId, EmployeeId],

            // The tool has no asOf and no knownAt, so the equivalent request never carries the two refused parameters.
            (services, tenantId, arguments, cancellationToken) => services.GetRequiredService<SnapshotOperations>().GetEmployeeAsync(
                tenantId, arguments.Required("snapshotId"), arguments.Required("employeeId"), StringValues.Empty, StringValues.Empty, cancellationToken)),

        new CompLedgerTool(
            "create_snapshot",
            "Create a snapshot",
            "Creates a named snapshot that freezes the compensation data as known right now for an as-of date, and returns it with its snapshotId. "
            + $"The date is {DateFormat}. Errors are tool errors holding a ProblemDetails object: status 400 for a bad name or date, 409 when the name is already taken.",
            readOnly: false,
            [
                new ToolArgument("name", Required: true, "The snapshot's name: 1 to 200 characters after trimming spaces, unique within the tenant."),
                new ToolArgument("asOf", Required: true, $"The as-of date of the snapshot: {DateFormat}."),
            ],

            // The body of the equivalent request is built from the two arguments, so only its value rules apply. The
            // name conflict (409) and the snapshot's log entry come from the handler.
            (services, tenantId, arguments, cancellationToken) => services.GetRequiredService<SnapshotOperations>().CreateAsync(
                tenantId, arguments.Required("name"), arguments.Required("asOf"), cancellationToken)),
    ];
}

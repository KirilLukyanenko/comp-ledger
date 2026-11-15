using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using CompLedger.Api.Operations;
using CompLedger.Api.Tenancy;
using CompLedger.Core;
using CompLedger.Core.Export;
using CompLedger.Infrastructure.Export;

namespace CompLedger.Api.Endpoints;

/// <summary>
/// The streaming export (PRP-004): every value in effect on a date as known at an instant, as one CSV file, for the
/// whole tenant or through a snapshot. Every refusal is decided, and the instant fixed, before the first byte.
/// </summary>
public static partial class ExportEndpoints
{
    public const string ExportPath = "/api/v1/compensation/export";

    /// <summary>Rows are flushed once this much is waiting, so the first bytes leave early and memory stays flat.</summary>
    private const int FlushBytes = 32 * 1024;

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ExportPath, ExportAsync).RequireTenant();

        // The id is bound as a string, as on the other snapshot paths: a malformed one is a failed lookup (404) after
        // the tenant and query checks, and a wrong method on the path is 405 whatever the segment holds.
        app.MapGet(SnapshotOperations.SnapshotsPath + "/{snapshotId}/export", SnapshotExportAsync).RequireTenant();
    }

    // Order of checks: tenant (401, middleware), the query (400), the snapshot (404), then the wait and the knownAt
    // bound on the database clock (400). Other query parameters, include among them, and Accept are ignored.

    private static async Task<IResult> ExportAsync(HttpContext context, ExportQuery query, ILoggerFactory loggerFactory)
    {
        var parameters = context.Request.Query;
        if (!CompensationOperations.TryReadAsOf(parameters["asOf"], out var asOf))
        {
            return CompensationOperations.InvalidAsOf().ToHttpResult();
        }

        if (!CompensationOperations.TryReadKnownAt(parameters["knownAt"], out var knownAt))
        {
            return CompensationOperations.InvalidKnownAt().ToHttpResult();
        }

        var tenantId = context.GetTenantId();
        var requestTime = await query.FixInstantAsync(tenantId, context.RequestAborted);
        if (knownAt > requestTime)
        {
            // A batch applied after this request could still get an earlier apply time: the file could change.
            return CompensationOperations.InvalidKnownAt().ToHttpResult();
        }

        return new CsvExport(query, Logger(loggerFactory), tenantId, null, asOf, knownAt ?? requestTime);
    }

    private static async Task<IResult> SnapshotExportAsync(
        string snapshotId, HttpContext context, SnapshotOperations snapshots, ExportQuery query, ILoggerFactory loggerFactory)
    {
        // The snapshot fixes both coordinates, so these two parameters are named only to refuse them, with any value.
        var parameters = context.Request.Query;
        if (parameters["asOf"].Count > 0 || parameters["knownAt"].Count > 0)
        {
            return OperationResult.Problem(
                    StatusCodes.Status400BadRequest, "A snapshot export takes no asOf and no knownAt: the snapshot fixes both.")
                .ToHttpResult();
        }

        var tenantId = context.GetTenantId();
        var snapshot = await snapshots.FindAsync(tenantId, snapshotId, context.RequestAborted);
        if (snapshot is null)
        {
            return SnapshotOperations.SnapshotNotFound().ToHttpResult();
        }

        // Like any export, it waits for a batch of the tenant being applied. The snapshot's instant came from this
        // database's clock, so it is never later than the request time.
        await query.FixInstantAsync(tenantId, context.RequestAborted);
        return new CsvExport(query, Logger(loggerFactory), tenantId, snapshot.SnapshotId, snapshot.AsOf, snapshot.KnownAt);
    }

    private static ILogger Logger(ILoggerFactory loggerFactory) => loggerFactory.CreateLogger("CompLedger.Export");

    [LoggerMessage(
        Message = "Export of tenant {TenantId} {Outcome}: snapshot {SnapshotId}, as of {AsOf}, known at {KnownAt}, {Rows} rows, {DurationMs} ms")]
    private static partial void LogExport(
        ILogger logger, LogLevel level, Exception? exception, string tenantId, string? snapshotId, string asOf, string knownAt, long rows, string outcome, long durationMs);

    /// <summary>
    /// Writes the file as it reads it: headers and the header line first, then one line per row, flushed now and
    /// then. Nothing collects the rows. After the first byte the status cannot change, so a failure or a cancellation
    /// aborts the connection instead of finishing the file or writing an error.
    /// </summary>
    private sealed class CsvExport(ExportQuery query, ILogger logger, Guid tenantId, Guid? snapshotId, DateOnly asOf, DateTime knownAt) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var stopwatch = Stopwatch.StartNew();
            var cancellationToken = httpContext.RequestAborted;
            var response = httpContext.Response;
            var asOfText = ExportCsv.FormatDate(asOf);
            var knownAtText = KnownAt.Format(knownAt);

            // No Content-Length: the length is not known in advance, so the body is sent chunked.
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/csv; charset=utf-8";
            response.Headers["X-As-Of"] = asOfText;
            response.Headers["X-Known-At"] = knownAtText;

            long rows = 0;
            Exception? failure = null;
            var completed = false;
            try
            {
                var writer = response.BodyWriter;
                Encoding.UTF8.GetBytes(ExportCsv.HeaderLine, writer);
                await FlushAsync(writer, cancellationToken);

                long unflushed = 0;
                await foreach (var row in query.ReadAsync(tenantId, asOf, knownAt, cancellationToken))
                {
                    unflushed += Encoding.UTF8.GetBytes(
                        ExportCsv.Line(row.EmployeeId, row.Component, row.EffectiveFrom, row.EffectiveTo, row.Amount, row.Currency), writer);
                    rows++;
                    if (unflushed >= FlushBytes)
                    {
                        await FlushAsync(writer, cancellationToken);
                        unflushed = 0;
                    }
                }

                await FlushAsync(writer, cancellationToken);
                completed = true;
            }
            catch (Exception ex)
            {
                // A client that went away is not an error of the service; anything else is, but either way the status is
                // already sent: no ProblemDetails, no rest of the file.
                failure = ex is OperationCanceledException || cancellationToken.IsCancellationRequested ? null : ex;
                httpContext.Abort();
            }

            // One entry per export that sent a 200. Ids, coordinates and a count: never a row's contents.
            LogExport(
                logger,
                failure is null ? LogLevel.Information : LogLevel.Warning,
                failure,
                tenantId.ToString("D"),
                snapshotId?.ToString("D"),
                asOfText,
                knownAtText,
                rows,
                completed ? "Completed" : "Aborted",
                stopwatch.ElapsedMilliseconds);
        }

        private static async Task FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            var result = await writer.FlushAsync(cancellationToken);
            if (result.IsCanceled || result.IsCompleted)
            {
                // The reader side is gone: the client will not read the rest.
                throw new OperationCanceledException("The client stopped reading the export.", cancellationToken);
            }
        }
    }
}

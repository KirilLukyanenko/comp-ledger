using System.Diagnostics;
using CompLedger.Api.Tenancy;
using CompLedger.Infrastructure.Ingestion;
using Microsoft.AspNetCore.Mvc;

namespace CompLedger.Api.Endpoints;

public static partial class IngestionEndpoints
{
    public const long DefaultMaxBodyBytes = 256L * 1024 * 1024;

    private const string BatchPath = "/api/v1/ingestions";

    public static void MapIngestionEndpoints(this IEndpointRouteBuilder app, long maxBodyBytes)
    {
        // The size limit is raised (or lowered) for this endpoint only; Kestrel enforces it while the body streams.
        app.MapPost(BatchPath, (HttpContext context, PostgresIngestor ingestor, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
                IngestAsync(context, ingestor, loggerFactory.CreateLogger("CompLedger.Ingestion"), maxBodyBytes, cancellationToken))
            .RequireTenant()
            .WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes));

        app.MapGet(BatchPath + "/{batchId}", GetAsync).RequireTenant();
    }

    private static async Task<IResult> IngestAsync(
        HttpContext context, PostgresIngestor ingestor, ILogger logger, long maxBodyBytes, CancellationToken cancellationToken)
    {
        // The tenant was checked by the middleware (401). Then content type, then the declared size; nothing of the
        // body has been read at this point.
        var request = context.Request;
        if (!RequestChecks.HasMediaType(request, "text/csv"))
        {
            return RequestChecks.Problem(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be text/csv (UTF-8).");
        }

        if (request.ContentLength > maxBodyBytes)
        {
            return TooLarge(maxBodyBytes);
        }

        var tenantId = context.GetTenantId();
        var stopwatch = Stopwatch.StartNew();
        IngestionBatch batch;
        try
        {
            batch = await ingestor.IngestAsync(tenantId, request.Body, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A body without a declared length went over the limit while streaming. Nothing was applied or recorded.
            return TooLarge(maxBodyBytes);
        }

        // The one log entry of the batch. Ids and counts only: never row contents or amounts.
        LogBatch(
            logger,
            tenantId.ToString("D"),
            batch.BatchId.ToString("D"),
            batch.Status,
            batch.Rows.Total,
            batch.Rows.Inserted,
            batch.Rows.Corrected,
            batch.Rows.Unchanged,
            batch.ErrorCount,
            stopwatch.ElapsedMilliseconds);

        return batch.Status == BatchStatus.Applied
            ? Results.Created($"{BatchPath}/{batch.BatchId:D}", batch)
            : Results.Json(batch, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(string batchId, HttpContext context, PostgresIngestor ingestor, CancellationToken cancellationToken)
    {
        // An id that is not a UUID cannot exist, and an id of another tenant must look the same as a missing one.
        var batch = Guid.TryParseExact(batchId, "D", out var id)
            ? await ingestor.FindBatchAsync(context.GetTenantId(), id, cancellationToken)
            : null;

        return batch is null
            ? RequestChecks.Problem(StatusCodes.Status404NotFound, "The batch does not exist.")
            : Results.Json(batch);
    }

    private static IResult TooLarge(long maxBodyBytes) =>
        RequestChecks.Problem(StatusCodes.Status413PayloadTooLarge, $"The body is larger than {maxBodyBytes} bytes.");

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Ingestion batch {BatchId} of tenant {TenantId} finished as {Status}: {TotalRows} rows, {InsertedRows} inserted, "
            + "{CorrectedRows} corrected, {UnchangedRows} unchanged, {ErrorCount} errors, {DurationMs} ms")]
    private static partial void LogBatch(
        ILogger logger,
        string tenantId,
        string batchId,
        string status,
        int totalRows,
        int insertedRows,
        int correctedRows,
        int unchangedRows,
        int errorCount,
        long durationMs);
}

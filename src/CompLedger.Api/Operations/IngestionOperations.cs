using CompLedger.Infrastructure.Ingestion;

namespace CompLedger.Api.Operations;

/// <summary>Reading one batch (PRP-001 3.5). The upload stays on its endpoint: it streams a request body.</summary>
public sealed class IngestionOperations(PostgresIngestor ingestor)
{
    public async Task<OperationResult> GetAsync(Guid tenantId, string batchId, CancellationToken cancellationToken)
    {
        // An id that is not a UUID cannot exist, and an id of another tenant must look the same as a missing one.
        var batch = Guid.TryParseExact(batchId, "D", out var id)
            ? await ingestor.FindBatchAsync(tenantId, id, cancellationToken)
            : null;

        return batch is null
            ? OperationResult.Problem(StatusCodes.Status404NotFound, "The batch does not exist.")
            : OperationResult.Ok(batch);
    }
}

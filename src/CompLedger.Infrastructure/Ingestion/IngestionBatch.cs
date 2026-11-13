using CompLedger.Core.Ingestion;

namespace CompLedger.Infrastructure.Ingestion;

public static class BatchStatus
{
    public const string Applied = "Applied";
    public const string Rejected = "Rejected";
}

public sealed record BatchRows(int Total, int Inserted, int Corrected, int Unchanged);

/// <summary>The batch resource of PRP-001 3.6. Timestamps are UTC. <see cref="Errors"/> holds at most 100 entries.</summary>
public sealed record IngestionBatch(
    Guid BatchId,
    string Status,
    DateTime ReceivedAt,
    DateTime CompletedAt,
    BatchRows Rows,
    int ErrorCount,
    IReadOnlyList<IngestionError> Errors);

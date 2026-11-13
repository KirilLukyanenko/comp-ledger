using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CompLedger.Core.Ingestion;

namespace CompLedger.Infrastructure.Ingestion;

public static class BatchStatus
{
    public const string Applied = "Applied";
    public const string Rejected = "Rejected";
}

public sealed record BatchRows(int Total, int Inserted, int Corrected, int Unchanged);

/// <summary>
/// The batch resource of PRP-001 3.6, with the apply time of PRP-002 3.5. Timestamps are UTC.
/// <see cref="AppliedAt"/> is null for a rejected batch. <see cref="Errors"/> holds at most 100 entries.
/// </summary>
public sealed record IngestionBatch(
    Guid BatchId,
    string Status,
    DateTime ReceivedAt,
    DateTime CompletedAt,
    [property: JsonConverter(typeof(InstantJsonConverter))] DateTime? AppliedAt,
    BatchRows Rows,
    int ErrorCount,
    IReadOnlyList<IngestionError> Errors);

/// <summary>Writes an instant in the PRP-002 response format: UTC with exactly six fractional digits.</summary>
public sealed class InstantJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Instants are only written.");

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
}

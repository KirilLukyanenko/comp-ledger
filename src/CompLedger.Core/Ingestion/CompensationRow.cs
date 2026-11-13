namespace CompLedger.Core.Ingestion;

/// <summary>One validated data row of an upload. <see cref="Currency"/> is null when the component takes none.</summary>
public readonly record struct CompensationRow(
    int RowNumber,
    string EmployeeId,
    string Component,
    DateOnly EffectiveFrom,
    decimal Amount,
    string? Currency);

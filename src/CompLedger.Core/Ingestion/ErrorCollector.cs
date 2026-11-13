namespace CompLedger.Core.Ingestion;

/// <summary>The error codes of PRP-001 3.4.</summary>
public static class ErrorCodes
{
    public const string EmptyFile = "EMPTY_FILE";
    public const string InvalidEncoding = "INVALID_ENCODING";
    public const string MissingColumn = "MISSING_COLUMN";
    public const string UnknownColumn = "UNKNOWN_COLUMN";
    public const string DuplicateColumn = "DUPLICATE_COLUMN";
    public const string MalformedCsv = "MALFORMED_CSV";
    public const string InvalidEmployeeId = "INVALID_EMPLOYEE_ID";
    public const string UnknownComponent = "UNKNOWN_COMPONENT";
    public const string InvalidDate = "INVALID_DATE";
    public const string DateOutOfRange = "DATE_OUT_OF_RANGE";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string AmountOutOfRange = "AMOUNT_OUT_OF_RANGE";
    public const string CurrencyRequired = "CURRENCY_REQUIRED";
    public const string CurrencyNotAllowed = "CURRENCY_NOT_ALLOWED";
    public const string InvalidCurrency = "INVALID_CURRENCY";
    public const string DuplicateKeyInFile = "DUPLICATE_KEY_IN_FILE";
}

/// <summary>One error of a rejected batch. <see cref="Message"/> is free text and never contains row values.</summary>
public sealed record IngestionError(int? Row, string? Column, string Code, string Message);

/// <summary>
/// Counts every error and keeps the first <see cref="MaxKept"/> in report order: by row with file-level errors
/// (no row) first, then by the order value the caller gives within the row, then by arrival.
/// Memory stays bounded however many errors a file has.
/// </summary>
public sealed class ErrorCollector
{
    public const int MaxKept = 100;

    /// <summary>Order value for file-level and row-level errors, which come before the field errors of their row.</summary>
    public const int RowLevelOrder = -1;

    private readonly List<Entry> _kept = new(MaxKept + 1);

    /// <summary>The full number of errors, including those not kept.</summary>
    public int Count { get; private set; }

    public bool HasErrors => Count > 0;

    /// <summary>The kept errors, in report order.</summary>
    public IReadOnlyList<IngestionError> ToList() => _kept.ConvertAll(entry => entry.Error);

    public void Add(IngestionError error, int order)
    {
        Count++;
        var entry = new Entry(error.Row ?? 0, order, error);

        // Errors mostly arrive in report order, so the scan from the end usually stops at once.
        var index = _kept.Count;
        while (index > 0 && entry.ComesBefore(_kept[index - 1]))
        {
            index--;
        }

        if (index >= MaxKept)
        {
            return;
        }

        _kept.Insert(index, entry);
        if (_kept.Count > MaxKept)
        {
            _kept.RemoveAt(MaxKept);
        }
    }

    /// <summary>Counts errors that are known to come after every kept error and so need not be materialised.</summary>
    public void AddUnlisted(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count += count;
    }

    private readonly record struct Entry(int RowKey, int Order, IngestionError Error)
    {
        public bool ComesBefore(Entry other) =>
            RowKey != other.RowKey ? RowKey < other.RowKey : Order < other.Order;
    }
}

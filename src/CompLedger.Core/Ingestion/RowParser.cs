using System.Globalization;
using System.Text.RegularExpressions;

namespace CompLedger.Core.Ingestion;

public enum DateCheck
{
    Valid,
    Invalid,
    OutOfRange,
}

/// <summary>
/// Validates the records of one CSV file in order (PRP-001 3.3 and 3.4): the header first, then each data record.
/// The caller splits the file into records and says whether a record could be parsed; this class owns row numbers,
/// blank-line handling, the field rules and the error report. It holds one record at a time and does no I/O.
/// </summary>
public sealed partial class RowParser(ErrorCollector errors)
{
    public static readonly DateOnly MinDate = new(1900, 1, 1);
    public static readonly DateOnly MaxDate = new(2999, 12, 31);

    private const int EmployeeIdColumn = 0;
    private const int ComponentColumn = 1;
    private const int EffectiveFromColumn = 2;
    private const int AmountColumn = 3;
    private const int CurrencyColumn = 4;

    /// <summary>Header errors for missing columns come after the errors ordered by position in the file.</summary>
    private const int MissingColumnOrderBase = int.MaxValue - 16;

    private static readonly char[] TrimChars = [' ', '\t', '\r', '\n'];

    /// <summary>For each canonical column, the index of its field in a record. Null until a valid header was read.</summary>
    private int[]? _fieldIndex;
    private int _recordNumber;
    private int _pendingBlankRecords;
    private bool _encodingFailed;

    /// <summary>The required columns, in the canonical order used for error reports.</summary>
    public static IReadOnlyList<string> CanonicalColumns { get; } =
        ["employee_id", "component", "effective_from", "amount", "currency"];

    /// <summary>Data rows read so far. Trailing blank lines are not rows.</summary>
    public int TotalRows { get; private set; }

    /// <summary>True once the header has an error. No further records must be read.</summary>
    public bool HeaderFailed { get; private set; }

    /// <summary>Removes the CSV whitespace set (space, tab, CR, LF) from both ends, and nothing else.</summary>
    public static string Trim(string value) => value.Trim(TrimChars);

    /// <summary>Checks a <c>yyyy-MM-dd</c> value: format and real calendar date first, then the supported range.</summary>
    public static DateCheck CheckDate(string value, out DateOnly date)
    {
        date = default;
        if (!DatePattern().IsMatch(value)
            || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return DateCheck.Invalid;
        }

        return date < MinDate || date > MaxDate ? DateCheck.OutOfRange : DateCheck.Valid;
    }

    /// <summary>
    /// Takes the next record of the file. <paramref name="malformed"/> is true when the CSV reader could not parse it.
    /// Returns true and sets <paramref name="row"/> when the record is a data row without errors.
    /// </summary>
    public bool Accept(IReadOnlyList<string> fields, bool malformed, out CompensationRow row)
    {
        row = default;
        if (HeaderFailed || _encodingFailed)
        {
            throw new InvalidOperationException("No records are accepted after a header or encoding error.");
        }

        _recordNumber++;

        if (!malformed && fields.Count == 1 && Trim(fields[0]).Length == 0)
        {
            // A blank line. Whether it is a row or ignorable trailing space is known only when the next record comes.
            _pendingBlankRecords++;
            return false;
        }

        if (_fieldIndex is null)
        {
            AcceptHeader(fields, malformed);
            return false;
        }

        FlushPendingBlankRows();
        TotalRows++;

        if (malformed || fields.Count != CanonicalColumns.Count)
        {
            errors.Add(RowError(_recordNumber, ErrorCodes.MalformedCsv, "The row cannot be parsed or has the wrong number of fields."), ErrorCollector.RowLevelOrder);
            return false;
        }

        return TryParseFields(fields, _fieldIndex, _recordNumber, out row);
    }

    /// <summary>Call at the end of the file.</summary>
    public void Complete()
    {
        if (HeaderFailed || _encodingFailed)
        {
            return;
        }

        // Blank lines still pending are trailing ones and are ignored.
        if (_fieldIndex is null || TotalRows == 0)
        {
            errors.Add(new IngestionError(null, null, ErrorCodes.EmptyFile, "The file has no data rows."), ErrorCollector.RowLevelOrder);
        }
    }

    /// <summary>Call instead of <see cref="Complete"/> when the bytes stop being valid UTF-8. Reading must stop.</summary>
    public void FailEncoding()
    {
        _encodingFailed = true;
        errors.Add(new IngestionError(null, null, ErrorCodes.InvalidEncoding, "The file is not valid UTF-8."), ErrorCollector.RowLevelOrder);
    }

    private static IngestionError RowError(int row, string code, string message) => new(row, null, code, message);

    private void AcceptHeader(IReadOnlyList<string> fields, bool malformed)
    {
        if (_pendingBlankRecords > 0 || malformed)
        {
            // The first record was blank and something follows it, or the first record cannot be parsed.
            HeaderFailed = true;
            errors.Add(RowError(1, ErrorCodes.MalformedCsv, "The header row is blank or cannot be parsed."), ErrorCollector.RowLevelOrder);
            return;
        }

        var fieldIndex = new int[CanonicalColumns.Count];
        Array.Fill(fieldIndex, -1);

        for (var position = 0; position < fields.Count; position++)
        {
            var name = Trim(fields[position]);
            var column = IndexOfColumn(name);
            if (column < 0)
            {
                // The name as written, except U+0000: PostgreSQL jsonb cannot store it, so each one becomes U+FFFD.
                errors.Add(new IngestionError(1, name.Replace('\0', '�'), ErrorCodes.UnknownColumn, "The column is not part of the format."), position);
            }
            else if (fieldIndex[column] >= 0)
            {
                errors.Add(new IngestionError(1, CanonicalColumns[column], ErrorCodes.DuplicateColumn, "The column appears more than once."), position);
            }
            else
            {
                fieldIndex[column] = position;
            }
        }

        for (var column = 0; column < fieldIndex.Length; column++)
        {
            if (fieldIndex[column] < 0)
            {
                errors.Add(new IngestionError(1, CanonicalColumns[column], ErrorCodes.MissingColumn, "The required column is missing."), MissingColumnOrderBase + column);
            }
        }

        if (errors.HasErrors)
        {
            HeaderFailed = true;
        }
        else
        {
            _fieldIndex = fieldIndex;
        }
    }

    private static int IndexOfColumn(string name)
    {
        for (var column = 0; column < CanonicalColumns.Count; column++)
        {
            if (string.Equals(CanonicalColumns[column], name, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return -1;
    }

    private void FlushPendingBlankRows()
    {
        // The blank lines sit between the previous record and this one, so each of them is a row with an error.
        for (var row = _recordNumber - _pendingBlankRecords; row < _recordNumber; row++)
        {
            errors.Add(RowError(row, ErrorCodes.MalformedCsv, "The row is blank."), ErrorCollector.RowLevelOrder);
        }

        TotalRows += _pendingBlankRecords;
        _pendingBlankRecords = 0;
    }

    private bool TryParseFields(IReadOnlyList<string> fields, int[] fieldIndex, int rowNumber, out CompensationRow row)
    {
        row = default;
        var valid = true;

        void Fail(int column, string code, string message)
        {
            valid = false;
            errors.Add(new IngestionError(rowNumber, CanonicalColumns[column], code, message), column);
        }

        // At most one error per field; the fields are checked in canonical order.
        var employeeId = Trim(fields[fieldIndex[EmployeeIdColumn]]);
        if (!EmployeeIdPattern().IsMatch(employeeId))
        {
            Fail(EmployeeIdColumn, ErrorCodes.InvalidEmployeeId, "employee_id must be 1 to 64 characters from A-Z, a-z, 0-9, '.', '_' and '-', starting with a letter or digit.");
        }

        var componentCode = Trim(fields[fieldIndex[ComponentColumn]]);
        var component = Components.Find(componentCode);
        if (component is null)
        {
            Fail(ComponentColumn, ErrorCodes.UnknownComponent, "component is not in the catalogue.");
        }

        var dateCheck = CheckDate(Trim(fields[fieldIndex[EffectiveFromColumn]]), out var effectiveFrom);
        if (dateCheck == DateCheck.Invalid)
        {
            Fail(EffectiveFromColumn, ErrorCodes.InvalidDate, "effective_from must be a real date written as yyyy-MM-dd.");
        }
        else if (dateCheck == DateCheck.OutOfRange)
        {
            Fail(EffectiveFromColumn, ErrorCodes.DateOutOfRange, "effective_from must be from 1900-01-01 to 2999-12-31.");
        }

        var amountText = Trim(fields[fieldIndex[AmountColumn]]);
        var amount = 0m;
        if (!AmountPattern().IsMatch(amountText))
        {
            Fail(AmountColumn, ErrorCodes.InvalidAmount, "amount must have 1 to 15 digits and at most 4 decimals after a dot, with no sign, grouping or exponent.");
        }
        else
        {
            amount = decimal.Parse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

            // The range depends on the component, so it is skipped when the component is unknown.
            if (component is not null && amount > component.MaxAmount)
            {
                Fail(AmountColumn, ErrorCodes.AmountOutOfRange, "amount is outside the range of the component.");
            }
        }

        var currency = Trim(fields[fieldIndex[CurrencyColumn]]);
        if (component is { CurrencyRequired: true } && currency.Length == 0)
        {
            Fail(CurrencyColumn, ErrorCodes.CurrencyRequired, "The component needs a currency.");
        }
        else if (component is { CurrencyRequired: false } && currency.Length > 0)
        {
            Fail(CurrencyColumn, ErrorCodes.CurrencyNotAllowed, "The component takes no currency.");
        }
        else if (currency.Length > 0 && !CurrencyPattern().IsMatch(currency))
        {
            Fail(CurrencyColumn, ErrorCodes.InvalidCurrency, "currency must be three uppercase letters A-Z.");
        }

        if (!valid)
        {
            return false;
        }

        row = new CompensationRow(rowNumber, employeeId, component!.Code, effectiveFrom, amount, currency.Length == 0 ? null : currency);
        return true;
    }

    // ASCII classes with \A and \z only: \d matches other Unicode digits and $ matches before a final line break.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmployeeIdPattern();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\A[0-9]{1,15}(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\A[A-Z]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}

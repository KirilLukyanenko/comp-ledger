using System.Globalization;

namespace CompLedger.Core.Export;

/// <summary>
/// The export file format (PRP-004 3.5): UTF-8 without a BOM, LF after every line including the last, fields never
/// quoted. No value can need quoting: ids and codes follow the PRP-001 patterns, the rest are dates, numbers and
/// three letters.
/// </summary>
public static class ExportCsv
{
    /// <summary>The header line, with its line break. Always the first line, also of a file with no rows.</summary>
    public const string HeaderLine = "employee_id,component,effective_from,effective_to,amount,currency\n";

    /// <summary>
    /// One row with its line break. <paramref name="effectiveTo"/> is inclusive; null (open-ended) and a null
    /// <paramref name="currency"/> are written as empty fields.
    /// </summary>
    public static string Line(string employeeId, string component, DateOnly effectiveFrom, DateOnly? effectiveTo, decimal amount, string? currency) =>
        string.Concat(
            [
                employeeId, ",",
                component, ",",
                FormatDate(effectiveFrom), ",",
                effectiveTo is { } to ? FormatDate(to) : "", ",",
                FormatAmount(amount), ",",
                currency ?? "", "\n",
            ]);

    /// <summary>
    /// The shortest form of a stored amount: the integer part, then a dot and the fractional digits only when the
    /// fractional part is not zero, without trailing zeros. Invariant: no sign, grouping or exponent, always a dot.
    /// Stored amounts have at most four decimals (<c>numeric(19,4)</c>).
    /// </summary>
    public static string FormatAmount(decimal amount) => amount.ToString("0.####", CultureInfo.InvariantCulture);

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

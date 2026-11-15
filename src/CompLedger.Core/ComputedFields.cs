namespace CompLedger.Core;

/// <summary>
/// A component value in effect on the read's date, with its period as the as-of read gives it (PRP-001).
/// <see cref="EffectiveTo"/> is inclusive; null means open-ended.
/// </summary>
public sealed record ComputedInput(string Component, decimal Amount, string? Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>
/// One computed field (PRP-003 3.6). <see cref="Reason"/> is null exactly when <see cref="Amount"/> is not; a field
/// that is not computable has no amount, currency or period.
/// </summary>
public sealed record ComputedField(string Field, decimal? Amount, string? Currency, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string? Reason);

/// <summary>
/// The fixed catalogue of computed fields (PRP-003 3.2), their arithmetic (3.3) and periods (3.4). Pure: a function of
/// the inputs in effect and the start of the earliest later <c>ALLOWANCE</c>.
/// </summary>
public static class ComputedFields
{
    public const string TargetBonus = "TARGET_BONUS";

    public const string TotalFixedPay = "TOTAL_FIXED_PAY";

    public const string TotalTargetCash = "TOTAL_TARGET_CASH";

    public const string MissingInput = "MISSING_INPUT";

    public const string CurrencyMismatch = "CURRENCY_MISMATCH";

    /// <summary>The field codes in catalogue order, which is the response order.</summary>
    public static IReadOnlyList<string> Catalogue { get; } = [TargetBonus, TotalFixedPay, TotalTargetCash];

    /// <summary>
    /// The three computed fields, in catalogue order, on <paramref name="asOf"/>.
    /// </summary>
    /// <param name="asOf">The read's date.</param>
    /// <param name="inputs">The values in effect on <paramref name="asOf"/>, at most one per component.</param>
    /// <param name="nextAllowanceFrom">
    /// The earliest <c>effective_from</c> after <paramref name="asOf"/> of an <c>ALLOWANCE</c> among the values the
    /// read uses, or null when there is none.
    /// </param>
    public static IReadOnlyList<ComputedField> For(DateOnly asOf, IEnumerable<ComputedInput> inputs, DateOnly? nextAllowanceFrom)
    {
        if (nextAllowanceFrom is { } next && next <= asOf)
        {
            throw new ArgumentException("A later ALLOWANCE starts after the read's date.", nameof(nextAllowanceFrom));
        }

        ComputedInput? baseSalary = null, allowance = null, bonusPct = null;
        foreach (var input in inputs)
        {
            if (input.EffectiveFrom > asOf || input.EffectiveTo < asOf)
            {
                throw new ArgumentException("Every input must be in effect on the read's date.", nameof(inputs));
            }

            if (input.Component == Components.BaseSalary.Code)
            {
                baseSalary = input;
            }
            else if (input.Component == Components.Allowance.Code)
            {
                allowance = input;
            }
            else if (input.Component == Components.BonusTargetPct.Code)
            {
                bonusPct = input;
            }
        }

        // The range an ALLOWANCE covers for a field that uses it: its own period when it is in effect, otherwise from
        // the open past to the day before the next one starts (PRP-001 periods run until the next value).
        var allowanceRange = allowance is not null
            ? (From: (DateOnly?)allowance.EffectiveFrom, To: allowance.EffectiveTo)
            : (From: null, To: nextAllowanceFrom?.AddDays(-1));

        var allowanceMismatch = baseSalary is not null && allowance is not null
            && !string.Equals(allowance.Currency, baseSalary.Currency, StringComparison.Ordinal);

        ComputedField Field(string code, bool needsPct, bool usesAllowance, Func<decimal, decimal, decimal> amount)
        {
            if (baseSalary is null || (needsPct && bonusPct is null))
            {
                return NotComputable(code, MissingInput);
            }

            if (usesAllowance && allowanceMismatch)
            {
                return NotComputable(code, CurrencyMismatch);
            }

            var ranges = new List<(DateOnly? From, DateOnly? To)> { (baseSalary.EffectiveFrom, baseSalary.EffectiveTo) };
            if (needsPct)
            {
                ranges.Add((bonusPct!.EffectiveFrom, bonusPct.EffectiveTo));
            }

            if (usesAllowance)
            {
                ranges.Add(allowanceRange);
            }

            var (from, to) = Intersect(ranges);
            var bonus = needsPct ? RoundBonus(baseSalary.Amount * bonusPct!.Amount / 100m) : 0m;
            var fixedPay = baseSalary.Amount + (usesAllowance ? allowance?.Amount ?? 0m : 0m);
            return new ComputedField(code, amount(bonus, fixedPay), baseSalary.Currency, from, to, null);
        }

        return
        [
            Field(TargetBonus, needsPct: true, usesAllowance: false, (bonus, _) => bonus),
            Field(TotalFixedPay, needsPct: false, usesAllowance: true, (_, fixedPay) => fixedPay),

            // The rounded bonus, so the three amounts of a response always add up.
            Field(TotalTargetCash, needsPct: true, usesAllowance: true, (bonus, fixedPay) => fixedPay + bonus),
        ];
    }

    /// <summary>Rounds a target bonus to 4 decimal places, half away from zero (PRP-003 3.3).</summary>
    public static decimal RoundBonus(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The intersection of inclusive ranges: the latest start that is set (null if every range is open to the past),
    /// and the earliest end that is set (null if every range is open-ended).
    /// </summary>
    private static (DateOnly? From, DateOnly? To) Intersect(IEnumerable<(DateOnly? From, DateOnly? To)> ranges)
    {
        DateOnly? from = null, to = null;
        foreach (var range in ranges)
        {
            if (range.From is { } start && (from is null || start > from))
            {
                from = start;
            }

            if (range.To is { } end && (to is null || end < to))
            {
                to = end;
            }
        }

        return (from, to);
    }

    private static ComputedField NotComputable(string code, string reason) => new(code, null, null, null, null, reason);
}

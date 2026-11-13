namespace CompLedger.Core;

/// <summary>One entry of the fixed component catalogue (PRP-001 3.2). Amounts range from 0 to <see cref="MaxAmount"/>.</summary>
public sealed record ComponentDefinition(string Code, bool CurrencyRequired, decimal MaxAmount);

/// <summary>The component catalogue. Codes are matched exactly and case-sensitively.</summary>
public static class Components
{
    /// <summary>The largest money amount: 15 integer digits and 4 decimals.</summary>
    public const decimal MaxMoneyAmount = 999999999999999.9999m;

    public const decimal MaxBonusTargetPercent = 500m;

    public static readonly ComponentDefinition BaseSalary = new("BASE_SALARY", CurrencyRequired: true, MaxMoneyAmount);

    public static readonly ComponentDefinition Allowance = new("ALLOWANCE", CurrencyRequired: true, MaxMoneyAmount);

    public static readonly ComponentDefinition BonusTargetPct = new("BONUS_TARGET_PCT", CurrencyRequired: false, MaxBonusTargetPercent);

    public static IReadOnlyList<ComponentDefinition> All { get; } = [BaseSalary, Allowance, BonusTargetPct];

    public static ComponentDefinition? Find(string code)
    {
        foreach (var component in All)
        {
            if (string.Equals(component.Code, code, StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }
}

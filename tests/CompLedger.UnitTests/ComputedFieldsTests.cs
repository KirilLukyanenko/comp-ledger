using CompLedger.Core;

namespace CompLedger.UnitTests;

/// <summary>PRP-003 3.2 (catalogue, formulas, reasons), 3.3 (arithmetic) and 3.4 (computed period).</summary>
public sealed class ComputedFieldsTests
{
    private static readonly DateOnly AsOf = new(2026, 5, 15);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static ComputedInput Base(decimal amount, string currency = "EUR", DateOnly? from = null, DateOnly? to = null) =>
        new("BASE_SALARY", amount, currency, from ?? D(2026, 1, 1), to);

    private static ComputedInput Allowance(decimal amount, string currency = "EUR", DateOnly? from = null, DateOnly? to = null) =>
        new("ALLOWANCE", amount, currency, from ?? D(2026, 1, 1), to);

    private static ComputedInput Pct(decimal amount, DateOnly? from = null, DateOnly? to = null) =>
        new("BONUS_TARGET_PCT", amount, null, from ?? D(2026, 1, 1), to);

    private static IReadOnlyList<ComputedField> Compute(DateOnly? nextAllowance, params ComputedInput[] inputs) =>
        ComputedFields.For(AsOf, inputs, nextAllowance);

    private static ComputedField Get(IReadOnlyList<ComputedField> fields, string code) => Assert.Single(fields, f => f.Field == code);

    private static void AssertComputable(ComputedField field, decimal amount, string currency, DateOnly? from, DateOnly? to)
    {
        Assert.Equal(amount, field.Amount);
        Assert.Equal(currency, field.Currency);
        Assert.Equal(from, field.EffectiveFrom);
        Assert.Equal(to, field.EffectiveTo);
        Assert.Null(field.Reason);
    }

    private static void AssertNotComputable(ComputedField field, string reason)
    {
        Assert.Null(field.Amount);
        Assert.Null(field.Currency);
        Assert.Null(field.EffectiveFrom);
        Assert.Null(field.EffectiveTo);
        Assert.Equal(reason, field.Reason);
    }

    // ---------- catalogue ----------

    [Fact]
    public void Three_fields_are_returned_in_catalogue_order_whether_computable_or_not()
    {
        Assert.Equal(["TARGET_BONUS", "TOTAL_FIXED_PAY", "TOTAL_TARGET_CASH"], Compute(null).Select(f => f.Field));
        Assert.Equal(["TARGET_BONUS", "TOTAL_FIXED_PAY", "TOTAL_TARGET_CASH"], Compute(null, Base(1), Allowance(1), Pct(1)).Select(f => f.Field));
        Assert.Equal(["TARGET_BONUS", "TOTAL_FIXED_PAY", "TOTAL_TARGET_CASH"], ComputedFields.Catalogue);
    }

    // ---------- formulas ----------

    [Fact]
    public void Everything_in_effect_gives_each_formula()
    {
        var fields = Compute(null, Base(60000), Allowance(6000), Pct(10));

        AssertComputable(Get(fields, "TARGET_BONUS"), 6000m, "EUR", D(2026, 1, 1), null);
        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 66000m, "EUR", D(2026, 1, 1), null);
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 72000m, "EUR", D(2026, 1, 1), null);
    }

    [Fact]
    public void A_missing_allowance_counts_as_zero_in_both_totals()
    {
        var fields = Compute(null, Base(60000), Pct(10));

        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 60000m, "EUR", D(2026, 1, 1), null);
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 66000m, "EUR", D(2026, 1, 1), null);
    }

    [Fact]
    public void Zero_percent_gives_a_zero_bonus_and_cash_equal_to_fixed_pay()
    {
        var fields = Compute(null, Base(60000), Allowance(500), Pct(0));

        AssertComputable(Get(fields, "TARGET_BONUS"), 0m, "EUR", D(2026, 1, 1), null);
        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 60500m, "EUR", D(2026, 1, 1), null);
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 60500m, "EUR", D(2026, 1, 1), null);
    }

    [Fact]
    public void The_currency_is_the_base_salarys()
    {
        var fields = Compute(null, Base(100, "USD"), Allowance(1, "USD"), Pct(10));

        Assert.All(fields, f => Assert.Equal("USD", f.Currency));
    }

    // ---------- arithmetic ----------

    [Fact]
    public void The_bonus_midpoint_rounds_half_away_from_zero()
    {
        // 1000.0001 x 50 / 100 = 500.00005: banker's rounding would give 500.0000.
        var fields = Compute(null, Base(1000.0001m), Pct(50));

        Assert.Equal(500.0001m, Get(fields, "TARGET_BONUS").Amount);
        Assert.Equal(1000.0001m, Get(fields, "TOTAL_FIXED_PAY").Amount);
        Assert.Equal(1500.0002m, Get(fields, "TOTAL_TARGET_CASH").Amount);
    }

    [Fact]
    public void A_value_just_below_the_midpoint_rounds_down()
    {
        // 1000.0001 x 49.9999 / 100 = 499.9990499999: just below the midpoint 499.99905, so 499.9990.
        var fields = Compute(null, Base(1000.0001m), Pct(49.9999m));

        Assert.Equal(499.9990m, Get(fields, "TARGET_BONUS").Amount);
    }

    [Fact]
    public void Other_midpoints_round_up_where_half_to_even_would_round_down()
    {
        // 0.0001 x 50 / 100 = 0.00005: half to even gives 0.0000.
        Assert.Equal(0.0001m, Get(Compute(null, Base(0.0001m), Pct(50)), "TARGET_BONUS").Amount);

        // 2000.0005 x 10 / 100 = 200.00005: half to even gives 200.0000.
        Assert.Equal(200.0001m, Get(Compute(null, Base(2000.0005m), Pct(10)), "TARGET_BONUS").Amount);
    }

    [Theory]
    [InlineData("500.00005", "500.0001")]
    [InlineData("500.000049999", "500.0000")]
    [InlineData("0.00005", "0.0001")]
    [InlineData("0.000049", "0.0000")]
    [InlineData("12.34565", "12.3457")]
    public void Rounding_is_half_away_from_zero_to_four_places(string value, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            ComputedFields.RoundBonus(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Cash_adds_the_rounded_bonus_so_the_three_numbers_add_up()
    {
        // 3333.3333 x 33.3333 / 100 = 1111.1099888889; rounded 1111.1100.
        var fields = Compute(null, Base(3333.3333m), Allowance(0.0001m), Pct(33.3333m));

        Assert.Equal(1111.1100m, Get(fields, "TARGET_BONUS").Amount);
        Assert.Equal(3333.3334m, Get(fields, "TOTAL_FIXED_PAY").Amount);
        Assert.Equal(4444.4434m, Get(fields, "TOTAL_TARGET_CASH").Amount);
    }

    [Fact]
    public void Results_can_exceed_the_stored_amount_range()
    {
        var fields = Compute(null, Base(Components.MaxMoneyAmount), Allowance(Components.MaxMoneyAmount), Pct(500));

        Assert.Equal(4999999999999999.9995m, Get(fields, "TARGET_BONUS").Amount);
        Assert.Equal(1999999999999999.9998m, Get(fields, "TOTAL_FIXED_PAY").Amount);
        Assert.Equal(6999999999999999.9993m, Get(fields, "TOTAL_TARGET_CASH").Amount);
    }

    // ---------- reasons ----------

    [Fact]
    public void Without_a_bonus_percent_the_bonus_and_cash_are_missing_input_not_zero()
    {
        var fields = Compute(null, Base(50000));

        AssertNotComputable(Get(fields, "TARGET_BONUS"), "MISSING_INPUT");
        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 50000m, "EUR", D(2026, 1, 1), null);
        AssertNotComputable(Get(fields, "TOTAL_TARGET_CASH"), "MISSING_INPUT");
    }

    [Fact]
    public void Without_a_base_salary_all_three_are_missing_input()
    {
        Assert.All(Compute(null, Allowance(5000), Pct(10)), f => AssertNotComputable(f, "MISSING_INPUT"));
        Assert.All(Compute(null, Allowance(5000)), f => AssertNotComputable(f, "MISSING_INPUT"));
        Assert.All(Compute(null, Pct(10)), f => AssertNotComputable(f, "MISSING_INPUT"));
    }

    [Fact]
    public void No_inputs_at_all_give_three_missing_inputs()
    {
        Assert.All(Compute(D(2026, 7, 1)), f => AssertNotComputable(f, "MISSING_INPUT"));
    }

    [Fact]
    public void A_mismatched_allowance_leaves_the_bonus_computable()
    {
        var fields = Compute(null, Base(50000, "EUR"), Allowance(5000, "USD"), Pct(10));

        AssertComputable(Get(fields, "TARGET_BONUS"), 5000m, "EUR", D(2026, 1, 1), null);
        AssertNotComputable(Get(fields, "TOTAL_FIXED_PAY"), "CURRENCY_MISMATCH");
        AssertNotComputable(Get(fields, "TOTAL_TARGET_CASH"), "CURRENCY_MISMATCH");
    }

    [Fact]
    public void A_zero_allowance_in_another_currency_is_still_a_mismatch()
    {
        var fields = Compute(null, Base(50000, "EUR"), Allowance(0, "USD"), Pct(10));

        AssertNotComputable(Get(fields, "TOTAL_FIXED_PAY"), "CURRENCY_MISMATCH");
        AssertNotComputable(Get(fields, "TOTAL_TARGET_CASH"), "CURRENCY_MISMATCH");
    }

    [Fact]
    public void Missing_input_is_checked_before_currency_mismatch()
    {
        // No bonus percent and a mismatched allowance: the cash field has both problems, MISSING_INPUT comes first.
        var fields = Compute(null, Base(50000, "EUR"), Allowance(5000, "USD"));

        AssertNotComputable(Get(fields, "TARGET_BONUS"), "MISSING_INPUT");
        AssertNotComputable(Get(fields, "TOTAL_FIXED_PAY"), "CURRENCY_MISMATCH");
        AssertNotComputable(Get(fields, "TOTAL_TARGET_CASH"), "MISSING_INPUT");
    }

    [Fact]
    public void Currencies_are_compared_ordinally()
    {
        var fields = Compute(null, Base(50000, "EUR"), Allowance(5000, "EUr"), Pct(10));

        AssertNotComputable(Get(fields, "TOTAL_FIXED_PAY"), "CURRENCY_MISMATCH");
    }

    // ---------- periods ----------

    [Fact]
    public void The_period_with_an_allowance_in_effect_is_the_intersection_of_all_inputs()
    {
        var fields = Compute(
            D(2027, 1, 1),
            Base(60000, from: D(2026, 4, 1)),
            Allowance(3000, from: D(2026, 2, 1), to: D(2026, 9, 30)),
            Pct(10, from: D(2026, 1, 1), to: D(2026, 12, 31)));

        // The bonus uses no allowance; a later allowance does not end it.
        AssertComputable(Get(fields, "TARGET_BONUS"), 6000m, "EUR", D(2026, 4, 1), D(2026, 12, 31));
        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 63000m, "EUR", D(2026, 4, 1), D(2026, 9, 30));
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 69000m, "EUR", D(2026, 4, 1), D(2026, 9, 30));
    }

    [Fact]
    public void An_absent_allowance_with_a_later_one_ends_the_totals_the_day_before()
    {
        var fields = Compute(D(2026, 7, 1), Base(60000, from: D(2026, 4, 1)), Pct(10, from: D(2026, 1, 1)));

        AssertComputable(Get(fields, "TARGET_BONUS"), 6000m, "EUR", D(2026, 4, 1), null);
        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 60000m, "EUR", D(2026, 4, 1), D(2026, 6, 30));
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 66000m, "EUR", D(2026, 4, 1), D(2026, 6, 30));
    }

    [Fact]
    public void An_absent_allowance_sets_no_start()
    {
        // The allowance range is open to the past, so the start is the latest required input's.
        var fields = Compute(D(2026, 5, 16), Base(60000, from: D(2026, 1, 1)), Pct(10, from: D(2026, 3, 1)));

        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 60000m, "EUR", D(2026, 1, 1), D(2026, 5, 15));
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 66000m, "EUR", D(2026, 3, 1), D(2026, 5, 15));
    }

    [Fact]
    public void An_absent_allowance_with_none_later_leaves_the_totals_open_ended()
    {
        var fields = Compute(null, Base(60000, from: D(2026, 4, 1)), Pct(10, from: D(2026, 1, 1)));

        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 60000m, "EUR", D(2026, 4, 1), null);
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 66000m, "EUR", D(2026, 4, 1), null);
    }

    [Fact]
    public void A_required_input_that_ends_first_ends_the_period()
    {
        var fields = Compute(
            D(2026, 9, 1),
            Base(60000, from: D(2026, 1, 1), to: D(2026, 5, 31)),
            Pct(10, from: D(2026, 2, 1), to: D(2026, 6, 30)));

        AssertComputable(Get(fields, "TARGET_BONUS"), 6000m, "EUR", D(2026, 2, 1), D(2026, 5, 31));
        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 60000m, "EUR", D(2026, 1, 1), D(2026, 5, 31));
        AssertComputable(Get(fields, "TOTAL_TARGET_CASH"), 66000m, "EUR", D(2026, 2, 1), D(2026, 5, 31));
    }

    [Fact]
    public void An_allowance_in_effect_ignores_the_later_allowance_start()
    {
        // The in-effect allowance's own period already ends before the next one; the lookup value is not used.
        var fields = Compute(D(2026, 7, 1), Base(60000), Allowance(1000, to: D(2026, 6, 30)), Pct(10));

        AssertComputable(Get(fields, "TOTAL_FIXED_PAY"), 61000m, "EUR", D(2026, 1, 1), D(2026, 6, 30));
    }

    [Fact]
    public void A_not_computable_field_has_no_period()
    {
        var fields = Compute(D(2026, 7, 1), Base(50000, "EUR", D(2026, 1, 1), D(2026, 12, 31)), Allowance(1, "USD"));

        Assert.All(fields.Where(f => f.Reason is not null), f =>
        {
            Assert.Null(f.EffectiveFrom);
            Assert.Null(f.EffectiveTo);
        });
    }

    // ---------- arguments ----------

    [Fact]
    public void A_later_allowance_that_does_not_start_after_the_date_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Compute(AsOf, Base(1)));
    }

    [Fact]
    public void An_input_not_in_effect_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Compute(null, Base(1, from: D(2026, 6, 1))));
        Assert.Throws<ArgumentException>(() => Compute(null, Base(1, from: D(2026, 1, 1), to: D(2026, 5, 14))));
    }
}

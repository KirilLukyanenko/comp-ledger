using CompLedger.Core;

namespace CompLedger.UnitTests;

public sealed class ComponentsTests
{
    [Fact]
    public void Catalogue_holds_exactly_the_three_components()
    {
        Assert.Equal(["ALLOWANCE", "BASE_SALARY", "BONUS_TARGET_PCT"], Components.All.Select(c => c.Code).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("BASE_SALARY", true, "999999999999999.9999")]
    [InlineData("ALLOWANCE", true, "999999999999999.9999")]
    [InlineData("BONUS_TARGET_PCT", false, "500")]
    public void Component_rules_match_the_catalogue(string code, bool currencyRequired, string maxAmount)
    {
        var component = Components.Find(code);

        Assert.NotNull(component);
        Assert.Equal(currencyRequired, component.CurrencyRequired);
        Assert.Equal(decimal.Parse(maxAmount, System.Globalization.CultureInfo.InvariantCulture), component.MaxAmount);
    }

    [Theory]
    [InlineData("base_salary")]
    [InlineData("Base_Salary")]
    [InlineData("BASE_SALARY ")]
    [InlineData("BONUS")]
    [InlineData("")]
    public void Find_is_exact_and_case_sensitive(string code)
    {
        Assert.Null(Components.Find(code));
    }
}

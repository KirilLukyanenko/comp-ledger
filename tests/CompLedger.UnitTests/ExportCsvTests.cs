using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CompLedger.Core;
using CompLedger.Core.Export;

namespace CompLedger.UnitTests;

/// <summary>PRP-004 T1: the export file format (3.5).</summary>
public sealed class ExportCsvTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    // ---------- header ----------

    [Fact]
    public void The_header_line_is_exactly_the_six_column_names_followed_by_LF()
    {
        Assert.Equal("employee_id,component,effective_from,effective_to,amount,currency\n", ExportCsv.HeaderLine);
    }

    [Fact]
    public void The_header_encodes_to_utf8_without_a_bom_and_without_a_CR()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ExportCsv.HeaderLine);

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Single(bytes, b => b == (byte)'\n');
    }

    // ---------- rows ----------

    [Fact]
    public void A_row_has_the_six_fields_in_order_unquoted_and_ends_with_LF()
    {
        var line = ExportCsv.Line("E-1001", "BASE_SALARY", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), 52000.5000m, "EUR");

        Assert.Equal("E-1001,BASE_SALARY,2026-04-01,2026-06-30,52000.5,EUR\n", line);
    }

    [Fact]
    public void An_open_ended_period_has_an_empty_effective_to()
    {
        Assert.Equal("E-1001,ALLOWANCE,2026-01-01,,6000,EUR\n", ExportCsv.Line("E-1001", "ALLOWANCE", Jan1, null, 6000.0000m, "EUR"));
    }

    [Fact]
    public void A_value_without_a_currency_has_an_empty_currency_field()
    {
        Assert.Equal("E-1001,BONUS_TARGET_PCT,2026-01-01,,10,\n", ExportCsv.Line("E-1001", "BONUS_TARGET_PCT", Jan1, null, 10.0000m, null));
    }

    [Fact]
    public void Both_fields_can_be_empty_and_the_line_still_has_six_fields()
    {
        var line = ExportCsv.Line("a-1", "BONUS_TARGET_PCT", Jan1, null, 0m, null);

        Assert.Equal("a-1,BONUS_TARGET_PCT,2026-01-01,,0,\n", line);
        Assert.Equal(6, line.TrimEnd('\n').Split(',').Length);
    }

    [Theory]
    [InlineData(1900, 1, 1, "1900-01-01")]
    [InlineData(2999, 12, 31, "2999-12-31")]
    [InlineData(2026, 2, 3, "2026-02-03")]
    public void Dates_are_written_as_yyyy_MM_dd(int year, int month, int day, string expected)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(expected, ExportCsv.FormatDate(date));
        Assert.Equal($"E-1,BASE_SALARY,{expected},{expected},1,EUR\n", ExportCsv.Line("E-1", "BASE_SALARY", date, date, 1m, "EUR"));
    }

    [Fact]
    public void No_line_contains_a_CR_and_each_has_exactly_one_LF_at_the_end()
    {
        string[] lines =
        [
            ExportCsv.HeaderLine,
            ExportCsv.Line("E-1001", "BASE_SALARY", Jan1, new DateOnly(2026, 3, 31), 50000.0000m, "EUR"),
            ExportCsv.Line("E-1001", "BONUS_TARGET_PCT", Jan1, null, 10.0000m, null),
            ExportCsv.Line("Z.9_x-", "ALLOWANCE", Jan1, null, Components.MaxMoneyAmount, "USD"),
        ];

        foreach (var line in lines)
        {
            Assert.DoesNotContain('\r', line);
            Assert.EndsWith("\n", line, StringComparison.Ordinal);
            Assert.Single(line, c => c == '\n');
            Assert.DoesNotContain('"', line);
        }
    }

    // ---------- amounts ----------

    [Theory]
    [InlineData("50000.0000", "50000")]
    [InlineData("52000.5000", "52000.5")]
    [InlineData("0.0001", "0.0001")]
    [InlineData("0", "0")]
    [InlineData("0.0000", "0")]
    [InlineData("10.0000", "10")]
    [InlineData("6000.1200", "6000.12")]
    [InlineData("1234567.8900", "1234567.89")]
    [InlineData("100.0100", "100.01")]
    [InlineData("1000000.0000", "1000000")]
    [InlineData("500.0000", "500")]
    [InlineData("999999999999999.9999", "999999999999999.9999")]
    [InlineData("999999999999999.0000", "999999999999999")]
    public void Amounts_are_written_in_their_shortest_form(string stored, string expected)
    {
        // Parsed with the scale it is written with, as Npgsql returns numeric(19,4).
        var amount = decimal.Parse(stored, CultureInfo.InvariantCulture);

        Assert.Equal(expected, ExportCsv.FormatAmount(amount));
    }

    [Fact]
    public void The_largest_PRP_001_amount_is_written_in_full_without_an_exponent()
    {
        Assert.Equal("999999999999999.9999", ExportCsv.FormatAmount(Components.MaxMoneyAmount));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("hi-IN")]
    public void Amounts_and_dates_do_not_depend_on_the_current_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal("1234567.5", ExportCsv.FormatAmount(1234567.5000m));
            Assert.Equal("E-1,BASE_SALARY,2026-01-01,,52000.5,EUR\n", ExportCsv.Line("E-1", "BASE_SALARY", Jan1, null, 52000.5000m, "EUR"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0001")]
    [InlineData("0.1000")]
    [InlineData("1.0000")]
    [InlineData("52000.5000")]
    [InlineData("999999999999999.9999")]
    [InlineData("123456789012345.0001")]
    public void Every_amount_written_is_valid_in_a_PRP_001_upload(string stored)
    {
        var written = ExportCsv.FormatAmount(decimal.Parse(stored, CultureInfo.InvariantCulture));

        // The PRP-001 amount pattern, and the shortest form: no trailing zero after a dot, no lone dot.
        Assert.Matches(new Regex(@"\A[0-9]{1,15}(\.[0-9]{0,3}[1-9])?\z"), written);
        Assert.Equal(decimal.Parse(stored, CultureInfo.InvariantCulture), decimal.Parse(written, CultureInfo.InvariantCulture));
    }
}

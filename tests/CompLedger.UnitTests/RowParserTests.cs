using CompLedger.Core.Ingestion;

namespace CompLedger.UnitTests;

public sealed class RowParserTests
{
    private const string Header = "employee_id,component,effective_from,amount,currency";

    /// <summary>A record the CSV reader could not parse.</summary>
    private const string Unparseable = "\u0001unparseable";

    private sealed record Outcome(List<CompensationRow> Rows, RowParser Parser, int ErrorCount, IReadOnlyList<IngestionError> Errors)
    {
        public IEnumerable<(int? Row, string? Column, string Code)> Report => Errors.Select(e => (e.Row, e.Column, e.Code));
    }

    /// <summary>Feeds records to a parser. Each string is one record; fields are split on commas (no quoting here).</summary>
    private static Outcome Run(params string[] records) => Run(records, encodingFailsAtEnd: false);

    private static Outcome Run(string[] records, bool encodingFailsAtEnd)
    {
        var errors = new ErrorCollector();
        var parser = new RowParser(errors);
        var rows = new List<CompensationRow>();
        foreach (var record in records)
        {
            if (parser.HeaderFailed)
            {
                break;
            }

            if (parser.Accept(record.Split(','), record == Unparseable, out var row))
            {
                rows.Add(row);
            }
        }

        if (encodingFailsAtEnd)
        {
            parser.FailEncoding();
        }
        else
        {
            parser.Complete();
        }

        return new Outcome(rows, parser, errors.Count, errors.ToList());
    }

    /// <summary>Runs one data row under the standard header and returns its errors as (column, code).</summary>
    private static List<(string? Column, string Code)> RowErrors(string record)
    {
        var outcome = Run(Header, record);
        Assert.All(outcome.Errors, e => Assert.Equal(2, e.Row));
        return outcome.Errors.Select(e => (e.Column, e.Code)).ToList();
    }

    // ---------- valid rows ----------

    [Fact]
    public void Valid_rows_are_parsed_with_record_based_row_numbers()
    {
        var outcome = Run(
            Header,
            "E-1001,BASE_SALARY,2026-01-01,50000,EUR",
            "E-1001,BONUS_TARGET_PCT,2026-01-01,10,",
            "e.x_1,ALLOWANCE,2026-07-01,0.5,USD");

        Assert.Equal(0, outcome.ErrorCount);
        Assert.Equal(3, outcome.Parser.TotalRows);
        Assert.Equal(
            [
                new CompensationRow(2, "E-1001", "BASE_SALARY", new DateOnly(2026, 1, 1), 50000m, "EUR"),
                new CompensationRow(3, "E-1001", "BONUS_TARGET_PCT", new DateOnly(2026, 1, 1), 10m, null),
                new CompensationRow(4, "e.x_1", "ALLOWANCE", new DateOnly(2026, 7, 1), 0.5m, "USD"),
            ],
            outcome.Rows);
    }

    [Fact]
    public void Values_are_trimmed_of_space_tab_cr_and_lf()
    {
        var outcome = Run(Header, " E-1\t,\r\nBASE_SALARY \n,\t2026-01-01 , 50000.00\r, EUR ");

        Assert.Equal(0, outcome.ErrorCount);
        Assert.Equal(new CompensationRow(2, "E-1", "BASE_SALARY", new DateOnly(2026, 1, 1), 50000.00m, "EUR"), Assert.Single(outcome.Rows));
    }

    [Fact]
    public void Whitespace_only_currency_means_no_currency()
    {
        var outcome = Run(Header, "E-1,BONUS_TARGET_PCT,2026-01-01,10, \t ");

        Assert.Equal(0, outcome.ErrorCount);
        Assert.Null(Assert.Single(outcome.Rows).Currency);
    }

    [Fact]
    public void Other_whitespace_is_not_trimmed()
    {
        // A no-break space is not in the trimming set, so it stays and breaks the field rules.
        Assert.Equal(
            [("employee_id", ErrorCodes.InvalidEmployeeId), ("currency", ErrorCodes.InvalidCurrency)],
            RowErrors(" E-1,BASE_SALARY,2026-01-01,1,EUR "));
    }

    // ---------- header ----------

    [Fact]
    public void Header_columns_may_come_in_any_order_case_and_padding()
    {
        var outcome = Run(" Currency ,AMOUNT,\teffective_FROM,Component,EMPLOYEE_ID\r", "EUR,50000,2026-01-01,BASE_SALARY,E-1");

        Assert.Equal(0, outcome.ErrorCount);
        Assert.Equal(new CompensationRow(2, "E-1", "BASE_SALARY", new DateOnly(2026, 1, 1), 50000m, "EUR"), Assert.Single(outcome.Rows));
    }

    [Fact]
    public void Missing_columns_give_one_error_each_in_canonical_order_and_stop_processing()
    {
        var outcome = Run("amount,employee_id", "1,E-1");

        Assert.Equal(
            [(1, "component", ErrorCodes.MissingColumn), (1, "effective_from", ErrorCodes.MissingColumn), (1, "currency", ErrorCodes.MissingColumn)],
            outcome.Report);
        Assert.True(outcome.Parser.HeaderFailed);
        Assert.Equal(0, outcome.Parser.TotalRows);
        Assert.Empty(outcome.Rows);
    }

    [Fact]
    public void Unknown_columns_give_one_error_per_occurrence_with_the_name_as_written_trimmed()
    {
        var outcome = Run(Header + ", Grade ,grade,", "E-1,BASE_SALARY,2026-01-01,1,EUR,a,b,c");

        Assert.Equal(
            [(1, "Grade", ErrorCodes.UnknownColumn), (1, "grade", ErrorCodes.UnknownColumn), (1, "", ErrorCodes.UnknownColumn)],
            outcome.Report);
        Assert.Equal(0, outcome.Parser.TotalRows);
    }

    [Fact]
    public void U0000_in_an_unknown_column_name_becomes_ufffd_one_for_one()
    {
        var outcome = Run(Header + ",gr\0ade, \0\0 ,\0employee_id,amount\0");

        Assert.Equal(
            [
                (1, "gr�ade", ErrorCodes.UnknownColumn),
                (1, "��", ErrorCodes.UnknownColumn),
                (1, "�employee_id", ErrorCodes.UnknownColumn),
                (1, "amount�", ErrorCodes.UnknownColumn),
            ],
            outcome.Report);
        Assert.All(outcome.Errors, e => Assert.DoesNotContain('\0', e.Message));
    }

    [Fact]
    public void U0000_in_a_data_field_breaks_the_field_rule_and_never_reaches_a_message()
    {
        var outcome = Run(Header, "E\0-1,BASE\0_SALARY,2026-01-0\0,1\0,EU\0", "E-1,BONUS_TARGET_PCT,2026-01-01,1,\0");

        Assert.Equal(
            [
                (2, "employee_id", ErrorCodes.InvalidEmployeeId),
                (2, "component", ErrorCodes.UnknownComponent),
                (2, "effective_from", ErrorCodes.InvalidDate),
                (2, "amount", ErrorCodes.InvalidAmount),
                (2, "currency", ErrorCodes.InvalidCurrency),
                (3, "currency", ErrorCodes.CurrencyNotAllowed),
            ],
            outcome.Report);
        Assert.Empty(outcome.Rows);
        Assert.All(outcome.Errors, e => Assert.DoesNotContain('\0', e.Message));
    }

    [Fact]
    public void Duplicate_required_columns_give_one_error_per_extra_occurrence_with_the_canonical_name()
    {
        var outcome = Run(Header + ",AMOUNT, Amount ");

        Assert.Equal([(1, "amount", ErrorCodes.DuplicateColumn), (1, "amount", ErrorCodes.DuplicateColumn)], outcome.Report);
    }

    [Fact]
    public void Header_errors_come_by_file_position_then_missing_columns_and_no_empty_file_is_added()
    {
        var outcome = Run("bogus,amount,component,amount,other");

        Assert.Equal(
            [
                (1, "bogus", ErrorCodes.UnknownColumn),
                (1, "amount", ErrorCodes.DuplicateColumn),
                (1, "other", ErrorCodes.UnknownColumn),
                (1, "employee_id", ErrorCodes.MissingColumn),
                (1, "effective_from", ErrorCodes.MissingColumn),
                (1, "currency", ErrorCodes.MissingColumn),
            ],
            outcome.Report);
        Assert.Equal(6, outcome.ErrorCount);
    }

    [Fact]
    public void A_file_that_starts_with_data_gets_header_errors()
    {
        var outcome = Run("E-1,BASE_SALARY,2026-01-01,1,EUR", "E-2,BASE_SALARY,2026-01-01,1,EUR");

        Assert.Equal(10, outcome.ErrorCount);
        Assert.Equal(5, outcome.Errors.Count(e => e.Code == ErrorCodes.UnknownColumn));
        Assert.Equal(5, outcome.Errors.Count(e => e.Code == ErrorCodes.MissingColumn));
        Assert.Equal(0, outcome.Parser.TotalRows);
    }

    [Fact]
    public void A_blank_first_record_followed_by_anything_is_a_malformed_header_and_the_only_error()
    {
        var outcome = Run("", "  ", Header, "E-1,BASE_SALARY,2026-01-01,1,EUR");

        Assert.Equal([(1, null, ErrorCodes.MalformedCsv)], outcome.Report);
        Assert.True(outcome.Parser.HeaderFailed);
        Assert.Equal(0, outcome.Parser.TotalRows);
    }

    [Fact]
    public void An_unparseable_first_record_is_a_malformed_header_and_the_only_error()
    {
        var outcome = Run(Unparseable, "E-1,BASE_SALARY,2026-01-01,1,EUR");

        Assert.Equal([(1, null, ErrorCodes.MalformedCsv)], outcome.Report);
        Assert.Equal(0, outcome.Parser.TotalRows);
    }

    // ---------- empty files and blank lines ----------

    [Theory]
    [InlineData]
    [InlineData("")]
    [InlineData(" \t ", "", "\r")]
    [InlineData(Header)]
    [InlineData(Header, "", "   ")]
    public void A_file_without_data_rows_is_empty(params string[] records)
    {
        var outcome = Run(records);

        Assert.Equal([(null, null, ErrorCodes.EmptyFile)], outcome.Report);
        Assert.Equal(0, outcome.Parser.TotalRows);
    }

    [Fact]
    public void Trailing_blank_lines_are_ignored_however_many()
    {
        var outcome = Run([Header, "E-1,BASE_SALARY,2026-01-01,1,EUR", .. Enumerable.Repeat(" ", 500)]);

        Assert.Equal(0, outcome.ErrorCount);
        Assert.Equal(1, outcome.Parser.TotalRows);
    }

    [Fact]
    public void A_blank_line_before_the_last_data_record_is_a_malformed_row()
    {
        var outcome = Run(
            Header,
            "",
            "E-1,BASE_SALARY,2026-01-01,1,EUR",
            " \t",
            "",
            "E-2,BASE_SALARY,2026-01-01,x,EUR",
            "");

        Assert.Equal(
            [(2, null, ErrorCodes.MalformedCsv), (4, null, ErrorCodes.MalformedCsv), (5, null, ErrorCodes.MalformedCsv), (6, "amount", ErrorCodes.InvalidAmount)],
            outcome.Report);
        Assert.Equal(5, outcome.Parser.TotalRows);
        Assert.Equal(3, Assert.Single(outcome.Rows).RowNumber);
    }

    [Theory]
    [InlineData("E-1,BASE_SALARY,2026-01-01,1")]
    [InlineData("E-1,BASE_SALARY,2026-01-01,1,EUR,extra")]
    [InlineData("x")]
    [InlineData(",")]
    [InlineData(Unparseable)]
    public void A_row_that_is_unparseable_or_has_the_wrong_field_count_is_malformed_and_gets_no_other_error(string record)
    {
        var outcome = Run(Header, record, "E-2,BASE_SALARY,2026-01-01,1,EUR");

        Assert.Equal([(2, null, ErrorCodes.MalformedCsv)], outcome.Report);
        Assert.Equal(2, outcome.Parser.TotalRows);
        Assert.Equal(3, Assert.Single(outcome.Rows).RowNumber);
    }

    // ---------- employee_id ----------

    [Theory]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData("a.b_c-D")]
    [InlineData("E..--__")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")] // 64 chars
    public void Valid_employee_ids(string employeeId)
    {
        Assert.Empty(RowErrors($"{employeeId},BASE_SALARY,2026-01-01,1,EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("-E1")]
    [InlineData("_E1")]
    [InlineData("E 1")]
    [InlineData("E/1")]
    [InlineData("É1")]
    [InlineData("E١")] // Arabic-Indic digit
    [InlineData("a2345678901234567890123456789012345678901234567890123456789012345")] // 65 chars
    public void Invalid_employee_ids(string employeeId)
    {
        Assert.Equal([("employee_id", ErrorCodes.InvalidEmployeeId)], RowErrors($"{employeeId},BASE_SALARY,2026-01-01,1,EUR"));
    }

    [Fact]
    public void Employee_ids_are_case_sensitive()
    {
        var outcome = Run(Header, "e-1,BASE_SALARY,2026-01-01,1,EUR", "E-1,BASE_SALARY,2026-01-01,1,EUR");

        Assert.Equal(["e-1", "E-1"], outcome.Rows.Select(r => r.EmployeeId));
    }

    // ---------- component ----------

    [Theory]
    [InlineData("base_salary")]
    [InlineData("Allowance")]
    [InlineData("BONUS")]
    [InlineData("")]
    public void Unknown_components(string component)
    {
        Assert.Equal([("component", ErrorCodes.UnknownComponent)], RowErrors($"E-1,{component},2026-01-01,1,EUR"));
    }

    [Fact]
    public void Unknown_component_skips_the_checks_that_depend_on_it()
    {
        // No currency-presence check (empty and given are both fine) and no amount-range check.
        Assert.Equal([("component", ErrorCodes.UnknownComponent)], RowErrors("E-1,NOPE,2026-01-01,999999999999999.9999,"));
        Assert.Equal([("component", ErrorCodes.UnknownComponent)], RowErrors("E-1,NOPE,2026-01-01,501,EUR"));
    }

    [Fact]
    public void Unknown_component_still_runs_the_independent_checks_in_canonical_order()
    {
        Assert.Equal(
            [
                ("employee_id", ErrorCodes.InvalidEmployeeId),
                ("component", ErrorCodes.UnknownComponent),
                ("effective_from", ErrorCodes.InvalidDate),
                ("amount", ErrorCodes.InvalidAmount),
                ("currency", ErrorCodes.InvalidCurrency),
            ],
            RowErrors(".bad,NOPE,2026-02-30,1.23456,eur"));
    }

    [Fact]
    public void Field_errors_follow_canonical_order_even_when_the_file_orders_columns_differently()
    {
        var outcome = Run("currency,amount,effective_from,component,employee_id", "eur,-1,1899-12-31,BASE_SALARY,.");

        Assert.Equal(
            [
                (2, "employee_id", ErrorCodes.InvalidEmployeeId),
                (2, "effective_from", ErrorCodes.DateOutOfRange),
                (2, "amount", ErrorCodes.InvalidAmount),
                (2, "currency", ErrorCodes.InvalidCurrency),
            ],
            outcome.Report);
    }

    // ---------- effective_from ----------

    [Theory]
    [InlineData("1900-01-01")]
    [InlineData("2999-12-31")]
    [InlineData("2024-02-29")]
    public void Valid_dates(string date)
    {
        Assert.Empty(RowErrors($"E-1,BASE_SALARY,{date},1,EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000-01-01")]
    [InlineData("2026-02-30")]
    [InlineData("2025-02-29")]
    [InlineData("2026-13-01")]
    [InlineData("2026-00-10")]
    [InlineData("2026-1-01")]
    [InlineData("26-01-01")]
    [InlineData("2026/01/01")]
    [InlineData("01-01-2026")]
    [InlineData("2026-01-01T00:00:00")]
    [InlineData("12026-01-01")]
    [InlineData("202٦-01-01")] // Arabic-Indic digit
    [InlineData("２０２６-01-01")] // fullwidth digits
    public void Invalid_dates(string date)
    {
        Assert.Equal([("effective_from", ErrorCodes.InvalidDate)], RowErrors($"E-1,BASE_SALARY,{date},1,EUR"));
    }

    [Theory]
    [InlineData("1899-12-31")]
    [InlineData("3000-01-01")]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31")]
    public void Dates_out_of_range(string date)
    {
        Assert.Equal([("effective_from", ErrorCodes.DateOutOfRange)], RowErrors($"E-1,BASE_SALARY,{date},1,EUR"));
    }

    [Theory]
    [InlineData("2026-05-15", DateCheck.Valid)]
    [InlineData("1900-01-01", DateCheck.Valid)]
    [InlineData("2999-12-31", DateCheck.Valid)]
    [InlineData("1899-12-31", DateCheck.OutOfRange)]
    [InlineData("3000-01-01", DateCheck.OutOfRange)]
    [InlineData(" 2026-05-15", DateCheck.Invalid)]
    [InlineData("2026-05-15\n", DateCheck.Invalid)]
    [InlineData("", DateCheck.Invalid)]
    public void CheckDate_matches_the_whole_value_without_trimming(string value, DateCheck expected)
    {
        Assert.Equal(expected, RowParser.CheckDate(value, out _));
    }

    // ---------- amount ----------

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0.0001", "0.0001")]
    [InlineData("007", "7")]
    [InlineData("50000.00", "50000")]
    [InlineData("1.2345", "1.2345")] // 4 decimals
    [InlineData("999999999999999", "999999999999999")] // 15 integer digits
    [InlineData("999999999999999.9999", "999999999999999.9999")]
    public void Valid_amounts(string text, string expected)
    {
        var outcome = Run(Header, $"E-1,BASE_SALARY,2026-01-01,{text},EUR", $"E-1,ALLOWANCE,2026-01-01,{text},EUR");

        Assert.Equal(0, outcome.ErrorCount);
        Assert.All(outcome.Rows, row => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), row.Amount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.23456")] // 5 decimals: an error, never rounded
    [InlineData("1.00000")]
    [InlineData("1000000000000000")] // 16 integer digits
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1 000")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1.2.3")]
    [InlineData("1;5")]
    [InlineData("0x10")]
    [InlineData("abc")]
    [InlineData("١٢")] // Arabic-Indic digits
    [InlineData("１２")] // fullwidth digits
    public void Invalid_amounts(string text)
    {
        Assert.Equal([("amount", ErrorCodes.InvalidAmount)], RowErrors($"E-1,BASE_SALARY,2026-01-01,{text},EUR"));
        Assert.Equal([("amount", ErrorCodes.InvalidAmount)], RowErrors($"E-1,BONUS_TARGET_PCT,2026-01-01,{text},"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("500")]
    [InlineData("500.0000")]
    [InlineData("499.9999")]
    public void Bonus_percent_within_range(string text)
    {
        Assert.Empty(RowErrors($"E-1,BONUS_TARGET_PCT,2026-01-01,{text},"));
    }

    [Theory]
    [InlineData("500.0001")]
    [InlineData("501")]
    [InlineData("999999999999999.9999")]
    public void Bonus_percent_out_of_range(string text)
    {
        Assert.Equal([("amount", ErrorCodes.AmountOutOfRange)], RowErrors($"E-1,BONUS_TARGET_PCT,2026-01-01,{text},"));
    }

    // ---------- currency ----------

    [Theory]
    [InlineData("BASE_SALARY")]
    [InlineData("ALLOWANCE")]
    public void Money_components_require_a_currency(string component)
    {
        Assert.Equal([("currency", ErrorCodes.CurrencyRequired)], RowErrors($"E-1,{component},2026-01-01,1,"));
        Assert.Equal([("currency", ErrorCodes.CurrencyRequired)], RowErrors($"E-1,{component},2026-01-01,1,  "));
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("eur")] // presence is checked first, so the format error is never reported
    [InlineData("x")]
    public void Bonus_percent_takes_no_currency(string currency)
    {
        Assert.Equal([("currency", ErrorCodes.CurrencyNotAllowed)], RowErrors($"E-1,BONUS_TARGET_PCT,2026-01-01,10,{currency}"));
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("Eur")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData("E R")]
    [InlineData("ÉUR")]
    public void Invalid_currencies(string currency)
    {
        Assert.Equal([("currency", ErrorCodes.InvalidCurrency)], RowErrors($"E-1,BASE_SALARY,2026-01-01,1,{currency}"));
    }

    [Fact]
    public void Currency_is_not_checked_against_iso_4217()
    {
        Assert.Empty(RowErrors("E-1,BASE_SALARY,2026-01-01,1,ZZZ"));
    }

    // ---------- several errors, cap, encoding ----------

    [Fact]
    public void A_row_has_at_most_one_error_per_field_but_may_have_errors_in_several_fields()
    {
        Assert.Equal(
            [("effective_from", ErrorCodes.DateOutOfRange), ("amount", ErrorCodes.AmountOutOfRange), ("currency", ErrorCodes.CurrencyNotAllowed)],
            RowErrors("E-1,BONUS_TARGET_PCT,3000-01-01,600,eur"));
    }

    [Fact]
    public void Only_the_first_100_errors_are_kept_and_all_are_counted()
    {
        var records = new List<string> { Header };
        records.AddRange(Enumerable.Range(0, 80).Select(_ => ".,BASE_SALARY,2026-01-01,x,EUR")); // two errors per row

        var outcome = Run([.. records]);

        Assert.Equal(160, outcome.ErrorCount);
        Assert.Equal(100, outcome.Errors.Count);
        Assert.Equal((2, "employee_id", ErrorCodes.InvalidEmployeeId), outcome.Report.First());
        Assert.Equal((51, "amount", ErrorCodes.InvalidAmount), outcome.Report.Last());
        Assert.Equal(80, outcome.Parser.TotalRows);
    }

    [Fact]
    public void Invalid_encoding_is_a_file_level_error_that_keeps_earlier_errors_and_the_row_count()
    {
        var outcome = Run([Header, "E-1,BASE_SALARY,2026-01-01,1,EUR", "E-2,BASE_SALARY,2026-01-01,x,EUR", ""], encodingFailsAtEnd: true);

        Assert.Equal([(null, null, ErrorCodes.InvalidEncoding), (3, "amount", ErrorCodes.InvalidAmount)], outcome.Report);
        Assert.Equal(2, outcome.Parser.TotalRows);

        outcome.Parser.Complete();
        Assert.Equal(2, outcome.ErrorCount);
    }

    [Fact]
    public void Invalid_encoding_before_any_row_is_the_only_error()
    {
        var outcome = Run([Header], encodingFailsAtEnd: true);

        Assert.Equal([(null, null, ErrorCodes.InvalidEncoding)], outcome.Report);
        Assert.Equal(0, outcome.Parser.TotalRows);
    }
}

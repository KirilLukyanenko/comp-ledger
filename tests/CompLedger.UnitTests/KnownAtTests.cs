using CompLedger.Core;

namespace CompLedger.UnitTests;

/// <summary>PRP-002 3.2 (the <c>knownAt</c> rules that need no clock) and the response format of 3.5.</summary>
public sealed class KnownAtTests
{
    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second, int microsecond = 0) =>
        new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(microsecond * 10L);

    // ---------- accepted ----------

    [Fact]
    public void A_value_without_a_fraction_is_parsed_as_utc()
    {
        Assert.True(KnownAt.TryParse("2026-11-13T14:22:31Z", out var instant));

        Assert.Equal(Utc(2026, 11, 13, 14, 22, 31), instant);
        Assert.Equal(DateTimeKind.Utc, instant.Kind);
    }

    [Theory]
    [InlineData("2026-11-13T14:22:31.1Z", 100000)]
    [InlineData("2026-11-13T14:22:31.12Z", 120000)]
    [InlineData("2026-11-13T14:22:31.123Z", 123000)]
    [InlineData("2026-11-13T14:22:31.1234Z", 123400)]
    [InlineData("2026-11-13T14:22:31.12345Z", 123450)]
    [InlineData("2026-11-13T14:22:31.123456Z", 123456)]
    [InlineData("2026-11-13T14:22:31.000001Z", 1)]
    [InlineData("2026-11-13T14:22:31.000000Z", 0)]
    [InlineData("2026-11-13T14:22:31.0Z", 0)]
    [InlineData("2026-11-13T14:22:31.999999Z", 999999)]
    public void One_to_six_fractional_digits_are_read_as_microseconds(string value, int microseconds)
    {
        Assert.True(KnownAt.TryParse(value, out var instant));

        Assert.Equal(Utc(2026, 11, 13, 14, 22, 31, microseconds), instant);
        Assert.Equal(DateTimeKind.Utc, instant.Kind);
    }

    [Theory]
    [InlineData("2024-02-29T00:00:00Z")] // leap day
    [InlineData("2026-12-31T23:59:59.999999Z")]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.999999Z")] // the upper bound is the request time, checked against the database clock
    public void Real_instants_are_accepted(string value)
    {
        Assert.True(KnownAt.TryParse(value, out _));
    }

    // ---------- format ----------

    [Theory]
    [InlineData("2026-11-13T10:00:00+01:00")]
    [InlineData("2026-11-13T10:00:00-00:00")]
    [InlineData("2026-11-13T10:00:00+00:00")]
    [InlineData("2026-11-13T10:00:00 01:00")] // a "+" that was not percent-encoded
    [InlineData("2026-11-13T10:00:00Z+01:00")]
    [InlineData("2026-11-13T10:00:00")] // local time: no Z
    [InlineData("2026-11-13T10:00:00UTC")]
    [InlineData("2026-11-13T10:00:00GMT")]
    public void Offsets_and_local_times_are_refused(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    [Theory]
    [InlineData("2026-11-13t10:00:00Z")]
    [InlineData("2026-11-13T10:00:00z")]
    [InlineData("2026-11-13t10:00:00z")]
    [InlineData("2026-11-13 10:00:00Z")]
    public void The_t_and_the_z_must_be_uppercase(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    [Theory]
    [InlineData("2026-11-13T10:00:00.1234567Z")] // seven digits are refused, not rounded
    [InlineData("2026-11-13T10:00:00.1234560Z")]
    [InlineData("2026-11-13T10:00:00.0000000Z")]
    [InlineData("2026-11-13T10:00:00.Z")]
    [InlineData("2026-11-13T10:00:00,5Z")]
    public void More_than_six_fractional_digits_or_an_empty_fraction_are_refused(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Z")]
    [InlineData("2026-11-13")]
    [InlineData("2026-11-13T10:00Z")]
    [InlineData("2026-11-13T1:00:00Z")]
    [InlineData("26-11-13T10:00:00Z")]
    [InlineData("20261113T100000Z")]
    [InlineData("2026-11-13T10:00:00ZZ")]
    [InlineData("+2026-11-13T10:00:00Z")]
    [InlineData("2026-11-13T10:00:00Z\0")]
    [InlineData("\02026-11-13T10:00:00Z")]
    [InlineData("2026-11-13T10:00:\0\0Z")]
    [InlineData("now")]
    [InlineData("1731500000")]
    public void Other_spellings_are_refused(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.False(KnownAt.TryParse(null, out _));
    }

    [Theory]
    [InlineData(" 2026-11-13T10:00:00Z")]
    [InlineData("2026-11-13T10:00:00Z ")]
    [InlineData("\t2026-11-13T10:00:00Z")]
    [InlineData("2026-11-13T10:00:00Z\n")] // "$" would match before this line break; \z does not
    [InlineData("2026-11-13T10:00:00Z\r\n")]
    public void The_value_is_not_trimmed(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    [Theory]
    [InlineData("٢٠٢٦-11-13T10:00:00Z")] // Arabic-Indic digits
    [InlineData("2026-11-13T10:00:0０Z")] // a fullwidth zero
    [InlineData("2026-11-13T10:00:00.１Z")]
    public void Only_ascii_digits_are_digits(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    // ---------- real instants ----------

    [Theory]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-02-29T00:00:00Z")] // 2026 is not a leap year
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-00-10T00:00:00Z")]
    [InlineData("2026-04-31T00:00:00Z")]
    [InlineData("2026-11-00T00:00:00Z")]
    [InlineData("2026-11-13T24:00:00Z")]
    [InlineData("2026-11-13T25:00:00Z")]
    [InlineData("2026-11-13T10:60:00Z")]
    [InlineData("2026-11-13T10:00:60Z")] // no leap seconds
    [InlineData("2016-12-31T23:59:60Z")] // not even a real one
    [InlineData("0000-01-01T00:00:00Z")]
    public void Values_that_are_not_a_real_instant_are_refused(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    // ---------- range ----------

    [Fact]
    public void The_lower_bound_is_inclusive()
    {
        Assert.True(KnownAt.TryParse("1900-01-01T00:00:00Z", out var first));
        Assert.Equal(KnownAt.Min, first);
        Assert.True(KnownAt.TryParse("1900-01-01T00:00:00.000000Z", out _));
        Assert.True(KnownAt.TryParse("1900-01-01T00:00:00.000001Z", out _));
    }

    [Theory]
    [InlineData("1899-12-31T23:59:59.999999Z")]
    [InlineData("1899-12-31T23:59:59Z")]
    [InlineData("1899-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    public void Instants_before_1900_are_refused(string value)
    {
        Assert.False(KnownAt.TryParse(value, out _));
    }

    // ---------- response format and round trip ----------

    [Theory]
    [InlineData("2026-11-13T14:22:09Z", "2026-11-13T14:22:09.000000Z")]
    [InlineData("2026-11-13T14:22:09.5Z", "2026-11-13T14:22:09.500000Z")]
    [InlineData("2026-11-13T14:22:09.123Z", "2026-11-13T14:22:09.123000Z")]
    [InlineData("1900-01-01T00:00:00Z", "1900-01-01T00:00:00.000000Z")]
    public void Format_always_writes_six_fractional_digits(string value, string expected)
    {
        Assert.True(KnownAt.TryParse(value, out var instant));

        Assert.Equal(expected, KnownAt.Format(instant));
    }

    [Theory]
    [InlineData("2026-11-13T14:22:31.123456Z")]
    [InlineData("2026-11-13T14:22:31.000001Z")]
    [InlineData("2026-11-13T14:22:31.999999Z")]
    [InlineData("2026-11-13T14:22:31.100000Z")]
    [InlineData("2026-11-13T00:00:00.000000Z")]
    [InlineData("1900-01-01T00:00:00.000000Z")]
    [InlineData("2999-12-31T23:59:59.999999Z")]
    public void Six_digit_values_round_trip_exactly(string value)
    {
        Assert.True(KnownAt.TryParse(value, out var instant));

        Assert.Equal(value, KnownAt.Format(instant));
    }

    [Fact]
    public void Neighbouring_microseconds_stay_distinct_and_ordered()
    {
        Assert.True(KnownAt.TryParse("2026-11-13T14:22:31.123456Z", out var at));
        Assert.True(KnownAt.TryParse("2026-11-13T14:22:31.123455Z", out var before));

        Assert.Equal(TimeSpan.FromTicks(10), at - before);
        Assert.Equal("2026-11-13T14:22:31.123455Z", KnownAt.Format(at.AddTicks(-10)));
    }

    [Fact]
    public void Format_writes_an_unspecified_kind_as_utc_and_refuses_local_time()
    {
        // What a driver returns for timestamptz may carry no kind; it is UTC all the same.
        Assert.Equal("2026-11-13T14:22:31.000010Z", KnownAt.Format(new DateTime(2026, 11, 13, 14, 22, 31, DateTimeKind.Unspecified).AddTicks(100)));
        Assert.Throws<ArgumentException>(() => KnownAt.Format(new DateTime(2026, 11, 13, 14, 22, 31, DateTimeKind.Local)));
    }
}

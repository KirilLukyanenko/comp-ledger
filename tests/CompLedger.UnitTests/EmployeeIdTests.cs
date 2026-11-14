using CompLedger.Core.Ingestion;

namespace CompLedger.UnitTests;

/// <summary>PRP-005 3.2: which strings are employee ids (PRP-001 3.3), on the value exactly as given.</summary>
public sealed class EmployeeIdTests
{
    [Theory]
    [InlineData("E-1001")]
    [InlineData("e")]
    [InlineData("0")]
    [InlineData("a.b_c-d")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Valid_ids_are_recognised(string value) => Assert.True(RowParser.IsEmployeeId(value));

    [Theory]
    [InlineData("")]
    [InlineData(" E-1001")]
    [InlineData("E-1001 ")]
    [InlineData("E-1001\n")]
    [InlineData("E 1001")]
    [InlineData("-E")]
    [InlineData(".E")]
    [InlineData("_E")]
    [InlineData("a/b")]
    [InlineData("E\u00001")]
    [InlineData("\u0000")]
    [InlineData("É-1")]
    [InlineData("E%2D1")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Anything_else_is_not_an_id_and_is_not_trimmed_first(string value) => Assert.False(RowParser.IsEmployeeId(value));
}

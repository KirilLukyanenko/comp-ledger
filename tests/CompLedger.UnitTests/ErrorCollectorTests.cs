using CompLedger.Core.Ingestion;

namespace CompLedger.UnitTests;

public sealed class ErrorCollectorTests
{
    private static IngestionError Error(int? row, string? column = null, string code = "X") => new(row, column, code, "m");

    [Fact]
    public void Starts_empty()
    {
        var collector = new ErrorCollector();

        Assert.False(collector.HasErrors);
        Assert.Equal(0, collector.Count);
        Assert.Empty(collector.ToList());
    }

    [Fact]
    public void Keeps_the_first_100_and_counts_all()
    {
        var collector = new ErrorCollector();
        for (var row = 2; row < 252; row++)
        {
            collector.Add(Error(row), ErrorCollector.RowLevelOrder);
        }

        Assert.Equal(250, collector.Count);
        Assert.Equal(Enumerable.Range(2, 100).Cast<int?>(), collector.ToList().Select(e => e.Row));
    }

    [Fact]
    public void Exactly_100_errors_are_all_kept()
    {
        var collector = new ErrorCollector();
        for (var row = 2; row < 102; row++)
        {
            collector.Add(Error(row), ErrorCollector.RowLevelOrder);
        }

        Assert.Equal(100, collector.Count);
        Assert.Equal(100, collector.ToList().Count);
    }

    [Fact]
    public void Orders_by_row_with_file_level_errors_first()
    {
        var collector = new ErrorCollector();
        collector.Add(Error(7), 0);
        collector.Add(Error(3), 0);
        collector.Add(Error(null, code: "FILE"), ErrorCollector.RowLevelOrder);
        collector.Add(Error(5), 0);

        Assert.Equal([null, 3, 5, 7], collector.ToList().Select(e => e.Row));
    }

    [Fact]
    public void Orders_within_a_row_by_order_value_then_arrival()
    {
        var collector = new ErrorCollector();
        collector.Add(Error(2, "currency"), 4);
        collector.Add(Error(2, "employee_id", "first"), 0);
        collector.Add(Error(2, null), ErrorCollector.RowLevelOrder);
        collector.Add(Error(2, "employee_id", "second"), 0);

        Assert.Equal(
            [(null, "X"), ("employee_id", "first"), ("employee_id", "second"), ("currency", "X")],
            collector.ToList().Select(e => (e.Column, e.Code)));
    }

    [Fact]
    public void A_late_error_that_sorts_earlier_displaces_the_last_kept_one()
    {
        var collector = new ErrorCollector();
        for (var row = 10; row < 130; row++)
        {
            collector.Add(Error(row), 0);
        }

        collector.Add(Error(5, code: "EARLY"), ErrorCollector.RowLevelOrder);
        collector.Add(Error(null, code: "FILE"), ErrorCollector.RowLevelOrder);
        collector.Add(Error(500, code: "LATE"), ErrorCollector.RowLevelOrder);

        var kept = collector.ToList();
        Assert.Equal(123, collector.Count);
        Assert.Equal(100, kept.Count);
        Assert.Equal("FILE", kept[0].Code);
        Assert.Equal("EARLY", kept[1].Code);
        Assert.Equal(10, kept[2].Row);
        Assert.Equal(107, kept[99].Row);
    }

    [Fact]
    public void AddUnlisted_only_raises_the_count()
    {
        var collector = new ErrorCollector();
        collector.Add(Error(2), 0);
        collector.AddUnlisted(41);

        Assert.Equal(42, collector.Count);
        Assert.Single(collector.ToList());
    }
}

using System.Globalization;
using System.Net;
using CompLedger.Infrastructure.Export;

namespace CompLedger.IntegrationTests;

/// <summary>
/// The export tests find (and end) export statements by their text in <c>pg_stat_activity</c>, so the classes that
/// run export statements never run at the same time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ExportCollection
{
    public const string Name = "Export";
}

/// <summary>
/// PRP-004 T2: <see cref="ExportQuery"/> against the database: the same rows as the as-of read (3.4 items 1 and 2),
/// ordinal order (A2), the known-at boundary (6.5 #9), and no lock held while the rows are read (3.4 item 5).
/// </summary>
[Collection(ExportCollection.Name)]
public sealed class ExportQueryTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Header = ApiFactory.Header;

    private readonly HttpClient _client = api.CreateClient();

    private readonly ExportQuery _export = new(api.Database.AppDataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<ExportRow>> ExportAsync(Guid tenant, string asOf, DateTime knownAt)
    {
        var rows = new List<ExportRow>();
        await foreach (var row in _export.ReadAsync(tenant, DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture), knownAt, Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static (string Component, decimal Amount, string? Currency, string From, string? To) AsPeriod(ExportRow row) =>
        (row.Component, row.Amount, row.Currency, Date(row.EffectiveFrom), row.EffectiveTo is { } to ? Date(to) : null);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_rows_equal_the_as_of_read_of_every_employee_with_corrections_and_back_dated_values()
    {
        var tenant = await api.NewTenantAsync(_client);
        string[] employees = ["E-1", "E-2", "E-3", "e-4", "E-5"];

        var appliedAt = new List<string>
        {
            await _client.AppliedAtAsync(tenant, Header
                + "E-1,BASE_SALARY,2026-01-01,50000,EUR\n"
                + "E-1,BASE_SALARY,2026-07-01,55000,EUR\n"
                + "E-1,BONUS_TARGET_PCT,2026-01-01,10,\n"
                + "E-1,ALLOWANCE,2026-03-01,1200.5,EUR\n"
                + "E-2,BASE_SALARY,2025-01-01,40000,USD\n"
                + "E-3,BASE_SALARY,2026-06-01,60000,EUR\n"),

            // A correction of E-1's first salary and E-2's currency, and a back-dated value that splits E-1's period.
            await _client.AppliedAtAsync(tenant, Header
                + "E-1,BASE_SALARY,2026-01-01,51000,EUR\n"
                + "E-1,BASE_SALARY,2026-04-01,52000.5,EUR\n"
                + "E-2,BASE_SALARY,2025-01-01,40000,GBP\n"
                + "e-4,ALLOWANCE,2026-02-01,0.0001,EUR\n"),

            // Another back-dated value before every other one, and a correction of the back-dated one.
            await _client.AppliedAtAsync(tenant, Header
                + "E-3,BASE_SALARY,2025-06-01,58000,EUR\n"
                + "E-1,BASE_SALARY,2026-04-01,53000,EUR\n"
                + "E-5,BONUS_TARGET_PCT,2026-05-01,0,\n"),
        };

        var now = (await _export.FixInstantAsync(tenant, Ct)).ToString(Instants.Format, CultureInfo.InvariantCulture);
        var instants = appliedAt.SelectMany(t => new[] { Instants.Shift(t, -1), t, Instants.Shift(t, 1) }).Append(now).ToList();
        string[] dates = ["2025-01-01", "2025-12-31", "2026-02-15", "2026-03-31", "2026-04-01", "2026-05-15", "2026-06-30", "2026-07-01", "2027-01-01"];

        foreach (var knownAt in instants)
        {
            foreach (var asOf in dates)
            {
                var exported = await ExportAsync(tenant, asOf, Instants.Parse(knownAt));
                foreach (var employee in employees)
                {
                    using var response = await _client.GetAsync($"/api/v1/employees/{employee}/compensation?asOf={asOf}&knownAt={knownAt}", tenant);
                    List<(string Component, decimal Amount, string? Currency, string From, string? To)> expected = response.StatusCode == HttpStatusCode.NotFound
                        ? []
                        : (await response.JsonAsync()).GetProperty("components").Periods();
                    var actual = exported.Where(r => r.EmployeeId == employee).Select(AsPeriod).ToList();
                    Assert.True(expected.SequenceEqual(actual), $"{employee} as of {asOf} known at {knownAt}");
                }

                Assert.All(exported, r => Assert.Contains(r.EmployeeId, employees));
            }
        }

        // A spot check of the periods: the back-dated value split E-1's salary, and the correction replaced it.
        var rows = await ExportAsync(tenant, "2026-05-15", Instants.Parse(now));
        Assert.Contains(new ExportRow("E-1", "BASE_SALARY", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), 53000m, "EUR"), rows);
    }

    [Fact]
    public async Task Rows_are_in_ordinal_order_of_employee_id_then_component()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Header
            + "a-1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E-9,BASE_SALARY,2026-01-01,1,EUR\n"
            + "Z-1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E-10,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E.1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "A-1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E_1,BASE_SALARY,2026-01-01,1,EUR\n"
            + "E-10,BONUS_TARGET_PCT,2026-01-01,5,\n"
            + "E-10,ALLOWANCE,2026-01-01,5,EUR\n");

        var rows = await ExportAsync(tenant, "2026-02-01", await _export.FixInstantAsync(tenant, Ct));

        Assert.Equal(
            [
                ("A-1", "BASE_SALARY"), ("E-10", "ALLOWANCE"), ("E-10", "BASE_SALARY"), ("E-10", "BONUS_TARGET_PCT"),
                ("E-9", "BASE_SALARY"), ("E.1", "BASE_SALARY"), ("E_1", "BASE_SALARY"), ("Z-1", "BASE_SALARY"), ("a-1", "BASE_SALARY"),
            ],
            rows.Select(r => (r.EmployeeId, r.Component)).ToList());
        Assert.Equal(
            rows.Select(r => (r.EmployeeId, r.Component)).Order(Comparer<(string, string)>.Create((x, y) =>
                string.CompareOrdinal(x.Item1, y.Item1) is var c and not 0 ? c : string.CompareOrdinal(x.Item2, y.Item2))),
            rows.Select(r => (r.EmployeeId, r.Component)));
    }

    [Fact]
    public async Task At_a_batchs_apply_time_its_values_are_in_and_the_ones_it_superseded_are_not()
    {
        var tenant = await api.NewTenantAsync(_client);
        var t1 = await _client.AppliedAtAsync(tenant, Header + "E-1,BASE_SALARY,2026-01-01,50000,EUR\n");
        var t2 = await _client.AppliedAtAsync(tenant, Header + "E-1,BASE_SALARY,2026-01-01,51000,EUR\nE-2,BASE_SALARY,2026-01-01,1,EUR\n");

        var before = await ExportAsync(tenant, "2026-02-01", Instants.Parse(Instants.Shift(t2, -1)));
        var at = await ExportAsync(tenant, "2026-02-01", Instants.Parse(t2));
        var beforeFirst = await ExportAsync(tenant, "2026-02-01", Instants.Parse(Instants.Shift(t1, -1)));

        Assert.Equal([new ExportRow("E-1", "BASE_SALARY", new DateOnly(2026, 1, 1), null, 50000m, "EUR")], before);
        Assert.Equal(
            [
                new ExportRow("E-1", "BASE_SALARY", new DateOnly(2026, 1, 1), null, 51000m, "EUR"),
                new ExportRow("E-2", "BASE_SALARY", new DateOnly(2026, 1, 1), null, 1m, "EUR"),
            ],
            at);
        Assert.Empty(beforeFirst);
    }

    [Fact]
    public async Task The_lock_is_not_held_while_rows_are_read_and_a_batch_committed_meanwhile_is_not_in_them()
    {
        var tenant = await api.NewTenantAsync(_client);
        await _client.AppliedAtAsync(tenant, Header + "E-1,BASE_SALARY,2026-01-01,50000,EUR\nE-2,BASE_SALARY,2026-01-01,1,EUR\n");

        var instant = await _export.FixInstantAsync(tenant, Ct);
        await using var rows = _export.ReadAsync(tenant, new DateOnly(2026, 2, 1), instant, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await rows.MoveNextAsync());
        Assert.Equal("E-1", rows.Current.EmployeeId);

        // The reader is open with its statement and transaction: an upload of the same tenant still takes the lock,
        // applies and commits.
        using (var upload = await _client.UploadAsync(tenant, Header + "E-1,BASE_SALARY,2026-01-01,60000,EUR\nE-3,BASE_SALARY,2026-01-01,1,EUR\n")
                   .WaitAsync(TimeSpan.FromSeconds(10), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        var rest = new List<ExportRow>();
        while (await rows.MoveNextAsync())
        {
            rest.Add(rows.Current);
        }

        Assert.Equal([new ExportRow("E-2", "BASE_SALARY", new DateOnly(2026, 1, 1), null, 1m, "EUR")], rest);
    }
}

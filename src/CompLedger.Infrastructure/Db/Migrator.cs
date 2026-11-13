using DbUp;

namespace CompLedger.Infrastructure.Db;

/// <summary>Applies the embedded scripts from <c>db/migrations</c> in name order. Runs as the schema owner.</summary>
public static class Migrator
{
    /// <summary>
    /// Brings the schema up to date. DbUp has no asynchronous API; this runs once at startup, before the app listens,
    /// and never on a request path.
    /// </summary>
    public static void Migrate(string connectionString)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly, name => name.EndsWith(".sql", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed in script '{result.ErrorScript?.Name}': {result.Error.Message}", result.Error);
        }
    }
}

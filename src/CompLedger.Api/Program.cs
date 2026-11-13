using CompLedger.Api.Endpoints;
using CompLedger.Api.Tenancy;
using CompLedger.Infrastructure.Db;
using CompLedger.Infrastructure.Ingestion;
using CompLedger.Infrastructure.Queries;
using CompLedger.Infrastructure.Snapshots;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Every error response is RFC 7807 ProblemDetails, including the 404, 405 and 413 that have no body of their own.
builder.Services.AddProblemDetails();

// The app login: a member of comp_app, never the table owner or a superuser. Created on first use, so /health
// works without a database.
builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
    services.GetRequiredService<IConfiguration>().GetConnectionString("App")
    ?? throw new InvalidOperationException("ConnectionStrings:App is not configured.")));
builder.Services.AddSingleton<TenantStore>();
builder.Services.AddSingleton<PostgresIngestor>();
builder.Services.AddSingleton<CompensationQueries>();
builder.Services.AddSingleton<SnapshotStore>();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    // Runs to completion before the app listens, so a 200 from /health means the schema is in place.
    Migrator.Migrate(
        app.Configuration.GetConnectionString("Migrations")
        ?? throw new InvalidOperationException("ConnectionStrings:Migrations is not configured."));
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapHealthEndpoints();
if (app.Configuration.GetValue<bool>("TenantAdminApi:Enabled"))
{
    app.MapTenantEndpoints();
}

app.MapIngestionEndpoints(app.Configuration.GetValue<long?>("Ingestion:MaxBodyBytes") ?? IngestionEndpoints.DefaultMaxBodyBytes);
app.MapCompensationEndpoints();
app.MapSnapshotEndpoints();

await app.RunAsync();

public partial class Program;

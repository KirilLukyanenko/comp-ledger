using CompLedger.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();

public partial class Program;

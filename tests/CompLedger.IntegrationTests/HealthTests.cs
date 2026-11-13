using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CompLedger.IntegrationTests;

public sealed class HealthTests
{
    [Fact]
    public async Task Health_returns_ok_without_a_database()
    {
        // No connection string is configured here, so a 200 also proves that /health does not touch the database.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

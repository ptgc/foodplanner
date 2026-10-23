using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Foodplanner.Api.Tests;

public class ReadinessEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // Port 1 on loopback refuses immediately; Connect Timeout=1 bounds anything slower.
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=foodplanner;User Id=sa;TrustServerCertificate=True;Connect Timeout=1";

    private HttpClient CreateClient(string? connectionString) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }))).CreateClient();

    private static async Task AssertProblem(HttpResponseMessage response, string title)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.DoesNotContain("127.0.0.1", body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_returns_503_problem_when_server_is_unreachable_within_10_seconds()
    {
        using var client = CreateClient(UnreachableConnectionString);
        var clock = Stopwatch.StartNew();

        using var response = await client.GetAsync("/api/health/ready", TestContext.Current.CancellationToken);

        clock.Stop();
        await AssertProblem(response, "Database unavailable");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Ready_returns_503_problem_when_connection_string_is_missing(string? value)
    {
        using var client = CreateClient(value);

        using var response = await client.GetAsync("/api/health/ready", TestContext.Current.CancellationToken);

        await AssertProblem(response, "Database not configured");
    }

    [Fact]
    public async Task Health_stays_200_without_database()
    {
        using var client = CreateClient(UnreachableConnectionString);

        using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

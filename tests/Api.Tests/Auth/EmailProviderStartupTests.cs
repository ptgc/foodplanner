using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Foodplanner.Api.Tests.Auth;

public sealed class EmailProviderStartupTests
{
    private static void Start(string environment, string? provider)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Provider"] = provider }));
        });
        using var client = factory.CreateClient();
    }

    private static string Messages(Exception ex) =>
        string.Join(" | ", Chain(ex).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Missing_email_provider_fails_startup(string environment)
    {
        var ex = Assert.ThrowsAny<Exception>(() => Start(environment, provider: null));

        Assert.Contains("Email:Provider", Messages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_email_provider_fails_startup()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Start("Production", provider: "Carrier-pigeon"));

        Assert.Contains("Email:Provider", Messages(ex), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", "Recording")]
    public void Recording_provider_starts(string environment, string? provider)
    {
        Start(environment, provider);
    }
}

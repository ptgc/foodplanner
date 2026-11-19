using Foodplanner.Api.Tests.Import;
using Foodplanner.Infrastructure.Import.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests;

/// <summary>
/// The only way an Api test creates a host from <c>Program</c> (T-077 hardening; <c>TestHostRulesTests</c> in Architecture.Tests
/// fails on a bare <c>new WebApplicationFactory&lt;Program&gt;()</c>). The AI fallback is forced off whatever the developer's user
/// secrets say, and the AI client is replaced by one that throws, so no test host can reach the real Anthropic API.
/// </summary>
public class TestApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiImport:Enabled"] = "false",
            ["AiImport:ApiKey"] = "",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRecipeLineAi>();
            services.AddSingleton<IRecipeLineAi, NoRealAiClient>();
        });
    }
}

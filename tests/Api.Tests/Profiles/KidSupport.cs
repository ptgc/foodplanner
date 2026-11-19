using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>Collects every log message of a test host, to prove no PIN is ever logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Messages)
            {
                owner.Messages.Add(formatter(state, exception) + exception);
            }
        }
    }
}

internal static class KidHost
{
    public static readonly Guid Middag = TagSeed.All.Single(t => t.Key == "middag").Id;
    public static readonly Guid Dessert = TagSeed.All.Single(t => t.Key == "dessert").Id;
    public static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;

    public const string Pin = "4827";

    public static AuthHost Create(
        string connectionString, Action<IServiceCollection>? configure = null, params (string Key, string Value)[] settings) =>
        AuthHost.Create(
            connectionString,
            settings.ToDictionary(s => s.Key, s => (string?)s.Value),
            configureServices: services =>
            {
                services.AddSingleton<IStartupFilter, PolicyTestEndpointsStartupFilter>();
                configure?.Invoke(services);
            });

    public static async Task<Guid> CreateRecipeAsync(this ApiClient client, Guid household, params Guid[] tagIds)
    {
        using var response = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Kid " + Guid.NewGuid().ToString("N"),
            servings = 4,
            ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
            tagIds,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static async Task SetPinAsync(this ApiClient client, string pin = Pin)
    {
        using var response = await client.PutAsync("/api/auth/pin", new { pin });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static async Task<Guid> CreateProfileAsync(this ApiClient client, Guid household, string? name = null, string avatar = "fox")
    {
        using var response = await client.PostAsync(
            $"/api/households/{household}/profiles", new { displayName = name ?? "Kid " + Guid.NewGuid().ToString("N")[..8], avatar });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> ActivateAsync(this ApiClient client, Guid household, Guid profile) =>
        client.PostAsync($"/api/households/{household}/profiles/{profile}/activate");

    /// <summary>Activates the profile and asserts the cookie was re-issued.</summary>
    public static async Task EnterKidModeAsync(this ApiClient client, Guid household, Guid profile)
    {
        using var response = await client.ActivateAsync(household, profile);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
    }

    public static async Task<JsonElement> MeAsync(this ApiClient client)
    {
        using var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    public static Task<HttpResponseMessage> UnlockAsync(this ApiClient client, string pin = Pin) =>
        client.PostAsync("/api/auth/pin/unlock", new { pin });

    /// <summary>An Owner with a household, a PIN and two kid profiles; not yet in kid mode.</summary>
    public static async Task<KidScenario> NewScenarioAsync(this AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await owner.Client.SetPinAsync();
        var first = await owner.Client.CreateProfileAsync(household, "Mia", "fox");
        var second = await owner.Client.CreateProfileAsync(household, "Ola", "owl");
        return new KidScenario(owner, household, first, second);
    }
}

internal sealed record KidScenario(SignedInUser Owner, Guid Household, Guid Profile1, Guid Profile2);

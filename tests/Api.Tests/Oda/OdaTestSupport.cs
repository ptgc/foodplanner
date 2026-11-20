using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Domain.Catalog;
using Foodplanner.Infrastructure.Oda;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Api.Tests.Oda;

internal static class OdaHost
{
    public const string ClientId = "test-client-id";
    public const string Origin = "https://app.example.test";
    public const string Monday = "2026-10-26";
    public const string Code = "fake-authorization-code-5c0de9";

    /// <summary>A host whose Oda transport is <paramref name="fake"/>; nothing in a test host can reach the real Oda.</summary>
    public static AuthHost Create(
        string connectionString, FakeOda fake, Foodplanner.Api.Tests.Profiles.CapturingLoggerProvider? logs = null, Action<IServiceCollection>? more = null, params (string Key, string Value)[] settings)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Oda:ClientId"] = ClientId,
            ["Oda:WebOrigin"] = Origin,
        };
        foreach (var (key, value) in settings)
        {
            configuration[key] = value;
        }

        return AuthHost.Create(
            connectionString,
            configuration,
            configureServices: services =>
            {
                services.RemoveAll<OdaInnerHandler>();
                services.AddSingleton(new OdaInnerHandler(fake));
                more?.Invoke(services);
                if (logs is not null)
                {
                    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                }
            });
    }

    public static string Url(Guid household, string action, string week = Monday) =>
        $"/api/households/{household}/shopping-lists/{week}/oda/{action}";

    public static async Task<(SignedInUser Owner, Guid Household)> ScenarioAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        owner.Client.Language = "en";
        var rice = IngredientSeed.All.Single(r => r.Key == "ris").Id;
        var egg = IngredientSeed.All.Single(r => r.Key == "egg").Id;
        var salt = IngredientSeed.All.Single(r => r.Key == "salt").Id; // a staple: "Check you have"
        var gram = UnitSeed.All.Single(r => r.Code == "g").Id;
        var pieces = UnitSeed.All.Single(r => r.Code == "stk").Id;
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Rice and egg",
            servings = 4,
            ingredients = new[]
            {
                new { ingredientId = rice, quantity = (decimal?)500, unitId = (Guid?)gram, note = (string?)null },
                new { ingredientId = egg, quantity = (decimal?)5, unitId = (Guid?)pieces, note = (string?)null },
                new { ingredientId = salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday}/entries", new { date = Monday, recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        using var generated = await owner.Client.PostAsync($"/api/households/{household}/shopping-lists/{Monday}/generate");
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        return (owner, household);
    }

    public static async Task<Guid> AddManualAsync(ApiClient client, Guid household, string label)
    {
        using var response = await client.PostAsync($"/api/households/{household}/shopping-lists/{Monday}/items", new { label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static async Task CheckAsync(ApiClient client, Guid household, Guid item)
    {
        using var response = await client.PutAsync($"/api/households/{household}/shopping-lists/{Monday}/items/{item}/check", new { @checked = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Starts the flow and returns the authorize URL and the <c>state</c> Oda would send back.</summary>
    public static async Task<(Uri Authorize, string State)> StartAsync(ApiClient client, Guid household, string week = Monday)
    {
        using var response = await client.PostAsync(Url(household, "start", week), new { confirmed = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authorize = new Uri((await response.JsonAsync()).GetProperty("authorizeUrl").GetString()!);
        return (authorize, Query(authorize)["state"]);
    }

    public static Task<HttpResponseMessage> CartAsync(ApiClient client, Guid household, string state, string week = Monday) =>
        client.PostAsync(Url(household, "cart", week), new { state, code = Code });

    public static async Task<JsonElement> StartAndCompleteAsync(ApiClient client, Guid household)
    {
        var (_, state) = await StartAsync(client, household);
        using var response = await CartAsync(client, household, state);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    public static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    public static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    /// <summary>True when any text column of any table contains one of the needles.</summary>
    public static async Task<List<string>> FindInDatabaseAsync(string connectionString, params string[] needles)
    {
        var hits = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var columns = new List<(string Table, string Column)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT TABLE_SCHEMA + '.' + TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('nvarchar','varchar','nchar','char','ntext','text') AND TABLE_NAME IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE')";
            await using var reader = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.True(columns.Count > 20, "the scan must see the real tables");
        foreach (var (table, column) in columns)
        {
            foreach (var needle in needles)
            {
                await using var count = connection.CreateCommand();
                var parts = table.Split('.');
                count.CommandText = $"SELECT COUNT(*) FROM [{parts[0]}].[{parts[1]}] WHERE CAST([{column}] AS nvarchar(max)) LIKE @needle";
                count.Parameters.AddWithValue("@needle", "%" + needle + "%");
                if ((int)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0)
                {
                    hits.Add($"{table}.{column} contains {needle}");
                }
            }
        }

        return hits;
    }
}

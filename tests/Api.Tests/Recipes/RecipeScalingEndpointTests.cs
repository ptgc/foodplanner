using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>GET /recipes/{id}/scaled?servings= (T-040): Domain scaling plus display rounding, validation, tenancy.</summary>
public sealed class RecipeScalingEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;
    private static readonly Guid Flour = IngredientSeed.All.Single(r => r.Key == "hvetemel").Id;
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;
    private static readonly Guid Dl = UnitSeed.All.Single(r => r.Code == "dl").Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, Guid id, string query = "") =>
        $"/api/households/{household}/recipes/{id}/scaled{query}";

    private static async Task<Guid> CreateAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Skalering " + Guid.NewGuid().ToString("N"),
            servings = 4,
            steps = new[] { new { text = "Bland" } },
            ingredients = new object[]
            {
                new { ingredientId = Flour, quantity = 500m, unitId = Gram, note = (string?)null },
                new { ingredientId = Flour, quantity = 2.5m, unitId = Dl, note = (string?)null },
                new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Four_to_six_servings_scales_rounds_and_picks_display_units()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var id = await CreateAsync(owner.Client, household);

        using var response = await owner.Client.GetAsync(Url(household, id, "?servings=6"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(4, body.GetProperty("baseServings").GetInt32());
        Assert.Equal(6, body.GetProperty("servings").GetInt32());
        var lines = body.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(750m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("g", lines[0].GetProperty("unitCode").GetString());
        Assert.Equal(375m, lines[1].GetProperty("quantity").GetDecimal()); // 2.5 dl * 1.5 = 3.75 dl, shown as ml
        Assert.Equal("ml", lines[1].GetProperty("unitCode").GetString());
        Assert.Equal("Hvetemel", lines[1].GetProperty("ingredientName").GetString());
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("quantity").ValueKind); // to taste unchanged
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("unitCode").ValueKind);

        using var many = await owner.Client.GetAsync(Url(household, id, "?servings=8"));
        var big = (await many.JsonAsync()).GetProperty("ingredients").EnumerateArray().First();
        Assert.Equal(1m, big.GetProperty("quantity").GetDecimal()); // 1000 g reads 1 kg
        Assert.Equal("kg", big.GetProperty("unitCode").GetString());
    }

    [Fact]
    public async Task Servings_default_to_the_recipes_own_and_must_be_1_to_100()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var id = await CreateAsync(owner.Client, household);

        using var plain = await owner.Client.GetAsync(Url(household, id));
        var body = await plain.JsonAsync();
        Assert.Equal(4, body.GetProperty("servings").GetInt32());
        Assert.Equal(500m, body.GetProperty("ingredients").EnumerateArray().First().GetProperty("quantity").GetDecimal());

        foreach (var bad in new[] { "0", "101", "-3" })
        {
            using var response = await owner.Client.GetAsync(Url(household, id, "?servings=" + bad));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("servings", out _));
        }

        using var text = await owner.Client.GetAsync(Url(household, id, "?servings=abc"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        using var edge = await owner.Client.GetAsync(Url(household, id, "?servings=100"));
        Assert.Equal(HttpStatusCode.OK, edge.StatusCode);
    }

    [Fact]
    public async Task Unknown_recipe_is_404_and_another_household_cannot_read_it()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var id = await CreateAsync(a.Client, householdA);
        var b = await host.SignInAsync();
        var householdB = await b.Client.CreateHouseholdAsync();

        using var unknown = await a.Client.GetAsync(Url(householdA, Guid.NewGuid(), "?servings=2"));
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        using var otherMember = await b.Client.GetAsync(Url(householdA, id, "?servings=2"));
        await otherMember.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var otherScope = await b.Client.GetAsync(Url(householdB, id, "?servings=2"));
        await otherScope.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
    }
}

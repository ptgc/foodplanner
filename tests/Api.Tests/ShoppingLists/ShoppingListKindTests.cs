using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>T-076: to taste, as needed, optional lines and alternatives on the generated shopping list.</summary>
public sealed class ShoppingListKindTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Rice = Ingredient("ris");
    private static readonly Guid Butter = Ingredient("smor");
    private static readonly Guid Oil = Ingredient("rapsolje");
    private static readonly Guid Pepper = Ingredient("sort_pepper");
    private static readonly Guid Cheese = Ingredient("ost");
    private static readonly Guid Milk = Ingredient("melk");
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;
    private static readonly Guid Tbsp = UnitSeed.All.Single(r => r.Code == "ss").Id;
    private static readonly Guid Deciliter = UnitSeed.All.Single(r => r.Code == "dl").Id;

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static object Line(Guid ingredient, string kind, decimal? quantity, Guid? unit, string purpose = "Main", bool optional = false, Guid[]? alternatives = null) =>
        new { ingredientId = ingredient, quantity, unitId = unit, note = (string?)null, amountKind = kind, purpose, isOptional = optional, alternativeIds = alternatives };

    private static async Task<Guid> PlanRecipeAsync(ApiClient client, Guid household, int servings, int planned, params object[] lines)
    {
        using var created = await client.PostAsync($"/api/households/{household}/recipes", new { title = "Stek " + Guid.NewGuid().ToString("N"), servings, ingredients = lines });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var recipe = (await created.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries", new { date = Monday.ToString("yyyy-MM-dd"), recipeId = recipe, servings = planned });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        return recipe;
    }

    private static async Task<JsonElement> GenerateAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync($"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}/generate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static JsonElement Find(JsonElement list, string section, string name) =>
        list.GetProperty(section).EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);

    private static string[] Names(JsonElement list, string section) =>
        [.. list.GetProperty(section).EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    [Fact]
    public async Task To_taste_as_needed_optional_and_small_frying_fat_are_checked_not_bought_and_alternatives_are_shown()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await PlanRecipeAsync(owner.Client, household, 4, 4,
            Line(Rice, "Measured", 200, Gram),
            Line(Pepper, "ToTaste", null, null, "Seasoning"),
            Line(Butter, "AsNeeded", null, null, "Frying", alternatives: [Oil]),
            Line(Cheese, "Measured", 1, Deciliter, optional: true),
            Line(Milk, "Measured", 2, Tbsp, "Frying"));

        var list = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Ris"], Names(list, "main"));
        Assert.Equal(["Melk", "Ost", "Smør", "Sort pepper"], Names(list, "checkYouHave").Order(StringComparer.Ordinal));
        Assert.Empty(Find(list, "checkYouHave", "Sort pepper").GetProperty("quantities").EnumerateArray());
        var butter = Find(list, "checkYouHave", "Smør");
        Assert.Equal(["Rapsolje"], butter.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("ingredientName").GetString()));
        Assert.Empty(Find(list, "main", "Ris").GetProperty("alternatives").EnumerateArray());
    }

    [Fact]
    public async Task A_measured_line_in_another_recipe_keeps_the_ingredient_on_the_main_list_without_the_checked_amounts()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await PlanRecipeAsync(owner.Client, household, 4, 4, Line(Milk, "Measured", 2, Tbsp, "Frying"));
        await PlanRecipeAsync(owner.Client, household, 4, 8, Line(Milk, "Measured", 3, Deciliter));

        var list = await GenerateAsync(owner.Client, household);

        var milk = Find(list, "main", "Melk");
        var quantity = Assert.Single(milk.GetProperty("quantities").EnumerateArray());
        // 3 dl scaled to 8 servings is 600 ml; the 2 ss of frying fat are not added.
        Assert.Equal(600m, quantity.GetProperty("amount").GetDecimal());
        Assert.Equal("ml", quantity.GetProperty("unitCode").GetString());
        Assert.Equal(2, milk.GetProperty("recipes").GetArrayLength());
        Assert.DoesNotContain("Melk", Names(list, "checkYouHave"));
    }

    [Fact]
    public async Task Regenerating_moves_a_changed_item_between_the_sections()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await PlanRecipeAsync(owner.Client, household, 4, 4, Line(Pepper, "Measured", 1, Gram));
        Assert.Equal(["Sort pepper"], Names(await GenerateAsync(owner.Client, household), "checkYouHave")); // a staple

        using var update = await owner.Client.PutAsync(
            $"/api/households/{household}/recipes/{recipe}",
            new { title = "Stek", servings = 4, ingredients = new[] { Line(Cheese, "ToTaste", null, null, "Seasoning") } });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var list = await GenerateAsync(owner.Client, household);

        Assert.Empty(Names(list, "main"));
        Assert.Equal(["Ost"], Names(list, "checkYouHave"));
    }
}

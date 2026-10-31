using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>T-089: the food type of a recipe (derived or manual), the list filter and the protein class of own ingredients.</summary>
public sealed class RecipeFoodTypeTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Guid Salmon = Seed("laks");
    private static readonly Guid Chicken = Seed("kyllingfilet");
    private static readonly Guid Potato = Seed("potet");
    private static readonly Guid Milk = Seed("melk");
    private static readonly Guid Butter = Seed("smor");
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;

    private static Guid Seed(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Base(Guid household) => $"/api/households/{household}";

    private static object Line(Guid ingredient, string purpose = "Main", bool optional = false) =>
        new { ingredientId = ingredient, quantity = (decimal?)100m, unitId = (Guid?)Gram, purpose, isOptional = optional };

    private static object Body(object[] ingredients, string? foodType = null) =>
        new { title = "Rett " + Guid.NewGuid().ToString("N")[..8], servings = 4, ingredients, foodType };

    private static async Task<JsonElement> CreateAsync(ApiClient client, Guid household, object body)
    {
        using var response = await client.PostAsync($"{Base(household)}/recipes", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string Type(JsonElement recipe) => recipe.GetProperty("foodType").GetString()!;

    [Fact]
    public async Task The_type_is_derived_from_the_main_lines_and_follows_an_update()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        // Butter for frying and chicken as an optional line do not count: salmon decides.
        var created = await CreateAsync(owner.Client, household, Body([Line(Salmon), Line(Potato), Line(Butter, "Frying"), Line(Chicken, optional: true)]));
        Assert.Equal("Fish", Type(created));
        Assert.False(created.GetProperty("foodTypeIsManual").GetBoolean());
        var id = created.GetProperty("id").GetGuid();

        using var toDairy = await owner.Client.PutAsync($"{Base(household)}/recipes/{id}", Body([Line(Potato), Line(Milk)]));
        Assert.Equal("Vegetarian", Type(await toDairy.JsonAsync()));

        using var toVegan = await owner.Client.PutAsync($"{Base(household)}/recipes/{id}", Body([Line(Potato)]));
        Assert.Equal("Vegan", Type(await toVegan.JsonAsync()));

        using var empty = await owner.Client.PutAsync($"{Base(household)}/recipes/{id}", Body([]));
        Assert.Equal("Unknown", Type(await empty.JsonAsync()));
    }

    [Fact]
    public async Task A_manual_type_wins_and_automatic_takes_over_again_when_it_is_left_out()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var created = await CreateAsync(owner.Client, household, Body([Line(Salmon)], "Beef"));
        Assert.Equal("Beef", Type(created));
        Assert.True(created.GetProperty("foodTypeIsManual").GetBoolean());
        var id = created.GetProperty("id").GetGuid();

        using var keep = await owner.Client.PutAsync($"{Base(household)}/recipes/{id}", Body([Line(Chicken)], "beef"));
        Assert.Equal("Beef", Type(await keep.JsonAsync()));

        using var auto = await owner.Client.PutAsync($"{Base(household)}/recipes/{id}", Body([Line(Chicken)]));
        var autoBody = await auto.JsonAsync();
        Assert.Equal("Chicken", Type(autoBody));
        Assert.False(autoBody.GetProperty("foodTypeIsManual").GetBoolean());

        using var get = await owner.Client.GetAsync($"{Base(household)}/recipes/{id}");
        Assert.Equal("Chicken", Type(await get.JsonAsync()));
    }

    [Theory]
    [InlineData("Poultry")]
    [InlineData("3")]
    public async Task An_invalid_food_type_is_a_validation_problem_and_writes_nothing(string value)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync($"{Base(household)}/recipes", Body([Line(Salmon)], value));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("foodType", out _));

        using var list = await owner.Client.GetAsync($"{Base(household)}/recipes");
        Assert.Equal(0, (await list.JsonAsync()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_list_filters_by_one_or_more_food_types_and_items_carry_the_type()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var fish = (await CreateAsync(owner.Client, household, Body([Line(Salmon)]))).GetProperty("id").GetGuid();
        var chicken = (await CreateAsync(owner.Client, household, Body([Line(Chicken)]))).GetProperty("id").GetGuid();
        var vegan = (await CreateAsync(owner.Client, household, Body([Line(Potato)]))).GetProperty("id").GetGuid();
        var manual = (await CreateAsync(owner.Client, household, Body([Line(Potato)], "Fish"))).GetProperty("id").GetGuid();

        async Task<List<Guid>> IdsAsync(string query)
        {
            using var response = await owner.Client.GetAsync($"{Base(household)}/recipes{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.JsonAsync()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        }

        Assert.Equivalent(new[] { fish, manual }, await IdsAsync("?foodType=Fish"));
        Assert.Equivalent(new[] { chicken, vegan }, await IdsAsync("?foodType=Chicken&foodType=vegan"));
        Assert.Equivalent(new[] { fish, chicken, vegan, manual }, await IdsAsync(string.Empty));
        Assert.Empty(await IdsAsync("?foodType=Lamb"));

        using var bad = await owner.Client.GetAsync($"{Base(household)}/recipes?foodType=Poultry");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var all = await owner.Client.GetAsync($"{Base(household)}/recipes?foodType=Chicken");
        Assert.Equal("Chicken", Type((await all.JsonAsync()).GetProperty("items")[0]));
    }

    [Fact]
    public async Task The_filter_never_shows_another_households_recipes()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        await CreateAsync(a.Client, householdA, Body([Line(Salmon)]));
        await CreateAsync(b.Client, householdB, Body([Line(Salmon)]));

        using var list = await a.Client.GetAsync($"{Base(householdA)}/recipes?foodType=Fish");
        Assert.Equal(1, (await list.JsonAsync()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task An_own_ingredient_is_unclassified_until_set_and_its_recipes_follow_the_class()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var created = await owner.Client.PostAsync($"{Base(household)}/ingredients", new { name = "Reinsdyr " + Guid.NewGuid().ToString("N")[..6], isStaple = false, dimension = "Mass" });
        var createdBody = await created.JsonAsync();
        Assert.Equal("Unknown", createdBody.GetProperty("proteinClass").GetString());
        var own = createdBody.GetProperty("id").GetGuid();
        var name = createdBody.GetProperty("name").GetString();

        var auto = (await CreateAsync(owner.Client, household, Body([Line(own), Line(Potato)]))).GetProperty("id").GetGuid();
        var manual = (await CreateAsync(owner.Client, household, Body([Line(own)], "Vegan"))).GetProperty("id").GetGuid();
        using (var before = await owner.Client.GetAsync($"{Base(household)}/recipes/{auto}"))
        {
            Assert.Equal("Unknown", Type(await before.JsonAsync()));
        }

        using var set = await owner.Client.PutAsync($"{Base(household)}/ingredients/{own}", new { name, proteinClass = "Meat" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Meat", (await set.JsonAsync()).GetProperty("proteinClass").GetString());

        using (var after = await owner.Client.GetAsync($"{Base(household)}/recipes/{auto}"))
        {
            Assert.Equal("MixedMeat", Type(await after.JsonAsync()));
        }

        using (var untouched = await owner.Client.GetAsync($"{Base(household)}/recipes/{manual}"))
        {
            Assert.Equal("Vegan", Type(await untouched.JsonAsync())); // a manual type is never overwritten
        }

        // Omitting proteinClass on a rename keeps it.
        using var rename = await owner.Client.PutAsync($"{Base(household)}/ingredients/{own}", new { name = name + "x" });
        Assert.Equal("Meat", (await rename.JsonAsync()).GetProperty("proteinClass").GetString());

        using var bad = await owner.Client.PutAsync($"{Base(household)}/ingredients/{own}", new { name, proteinClass = "Poultry" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_protein_class_can_be_given_when_creating_an_own_ingredient_and_global_rows_stay_read_only()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var created = await owner.Client.PostAsync($"{Base(household)}/ingredients", new { name = "Torsk-ish " + Guid.NewGuid().ToString("N")[..6], isStaple = false, dimension = "Mass", proteinClass = "Fish" });
        Assert.Equal("Fish", (await created.JsonAsync()).GetProperty("proteinClass").GetString());

        using var bad = await owner.Client.PostAsync($"{Base(household)}/ingredients", new { name = "Zzz " + Guid.NewGuid().ToString("N")[..6], isStaple = false, dimension = "Mass", proteinClass = "Poultry" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var global = await owner.Client.PutAsync($"{Base(household)}/ingredients/{Salmon}", new { name = "Laks", proteinClass = "None" });
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);

        using var catalog = await owner.Client.GetAsync($"{Base(household)}/ingredients?search=laks");
        var salmon = (await catalog.JsonAsync()).EnumerateArray().First(i => i.GetProperty("id").GetGuid() == Salmon);
        Assert.Equal("Fish", salmon.GetProperty("proteinClass").GetString());
    }

    [Fact]
    public async Task A_child_cannot_set_the_food_type_or_a_protein_class()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");

        using var recipe = await child.Client.PostAsync($"{Base(household)}/recipes", Body([Line(Salmon)], "Fish"));
        Assert.Equal(HttpStatusCode.Forbidden, recipe.StatusCode);

        using var ingredient = await child.Client.PostAsync($"{Base(household)}/ingredients", new { name = "Barnemat", isStaple = false, dimension = "Mass", proteinClass = "Fish" });
        Assert.Equal(HttpStatusCode.Forbidden, ingredient.StatusCode);
    }
}

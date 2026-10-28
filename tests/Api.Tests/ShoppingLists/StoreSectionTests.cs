using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>Store sections (T-085, Q-019): the section of an ingredient, and the shopping list and export sorted by it.</summary>
public sealed class StoreSectionTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;

    private static Guid Seed(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Base(Guid household) => $"/api/households/{household}";

    private static string NewName(string prefix = "Aaa") => $"{prefix}{Guid.NewGuid():N}"[..20];

    private static async Task<JsonElement> CreateOwnAsync(ApiClient client, Guid household, string name, string? section)
    {
        using var response = await client.PostAsync(
            $"{Base(household)}/ingredients", new { name, isStaple = false, dimension = "Mass", section });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task PlanRecipeAsync(ApiClient client, Guid household, params Guid[] ingredients)
    {
        using var recipe = await client.PostAsync($"{Base(household)}/recipes", new
        {
            title = "Sections " + Guid.NewGuid().ToString("N")[..6],
            servings = 4,
            ingredients = ingredients.Select(i => new { ingredientId = i, quantity = (decimal?)100, unitId = (Guid?)Gram, note = (string?)null }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await client.PostAsync(
            $"{Base(household)}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
    }

    private static async Task<JsonElement> GenerateAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}/generate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static (string Name, string Section)[] Items(JsonElement list, string part) =>
        [.. list.GetProperty(part).EnumerateArray().Select(i => (i.GetProperty("name").GetString()!, i.GetProperty("storeSection").GetString()!))];

    [Fact]
    public async Task The_list_is_sorted_by_store_section_then_name_with_manual_items_and_unassigned_ingredients_last()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var unassigned = await CreateOwnAsync(owner.Client, household, NewName("Abc"), section: null);
        var ownDairy = await CreateOwnAsync(owner.Client, household, NewName("Zzz"), "Dairy");
        await PlanRecipeAsync(
            owner.Client, household,
            Seed("ris"), Seed("melk"), Seed("tomat"), Seed("fiskepinner"), Seed("kyllingfilet"), Seed("agurk"), Seed("egg"),
            unassigned.GetProperty("id").GetGuid(), ownDairy.GetProperty("id").GetGuid(),
            Seed("salt"), Seed("smor"));
        using var manual = await owner.Client.PostAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}/items", new { label = "Batterier" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        var generated = await GenerateAsync(owner.Client, household);
        using var read = await owner.Client.GetAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}");
        var list = await read.JsonAsync();

        foreach (var response in new[] { generated, list })
        {
            var main = Items(response, "main");
            Assert.Equal(
                [
                    ("Agurk", "FruitAndVegetables"),
                    ("Tomat", "FruitAndVegetables"),
                    ("Kyllingfilet", "MeatFishAndCheese"),
                    ("Fiskepinner", "Frozen"),
                    ("Egg", "Dairy"),
                    ("Melk", "Dairy"),
                    (ownDairy.GetProperty("name").GetString()!, "Dairy"),
                    ("Ris", "DryGoods"),
                    (unassigned.GetProperty("name").GetString()!, "Other"),
                    ("Batterier", "Other"),
                ],
                main);

            // "Check you have" keeps the same order: butter (dairy) before salt (dry goods).
            Assert.Equal([("Smør", "Dairy"), ("Salt", "DryGoods")], Items(response, "checkYouHave"));
        }
    }

    [Fact]
    public async Task The_order_holds_in_English_too()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        owner.Client.Language = "en";
        var household = await owner.Client.CreateHouseholdAsync();
        await PlanRecipeAsync(owner.Client, household, Seed("ris"), Seed("melk"), Seed("tomat"), Seed("kyllingfilet"), Seed("egg"));

        var list = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Tomato", "Chicken breast fillet", "Egg", "Milk", "Rice"], Items(list, "main").Select(i => i.Name));
    }

    [Fact]
    public async Task An_own_ingredient_gets_Other_by_default_and_a_section_on_create_and_edit()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var plain = await CreateOwnAsync(owner.Client, household, NewName(), section: null);
        Assert.Equal("Other", plain.GetProperty("section").GetString());
        var produce = await CreateOwnAsync(owner.Client, household, NewName(), "fruitAndVegetables");
        Assert.Equal("FruitAndVegetables", produce.GetProperty("section").GetString());

        // Edit: the name stays, the section changes; leaving the section out keeps it.
        var id = plain.GetProperty("id").GetGuid();
        using var changed = await owner.Client.PutAsync($"{Base(household)}/ingredients/{id}", new { name = plain.GetProperty("name").GetString(), section = "Frozen" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("Frozen", (await changed.JsonAsync()).GetProperty("section").GetString());
        using var renamed = await owner.Client.PutAsync($"{Base(household)}/ingredients/{id}", new { name = NewName() });
        Assert.Equal("Frozen", (await renamed.JsonAsync()).GetProperty("section").GetString());

        using var own = await owner.Client.GetAsync($"{Base(household)}/ingredients/own");
        var sections = (await own.JsonAsync()).EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("section").GetString());
        Assert.Equal("Frozen", sections[id]);
        Assert.Equal("FruitAndVegetables", sections[produce.GetProperty("id").GetGuid()]);
    }

    [Fact]
    public async Task An_unknown_section_is_a_validation_problem_and_a_global_ingredient_stays_read_only()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var create = await owner.Client.PostAsync(
            $"{Base(household)}/ingredients", new { name = NewName(), isStaple = false, dimension = "Mass", section = "Basement" });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.True((await create.JsonAsync()).GetProperty("errors").TryGetProperty("section", out _));

        var own = await CreateOwnAsync(owner.Client, household, NewName(), null);
        using var edit = await owner.Client.PutAsync(
            $"{Base(household)}/ingredients/{own.GetProperty("id").GetGuid()}", new { name = own.GetProperty("name").GetString(), section = "3" });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);

        using var global = await owner.Client.PutAsync($"{Base(household)}/ingredients/{Seed("ris")}", new { name = "Ris", section = "Dairy" });
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);
    }

    [Fact]
    public async Task The_catalog_returns_the_section_of_every_ingredient()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.GetAsync($"{Base(household)}/ingredients?search=Melk");
        var milk = (await response.JsonAsync()).EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Melk");

        Assert.Equal("Dairy", milk.GetProperty("section").GetString());
    }

    [Fact]
    public async Task Merge_keeps_the_section_of_the_target_and_the_list_item_follows_it()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var source = await CreateOwnAsync(owner.Client, household, NewName(), "Frozen");
        await PlanRecipeAsync(owner.Client, household, source.GetProperty("id").GetGuid());
        var before = Items(await GenerateAsync(owner.Client, household), "main");
        Assert.Equal("Frozen", Assert.Single(before).Section);

        using var merged = await owner.Client.PostAsync(
            $"{Base(household)}/ingredients/{source.GetProperty("id").GetGuid()}/merge", new { targetIngredientId = Seed("melk") });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal("Dairy", (await merged.JsonAsync()).GetProperty("target").GetProperty("section").GetString());

        using var read = await owner.Client.GetAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}");
        Assert.Equal([("Melk", "Dairy")], Items(await read.JsonAsync(), "main"));
    }

    [Fact]
    public async Task The_text_export_follows_the_section_order()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await PlanRecipeAsync(owner.Client, household, Seed("ris"), Seed("melk"), Seed("tomat"));
        await GenerateAsync(owner.Client, household);
        using var manual = await owner.Client.PostAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}/items", new { label = "Batterier" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        using var export = await owner.Client.GetAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}/export");

        Assert.Equal(
            "Å handle\n- Tomat 100 g\n- Melk 100 g\n- Ris 100 g\n- Batterier",
            (await export.JsonAsync()).GetProperty("text").GetString());
    }

    [Fact]
    public void Every_section_name_the_api_accepts_parses_to_the_enum()
    {
        foreach (var section in Enum.GetValues<StoreSection>())
        {
            Assert.True(StoreSections.TryParse(section.ToString(), out var parsed));
            Assert.Equal(section, parsed);
        }
    }
}

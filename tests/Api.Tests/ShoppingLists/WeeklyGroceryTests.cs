using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>T-092: the household's weekly groceries and the shopping list button that puts them on a week.</summary>
public sealed class WeeklyGroceryTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Milk = Ingredient("melk"); // Volume, Dairy
    private static readonly Guid Bread = Ingredient("brod"); // Count, DryGoods
    private static readonly Guid Cheese = Ingredient("ost"); // Mass, MeatFishAndCheese
    private static readonly Guid Salt = Ingredient("salt"); // staple
    private static readonly Guid Litre = IngredientUnit("l");
    private static readonly Guid Gram = IngredientUnit("g");
    private static readonly Guid Millilitre = IngredientUnit("ml");

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static Guid IngredientUnit(string code) => UnitSeed.All.Single(r => r.Code == code).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/weekly-groceries{rest}";

    private static string ListUrl(Guid household, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}{rest}";

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    private static async Task<JsonElement> AddAsync(ApiClient client, Guid household, Guid ingredient, decimal? quantity = null, Guid? unit = null)
    {
        using var response = await client.PostAsync(Url(household), new { ingredientId = ingredient, quantity, unitId = unit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> PressAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(ListUrl(household, "/weekly"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static JsonElement Item(JsonElement list, string section, string name) =>
        list.GetProperty(section).EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);

    private static string[] Names(JsonElement list, string section) =>
        [.. list.GetProperty(section).EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    private static async Task AssertValidationAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for '{key}'");
    }

    private static async Task<Guid> PlanMilkRecipeAsync(ApiClient client, Guid household, decimal millilitres)
    {
        using var recipe = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Pannekaker",
            servings = 4,
            ingredients = new[] { new { ingredientId = Milk, quantity = (decimal?)millilitres, unitId = (Guid?)Millilitre, note = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        return recipeId;
    }

    [Fact]
    public async Task An_adult_manages_the_set_and_it_is_listed_by_store_section()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var milk = await AddAsync(owner.Client, household, Milk, 1.5m, Litre);
        var bread = await AddAsync(owner.Client, household, Bread);
        Assert.Equal("Melk", milk.GetProperty("name").GetString());
        Assert.Equal("l", milk.GetProperty("unitCode").GetString());
        Assert.Equal(JsonValueKind.Null, bread.GetProperty("quantity").ValueKind);

        using var list = await owner.Client.GetAsync(Url(household));
        var rows = (await list.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["Melk", "Brød"], rows.Select(r => r.GetProperty("name").GetString())); // Dairy before DryGoods
        Assert.Equal("Dairy", rows[0].GetProperty("section").GetString());

        using var update = await owner.Client.PutAsync(Url(household, $"/{milk.GetProperty("id").GetGuid()}"), new { quantity = 2m, unitId = Litre });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(2m, (await update.JsonAsync()).GetProperty("quantity").GetDecimal());

        using var remove = await owner.Client.DeleteAsync(Url(household, $"/{bread.GetProperty("id").GetGuid()}"));
        using var removeAgain = await owner.Client.DeleteAsync(Url(household, $"/{bread.GetProperty("id").GetGuid()}"));
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removeAgain.StatusCode);
        using var after = await owner.Client.GetAsync(Url(household));
        Assert.Equal(1, (await after.JsonAsync()).GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Input_is_validated_and_an_ingredient_can_only_be_added_once()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var noIngredient = await owner.Client.PostAsync(Url(household), new { quantity = (decimal?)null });
        using var unknownIngredient = await owner.Client.PostAsync(Url(household), new { ingredientId = Guid.NewGuid() });
        using var halfAmount = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, quantity = 1m });
        using var zero = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, quantity = 0m, unitId = Litre });
        using var manyDecimals = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, quantity = 1.00001m, unitId = Litre });
        using var unknownUnit = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk, quantity = 1m, unitId = Guid.NewGuid() });
        await AssertValidationAsync(noIngredient, "ingredientId");
        await AssertValidationAsync(unknownIngredient, "ingredientId");
        await AssertValidationAsync(halfAmount, "quantity");
        await AssertValidationAsync(zero, "quantity");
        await AssertValidationAsync(manyDecimals, "quantity");
        await AssertValidationAsync(unknownUnit, "unitId");

        await AddAsync(owner.Client, household, Milk);
        using var duplicate = await owner.Client.PostAsync(Url(household), new { ingredientId = Milk });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.EndsWith("weekly-grocery-exists", (await duplicate.JsonAsync()).GetProperty("type").GetString());

        using var missing = await owner.Client.PutAsync(Url(household, $"/{Guid.NewGuid()}"), new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_household_has_at_most_fifty_weekly_groceries()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var ids = IngredientSeed.All.Take(WeeklyGroceryLimit + 1).Select(r => r.Id).ToList();

        foreach (var id in ids.Take(WeeklyGroceryLimit))
        {
            await AddAsync(owner.Client, household, id);
        }

        using var over = await owner.Client.PostAsync(Url(household), new { ingredientId = ids[WeeklyGroceryLimit] });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.EndsWith("weekly-grocery-limit", (await over.JsonAsync()).GetProperty("type").GetString());
    }

    private const int WeeklyGroceryLimit = Foodplanner.Domain.ShoppingLists.WeeklyGrocery.MaxPerHousehold;

    [Fact]
    public async Task Another_households_ingredient_is_refused_like_an_unknown_one()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        using var created = await other.Client.PostAsync($"/api/households/{otherHousehold}/ingredients", new { name = "Hemmelig " + Guid.NewGuid().ToString("N")[..8], dimension = "Mass", isStaple = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var foreign = (await created.JsonAsync()).GetProperty("id").GetGuid();

        using var response = await owner.Client.PostAsync(Url(household), new { ingredientId = foreign });

        await AssertValidationAsync(response, "ingredientId");
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task A_child_reads_the_set_and_presses_the_button_but_cannot_change_the_set()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var adult = await host.JoinAsync(owner, household, "Adult");
        var milk = await AddAsync(owner.Client, household, Milk);
        var id = milk.GetProperty("id").GetGuid();

        using var read = await child.Client.GetAsync(Url(household));
        using var add = await child.Client.PostAsync(Url(household), new { ingredientId = Bread });
        using var edit = await child.Client.PutAsync(Url(household, $"/{id}"), new { });
        using var remove = await child.Client.DeleteAsync(Url(household, $"/{id}"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);

        var list = await PressAsync(child.Client, household);
        Assert.Equal(["Melk"], Names(list, "main"));
        using var adultAdd = await adult.Client.PostAsync(Url(household), new { ingredientId = Bread });
        Assert.Equal(HttpStatusCode.Created, adultAdd.StatusCode);
    }

    [Fact]
    public async Task A_non_member_is_refused_everywhere()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var stranger = await host.SignInAsync();

        using var read = await stranger.Client.GetAsync(Url(household));
        using var add = await stranger.Client.PostAsync(Url(household), new { ingredientId = Milk });
        using var press = await stranger.Client.PostAsync(ListUrl(household, "/weekly"));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, press.StatusCode);
    }

    [Fact]
    public async Task The_button_puts_the_set_on_the_list_and_pressing_again_changes_nothing()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Milk, 2m, Litre);
        await AddAsync(owner.Client, household, Bread);

        var first = await PressAsync(owner.Client, household);

        Assert.Equal(["Melk", "Brød"], Names(first, "main")); // store sections: Dairy, DryGoods
        var milk = Item(first, "main", "Melk");
        Assert.True(milk.GetProperty("isWeekly").GetBoolean());
        Assert.Equal("Dairy", milk.GetProperty("storeSection").GetString());
        var amount = milk.GetProperty("quantities").EnumerateArray().Single();
        Assert.Equal((2m, "l"), (amount.GetProperty("amount").GetDecimal(), amount.GetProperty("unitCode").GetString()));
        Assert.Equal(0, Item(first, "main", "Brød").GetProperty("quantities").GetArrayLength());

        var second = await PressAsync(owner.Client, household);
        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Equal(2, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM ShoppingListItems WHERE HouseholdId = @h", ("@h", household)))!);
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task The_button_with_an_empty_set_changes_nothing_and_creates_no_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var list = await PressAsync(owner.Client, household);

        Assert.Equal(JsonValueKind.Null, list.GetProperty("id").ValueKind);
        Assert.Empty(Names(list, "main"));
    }

    [Fact]
    public async Task A_weekly_amount_is_added_to_the_recipe_item_and_a_different_dimension_is_an_extra_line()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await PlanMilkRecipeAsync(owner.Client, household, 500);
        using var generate = await owner.Client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        var milkWeekly = await AddAsync(owner.Client, household, Milk, 1m, Litre);

        var list = await PressAsync(owner.Client, household);

        var milk = Item(list, "main", "Melk");
        var lines = milk.GetProperty("quantities").EnumerateArray().Select(q => (q.GetProperty("amount").GetDecimal(), q.GetProperty("unitCode").GetString())).ToList();
        Assert.Equal([(1.5m, "l")], lines); // 500 ml from the recipe + 1 l weekly, one item, one line
        Assert.Equal(1, list.GetProperty("main").GetArrayLength());
        Assert.True(milk.GetProperty("isWeekly").GetBoolean());
        Assert.Single(milk.GetProperty("recipes").EnumerateArray()); // still "from Pannekaker"

        // A different dimension of the same ingredient stays a separate quantity line on the same item.
        using var change = await owner.Client.PutAsync(Url(household, $"/{milkWeekly.GetProperty("id").GetGuid()}"), new { quantity = 300m, unitId = Gram });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var pressed = await PressAsync(owner.Client, household);
        Assert.Equal(1, pressed.GetProperty("main").GetArrayLength());
        var changed = Item(pressed, "main", "Melk").GetProperty("quantities").EnumerateArray()
            .Select(q => (q.GetProperty("amount").GetDecimal(), q.GetProperty("unitCode").GetString())).ToList();
        Assert.Equal([(300m, "g"), (500m, "ml")], changed.OrderBy(c => c.Item2, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Regenerating_from_the_plan_keeps_the_weekly_items()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Bread);
        await AddAsync(owner.Client, household, Cheese, 500m, Gram);
        await PressAsync(owner.Client, household);

        using var regenerate = await owner.Client.PostAsync(ListUrl(household, "/generate"));
        var list = await regenerate.JsonAsync();
        Assert.Equal(["Ost", "Brød"], Names(list, "main")); // MeatFishAndCheese before DryGoods

        // The plan now needs milk too; the weekly items stay, and removing the plan entry later keeps them as well.
        await PlanMilkRecipeAsync(owner.Client, household, 500);
        using var again = await owner.Client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(["Ost", "Melk", "Brød"], Names(await again.JsonAsync(), "main")); // cheese, dairy, dry goods
    }

    [Fact]
    public async Task A_removed_weekly_item_stays_removed_through_regeneration_and_comes_back_when_pressed_again()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await AddAsync(owner.Client, household, Bread);
        await AddAsync(owner.Client, household, Cheese, 500m, Gram);
        var list = await PressAsync(owner.Client, household);
        var breadId = Item(list, "main", "Brød").GetProperty("id").GetGuid();

        using var remove = await owner.Client.DeleteAsync(ListUrl(household, $"/items/{breadId}"));
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using var read = await owner.Client.GetAsync(ListUrl(household));
        Assert.Equal(["Ost"], Names(await read.JsonAsync(), "main"));
        using var regenerate = await owner.Client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(["Ost"], Names(await regenerate.JsonAsync(), "main"));

        Assert.Equal(["Ost", "Brød"], Names(await PressAsync(owner.Client, household), "main"));
    }

    [Fact]
    public async Task Removing_the_weekly_part_of_a_recipe_item_leaves_the_recipe_amount_and_a_weekly_item_can_be_checked_off()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await PlanMilkRecipeAsync(owner.Client, household, 500);
        await AddAsync(owner.Client, household, Milk, 1m, Litre);
        await AddAsync(owner.Client, household, Bread);
        var list = await PressAsync(owner.Client, household);
        var breadId = Item(list, "main", "Brød").GetProperty("id").GetGuid();
        var milkId = Item(list, "main", "Melk").GetProperty("id").GetGuid();

        using var check = await owner.Client.PutAsync(ListUrl(household, $"/items/{breadId}/check"), new { @checked = true });
        Assert.True((await check.JsonAsync()).GetProperty("checked").GetBoolean());

        using var remove = await owner.Client.DeleteAsync(ListUrl(household, $"/items/{milkId}"));
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using var read = await owner.Client.GetAsync(ListUrl(household));
        var after = await read.JsonAsync();
        var milk = Item(after, "main", "Melk");
        Assert.False(milk.GetProperty("isWeekly").GetBoolean());
        Assert.Equal(milkId, milk.GetProperty("id").GetGuid());
        var amount = milk.GetProperty("quantities").EnumerateArray().Single();
        Assert.Equal((500m, "ml"), (amount.GetProperty("amount").GetDecimal(), amount.GetProperty("unitCode").GetString()));
        Assert.True(Item(after, "main", "Brød").GetProperty("checked").GetBoolean()); // untouched

        // A plan item that is not weekly still cannot be removed.
        using var refused = await owner.Client.DeleteAsync(ListUrl(household, $"/items/{milkId}"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task A_weekly_staple_moves_from_check_you_have_to_the_main_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Salt",
            servings = 2,
            ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
        });
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        (await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId })).Dispose();
        using var generate = await owner.Client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(["Salt"], Names(await generate.JsonAsync(), "checkYouHave"));
        await AddAsync(owner.Client, household, Salt, 1m, Gram);

        var list = await PressAsync(owner.Client, household);

        Assert.Empty(Names(list, "checkYouHave"));
        Assert.Equal(["Salt"], Names(list, "main"));
    }
}

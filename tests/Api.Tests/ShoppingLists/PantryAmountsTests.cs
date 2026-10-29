using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.ShoppingLists;

/// <summary>T-093: a pantry for everything, with amounts; the shopping list subtracts what the household has.</summary>
public sealed class PantryAmountsTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Cheese = Ingredient("ost"); // Mass, not a staple
    private static readonly Guid Tortillas = Ingredient("tortillalefser"); // Count, not a staple
    private static readonly Guid Milk = Ingredient("melk"); // Volume
    private static readonly Guid Salt = Ingredient("salt"); // staple
    private static readonly Guid Gram = IngredientUnit("g");
    private static readonly Guid Piece = IngredientUnit("stk");
    private static readonly Guid Litre = IngredientUnit("l");
    private static readonly Guid Decilitre = IngredientUnit("dl");

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static Guid IngredientUnit(string code) => UnitSeed.All.Single(r => r.Code == code).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/pantry{rest}";

    private static string ListUrl(Guid household, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{Monday:yyyy-MM-dd}{rest}";

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    /// <summary>The tacos of the task: 200 g cheese and 8 tortillas for 4 people, planned for the week.</summary>
    private static async Task<(SignedInUser Owner, Guid Household)> TacoScenarioAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Tacos",
            servings = 4,
            ingredients = new[]
            {
                new { ingredientId = Cheese, quantity = (decimal?)200, unitId = (Guid?)Gram, note = (string?)null },
                new { ingredientId = Tortillas, quantity = (decimal?)8, unitId = (Guid?)Piece, note = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.Created, recipe.StatusCode);
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        using var entry = await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        return (owner, household);
    }

    private static async Task<JsonElement> GenerateAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(ListUrl(household, "/generate"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task PutAsync(ApiClient client, Guid household, Guid ingredient, object body)
    {
        using var response = await client.PutAsync(Url(household, $"/{ingredient}"), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string[] Names(JsonElement list, string section) =>
        [.. list.GetProperty(section).EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    private static JsonElement Item(JsonElement list, string section, string name) =>
        list.GetProperty(section).EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);

    private static string Amount(JsonElement quantity) =>
        quantity.GetProperty("amount").GetDecimal().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Quantities(JsonElement item) =>
        [.. item.GetProperty("quantities").EnumerateArray().Select(q => $"{Amount(q)} {q.GetProperty("unitCode").GetString()}")];

    private static string? AtHome(JsonElement item)
    {
        var atHome = item.GetProperty("atHome");
        if (atHome.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var quantity = atHome.GetProperty("quantity");
        return quantity.ValueKind == JsonValueKind.Null ? "some" : $"{Amount(quantity)} {quantity.GetProperty("unitCode").GetString()}";
    }

    [Fact]
    public async Task Tacos_cheese_300_g_is_not_bought_and_tortillas_without_an_amount_are_checked()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        var before = await GenerateAsync(owner.Client, household);
        Assert.Equal(["Ost", "Tortillalefser"], Names(before, "main"));
        Assert.Null(AtHome(Item(before, "main", "Ost")));

        await PutAsync(owner.Client, household, Cheese, new { quantity = 300m, unitId = Gram });
        await PutAsync(owner.Client, household, Tortillas, new { });
        var list = await GenerateAsync(owner.Client, household);

        Assert.Empty(Names(list, "main"));
        Assert.Equal(["Ost", "Tortillalefser"], Names(list, "checkYouHave"));
        var cheese = Item(list, "checkYouHave", "Ost");
        Assert.Equal(["200 g"], Quantities(cheese)); // what the plan needs
        Assert.Equal("300 g", AtHome(cheese));
        var tortillas = Item(list, "checkYouHave", "Tortillalefser");
        Assert.Equal(["8 stk"], Quantities(tortillas));
        Assert.Equal("some", AtHome(tortillas));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Tacos_cheese_100_g_buys_the_other_100_g_with_the_note()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);

        await PutAsync(owner.Client, household, Cheese, new { quantity = 100m, unitId = Gram });
        var list = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Ost", "Tortillalefser"], Names(list, "main"));
        var cheese = Item(list, "main", "Ost");
        Assert.Equal(["100 g"], Quantities(cheese));
        Assert.Equal("100 g", AtHome(cheese));
        Assert.Null(AtHome(Item(list, "main", "Tortillalefser")));
        Assert.Empty(Names(list, "checkYouHave"));
    }

    [Fact]
    public async Task A_different_dimension_is_have_some_check_and_regenerating_is_idempotent()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        await PutAsync(owner.Client, household, Cheese, new { quantity = 2m, unitId = Decilitre });

        var first = await GenerateAsync(owner.Client, household);
        var second = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Tortillalefser"], Names(first, "main"));
        var cheese = Item(first, "checkYouHave", "Ost");
        Assert.Equal(["200 g"], Quantities(cheese));
        Assert.Equal("2 dl", AtHome(cheese));
        Assert.Equal(first.GetProperty("checkYouHave").GetRawText(), second.GetProperty("checkYouHave").GetRawText());
        Assert.Equal(first.GetProperty("main").GetRawText(), second.GetProperty("main").GetRawText());
    }

    [Fact]
    public async Task An_expired_entry_no_longer_counts_and_the_amount_is_bought_again()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        await PutAsync(owner.Client, household, Cheese, new { quantity = 300m, unitId = Gram, expiresAt = host.Time.Now.AddDays(3) });
        Assert.Equal(["Ost"], Names(await GenerateAsync(owner.Client, household), "checkYouHave"));

        host.Time.Now = host.Time.Now.AddDays(4);
        var list = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Ost", "Tortillalefser"], Names(list, "main"));
        Assert.Equal(["200 g"], Quantities(Item(list, "main", "Ost")));
        Assert.Null(AtHome(Item(list, "main", "Ost")));
        // Not listed by default, but the pantry screen can ask for it to renew or remove it.
        using var active = await owner.Client.GetAsync(Url(household));
        Assert.Equal(0, (await active.JsonAsync()).GetArrayLength());
        using var all = await owner.Client.GetAsync(Url(household) + "?includeExpired=true");
        var rows = (await all.JsonAsync()).EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.True(rows[0].GetProperty("isExpired").GetBoolean());
    }

    [Fact]
    public async Task Removing_an_entry_buys_the_full_amount_again()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        await PutAsync(owner.Client, household, Cheese, new { quantity = 300m, unitId = Gram });
        Assert.Equal(["Ost"], Names(await GenerateAsync(owner.Client, household), "checkYouHave"));

        (await owner.Client.DeleteAsync(Url(household, $"/{Cheese}"))).Dispose();
        var list = await GenerateAsync(owner.Client, household);

        Assert.Equal(["200 g"], Quantities(Item(list, "main", "Ost")));
    }

    [Fact]
    public async Task An_entry_is_replaced_as_a_whole_and_shows_amount_unit_and_expiry()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);

        await PutAsync(owner.Client, household, Cheese, new { quantity = 300m, unitId = Gram, expiresAt = host.Time.Now.AddDays(5) });
        using var again = await owner.Client.PutAsync(Url(household, $"/{Cheese}"), new { quantity = 1.5m, unitId = Litre });
        var item = await again.JsonAsync();

        Assert.Equal(1.5m, item.GetProperty("quantity").GetDecimal());
        Assert.Equal("l", item.GetProperty("unitCode").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);
        Assert.False(item.GetProperty("isExpired").GetBoolean());
        using var list = await owner.Client.GetAsync(Url(household));
        Assert.Equal(1, (await list.JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_weekly_grocery_is_subtracted_from_what_the_household_has()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var weekly = await owner.Client.PostAsync($"/api/households/{household}/weekly-groceries", new { ingredientId = Milk, quantity = 2m, unitId = Litre });
        Assert.Equal(HttpStatusCode.Created, weekly.StatusCode);
        using var press = await owner.Client.PostAsync(ListUrl(household, "/weekly"));
        Assert.Equal(["Melk"], Names(await press.JsonAsync(), "main"));

        // The household has 1.5 l: buy the other 0.5 l, with the note.
        await PutAsync(owner.Client, household, Milk, new { quantity = 1.5m, unitId = Litre });
        var partly = await GenerateAsync(owner.Client, household);
        Assert.Equal(["500 ml"], Quantities(Item(partly, "main", "Melk")));
        Assert.Equal("1.5 l", AtHome(Item(partly, "main", "Melk")));
        Assert.True(Item(partly, "main", "Melk").GetProperty("isWeekly").GetBoolean());

        // It has 3 l: nothing is bought, but the weekly grocery is not hidden, it is checked.
        await PutAsync(owner.Client, household, Milk, new { quantity = 3m, unitId = Litre });
        var covered = await GenerateAsync(owner.Client, household);
        Assert.Empty(Names(covered, "main"));
        var milk = Item(covered, "checkYouHave", "Melk");
        Assert.Equal(["2 l"], Quantities(milk));
        Assert.Equal("3 l", AtHome(milk));
        Assert.True(milk.GetProperty("isWeekly").GetBoolean());

        // Pressing the button again changes nothing about the weekly part.
        using var pressAgain = await owner.Client.PostAsync(ListUrl(household, "/weekly"));
        var after = await pressAgain.JsonAsync();
        Assert.Equal(covered.GetProperty("checkYouHave").GetRawText(), after.GetProperty("checkYouHave").GetRawText());

        // Without an entry the full amount is on the main list again.
        (await owner.Client.DeleteAsync(Url(household, $"/{Milk}"))).Dispose();
        var gone = await GenerateAsync(owner.Client, household);
        Assert.Equal(["2 l"], Quantities(Item(gone, "main", "Melk")));
        Assert.Null(AtHome(Item(gone, "main", "Melk")));
    }

    [Fact]
    public async Task A_staple_entry_without_an_amount_still_hides_the_staple()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var recipe = await owner.Client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Salty",
            servings = 4,
            ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
        });
        var recipeId = (await recipe.JsonAsync()).GetProperty("id").GetGuid();
        (await owner.Client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.ToString("yyyy-MM-dd"), recipeId, servings = 4 })).Dispose();
        Assert.Equal(["Salt"], Names(await GenerateAsync(owner.Client, household), "checkYouHave"));

        await PutAsync(owner.Client, household, Salt, new { });

        Assert.Empty(Names(await GenerateAsync(owner.Client, household), "checkYouHave"));
    }

    [Fact]
    public async Task The_amount_is_validated_and_the_unit_must_exist()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);

        using var half = await owner.Client.PutAsync(Url(household, $"/{Cheese}"), new { quantity = 1m });
        using var halfUnit = await owner.Client.PutAsync(Url(household, $"/{Cheese}"), new { unitId = Gram });
        using var zero = await owner.Client.PutAsync(Url(household, $"/{Cheese}"), new { quantity = 0m, unitId = Gram });
        using var tooMany = await owner.Client.PutAsync(Url(household, $"/{Cheese}"), new { quantity = 1.00001m, unitId = Gram });
        using var unknownUnit = await owner.Client.PutAsync(Url(household, $"/{Cheese}"), new { quantity = 1m, unitId = Guid.NewGuid() });
        using var unknownIngredient = await owner.Client.PutAsync(Url(household, $"/{Guid.NewGuid()}"), new { quantity = 1m, unitId = Gram });

        foreach (var (response, key) in new[] { (half, "quantity"), (halfUnit, "quantity"), (zero, "quantity"), (tooMany, "quantity"), (unknownUnit, "unitId"), (unknownIngredient, "ingredientId") })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for '{key}'");
        }

        using var list = await owner.Client.GetAsync(Url(household));
        Assert.Equal(0, (await list.JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Any_member_adds_but_only_an_adult_clears_the_whole_pantry()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        var child = await host.JoinAsync(owner, household, "Child");
        var adult = await host.JoinAsync(owner, household, "Adult");
        await PutAsync(child.Client, household, Cheese, new { quantity = 300m, unitId = Gram }); // a Child may add
        await PutAsync(owner.Client, household, Tortillas, new { });

        using var childClears = await child.Client.DeleteAsync(Url(household));
        await childClears.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var stillThere = await owner.Client.GetAsync(Url(household));
        Assert.Equal(2, (await stillThere.JsonAsync()).GetArrayLength());

        using var adultClears = await adult.Client.DeleteAsync(Url(household));
        using var again = await owner.Client.DeleteAsync(Url(household));

        Assert.Equal(HttpStatusCode.NoContent, adultClears.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using var empty = await owner.Client.GetAsync(Url(household) + "?includeExpired=true");
        Assert.Equal(0, (await empty.JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Clearing_the_pantry_touches_only_this_household()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        var stranger = await host.SignInAsync();
        var strangerHousehold = await stranger.Client.CreateHouseholdAsync();
        await PutAsync(owner.Client, household, Cheese, new { quantity = 300m, unitId = Gram });
        await PutAsync(stranger.Client, strangerHousehold, Cheese, new { quantity = 1m, unitId = Gram });

        using var clear = await owner.Client.DeleteAsync(Url(household));
        using var forbidden = await stranger.Client.DeleteAsync(Url(household));

        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        await forbidden.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var theirs = await stranger.Client.GetAsync(Url(strangerHousehold));
        Assert.Equal(1, (await theirs.JsonAsync()).GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task The_export_keeps_check_you_have_items_as_names_only()
    {
        await using var host = Host();
        var (owner, household) = await TacoScenarioAsync(host);
        await PutAsync(owner.Client, household, Cheese, new { quantity = 300m, unitId = Gram });
        await GenerateAsync(owner.Client, household);

        using var export = await owner.Client.GetAsync(ListUrl(household, "/export"));
        var text = (await export.JsonAsync()).GetProperty("text").GetString()!;

        Assert.Contains("Ost", text);
        Assert.DoesNotContain("200", text.Split('\n').Single(l => l.Contains("Ost")));
    }
}

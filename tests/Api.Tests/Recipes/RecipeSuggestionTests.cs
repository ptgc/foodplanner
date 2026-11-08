using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>T-100 (Q-029): "What can I make with what we have?", cook today and add the missing items to the list.</summary>
public sealed class RecipeSuggestionTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    // Wednesday 4 November 2026 (Oslo: 11:00); the plan week starts Monday 2 November.
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static readonly Guid Cheese = Ing("ost"); // Mass
    private static readonly Guid Tortillas = Ing("tortillalefser"); // Count
    private static readonly Guid Milk = Ing("melk"); // Volume
    private static readonly Guid Egg = Ing("egg"); // Count
    private static readonly Guid Onion = Ing("loek"); // Count
    private static readonly Guid Margarine = Ing("margarin"); // staple
    private static readonly Guid Gram = Unit("g");
    private static readonly Guid Piece = Unit("stk");
    private static readonly Guid Decilitre = Unit("dl");
    private static readonly Guid Litre = Unit("l");

    private static Guid Ing(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static Guid Unit(string code) => UnitSeed.All.Single(r => r.Code == code).Id;

    private AuthHost Host()
    {
        var host = KidHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        host.Time.Now = Wednesday;
        return host;
    }

    private static string H(Guid household) => $"/api/households/{household}";

    private static string Suggest(Guid household, string query = "") => $"{H(household)}/what-can-i-make{query}";

    private static object Line(Guid ingredient, decimal? quantity, Guid? unit, Guid[]? alternatives = null) =>
        new { ingredientId = ingredient, quantity, unitId = unit, note = (string?)null, alternativeIds = alternatives };

    private static async Task<Guid> RecipeAsync(ApiClient client, Guid household, string title, int servings, params object[] lines)
    {
        using var response = await client.PostAsync($"{H(household)}/recipes", new { title, servings, ingredients = lines });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task PantryAsync(ApiClient client, Guid household, Guid ingredient, decimal? quantity = null, Guid? unit = null, DateTimeOffset? expires = null)
    {
        using var response = await client.PutAsync($"{H(household)}/pantry/{ingredient}", new { quantity, unitId = unit, expiresAt = expires });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> GetAsync(ApiClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string[] Titles(JsonElement body) =>
        [.. body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    private static JsonElement Item(JsonElement body, string title) =>
        body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("title").GetString() == title);

    private static string[] MissingNames(JsonElement item) =>
        [.. item.GetProperty("missing").EnumerateArray().Select(m => m.GetProperty("name").GetString()!)];

    private static string Quantity(JsonElement missing)
    {
        var q = missing.GetProperty("quantities").EnumerateArray().Single();
        return $"{q.GetProperty("amount").GetDecimal().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} {q.GetProperty("unitCode").GetString()}";
    }

    [Fact]
    public async Task Suggestions_are_ranked_by_missing_then_kid_likes_and_three_missing_are_left_out()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var c = s.Owner.Client;
        var tacos = await RecipeAsync(c, s.Household, "Tacos", 4, Line(Cheese, 200, Gram), Line(Tortillas, 8, Piece));
        await RecipeAsync(c, s.Household, "Ostetoast", 2, Line(Cheese, 100, Gram));
        await RecipeAsync(c, s.Household, "Omelett", 4, Line(Egg, 4, Piece), Line(Milk, 2, Decilitre), Line(Cheese, 50, Gram));
        await RecipeAsync(c, s.Household, "Gryte", 4, Line(Onion, 3, Piece), Line(Egg, 2, Piece), Line(Milk, 1, Litre), Line(Cheese, 100, Gram));
        await PantryAsync(c, s.Household, Cheese, 300, Gram);
        await PantryAsync(c, s.Household, Tortillas);

        // Mia likes Tacos (kid mode), then the parent unlocks again.
        await c.EnterKidModeAsync(s.Household, s.Profile1);
        using (var like = await c.PutAsync($"{H(s.Household)}/recipes/{tacos}/like"))
        {
            Assert.Equal(HttpStatusCode.NoContent, like.StatusCode);
        }

        using (var unlock = await c.UnlockAsync())
        {
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        }

        var body = await GetAsync(c, Suggest(s.Household));

        Assert.Equal(["Tacos", "Ostetoast", "Omelett"], Titles(body)); // Gryte misses 3 things: not shown
        Assert.Equal(Today.ToString("yyyy-MM-dd"), body.GetProperty("today").GetString());
        Assert.Equal(Monday.ToString("yyyy-MM-dd"), body.GetProperty("weekStart").GetString());
        Assert.Equal(1, Item(body, "Tacos").GetProperty("kidLikes").GetInt32());
        Assert.Empty(Item(body, "Tacos").GetProperty("missing").EnumerateArray());
        Assert.Equal("Vegetarian", Item(body, "Tacos").GetProperty("foodType").GetString());
        var omelett = Item(body, "Omelett");
        Assert.Equal(["Egg", "Melk"], MissingNames(omelett));
        Assert.Equal("4 stk", Quantity(omelett.GetProperty("missing")[0]));
        Assert.Equal("200 ml", Quantity(omelett.GetProperty("missing")[1]));
        Assert.Equal("NotAtHome", omelett.GetProperty("missing")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_pantry_amount_that_is_too_small_is_missing_by_the_shortfall_at_the_chosen_servings()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await RecipeAsync(owner.Client, household, "Tacos", 4, Line(Cheese, 200, Gram));
        await PantryAsync(owner.Client, household, Cheese, 100, Gram);

        var own = await GetAsync(owner.Client, Suggest(household));
        var missing = Item(own, "Tacos").GetProperty("missing").EnumerateArray().Single();
        Assert.Equal("Ost", missing.GetProperty("name").GetString());
        Assert.Equal("NotEnough", missing.GetProperty("reason").GetString());
        Assert.Equal("100 g", Quantity(missing));

        var eight = await GetAsync(owner.Client, Suggest(household, "?servings=8"));
        Assert.Equal(8, Item(eight, "Tacos").GetProperty("servings").GetInt32());
        Assert.Equal("300 g", Quantity(Item(eight, "Tacos").GetProperty("missing")[0]));

        var two = await GetAsync(owner.Client, Suggest(household, "?servings=2"));
        Assert.Empty(Item(two, "Tacos").GetProperty("missing").EnumerateArray());

        foreach (var bad in new[] { "0", "51", "-1" })
        {
            using var response = await owner.Client.GetAsync(Suggest(household, $"?servings={bad}"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("servings", out _));
        }
    }

    [Fact]
    public async Task A_pantry_entry_that_expires_stops_counting()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await RecipeAsync(owner.Client, household, "Ostetoast", 2, Line(Cheese, 100, Gram));
        await PantryAsync(owner.Client, household, Cheese, 300, Gram, expires: Wednesday.AddDays(1));

        Assert.Empty(Item(await GetAsync(owner.Client, Suggest(household)), "Ostetoast").GetProperty("missing").EnumerateArray());

        host.Time.Now = Wednesday.AddDays(2);

        Assert.Equal(["Ost"], MissingNames(Item(await GetAsync(owner.Client, Suggest(household)), "Ostetoast")));
    }

    [Fact]
    public async Task Staples_and_alternatives_count_as_at_home()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await RecipeAsync(owner.Client, household, "Staple only", 2, Line(Margarine, 500, Gram));
        await RecipeAsync(owner.Client, household, "With alternative", 2, Line(Egg, 2, Piece, [Tortillas]));
        await PantryAsync(owner.Client, household, Tortillas);

        var body = await GetAsync(owner.Client, Suggest(household));

        Assert.Equal(["Staple only", "With alternative"], Titles(body));
        Assert.Empty(Item(body, "With alternative").GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public async Task Cook_today_adds_the_recipe_once_next_to_the_meals_the_day_has_and_the_suggestion_is_marked()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var toast = await RecipeAsync(owner.Client, household, "Ostetoast", 2, Line(Cheese, 100, Gram));
        var soup = await RecipeAsync(owner.Client, household, "Suppe", 4, Line(Milk, 5, Decilitre));
        await PantryAsync(owner.Client, household, Cheese);
        using (var existing = await owner.Client.PostAsync(
            $"{H(household)}/meal-plans/{Monday:yyyy-MM-dd}/entries", new { date = Today.ToString("yyyy-MM-dd"), recipeId = soup, servings = 3 }))
        {
            Assert.Equal(HttpStatusCode.Created, existing.StatusCode);
        }

        using var first = await owner.Client.PostAsync(Suggest(household, $"/{toast}/cook-today"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.JsonAsync();
        Assert.False(created.GetProperty("alreadyPlanned").GetBoolean());
        Assert.Equal(Monday.ToString("yyyy-MM-dd"), created.GetProperty("weekStart").GetString());
        var entry = created.GetProperty("entry");
        Assert.Equal(Today.ToString("yyyy-MM-dd"), entry.GetProperty("date").GetString());
        Assert.Equal(2, entry.GetProperty("servings").GetInt32()); // the recipe's own servings

        using var again = await owner.Client.PostAsync(Suggest(household, $"/{toast}/cook-today"), new { servings = 6 });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var second = await again.JsonAsync();
        Assert.True(second.GetProperty("alreadyPlanned").GetBoolean());
        Assert.Equal(entry.GetProperty("id").GetGuid(), second.GetProperty("entry").GetProperty("id").GetGuid());
        Assert.Equal(2, second.GetProperty("entry").GetProperty("servings").GetInt32()); // nothing changed

        var plan = await GetAsync(owner.Client, $"{H(household)}/meal-plans/{Monday:yyyy-MM-dd}");
        Assert.Equal(["Ostetoast", "Suppe"], plan.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("recipeTitle").GetString()!).Order().ToArray());

        var body = await GetAsync(owner.Client, Suggest(household));
        var suggestion = Item(body, "Ostetoast");
        Assert.True(suggestion.GetProperty("plannedToday").GetBoolean());
        Assert.True(suggestion.GetProperty("cookedRecently").GetBoolean());
    }

    [Fact]
    public async Task Cook_today_with_servings_uses_them_and_rejects_bad_ones_and_unknown_recipes()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var toast = await RecipeAsync(owner.Client, household, "Ostetoast", 2, Line(Cheese, 100, Gram));

        using var bad = await owner.Client.PostAsync(Suggest(household, $"/{toast}/cook-today"), new { servings = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var unknown = await owner.Client.PostAsync(Suggest(household, $"/{Guid.NewGuid()}/cook-today"));
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");

        using var ok = await owner.Client.PostAsync(Suggest(household, $"/{toast}/cook-today"), new { servings = 5 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(5, (await ok.JsonAsync()).GetProperty("entry").GetProperty("servings").GetInt32());
    }

    [Fact]
    public async Task Kid_mode_and_a_child_cannot_cook_or_add_and_kid_mode_cannot_read()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var child = await host.JoinAsync(s.Owner, s.Household, "Child");
        var adult = await host.JoinAsync(s.Owner, s.Household, "Adult");
        var toast = await RecipeAsync(s.Owner.Client, s.Household, "Ostetoast", 2, Line(Cheese, 100, Gram));

        // A Child member may read (outside kid mode) but not act.
        Assert.Equal(["Ostetoast"], Titles(await GetAsync(child.Client, Suggest(s.Household))));
        using (var cook = await child.Client.PostAsync(Suggest(s.Household, $"/{toast}/cook-today")))
        using (var add = await child.Client.PostAsync(Suggest(s.Household, $"/{toast}/add-missing")))
        {
            await cook.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            await add.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // An Adult may act.
        using (var cook = await adult.Client.PostAsync(Suggest(s.Household, $"/{toast}/cook-today")))
        {
            Assert.Equal(HttpStatusCode.Created, cook.StatusCode);
        }

        // Kid mode: nothing, with the kid-mode problem; the plan is unchanged.
        var other = await RecipeAsync(s.Owner.Client, s.Household, "Annet", 2, Line(Cheese, 100, Gram));
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using (var cook = await s.Owner.Client.PostAsync(Suggest(s.Household, $"/{other}/cook-today")))
        using (var add = await s.Owner.Client.PostAsync(Suggest(s.Household, $"/{toast}/add-missing")))
        using (var read = await s.Owner.Client.GetAsync(Suggest(s.Household)))
        {
            await cook.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
            await add.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
            await read.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        }

        using var unlock = await s.Owner.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        var plan = await GetAsync(s.Owner.Client, $"{H(s.Household)}/meal-plans/{Monday:yyyy-MM-dd}");
        Assert.Equal(["Ostetoast"], plan.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("recipeTitle").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_non_member_is_refused_everywhere()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var toast = await RecipeAsync(owner.Client, household, "Ostetoast", 2, Line(Cheese, 100, Gram));
        var outsider = await host.SignInAsync();

        using var read = await outsider.Client.GetAsync(Suggest(household));
        using var cook = await outsider.Client.PostAsync(Suggest(household, $"/{toast}/cook-today"));
        using var add = await outsider.Client.PostAsync(Suggest(household, $"/{toast}/add-missing"));
        await read.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await cook.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await add.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Add_missing_puts_the_missing_amount_on_the_list_merged_with_what_the_week_already_needs()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var tacos = await RecipeAsync(owner.Client, household, "Tacos", 4, Line(Cheese, 200, Gram), Line(Tortillas, 8, Piece));
        var toast = await RecipeAsync(owner.Client, household, "Ostetoast", 2, Line(Cheese, 100, Gram));
        await PantryAsync(owner.Client, household, Cheese, 100, Gram);
        await PantryAsync(owner.Client, household, Tortillas);
        var listUrl = $"{H(household)}/shopping-lists/{Monday:yyyy-MM-dd}";

        // Ostetoast is already planned on Friday and the list generated: cheese 100 g, minus 100 g at home = nothing to buy.
        using (var friday = await owner.Client.PostAsync(
            $"{H(household)}/meal-plans/{Monday:yyyy-MM-dd}/entries", new { date = "2026-11-06", recipeId = toast, servings = 2 }))
        {
            Assert.Equal(HttpStatusCode.Created, friday.StatusCode);
        }

        // Not planned yet: the list is made from the plan.
        using (var notPlanned = await owner.Client.PostAsync(Suggest(household, $"/{tacos}/add-missing")))
        {
            await notPlanned.AssertProblemAsync(HttpStatusCode.Conflict, "recipe-not-planned");
        }

        using (var cook = await owner.Client.PostAsync(Suggest(household, $"/{tacos}/cook-today")))
        {
            Assert.Equal(HttpStatusCode.Created, cook.StatusCode);
        }

        using var add = await owner.Client.PostAsync(Suggest(household, $"/{tacos}/add-missing"));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var body = await add.JsonAsync();
        Assert.Equal(Monday.ToString("yyyy-MM-dd"), body.GetProperty("weekStart").GetString());
        var missing = body.GetProperty("missing").EnumerateArray().Single();
        Assert.Equal("Ost", missing.GetProperty("name").GetString());
        Assert.Equal("100 g", Quantity(missing));

        // The list: tacos 200 g + toast 100 g = 300 g, 100 g at home: buy 200 g. Tortillas ("some" at home) are only checked.
        var list = await GetAsync(owner.Client, listUrl);
        var cheese = list.GetProperty("main").EnumerateArray().Single();
        Assert.Equal("Ost", cheese.GetProperty("name").GetString());
        Assert.Equal(200m, cheese.GetProperty("quantities")[0].GetProperty("amount").GetDecimal());
        Assert.Equal(["Tortillalefser"], list.GetProperty("checkYouHave").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());

        // Pressing again changes nothing.
        using var again = await owner.Client.PostAsync(Suggest(household, $"/{tacos}/add-missing"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(200m, (await GetAsync(owner.Client, listUrl)).GetProperty("main")[0].GetProperty("quantities")[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Add_missing_with_nothing_missing_creates_no_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var toast = await RecipeAsync(owner.Client, household, "Ostetoast", 2, Line(Cheese, 100, Gram));
        await PantryAsync(owner.Client, household, Cheese);
        using (var cook = await owner.Client.PostAsync(Suggest(household, $"/{toast}/cook-today")))
        {
            Assert.Equal(HttpStatusCode.Created, cook.StatusCode);
        }

        using var add = await owner.Client.PostAsync(Suggest(household, $"/{toast}/add-missing"));

        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Empty((await add.JsonAsync()).GetProperty("missing").EnumerateArray());
        var list = await GetAsync(owner.Client, $"{H(household)}/shopping-lists/{Monday:yyyy-MM-dd}");
        Assert.Equal(JsonValueKind.Null, list.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Another_household_never_influences_or_leaks_into_the_suggestions_or_actions()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync("A");
        var householdB = await b.Client.CreateHouseholdAsync("B");
        await RecipeAsync(a.Client, householdA, "A-Tacos", 4, Line(Cheese, 200, Gram));
        var bRecipe = await RecipeAsync(b.Client, householdB, "B-Secret", 4, Line(Cheese, 200, Gram));
        await RecipeAsync(b.Client, householdB, "B-Tacos", 4, Line(Cheese, 200, Gram));
        await PantryAsync(b.Client, householdB, Cheese); // B has cheese; A does not

        var forA = await GetAsync(a.Client, Suggest(householdA));
        Assert.Equal(["A-Tacos"], Titles(forA));
        Assert.Equal(["Ost"], MissingNames(Item(forA, "A-Tacos"))); // B's pantry does not satisfy A
        var forB = await GetAsync(b.Client, Suggest(householdB));
        Assert.Equal(["B-Secret", "B-Tacos"], Titles(forB));
        Assert.Empty(Item(forB, "B-Tacos").GetProperty("missing").EnumerateArray());

        // B's recipe id through A's household is simply not found, and nothing is planned.
        using var cook = await a.Client.PostAsync(Suggest(householdA, $"/{bRecipe}/cook-today"));
        using var add = await a.Client.PostAsync(Suggest(householdA, $"/{bRecipe}/add-missing"));
        await cook.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        await add.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        var plan = await GetAsync(a.Client, $"{H(householdA)}/meal-plans/{Monday:yyyy-MM-dd}");
        Assert.Empty(plan.GetProperty("entries").EnumerateArray());

        // A cannot reach B's route at all.
        using var foreign = await a.Client.GetAsync(Suggest(householdB));
        await foreign.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }
}

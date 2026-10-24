using System.Net;
using System.Text.Json;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.ShoppingLists;

public sealed class ShoppingListEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Rice = Ingredient("ris");
    private static readonly Guid Egg = Ingredient("egg");
    private static readonly Guid Milk = Ingredient("melk");
    private static readonly Guid Salt = Ingredient("salt"); // staple
    private static readonly Guid Flour = Ingredient("hvetemel"); // staple
    private static readonly Guid Gram = Unit("g");
    private static readonly Guid Kilogram = Unit("kg");
    private static readonly Guid Pieces = Unit("stk");
    private static readonly Guid Deciliter = Unit("dl");

    private static Guid Ingredient(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static Guid Unit(string code) => UnitSeed.All.Single(r => r.Code == code).Id;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private static string ListUrl(Guid household, DateOnly week, string rest = "") =>
        $"/api/households/{household}/shopping-lists/{week:yyyy-MM-dd}{rest}";

    private sealed record Line(Guid Ingredient, decimal? Quantity = null, Guid? Unit = null);

    private static async Task<Guid> CreateRecipeAsync(ApiClient client, Guid household, string title, int servings, params Line[] lines)
    {
        using var response = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title,
            servings,
            ingredients = lines.Select(l => new { ingredientId = l.Ingredient, quantity = l.Quantity, unitId = l.Unit, note = (string?)null }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PlanAsync(ApiClient client, Guid household, Guid recipe, int servings, DateOnly? date = null)
    {
        using var response = await client.PostAsync(
            $"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = (date ?? Monday).ToString("yyyy-MM-dd"), recipeId = recipe, servings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GenerateAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(ListUrl(household, Monday, "/generate"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> GetAsync(ApiClient client, Guid household)
    {
        using var response = await client.GetAsync(ListUrl(household, Monday));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> AddManualAsync(ApiClient client, Guid household, string label, decimal? quantity = null, Guid? unit = null)
    {
        using var response = await client.PostAsync(ListUrl(household, Monday, "/items"), new { label, quantity, unitId = unit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static Task<HttpResponseMessage> CheckAsync(ApiClient client, Guid household, Guid item, bool isChecked) =>
        client.PutAsync(ListUrl(household, Monday, $"/items/{item}/check"), new { @checked = isChecked });

    private static JsonElement Find(JsonElement list, string section, string name) =>
        list.GetProperty(section).EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);

    private static string[] Names(JsonElement list, string section) =>
        [.. list.GetProperty(section).EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    private static string Quantities(JsonElement item) =>
        string.Join(
            " + ",
            item.GetProperty("quantities").EnumerateArray().Select(q =>
                q.GetProperty("amount").ValueKind == JsonValueKind.Null ? "to taste" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{q.GetProperty("amount").GetDecimal():0.####} {q.GetProperty("unitCode").GetString()}")));

    private async Task<int> CountAsync(string table, string where, params (string Name, object Value)[] parameters) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {where}", parameters))!;

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    /// <summary>A household with two recipes that share rice, plus staples and an egg, both planned for the week.</summary>
    private async Task<(SignedInUser Owner, Guid Household, Guid Pasta, Guid Pudding, Guid PastaEntry, Guid PuddingEntry)> ScenarioAsync(AuthHost host)
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var first = await CreateRecipeAsync(owner.Client, household, "Rice and egg", 4,
            new Line(Rice, 200, Gram), new Line(Egg, 2, Pieces), new Line(Salt, 1, Gram));
        var second = await CreateRecipeAsync(owner.Client, household, "Rice pudding", 4,
            new Line(Rice, 0.3m, Kilogram), new Line(Milk, 5, Deciliter), new Line(Salt), new Line(Flour, 100, Gram));
        var firstEntry = await PlanAsync(owner.Client, household, first, 4);
        var secondEntry = await PlanAsync(owner.Client, household, second, 4, Monday.AddDays(1));
        return (owner, household, first, second, firstEntry, secondEntry);
    }

    [Fact]
    public async Task Reading_a_week_without_a_list_returns_an_empty_shape_and_writes_nothing()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var list = await GetAsync(owner.Client, household);

        Assert.Equal(JsonValueKind.Null, list.GetProperty("id").ValueKind);
        Assert.Equal("2026-10-26", list.GetProperty("weekStart").GetString());
        Assert.Equal(0, list.GetProperty("main").GetArrayLength());
        Assert.Equal(0, list.GetProperty("checkYouHave").GetArrayLength());
        Assert.Equal(0, await CountAsync("ShoppingLists", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task A_week_start_that_is_not_a_Monday_is_a_validation_problem_on_every_route()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var tuesday = new DateOnly(2026, 10, 27);
        var item = Guid.NewGuid();

        var responses = new[]
        {
            await owner.Client.GetAsync(ListUrl(household, tuesday)),
            await owner.Client.PostAsync(ListUrl(household, tuesday, "/generate")),
            await owner.Client.PostAsync(ListUrl(household, tuesday, "/items"), new { label = "x" }),
            await owner.Client.DeleteAsync(ListUrl(household, tuesday, $"/items/{item}")),
            await owner.Client.PutAsync(ListUrl(household, tuesday, $"/items/{item}/check"), new { @checked = true }),
            await owner.Client.GetAsync($"/api/households/{household}/shopping-lists/not-a-date"),
        };
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("weekStart", out _));
            }
        }

        Assert.Equal(0, await CountAsync("ShoppingLists", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Generate_merges_quantities_across_recipes_and_puts_staples_only_in_check_you_have()
    {
        await using var host = Host();
        var (owner, household, pasta, pudding, _, _) = await ScenarioAsync(host);

        var list = await GenerateAsync(owner.Client, household);

        // 200 g + 0.3 kg = 500 g, from both recipes.
        var rice = Find(list, "main", "Ris");
        Assert.Equal("500 g", Quantities(rice));
        Assert.Equal(["Rice and egg", "Rice pudding"], rice.GetProperty("recipes").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        Assert.Equal(new[] { pasta, pudding }.Order(), rice.GetProperty("recipes").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Order());
        Assert.Equal("2 stk", Quantities(Find(list, "main", "Egg")));
        Assert.Equal("500 ml", Quantities(Find(list, "main", "Melk")));

        // Staples (even with a quantity) are never in the main section, and have no quantity.
        Assert.Equal(["Egg", "Melk", "Ris"], Names(list, "main"));
        Assert.Equal(["Hvetemel", "Salt"], Names(list, "checkYouHave"));
        Assert.All(list.GetProperty("checkYouHave").EnumerateArray(), s => Assert.Equal(0, s.GetProperty("quantities").GetArrayLength()));
        Assert.All(list.GetProperty("checkYouHave").EnumerateArray(), s => Assert.Equal("CheckYouHave", s.GetProperty("section").GetString()));
        Assert.Equal(2, Find(list, "checkYouHave", "Salt").GetProperty("recipes").GetArrayLength());

        // Structured storage: generated items carry an ingredient and no label.
        Assert.Equal(0, await CountAsync("ShoppingListItems", "HouseholdId = @h AND Label IS NOT NULL", ("@h", household)));
        Assert.Equal(0, await CountAsync("ShoppingListItems", "HouseholdId = @h AND IngredientId IS NULL", ("@h", household)));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Generate_scales_by_the_planned_servings()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household, "Scaled", 4, new Line(Rice, 200, Gram));
        await PlanAsync(owner.Client, household, recipe, 6);

        Assert.Equal("300 g", Quantities(Find(await GenerateAsync(owner.Client, household), "main", "Ris")));
    }

    [Fact]
    public async Task Generate_for_a_week_without_a_plan_creates_an_empty_list()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        var list = await GenerateAsync(owner.Client, household);

        Assert.NotEqual(JsonValueKind.Null, list.GetProperty("id").ValueKind);
        Assert.Equal(0, list.GetProperty("main").GetArrayLength());
        Assert.Equal(1, await CountAsync("ShoppingLists", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Regeneration_keeps_checked_items_and_is_idempotent()
    {
        await using var host = Host();
        var (owner, household, _, _, _, _) = await ScenarioAsync(host);
        var first = await GenerateAsync(owner.Client, household);
        var rice = Find(first, "main", "Ris").GetProperty("id").GetGuid();
        var salt = Find(first, "checkYouHave", "Salt").GetProperty("id").GetGuid();
        using (var checkRice = await CheckAsync(owner.Client, household, rice, true))
        {
            Assert.Equal(HttpStatusCode.OK, checkRice.StatusCode);
        }

        using (var checkSalt = await CheckAsync(owner.Client, household, salt, true))
        {
            Assert.Equal(HttpStatusCode.OK, checkSalt.StatusCode);
        }

        var before = await GetAsync(owner.Client, household);
        var again = await GenerateAsync(owner.Client, household);
        var thrice = await GenerateAsync(owner.Client, household);

        // Same ids, same checked state, same checker and time, no flags, no duplicates.
        Assert.Equal(before.GetRawText(), again.GetRawText());
        Assert.Equal(before.GetRawText(), thrice.GetRawText());
        var kept = Find(again, "main", "Ris");
        Assert.Equal(rice, kept.GetProperty("id").GetGuid());
        Assert.True(kept.GetProperty("checked").GetBoolean());
        Assert.Equal(owner.User.Id, kept.GetProperty("checkedByUserId").GetGuid());
        Assert.Equal(owner.User.Email, kept.GetProperty("checkedByEmail").GetString());
        Assert.False(kept.GetProperty("quantityChangedSinceChecked").GetBoolean());
        Assert.True(Find(again, "checkYouHave", "Salt").GetProperty("checked").GetBoolean());
        Assert.False(Find(again, "main", "Egg").GetProperty("checked").GetBoolean());
        Assert.Equal(5, await CountAsync("ShoppingListItems", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task A_checked_item_whose_quantity_grows_stays_checked_and_is_flagged_until_checked_again()
    {
        await using var host = Host();
        var (owner, household, pasta, _, _, _) = await ScenarioAsync(host);
        var list = await GenerateAsync(owner.Client, household);
        var rice = Find(list, "main", "Ris").GetProperty("id").GetGuid();
        var egg = Find(list, "main", "Egg").GetProperty("id").GetGuid();
        (await CheckAsync(owner.Client, household, rice, true)).Dispose();

        await PlanAsync(owner.Client, household, pasta, 4, Monday.AddDays(2)); // another 200 g rice and 2 eggs
        var regenerated = await GenerateAsync(owner.Client, household);

        var changed = Find(regenerated, "main", "Ris");
        Assert.Equal(rice, changed.GetProperty("id").GetGuid());
        Assert.Equal("700 g", Quantities(changed));
        Assert.True(changed.GetProperty("checked").GetBoolean());
        Assert.True(changed.GetProperty("quantityChangedSinceChecked").GetBoolean());
        Assert.Equal(owner.User.Id, changed.GetProperty("checkedByUserId").GetGuid());

        // An unchecked item that changed is just changed.
        var unchecked_ = Find(regenerated, "main", "Egg");
        Assert.Equal(egg, unchecked_.GetProperty("id").GetGuid());
        Assert.Equal("4 stk", Quantities(unchecked_));
        Assert.False(unchecked_.GetProperty("quantityChangedSinceChecked").GetBoolean());

        // The flag survives another regeneration with no further change, and checking again acknowledges it.
        Assert.True(Find(await GenerateAsync(owner.Client, household), "main", "Ris").GetProperty("quantityChangedSinceChecked").GetBoolean());
        using var acknowledge = await CheckAsync(owner.Client, household, rice, true);
        var acknowledged = await acknowledge.JsonAsync();
        Assert.True(acknowledged.GetProperty("checked").GetBoolean());
        Assert.False(acknowledged.GetProperty("quantityChangedSinceChecked").GetBoolean());
        Assert.Equal(owner.User.Id, acknowledged.GetProperty("checkedByUserId").GetGuid());
    }

    [Fact]
    public async Task Removing_a_plan_entry_removes_the_generated_items_only_it_needed_and_manual_items_survive()
    {
        await using var host = Host();
        var (owner, household, _, _, _, puddingEntry) = await ScenarioAsync(host);
        var list = await GenerateAsync(owner.Client, household);
        var milk = Find(list, "main", "Melk").GetProperty("id").GetGuid();
        var rice = Find(list, "main", "Ris").GetProperty("id").GetGuid();
        (await CheckAsync(owner.Client, household, milk, true)).Dispose(); // checked items go too: they are no longer needed
        var manual = await AddManualAsync(owner.Client, household, "Birthday candles", 1, Pieces);
        (await CheckAsync(owner.Client, household, manual.GetProperty("id").GetGuid(), true)).Dispose();

        using (var delete = await owner.Client.DeleteAsync($"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries/{puddingEntry}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        // Nothing changes until the list is regenerated: a read never regenerates.
        Assert.Contains("Melk", Names(await GetAsync(owner.Client, household), "main"));

        var regenerated = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Birthday candles", "Egg", "Ris"], Names(regenerated, "main"));
        Assert.Equal(["Salt"], Names(regenerated, "checkYouHave")); // flour only came from the removed recipe
        Assert.Equal("200 g", Quantities(Find(regenerated, "main", "Ris")));
        Assert.Equal(rice, Find(regenerated, "main", "Ris").GetProperty("id").GetGuid());
        var survivor = Find(regenerated, "main", "Birthday candles");
        Assert.True(survivor.GetProperty("isManual").GetBoolean());
        Assert.True(survivor.GetProperty("checked").GetBoolean());
        Assert.Equal("1 stk", Quantities(survivor));
        Assert.Equal(1, Find(regenerated, "main", "Ris").GetProperty("recipes").GetArrayLength());

        // The database rows of the removed items are gone with them.
        Assert.Equal(0, await CountAsync("ShoppingListItems", "Id = @i", ("@i", milk)));
        Assert.Equal(0, await CountAsync("ShoppingListItemQuantities", "ShoppingListItemId = @i", ("@i", milk)));
        Assert.Equal(0, await CountAsync("ShoppingListItemSources", "ShoppingListItemId = @i", ("@i", milk)));
    }

    [Fact]
    public async Task Emptying_the_plan_and_regenerating_leaves_only_manual_items()
    {
        await using var host = Host();
        var (owner, household, _, _, firstEntry, secondEntry) = await ScenarioAsync(host);
        await GenerateAsync(owner.Client, household);
        await AddManualAsync(owner.Client, household, "Paper towels");
        foreach (var entry in new[] { firstEntry, secondEntry })
        {
            (await owner.Client.DeleteAsync($"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries/{entry}")).Dispose();
        }

        var list = await GenerateAsync(owner.Client, household);

        Assert.Equal(["Paper towels"], Names(list, "main"));
        Assert.Equal(0, list.GetProperty("checkYouHave").GetArrayLength());
    }

    [Fact]
    public async Task A_staple_becoming_a_regular_item_moves_section_and_starts_unchecked()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household, "Flour", 2, new Line(Flour, 100, Gram));
        await PlanAsync(owner.Client, household, recipe, 2);
        var staple = Find(await GenerateAsync(owner.Client, household), "checkYouHave", "Hvetemel").GetProperty("id").GetGuid();
        (await CheckAsync(owner.Client, household, staple, true)).Dispose();

        // The seeded flour is global and cannot change; flip the stored section to stand in for a staple flag change,
        // then regenerate: the generator says staple again, the item moves back and starts over.
        await HouseholdScenario.ScalarAsync(db.ConnectionString, "UPDATE ShoppingListItems SET Section = 'Main', Checked = 1 WHERE Id = @i", ("@i", staple));
        var list = await GenerateAsync(owner.Client, household);

        var moved = Find(list, "checkYouHave", "Hvetemel");
        Assert.Equal(staple, moved.GetProperty("id").GetGuid());
        Assert.False(moved.GetProperty("checked").GetBoolean());
        Assert.Equal(0, list.GetProperty("main").GetArrayLength());
    }

    [Fact]
    public async Task Manual_items_validate_label_and_quantity_and_unit_and_are_added_by_any_member()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");

        var item = await AddManualAsync(child.Client, household, "  Birthday   candles ", 2.5m, Pieces);

        Assert.Equal("Birthday candles", item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("isManual").GetBoolean());
        Assert.Equal("Main", item.GetProperty("section").GetString());
        Assert.Equal("2.5 stk", Quantities(item));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("ingredientId").ValueKind);
        Assert.False(item.GetProperty("checked").GetBoolean());
        Assert.Equal(0, item.GetProperty("recipes").GetArrayLength());

        var without = await AddManualAsync(owner.Client, household, "Napkins");
        Assert.Equal(0, without.GetProperty("quantities").GetArrayLength());

        var invalid = new (object Body, string Key)[]
        {
            (new { label = "" }, "label"),
            (new { label = "   " }, "label"),
            (new { label = new string('x', 101) }, "label"),
            (new { label = "bell\u0007here" }, "label"),
            (new { label = "x", quantity = 1m }, "quantity"),
            (new { label = "x", unitId = Pieces }, "quantity"),
            (new { label = "x", quantity = 0m, unitId = Pieces }, "quantity"),
            (new { label = "x", quantity = -1m, unitId = Pieces }, "quantity"),
            (new { label = "x", quantity = 100001m, unitId = Pieces }, "quantity"),
            (new { label = "x", quantity = 1.23456m, unitId = Pieces }, "quantity"),
            (new { label = "x", quantity = 1m, unitId = Guid.NewGuid() }, "unitId"),
        };
        foreach (var (body, key) in invalid)
        {
            using var response = await owner.Client.PostAsync(ListUrl(household, Monday, "/items"), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error on '{key}' for {JsonSerializer.Serialize(body)}");
        }

        using var noBody = await owner.Client.PostAsync(ListUrl(household, Monday, "/items"), null);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(2, await CountAsync("ShoppingListItems", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Any_member_removes_a_manual_item_but_a_generated_item_cannot_be_removed()
    {
        await using var host = Host();
        var (owner, household, _, _, _, _) = await ScenarioAsync(host);
        var child = await host.JoinAsync(owner, household, "Child");
        var list = await GenerateAsync(owner.Client, household);
        var manual = (await AddManualAsync(owner.Client, household, "Candles")).GetProperty("id").GetGuid();
        var generated = Find(list, "main", "Ris").GetProperty("id").GetGuid();

        using (var refused = await child.Client.DeleteAsync(ListUrl(household, Monday, $"/items/{generated}")))
        {
            await refused.AssertProblemAsync(HttpStatusCode.Conflict, "shopping-list-item-generated");
        }

        Assert.Equal(1, await CountAsync("ShoppingListItems", "Id = @i", ("@i", generated)));

        using (var removed = await child.Client.DeleteAsync(ListUrl(household, Monday, $"/items/{manual}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(0, await CountAsync("ShoppingListItems", "Id = @i", ("@i", manual)));
        using var again = await child.Client.DeleteAsync(ListUrl(household, Monday, $"/items/{manual}"));
        await again.AssertProblemAsync(HttpStatusCode.NotFound, "shopping-list-item-not-found");
    }

    [Fact]
    public async Task Owner_adult_and_child_check_off_and_see_who_did()
    {
        await using var host = Host();
        var (owner, household, _, _, _, _) = await ScenarioAsync(host);
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var list = await GenerateAsync(adult.Client, household); // any member may generate
        var rice = Find(list, "main", "Ris").GetProperty("id").GetGuid();
        var egg = Find(list, "main", "Egg").GetProperty("id").GetGuid();

        using var byChild = await CheckAsync(child.Client, household, rice, true);
        Assert.Equal(HttpStatusCode.OK, byChild.StatusCode);
        var checkedByChild = await byChild.JsonAsync();
        Assert.True(checkedByChild.GetProperty("checked").GetBoolean());
        Assert.Equal(child.User.Id, checkedByChild.GetProperty("checkedByUserId").GetGuid());
        Assert.Equal(child.User.Email, checkedByChild.GetProperty("checkedByEmail").GetString());
        Assert.NotEqual(JsonValueKind.Null, checkedByChild.GetProperty("checkedAt").ValueKind);

        // Checking again by someone else keeps the first checker (set semantics, no flip).
        using var byAdult = await CheckAsync(adult.Client, household, rice, true);
        Assert.Equal(child.User.Id, (await byAdult.JsonAsync()).GetProperty("checkedByUserId").GetGuid());

        // Unchecking clears checker and time.
        using var unchecked_ = await CheckAsync(owner.Client, household, rice, false);
        var cleared = await unchecked_.JsonAsync();
        Assert.False(cleared.GetProperty("checked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("checkedByUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("checkedAt").ValueKind);

        using var eggByChild = await CheckAsync(child.Client, household, egg, true);
        Assert.Equal(HttpStatusCode.OK, eggByChild.StatusCode);

        // The body is required and strict.
        using var missing = await owner.Client.PutAsync(ListUrl(household, Monday, $"/items/{egg}/check"), new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await missing.JsonAsync()).GetProperty("errors").TryGetProperty("checked", out _));
        using var unknown = await owner.Client.PutAsync(ListUrl(household, Monday, $"/items/{Guid.NewGuid()}/check"), new { @checked = true });
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "shopping-list-item-not-found");
        using var otherWeek = await owner.Client.PutAsync(ListUrl(household, Monday.AddDays(7), $"/items/{egg}/check"), new { @checked = false });
        await otherWeek.AssertProblemAsync(HttpStatusCode.NotFound, "shopping-list-item-not-found");
        Assert.True(Find(await GetAsync(owner.Client, household), "main", "Egg").GetProperty("checked").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_check_offs_of_the_same_value_end_with_one_checker_and_no_errors()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        SignedInUser[] members = [owner, adult, child];

        for (var round = 0; round < 8; round++)
        {
            var item = (await AddManualAsync(owner.Client, household, "Round " + round)).GetProperty("id").GetGuid();

            // Two requests per member, all at once.
            var responses = await Task.WhenAll(members.Concat(members).Select(async m =>
            {
                using var response = await CheckAsync(m.Client, household, item, true);
                return (Status: response.StatusCode, Body: await response.JsonAsync());
            }));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));
            Assert.All(responses, r => Assert.True(r.Body.GetProperty("checked").GetBoolean()));
            var winners = responses.Select(r => r.Body.GetProperty("checkedByUserId").GetGuid()).Distinct().ToList();
            var winner = Assert.Single(winners); // every response names the same checker
            Assert.Contains(winner, members.Select(m => m.User.Id));

            var stored = Find(await GetAsync(owner.Client, household), "main", "Round " + round);
            Assert.True(stored.GetProperty("checked").GetBoolean());
            Assert.Equal(winner, stored.GetProperty("checkedByUserId").GetGuid());
            Assert.Equal(1, await CountAsync("ShoppingListItems", "Id = @i AND Checked = 1 AND CheckedByUserId = @u AND CheckedAt IS NOT NULL", ("@i", item), ("@u", winner)));
        }
    }

    [Fact]
    public async Task Concurrent_check_and_uncheck_end_in_one_writers_state_never_a_mix()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var checkers = new[] { owner.User.Id, adult.User.Id };

        for (var round = 0; round < 8; round++)
        {
            var item = (await AddManualAsync(owner.Client, household, "Mixed " + round)).GetProperty("id").GetGuid();
            (await CheckAsync(child.Client, household, item, true)).Dispose(); // starts checked, by the child

            var responses = await Task.WhenAll(
                Request(owner, true), Request(adult, true), Request(child, false), Request(child, false), Request(owner, false));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));
            var stored = Find(await GetAsync(owner.Client, household), "main", "Mixed " + round);
            if (stored.GetProperty("checked").GetBoolean())
            {
                // Checked: by one of the members who asked for it, with a time.
                Assert.Contains(stored.GetProperty("checkedByUserId").GetGuid(), checkers);
                Assert.NotEqual(JsonValueKind.Null, stored.GetProperty("checkedAt").ValueKind);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, stored.GetProperty("checkedByUserId").ValueKind);
                Assert.Equal(JsonValueKind.Null, stored.GetProperty("checkedAt").ValueKind);
            }

            // Whatever the order was, repeating a request now is a no-op that reports the stored state.
            using var repeat = await CheckAsync(owner.Client, household, item, stored.GetProperty("checked").GetBoolean());
            Assert.Equal(stored.GetProperty("checkedByUserId").ToString(), (await repeat.JsonAsync()).GetProperty("checkedByUserId").ToString());

            async Task<(HttpStatusCode Status, JsonElement Body)> Request(SignedInUser member, bool value)
            {
                using var response = await CheckAsync(member.Client, household, item, value);
                return (response.StatusCode, await response.JsonAsync());
            }
        }
    }

    [Fact]
    public async Task Concurrent_regenerations_and_check_offs_leave_one_consistent_list()
    {
        await using var host = Host();
        var (owner, household, _, _, _, _) = await ScenarioAsync(host);
        var adult = await host.JoinAsync(owner, household, "Adult");
        // Four requests generate a list that does not exist yet, all at once: one list, one item per ingredient.
        var lists = await Task.WhenAll(
            GenerateAsync(owner.Client, household), GenerateAsync(adult.Client, household),
            GenerateAsync(owner.Client, household), GenerateAsync(adult.Client, household));
        Assert.Single(lists.Select(l => l.GetProperty("id").GetGuid()).Distinct());
        var rice = Find(lists[0], "main", "Ris").GetProperty("id").GetGuid();

        for (var round = 0; round < 4; round++)
        {
            var results = await Task.WhenAll(
                GenerateAsync(owner.Client, household), GenerateAsync(adult.Client, household), GenerateAsync(owner.Client, household),
                Task.Run(async () =>
                {
                    using var response = await CheckAsync(adult.Client, household, rice, round % 2 == 0);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    return await response.JsonAsync();
                }));
            Assert.NotNull(results);
        }

        var final = await GetAsync(owner.Client, household);
        Assert.Equal(["Egg", "Melk", "Ris"], Names(final, "main"));
        Assert.Equal(["Hvetemel", "Salt"], Names(final, "checkYouHave"));
        Assert.Equal(5, await CountAsync("ShoppingListItems", "HouseholdId = @h", ("@h", household)));
        Assert.Equal(1, await CountAsync("ShoppingLists", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Items_are_sorted_by_a_Norwegian_comparison_where_ae_oe_aa_come_after_z()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        foreach (var label in new[] { "Ål", "Zebra", "Øl", "Æbleskiver", "Appelsin" })
        {
            await AddManualAsync(owner.Client, household, label);
        }

        // nb-NO: ... z, æ, ø, å (ordinal would put å before æ and ø).
        Assert.Equal(["Appelsin", "Zebra", "Æbleskiver", "Øl", "Ål"], Names(await GetAsync(owner.Client, household), "main"));
    }

    [Fact]
    public async Task Deleting_a_recipe_that_a_stale_list_still_names_works_once_it_is_off_the_plan()
    {
        await using var host = Host();
        var (owner, household, _, pudding, _, puddingEntry) = await ScenarioAsync(host);
        await GenerateAsync(owner.Client, household);
        (await owner.Client.DeleteAsync($"/api/households/{household}/meal-plans/{Monday:yyyy-MM-dd}/entries/{puddingEntry}")).Dispose();

        using var delete = await owner.Client.DeleteAsync($"/api/households/{household}/recipes/{pudding}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, await CountAsync("ShoppingListItemSources", "RecipeId = @r", ("@r", pudding)));
        var list = await GenerateAsync(owner.Client, household);
        Assert.Equal("200 g", Quantities(Find(list, "main", "Ris")));
    }

    [Fact]
    public async Task Another_household_cannot_read_or_change_a_list_and_ids_do_not_cross()
    {
        await using var host = Host();
        var (owner, household, _, _, _, _) = await ScenarioAsync(host);
        var list = await GenerateAsync(owner.Client, household);
        var rice = Find(list, "main", "Ris").GetProperty("id").GetGuid();
        var stranger = await host.SignInAsync();
        var strangerHousehold = await stranger.Client.CreateHouseholdAsync();

        // A non-member of the household in the route.
        using (var read = await stranger.Client.GetAsync(ListUrl(household, Monday)))
        {
            await read.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var generate = await stranger.Client.PostAsync(ListUrl(household, Monday, "/generate")))
        {
            await generate.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var check = await CheckAsync(stranger.Client, household, rice, true))
        {
            await check.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // Own household, other household's item id.
        using (var foreignCheck = await CheckAsync(stranger.Client, strangerHousehold, rice, true))
        {
            await foreignCheck.AssertProblemAsync(HttpStatusCode.NotFound, "shopping-list-item-not-found");
        }

        using (var foreignDelete = await stranger.Client.DeleteAsync(ListUrl(strangerHousehold, Monday, $"/items/{rice}")))
        {
            await foreignDelete.AssertProblemAsync(HttpStatusCode.NotFound, "shopping-list-item-not-found");
        }

        // The stranger's own list is empty and independent.
        Assert.Equal(0, (await GetAsync(stranger.Client, strangerHousehold)).GetProperty("main").GetArrayLength());
        Assert.False(Find(await GetAsync(owner.Client, household), "main", "Ris").GetProperty("checked").GetBoolean());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Households_with_the_same_recipe_text_get_their_own_lists()
    {
        await using var host = Host();
        var (ownerA, householdA, _, _, _, _) = await ScenarioAsync(host);
        var (ownerB, householdB, _, _, _, _) = await ScenarioAsync(host);
        var listA = await GenerateAsync(ownerA.Client, householdA);
        (await CheckAsync(ownerA.Client, householdA, Find(listA, "main", "Ris").GetProperty("id").GetGuid(), true)).Dispose();

        var listB = await GenerateAsync(ownerB.Client, householdB);

        Assert.False(Find(listB, "main", "Ris").GetProperty("checked").GetBoolean());
        Assert.NotEqual(listA.GetProperty("id").GetGuid(), listB.GetProperty("id").GetGuid());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Every_mutation_calls_the_notifier_hook_with_the_household_week_and_item()
    {
        var recorder = new RecordingNotifier();
        await using var host = AuthHost.Create(
            db.ConnectionString, configureServices: services => services.AddSingleton<IShoppingListNotifier>(recorder));
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household, "Hooked", 2, new Line(Rice, 100, Gram));
        await PlanAsync(owner.Client, household, recipe, 2);

        var list = await GenerateAsync(owner.Client, household);
        var rice = Find(list, "main", "Ris").GetProperty("id").GetGuid();
        var manual = (await AddManualAsync(owner.Client, household, "Candles")).GetProperty("id").GetGuid();
        (await CheckAsync(owner.Client, household, rice, true)).Dispose();
        (await CheckAsync(owner.Client, household, rice, true)).Dispose(); // no change, no event
        (await CheckAsync(owner.Client, household, rice, false)).Dispose();
        (await owner.Client.DeleteAsync(ListUrl(household, Monday, $"/items/{manual}"))).Dispose();

        Assert.Equal(
            [
                (ShoppingListChangeKind.Generated, (Guid?)null),
                (ShoppingListChangeKind.ManualItemAdded, manual),
                (ShoppingListChangeKind.ItemChecked, rice),
                (ShoppingListChangeKind.ItemUnchecked, rice),
                (ShoppingListChangeKind.ItemRemoved, manual),
            ],
            recorder.Changes.Select(c => (c.Kind, c.ItemId)));
        Assert.All(recorder.Changes, c =>
        {
            Assert.Equal(household, c.HouseholdId);
            Assert.Equal(Monday, c.WeekStart);
        });
    }

    private sealed class RecordingNotifier : IShoppingListNotifier
    {
        private readonly List<ShoppingListChange> _changes = [];

        public IReadOnlyList<ShoppingListChange> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public Task NotifyAsync(ShoppingListChange change, CancellationToken cancellationToken)
        {
            lock (_changes)
            {
                _changes.Add(change);
            }

            return Task.CompletedTask;
        }
    }
}

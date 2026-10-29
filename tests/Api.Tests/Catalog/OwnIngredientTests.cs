using System.Data.Common;
using System.Net;
using System.Text.Json;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Catalog;

/// <summary>Manage the household's own ingredients (T-079): list with usage, rename, delete, merge.</summary>
public sealed class OwnIngredientTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Rice = Seed("ris");
    private static readonly Guid Salt = Seed("salt");
    private static readonly Guid Gram = UnitSeed.All.Single(r => r.Code == "g").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Seed(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private AuthHost Host()
    {
        var host = HouseholdHost.Create(db.ConnectionString);
        host.DefaultLanguage = "nb";
        return host;
    }

    private static string Base(Guid household) => $"/api/households/{household}";

    private static string NewName(string prefix = "Zorba") => $"{prefix}{Guid.NewGuid():N}"[..24];

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = Foodplanner.Api.Tests.Persistence.TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    private static async Task<Guid> CreateOwnAsync(ApiClient client, Guid household, string? name = null, bool staple = false, string dimension = "Mass")
    {
        using var response = await client.PostAsync($"{Base(household)}/ingredients", new { name = name ?? NewName(), isStaple = staple, dimension });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateRecipeAsync(ApiClient client, Guid household, params (Guid Ingredient, decimal? Amount)[] lines)
    {
        using var response = await client.PostAsync($"{Base(household)}/recipes", new
        {
            title = "Test " + Guid.NewGuid().ToString("N")[..6],
            servings = 4,
            ingredients = lines.Select(l => new
            {
                ingredientId = l.Ingredient,
                quantity = l.Amount,
                unitId = l.Amount is null ? (Guid?)null : Gram,
                note = (string?)null,
            }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task PlanAsync(ApiClient client, Guid household, Guid recipe, int dayOffset = 0)
    {
        using var response = await client.PostAsync(
            $"{Base(household)}/meal-plans/{Monday:yyyy-MM-dd}/entries",
            new { date = Monday.AddDays(dayOffset).ToString("yyyy-MM-dd"), recipeId = recipe, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> GenerateAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}/generate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> GetListAsync(ApiClient client, Guid household)
    {
        using var response = await client.GetAsync($"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static JsonElement[] Items(JsonElement list, string section) => [.. list.GetProperty(section).EnumerateArray()];

    private static async Task<JsonElement> RecipeAsync(ApiClient client, Guid household, Guid recipe)
    {
        using var response = await client.GetAsync($"{Base(household)}/recipes/{recipe}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> OwnAsync(ApiClient client, Guid household, string query = "")
    {
        using var response = await client.GetAsync($"{Base(household)}/ingredients/own{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static Task<HttpResponseMessage> MergeAsync(ApiClient client, Guid household, Guid source, Guid? target) =>
        client.PostAsync($"{Base(household)}/ingredients/{source}/merge", new { targetIngredientId = target });

    private async Task<int> CountAsync(string table, string column, Guid value) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {column} = @v", ("@v", value)))!;

    // ---- List ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_has_only_own_ingredients_with_usage_and_is_closed_to_children()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var other = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var used = await CreateOwnAsync(owner.Client, household, "Zorba Brukt");
        var unused = await CreateOwnAsync(owner.Client, household, "Zorba Ubrukt");
        await CreateOwnAsync(other.Client, otherHousehold, "Zorba Fremmed");
        var recipe = await CreateRecipeAsync(owner.Client, household, (used, 100), (Rice, 100));
        await CreateRecipeAsync(owner.Client, household, (used, 50));
        await PlanAsync(owner.Client, household, recipe);
        await GenerateAsync(owner.Client, household);

        var list = await OwnAsync(owner.Client, household);

        Assert.Equal(["Zorba Brukt", "Zorba Ubrukt"], list.EnumerateArray().Select(e => e.GetProperty("name").GetString()!));
        var usage = list[0].GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("recipes").GetInt32());
        Assert.Equal(1, usage.GetProperty("shoppingLists").GetInt32());
        Assert.Equal(0, usage.GetProperty("pantryItems").GetInt32());
        Assert.Equal(0, usage.GetProperty("importDrafts").GetInt32());
        Assert.Equal(unused, list[1].GetProperty("id").GetGuid());
        Assert.Single((await OwnAsync(owner.Client, household, "?search=ubr")).EnumerateArray());

        using var byChild = await child.Client.GetAsync($"{Base(household)}/ingredients/own");
        await byChild.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var foreign = await other.Client.GetAsync($"{Base(household)}/ingredients/own");
        await foreign.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    // ---- Delete --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_an_unused_ingredient_and_refuses_a_used_one_with_counts()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var unused = await CreateOwnAsync(owner.Client, household);
        var used = await CreateOwnAsync(owner.Client, household);
        await CreateRecipeAsync(owner.Client, household, (used, 100));

        using var deleted = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{unused}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync("Ingredients", "Id", unused));
        using var again = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{unused}");
        await again.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");

        using var refused = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{used}");
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-in-use");
        var usage = (await refused.JsonAsync()).GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("recipes").GetInt32());
        Assert.Equal(1, await CountAsync("Ingredients", "Id", used));
    }

    [Fact]
    public async Task Delete_is_refused_for_global_foreign_and_child_requests()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var other = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var mine = await CreateOwnAsync(owner.Client, household);
        var theirs = await CreateOwnAsync(other.Client, otherHousehold);

        using var global = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{Rice}");
        await global.AssertProblemAsync(HttpStatusCode.Forbidden, "ingredient-global");
        using var foreign = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{theirs}");
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");
        using var viaOtherRoute = await other.Client.DeleteAsync($"{Base(household)}/ingredients/{mine}");
        await viaOtherRoute.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var byChild = await child.Client.DeleteAsync($"{Base(household)}/ingredients/{mine}");
        await byChild.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal(1, await CountAsync("Ingredients", "Id", Rice));
        Assert.Equal(1, await CountAsync("Ingredients", "Id", theirs));
        Assert.Equal(1, await CountAsync("Ingredients", "Id", mine));
    }

    // ---- Rename --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_follows_the_create_rules()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var other = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var name = NewName();
        var id = await CreateOwnAsync(owner.Client, household, name);
        var second = NewName();
        await CreateOwnAsync(owner.Client, household, second);
        var theirs = await CreateOwnAsync(other.Client, otherHousehold);
        var url = $"{Base(household)}/ingredients/{id}";

        using var renamed = await owner.Client.PutAsync(url, new { name = "  Fersk   koriander  " });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Fersk koriander", (await renamed.JsonAsync()).GetProperty("name").GetString());
        using var sameCase = await owner.Client.PutAsync(url, new { name = "FERSK KORIANDER" });
        Assert.Equal(HttpStatusCode.OK, sameCase.StatusCode); // its own name in another case is not a duplicate

        using var duplicate = await owner.Client.PutAsync(url, new { name = second.ToUpperInvariant() });
        await duplicate.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");
        using var global = await owner.Client.PutAsync(url, new { name = "salt" });
        await global.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-exists");
        foreach (var bad in new object[] { new { name = "" }, new { name = new string('x', 101) }, new { } })
        {
            using var invalid = await owner.Client.PutAsync(url, bad);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var globalRow = await owner.Client.PutAsync($"{Base(household)}/ingredients/{Rice}", new { name = "Reis" });
        await globalRow.AssertProblemAsync(HttpStatusCode.Forbidden, "ingredient-global");
        using var foreign = await owner.Client.PutAsync($"{Base(household)}/ingredients/{theirs}", new { name = "Mine" });
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");
        using var byChild = await child.Client.PutAsync(url, new { name = "Child" });
        await byChild.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Another household may use the same name, and a rename moves nothing: references keep the id.
        using var otherSame = await other.Client.PutAsync($"{Base(otherHousehold)}/ingredients/{theirs}", new { name = "FERSK KORIANDER" });
        Assert.Equal(HttpStatusCode.OK, otherSame.StatusCode);
    }

    // ---- Merge: refusals -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Merge_refuses_self_unknown_foreign_global_source_and_child()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var other = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var source = await CreateOwnAsync(owner.Client, household);
        var theirs = await CreateOwnAsync(other.Client, otherHousehold);
        var recipe = await CreateRecipeAsync(owner.Client, household, (source, 100));

        using var self = await MergeAsync(owner.Client, household, source, source);
        await self.AssertProblemAsync(HttpStatusCode.BadRequest, "ingredient-merge-into-self");
        using var none = await MergeAsync(owner.Client, household, source, null);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using var unknown = await MergeAsync(owner.Client, household, source, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var foreignTarget = await MergeAsync(owner.Client, household, source, theirs);
        Assert.Equal(HttpStatusCode.BadRequest, foreignTarget.StatusCode);
        Assert.True((await foreignTarget.JsonAsync()).GetProperty("errors").TryGetProperty("targetIngredientId", out _));
        using var globalSource = await MergeAsync(owner.Client, household, Rice, Salt);
        await globalSource.AssertProblemAsync(HttpStatusCode.Forbidden, "ingredient-global");
        using var foreignSource = await MergeAsync(owner.Client, household, theirs, Rice);
        await foreignSource.AssertProblemAsync(HttpStatusCode.NotFound, "ingredient-not-found");
        using var byChild = await MergeAsync(child.Client, household, source, Rice);
        await byChild.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Nothing moved, nothing deleted.
        Assert.Equal(source, (await RecipeAsync(owner.Client, household, recipe)).GetProperty("ingredients")[0].GetProperty("ingredientId").GetGuid());
        Assert.Equal(1, await CountAsync("Ingredients", "Id", theirs));
        await AssertNoCrossHouseholdLinksAsync();
    }

    // ---- Merge: references ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Merge_moves_recipe_lines_combines_list_items_and_notifies()
    {
        var recorder = new RecordingNotifier();
        await using var host = AuthHost.Create(db.ConnectionString, configureServices: s => s.AddSingleton<IShoppingListNotifier>(recorder));
        host.DefaultLanguage = "nb";
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var source = await CreateOwnAsync(owner.Client, household, "Zorba Ris");
        var recipe1 = await CreateRecipeAsync(owner.Client, household, (source, 100), (Rice, 200));
        var recipe2 = await CreateRecipeAsync(owner.Client, household, (source, 50));
        await PlanAsync(owner.Client, household, recipe1);
        await PlanAsync(owner.Client, household, recipe2, 1);
        var before = await GenerateAsync(owner.Client, household);
        Assert.Equal(["Ris", "Zorba Ris"], Items(before, "main").Select(i => i.GetProperty("name").GetString()!).Order());

        var changesBefore = recorder.Changes.Count;
        using var merged = await MergeAsync(owner.Client, household, source, Rice);

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var body = await merged.JsonAsync();
        Assert.Equal(Rice, body.GetProperty("target").GetProperty("id").GetGuid());
        Assert.Equal("Ris", body.GetProperty("target").GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("moved").GetProperty("recipes").GetInt32());
        Assert.Equal(1, body.GetProperty("moved").GetProperty("shoppingLists").GetInt32());
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());

        // Recipes use the catalog ingredient and the source is gone.
        Assert.All((await RecipeAsync(owner.Client, household, recipe1)).GetProperty("ingredients").EnumerateArray(),
            l => Assert.Equal("Ris", l.GetProperty("ingredientName").GetString()));
        Assert.Equal(Rice, (await RecipeAsync(owner.Client, household, recipe2)).GetProperty("ingredients")[0].GetProperty("ingredientId").GetGuid());
        Assert.Equal(0, await CountAsync("Ingredients", "Id", source));
        Assert.Equal(0, await CountAsync("RecipeIngredients", "IngredientId", source));
        Assert.Empty((await OwnAsync(owner.Client, household)).EnumerateArray());

        // One list item for rice with the combined amount: 200 + 100 + 50 g.
        var list = await GetListAsync(owner.Client, household);
        var rice = Assert.Single(Items(list, "main"));
        Assert.Equal("Ris", rice.GetProperty("name").GetString());
        var quantity = Assert.Single(rice.GetProperty("quantities").EnumerateArray());
        Assert.Equal(350m, quantity.GetProperty("amount").GetDecimal());
        Assert.Equal("g", quantity.GetProperty("unitCode").GetString());
        Assert.Equal(0, await CountAsync("ShoppingListItems", "IngredientId", source));

        // Live sync signal for the regenerated week.
        var change = Assert.Single(recorder.Changes.Skip(changesBefore));
        Assert.Equal((household, Monday, ShoppingListChangeKind.Generated), (change.HouseholdId, change.WeekStart, change.Kind));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Merge_moves_a_list_item_that_has_no_counterpart_and_keeps_it_checked()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var source = await CreateOwnAsync(owner.Client, household, "Zorba Fersk");
        var target = await CreateOwnAsync(owner.Client, household, "Zorba Frisk", dimension: "Volume");
        var recipe = await CreateRecipeAsync(owner.Client, household, (source, 100));
        await PlanAsync(owner.Client, household, recipe);
        var item = Assert.Single(Items(await GenerateAsync(owner.Client, household), "main"));
        using var check = await owner.Client.PutAsync(
            $"{Base(household)}/shopping-lists/{Monday:yyyy-MM-dd}/items/{item.GetProperty("id").GetGuid()}/check", new { @checked = true });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);

        using var merged = await MergeAsync(owner.Client, household, source, target);

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal(["dimension-differs"], (await merged.JsonAsync()).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!));
        var after = Assert.Single(Items(await GetListAsync(owner.Client, household), "main"));
        Assert.Equal(item.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        Assert.Equal("Zorba Frisk", after.GetProperty("name").GetString());
        Assert.Equal(target, after.GetProperty("ingredientId").GetGuid());
        Assert.True(after.GetProperty("checked").GetBoolean());
        Assert.False(after.GetProperty("quantityChangedSinceChecked").GetBoolean());
    }

    [Fact]
    public async Task Merge_combines_pantry_rows_and_moves_a_lone_one()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var source = await CreateOwnAsync(owner.Client, household, staple: true);
        var lone = await CreateOwnAsync(owner.Client, household, staple: true);
        var own = await CreateOwnAsync(owner.Client, household, staple: true);
        var now = host.Time.Now;
        (await owner.Client.PutAsync($"{Base(household)}/pantry/{Salt}", new { expiresAt = now.AddDays(10) })).Dispose();
        host.Time.Now = now.AddHours(1);
        (await owner.Client.PutAsync($"{Base(household)}/pantry/{source}", new { expiresAt = now.AddDays(30) })).Dispose();
        (await owner.Client.PutAsync($"{Base(household)}/pantry/{lone}", new { })).Dispose();

        using var intoSalt = await MergeAsync(owner.Client, household, source, Salt);
        Assert.Equal(HttpStatusCode.OK, intoSalt.StatusCode);
        Assert.Equal(1, (await intoSalt.JsonAsync()).GetProperty("moved").GetProperty("pantryItems").GetInt32());
        using var intoOwn = await MergeAsync(owner.Client, household, lone, own);
        Assert.Equal(HttpStatusCode.OK, intoOwn.StatusCode);

        using var pantryResponse = await owner.Client.GetAsync($"{Base(household)}/pantry");
        var pantry = (await pantryResponse.JsonAsync()).EnumerateArray().ToDictionary(p => p.GetProperty("ingredientId").GetGuid());
        Assert.Equal(new[] { own, Salt }.Order(), pantry.Keys.Order());
        Assert.Equal(now.AddDays(30), pantry[Salt].GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(now.AddHours(1), pantry[Salt].GetProperty("confirmedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, pantry[own].GetProperty("expiresAt").ValueKind);
        Assert.Equal(0, await CountAsync("PantryItems", "IngredientId", source));
        Assert.Equal(0, await CountAsync("PantryItems", "IngredientId", lone));
        await AssertNoCrossHouseholdLinksAsync();
    }

    /// <summary>T-093 review: the merge rules for pantry amounts (PantryItem.Absorb) through the endpoint, not only in Domain.</summary>
    [Fact]
    public async Task Merge_combines_pantry_amounts_by_unit_and_recency()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var kilogram = UnitSeed.All.Single(r => r.Code == "kg").Id;
        var pantryUrl = $"{Base(household)}/pantry";

        async Task PutAsync(Guid ingredient, object body)
        {
            using var response = await owner.Client.PutAsync($"{pantryUrl}/{ingredient}", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Same unit: the amounts are added.
        var (sumSource, sumTarget) = (await CreateOwnAsync(owner.Client, household), await CreateOwnAsync(owner.Client, household));
        await PutAsync(sumTarget, new { quantity = 300, unitId = Gram });
        await PutAsync(sumSource, new { quantity = 200, unitId = Gram });
        // Different units: no conversion, the more recently confirmed entry wins (the source is newer here).
        var (unitSource, unitTarget) = (await CreateOwnAsync(owner.Client, household), await CreateOwnAsync(owner.Client, household));
        await PutAsync(unitTarget, new { quantity = 200, unitId = Gram });
        host.Time.Now = host.Time.Now.AddHours(1);
        await PutAsync(unitSource, new { quantity = 1, unitId = kilogram });
        // An amount beats "some", whichever is newer.
        var (someOlder, amountNewer) = (await CreateOwnAsync(owner.Client, household), await CreateOwnAsync(owner.Client, household));
        await PutAsync(someOlder, new { });
        host.Time.Now = host.Time.Now.AddHours(1);
        await PutAsync(amountNewer, new { quantity = 50, unitId = Gram });
        var (amountOlder, someNewer) = (await CreateOwnAsync(owner.Client, household), await CreateOwnAsync(owner.Client, household));
        await PutAsync(amountOlder, new { quantity = 100, unitId = Gram });
        host.Time.Now = host.Time.Now.AddHours(1);
        await PutAsync(someNewer, new { });

        foreach (var (source, target) in new[] { (sumSource, sumTarget), (unitSource, unitTarget), (someOlder, amountNewer), (someNewer, amountOlder) })
        {
            using var merged = await MergeAsync(owner.Client, household, source, target);
            Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        }

        using var response = await owner.Client.GetAsync(pantryUrl);
        var pantry = (await response.JsonAsync()).EnumerateArray().ToDictionary(p => p.GetProperty("ingredientId").GetGuid());
        Assert.Equal(4, pantry.Count);
        Assert.Equal((500m, Gram), (pantry[sumTarget].GetProperty("quantity").GetDecimal(), pantry[sumTarget].GetProperty("unitId").GetGuid()));
        Assert.Equal((1m, kilogram), (pantry[unitTarget].GetProperty("quantity").GetDecimal(), pantry[unitTarget].GetProperty("unitId").GetGuid()));
        Assert.Equal((50m, Gram), (pantry[amountNewer].GetProperty("quantity").GetDecimal(), pantry[amountNewer].GetProperty("unitId").GetGuid()));
        Assert.Equal((100m, Gram), (pantry[amountOlder].GetProperty("quantity").GetDecimal(), pantry[amountOlder].GetProperty("unitId").GetGuid()));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Merge_rewrites_open_import_drafts_and_the_draft_still_saves()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var name = "Zorbaflott";
        var source = await CreateOwnAsync(owner.Client, household, name);
        using var imported = await owner.Client.PostAsync(
            $"{Base(household)}/imports", new { text = $"Pannekake\n\nIngredienser\n2 dl {name}\n\nFremgangsmåte\nBland alt sammen og stek." });
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var draft = await imported.JsonAsync();
        var importId = draft.GetProperty("id").GetGuid();
        Assert.Equal(source, draft.GetProperty("lines")[0].GetProperty("ingredientId").GetGuid());
        Assert.Equal(1, (await OwnAsync(owner.Client, household))[0].GetProperty("usage").GetProperty("importDrafts").GetInt32());
        using var refused = await owner.Client.DeleteAsync($"{Base(household)}/ingredients/{source}");
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-in-use");
        Assert.Equal(1, (await refused.JsonAsync()).GetProperty("usage").GetProperty("importDrafts").GetInt32());

        using var merged = await MergeAsync(owner.Client, household, source, Rice);

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal(1, (await merged.JsonAsync()).GetProperty("moved").GetProperty("importDrafts").GetInt32());
        using var after = await owner.Client.GetAsync($"{Base(household)}/imports/{importId}");
        Assert.Equal(Rice, (await after.JsonAsync()).GetProperty("lines")[0].GetProperty("ingredientId").GetGuid());
        using var saved = await owner.Client.PostAsync($"{Base(household)}/imports/{importId}/save");
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task A_failure_part_way_rolls_everything_back()
    {
        var failing = new FailOnIngredientDelete();
        await using var host = AuthHost.Create(
            db.ConnectionString, configureServices: s => s.ConfigureDbContext<Foodplanner.Infrastructure.Persistence.AppDbContext>(o => o.AddInterceptors(failing)));
        host.DefaultLanguage = "nb";
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var source = await CreateOwnAsync(owner.Client, household, staple: true);
        var recipe = await CreateRecipeAsync(owner.Client, household, (source, 100));
        await PlanAsync(owner.Client, household, recipe);
        (await owner.Client.PutAsync($"{Base(household)}/pantry/{source}", new { })).Dispose();
        await GenerateAsync(owner.Client, household);
        failing.Armed = true;

        using var merged = await MergeAsync(owner.Client, household, source, Salt);

        await merged.AssertProblemAsync(HttpStatusCode.Conflict, "ingredient-conflict");
        failing.Armed = false;
        Assert.Equal(source, (await RecipeAsync(owner.Client, household, recipe)).GetProperty("ingredients")[0].GetProperty("ingredientId").GetGuid());
        Assert.Equal(1, await CountAsync("Ingredients", "Id", source));
        Assert.Equal(1, await CountAsync("PantryItems", "IngredientId", source));
        // Salt is a global row shared by every test of the class: look only at this household's pantry.
        Assert.Equal(0, (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM PantryItems WHERE IngredientId = @i AND HouseholdId = @h", ("@i", Salt), ("@h", household)))!);
        Assert.Equal(1, await CountAsync("RecipeIngredients", "IngredientId", source));
    }

    [Fact]
    public async Task Merge_racing_a_recipe_edit_ends_consistent_never_with_a_server_error()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        for (var round = 0; round < 6; round++)
        {
            var source = await CreateOwnAsync(owner.Client, household);
            var recipe = await CreateRecipeAsync(owner.Client, household, (source, 100));
            var edit = owner.Client.PutAsync($"{Base(household)}/recipes/{recipe}", new
            {
                title = "Edited",
                servings = 4,
                ingredients = new[] { new { ingredientId = source, quantity = (decimal?)100, unitId = (Guid?)Gram, note = (string?)null } },
            });
            var merge = MergeAsync(owner.Client, household, source, Rice);
            using var editResponse = await edit;
            using var mergeResponse = await merge;

            Assert.Contains(editResponse.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
            Assert.Contains(mergeResponse.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            if (mergeResponse.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(0, await CountAsync("Ingredients", "Id", source));
                Assert.Equal(0, await CountAsync("RecipeIngredients", "IngredientId", source));
            }
            else
            {
                Assert.Equal(1, await CountAsync("Ingredients", "Id", source));
            }
        }

        await AssertNoCrossHouseholdLinksAsync();
    }

    // ---- Coverage of the model ------------------------------------------------------------------------------------

    [Fact]
    public void Every_foreign_key_to_ingredient_in_the_model_is_handled_by_merge()
    {
        using var context = Foodplanner.Api.Tests.Persistence.TenancyDatabase.CreateProductionContext(db.ConnectionString);
        var inModel = context.Model.GetEntityTypes()
            .SelectMany(t => t.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Ingredient))
            .SelectMany(fk => fk.Properties.Select(p => $"{fk.DeclaringEntityType.ClrType.Name}.{p.Name}"))
            .Order()
            .ToList();
        var handled = Foodplanner.Api.Features.Catalog.IngredientReferences.Handled.Select(h => $"{h.Dependent.Name}.{h.Property}").Order().ToList();

        Assert.True(
            inModel.SequenceEqual(handled),
            "A foreign key to Ingredient was added or removed: teach IngredientMerger and IngredientUsage about it, then update IngredientReferences.Handled. "
            + $"Model: {string.Join(", ", inModel)}; handled: {string.Join(", ", handled)}");
    }

    // ---- Support -------------------------------------------------------------------------------------------------

    private sealed class FailOnIngredientDelete : DbCommandInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("DELETE FROM [Ingredients]", StringComparison.Ordinal))
            {
                throw new DbUpdateException("Injected failure");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("DELETE FROM [Ingredients]", StringComparison.Ordinal))
            {
                throw new DbUpdateException("Injected failure");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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

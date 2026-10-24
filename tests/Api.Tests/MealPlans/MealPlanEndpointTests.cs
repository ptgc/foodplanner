using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.MealPlans;

namespace Foodplanner.Api.Tests.MealPlans;

public sealed class MealPlanEndpointTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;

    private AuthHost Host() => HouseholdHost.Create(db.ConnectionString);

    private static string Url(Guid household, DateOnly week, string rest = "") =>
        $"/api/households/{household}/meal-plans/{week:yyyy-MM-dd}{rest}";

    private static async Task<Guid> CreateRecipeAsync(ApiClient client, Guid household, int servings = 4)
    {
        using var response = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Plan " + Guid.NewGuid().ToString("N"),
            servings,
            ingredients = new[] { new { ingredientId = Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddAsync(ApiClient client, Guid household, Guid recipe, DateOnly date, int? servings = null, DateOnly? week = null)
    {
        using var response = await client.PostAsync(Url(household, week ?? Monday, "/entries"), new { date = date.ToString("yyyy-MM-dd"), recipeId = recipe, servings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<int> CountAsync(string table, string where, params (string Name, object Value)[] parameters) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {where}", parameters))!;

    private static async Task AssertValidationAsync(HttpResponseMessage response, params string[] keys)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = (await response.JsonAsync()).GetProperty("errors");
        foreach (var key in keys)
        {
            Assert.True(errors.TryGetProperty(key, out _), $"expected an error for '{key}', got {errors}");
        }
    }

    private async Task AssertNoCrossHouseholdLinksAsync()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Reading_an_empty_week_returns_an_empty_plan_and_writes_nothing()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.GetAsync(Url(household, Monday));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("id").ValueKind);
        Assert.Equal("2026-10-26", body.GetProperty("weekStart").GetString());
        Assert.Equal(0, body.GetProperty("entries").GetArrayLength());
        Assert.Equal(0, await CountAsync("MealPlans", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Owner_adult_and_child_pick_for_the_same_week_and_see_each_others_entries()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await CreateRecipeAsync(owner.Client, household, servings: 4);

        var byOwner = await AddAsync(owner.Client, household, recipe, Monday);
        var byAdult = await AddAsync(adult.Client, household, recipe, Monday.AddDays(2), servings: 6);
        var byChild = await AddAsync(child.Client, household, recipe, Monday.AddDays(6), servings: 2);
        var secondOnSameDay = await AddAsync(child.Client, household, recipe, Monday);

        foreach (var viewer in new[] { owner, adult, child })
        {
            using var response = await viewer.Client.GetAsync(Url(household, Monday));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.JsonAsync();
            var entries = body.GetProperty("entries").EnumerateArray().ToList();

            // By date, then in creation order.
            Assert.Equal([byOwner, secondOnSameDay, byAdult, byChild], entries.Select(e => e.GetProperty("id").GetGuid()));
            Assert.Equal(["2026-10-26", "2026-10-26", "2026-10-28", "2026-11-01"], entries.Select(e => e.GetProperty("date").GetString()));
            Assert.Equal([4, 4, 6, 2], entries.Select(e => e.GetProperty("servings").GetInt32())); // default = the recipe's servings
            Assert.Equal(
                [owner.User.Id, child.User.Id, adult.User.Id, child.User.Id],
                entries.Select(e => e.GetProperty("pickedBy").GetProperty("userId").GetGuid()));
            Assert.Equal(
                [owner.User.Email, child.User.Email, adult.User.Email, child.User.Email],
                entries.Select(e => e.GetProperty("pickedBy").GetProperty("email").GetString()));
            Assert.All(entries, e => Assert.StartsWith("Plan ", e.GetProperty("recipeTitle").GetString(), StringComparison.Ordinal));
            Assert.All(entries, e => Assert.Equal(recipe, e.GetProperty("recipeId").GetGuid()));
        }

        // One plan for the week, however many members picked.
        Assert.Equal(1, await CountAsync("MealPlans", "HouseholdId = @h", ("@h", household)));
        Assert.Equal(4, await CountAsync("MealPlanEntries", "HouseholdId = @h", ("@h", household)));

        // Another week is its own, empty plan.
        using var next = await owner.Client.GetAsync(Url(household, Monday.AddDays(7)));
        Assert.Equal(0, (await next.JsonAsync()).GetProperty("entries").GetArrayLength());
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task The_picker_comes_from_the_caller_never_from_the_body()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await CreateRecipeAsync(owner.Client, household);

        using var response = await child.Client.PostAsync(Url(household, Monday, "/entries"), new
        {
            date = "2026-10-27",
            recipeId = recipe,
            pickedByUserId = owner.User.Id,
            pickedBy = new { userId = owner.User.Id },
            householdId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.JsonAsync();
        Assert.Equal(child.User.Id, created.GetProperty("pickedBy").GetProperty("userId").GetGuid());
        Assert.Equal(Url(household, Monday), response.Headers.Location?.OriginalString);
        Assert.Equal(1, await CountAsync("MealPlanEntries", "HouseholdId = @h AND PickedByUserId = @u", ("@h", household), ("@u", child.User.Id)));
    }

    [Theory]
    [InlineData("Owner", true, true)]
    [InlineData("Owner", false, true)]
    [InlineData("Adult", true, true)]
    [InlineData("Adult", false, true)]
    [InlineData("Child", true, true)]
    [InlineData("Child", false, false)]
    public async Task Resource_policy_Adult_or_Owner_may_change_any_entry_and_a_Child_only_their_own(string role, bool ownEntry, bool allowed)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var caller = role == "Owner" ? owner : await host.JoinAsync(owner, household, role);
        var other = role == "Owner" ? await host.JoinAsync(owner, household, "Adult") : owner;
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var entry = await AddAsync((ownEntry ? caller : other).Client, household, recipe, Monday);
        var url = Url(household, Monday, "/entries/" + entry);

        using var update = await caller.Client.PutAsync(url, new { date = "2026-10-28", servings = 5 });
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(5, (await update.JsonAsync()).GetProperty("servings").GetInt32());
        }
        else
        {
            await update.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            Assert.Equal(1, await CountAsync("MealPlanEntries", "Id = @e AND Servings = 4 AND Date = '2026-10-26'", ("@e", entry)));
        }

        using var delete = await caller.Client.DeleteAsync(url);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(0, await CountAsync("MealPlanEntries", "Id = @e", ("@e", entry)));
        }
        else
        {
            await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            Assert.Equal(1, await CountAsync("MealPlanEntries", "Id = @e", ("@e", entry)));
        }
    }

    [Fact]
    public async Task Update_changes_day_and_servings_and_keeps_the_recipe_and_the_picker()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var entry = await AddAsync(child.Client, household, recipe, Monday);

        using var update = await owner.Client.PutAsync(Url(household, Monday, "/entries/" + entry), new { date = "2026-11-01", servings = 3, recipeId = Guid.NewGuid() });

        var body = await update.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("2026-11-01", body.GetProperty("date").GetString());
        Assert.Equal(3, body.GetProperty("servings").GetInt32());
        Assert.Equal(recipe, body.GetProperty("recipeId").GetGuid());
        Assert.Equal(child.User.Id, body.GetProperty("pickedBy").GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Invalid_input_is_a_validation_problem_and_nothing_is_saved()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var entry = await AddAsync(owner.Client, household, recipe, Monday);
        var before = await CountAsync("MealPlanEntries", "HouseholdId = @h", ("@h", household));

        (string[] Keys, object Body)[] adds =
        [
            (["date"], new { recipeId = recipe }),
            (["date"], new { date = "2026-11-02", recipeId = recipe }), // the next Monday
            (["date"], new { date = "2026-10-25", recipeId = recipe }), // the Sunday before
            (["recipeId"], new { date = "2026-10-26" }),
            (["recipeId"], new { date = "2026-10-26", recipeId = Guid.Empty }),
            (["servings"], new { date = "2026-10-26", recipeId = recipe, servings = 0 }),
            (["servings"], new { date = "2026-10-26", recipeId = recipe, servings = MealPlanEntry.MaxServings + 1 }),
        ];
        foreach (var (keys, body) in adds)
        {
            using var response = await owner.Client.PostAsync(Url(household, Monday, "/entries"), body);
            await AssertValidationAsync(response, keys);
        }

        (string[] Keys, object Body)[] updates =
        [
            (["date"], new { date = "2026-11-02", servings = 2 }),
            (["date"], new { servings = 2 }),
            (["servings"], new { date = "2026-10-26" }),
            (["servings"], new { date = "2026-10-26", servings = 0 }),
            (["servings"], new { date = "2026-10-26", servings = MealPlanEntry.MaxServings + 1 }),
        ];
        foreach (var (keys, body) in updates)
        {
            using var response = await owner.Client.PutAsync(Url(household, Monday, "/entries/" + entry), body);
            await AssertValidationAsync(response, keys);
        }

        Assert.Equal(before, await CountAsync("MealPlanEntries", "HouseholdId = @h", ("@h", household)));
        Assert.Equal(1, await CountAsync("MealPlanEntries", "Id = @e AND Servings = 4 AND Date = '2026-10-26'", ("@e", entry)));

        // The boundaries are accepted.
        using var boundary = await owner.Client.PostAsync(Url(household, Monday, "/entries"), new { date = "2026-11-01", recipeId = recipe, servings = MealPlanEntry.MaxServings });
        Assert.Equal(HttpStatusCode.Created, boundary.StatusCode);
    }

    [Theory]
    [InlineData("2026-10-27")] // Tuesday
    [InlineData("2026-10-25")] // Sunday
    [InlineData("not-a-date")]
    [InlineData("2026-13-01")]
    [InlineData("20261026")]
    public async Task A_week_that_is_not_an_iso_monday_is_a_validation_problem_on_every_route(string week)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var baseUrl = $"/api/households/{household}/meal-plans/{week}";

        foreach (var response in new[]
        {
            await owner.Client.GetAsync(baseUrl),
            await owner.Client.PostAsync(baseUrl + "/entries", new { date = "2026-10-26", recipeId = recipe }),
            await owner.Client.PutAsync(baseUrl + "/entries/" + Guid.NewGuid(), new { date = "2026-10-26", servings = 2 }),
            await owner.Client.DeleteAsync(baseUrl + "/entries/" + Guid.NewGuid()),
        })
        {
            using (response)
            {
                await AssertValidationAsync(response, "weekStart");
            }
        }

        Assert.Equal(0, await CountAsync("MealPlans", "HouseholdId = @h", ("@h", household)));
    }

    [Fact]
    public async Task Another_households_or_unknown_recipe_is_a_validation_error_and_nothing_is_saved()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var recipeOfB = await CreateRecipeAsync(b.Client, householdB);

        using var foreign = await a.Client.PostAsync(Url(householdA, Monday, "/entries"), new { date = "2026-10-26", recipeId = recipeOfB });
        using var unknown = await a.Client.PostAsync(Url(householdA, Monday, "/entries"), new { date = "2026-10-26", recipeId = Guid.NewGuid() });

        await AssertValidationAsync(foreign, "recipeId");
        await AssertValidationAsync(unknown, "recipeId");
        Assert.Equal(await foreign.ProblemShapeAsync(), await unknown.ProblemShapeAsync()); // "someone else's" looks like "does not exist"
        Assert.Equal(0, await CountAsync("MealPlanEntries", "RecipeId = @r", ("@r", recipeOfB)));
        Assert.Equal(0, await CountAsync("MealPlans", "HouseholdId = @h", ("@h", householdA))); // a refused entry does not create the plan
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task Another_household_cannot_read_or_change_the_plan()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var recipeA = await CreateRecipeAsync(a.Client, householdA);
        var recipeB = await CreateRecipeAsync(b.Client, householdB);
        var entryA = await AddAsync(a.Client, householdA, recipeA, Monday);
        var urlA = Url(householdA, Monday, "/entries/" + entryA);

        // B on A's route: not a member.
        foreach (var response in new[]
        {
            await b.Client.GetAsync(Url(householdA, Monday)),
            await b.Client.PostAsync(Url(householdA, Monday, "/entries"), new { date = "2026-10-26", recipeId = recipeB }),
            await b.Client.PutAsync(urlA, new { date = "2026-10-27", servings = 2 }),
            await b.Client.DeleteAsync(urlA),
        })
        {
            using (response)
            {
                await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }
        }

        // B on its own route with A's entry id: the filter hides it.
        using var put = await b.Client.PutAsync(Url(householdB, Monday, "/entries/" + entryA), new { date = "2026-10-27", servings = 2 });
        await put.AssertProblemAsync(HttpStatusCode.NotFound, "meal-plan-entry-not-found");
        using var delete = await b.Client.DeleteAsync(Url(householdB, Monday, "/entries/" + entryA));
        await delete.AssertProblemAsync(HttpStatusCode.NotFound, "meal-plan-entry-not-found");

        // B's plan for the same week is empty and A's entry is untouched.
        using var planB = await b.Client.GetAsync(Url(householdB, Monday));
        Assert.Equal(0, (await planB.JsonAsync()).GetProperty("entries").GetArrayLength());
        Assert.Equal(1, await CountAsync("MealPlanEntries", "Id = @e AND HouseholdId = @h AND Servings = 4", ("@e", entryA), ("@h", householdA)));
        await AssertNoCrossHouseholdLinksAsync();
    }

    [Fact]
    public async Task An_entry_is_found_only_under_its_own_week()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var entry = await AddAsync(owner.Client, household, recipe, Monday);

        using var otherWeek = await owner.Client.DeleteAsync(Url(household, Monday.AddDays(7), "/entries/" + entry));

        await otherWeek.AssertProblemAsync(HttpStatusCode.NotFound, "meal-plan-entry-not-found");
        Assert.Equal(1, await CountAsync("MealPlanEntries", "Id = @e", ("@e", entry)));
        using var gone = await owner.Client.DeleteAsync(Url(household, Monday, "/entries/" + entry));
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        using var again = await owner.Client.DeleteAsync(Url(household, Monday, "/entries/" + entry));
        await again.AssertProblemAsync(HttpStatusCode.NotFound, "meal-plan-entry-not-found");
    }

    [Fact]
    public async Task A_planned_recipe_cannot_be_deleted_until_it_is_off_the_plan()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household);
        var entry = await AddAsync(owner.Client, household, recipe, Monday);
        var recipeUrl = $"/api/households/{household}/recipes/{recipe}";

        using var refused = await owner.Client.DeleteAsync(recipeUrl);

        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "recipe-in-use");
        Assert.Equal(1, await CountAsync("Recipes", "Id = @r", ("@r", recipe)));
        Assert.Equal(1, await CountAsync("MealPlanEntries", "Id = @e", ("@e", entry)));

        using var removed = await owner.Client.DeleteAsync(Url(household, Monday, "/entries/" + entry));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var deleted = await owner.Client.DeleteAsync(recipeUrl);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync("Recipes", "Id = @r", ("@r", recipe)));
    }

    [Fact]
    public async Task Servings_default_is_capped_at_the_entry_maximum()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var recipe = await CreateRecipeAsync(owner.Client, household, servings: 80);

        var entry = await AddAsync(owner.Client, household, recipe, Monday);

        Assert.Equal(MealPlanEntry.MaxServings, await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT Servings FROM MealPlanEntries WHERE Id = @e", ("@e", entry)));
    }

    [Fact]
    public async Task Two_members_adding_to_a_new_week_at_once_share_one_plan()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await CreateRecipeAsync(owner.Client, household);

        await Task.WhenAll(new[] { owner, adult, child }.Select(m => AddAsync(m.Client, household, recipe, Monday)));

        Assert.Equal(1, await CountAsync("MealPlans", "HouseholdId = @h", ("@h", household)));
        Assert.Equal(3, await CountAsync("MealPlanEntries", "HouseholdId = @h", ("@h", household)));
    }
}

using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>The household's weekly meal mix rules (T-090): who manages them, validation, and how they filter and refuse picks.</summary>
public sealed class MealMixRuleTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly string Week = Monday.ToString("yyyy-MM-dd");

    private AuthHost Host() => KidHost.Create(db.ConnectionString);

    private static string Rules(Guid household) => $"/api/households/{household}/meal-mix-rules";

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/meal-plans/{Week}{rest}";

    private static object[] FishAndVegetarian => [new { foodType = "Fish", minPerWeek = 1 }, new { foodType = "Vegetarian", minPerWeek = 1 }];

    private sealed record Setup(KidScenario S, ApiClient Admin, SignedInUser Adult, SignedInUser Child);

    private static async Task<Setup> NewAsync(AuthHost host)
    {
        var s = await host.NewScenarioAsync();
        var admin = host.CreateClient();
        using var login = await admin.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        return new Setup(s, admin, await host.JoinAsync(s.Owner, s.Household, "Adult"), await host.JoinAsync(s.Owner, s.Household, "Child"));
    }

    /// <summary>A recipe whose food type is set by hand; <c>dinner</c> puts it in the category Middag.</summary>
    private static async Task<Guid> RecipeAsync(ApiClient client, Guid household, string foodType, bool dinner = true)
    {
        using var response = await client.PostAsync($"/api/households/{household}/recipes", new
        {
            title = "Mix " + Guid.NewGuid().ToString("N"),
            servings = 4,
            ingredients = new[] { new { ingredientId = KidHost.Salt, quantity = (decimal?)null, unitId = (Guid?)null, note = (string?)null } },
            tagIds = dinner ? new[] { KidHost.Middag } : [KidHost.Dessert],
            foodType,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task SetRulesAsync(ApiClient client, Guid household, params object[] rules)
    {
        using var response = await client.PutAsync(Rules(household), new { rules });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> AddAsync(ApiClient client, Guid household, Guid recipe, int dayOffset) =>
        await client.PostAsync(Url(household, "/entries"), new { date = Monday.AddDays(dayOffset).ToString("yyyy-MM-dd"), recipeId = recipe });

    private static async Task FillMeatDaysAsync(ApiClient admin, Guid household, int days)
    {
        for (var i = 0; i < days; i++)
        {
            using var response = await AddAsync(admin, household, await RecipeAsync(admin, household, "Beef"), i);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    private static async Task<JsonElement> OptionsAsync(ApiClient client, Guid household, int dayOffset)
    {
        using var response = await client.GetAsync(Url(household, $"/pick-options?date={Monday.AddDays(dayOffset):yyyy-MM-dd}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static (bool Allowed, string? Reason) Option(JsonElement options, Guid recipe)
    {
        var item = options.GetProperty("recipes").EnumerateArray().Single(r => r.GetProperty("recipeId").GetGuid() == recipe);
        return (item.GetProperty("allowed").GetBoolean(), item.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Adult_and_owner_manage_the_rules_and_every_member_reads_them()
    {
        await using var host = Host();
        var x = await NewAsync(host);

        using (var empty = await x.Child.Client.GetAsync(Rules(x.S.Household)))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            var body = await empty.JsonAsync();
            Assert.Equal(0, body.GetProperty("rules").GetArrayLength());
            Assert.Equal(7, body.GetProperty("dinnerDaysPerWeek").GetInt32());
        }

        await SetRulesAsync(x.Adult.Client, x.S.Household, FishAndVegetarian);
        using (var read = await x.Child.Client.GetAsync(Rules(x.S.Household)))
        {
            var rules = (await read.JsonAsync()).GetProperty("rules");
            Assert.Equal(["Fish", "Vegetarian"], rules.EnumerateArray().Select(r => r.GetProperty("foodType").GetString()).ToArray());
        }

        // Replace: change one, drop one, add one.
        await SetRulesAsync(x.Admin, x.S.Household, new { foodType = "Fish", minPerWeek = 2 }, new { foodType = "Chicken", minPerWeek = 1 });
        using var after = await x.Admin.GetAsync(Rules(x.S.Household));
        var items = (await after.JsonAsync()).GetProperty("rules").EnumerateArray().Select(r => (r.GetProperty("foodType").GetString(), r.GetProperty("minPerWeek").GetInt32())).ToArray();
        Assert.Equal([("Fish", 2), ("Chicken", 1)], items);

        await SetRulesAsync(x.Admin, x.S.Household);
        using var cleared = await x.Admin.GetAsync(Rules(x.S.Household));
        Assert.Equal(0, (await cleared.JsonAsync()).GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task A_child_member_and_kid_mode_cannot_change_the_rules_and_another_household_cannot_see_them()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await SetRulesAsync(x.Admin, x.S.Household, FishAndVegetarian);

        using var child = await x.Child.Client.PutAsync(Rules(x.S.Household), new { rules = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, child.StatusCode);

        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);
        using var kid = await x.S.Owner.Client.PutAsync(Rules(x.S.Household), new { rules = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, kid.StatusCode);
        using var kidRead = await x.S.Owner.Client.GetAsync(Rules(x.S.Household));
        Assert.Equal(HttpStatusCode.OK, kidRead.StatusCode);

        var other = await host.NewScenarioAsync();
        using var otherRules = await other.Owner.Client.GetAsync(Rules(other.Household));
        Assert.Equal(0, (await otherRules.JsonAsync()).GetProperty("rules").GetArrayLength());
        using var foreign = await other.Owner.Client.GetAsync(Rules(x.S.Household));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Invalid_rules_are_a_validation_problem_and_change_nothing()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await SetRulesAsync(x.Admin, x.S.Household, FishAndVegetarian);

        var bad = new object[][]
        {
            [new { foodType = "Fish", minPerWeek = 4 }, new { foodType = "Chicken", minPerWeek = 4 }], // more than 7 dinners
            [new { foodType = "Fish", minPerWeek = 1 }, new { foodType = "fish", minPerWeek = 2 }], // duplicate
            [new { foodType = "Unknown", minPerWeek = 1 }],
            [new { foodType = "Cheese", minPerWeek = 1 }],
            [new { foodType = "Fish", minPerWeek = 0 }],
            [new { foodType = "Fish", minPerWeek = 8 }],
            [new { foodType = "Fish", minPerWeek = (int?)null }],
        };
        foreach (var rules in bad)
        {
            using var response = await x.Admin.PutAsync(Rules(x.S.Household), new { rules });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        using var noBody = await x.Admin.PutAsync(Rules(x.S.Household), new { });
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);

        using var read = await x.Admin.GetAsync(Rules(x.S.Household));
        Assert.Equal(2, (await read.JsonAsync()).GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task After_five_meat_dinners_a_kid_can_only_pick_fish_or_vegetarian_but_an_adult_can_override()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await SetRulesAsync(x.Admin, x.S.Household, FishAndVegetarian);
        await FillMeatDaysAsync(x.Admin, x.S.Household, 5);
        var meat = await RecipeAsync(x.Admin, x.S.Household, "Chicken");
        var fish = await RecipeAsync(x.Admin, x.S.Household, "Fish");
        var vegetarian = await RecipeAsync(x.Admin, x.S.Household, "Vegetarian");
        var unknown = await RecipeAsync(x.Admin, x.S.Household, "Unknown");
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);

        using var refused = await AddAsync(x.S.Owner.Client, x.S.Household, meat, 5);
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "pick-not-allowed");
        using var refusedUnknown = await AddAsync(x.S.Owner.Client, x.S.Household, unknown, 5);
        await refusedUnknown.AssertProblemAsync(HttpStatusCode.Conflict, "pick-not-allowed");

        using var fishPick = await AddAsync(x.S.Owner.Client, x.S.Household, fish, 5);
        Assert.Equal(HttpStatusCode.Created, fishPick.StatusCode);
        using var refusedAgain = await AddAsync(x.S.Owner.Client, x.S.Household, meat, 6);
        await refusedAgain.AssertProblemAsync(HttpStatusCode.Conflict, "pick-not-allowed");
        using var vegetarianPick = await AddAsync(x.S.Owner.Client, x.S.Household, vegetarian, 6);
        Assert.Equal(HttpStatusCode.Created, vegetarianPick.StatusCode);

        // An adult is never blocked, the UI only greys the recipe out.
        using var adult = await AddAsync(x.Admin, x.S.Household, meat, 6);
        Assert.Equal(HttpStatusCode.Created, adult.StatusCode);
    }

    [Fact]
    public async Task Without_rules_kids_are_limited_to_dinners_only()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await FillMeatDaysAsync(x.Admin, x.S.Household, 6);
        var meat = await RecipeAsync(x.Admin, x.S.Household, "Beef");
        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);

        using var response = await AddAsync(x.S.Owner.Client, x.S.Household, meat, 6);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Pick_options_say_which_recipes_fit_a_day_and_why_not()
    {
        await using var host = Host();
        var x = await NewAsync(host);
        await SetRulesAsync(x.Admin, x.S.Household, FishAndVegetarian);
        var meat = await RecipeAsync(x.Admin, x.S.Household, "Beef");
        var fish = await RecipeAsync(x.Admin, x.S.Household, "Fish");
        var dessert = await RecipeAsync(x.Admin, x.S.Household, "Beef", dinner: false);

        var early = await OptionsAsync(x.Admin, x.S.Household, 5);
        Assert.Equal(7, early.GetProperty("openDinnerDays").GetInt32());
        Assert.Equal((true, (string?)null), Option(early, meat));

        await FillMeatDaysAsync(x.Admin, x.S.Household, 5);
        var late = await OptionsAsync(x.Admin, x.S.Household, 5);
        Assert.Equal(2, late.GetProperty("openDinnerDays").GetInt32());
        Assert.Equal(["Fish", "Vegetarian"], late.GetProperty("allowedFoodTypes").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal((false, "breaks-meal-mix"), Option(late, meat));
        Assert.Equal((true, (string?)null), Option(late, fish));
        Assert.Equal((true, (string?)null), Option(late, dessert)); // an adult may add a non-dinner: it is not part of the mix

        await x.S.Owner.Client.EnterKidModeAsync(x.S.Household, x.S.Profile1);
        var kid = await OptionsAsync(x.S.Owner.Client, x.S.Household, 5);
        Assert.Equal((false, "breaks-meal-mix"), Option(kid, meat));
        Assert.Equal((true, (string?)null), Option(kid, fish));
        Assert.Equal((false, "not-dinner"), Option(kid, dessert));

        // Deleting a dinner frees a day again.
        using var plan = await x.Admin.GetAsync(Url(x.S.Household));
        var first = (await plan.JsonAsync()).GetProperty("entries")[0].GetProperty("id").GetGuid();
        using var deleted = await x.Admin.DeleteAsync(Url(x.S.Household, $"/entries/{first}"));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal((true, (string?)null), Option(await OptionsAsync(x.Admin, x.S.Household, 5), meat));
    }

    [Fact]
    public async Task Pick_options_validate_the_week_and_the_date()
    {
        await using var host = Host();
        var x = await NewAsync(host);

        using var noDate = await x.Admin.GetAsync(Url(x.S.Household, "/pick-options"));
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);
        using var wrongWeek = await x.Admin.GetAsync(Url(x.S.Household, $"/pick-options?date={Monday.AddDays(8):yyyy-MM-dd}"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongWeek.StatusCode);
    }
}

using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>Likes (T-026): a kid profile likes recipes, adults see and filter on it.</summary>
public sealed class LikeTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private AuthHost Host() => KidHost.Create(db.ConnectionString);

    private static string Like(Guid household, Guid recipe) => $"/api/households/{household}/recipes/{recipe}/like";

    private async Task<int> CountLikesAsync(Guid profile, Guid? recipe = null) =>
        (int)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            "SELECT COUNT(*) FROM RecipeLikes WHERE MemberProfileId = @p AND (@r IS NULL OR RecipeId = @r)",
            ("@p", profile),
            ("@r", (object?)recipe ?? DBNull.Value)))!;

    private static async Task<JsonElement> RecipeAsync(ApiClient client, Guid household, Guid recipe)
    {
        using var response = await client.GetAsync($"/api/households/{household}/recipes/{recipe}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<List<JsonElement>> ListAsync(ApiClient client, Guid household, string query = "")
    {
        using var response = await client.GetAsync($"/api/households/{household}/recipes?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).GetProperty("items").EnumerateArray()];
    }

    [Fact]
    public async Task Kid_can_like_and_unlike()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        var client = s.Owner.Client;

        using var first = await client.PutAsync(Like(s.Household, recipe));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        using var second = await client.PutAsync(Like(s.Household, recipe));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(1, await CountLikesAsync(s.Profile1, recipe));
        Assert.Equal(s.Household, (Guid)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT HouseholdId FROM RecipeLikes WHERE MemberProfileId = @p", ("@p", s.Profile1)))!);

        var detail = await RecipeAsync(client, s.Household, recipe);
        Assert.True(detail.GetProperty("liked").GetBoolean());
        Assert.Equal([s.Profile1], detail.GetProperty("likedBy").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal("Mia", detail.GetProperty("likedBy")[0].GetProperty("displayName").GetString());
        Assert.Equal("fox", detail.GetProperty("likedBy")[0].GetProperty("avatar").GetString());

        using var unlike = await client.DeleteAsync(Like(s.Household, recipe));
        Assert.Equal(HttpStatusCode.NoContent, unlike.StatusCode);
        using var unlikeAgain = await client.DeleteAsync(Like(s.Household, recipe));
        Assert.Equal(HttpStatusCode.NoContent, unlikeAgain.StatusCode);
        Assert.Equal(0, await CountLikesAsync(s.Profile1, recipe));
        Assert.False((await RecipeAsync(client, s.Household, recipe)).GetProperty("liked").GetBoolean());
    }

    [Fact]
    public async Task A_profile_id_in_the_body_or_query_is_ignored_and_parallel_puts_leave_one_row()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        using var forged = await s.Owner.Client.PutAsync(Like(s.Household, recipe) + $"?profileId={s.Profile2}", new { profileId = s.Profile2 });
        Assert.Equal(HttpStatusCode.NoContent, forged.StatusCode);
        Assert.Equal(1, await CountLikesAsync(s.Profile1, recipe));
        Assert.Equal(0, await CountLikesAsync(s.Profile2));

        using var other = host.CreateClient();
        using var login = await other.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        await other.EnterKidModeAsync(s.Household, s.Profile2);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await other.PutAsync(Like(s.Household, recipe));
            return response.StatusCode;
        }));
        Assert.All(results, status => Assert.Equal(HttpStatusCode.NoContent, status));
        Assert.Equal(1, await CountLikesAsync(s.Profile2, recipe));
    }

    [Fact]
    public async Task Like_needs_active_profile()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var adult = await host.JoinAsync(s.Owner, s.Household, "Adult");
        var child = await host.JoinAsync(s.Owner, s.Household, "Child");
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);

        foreach (var user in new[] { s.Owner, adult, child })
        {
            using var put = await user.Client.PutAsync(Like(s.Household, recipe));
            await put.AssertProblemAsync(HttpStatusCode.Conflict, "no-active-profile");
            using var delete = await user.Client.DeleteAsync(Like(s.Household, recipe));
            await delete.AssertProblemAsync(HttpStatusCode.Conflict, "no-active-profile");
        }

        // A stale claim: the profile was deleted from another session.
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var admin = host.CreateClient();
        using var login = await admin.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        using var deleted = await admin.DeleteAsync($"/api/households/{s.Household}/profiles/{s.Profile1}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var stale = await s.Owner.Client.PutAsync(Like(s.Household, recipe));
        await stale.AssertProblemAsync(HttpStatusCode.Conflict, "no-active-profile");

        Assert.Equal(0, (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM RecipeLikes WHERE HouseholdId = @h", ("@h", s.Household)))!);
    }

    [Fact]
    public async Task Likes_are_per_profile()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var shared = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var onlyFirst = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var onlySecond = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var client = s.Owner.Client;

        await client.EnterKidModeAsync(s.Household, s.Profile1);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(Like(s.Household, shared))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(Like(s.Household, onlyFirst))).StatusCode);
        await client.EnterKidModeAsync(s.Household, s.Profile2);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(Like(s.Household, shared))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(Like(s.Household, onlySecond))).StatusCode);

        // Profile 2 sees its own hearts only; both names are on the shared recipe.
        Assert.True((await RecipeAsync(client, s.Household, shared)).GetProperty("liked").GetBoolean());
        Assert.False((await RecipeAsync(client, s.Household, onlyFirst)).GetProperty("liked").GetBoolean());
        Assert.True((await RecipeAsync(client, s.Household, onlySecond)).GetProperty("liked").GetBoolean());
        var names = (await RecipeAsync(client, s.Household, shared)).GetProperty("likedBy").EnumerateArray().Select(p => p.GetProperty("displayName").GetString());
        Assert.Equal(["Mia", "Ola"], names); // ordered by name

        // Unliking as profile 2 keeps profile 1's like.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Like(s.Household, shared))).StatusCode);
        Assert.Equal(1, await CountLikesAsync(s.Profile1, shared));
        Assert.Equal(0, await CountLikesAsync(s.Profile2, shared));
    }

    [Fact]
    public async Task Likes_visible_to_adults_and_filterable()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var both = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var onlyFirst = await s.Owner.Client.CreateRecipeAsync(s.Household);
        var onlySecond = await s.Owner.Client.CreateRecipeAsync(s.Household);
        var nobody = await s.Owner.Client.CreateRecipeAsync(s.Household);
        using var kid = host.CreateClient();
        using var login = await kid.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        await kid.EnterKidModeAsync(s.Household, s.Profile1);
        foreach (var recipe in new[] { both, onlyFirst })
        {
            using var liked = await kid.PutAsync(Like(s.Household, recipe));
            Assert.Equal(HttpStatusCode.NoContent, liked.StatusCode);
        }

        await kid.EnterKidModeAsync(s.Household, s.Profile2);
        foreach (var recipe in new[] { both, onlySecond })
        {
            using var liked = await kid.PutAsync(Like(s.Household, recipe));
            Assert.Equal(HttpStatusCode.NoContent, liked.StatusCode);
        }

        var adult = s.Owner.Client; // a full session
        var items = await ListAsync(adult, s.Household);
        var bothItem = items.Single(i => i.GetProperty("id").GetGuid() == both);
        Assert.Equal(2, bothItem.GetProperty("likedBy").GetArrayLength());
        Assert.False(bothItem.GetProperty("liked").GetBoolean()); // not in kid mode
        Assert.Equal(0, items.Single(i => i.GetProperty("id").GetGuid() == nobody).GetProperty("likedBy").GetArrayLength());
        Assert.Equal(2, (await RecipeAsync(adult, s.Household, both)).GetProperty("likedBy").GetArrayLength());

        static HashSet<Guid> Ids(List<JsonElement> list) => [.. list.Select(i => i.GetProperty("id").GetGuid())];
        Assert.Equal([both, onlyFirst], Ids(await ListAsync(adult, s.Household, $"likedByProfileId={s.Profile1}")));
        Assert.Equal([both], Ids(await ListAsync(adult, s.Household, $"likedByProfileId={s.Profile1}&likedByProfileId={s.Profile2}")));
        Assert.Equal([both], Ids(await ListAsync(adult, s.Household, $"likedByProfileId={s.Profile1}&likedByProfileId={s.Profile2}&tagIds={KidHost.Middag}")));
        Assert.Empty(await ListAsync(adult, s.Household, $"likedByProfileId={Guid.NewGuid()}"));

        var foreign = await host.NewScenarioAsync();
        Assert.Empty(await ListAsync(adult, s.Household, $"likedByProfileId={foreign.Profile1}"));
        using var malformed = await adult.GetAsync($"/api/households/{s.Household}/recipes?likedByProfileId=not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var tooMany = string.Join("&", Enumerable.Range(0, 11).Select(_ => "likedByProfileId=" + Guid.NewGuid()));
        using var many = await adult.GetAsync($"/api/households/{s.Household}/recipes?{tooMany}");
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.True((await many.JsonAsync()).GetProperty("errors").TryGetProperty("likedByProfileId", out _));
    }

    [Fact]
    public async Task Likes_cleaned_up_with_parents()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var h = $"/api/households/{s.Household}";
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var planned = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var week = "2026-10-26";
        using var plan = await s.Owner.Client.PostAsync($"{h}/meal-plans/{week}/entries", new { date = week, recipeId = planned });
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
        using var kid = host.CreateClient();
        using var login = await kid.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        foreach (var profile in new[] { s.Profile1, s.Profile2 })
        {
            await kid.EnterKidModeAsync(s.Household, profile);
            foreach (var r in new[] { recipe, planned })
            {
                using var liked = await kid.PutAsync(Like(s.Household, r));
                Assert.Equal(HttpStatusCode.NoContent, liked.StatusCode);
            }
        }

        // A recipe on a plan is still in use.
        using var inUse = await s.Owner.Client.DeleteAsync($"{h}/recipes/{planned}");
        await inUse.AssertProblemAsync(HttpStatusCode.Conflict, "recipe-in-use");

        using var deletedProfile = await s.Owner.Client.DeleteAsync($"{h}/profiles/{s.Profile1}");
        Assert.Equal(HttpStatusCode.NoContent, deletedProfile.StatusCode);
        Assert.Equal(0, await CountLikesAsync(s.Profile1));
        Assert.Equal(2, await CountLikesAsync(s.Profile2));

        using var deletedRecipe = await s.Owner.Client.DeleteAsync($"{h}/recipes/{recipe}");
        Assert.Equal(HttpStatusCode.NoContent, deletedRecipe.StatusCode);
        Assert.Equal(1, await CountLikesAsync(s.Profile2));
        Assert.Equal(1, await CountLikesAsync(s.Profile2, planned));
    }

    [Fact]
    public async Task Likes_isolated_between_households()
    {
        await using var host = Host();
        var a = await host.NewScenarioAsync();
        var b = await host.NewScenarioAsync();
        var foreignRecipe = await b.Owner.Client.CreateRecipeAsync(b.Household, KidHost.Middag);
        await a.Owner.Client.EnterKidModeAsync(a.Household, a.Profile1);

        using var put = await a.Owner.Client.PutAsync(Like(a.Household, foreignRecipe));
        await put.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        using var delete = await a.Owner.Client.DeleteAsync(Like(a.Household, foreignRecipe));
        await delete.AssertProblemAsync(HttpStatusCode.NotFound, "recipe-not-found");
        using var viaB = await a.Owner.Client.PutAsync(Like(b.Household, foreignRecipe)); // a kid cannot step into household B either
        Assert.Equal(HttpStatusCode.Forbidden, viaB.StatusCode);

        Assert.Equal(0, await CountLikesAsync(a.Profile1));
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }
}

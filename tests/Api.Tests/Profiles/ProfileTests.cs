using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.TenantIsolation;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>Kid profile management (T-026): roles, validation, tenancy, household delete and export.</summary>
public sealed class ProfileTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private AuthHost Host() => KidHost.Create(db.ConnectionString);

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/profiles{rest}";

    private async Task<int> CountAsync(string table, string where, params (string Name, object Value)[] parameters) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {where}", parameters))!;

    [Fact]
    public async Task Profile_crud_roles()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var stranger = await host.SignInAsync();

        foreach (var (user, name) in new[] { (owner, "Owner"), (adult, "Adult") })
        {
            using var created = await user.Client.PostAsync(Url(household), new { displayName = name + " kid", avatar = "bear" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.JsonAsync()).GetProperty("id").GetGuid();
            using var renamed = await user.Client.PutAsync(Url(household, $"/{id}"), new { displayName = name + " kid 2", avatar = "cat" });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            var body = await renamed.JsonAsync();
            Assert.Equal(name + " kid 2", body.GetProperty("displayName").GetString());
            Assert.Equal("cat", body.GetProperty("avatar").GetString());
            using var deleted = await user.Client.DeleteAsync(Url(household, $"/{id}"));
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var existing = await owner.Client.CreateProfileAsync(household, "Existing");
        await owner.Client.SetPinAsync();

        // A Child member and a non-member can neither create, change nor delete; a kid-mode session cannot either.
        foreach (var other in new[] { child, stranger })
        {
            await AssertManagementRefusedAsync(other.Client, household, existing);
        }

        await owner.Client.EnterKidModeAsync(household, existing);
        await AssertManagementRefusedAsync(owner.Client, household, existing);

        // Listing is for every member and kid mode, not for outsiders or anonymous callers.
        foreach (var viewer in new[] { child, owner })
        {
            using var list = await viewer.Client.GetAsync(Url(household));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Single((await list.JsonAsync()).EnumerateArray());
        }

        using var outsider = await stranger.Client.GetAsync(Url(household));
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        using var anonymous = host.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(Url(household));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(1, await CountAsync("MemberProfiles", "HouseholdId = @h", ("@h", household)));
    }

    private static async Task AssertManagementRefusedAsync(ApiClient client, Guid household, Guid profile)
    {
        using var create = await client.PostAsync(Url(household), new { displayName = "Nope", avatar = "fox" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        using var rename = await client.PutAsync(Url(household, $"/{profile}"), new { displayName = "Nope", avatar = "fox" });
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        using var delete = await client.DeleteAsync(Url(household, $"/{profile}"));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Profile_validation()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var mia = await owner.Client.CreateProfileAsync(household, "Mia");
        var zed = await owner.Client.CreateProfileAsync(household, "Zed");

        foreach (var (body, key) in new (object, string)[]
        {
            (new { displayName = "", avatar = "fox" }, "displayName"),
            (new { displayName = "   ", avatar = "fox" }, "displayName"),
            (new { displayName = new string('x', 31), avatar = "fox" }, "displayName"),
            (new { displayName = "Ok", avatar = "dragon" }, "avatar"),
            (new { displayName = "Ok", avatar = "Fox" }, "avatar"),
            (new { displayName = "Ok", avatar = (string?)null }, "avatar"),
        })
        {
            using var response = await owner.Client.PostAsync(Url(household), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(key, out _), $"expected an error for {key}");
        }

        using var duplicate = await owner.Client.PostAsync(Url(household), new { displayName = "mIA", avatar = "owl" });
        await duplicate.AssertProblemAsync(HttpStatusCode.Conflict, "profile-exists");

        for (var i = 0; i < 8; i++)
        {
            await owner.Client.CreateProfileAsync(household);
        }

        using var eleventh = await owner.Client.PostAsync(Url(household), new { displayName = "Eleven", avatar = "owl" });
        await eleventh.AssertProblemAsync(HttpStatusCode.Conflict, "profile-limit");
        Assert.Equal(10, await CountAsync("MemberProfiles", "HouseholdId = @h", ("@h", household)));

        // Renaming onto another profile's name (any case) is refused; keeping the own name is fine; a bad avatar is 400.
        using var clash = await owner.Client.PutAsync(Url(household, $"/{zed}"), new { displayName = "MIA", avatar = "cat" });
        await clash.AssertProblemAsync(HttpStatusCode.Conflict, "profile-exists");
        using var same = await owner.Client.PutAsync(Url(household, $"/{mia}"), new { displayName = "mia", avatar = "panda" });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        using var badAvatar = await owner.Client.PutAsync(Url(household, $"/{mia}"), new { displayName = "Mia", avatar = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badAvatar.StatusCode);
        Assert.Equal(0, await CountAsync("MemberProfiles", "HouseholdId = @h AND DisplayName = 'Eleven'", ("@h", household)));
    }

    [Fact]
    public async Task Profiles_are_per_household_and_the_same_name_can_exist_in_two()
    {
        await using var host = Host();
        var a = await host.SignInAsync();
        var b = await host.SignInAsync();
        var householdA = await a.Client.CreateHouseholdAsync();
        var householdB = await b.Client.CreateHouseholdAsync();
        var profileA = await a.Client.CreateProfileAsync(householdA, "Mia");
        var profileB = await b.Client.CreateProfileAsync(householdB, "Mia");

        using var listA = await a.Client.GetAsync(Url(householdA));
        var idsA = (await listA.JsonAsync()).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([profileA], idsA);
        Assert.DoesNotContain(profileB, idsA);

        // B's profile through A's route, and A's household through B (not a member): refused.
        using var viaA = await a.Client.PutAsync(Url(householdA, $"/{profileB}"), new { displayName = "Hijack", avatar = "cat" });
        await viaA.AssertProblemAsync(HttpStatusCode.NotFound, "profile-not-found");
        using var intoB = await a.Client.PostAsync(Url(householdB), new { displayName = "Intruder", avatar = "cat" });
        Assert.Equal(HttpStatusCode.Forbidden, intoB.StatusCode);
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Household_delete_and_export_include_profiles()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var other = await host.NewScenarioAsync();
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var liked = await s.Owner.Client.PutAsync($"/api/households/{s.Household}/recipes/{recipe}/like");
        Assert.Equal(HttpStatusCode.NoContent, liked.StatusCode);
        using var unlocked = await s.Owner.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);

        using var export = await s.Owner.Client.GetAsync($"/api/households/{s.Household}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var entities = (await export.JsonAsync()).GetProperty("entities");
        var names = entities.GetProperty("MemberProfile").EnumerateArray().Select(p => p.GetProperty("displayName").GetString()).Order().ToList();
        Assert.Equal(["Mia", "Ola"], names);
        var exported = entities.GetProperty("MemberProfile")[0];
        Assert.True(exported.TryGetProperty("avatar", out _));
        Assert.Single(entities.GetProperty("RecipeLike").EnumerateArray());

        using var deleted = await s.Owner.Client.DeleteAsync($"/api/households/{s.Household}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync("MemberProfiles", "HouseholdId = @h", ("@h", s.Household)));
        Assert.Equal(0, await CountAsync("RecipeLikes", "HouseholdId = @h", ("@h", s.Household)));
        Assert.Equal(2, await CountAsync("MemberProfiles", "HouseholdId = @h", ("@h", other.Household)));
    }

    [Fact]
    public async Task Deleting_profile_keeps_history()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var h = $"/api/households/{s.Household}";
        var dinner = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        var week = Monday.ToString("yyyy-MM-dd");
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var pick = await s.Owner.Client.PostAsync($"{h}/meal-plans/{week}/entries", new { date = week, recipeId = dinner });
        Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        var entry = (await pick.JsonAsync()).GetProperty("id").GetGuid();
        using var unlocked = await s.Owner.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);

        using var deleted = await s.Owner.Client.DeleteAsync($"{h}/profiles/{s.Profile1}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var plan = await s.Owner.Client.GetAsync($"{h}/meal-plans/{week}");
        var entries = (await plan.JsonAsync()).GetProperty("entries");
        var kept = Assert.Single(entries.EnumerateArray());
        Assert.Equal(entry, kept.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("pickedByProfile").ValueKind);
        Assert.Equal(s.Owner.User.Id, kept.GetProperty("pickedBy").GetProperty("userId").GetGuid());
        Assert.Equal(0, await CountAsync("MemberProfiles", "Id = @p", ("@p", s.Profile1)));
        Assert.Equal(1, await CountAsync("MemberProfiles", "Id = @p", ("@p", s.Profile2)));
    }
}

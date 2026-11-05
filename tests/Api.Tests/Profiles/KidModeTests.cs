using System.Net;
using System.Text.RegularExpressions;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>Kid mode (T-026): what a device locked to a kid profile can and cannot do, and how it gets in and out.</summary>
public sealed partial class KidModeTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private AuthHost Host(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null) =>
        KidHost.Create(db.ConnectionString, configure);

    private async Task<int> CountAsync(string table, Guid household) =>
        (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM {table} WHERE HouseholdId = @h", ("@h", household)))!;

    [GeneratedRegex(@"\{(?<name>\w+)(:(?<constraint>\w+))?\??\}")]
    private static partial Regex RouteParameter();

    private static string BuildUrl(string template, Guid household)
    {
        var path = RouteParameter().Replace(template, m => m.Groups["name"].Value switch
        {
            "householdId" => household.ToString(),
            "weekStart" => Monday.ToString("yyyy-MM-dd"),
            var _ when m.Groups["constraint"].Value == "guid" => Guid.NewGuid().ToString(),
            _ => "x",
        });
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>A multipart endpoint is only routed for a multipart body, so it gets one; every other call has an empty JSON body.</summary>
    private static Task<HttpResponseMessage> CallAsync(ApiClient client, RouteEndpoint endpoint, string method, string url) =>
        endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes.Contains("multipart/form-data") == true
            ? client.PutFileAsync(url, [1, 2, 3])
            : client.SendAsync(new HttpMethod(method), url);

    private static List<(RouteEndpoint Endpoint, string Method, string Policy)> RoleProtectedEndpoints(AuthHost host) =>
    [
        .. host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
                    .Where(p => p is Policies.HouseholdAdult or Policies.HouseholdOwner or Policies.FullSession).Cast<string>().OrderBy(p => p == Policies.FullSession ? 0 : 1).ToList();
                var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
                return policies.Count == 0 ? [] : methods.Select(m => (e, m, policies[0]));
            }),
    ];

    [Fact]
    public async Task Kid_mode_blocks_every_adult_and_owner_endpoint()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var endpoints = RoleProtectedEndpoints(host);
        Assert.True(endpoints.Count >= 30, $"found {endpoints.Count} role-protected endpoints"); // not vacuous
        Assert.Contains(endpoints, e => e.Policy == Policies.FullSession);
        Assert.Contains(endpoints, e => e.Policy == Policies.HouseholdOwner);

        var before = await CountsAsync(s.Household);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        foreach (var (endpoint, method, policy) in endpoints)
        {
            var url = BuildUrl(endpoint.RoutePattern.RawText!, s.Household);
            using var response = await CallAsync(s.Owner.Client, endpoint, method, url);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {endpoint.RoutePattern.RawText} -> {(int)response.StatusCode}");
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, policy == Policies.FullSession ? "kid-mode" : "forbidden");
        }

        Assert.Equal(before, await CountsAsync(s.Household)); // nothing was written, nothing deleted
    }

    [Fact]
    public async Task The_same_endpoints_called_by_the_owner_without_kid_mode_are_not_refused_by_policy()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var destructive = new[] { "DELETE /api/households/{householdId:guid}", "POST /api/households/{householdId:guid}/leave" };
        static string Key(string method, RouteEndpoint e) => $"{method} {e.RoutePattern.RawText!.TrimEnd('/')}";

        foreach (var (endpoint, method, _) in RoleProtectedEndpoints(host))
        {
            if (destructive.Contains(Key(method, endpoint)))
            {
                continue; // they work, and would end the household this loop uses; covered by the household tests
            }

            using var response = await CallAsync(s.Owner.Client, endpoint, method, BuildUrl(endpoint.RoutePattern.RawText!, s.Household));
            Assert.True(
                response.StatusCode != HttpStatusCode.Forbidden,
                $"{method} {endpoint.RoutePattern.RawText} returned 403 for an Owner outside kid mode");
        }
    }

    private async Task<string> CountsAsync(Guid household)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "Recipes", "Memberships", "Invites", "MemberProfiles", "Stores", "Tags", "Ingredients" })
        {
            parts.Add($"{table}={await CountAsync(table, household)}");
        }

        parts.Add($"Households={await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM Households WHERE Id = @h", ("@h", household))}");
        return string.Join(",", parts);
    }

    [Fact]
    public async Task Child_profile_cannot_call_adult_only_endpoints()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var h = $"/api/households/{s.Household}";
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        var client = s.Owner.Client;

        var calls = new List<Task<HttpResponseMessage>>
        {
            client.PostAsync($"{h}/recipes", new { title = "x", servings = 2, ingredients = Array.Empty<object>() }),
            client.PutFileAsync($"{h}/recipes/{recipe}/image", [1, 2, 3]),
            client.PostAsync($"{h}/imports", new { source = "Text", text = "2 dl melk" }),
            client.PutAsync($"{h}/ingredients/{Guid.NewGuid()}", new { name = "x" }),
            client.PostAsync($"{h}/tags", new { name = "x", kind = "Tag" }),
            client.GetAsync($"{h}/export"),
            client.PostAsync($"{h}/invites", new { role = "Child" }),
            client.PutAsync($"{h}/members/{Guid.NewGuid()}/role", new { role = "Child" }),
            client.DeleteAsync(h),
            client.PostAsync($"{h}/leave"),
            client.PostAsync("/api/households", new { name = "Another" }),
        };
        foreach (var call in calls)
        {
            using var response = await call;
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        // The household and the recipe are still there.
        Assert.Equal(1, await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM Households WHERE Id = @h", ("@h", s.Household)) is int n ? n : -1);
        Assert.Equal(1, await CountAsync("Recipes", s.Household));
    }

    [Fact]
    public async Task Kid_mode_can_use_member_endpoints()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var h = $"/api/households/{s.Household}";
        var recipe = await s.Owner.Client.CreateRecipeAsync(s.Household, KidHost.Middag);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        var client = s.Owner.Client;
        var week = Monday.ToString("yyyy-MM-dd");

        using var recipes = await client.GetAsync($"{h}/recipes");
        Assert.Equal(HttpStatusCode.OK, recipes.StatusCode);
        using var plan = await client.GetAsync($"{h}/meal-plans/{week}");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        using var pick = await client.PostAsync($"{h}/meal-plans/{week}/entries", new { date = week, recipeId = recipe });
        Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        using var list = await client.GetAsync($"{h}/shopping-lists/{week}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var added = await client.PostAsync($"{h}/shopping-lists/{week}/items", new { label = "Milk" }); // read only since T-105
        await added.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        using var pantry = await client.GetAsync($"{h}/pantry");
        Assert.Equal(HttpStatusCode.OK, pantry.StatusCode);
        using var profiles = await client.GetAsync($"{h}/profiles");
        Assert.Equal(HttpStatusCode.OK, profiles.StatusCode);
    }

    [Fact]
    public async Task Effective_role_is_child_in_the_profiles_household()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync(); // Owner of A
        var other = await host.SignInAsync();
        var b = await other.Client.CreateHouseholdAsync("Other");
        var token = await other.Client.CreateInviteAsync(b, "Adult");
        using var accepted = await s.Owner.Client.AcceptAsync(token); // Adult of B
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var before = await s.Owner.Client.ListHouseholdsAsync();
        Assert.Contains(before, x => x.Id == s.Household && x.Role == "Owner");
        Assert.Contains(before, x => x.Id == b && x.Role == "Adult");

        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        foreach (var household in new[] { s.Household, b })
        {
            using var adultOnly = await s.Owner.Client.PostAsync($"/api/households/{household}/tags", new { name = "x", kind = "Tag" });
            await adultOnly.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // Q-026 / T-103: B is not reachable in kid mode (full coverage in KidModeOwnHouseholdTests).
        var during = Assert.Single(await s.Owner.Client.ListHouseholdsAsync());
        Assert.Equal((s.Household, "Child"), (during.Id, during.Role));
        var me = await s.Owner.Client.MeAsync();
        Assert.Equal(s.Profile1, me.GetProperty("kidMode").GetProperty("profileId").GetGuid());
        Assert.Equal(s.Household, me.GetProperty("kidMode").GetProperty("householdId").GetGuid());
    }

    [Fact]
    public async Task Kid_mode_survives_cookie_refresh()
    {
        // A validation interval of zero makes Identity rebuild the principal on EVERY request, which is what happens once the
        // real interval has passed: without the kid claims being carried over, the device would unlock itself.
        await using var host = Host(services => services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero));
        var s = await host.NewScenarioAsync();
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        for (var i = 0; i < 3; i++)
        {
            var me = await s.Owner.Client.MeAsync();
            Assert.Equal(s.Profile1, me.GetProperty("kidMode").GetProperty("profileId").GetGuid());
            using var adultOnly = await s.Owner.Client.PostAsync($"/api/households/{s.Household}/tags", new { name = "x", kind = "Tag" });
            await adultOnly.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Kid_claims_cannot_be_forged()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var h = $"/api/households/{s.Household}";

        // A tampered cookie is no cookie.
        var cookie = s.Owner.Client.AuthCookie!;
        var tampered = cookie[..^3] + (cookie.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");
        using var forged = host.CreateClient();
        using var anonymous = await forged.GetAsync("/api/auth/me", new Dictionary<string, string> { ["Cookie"] = tampered });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // A full session cannot enter kid mode by sending look-alike input, and a kid session cannot leave it that way.
        var headers = new Dictionary<string, string> { ["X-Profile"] = s.Profile1.ToString(), ["X-Kid-Mode"] = "true" };
        using var stillFull = await s.Owner.Client.GetAsync($"{h}/profiles", headers);
        Assert.Equal(HttpStatusCode.OK, stillFull.StatusCode);
        Assert.Equal(JsonValueKindNull, (await s.Owner.Client.MeAsync()).GetProperty("kidMode").ValueKind);
        using var tag = await s.Owner.Client.PostAsync($"{h}/tags", new { name = "ok" + Guid.NewGuid().ToString("N")[..6], kind = "Tag", profileId = s.Profile1 });
        Assert.Equal(HttpStatusCode.Created, tag.StatusCode);

        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var noExit = await s.Owner.Client.PostAsync($"{h}/tags", new { name = "no", kind = "Tag" }, new Dictionary<string, string> { ["X-Kid-Mode"] = "false" });
        await noExit.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Activate_requires_pin_set()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var profile = await owner.Client.CreateProfileAsync(household);

        using var refused = await owner.Client.ActivateAsync(household, profile);
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "pin-not-set");
        Assert.Empty(refused.SetCookies());
        Assert.Equal(JsonValueKindNull, (await owner.Client.MeAsync()).GetProperty("kidMode").ValueKind);

        await owner.Client.SetPinAsync();
        using var ok = await owner.Client.ActivateAsync(household, profile);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Contains(ok.SetCookies(), c => c.StartsWith("__Host-foodplanner=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Kid_to_kid_switch_needs_no_pin()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        using var switched = await s.Owner.Client.ActivateAsync(s.Household, s.Profile2);

        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);
        var me = await s.Owner.Client.MeAsync();
        Assert.Equal(s.Profile2, me.GetProperty("kidMode").GetProperty("profileId").GetGuid());
        using var adultOnly = await s.Owner.Client.PostAsync($"/api/households/{s.Household}/tags", new { name = "x", kind = "Tag" });
        Assert.Equal(HttpStatusCode.Forbidden, adultOnly.StatusCode);
    }

    [Fact]
    public async Task Activating_on_one_device_leaves_the_accounts_other_session_alone()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        using var other = host.CreateClient();
        using var login = await other.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        Assert.Equal(JsonValueKindNull, (await other.MeAsync()).GetProperty("kidMode").ValueKind);
        using var adult = await other.PostAsync($"/api/households/{s.Household}/tags", new { name = "t" + Guid.NewGuid().ToString("N")[..6], kind = "Tag" });
        Assert.Equal(HttpStatusCode.Created, adult.StatusCode);
    }

    [Fact]
    public async Task Unknown_or_foreign_profile_is_404()
    {
        await using var host = Host();
        var a = await host.NewScenarioAsync();
        var b = await host.NewScenarioAsync();
        var foreign = b.Profile1;
        var h = $"/api/households/{a.Household}";

        using var activate = await a.Owner.Client.ActivateAsync(a.Household, foreign);
        await activate.AssertProblemAsync(HttpStatusCode.NotFound, "profile-not-found");
        using var put = await a.Owner.Client.PutAsync($"{h}/profiles/{foreign}", new { displayName = "Hijack", avatar = "cat" });
        await put.AssertProblemAsync(HttpStatusCode.NotFound, "profile-not-found");
        using var delete = await a.Owner.Client.DeleteAsync($"{h}/profiles/{foreign}");
        await delete.AssertProblemAsync(HttpStatusCode.NotFound, "profile-not-found");
        using var unknown = await a.Owner.Client.ActivateAsync(a.Household, Guid.NewGuid());
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "profile-not-found");

        Assert.Equal(1, await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM MemberProfiles WHERE Id = @p AND DisplayName = 'Mia'", ("@p", foreign)) is int n ? n : -1);
        await using var context = Foodplanner.Api.Tests.Persistence.TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await TenantIsolation.CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Profile_deleted_elsewhere_keeps_the_device_locked()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var h = $"/api/households/{s.Household}";
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var second = host.CreateClient();
        using var login = await second.LoginAsync(s.Owner.User.Email, AuthHost.Password);
        using var deleted = await second.DeleteAsync($"{h}/profiles/{s.Profile1}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The kid device: still locked as Child, kid mode still shown (the client sees the profile is gone from the list).
        var me = await s.Owner.Client.MeAsync();
        Assert.Equal(s.Profile1, me.GetProperty("kidMode").GetProperty("profileId").GetGuid());
        using var adultOnly = await s.Owner.Client.PostAsync($"{h}/tags", new { name = "x", kind = "Tag" });
        Assert.Equal(HttpStatusCode.Forbidden, adultOnly.StatusCode);
        using var list = await s.Owner.Client.GetAsync($"{h}/profiles");
        Assert.DoesNotContain(
            (await list.JsonAsync()).EnumerateArray(), p => p.GetProperty("id").GetGuid() == s.Profile1);

        // New picks have no profile.
        var dinner = await second.CreateRecipeAsync(s.Household, KidHost.Middag);
        var week = Monday.ToString("yyyy-MM-dd");
        using var pick = await s.Owner.Client.PostAsync($"{h}/meal-plans/{week}/entries", new { date = week, recipeId = dinner });
        Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null, (await pick.JsonAsync()).GetProperty("pickedByProfile").ValueKind);
        Assert.Equal(
            DBNull.Value,
            await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT PickedByProfileId FROM MealPlanEntries WHERE Id = @e", ("@e", (await pick.JsonAsync()).GetProperty("id").GetGuid())));
    }

    [Fact]
    public async Task Hub_connections_of_the_account_are_closed_on_activate_and_unlock()
    {
        var live = new RecordingLiveConnections();
        await using var host = Host(services =>
        {
            services.AddSingleton(live);
            services.AddSingleton<Foodplanner.Api.Realtime.ILiveConnections>(live);
        });
        var s = await host.NewScenarioAsync();
        live.Closed.Clear();

        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        Assert.Equal([s.Owner.User.Id], live.Closed);

        using var unlocked = await s.Owner.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);
        Assert.Equal([s.Owner.User.Id, s.Owner.User.Id], live.Closed);
    }

    [Fact]
    public async Task A_hub_connection_with_the_kid_cookie_joins_its_own_household_only()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var stranger = await host.SignInAsync();
        var foreign = await stranger.Client.CreateHouseholdAsync("Foreign");
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using var response = await s.Owner.Client.GetAsync("/api/auth/me");
        var cookie = LatestCookie(s.Owner.Client);

        await using var hub = new Foodplanner.Api.Tests.ShoppingLists.HubTestClient(host, cookie);
        await hub.StartAsync();
        await hub.JoinAsync(s.Household);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => hub.JoinAsync(foreign));
        Assert.Contains("forbidden", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The kid cookie, as the cookie jar holds it, for a hub connection that does not share the jar.</summary>
    private static string LatestCookie(ApiClient client) => client.AuthCookie ?? throw new InvalidOperationException("no cookie");

    [Fact]
    public async Task Account_endpoints_require_full_session_or_are_allow_listed()
    {
        await using var host = Host();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal)
                && !e.RoutePattern.RawText!.StartsWith("/api/hubs/", StringComparison.Ordinal))
            .ToList();
        string[] allowList =
        [
            "/api/health", "/api/health/ready",
            "/api/auth/antiforgery", "/api/auth/register", "/api/auth/login", "/api/auth/confirm-email", "/api/auth/logout", "/api/auth/me",
            "/api/auth/pin/unlock", "/api/households", "/api/units",
        ];

        Assert.True(endpoints.Count > 50);
        var offenders = endpoints.Where(e =>
        {
            var path = e.RoutePattern.RawText!.TrimEnd('/');
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            var householdScoped = path.StartsWith("/api/households/{householdId:guid}", StringComparison.Ordinal);
            var isListHouseholds = path == "/api/households" && methods.All(m => m == "GET");
            return !householdScoped && !policies.Contains(Policies.FullSession) && !allowList.Contains(path)
                || (path == "/api/households" && !isListHouseholds && !policies.Contains(Policies.FullSession));
        }).Select(e => e.DisplayName).ToList();
        Assert.Empty(offenders);

        // The household-scoped ones all carry the household policy (the group's default).
        Assert.All(
            endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("/api/households/{householdId:guid}", StringComparison.Ordinal)),
            e => Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == Policies.HouseholdMember));
    }
}

/// <summary>Records which users' hub connections were closed (T-026: activate and unlock).</summary>
internal sealed class RecordingLiveConnections : Foodplanner.Api.Realtime.ILiveConnections
{
    public List<Guid> Closed { get; } = [];

    public Task EvictUserFromHouseholdAsync(Guid userId, Guid householdId) => Task.CompletedTask;

    public Task CloseHouseholdAsync(Guid householdId) => Task.CompletedTask;

    public Task CloseUserAsync(Guid userId)
    {
        lock (Closed)
        {
            Closed.Add(userId);
        }

        return Task.CompletedTask;
    }
}

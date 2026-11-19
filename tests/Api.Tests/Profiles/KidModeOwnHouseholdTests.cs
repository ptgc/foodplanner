using System.Net;
using System.Security.Claims;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>T-103 / Q-026: in kid mode the account reaches only the profile's household; the others answer as for a non-member.</summary>
public sealed partial class KidModeTests
{
    private static List<(RouteEndpoint Endpoint, string Method)> HouseholdScopedEndpoints(AuthHost host) =>
    [
        .. host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/households/{householdId:guid}", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (e, m))),
    ];

    /// <summary>The scenario's owner (Owner of A, with the profiles) is also an Adult in household B owned by someone else.</summary>
    private static async Task<(Guid B, SignedInUser Stranger)> JoinSecondHouseholdAsync(AuthHost host, KidScenario s)
    {
        var other = await host.SignInAsync();
        var b = await other.Client.CreateHouseholdAsync("Grandparents");
        var token = await other.Client.CreateInviteAsync(b, "Adult");
        using var accepted = await s.Owner.Client.AcceptAsync(token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return (b, await host.SignInAsync());
    }

    [Fact]
    public async Task Kid_mode_reaches_only_the_profiles_household_and_unlock_restores_the_rest()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var (b, stranger) = await JoinSecondHouseholdAsync(host, s);
        var endpoints = HouseholdScopedEndpoints(host);
        Assert.True(endpoints.Count >= 30, $"found {endpoints.Count} household-scoped endpoints"); // not vacuous

        // Outside kid mode the account is Adult in B and both households are listed.
        Assert.Equal(2, (await s.Owner.Client.ListHouseholdsAsync()).Count);
        using (var before = await s.Owner.Client.GetAsync($"/api/households/{b}/recipes"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        foreach (var (endpoint, method) in endpoints)
        {
            var url = BuildUrl(endpoint.RoutePattern.RawText!, b);
            using var asKid = await CallAsync(s.Owner.Client, endpoint, method, url);
            using var asStranger = await CallAsync(stranger.Client, endpoint, method, url);
            Assert.True(asStranger.StatusCode == HttpStatusCode.Forbidden, $"{method} {endpoint.RoutePattern.RawText} non-member -> {(int)asStranger.StatusCode}");
            Assert.True(asKid.StatusCode == asStranger.StatusCode, $"{method} {endpoint.RoutePattern.RawText} kid -> {(int)asKid.StatusCode}");
            // Even on a FullSession endpoint the answer for another household is the non-member one, not "kid-mode" (T-105).
            await asKid.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // A still works, at Child level.
        using (var own = await s.Owner.Client.GetAsync($"/api/households/{s.Household}/recipes"))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        using (var adultOnly = await s.Owner.Client.PostAsync($"/api/households/{s.Household}/tags", new { name = "no", kind = "Tag" }))
        {
            await adultOnly.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var listed = await s.Owner.Client.ListHouseholdsAsync();
        var only = Assert.Single(listed);
        Assert.Equal((s.Household, "Child"), (only.Id, only.Role));

        // Hub: A allowed, B refused.
        await using (var hub = new Foodplanner.Api.Tests.ShoppingLists.HubTestClient(host, LatestCookie(s.Owner.Client)))
        {
            await hub.StartAsync();
            await hub.JoinAsync(s.Household);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => hub.JoinAsync(b));
            Assert.Contains("forbidden", failure.Message, StringComparison.Ordinal);
        }

        // The PIN restores everything.
        using var unlocked = await s.Owner.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);
        Assert.Equal(2, (await s.Owner.Client.ListHouseholdsAsync()).Count);
        using var back = await s.Owner.Client.GetAsync($"/api/households/{b}/recipes");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        await using var hubAfter = new Foodplanner.Api.Tests.ShoppingLists.HubTestClient(host, LatestCookie(s.Owner.Client));
        await hubAfter.StartAsync();
        await hubAfter.JoinAsync(b);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)] // claim removed
    public async Task Malformed_or_missing_profile_household_claim_fails_closed(string? profileHousehold)
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        // Rewrite the kid cookie the way only a server bug could: the profile claim stays, the household claim is damaged.
        var format = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).TicketDataFormat;
        var value = LatestCookie(s.Owner.Client)["__Host-foodplanner=".Length..];
        var ticket = format.Unprotect(Uri.UnescapeDataString(value)) ?? throw new InvalidOperationException("cookie not readable");
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("fp:profile-household")!);
        if (profileHousehold is not null)
        {
            identity.AddClaim(new Claim("fp:profile-household", profileHousehold));
        }

        var forged = "__Host-foodplanner=" + format.Protect(ticket);

        var client = host.CreateClient();
        var headers = new Dictionary<string, string> { ["Cookie"] = forged };
        using var own = await client.GetAsync($"/api/households/{s.Household}/recipes", headers);
        await own.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        using var list = await client.GetAsync("/api/households", headers);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, (await list.JsonAsync()).GetArrayLength());

        await using var hub = new Foodplanner.Api.Tests.ShoppingLists.HubTestClient(host, forged);
        await hub.StartAsync();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => hub.JoinAsync(s.Household));
        Assert.Contains("forbidden", failure.Message, StringComparison.Ordinal);
    }
}

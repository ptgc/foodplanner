using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.ShoppingLists;

namespace Foodplanner.Api.Tests.Households;

/// <summary>Member roles and open invites for the Owner (T-048), plus the T-044 review items: logout and export limit.</summary>
public sealed class HouseholdMemberManagementTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private AuthHost Host(params (string Key, string Value)[] settings) => HouseholdHost.Create(db.ConnectionString, settings);

    private static string Url(Guid household, string rest) => $"/api/households/{household}{rest}";

    private static async Task<List<JsonElement>> ListAsync(ApiClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).EnumerateArray()];
    }

    private static async Task<string> RoleOfAsync(ApiClient client, Guid household, Guid userId) =>
        (await ListAsync(client, Url(household, "/members"))).Single(m => m.GetProperty("userId").GetGuid() == userId).GetProperty("role").GetString()!;

    private static Task<HttpResponseMessage> SetRoleAsync(ApiClient client, Guid household, Guid userId, string? role) =>
        client.PutAsync(Url(household, $"/members/{userId}/role"), new { role });

    private async Task<long> OwnerCountAsync(Guid household) =>
        Convert.ToInt64(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h AND Role = 'Owner'", ("@h", household)));

    // ---- members ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Owner_lists_members_with_email_role_and_joined_time()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");

        var members = await ListAsync(owner.Client, Url(household, "/members"));

        Assert.Equal(3, members.Count);
        Assert.Equal([owner.User.Id, adult.User.Id, child.User.Id], members.Select(m => m.GetProperty("userId").GetGuid()));
        var row = members.Single(m => m.GetProperty("userId").GetGuid() == adult.User.Id);
        Assert.Equal(adult.User.Email, row.GetProperty("email").GetString());
        Assert.Equal("Adult", row.GetProperty("role").GetString());
        Assert.True(row.GetProperty("joinedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    [Fact]
    public async Task Role_change_matrix_adult_to_child_child_to_adult_adult_to_owner()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");

        foreach (var role in new[] { "Child", "Adult", "Owner" })
        {
            using var changed = await SetRoleAsync(owner.Client, household, member.User.Id, role);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var body = await changed.JsonAsync();
            Assert.Equal(role, body.GetProperty("role").GetString());
            Assert.Equal(member.User.Email, body.GetProperty("email").GetString());
            Assert.Equal(role, await RoleOfAsync(owner.Client, household, member.User.Id));
        }

        // A role name is matched like the invite role: case-insensitive, no numbers.
        using var lower = await SetRoleAsync(owner.Client, household, member.User.Id, "child");
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
    }

    [Fact]
    public async Task A_role_change_takes_effect_on_the_members_next_request()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");

        using var adultExport = await member.Client.GetAsync(Url(household, "/export"));
        Assert.Equal(HttpStatusCode.OK, adultExport.StatusCode);
        using var toChild = await SetRoleAsync(owner.Client, household, member.User.Id, "Child");
        Assert.Equal(HttpStatusCode.OK, toChild.StatusCode);
        using var childExport = await member.Client.GetAsync(Url(household, "/export"));
        await childExport.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        using var toOwner = await SetRoleAsync(owner.Client, household, member.User.Id, "Owner");
        Assert.Equal(HttpStatusCode.OK, toOwner.StatusCode);
        using var ownerNow = await member.Client.GetAsync(Url(household, "/invites"));
        Assert.Equal(HttpStatusCode.OK, ownerNow.StatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_but_can_once_another_owner_exists()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");

        foreach (var role in new[] { "Adult", "Child" })
        {
            using var refused = await SetRoleAsync(owner.Client, household, owner.User.Id, role);
            await refused.AssertProblemAsync(HttpStatusCode.Conflict, "last-owner");
        }

        Assert.Equal("Owner", await RoleOfAsync(owner.Client, household, owner.User.Id));
        using (var same = await SetRoleAsync(owner.Client, household, owner.User.Id, "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode); // no change is not a demotion
        }

        using (var promoted = await SetRoleAsync(owner.Client, household, member.User.Id, "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        using var demoted = await SetRoleAsync(owner.Client, household, owner.User.Id, "Adult");
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        Assert.Equal(1, await OwnerCountAsync(household));

        // The former owner lost the Owner-only endpoints at once.
        using var now = await owner.Client.GetAsync(Url(household, "/members"));
        await now.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Invalid_role_unknown_member_and_other_households_member_are_problems()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");
        var otherOwner = await host.SignInAsync();
        var other = await otherOwner.Client.CreateHouseholdAsync("Other");
        var otherMember = await host.JoinAsync(otherOwner, other, "Adult");

        foreach (var bad in new string?[] { null, "", "Admin", "2", " Owner", "Superuser" })
        {
            using var response = await SetRoleAsync(owner.Client, household, member.User.Id, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("role", out _));
        }

        Assert.Equal("Adult", await RoleOfAsync(owner.Client, household, member.User.Id));

        using var unknown = await SetRoleAsync(owner.Client, household, Guid.NewGuid(), "Child");
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");

        using var foreign = await SetRoleAsync(owner.Client, household, otherMember.User.Id, "Owner");
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
        Assert.Equal("Adult", await RoleOfAsync(otherOwner.Client, other, otherMember.User.Id));

        // Not listed either, and the other household's owner cannot reach into this one.
        var members = await ListAsync(owner.Client, Url(household, "/members"));
        Assert.DoesNotContain(members, m => m.GetProperty("userId").GetGuid() == otherMember.User.Id);
        using var crossed = await SetRoleAsync(otherOwner.Client, household, member.User.Id, "Owner");
        await crossed.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_once_never_leave_the_household_without_an_owner()
    {
        await using var host = Host();
        for (var round = 0; round < 6; round++)
        {
            var first = await host.SignInAsync();
            var household = await first.Client.CreateHouseholdAsync();
            var second = await host.JoinAsync(first, household, "Adult");
            using (var promoted = await SetRoleAsync(first.Client, household, second.User.Id, "Owner"))
            {
                Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
            }

            var gate = new TaskCompletionSource();
            async Task<HttpStatusCode> DemoteAsync(SignedInUser caller, SignedInUser target)
            {
                await gate.Task;
                using var response = await SetRoleAsync(caller.Client, household, target.User.Id, "Child");
                return response.StatusCode;
            }

            var a = DemoteAsync(first, second);
            var b = DemoteAsync(second, first);
            gate.SetResult();
            var results = await Task.WhenAll(a, b);

            // One demotion wins. The other is refused as last-owner (409) or, if it started after the commit, as no longer an Owner (403).
            Assert.Equal(1, results.Count(r => r == HttpStatusCode.OK));
            Assert.Single(results, r => r is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);
            Assert.Equal(1, await OwnerCountAsync(household));
        }
    }

    [Fact]
    public async Task Removing_a_member_and_changing_that_members_role_at_once_stays_consistent()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");
        var coOwner = await host.JoinAsync(owner, household, "Adult");
        using (var promoted = await SetRoleAsync(owner.Client, household, coOwner.User.Id, "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        var gate = new TaskCompletionSource();
        async Task<HttpStatusCode> RemoveAsync()
        {
            await gate.Task;
            using var response = await owner.Client.DeleteAsync(Url(household, $"/members/{member.User.Id}"));
            return response.StatusCode;
        }

        async Task<HttpStatusCode> PromoteAsync()
        {
            await gate.Task;
            using var response = await SetRoleAsync(coOwner.Client, household, member.User.Id, "Owner");
            return response.StatusCode;
        }

        var remove = RemoveAsync();
        var promote = PromoteAsync();
        gate.SetResult();
        var removed = await remove;
        var promoted2 = await promote;

        // Serialised by the household lock: either the removal came first (promote: 404), or the promotion did (remove: 409,
        // an Owner is not removed). Never a removed Owner and never two successes.
        var count = Convert.ToInt64(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h AND UserId = @u", ("@h", household), ("@u", member.User.Id)));
        Assert.True(
            (removed == HttpStatusCode.NoContent && promoted2 == HttpStatusCode.NotFound && count == 0)
            || (removed == HttpStatusCode.Conflict && promoted2 == HttpStatusCode.OK && count == 1),
            $"remove={removed}, promote={promoted2}, rows={count}");
    }

    [Fact]
    public async Task Owner_removes_a_member_after_demoting_them_and_the_last_owner_cannot_be_removed()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var member = await host.JoinAsync(owner, household, "Adult");
        using (var promoted = await SetRoleAsync(owner.Client, household, member.User.Id, "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        using var ownerRefused = await owner.Client.DeleteAsync(Url(household, $"/members/{member.User.Id}"));
        await ownerRefused.AssertProblemAsync(HttpStatusCode.Conflict, "owner-cannot-be-removed");
        using var demoted = await SetRoleAsync(owner.Client, household, member.User.Id, "Child");
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        using var removed = await owner.Client.DeleteAsync(Url(household, $"/members/{member.User.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // The sole Owner is neither removable (cannot-remove-self) nor demotable (last-owner).
        using var self = await owner.Client.DeleteAsync(Url(household, $"/members/{owner.User.Id}"));
        await self.AssertProblemAsync(HttpStatusCode.Conflict, "cannot-remove-self");
        Assert.Equal(1, await OwnerCountAsync(household));
    }

    // ---- invites ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Open_invites_are_listed_without_token_or_hash_and_closed_ones_are_not()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var openToken = await owner.Client.CreateInviteAsync(household, "Child");
        var usedToken = await owner.Client.CreateInviteAsync(household, "Adult");
        var revokedToken = await owner.Client.CreateInviteAsync(household, "Adult");
        var expiredToken = await owner.Client.CreateInviteAsync(household, "Adult");
        var guest = await host.SignInAsync();
        using (var used = await guest.Client.AcceptAsync(usedToken))
        {
            Assert.Equal(HttpStatusCode.OK, used.StatusCode);
        }

        var before = await ListAsync(owner.Client, Url(household, "/invites"));
        Assert.Equal(3, before.Count);
        var revokedId = await InviteIdAsync(household, revokedToken);
        using (var revoke = await owner.Client.DeleteAsync(Url(household, $"/invites/{revokedId}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        // The two that remain are open; moving the clock past their expiry empties the list.
        var expiredId = await InviteIdAsync(household, expiredToken);
        var openId = await InviteIdAsync(household, openToken);
        var listed = await ListAsync(owner.Client, Url(household, "/invites"));
        Assert.Equal(new[] { expiredId, openId }.Order(), listed.Select(i => i.GetProperty("id").GetGuid()).Order());
        foreach (var invite in listed)
        {
            var names = invite.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(["createdAt", "createdByUserId", "expiresAt", "id", "role"], names);
            Assert.DoesNotContain(openToken, invite.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(owner.User.Id, invite.GetProperty("createdByUserId").GetGuid());
        }

        Assert.Equal("Child", listed.Single(i => i.GetProperty("id").GetGuid() == openId).GetProperty("role").GetString());

        host.Time.Now = host.Time.Now.AddDays(8); // invites last 7 days; the sign-in cookie lasts longer
        Assert.Empty(await ListAsync(owner.Client, Url(household, "/invites")));
    }

    /// <summary>The id of the invite with this token, found by its hash (the API never reveals it).</summary>
    private async Task<Guid> InviteIdAsync(Guid household, string token)
    {
        var hash = Foodplanner.Domain.Tenancy.InviteToken.Hash(token);
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Invites WHERE HouseholdId = @h AND TokenHash = @t";
        command.Parameters.AddWithValue("@h", household);
        command.Parameters.AddWithValue("@t", hash);
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task A_revoked_invite_is_rejected_as_invalid_and_cannot_be_revoked_twice()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var token = await owner.Client.CreateInviteAsync(household, "Adult");
        var keep = await owner.Client.CreateInviteAsync(household, "Adult");
        var id = await InviteIdAsync(household, token);
        var guest = await host.SignInAsync();

        using (var revoke = await owner.Client.DeleteAsync(Url(household, $"/invites/{id}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using var rejected = await guest.Client.AcceptAsync(token);
        await rejected.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        Assert.Empty(await guest.Client.ListHouseholdsAsync());

        using var again = await owner.Client.DeleteAsync(Url(household, $"/invites/{id}"));
        await again.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");

        // Revoking one invite leaves the others alone.
        using var accepted = await guest.Client.AcceptAsync(keep);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // An accepted invite is no longer open: it cannot be revoked (404) and the membership stays.
        var acceptedId = await InviteIdAsync(household, keep);
        using var late = await owner.Client.DeleteAsync(Url(household, $"/invites/{acceptedId}"));
        await late.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");
        Assert.Single(await guest.Client.ListHouseholdsAsync());
    }

    [Fact]
    public async Task Another_households_invites_are_never_listed_and_cannot_be_revoked()
    {
        await using var host = Host();
        var ownerA = await host.SignInAsync();
        var a = await ownerA.Client.CreateHouseholdAsync("A");
        var ownerB = await host.SignInAsync();
        var b = await ownerB.Client.CreateHouseholdAsync("B");
        var tokenA = await ownerA.Client.CreateInviteAsync(a, "Adult");
        await ownerB.Client.CreateInviteAsync(b, "Child");
        var idA = await InviteIdAsync(a, tokenA);

        var listedB = await ListAsync(ownerB.Client, Url(b, "/invites"));
        Assert.Single(listedB);
        Assert.DoesNotContain(listedB, i => i.GetProperty("id").GetGuid() == idA);

        // B's Owner uses A's invite id inside B (404) or A's household (403): either way A's invite is untouched.
        using var inside = await ownerB.Client.DeleteAsync(Url(b, $"/invites/{idA}"));
        await inside.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");
        using var outside = await ownerB.Client.DeleteAsync(Url(a, $"/invites/{idA}"));
        await outside.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var list = await ownerB.Client.GetAsync(Url(a, "/invites"));
        await list.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        var guest = await host.SignInAsync();
        using var accepted = await guest.Client.AcceptAsync(tokenA);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Adult_and_child_get_403_on_every_member_and_invite_endpoint_and_nothing_changes()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var token = await owner.Client.CreateInviteAsync(household, "Adult");
        var inviteId = await InviteIdAsync(household, token);

        foreach (var caller in new[] { adult, child })
        {
            using var members = await caller.Client.GetAsync(Url(household, "/members"));
            await members.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var role = await SetRoleAsync(caller.Client, household, caller.User.Id, "Owner");
            await role.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var remove = await caller.Client.DeleteAsync(Url(household, $"/members/{owner.User.Id}"));
            await remove.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var invites = await caller.Client.GetAsync(Url(household, "/invites"));
            await invites.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var revoke = await caller.Client.DeleteAsync(Url(household, $"/invites/{inviteId}"));
            await revoke.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using var anonymous = await host.CreateClient().GetAsync(Url(household, "/members"));
        await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Equal("Adult", await RoleOfAsync(owner.Client, household, adult.User.Id));
        Assert.Equal("Child", await RoleOfAsync(owner.Client, household, child.User.Id));
        Assert.Single(await ListAsync(owner.Client, Url(household, "/invites")));
    }

    // ---- T-044 review items -----------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_closes_the_users_live_connections_and_only_theirs()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var household = await user.Client.CreateHouseholdAsync();
        var bystander = await host.SignInAsync();
        var elsewhere = await bystander.Client.CreateHouseholdAsync("Bystander");
        await using var joined = new HubTestClient(host, user.Client.AuthCookie);
        await using var connectedOnly = new HubTestClient(host, user.Client.AuthCookie);
        await using var unrelated = new HubTestClient(host, bystander.Client.AuthCookie);
        foreach (var hub in new[] { joined, connectedOnly, unrelated })
        {
            await hub.StartAsync();
        }

        await joined.JoinAsync(household);
        await unrelated.JoinAsync(elsewhere);

        using var logout = await user.Client.PostAsync("/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.True(await joined.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await connectedOnly.ClosesWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await unrelated.ClosesWithinAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Export_is_rate_limited_per_user()
    {
        await using var host = Host(("RateLimiting:Export:PermitLimit", "2"));
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");

        for (var i = 0; i < 2; i++)
        {
            using var ok = await owner.Client.GetAsync(Url(household, "/export"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await owner.Client.GetAsync(Url(household, "/export"));
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.Contains("Retry-After"));

        // Another member (same household, same client address) has their own budget; other endpoints are not affected.
        using var other = await adult.Client.GetAsync(Url(household, "/export"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var members = await owner.Client.GetAsync(Url(household, "/members"));
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
    }
}

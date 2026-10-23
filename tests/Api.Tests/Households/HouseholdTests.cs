using System.Net;
using System.Security.Cryptography;
using System.Text;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Households;
using Foodplanner.Api.Tenancy;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Households;

public sealed class HouseholdTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost Host(params (string Key, string Value)[] settings) => HouseholdHost.Create(db.ConnectionString, settings);

    private static Task<object?> InviteCountAsync(string connectionString, Guid householdId) =>
        HouseholdScenario.ScalarAsync(connectionString, "SELECT COUNT(*) FROM Invites WHERE HouseholdId = @h", ("@h", householdId));

    [Fact]
    public async Task Create_household_makes_caller_owner()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();

        using var created = await owner.Client.PostAsync("/api/households", new { name = "  The Smiths  " });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("The Smiths", body.GetProperty("name").GetString());
        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.Equal($"/api/households/{id}", created.Headers.Location?.ToString());
        var listed = Assert.Single(await owner.Client.ListHouseholdsAsync());
        Assert.Equal((id, "The Smiths", "Owner"), listed);
    }

    [Fact]
    public async Task Create_household_validation()
    {
        await using var host = Host();
        var user = await host.SignInAsync();

        foreach (var name in new[] { "", "   ", new string('x', 101) })
        {
            using var response = await user.Client.PostAsync("/api/households", new { name });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("name", out _));
        }

        using var missing = await user.Client.PostAsync("/api/households", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var boundary = await user.Client.PostAsync("/api/households", new { name = new string('x', 100) });
        Assert.Equal(HttpStatusCode.Created, boundary.StatusCode);
        Assert.Single(await user.Client.ListHouseholdsAsync());

        using var anonymous = host.CreateClient();
        using var denied = await anonymous.PostAsync("/api/households", new { name = "Nope" });
        await denied.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Theory]
    [InlineData("member", new[] { 200, 200, 200, 403, 401 })]
    [InlineData("adult", new[] { 200, 200, 403, 403, 401 })]
    [InlineData("owner", new[] { 200, 403, 403, 403, 401 })]
    public async Task Policy_matrix(string endpoint, int[] expected)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var outsider = await host.SignInAsync();
        using var anonymous = host.CreateClient();

        var callers = new[] { owner.Client, adult.Client, child.Client, outsider.Client, anonymous };
        var statuses = new List<int>();
        foreach (var caller in callers)
        {
            using var response = await caller.GetAsync($"/api/households/{household}/test/{endpoint}");
            statuses.Add((int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                await response.AssertProblemAsync(response.StatusCode, response.StatusCode == HttpStatusCode.Unauthorized ? "unauthenticated" : "forbidden");
            }
        }

        Assert.Equal(expected, statuses);
    }

    [Fact]
    public async Task Only_owner_creates_invites()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var otherOwner = await host.SignInAsync();
        var other = await otherOwner.Client.CreateHouseholdAsync("Other");
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        Assert.Equal(2L, Convert.ToInt64(await InviteCountAsync(db.ConnectionString, household)));

        foreach (var caller in new[] { adult.Client, child.Client })
        {
            using var response = await caller.PostAsync($"/api/households/{household}/invites", new { role = "Child" });
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // The Owner of household A is not an Owner of household B.
        using var crossHousehold = await owner.Client.PostAsync($"/api/households/{other}/invites", new { role = "Child" });
        await crossHousehold.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal(2L, Convert.ToInt64(await InviteCountAsync(db.ConnectionString, household)));
        Assert.Equal(0L, Convert.ToInt64(await InviteCountAsync(db.ConnectionString, other)));
    }

    [Fact]
    public async Task Invite_accept_joins_household()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync("Home");
        var guest = await host.SignInAsync();
        using var invite = await owner.Client.PostAsync($"/api/households/{household}/invites", new { role = "Adult" });
        var invited = await invite.JsonAsync();
        Assert.Equal("Adult", invited.GetProperty("role").GetString());
        Assert.True(invited.GetProperty("expiresAt").GetDateTimeOffset() > host.Time.GetUtcNow().AddDays(6));

        using var accepted = await guest.Client.AcceptAsync(invited.GetProperty("token").GetString()!);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.JsonAsync();
        Assert.Equal(household, body.GetProperty("householdId").GetGuid());
        Assert.Equal("Adult", body.GetProperty("role").GetString());
        Assert.Equal((household, "Home", "Adult"), Assert.Single(await guest.Client.ListHouseholdsAsync()));
        using var inHousehold = await guest.Client.GetAsync($"/api/households/{household}/test/adult");
        Assert.Equal(HttpStatusCode.OK, inHousehold.StatusCode);
    }

    [Fact]
    public async Task Used_invite_rejected()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var token = await owner.Client.CreateInviteAsync(household, "Child");
        var first = await host.SignInAsync();
        var second = await host.SignInAsync();
        using var ok = await first.Client.AcceptAsync(token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var again = await second.Client.AcceptAsync(token);

        await again.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        Assert.Empty(await second.Client.ListHouseholdsAsync());
        // The member who used it also gets 410 (the invite is consumed), not 409.
        using var sameUser = await first.Client.AcceptAsync(token);
        await sameUser.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
    }

    [Fact]
    public async Task Expired_invite_rejected()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var created = await owner.Client.PostAsync($"/api/households/{household}/invites", new { role = "Adult" });
        var invite = await created.JsonAsync();
        var token = invite.GetProperty("token").GetString()!;
        var guest = await host.SignInAsync();

        host.Time.Now = invite.GetProperty("expiresAt").GetDateTimeOffset().AddSeconds(1);
        using var late = await guest.Client.AcceptAsync(token);

        await late.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        Assert.Empty(await guest.Client.ListHouseholdsAsync());
        Assert.Equal(1L, Convert.ToInt64(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h", ("@h", household))));
    }

    [Fact]
    public async Task Invite_lifetime_is_configurable()
    {
        await using var host = Host(("Households:InviteLifetimeDays", "1"));
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var created = await owner.Client.PostAsync($"/api/households/{household}/invites", new { role = "Child" });

        var expiresAt = (await created.JsonAsync()).GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(expiresAt <= host.Time.GetUtcNow().AddDays(1) && expiresAt > host.Time.GetUtcNow().AddHours(23));
    }

    [Fact]
    public async Task Unknown_invite_rejected()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        using var created = await owner.Client.PostAsync($"/api/households/{household}/invites", new { role = "Adult" });
        var invite = await created.JsonAsync();
        var guest = await host.SignInAsync();
        host.Time.Now = invite.GetProperty("expiresAt").GetDateTimeOffset().AddMinutes(1);
        using var expired = await guest.Client.AcceptAsync(invite.GetProperty("token").GetString()!);

        using var unknown = await guest.Client.AcceptAsync(InviteToken.Generate());
        using var malformed = await guest.Client.AcceptAsync("not-a-token");

        await unknown.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        await malformed.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        var expiredShape = await expired.ProblemShapeAsync();
        Assert.Equal(expiredShape, await unknown.ProblemShapeAsync());
        Assert.Equal(expiredShape, await malformed.ProblemShapeAsync());
    }

    [Fact]
    public async Task Anonymous_accept_is_unauthorized()
    {
        await using var host = Host();
        using var anonymous = host.CreateClient();

        using var response = await anonymous.AcceptAsync(InviteToken.Generate());

        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Concurrent_accept_single_use()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var token = await owner.Client.CreateInviteAsync(household, "Child");
        var first = await host.SignInAsync();
        var second = await host.SignInAsync();

        var results = await Task.WhenAll(first.Client.AcceptAsync(token), second.Client.AcceptAsync(token));

        try
        {
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Gone], results.Select(r => r.StatusCode).Order().ToArray());
            var loser = results.Single(r => r.StatusCode == HttpStatusCode.Gone);
            await loser.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
            Assert.Equal(2L, Convert.ToInt64(await HouseholdScenario.ScalarAsync(
                db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h", ("@h", household))));
        }
        finally
        {
            foreach (var result in results)
            {
                result.Dispose();
            }
        }
    }

    [Fact]
    public async Task Existing_member_accept_conflict()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var token = await owner.Client.CreateInviteAsync(household, "Adult");

        using var conflict = await owner.Client.AcceptAsync(token);

        await conflict.AssertProblemAsync(HttpStatusCode.Conflict, "already-member");
        var guest = await host.SignInAsync();
        using var accepted = await guest.Client.AcceptAsync(token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        // The Owner's role is untouched.
        Assert.Equal("Owner", Assert.Single(await owner.Client.ListHouseholdsAsync()).Role);
    }

    [Fact]
    public async Task Token_stored_hashed()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var token = await owner.Client.CreateInviteAsync(household, "Adult");

        var stored = (byte[])(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT TokenHash FROM Invites WHERE HouseholdId = @h", ("@h", household)))!;

        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), stored);
        Assert.NotEqual(Encoding.UTF8.GetBytes(token), stored);
        // No text column of the row holds the plain token.
        Assert.Equal(0, await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Invites WHERE HouseholdId = @h AND Role LIKE '%' + @t + '%'", ("@h", household), ("@t", token)));
    }

    [Fact]
    public async Task Two_households_and_switch()
    {
        await using var host = Host();
        var ownerOfB = await host.SignInAsync();
        var b = await ownerOfB.Client.CreateHouseholdAsync("B");
        var user = await host.SignInAsync();
        var a = await user.Client.CreateHouseholdAsync("A");
        var token = await ownerOfB.Client.CreateInviteAsync(b, "Adult");
        using var joined = await user.Client.AcceptAsync(token);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        var listed = await user.Client.ListHouseholdsAsync();

        Assert.Equal([(a, "A", "Owner"), (b, "B", "Adult")], listed);
        using var inA = await user.Client.GetAsync($"/api/households/{a}/test/owner");
        using var inB = await user.Client.GetAsync($"/api/households/{b}/test/owner");
        using var adultInB = await user.Client.GetAsync($"/api/households/{b}/test/adult");
        using var unknown = await user.Client.GetAsync($"/api/households/{Guid.NewGuid()}/test/member");
        Assert.Equal(HttpStatusCode.OK, inA.StatusCode);
        await inB.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(HttpStatusCode.OK, adultInB.StatusCode);
        await unknown.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("owner")]
    [InlineData("Boss")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Owner_role_invite_rejected(string? role)
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();

        using var response = await owner.Client.PostAsync($"/api/households/{household}/invites", new { role });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("role", out _));
        Assert.Equal(0L, Convert.ToInt64(await InviteCountAsync(db.ConnectionString, household)));
    }

    [Fact]
    public async Task Household_endpoints_under_route()
    {
        await using var host = Host();
        // A request first, so routing has built the endpoints that the test startup filter added.
        using var client = host.CreateClient();
        using var warmup = await client.GetAsync("/api/health");
        var filter = host.Services.GetServices<Microsoft.AspNetCore.Hosting.IStartupFilter>().OfType<PolicyTestEndpointsStartupFilter>().Single();
        var endpoints = filter.Endpoints.OfType<RouteEndpoint>().ToList();
        string[] householdPolicies = [Policies.HouseholdMember, Policies.HouseholdAdult, Policies.HouseholdOwner];

        var withPolicy = endpoints
            .Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy is not null && householdPolicies.Contains(a.Policy)))
            .ToList();
        var underRoute = endpoints
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/households/{householdId", StringComparison.Ordinal))
            .ToList();

        Assert.True(withPolicy.Count >= 5, $"found {withPolicy.Count}"); // not vacuous: create-invite plus the four test endpoints
        // Both ways: a household policy implies the route and the context metadata; the route implies both as well.
        Assert.All(withPolicy, e => Assert.StartsWith("/api/households/{householdId", e.RoutePattern.RawText, StringComparison.Ordinal));
        Assert.All(withPolicy, e => Assert.NotNull(e.Metadata.GetMetadata<HouseholdScopedMetadata>()));
        Assert.All(underRoute, e => Assert.NotNull(e.Metadata.GetMetadata<HouseholdScopedMetadata>()));
        Assert.All(underRoute, e => Assert.Contains(
            e.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy is not null && householdPolicies.Contains(a.Policy)));
        Assert.Contains(withPolicy, e => e.RoutePattern.RawText!.Contains("/invites", StringComparison.Ordinal));
        Assert.Contains(underRoute, e => e.RoutePattern.RawText!.EndsWith("/test/unprotected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scoped_endpoint_without_own_policy_requires_membership()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var outsider = await host.SignInAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        using var anonymous = host.CreateClient();

        using var denied = await outsider.Client.GetAsync($"/api/households/{household}/test/unprotected");
        using var member = await child.Client.GetAsync($"/api/households/{household}/test/unprotected");
        using var signedOut = await anonymous.GetAsync($"/api/households/{household}/test/unprotected");

        await denied.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        await signedOut.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("91")]
    public async Task Invite_lifetime_out_of_range_fails_startup(string days)
    {
        await using var host = Host(("Households:InviteLifetimeDays", days));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = host.CreateClient();
            await client.GetAsync("/api/health");
        });

        // Not just any startup failure: the options validation, and it names the setting.
        var validation = Flatten(thrown).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.NotNull(validation);
        Assert.Contains(nameof(HouseholdOptions.InviteLifetimeDays), validation.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }

    [Fact]
    public async Task Invite_accept_rate_limited()
    {
        await using var host = Host(("RateLimiting:InviteAccept:PermitLimit", "2"));
        var user = await host.SignInAsync("203.0.113.50");

        using var first = await user.Client.AcceptAsync(InviteToken.Generate());
        using var second = await user.Client.AcceptAsync(InviteToken.Generate());
        using var third = await user.Client.AcceptAsync(InviteToken.Generate());

        await first.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        await second.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        await third.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(third.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Households_isolated()
    {
        await using var host = Host();
        var ownerA = await host.SignInAsync();
        var a = await ownerA.Client.CreateHouseholdAsync("A");
        var ownerB = await host.SignInAsync();
        var b = await ownerB.Client.CreateHouseholdAsync("B");
        await ownerB.Client.CreateInviteAsync(b, "Child");
        await host.JoinAsync(ownerB, b, "Adult");

        // API: a user in A only never sees B.
        Assert.Equal(a, Assert.Single(await ownerA.Client.ListHouseholdsAsync()).Id);
        using var intoB = await ownerA.Client.GetAsync($"/api/households/{b}/test/member");
        await intoB.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Data layer: with context A, no query returns B's memberships or invites, with the filter doing the work.
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await using var inA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(db.ConnectionString).Options,
            new FixedHousehold(a, HouseholdRole.Owner),
            new TenantWriteScope(),
            TimeProvider.System);
        Assert.All(await inA.Memberships.ToListAsync(Ct), m => Assert.Equal(a, m.HouseholdId));
        Assert.All(await inA.Invites.ToListAsync(Ct), i => Assert.Equal(a, i.HouseholdId));
        Assert.Empty(await inA.Memberships.Where(m => m.HouseholdId == b).ToListAsync(Ct));
        Assert.Empty(await inA.Invites.Where(i => i.HouseholdId == b).ToListAsync(Ct));
        // Without a household context nothing is visible at all.
        Assert.Empty(await context.Memberships.ToListAsync(Ct));
        Assert.Empty(await context.Invites.ToListAsync(Ct));
        // The raw table does have B's rows, so the filter is what hides them.
        Assert.True(Convert.ToInt64(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h", ("@h", b))) >= 2);
    }

    [Fact]
    public async Task Tenant_write_guard_applies_to_membership_and_invite()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(db.ConnectionString).Options,
            new FixedHousehold(a, HouseholdRole.Owner),
            new TenantWriteScope(),
            TimeProvider.System);
        context.Memberships.Add(new Membership(b, Guid.NewGuid(), HouseholdRole.Child));

        await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));

        context.ChangeTracker.Clear();
        context.Invites.Add(new Invite(b, InviteToken.Hash(InviteToken.Generate()), HouseholdRole.Child, DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid()));
        await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Model_marks_membership_and_invite_as_tenant_and_household_as_root()
    {
        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);

        Assert.NotNull(context.Model.FindEntityType(typeof(Membership))!.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        Assert.NotNull(context.Model.FindEntityType(typeof(Invite))!.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        var household = context.Model.FindEntityType(typeof(Household))!;
        Assert.Null(household.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        Assert.Null(household.FindProperty("HouseholdId"));
        Assert.False(typeof(ITenantEntity).IsAssignableFrom(typeof(Household)));
        Assert.Empty(household.GetNavigations());
    }

    [Fact]
    public async Task Tenant_resolution_queries_are_scoped_to_the_user()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var resolution = scope.ServiceProvider.GetRequiredService<ITenantResolution>();
        var token = InviteToken.Generate();

        Assert.Equal(household, Assert.Single(await resolution.GetMembershipsForUser(owner.User.Id, Ct)).HouseholdId);
        Assert.Equal(HouseholdRole.Owner, await resolution.FindRole(owner.User.Id, household, Ct));
        Assert.Null(await resolution.FindRole(Guid.NewGuid(), household, Ct));
        Assert.Null(await resolution.FindInviteByTokenHash(InviteToken.Hash(token), Ct));
    }
}

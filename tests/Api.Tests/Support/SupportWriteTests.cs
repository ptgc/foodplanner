using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Infrastructure.Email;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Support;

/// <summary>T-096: the four named fixes, only in a write session, only inside the session's household.</summary>
public sealed class SupportWriteTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private const string SupportMailSubject = "Foodplanner support accessed your household";

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The mail server is down.");
    }

    private static string Base(Guid household) => $"/api/support/households/{household}";

    private async Task<string> InviteStateAsync(Guid invite) =>
        Convert.ToString(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT CASE WHEN AcceptedAt IS NULL AND ExpiresAt > SYSDATETIMEOFFSET() THEN 'open' ELSE 'ended' END FROM Invites WHERE Id = @id", ("@id", invite)))!;

    private async Task<string> RoleOfAsync(Guid household, Guid user) =>
        (string)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT Role FROM Memberships WHERE HouseholdId = @h AND UserId = @u", ("@h", household), ("@u", user)))!;

    [Fact]
    public async Task Read_session_cannot_write_and_the_database_is_unchanged()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        using (var lockWeek = await target.Owner.Client.PutAsync($"/api/households/{target.Id}/meal-plans/{target.Week}/lock"))
        {
            Assert.Equal(HttpStatusCode.OK, lockWeek.StatusCode);
        }

        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id);

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Delete, $"/invites/{target.OpenInvite}", null),
            (HttpMethod.Put, $"/members/{target.Adult.User.Id}/role", new { role = "Owner" }),
            (HttpMethod.Post, $"/members/{target.Owner.User.Id}/pin-unlock", null),
            (HttpMethod.Delete, $"/meal-plans/{target.Week}/lock", null),
        };
        foreach (var (method, path, body) in attempts)
        {
            using var response = await admin.Client.SendAsync(method, Base(target.Id) + path, body);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "support-write-required");
        }

        Assert.Equal("open", await InviteStateAsync(target.OpenInvite));
        Assert.Equal("Adult", await RoleOfAsync(target.Id, target.Adult.User.Id));
        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM MealPlans WHERE HouseholdId = @h AND LockedAt IS NOT NULL", ("@h", target.Id)));
        var denied = (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Where(r => r.Action == "access.denied").ToList();
        Assert.Equal(4, denied.Count);
        Assert.All(denied, r => Assert.Contains("support-write-required", r.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_invite_revokes_an_open_invite_only()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var token = await target.Owner.Client.CreateInviteAsync(target.Id, "Adult");
        using var list = await target.Owner.Client.GetAsync($"/api/households/{target.Id}/invites");
        var openInvites = (await list.JsonAsync()).EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        var stuck = openInvites.Single(i => i != target.OpenInvite);
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        using (var cancel = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{stuck}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        var joiner = await host.SignInAsync();
        using (var accept = await joiner.Client.AcceptAsync(token))
        {
            await accept.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        }

        // Already ended, unknown, or an accepted invite of the household: 404.
        using (var again = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{stuck}"))
        {
            await again.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");
        }

        using (var unknown = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{Guid.NewGuid()}"))
        {
            await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");
        }

        var accepted = Guid.Parse((string)(await HouseholdScenario.ScalarAsync(
            db.ConnectionString, "SELECT TOP 1 CONVERT(varchar(36), Id) FROM Invites WHERE HouseholdId = @h AND AcceptedAt IS NOT NULL", ("@h", target.Id)))!);
        using (var used = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{accepted}"))
        {
            await used.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");
        }

        var cancelled = Assert.Single((await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)), r => r.Action == "invite.cancel" && r.Outcome == "Success");
        Assert.Contains(stuck.ToString(), cancelled.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Promote_member_to_owner_keeps_the_old_owner_and_validates()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        using (var promoted = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
            Assert.Equal("Owner", (await promoted.JsonAsync()).GetProperty("role").GetString());
        }

        Assert.Equal("Owner", await RoleOfAsync(target.Id, target.Adult.User.Id));
        Assert.Equal("Owner", await RoleOfAsync(target.Id, target.Owner.User.Id));
        Assert.Equal(2, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h AND Role = 'Owner'", ("@h", target.Id)));

        // An Owner promoted again is a no-op 200.
        using (var again = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        // Other roles are not support's to give: no demotion, no other role.
        foreach (var role in new object?[] { "Adult", "Child", "SystemAdmin", null })
        {
            using var response = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Child.User.Id}/role", new { role });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("role", out _));
        }

        Assert.Equal("Child", await RoleOfAsync(target.Id, target.Child.User.Id));

        // Not a member of this household.
        var stranger = await host.SignInAsync();
        using var notMember = await admin.Client.PutAsync($"{Base(target.Id)}/members/{stranger.User.Id}/role", new { role = "Owner" });
        await notMember.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");

        // No endpoint demotes, removes or adds a member: the support route table has exactly the named fixes.
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/support", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "DELETE /api/support/households/{targetHouseholdId:guid}/invites/{inviteId:guid}",
                "DELETE /api/support/households/{targetHouseholdId:guid}/meal-plans/{weekStart}/lock",
                "DELETE /api/support/households/{targetHouseholdId:guid}/plan", // T-123: reset a household's plan (a named fix of its own)
                "DELETE /api/support/sessions/current",
                "GET /api/support/households",
                "GET /api/support/households/{targetHouseholdId:guid}/",
                "GET /api/support/households/{targetHouseholdId:guid}/access-log",
                "GET /api/support/households/{targetHouseholdId:guid}/invites",
                "GET /api/support/households/{targetHouseholdId:guid}/meal-plans/{weekStart}",
                "GET /api/support/households/{targetHouseholdId:guid}/members",
                "GET /api/support/households/{targetHouseholdId:guid}/plan",
                "GET /api/support/households/{targetHouseholdId:guid}/profiles",
                "GET /api/support/households/{targetHouseholdId:guid}/recipes",
                "GET /api/support/households/{targetHouseholdId:guid}/shopping-lists/{weekStart}",
                "GET /api/support/sessions/current",
                "GET /api/support/users",
                "POST /api/support/households/{targetHouseholdId:guid}/members/{userId:guid}/pin-unlock",
                "POST /api/support/sessions/",
                "PUT /api/support/households/{targetHouseholdId:guid}/members/{userId:guid}/role",
                "PUT /api/support/households/{targetHouseholdId:guid}/plan",
            ],
            routes);
    }

    [Fact]
    public async Task Promoting_takes_the_household_row_lock()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        // Another transaction holds the household row (as a concurrent Owner change would): the promotion has to wait for it.
        await using var holder = new SqlConnection(db.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var hold = holder.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = "UPDATE Households SET Name = Name WHERE Id = @h";
            hold.Parameters.AddWithValue("@h", target.Id);
            await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var promotion = admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" });
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.False(promotion.IsCompleted, "the promotion must wait for the household row lock");
        Assert.Equal("Adult", await RoleOfAsync(target.Id, target.Adult.User.Id));

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        using var response = await promotion;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Owner", await RoleOfAsync(target.Id, target.Adult.User.Id));
    }

    [Fact]
    public async Task Restore_owner_when_the_only_owner_left()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        // The state the API itself refuses to create: no Owner at all.
        await SupportHost.ExecuteAsync(db.ConnectionString, "UPDATE Memberships SET Role = 'Adult' WHERE HouseholdId = @h AND Role = 'Owner'", ("@h", target.Id));
        using (var before = await target.Adult.Client.GetAsync($"/api/households/{target.Id}/members"))
        {
            await before.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (var promote = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        // The new Owner manages the rest through the household's normal endpoints.
        using var members = await target.Adult.Client.GetAsync($"/api/households/{target.Id}/members");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
    }

    [Fact]
    public async Task Clear_pin_lockout_clears_the_counter_and_the_lock_and_never_touches_the_pin()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        await target.Owner.Client.SetPinAsync();
        var profile = await target.Owner.Client.CreateProfileAsync(target.Id, "Ola", "owl");
        await target.Owner.Client.EnterKidModeAsync(target.Id, profile);
        await SupportHost.ExecuteAsync(
            db.ConnectionString, "UPDATE UserPins SET FailedCount = 3, LockedUntil = DATEADD(minute, 5, SYSDATETIMEOFFSET()) WHERE UserId = @u", ("@u", target.Owner.User.Id));
        var hashBefore = (string)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT Hash FROM UserPins WHERE UserId = @u", ("@u", target.Owner.User.Id)))!;
        using (var locked = await target.Owner.Client.UnlockAsync())
        {
            await locked.AssertProblemAsync(HttpStatusCode.TooManyRequests, "pin-locked");
        }

        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (var before = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            var owner = (await before.JsonAsync()).EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == target.Owner.User.Id);
            Assert.True(owner.GetProperty("pinLocked").GetBoolean());
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, owner.GetProperty("pinLockedUntil").ValueKind);
        }

        using (var clear = await admin.Client.PostAsync($"{Base(target.Id)}/members/{target.Owner.User.Id}/pin-unlock"))
        {
            Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        }

        using (var again = await admin.Client.PostAsync($"{Base(target.Id)}/members/{target.Owner.User.Id}/pin-unlock"))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode); // idempotent
        }

        Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM UserPins WHERE UserId = @u AND (FailedCount <> 0 OR LockedUntil IS NOT NULL)", ("@u", target.Owner.User.Id)));
        Assert.Equal(hashBefore, (string)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT Hash FROM UserPins WHERE UserId = @u", ("@u", target.Owner.User.Id)))!);
        using (var after = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            var owner = (await after.JsonAsync()).EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == target.Owner.User.Id);
            Assert.False(owner.GetProperty("pinLocked").GetBoolean());
        }

        using (var unlock = await target.Owner.Client.UnlockAsync())
        {
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode); // the right PIN works again
        }

        // A user who is a member only of another household is not found.
        var otherHousehold = await host.NewHouseholdAsync("Other");
        using var foreign = await admin.Client.PostAsync($"{Base(target.Id)}/members/{otherHousehold.Owner.User.Id}/pin-unlock");
        await foreign.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
    }

    [Fact]
    public async Task Unlock_week_clears_the_lock_and_is_idempotent()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        using (var lockWeek = await target.Owner.Client.PutAsync($"/api/households/{target.Id}/meal-plans/{target.Week}/lock"))
        {
            Assert.Equal(HttpStatusCode.OK, lockWeek.StatusCode);
        }

        var recipe = target.Recipe;
        using (var blocked = await target.Child.Client.PostAsync(
                   $"/api/households/{target.Id}/meal-plans/{target.Week}/entries", new { date = "2026-03-04", recipeId = recipe, servings = 2 }))
        {
            await blocked.AssertProblemAsync(HttpStatusCode.Conflict, "week-locked");
        }

        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (var unlock = await admin.Client.DeleteAsync($"{Base(target.Id)}/meal-plans/{target.Week}/lock"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        }

        using (var plan = await admin.Client.SupportGetAsync(target.Id, $"/meal-plans/{target.Week}"))
        {
            var body = await plan.JsonAsync();
            Assert.False(body.GetProperty("locked").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("lockedAt").ValueKind);
        }

        using (var allowed = await target.Child.Client.PostAsync(
                   $"/api/households/{target.Id}/meal-plans/{target.Week}/entries", new { date = "2026-03-04", recipeId = recipe, servings = 2 }))
        {
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        // An unlocked week and a week without a plan: 204, and no plan row appears.
        var plans = await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM MealPlans WHERE HouseholdId = @h", ("@h", target.Id));
        foreach (var week in new[] { target.Week, "2026-06-01" })
        {
            using var response = await admin.Client.DeleteAsync($"{Base(target.Id)}/meal-plans/{week}/lock");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal(plans, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM MealPlans WHERE HouseholdId = @h", ("@h", target.Id)));

        using var notMonday = await admin.Client.DeleteAsync($"{Base(target.Id)}/meal-plans/2026-03-03/lock");
        Assert.Equal(HttpStatusCode.BadRequest, notMonday.StatusCode);
        Assert.True((await notMonday.JsonAsync()).GetProperty("errors").TryGetProperty("weekStart", out _));
    }

    [Fact]
    public async Task Support_write_actions_cannot_touch_another_household_through_a_session()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var a = await host.NewHouseholdAsync("A side");
        var b = await host.NewHouseholdAsync("B side");
        await b.Owner.Client.SetPinAsync();
        await SupportHost.ExecuteAsync(
            db.ConnectionString, "UPDATE UserPins SET FailedCount = 3, LockedUntil = DATEADD(minute, 5, SYSDATETIMEOFFSET()) WHERE UserId = @u", ("@u", b.Owner.User.Id));
        using (var lockB = await b.Owner.Client.PutAsync($"/api/households/{b.Id}/meal-plans/{b.Week}/lock"))
        {
            Assert.Equal(HttpStatusCode.OK, lockB.StatusCode);
        }

        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(a.Id, "Write");

        using (var promote = await admin.Client.PutAsync($"{Base(a.Id)}/members/{b.Adult.User.Id}/role", new { role = "Owner" }))
        {
            await promote.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
        }

        using (var cancel = await admin.Client.DeleteAsync($"{Base(a.Id)}/invites/{b.OpenInvite}"))
        {
            await cancel.AssertProblemAsync(HttpStatusCode.NotFound, "invite-not-found");
        }

        using (var unlock = await admin.Client.DeleteAsync($"{Base(a.Id)}/meal-plans/{b.Week}/lock"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode); // A's week of that date: nothing to do
        }

        using (var pin = await admin.Client.PostAsync($"{Base(a.Id)}/members/{b.Owner.User.Id}/pin-unlock"))
        {
            await pin.AssertProblemAsync(HttpStatusCode.NotFound, "member-not-found");
        }

        Assert.Equal("Adult", await RoleOfAsync(b.Id, b.Adult.User.Id));
        Assert.Equal("open", await InviteStateAsync(b.OpenInvite));
        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM MealPlans WHERE HouseholdId = @h AND LockedAt IS NOT NULL", ("@h", b.Id)));
        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM UserPins WHERE UserId = @u AND FailedCount = 3 AND LockedUntil IS NOT NULL", ("@u", b.Owner.User.Id)));
        await using var context = Foodplanner.Api.Tests.Persistence.TenancyDatabase.CreateProductionContext(db.ConnectionString);
        await Foodplanner.Api.Tests.TenantIsolation.CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString);
    }

    [Fact]
    public async Task Owners_are_emailed_on_a_write_session_and_on_each_write_but_not_for_reads()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        List<EmailMessage> Support() => [.. host.Email.Messages.Where(m => m.Subject == SupportMailSubject)];

        await admin.Client.StartOkAsync(target.Id);
        using (var read = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        Assert.Empty(Support()); // a read session sends none

        await admin.Client.StartOkAsync(target.Id, "Write");
        var started = Assert.Single(Support());
        Assert.Equal(target.Owner.User.Email, started.To); // Owners only: not the Adult, not the Child
        Assert.Contains("LostOwner", started.TextBody, StringComparison.Ordinal);

        using (var cancel = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{target.OpenInvite}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        Assert.Equal(2, Support().Count);
        Assert.Contains("invite.cancel", Support()[1].TextBody, StringComparison.Ordinal);

        using (var promote = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        // Two Owners now, after the commit: one mail each.
        Assert.Equal(
            new[] { target.Owner.User.Email, target.Adult.User.Email }.Order(StringComparer.Ordinal),
            Support().Skip(2).Select(m => m.To).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Support(), m => m.To == target.Child.User.Email);
    }

    [Fact]
    public async Task A_failing_mail_server_never_fails_or_undoes_the_action()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.Replace(ServiceDescriptor.Singleton<IEmailSender, ThrowingEmailSender>()));
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        using var cancel = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{target.OpenInvite}");

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal("ended", await InviteStateAsync(target.OpenInvite));
        Assert.Single((await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)), r => r.Action == "invite.cancel");
    }
}

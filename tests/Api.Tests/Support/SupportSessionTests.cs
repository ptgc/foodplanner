using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;

namespace Foodplanner.Api.Tests.Support;

/// <summary>T-096: starting, reading, ending and expiring support sessions.</summary>
public sealed class SupportSessionTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(field, out _), $"expected a validation error on {field}");
    }

    [Fact]
    public async Task Start_session_requires_a_reason_and_a_known_household()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        using (var missing = await admin.Client.StartAsync(target.Id, note: null))
        {
            await AssertValidationAsync(missing, "reasonNote");
        }

        using (var shortNote = await admin.Client.StartAsync(target.Id, note: "too short"))
        {
            await AssertValidationAsync(shortNote, "reasonNote");
        }

        using (var longNote = await admin.Client.StartAsync(target.Id, note: new string('x', 501)))
        {
            await AssertValidationAsync(longNote, "reasonNote");
        }

        using (var category = await admin.Client.StartAsync(target.Id, category: "Whim"))
        {
            await AssertValidationAsync(category, "reasonCategory");
        }

        using (var number = await admin.Client.StartAsync(target.Id, category: "1"))
        {
            await AssertValidationAsync(number, "reasonCategory");
        }

        using (var mode = await admin.Client.StartAsync(target.Id, mode: "Root"))
        {
            await AssertValidationAsync(mode, "mode");
        }

        using (var nobody = await admin.Client.PostAsync("/api/support/sessions", new { mode = "Read", reasonCategory = "Other", reasonNote = SupportHost.Note }))
        {
            await AssertValidationAsync(nobody, "householdId");
        }

        using (var unknown = await admin.Client.StartAsync(Guid.NewGuid()))
        {
            await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "household-not-found");
        }

        Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE AdminUserId = @id", ("@id", admin.User.Id)));

        using var ok = await admin.Client.StartAsync(target.Id, category: "StuckInvite");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var body = await ok.JsonAsync();
        Assert.Equal(target.Id, body.GetProperty("householdId").GetGuid());
        Assert.Equal("Hansen", body.GetProperty("householdName").GetString());
        Assert.Equal("Read", body.GetProperty("mode").GetString());
        Assert.Equal("StuckInvite", body.GetProperty("reasonCategory").GetString());

        var started = Assert.Single(await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id), r => r.Action == "session.start" && r.Outcome == "Success");
        Assert.Equal(SupportHost.Note, started.ReasonNote);
        Assert.Equal(target.Id, started.TargetHouseholdId);
        Assert.Equal("StuckInvite", started.ReasonCategory);
    }

    [Fact]
    public async Task Write_session_requires_the_admins_password_and_a_wrong_one_counts_as_a_failed_sign_in()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email, configure: null, ("Auth:LockoutMaxFailedAttempts", "3"));
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        using (var none = await admin.Client.StartAsync(target.Id, "Write"))
        {
            await AssertValidationAsync(none, "password");
        }

        using (var wrong = await admin.Client.StartAsync(target.Id, "Write", password: "not the password"))
        {
            await wrong.AssertProblemAsync(HttpStatusCode.Forbidden, "password-incorrect");
        }

        Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE AdminUserId = @id", ("@id", admin.User.Id)));
        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT AccessFailedCount FROM AspNetUsers WHERE Id = @id", ("@id", admin.User.Id)));

        // Read mode needs no password; the right password gives a write session.
        using (var read = await admin.Client.StartAsync(target.Id))
        {
            Assert.Equal(HttpStatusCode.Created, read.StatusCode);
        }

        using var right = await admin.Client.StartAsync(target.Id, "Write", password: AuthHost.Password);
        Assert.Equal(HttpStatusCode.Created, right.StatusCode);
        Assert.Equal("Write", (await right.JsonAsync()).GetProperty("mode").GetString());
        Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT AccessFailedCount FROM AspNetUsers WHERE Id = @id", ("@id", admin.User.Id)));

        // Too many wrong passwords lock the account out, exactly like sign-in: then even the right one is refused, and a locked-out
        // account is no admin until the lockout ends.
        for (var i = 0; i < 3; i++)
        {
            using var again = await admin.Client.StartAsync(target.Id, "Write", password: "still wrong");
            await again.AssertProblemAsync(HttpStatusCode.Forbidden, "password-incorrect");
        }

        using var locked = await admin.Client.StartAsync(target.Id, "Write", password: AuthHost.Password);
        await locked.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_write_session_needs_an_account_with_a_password()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var user = await host.CreateUserAsync(email, confirmed: true, password: AuthHost.Password);
        var admin = host.CreateClient();
        using (var login = await admin.LoginAsync(email, AuthHost.Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        await SupportHost.ExecuteAsync(db.ConnectionString, "UPDATE AspNetUsers SET PasswordHash = NULL WHERE Id = @id", ("@id", user.Id));

        using var response = await admin.StartAsync(target.Id, "Write", password: AuthHost.Password);
        await response.AssertProblemAsync(HttpStatusCode.Conflict, "password-required");
    }

    [Fact]
    public async Task Session_expires_after_thirty_minutes_for_read_and_fifteen_for_write()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        await admin.Client.StartOkAsync(target.Id, "Write");
        host.Time.Now = host.Time.Now.AddMinutes(14);
        using (var stillOpen = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            Assert.Equal(HttpStatusCode.OK, stillOpen.StatusCode);
        }

        host.Time.Now = host.Time.Now.AddMinutes(2);
        using (var writeOver = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            await writeOver.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-required");
        }

        using (var current = await admin.Client.GetAsync("/api/support/sessions/current"))
        {
            await current.AssertProblemAsync(HttpStatusCode.NotFound, "support-session-required");
        }

        await admin.Client.StartOkAsync(target.Id);
        host.Time.Now = host.Time.Now.AddMinutes(29);
        using (var readOpen = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            Assert.Equal(HttpStatusCode.OK, readOpen.StatusCode);
        }

        host.Time.Now = host.Time.Now.AddMinutes(2);
        using (var readOver = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            await readOver.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-required");
        }

        // A new session can be started after the old one ran out; nothing wrote the expiry.
        await admin.Client.StartOkAsync(target.Id);
        using var restored = await admin.Client.SupportGetAsync(target.Id, "/members");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task One_active_session_per_admin_and_two_parallel_starts_leave_exactly_one()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var first = await host.NewHouseholdAsync("First");
        var second = await host.NewHouseholdAsync("Second");
        var admin = await host.SignInAsAsync(email);

        var one = await admin.Client.StartOkAsync(first.Id);
        var two = await admin.Client.StartOkAsync(second.Id);

        // The first no longer authorises: only the session for the second household is active.
        using (var old = await admin.Client.SupportGetAsync(first.Id, "/members"))
        {
            await old.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-mismatch");
        }

        using (var current = await admin.Client.SupportGetAsync(second.Id, "/members"))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        var replaced = await SupportHost.CountAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE Id = @id AND EndedReason = 'replaced' AND EndedAt IS NOT NULL", ("@id", one.GetProperty("id").GetGuid()));
        Assert.Equal(1, replaced);

        // Two starts at once, from two devices of the same admin.
        var phone = host.CreateClient();
        using (var login = await phone.LoginAsync(email, AuthHost.Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        var results = await Task.WhenAll(admin.Client.StartAsync(first.Id), phone.StartAsync(second.Id));
        try
        {
            Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
            Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Created);
        }
        finally
        {
            foreach (var result in results)
            {
                result.Dispose();
            }
        }

        Assert.Equal(
            1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE AdminUserId = @id AND EndedAt IS NULL", ("@id", admin.User.Id)));
        Assert.NotEqual(Guid.Empty, two.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task End_session_is_idempotent_audited_and_the_session_no_longer_authorises()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        // Nothing to end: still 204.
        using (var none = await admin.Client.DeleteAsync("/api/support/sessions/current"))
        {
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        }

        var session = await admin.Client.StartOkAsync(target.Id);
        using (var current = await admin.Client.GetAsync("/api/support/sessions/current"))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal(session.GetProperty("id").GetGuid(), (await current.JsonAsync()).GetProperty("id").GetGuid());
        }

        using (var ended = await admin.Client.DeleteAsync("/api/support/sessions/current"))
        {
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        }

        using (var after = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            await after.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-required");
        }

        using (var gone = await admin.Client.GetAsync("/api/support/sessions/current"))
        {
            await gone.AssertProblemAsync(HttpStatusCode.NotFound, "support-session-required");
        }

        var rows = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        Assert.Equal(2, rows.Count(r => r.Action == "session.end"));
        Assert.Equal(1, await SupportHost.CountAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE Id = @id AND EndedReason = 'ended'", ("@id", session.GetProperty("id").GetGuid())));
    }

    [Fact]
    public async Task Session_endpoints_are_rate_limited()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email, configure: null, ("RateLimiting:SupportSession:PermitLimit", "20"));
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        for (var i = 0; i < 20; i++)
        {
            using var ok = await admin.Client.StartAsync(target.Id);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var limited = await admin.Client.StartAsync(target.Id);
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.Contains("Retry-After"));
    }
}

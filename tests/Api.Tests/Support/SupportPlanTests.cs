using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Infrastructure.Email;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Support;

/// <summary>T-123: the support-only plan endpoints (view, set, reset). Only a super admin in a write session changes a plan, and every call is audited.</summary>
public sealed class SupportPlanTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private const string SupportMailSubject = "Foodplanner support accessed your household";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Plan(Guid household) => $"/api/support/households/{household}/plan";

    private static object Limited(int limit) => new { unlimited = false, limit };

    private static object Unlimited => new { unlimited = true };

    private static object Body(string? name, object photo, object day, object month) =>
        new { name, photoImportsPerDay = photo, lineHelpPerDay = day, lineHelpPerMonth = month };

    private static readonly object HomeBody = Body("Unlimited", Unlimited, Limited(40), Unlimited);

    private async Task<int> PlanRowsAsync(Guid? household = null) =>
        await SupportHost.CountAsync(
            db.ConnectionString, "SELECT COUNT(*) FROM HouseholdPlans WHERE (@h IS NULL OR HouseholdId = @h)", ("@h", (object?)household ?? DBNull.Value));

    private async Task<string> PlanSignatureAsync(Guid household) =>
        Convert.ToString(await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            "SELECT COALESCE((SELECT CONCAT(Name, '|', PhotoImportsPerDay, '|', LineHelpPerDay, '|', LineHelpPerMonth) FROM HouseholdPlans WHERE HouseholdId = @h), 'none')",
            ("@h", household)))!;

    [Fact]
    public async Task Plan_endpoints_require_admin_and_session()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var a = await host.NewHouseholdAsync("Alpha");
        var b = await host.NewHouseholdAsync("Beta");
        var admin = await host.SignInAsAsync(email);
        var calls = new (HttpMethod Method, object? Body)[] { (HttpMethod.Get, null), (HttpMethod.Put, HomeBody), (HttpMethod.Delete, null) };

        // Household roles and anonymous: never.
        using var anonymous = host.CreateClient();
        foreach (var (method, body) in calls)
        {
            using var none = await anonymous.SendAsync(method, Plan(a.Id), body);
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
            foreach (var who in new[] { a.Owner, a.Adult, a.Child })
            {
                using var response = await who.Client.SendAsync(method, Plan(a.Id), body);
                await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }
        }

        Assert.Equal(0, await PlanRowsAsync(a.Id));

        // An admin without a session.
        foreach (var (method, body) in calls)
        {
            using var response = await admin.Client.SendAsync(method, Plan(a.Id), body);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-required");
        }

        Assert.Equal(calls.Length, (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Count(r => r.Action == "access.denied"));

        // A read session may view, never write.
        await admin.Client.StartOkAsync(a.Id);
        using (var view = await admin.Client.SendAsync(HttpMethod.Get, Plan(a.Id), null))
        {
            Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        }

        foreach (var (method, body) in calls.Skip(1))
        {
            using var response = await admin.Client.SendAsync(method, Plan(a.Id), body);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "support-write-required");
        }

        Assert.Equal(0, await PlanRowsAsync(a.Id));

        // A session for A cannot reach B, in any mode.
        await admin.Client.StartOkAsync(a.Id, "Write");
        foreach (var (method, body) in calls)
        {
            using var response = await admin.Client.SendAsync(method, Plan(b.Id), body);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-mismatch");
        }

        Assert.Equal(0, await PlanRowsAsync(b.Id));
    }

    [Fact]
    public async Task Admin_sets_plan_in_write_session()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, null, ("AiPhotoImport:DailyPerHousehold", "2"), ("AiImport:DailyCallsPerHousehold", "20"), ("AiImport:MonthlyCallsPerHousehold", "200"));
        var target = await host.NewHouseholdAsync();
        var other = await host.NewHouseholdAsync("Other");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        using (var before = await admin.Client.GetAsync(Plan(target.Id)))
        {
            var plan = await before.JsonAsync();
            Assert.Equal("Default", plan.GetProperty("source").GetString());
            Assert.Equal(2, plan.GetProperty("photoImportsPerDay").GetProperty("limit").GetInt32());
            Assert.Equal(20, plan.GetProperty("lineHelpPerDay").GetProperty("limit").GetInt32());
            Assert.Equal(200, plan.GetProperty("lineHelpPerMonth").GetProperty("limit").GetInt32());
            Assert.Equal(2, plan.GetProperty("defaults").GetProperty("photoImportsPerDay").GetProperty("limit").GetInt32());
        }

        for (var i = 0; i < 2; i++) // idempotent: the same body twice leaves one row
        {
            using var put = await admin.Client.PutAsync(Plan(target.Id), HomeBody);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var plan = await put.JsonAsync();
            Assert.Equal("Custom", plan.GetProperty("source").GetString());
            Assert.Equal("Unlimited", plan.GetProperty("name").GetString());
            Assert.True(plan.GetProperty("photoImportsPerDay").GetProperty("unlimited").GetBoolean());
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("photoImportsPerDay").GetProperty("limit").ValueKind);
            Assert.False(plan.GetProperty("lineHelpPerDay").GetProperty("unlimited").GetBoolean());
            Assert.Equal(40, plan.GetProperty("lineHelpPerDay").GetProperty("limit").GetInt32());
            Assert.True(plan.GetProperty("lineHelpPerMonth").GetProperty("unlimited").GetBoolean());
        }

        Assert.Equal(1, await PlanRowsAsync(target.Id));
        Assert.Equal(0, await PlanRowsAsync(other.Id));
        using var after = await admin.Client.GetAsync(Plan(target.Id));
        var read = await after.JsonAsync();
        Assert.Equal("Custom", read.GetProperty("source").GetString());
        Assert.Equal(2, read.GetProperty("defaults").GetProperty("photoImportsPerDay").GetProperty("limit").GetInt32());

        // The member sees the effect through the existing endpoint, and only its own numbers.
        using var usage = await target.Owner.Client.GetAsync($"/api/households/{target.Id}/imports/photo-usage");
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
    }

    [Fact]
    public async Task Plan_set_is_validated()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        var ok = Limited(3);

        var bad = new (string Field, object Body)[]
        {
            ("photoImportsPerDay", new { name = "x", lineHelpPerDay = ok, lineHelpPerMonth = ok }), // missing object
            ("lineHelpPerDay", Body("x", ok, new { unlimited = false }, ok)), // no limit
            ("lineHelpPerMonth", Body("x", ok, ok, new { unlimited = true, limit = 5 })), // unlimited with a number
            ("photoImportsPerDay", Body("x", Limited(-1), ok, ok)),
            ("lineHelpPerDay", Body("x", ok, Limited(10_001), ok)),
            ("name", Body(new string('a', 41), ok, ok, ok)),
            ("name", Body("bell\u0007", ok, ok, ok)),
        };
        foreach (var (field, body) in bad)
        {
            using var response = await admin.Client.PutAsync(Plan(target.Id), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.JsonAsync();
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on {field}");
        }

        Assert.Equal(0, await PlanRowsAsync(target.Id));

        // The edges are fine: 0 and 10000, a label of 40 characters, an empty label becomes null, a monthly limit below the daily one.
        using (var edge = await admin.Client.PutAsync(Plan(target.Id), Body("", Limited(0), Limited(10_000), Limited(1))))
        {
            Assert.Equal(HttpStatusCode.OK, edge.StatusCode);
            Assert.Equal(JsonValueKind.Null, (await edge.JsonAsync()).GetProperty("name").ValueKind);
        }

        using var longest = await admin.Client.PutAsync(Plan(target.Id), Body(new string('a', 40), ok, ok, ok));
        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
    }

    [Fact]
    public async Task Plan_reset()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email, null, ("AiPhotoImport:DailyPerHousehold", "2"));
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (var put = await admin.Client.PutAsync(Plan(target.Id), HomeBody))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        foreach (var _ in new[] { 1, 2 }) // a second reset (no row) is fine too
        {
            using var delete = await admin.Client.DeleteAsync(Plan(target.Id));
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.Equal(0, await PlanRowsAsync(target.Id));
        using var view = await admin.Client.GetAsync(Plan(target.Id));
        Assert.Equal("Default", (await view.JsonAsync()).GetProperty("source").GetString());
        var rows = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        Assert.Equal(2, rows.Count(r => r.Action == "plan.reset"));
    }

    [Fact]
    public async Task Plan_changes_are_audited()
    {
        var email = SupportHost.NewAdminEmail();
        var fault = new FailAuditInterceptor();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(fault)),
            ("AiPhotoImport:DailyPerHousehold", "2"));
        var target = await host.NewHouseholdAsync();
        var other = await host.NewHouseholdAsync("Other");
        var admin = await host.SignInAsAsync(email);
        var session = await admin.Client.StartOkAsync(target.Id, "Write");
        var sessionId = session.GetProperty("id").GetGuid();

        using (await admin.Client.GetAsync(Plan(target.Id)))
        {
        }

        using (await admin.Client.PutAsync(Plan(target.Id), Body("Family Secret Label", Limited(7), Unlimited, Limited(90))))
        {
        }

        using (await admin.Client.DeleteAsync(Plan(target.Id)))
        {
        }

        var rows = (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Where(r => r.Action.Contains("plan", StringComparison.Ordinal)).ToList();
        Assert.Equal(["plan.reset", "plan.set", "view.plan"], rows.Select(r => r.Action).Order(StringComparer.Ordinal)); // one each (the clock is fixed, so no order)
        foreach (var row in rows)
        {
            Assert.Equal(admin.User.Id, row.AdminUserId);
            Assert.Equal(sessionId, row.SessionId);
            Assert.Equal(target.Id, row.TargetHouseholdId);
            Assert.Equal("Write", row.Mode);
            Assert.Equal("LostOwner", row.ReasonCategory);
            Assert.Equal("Success", row.Outcome);
            Assert.DoesNotContain(other.Id.ToString(), row.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AuthHost.Password, row.Detail ?? string.Empty, StringComparison.Ordinal);
        }

        var set = rows.Single(r => r.Action == "plan.set").Detail!;
        using var detail = JsonDocument.Parse(set);
        var d = detail.RootElement;
        Assert.Equal(2, d.GetProperty("photoImportsPerDayBefore").GetInt32()); // the default was in force
        Assert.Equal(7, d.GetProperty("photoImportsPerDayAfter").GetInt32());
        Assert.Equal("unlimited", d.GetProperty("lineHelpPerDayAfter").GetString());
        Assert.Equal(90, d.GetProperty("lineHelpPerMonthAfter").GetInt32());
        Assert.Equal("Default", d.GetProperty("previousSource").GetString());
        Assert.Equal("Family Secret Label".Length, d.GetProperty("nameLength").GetInt32());
        Assert.DoesNotContain("Family Secret Label", set, StringComparison.Ordinal); // the label text is never stored in the audit row

        // One transaction: with the audit insert faulted the change is not applied.
        fault.Fail = true;
        using (var put = await admin.Client.PutAsync(Plan(target.Id), HomeBody))
        {
            await put.AssertProblemAsync(HttpStatusCode.InternalServerError, "support-audit-failed");
        }

        fault.Fail = false;
        Assert.Equal(0, await PlanRowsAsync(target.Id));
    }

    [Fact]
    public async Task Plan_write_emails_owners()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        List<EmailMessage> Mails() => [.. host.Email.Messages.Where(m => m.Subject == SupportMailSubject)];
        var started = Mails().Count;

        using (await admin.Client.PutAsync(Plan(target.Id), HomeBody))
        {
        }

        using (await admin.Client.DeleteAsync(Plan(target.Id)))
        {
        }

        var sent = Mails().Skip(started).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m => Assert.Equal(target.Owner.User.Email, m.To)); // Owners only
        Assert.Contains("plan.set", sent[0].TextBody, StringComparison.Ordinal);
        Assert.Contains("plan.reset", sent[1].TextBody, StringComparison.Ordinal);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The mail server is down.");
    }

    [Fact]
    public async Task A_failing_mail_server_does_not_undo_a_plan_change()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.Replace(ServiceDescriptor.Singleton<IEmailSender, ThrowingEmailSender>()));
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        using var put = await admin.Client.PutAsync(Plan(target.Id), HomeBody);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, await PlanRowsAsync(target.Id));
        Assert.Single((await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)), r => r.Action == "plan.set");
    }

    [Fact]
    public async Task Household_sees_plan_change_in_support_access()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var other = await host.NewHouseholdAsync("Other");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (await admin.Client.PutAsync(Plan(target.Id), HomeBody))
        {
        }

        foreach (var member in new[] { target.Owner, target.Adult })
        {
            using var response = await member.Client.GetAsync($"/api/households/{target.Id}/support-access");
            var text = await response.Content.ReadAsStringAsync(Ct);
            var items = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().ToList();
            var row = Assert.Single(items, i => i.GetProperty("action").GetString() == "plan.set");
            Assert.Equal("Write", row.GetProperty("mode").GetString());
            Assert.Equal("LostOwner", row.GetProperty("reasonCategory").GetString());
            Assert.Equal("Success", row.GetProperty("outcome").GetString());
            Assert.Equal(["action", "at", "mode", "outcome", "reasonCategory"], row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain("Unlimited", text, StringComparison.Ordinal);
            Assert.DoesNotContain(email, text, StringComparison.Ordinal);
        }

        using var otherLog = await other.Owner.Client.GetAsync($"/api/households/{other.Id}/support-access");
        Assert.Equal(0, (await otherLog.JsonAsync()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Overview_plan_field()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        async Task<JsonElement> Overview()
        {
            using var response = await admin.Client.GetAsync($"/api/support/households/{target.Id}");
            return (await response.JsonAsync()).GetProperty("plan");
        }

        Assert.Equal(JsonValueKind.Null, (await Overview()).ValueKind); // no row
        using (await admin.Client.PutAsync(Plan(target.Id), HomeBody))
        {
        }

        Assert.Equal("Unlimited", (await Overview()).GetString());
        using (await admin.Client.PutAsync(Plan(target.Id), Body(null, Limited(1), Limited(1), Limited(1))))
        {
        }

        Assert.Equal(JsonValueKind.Null, (await Overview()).ValueKind); // an unnamed plan
    }

    [Fact]
    public async Task Plan_write_cannot_touch_another_household()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var a = await host.NewHouseholdAsync("Alpha");
        var b = await host.NewHouseholdAsync("Beta");
        var admin = await host.SignInAsAsync(email);

        // B has a plan of its own, set in a session for B.
        await admin.Client.StartOkAsync(b.Id, "Write");
        using (var put = await admin.Client.PutAsync(Plan(b.Id), Body("Beta plan", Limited(9), Limited(9), Limited(9))))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        var signature = await PlanSignatureAsync(b.Id);

        // A session for A, with B's id in every route: nothing of B changes, and a PUT in A's own route writes A's row only.
        await admin.Client.StartOkAsync(a.Id, "Write");
        foreach (var (method, body) in new (HttpMethod, object?)[] { (HttpMethod.Get, null), (HttpMethod.Put, HomeBody), (HttpMethod.Delete, null) })
        {
            using var response = await admin.Client.SendAsync(method, Plan(b.Id), body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(signature, await PlanSignatureAsync(b.Id));
        }

        using (var own = await admin.Client.PutAsync(Plan(a.Id), HomeBody))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        Assert.Equal(signature, await PlanSignatureAsync(b.Id));
        Assert.Equal(1, await PlanRowsAsync(a.Id));
        Assert.Equal(1, await PlanRowsAsync(b.Id));

        // The write guard: attaching a plan row for another household while a household is current is a tenancy violation.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(db.ConnectionString).Options;
        await using var context = new AppDbContext(
            options, new Foodplanner.Api.Tests.Persistence.FixedHousehold(a.Id), new Foodplanner.Infrastructure.Persistence.Tenancy.TenantWriteScope(), TimeProvider.System);
        var foreign = new Foodplanner.Domain.Tenancy.HouseholdPlan("x", default);
        context.Entry(foreign).State = EntityState.Added;
        context.Entry(foreign).Property(nameof(Foodplanner.Domain.Tenancy.HouseholdPlan.HouseholdId)).CurrentValue = b.Id;
        await Assert.ThrowsAsync<Foodplanner.Infrastructure.Persistence.Tenancy.TenancyViolationException>(() => context.SaveChangesAsync(Ct));
    }
}

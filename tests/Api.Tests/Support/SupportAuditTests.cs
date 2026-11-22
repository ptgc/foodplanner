using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Support;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Api.Tests.Support;

/// <summary>Fails the save of an audit row on demand: the fault injection of <c>Audit_failure_fails_the_request</c>.</summary>
internal sealed class FailAuditInterceptor : SaveChangesInterceptor
{
    public volatile bool Fail;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Fail && eventData.Context!.ChangeTracker.Entries<SupportAuditEntry>().Any(e => e.State == EntityState.Added))
        {
            throw new InvalidOperationException("Injected audit failure.");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>T-096: the audit log (who, what, which household, when, why), what the household sees of it, and that it cannot be edited.</summary>
public sealed class SupportAuditTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static string Base(Guid household) => $"/api/support/households/{household}";

    /// <summary>Calls every support endpoint once in a write session; returns the action each one must have written.</summary>
    private static async Task<List<string>> CallEveryEndpointAsync(ApiClient admin, SupportHousehold target, Guid pinUser)
    {
        var expected = new List<string>();

        async Task Call(HttpMethod method, string url, object? body, string action)
        {
            using var response = await admin.SendAsync(method, url, body);
            Assert.True((int)response.StatusCode < 400, $"{method} {url} -> {response.StatusCode}");
            expected.Add(action);
        }

        await Call(HttpMethod.Get, "/api/support/sessions/current", null, "session.view");
        await Call(HttpMethod.Get, "/api/support/households?search=Hansen", null, "search.households");
        await Call(HttpMethod.Get, "/api/support/users?email=user-", null, "search.users");
        await Call(HttpMethod.Get, Base(target.Id), null, "view.overview");
        await Call(HttpMethod.Get, Base(target.Id) + "/members", null, "view.members");
        await Call(HttpMethod.Get, Base(target.Id) + "/invites", null, "view.invites");
        await Call(HttpMethod.Get, Base(target.Id) + "/profiles", null, "view.profiles");
        await Call(HttpMethod.Get, Base(target.Id) + $"/meal-plans/{target.Week}", null, "view.meal-plan");
        await Call(HttpMethod.Get, Base(target.Id) + $"/shopping-lists/{target.Week}", null, "view.shopping-list");
        await Call(HttpMethod.Get, Base(target.Id) + "/recipes", null, "view.recipes");
        await Call(HttpMethod.Get, Base(target.Id) + "/access-log", null, "view.access-log");
        await Call(HttpMethod.Delete, Base(target.Id) + $"/invites/{target.OpenInvite}", null, "invite.cancel");
        await Call(HttpMethod.Put, Base(target.Id) + $"/members/{target.Adult.User.Id}/role", new { role = "Owner" }, "member.promote-owner");
        await Call(HttpMethod.Post, Base(target.Id) + $"/members/{pinUser}/pin-unlock", null, "member.pin-unlock");
        await Call(HttpMethod.Delete, Base(target.Id) + $"/meal-plans/{target.Week}/lock", null, "week.unlock");
        await Call(HttpMethod.Delete, "/api/support/sessions/current", null, "session.end");
        return expected;
    }

    [Fact]
    public async Task Every_support_endpoint_writes_exactly_one_complete_audit_row()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync("Audit House");
        var admin = await host.SignInAsAsync(email);
        var now = host.Time.Now.AddMinutes(1); // the invite and the session are still valid
        host.Time.Now = now;
        var session = await admin.Client.StartOkAsync(target.Id, "Write");
        var sessionId = session.GetProperty("id").GetGuid();

        var before = (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Count;
        Assert.Equal(1, before); // session.start
        var expected = await CallEveryEndpointAsync(admin.Client, target, target.Owner.User.Id);

        var rows = (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Where(r => r.Action != "session.start").ToList();
        Assert.Equal(expected.Count, rows.Count);
        foreach (var action in expected)
        {
            var row = Assert.Single(rows, r => r.Action == action);
            Assert.Equal(admin.User.Id, row.AdminUserId);
            Assert.Equal(email, row.AdminEmail);
            Assert.Equal(now, row.At);
            Assert.Equal("Success", row.Outcome);
            Assert.Equal(sessionId, row.SessionId);
            Assert.Equal(SupportHost.Note, row.ReasonNote);
            Assert.Equal("LostOwner", row.ReasonCategory);
            Assert.Equal("Write", row.Mode);
            if (action.StartsWith("search.", StringComparison.Ordinal))
            {
                Assert.Null(row.TargetHouseholdId);
            }
            else
            {
                Assert.Equal(target.Id, row.TargetHouseholdId);
                Assert.Equal("Audit House", row.TargetHouseholdName);
            }
        }

        Assert.Contains("user-", Assert.Single(rows, r => r.Action == "search.users").Detail, StringComparison.Ordinal);
        Assert.Contains(target.OpenInvite.ToString(), Assert.Single(rows, r => r.Action == "invite.cancel").Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_failure_fails_the_request_and_a_write_is_not_applied()
    {
        var email = SupportHost.NewAdminEmail();
        var fault = new FailAuditInterceptor();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(fault)));
        var target = await host.NewHouseholdAsync("Secret Household");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        var rowsBefore = (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Count;

        fault.Fail = true;
        using (var read = await admin.Client.SupportGetAsync(target.Id, "/members"))
        {
            await read.AssertProblemAsync(HttpStatusCode.InternalServerError, "support-audit-failed");
            var text = await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(target.Owner.User.Email, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret Household", text, StringComparison.Ordinal);
        }

        using (var write = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{target.OpenInvite}"))
        {
            await write.AssertProblemAsync(HttpStatusCode.InternalServerError, "support-audit-failed");
        }

        using (var promote = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" }))
        {
            await promote.AssertProblemAsync(HttpStatusCode.InternalServerError, "support-audit-failed");
        }

        fault.Fail = false;

        // Same transaction: the change was rolled back with the audit row that could not be written.
        Assert.Equal(
            1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM Invites WHERE Id = @id AND AcceptedAt IS NULL AND ExpiresAt > SYSDATETIMEOFFSET()", ("@id", target.OpenInvite)));
        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM Memberships WHERE UserId = @u AND Role = 'Adult'", ("@u", target.Adult.User.Id)));
        Assert.Equal(rowsBefore, (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Count);

        // And once the audit works again, the same calls go through.
        using var again = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{target.OpenInvite}");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Audit_rows_and_log_lines_contain_no_secrets_and_the_log_mirrors_every_row()
    {
        var email = SupportHost.NewAdminEmail();
        var log = new CategoryLogCapture();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.AddLogging(logging => logging.AddProvider(log)));
        var target = await host.NewHouseholdAsync("Mirror House");
        await target.Owner.Client.SetPinAsync();
        var token = await target.Owner.Client.CreateInviteAsync(target.Id, "Child");
        var admin = await host.SignInAsAsync(email);
        var started = await admin.Client.StartOkAsync(target.Id, "Write");
        await CallEveryEndpointAsync(admin.Client, target, target.Owner.User.Id);

        var secrets = new List<string> { AuthHost.Password, KidHost.Pin, token };
        foreach (var (table, column, key) in new[] { ("AspNetUsers", "PasswordHash", "Id"), ("AspNetUsers", "SecurityStamp", "Id"), ("UserPins", "Hash", "UserId") })
        {
            secrets.Add((string)(await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT TOP 1 {column} FROM {table} WHERE {key} = @id", ("@id", target.Owner.User.Id)))!);
        }

        secrets.Add((string)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT TOP 1 Title FROM Recipes WHERE HouseholdId = @h", ("@h", target.Id)))!);

        var rows = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        // Ids are random hex, which could contain a short PIN by chance: look at everything but the guids.
        static string WithoutGuids(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<id>");
        var dump = WithoutGuids(string.Join('\n', rows.Select(r => $"{r.Detail}|{r.ReasonNote}|{r.TargetHouseholdName}|{r.AdminEmail}")));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, dump, StringComparison.Ordinal);
        }

        // The second copy: one structured event per row in category SupportAudit, naming the category and never the note text.
        List<string> mirrored;
        lock (log.Events)
        {
            mirrored = [.. log.Events.Where(e => e.Category == "SupportAudit").Select(e => e.Message)];
        }

        Assert.Equal(rows.Count, mirrored.Count);
        Assert.All(mirrored, line =>
        {
            Assert.DoesNotContain(SupportHost.Note, line, StringComparison.Ordinal);
            Assert.Contains("category=LostOwner", line, StringComparison.Ordinal);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, WithoutGuids(line), StringComparison.Ordinal);
            }
        });
        Assert.Contains(mirrored, line => line.Contains("Support audit session.start", StringComparison.Ordinal));
        Assert.NotEqual(Guid.Empty, started.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Audit_is_append_only_in_the_save_guard_and_in_the_database()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id);
        var adminRows = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        var row = Assert.Single(adminRows);

        // 1. SaveChanges refuses a changed or deleted entry.
        await using (var context = TenancyDatabase.CreateProductionContext(db.ConnectionString))
        {
            var entry = await context.SupportAuditEntries.SingleAsync(e => e.Id == row.Id, TestContext.Current.CancellationToken);
            context.Entry(entry).Property(e => e.Detail).CurrentValue = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        await using (var context = TenancyDatabase.CreateProductionContext(db.ConnectionString))
        {
            var entry = await context.SupportAuditEntries.SingleAsync(e => e.Id == row.Id, TestContext.Current.CancellationToken);
            context.SupportAuditEntries.Remove(entry);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        // 2. The trigger refuses UPDATE and DELETE for every login, the application's included.
        var update = await Assert.ThrowsAsync<SqlException>(() => SupportHost.ExecuteAsync(
            db.ConnectionString, "UPDATE SupportAuditEntries SET Outcome = 'Success' WHERE Id = @id", ("@id", row.Id)));
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);
        var delete = await Assert.ThrowsAsync<SqlException>(() => SupportHost.ExecuteAsync(
            db.ConnectionString, "DELETE FROM SupportAuditEntries WHERE Id = @id", ("@id", row.Id)));
        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
        Assert.Equal(row, Assert.Single(await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)));

        // 3. No route of the API edits or deletes audit rows: the only writes under /api/support are the named fixes and sessions.
        Assert.DoesNotContain(
            SupportAdminAccessTests.SupportCalls(host, target.Id),
            c => c.Url.Contains("audit", StringComparison.OrdinalIgnoreCase) && c.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Audit_and_sessions_survive_deleting_the_household_and_the_account_and_have_no_foreign_keys()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync("Doomed House");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (var cancel = await admin.Client.DeleteAsync($"{Base(target.Id)}/invites/{target.OpenInvite}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        var before = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        Assert.Equal(2, before.Count);

        using (var delete = await target.Owner.Client.DeleteAsync($"/api/households/{target.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        await SupportHost.ExecuteAsync(db.ConnectionString, "DELETE FROM AspNetUsers WHERE Id = @id", ("@id", admin.User.Id));

        var after = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        Assert.Equal(before, after);
        Assert.All(after, r =>
        {
            Assert.Equal("Doomed House", r.TargetHouseholdName);
            Assert.Equal(email, r.AdminEmail);
        });
        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE AdminUserId = @id", ("@id", admin.User.Id)));

        await using var context = TenancyDatabase.CreateProductionContext(db.ConnectionString);
        foreach (var type in new[] { typeof(SupportSession), typeof(SupportAuditEntry) })
        {
            var entityType = context.Model.FindEntityType(type)!;
            Assert.Empty(entityType.GetForeignKeys());
            Assert.Empty(entityType.GetReferencingForeignKeys());
            Assert.DoesNotContain(entityType.GetProperties(), p => p.Name == "HouseholdId");
            Assert.True(TenantCoverage.Exempt.TryGetValue(type, out var reason) && !string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public async Task The_household_sees_support_access_without_the_admin_the_note_or_the_detail()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var other = await host.NewHouseholdAsync("Other");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");
        using (var promote = await admin.Client.PutAsync($"{Base(target.Id)}/members/{target.Adult.User.Id}/role", new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        // The session is for the first household: a read of the other one is refused (a denied row) and a search is not household data.
        using (var denied = await admin.Client.SupportGetAsync(other.Id, "/members"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var search = await admin.Client.GetAsync("/api/support/households?search=Other"))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        }

        foreach (var member in new[] { target.Owner, target.Adult })
        {
            using var response = await member.Client.GetAsync($"/api/households/{target.Id}/support-access");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var page = System.Text.Json.JsonDocument.Parse(text).RootElement;
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, page.GetProperty("total").GetInt32());
            var start = Assert.Single(items, i => i.GetProperty("action").GetString() == "session.start");
            Assert.Equal("Write", start.GetProperty("mode").GetString());
            Assert.Equal("LostOwner", start.GetProperty("reasonCategory").GetString());
            Assert.Equal("Success", start.GetProperty("outcome").GetString());
            Assert.Contains(items, i => i.GetProperty("action").GetString() == "member.promote-owner");
            Assert.All(items, i => Assert.Equal(["action", "at", "mode", "outcome", "reasonCategory"], i.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
            Assert.DoesNotContain(email, text, StringComparison.Ordinal);
            Assert.DoesNotContain(admin.User.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SupportHost.Note, text, StringComparison.Ordinal);
            Assert.DoesNotContain("detail", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("denied", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("search", text, StringComparison.OrdinalIgnoreCase);
        }

        // The other household's list: nothing, because no data of that household was accessed (a denied attempt is not an access).
        using (var otherLog = await other.Owner.Client.GetAsync($"/api/households/{other.Id}/support-access"))
        {
            Assert.Equal(0, (await otherLog.JsonAsync()).GetProperty("total").GetInt32());
        }

        // Only the last 90 days.
        host.Time.Now = host.Time.Now.AddDays(91);
        using var old = await target.Owner.Client.GetAsync($"/api/households/{target.Id}/support-access");
        Assert.Equal(0, (await old.JsonAsync()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Support_access_is_for_owners_and_adults_of_the_household_only()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var other = await host.NewHouseholdAsync("Elsewhere");
        var kid = await host.NewScenarioAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(other.Id);

        using (var child = await target.Child.Client.GetAsync($"/api/households/{target.Id}/support-access"))
        {
            await child.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var outsider = await other.Owner.Client.GetAsync($"/api/households/{target.Id}/support-access"))
        {
            await outsider.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        await kid.Owner.Client.EnterKidModeAsync(kid.Household, kid.Profile1);
        using (var kidMode = await kid.Owner.Client.GetAsync($"/api/households/{kid.Household}/support-access"))
        {
            await kidMode.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // Another household's rows are never returned: the row of the session for "Elsewhere" is in its own list only.
        using (var own = await other.Owner.Client.GetAsync($"/api/households/{other.Id}/support-access"))
        {
            Assert.Equal(1, (await own.JsonAsync()).GetProperty("total").GetInt32());
        }

        using var mine = await target.Owner.Client.GetAsync($"/api/households/{target.Id}/support-access");
        Assert.Equal(0, (await mine.JsonAsync()).GetProperty("total").GetInt32());
    }
}

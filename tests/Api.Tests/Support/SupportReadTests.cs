using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Support;

/// <summary>Records every SQL command the API runs, with its parameter values (T-096 tests).</summary>
internal sealed class CommandLog : DbCommandInterceptor
{
    private readonly List<(string Sql, string[] Values)> _commands = [];

    public IReadOnlyList<(string Sql, string[] Values)> Commands
    {
        get
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }
    }

    public void Clear()
    {
        lock (_commands)
        {
            _commands.Clear();
        }
    }

    /// <summary>Tables written (INSERT, UPDATE, DELETE, MERGE) since the last <see cref="Clear"/>.</summary>
    public string[] WrittenTables() =>
        [.. Commands
            .SelectMany(c => Regex.Matches(c.Sql, @"\b(?:INSERT INTO|UPDATE|DELETE FROM|MERGE INTO|MERGE)\s+\[(\w+)\]").Select(m => m.Groups[1].Value))
            .Distinct()
            .Order(StringComparer.Ordinal)];

    private void Add(System.Data.Common.DbCommand command)
    {
        var values = command.Parameters.Cast<System.Data.Common.DbParameter>().Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).ToArray();
        lock (_commands)
        {
            _commands.Add((command.CommandText, values));
        }
    }

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Add(command);
        return result;
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Add(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>T-096: what support can see, and that it sees only the household of its session.</summary>
public sealed class SupportReadTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly Regex GuidPattern = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static string[] ReadPaths(string week) =>
        ["", "/members", "/invites", "/profiles", $"/meal-plans/{week}", $"/shopping-lists/{week}", "/recipes", "/access-log"];

    private static HashSet<Guid> GuidsIn(string text) => [.. GuidPattern.Matches(text).Select(m => Guid.Parse(m.Value))];

    /// <summary>Every id of the household's rows, by raw SQL, outside the tenant filter.</summary>
    private async Task<HashSet<Guid>> RawIdsAsync(Guid household)
    {
        var ids = new HashSet<Guid> { household };
        foreach (var table in new[] { "Memberships", "Invites", "MemberProfiles", "Recipes", "MealPlans", "MealPlanEntries", "RecipeSteps", "RecipeIngredients" })
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(db.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT Id FROM {table} WHERE HouseholdId = @h";
            command.Parameters.AddWithValue("@h", household);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        return ids;
    }

    [Fact]
    public async Task Admin_without_a_session_gets_support_session_required_and_one_denied_audit_row()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        using var response = await admin.Client.SupportGetAsync(target.Id, "/members");

        await response.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-required");
        var denied = Assert.Single(await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id));
        Assert.Equal("access.denied", denied.Action);
        Assert.Equal("Denied", denied.Outcome);
        Assert.Equal(target.Id, denied.TargetHouseholdId);
        Assert.Contains("support-session-required", denied.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Owner.User.Email, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_session_for_A_cannot_read_B_and_sees_only_A_data()
    {
        var email = SupportHost.NewAdminEmail();
        var commands = new CommandLog();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.ConfigureDbContext<Foodplanner.Infrastructure.Persistence.AppDbContext>(o => o.AddInterceptors(commands)));
        var a = await host.NewHouseholdAsync("Alpha Household");
        var b = await host.NewHouseholdAsync("Bravo Household");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(a.Id);
        var aIds = await RawIdsAsync(a.Id);
        aIds.UnionWith([a.Owner.User.Id, a.Adult.User.Id, a.Child.User.Id]);
        var bIds = await RawIdsAsync(b.Id);
        bIds.UnionWith([b.Owner.User.Id, b.Adult.User.Id, b.Child.User.Id]);
        Assert.True(bIds.Count > 8, "household B has data in every table"); // not vacuous

        // A's data: only A's ids appear, and the members are A's.
        foreach (var path in ReadPaths(a.Week))
        {
            using var response = await admin.Client.SupportGetAsync(a.Id, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var seen = GuidsIn(text);
            Assert.True(seen.IsSubsetOf(aIds), $"{path} returned an id outside household A");
            Assert.False(seen.Overlaps(bIds), $"{path} returned an id of household B");
            Assert.DoesNotContain("Bravo", text, StringComparison.Ordinal);
        }

        // B through A's session: refused, the body names nothing of B, and a denied row records the attempt.
        commands.Clear();
        foreach (var path in ReadPaths(b.Week))
        {
            using var response = await admin.Client.SupportGetAsync(b.Id, path);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "support-session-mismatch");
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(b.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bravo", text, StringComparison.Ordinal);
            Assert.DoesNotContain(b.Owner.User.Email, text, StringComparison.Ordinal);
        }

        var denied = (await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id)).Where(r => r.Action == "access.denied").ToList();
        Assert.Equal(ReadPaths(b.Week).Length, denied.Count);
        Assert.All(denied, r =>
        {
            Assert.Equal(b.Id, r.TargetHouseholdId);
            Assert.Null(r.TargetHouseholdName); // the denied attempt reads nothing of household B, not even its name
        });

        // The household of every query is the session's, never the route's: no command except the audit insert carries B's id.
        var withRouteId = commands.Commands.Where(c => c.Values.Contains(b.Id.ToString(), StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.All(withRouteId, c => Assert.Contains("SupportAuditEntries", c.Sql, StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Commands, c => c.Sql.Contains("FROM [Memberships]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_endpoints_write_only_the_audit_row()
    {
        var email = SupportHost.NewAdminEmail();
        var commands = new CommandLog();
        await using var host = SupportHost.Create(
            db.ConnectionString, email, services => services.ConfigureDbContext<Foodplanner.Infrastructure.Persistence.AppDbContext>(o => o.AddInterceptors(commands)));
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id);

        foreach (var path in ReadPaths(target.Week))
        {
            commands.Clear();
            using var response = await admin.Client.SupportGetAsync(target.Id, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["SupportAuditEntries"], commands.WrittenTables());
        }

        foreach (var url in new[] { "/api/support/households", "/api/support/households?search=Hansen", "/api/support/users?email=abc", "/api/support/sessions/current" })
        {
            commands.Clear();
            using var response = await admin.Client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["SupportAuditEntries"], commands.WrittenTables());
        }

        var rows = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        foreach (var action in new[] { "view.overview", "view.members", "view.invites", "view.profiles", "view.meal-plan", "view.shopping-list", "view.recipes", "view.access-log" })
        {
            Assert.Single(rows, r => r.Action == action);
        }
    }

    [Fact]
    public async Task Overview_members_invites_profiles_and_plan_show_what_support_needs()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync("Støtte");
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id);

        using var overview = await admin.Client.SupportGetAsync(target.Id, string.Empty);
        var counts = await overview.JsonAsync();
        Assert.Equal("Støtte", counts.GetProperty("name").GetString());
        Assert.Equal(3, counts.GetProperty("memberCount").GetInt32());
        Assert.Equal(1, counts.GetProperty("ownerCount").GetInt32());
        Assert.Equal(1, counts.GetProperty("openInviteCount").GetInt32());
        Assert.Equal(1, counts.GetProperty("profileCount").GetInt32());
        Assert.Equal(1, counts.GetProperty("recipeCount").GetInt32());
        Assert.Equal(1, counts.GetProperty("mealPlanCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, counts.GetProperty("plan").ValueKind); // household plans arrive with T-123

        using var members = await admin.Client.SupportGetAsync(target.Id, "/members");
        var list = (await members.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, m => m.GetProperty("email").GetString() == target.Owner.User.Email && m.GetProperty("role").GetString() == "Owner");
        Assert.All(list, m => Assert.False(m.GetProperty("pinLocked").GetBoolean()));

        using var invites = await admin.Client.SupportGetAsync(target.Id, "/invites");
        var invite = Assert.Single((await invites.JsonAsync()).EnumerateArray(), i => i.GetProperty("id").GetGuid() == target.OpenInvite);
        Assert.Equal("Open", invite.GetProperty("status").GetString());

        using var profiles = await admin.Client.SupportGetAsync(target.Id, "/profiles");
        Assert.Equal("Mia", Assert.Single((await profiles.JsonAsync()).EnumerateArray()).GetProperty("displayName").GetString());

        using var plan = await admin.Client.SupportGetAsync(target.Id, $"/meal-plans/{target.Week}");
        Assert.Single((await plan.JsonAsync()).GetProperty("entries").EnumerateArray());

        using var badWeek = await admin.Client.SupportGetAsync(target.Id, "/meal-plans/2026-03-03");
        Assert.Equal(HttpStatusCode.BadRequest, badWeek.StatusCode);

        using var recipes = await admin.Client.SupportGetAsync(target.Id, "/recipes");
        var page = await recipes.JsonAsync();
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        // Titles only: no recipe detail.
        Assert.Equal(["createdAt", "id", "title"], page.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Households_can_be_listed_paged_and_searched_with_only_the_listed_fields()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var first = await host.NewHouseholdAsync($"Zz{tag} one");
        await host.NewHouseholdAsync($"Zz{tag} two");
        await host.NewHouseholdAsync($"Zz{tag} three");
        var admin = await host.SignInAsAsync(email);

        // Listing everything is paged: the total counts them all, a page holds at most 20.
        using var all = await admin.Client.GetAsync("/api/support/households?pageSize=100");
        var allPage = await all.JsonAsync();
        Assert.True(allPage.GetProperty("total").GetInt32() >= 3);
        Assert.Equal(20, allPage.GetProperty("pageSize").GetInt32());
        Assert.True(allPage.GetProperty("items").GetArrayLength() <= 20);

        using var search = await admin.Client.GetAsync($"/api/support/households?search=Zz{tag}&pageSize=2");
        var firstPage = await search.JsonAsync();
        Assert.Equal(3, firstPage.GetProperty("total").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        Assert.Equal(["createdAt", "id", "name"], firstPage.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        using var second = await admin.Client.GetAsync($"/api/support/households?search=Zz{tag}&pageSize=2&page=2");
        Assert.Single((await second.JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal([$"Zz{tag} one", $"Zz{tag} three"], firstPage.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));

        // By exact household id.
        using var byId = await admin.Client.GetAsync($"/api/support/households?search={first.Id}");
        Assert.Equal(first.Id, Assert.Single((await byId.JsonAsync()).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var rows = await SupportHost.AuditRowsAsync(db.ConnectionString, admin.User.Id);
        Assert.All(rows.Where(r => r.Action == "search.households"), r => Assert.Null(r.TargetHouseholdId));
        Assert.Contains(rows, r => r.Action == "search.households" && r.Detail!.Contains($"Zz{tag}", StringComparison.Ordinal) && r.Detail.Contains("resultCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_search_shows_only_the_listed_fields_and_no_response_holds_a_secret()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        await target.Owner.Client.SetPinAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id);
        var prefix = target.Owner.User.Email[..12];

        using var users = await admin.Client.GetAsync($"/api/support/users?email={prefix}");
        var found = await users.JsonAsync();
        var owner = Assert.Single(found.EnumerateArray(), u => u.GetProperty("userId").GetGuid() == target.Owner.User.Id);
        Assert.Equal(new[] { "email", "emailConfirmed", "households", "userId" }.Order(StringComparer.Ordinal), owner.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var household = Assert.Single(owner.GetProperty("households").EnumerateArray());
        Assert.Equal(["id", "name", "role"], household.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Owner", household.GetProperty("role").GetString());

        // No hash, stamp, PIN data or token anywhere in what support receives.
        var secrets = new List<string>();
        foreach (var (table, column) in new[] { ("AspNetUsers", "PasswordHash"), ("AspNetUsers", "SecurityStamp"), ("AspNetUsers", "ConcurrencyStamp"), ("UserPins", "Hash") })
        {
            var value = (string?)await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT TOP 1 {column} FROM {table} WHERE {(table == "UserPins" ? "UserId" : "Id")} = @id", ("@id", target.Owner.User.Id));
            Assert.False(string.IsNullOrEmpty(value));
            secrets.Add(value!);
        }

        var bodies = new List<string> { found.GetRawText() };
        foreach (var path in ReadPaths(target.Week))
        {
            using var response = await admin.Client.SupportGetAsync(target.Id, path);
            bodies.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        foreach (var body in bodies)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            }

            Assert.DoesNotMatch(@"(?i)passwordhash|securitystamp|concurrencystamp|tokenhash|""hash""|failedcount", body);
        }
    }

    [Fact]
    public async Task Search_limits_and_the_sixty_first_search_in_a_minute_is_429()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email, configure: null, ("RateLimiting:SupportSearch:PermitLimit", "60"));
        var admin = await host.SignInAsAsync(email);

        using (var one = await admin.Client.GetAsync("/api/support/households?search=a"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, one.StatusCode);
            Assert.True((await one.JsonAsync()).GetProperty("errors").TryGetProperty("search", out _));
        }

        using (var two = await admin.Client.GetAsync("/api/support/users?email=ab"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
            Assert.True((await two.JsonAsync()).GetProperty("errors").TryGetProperty("email", out _));
        }

        using (var none = await admin.Client.GetAsync("/api/support/users"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        }

        // At most 20 results.
        for (var i = 0; i < 22; i++)
        {
            await host.CreateUserAsync($"capuser-{i:00}-{Guid.NewGuid():N}@example.test");
        }

        using (var capped = await admin.Client.GetAsync("/api/support/users?email=capuser-"))
        {
            Assert.Equal(20, (await capped.JsonAsync()).GetArrayLength());
        }

        // 3 requests were made above (and one more just now); the rest of the minute's budget is 56 more, the next one is refused.
        for (var i = 0; i < 56; i++)
        {
            using var ok = await admin.Client.GetAsync("/api/support/households?search=zz");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await admin.Client.GetAsync("/api/support/households?search=zz");
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.Contains("Retry-After"));
    }
}

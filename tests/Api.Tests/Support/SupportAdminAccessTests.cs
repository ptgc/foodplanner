using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Domain.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Support;

/// <summary>T-096: how an account becomes a system admin, and that nobody else can reach any support route.</summary>
public sealed class SupportAdminAccessTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static async Task<bool> IsAdminAsync(ApiClient client) => (await client.MeAsync()).GetProperty("isSystemAdmin").GetBoolean();

    [Fact]
    public async Task Allow_listed_confirmed_email_is_admin()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email.ToUpperInvariant());

        var admin = await host.SignInAsAsync(email);
        var other = await host.SignInAsync();

        Assert.True(await IsAdminAsync(admin.Client));
        Assert.False(await IsAdminAsync(other.Client));
        using var allowed = await admin.Client.GetAsync("/api/support/households");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var refused = await other.Client.GetAsync("/api/support/households");
        await refused.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Unconfirmed_allow_listed_account_is_not_admin()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var admin = await host.SignInAsAsync(email);
        Assert.True(await IsAdminAsync(admin.Client));

        // The mailbox is no longer confirmed (for example the address was changed back to unconfirmed): admin status goes with it.
        await SupportHost.ExecuteAsync(db.ConnectionString, "UPDATE AspNetUsers SET EmailConfirmed = 0 WHERE Id = @id", ("@id", admin.User.Id));

        Assert.False(await IsAdminAsync(admin.Client));
        using var refused = await admin.Client.GetAsync("/api/support/households");
        await refused.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Registering an allow-listed address as a new, unconfirmed account does not make it an admin either.
        var registered = SupportHost.NewAdminEmail();
        await using var second = SupportHost.Create(db.ConnectionString, registered);
        using var register = await second.CreateClient().PostAsync("/api/auth/register", new { email = registered });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var created = await second.FindUserAsync(registered);
        Assert.False(created.EmailConfirmed);
        using var scope = second.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ISystemAdminDirectory>().IsAdminAsync(created.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admin_status_cannot_be_self_assigned()
    {
        var listed = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, listed);
        var user = await host.SignInAsync();
        var household = await user.Client.CreateHouseholdAsync();

        // Headers, query strings and body fields of every kind: none of them changes who is an admin.
        var headers = new Dictionary<string, string> { ["X-System-Admin"] = "true", ["X-Role"] = "SystemAdmin", ["X-Is-System-Admin"] = "1" };
        using var withHeaders = await user.Client.GetAsync("/api/support/households?isSystemAdmin=true&role=SystemAdmin", headers);
        await withHeaders.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var inBody = await user.Client.PostAsync(
            "/api/support/sessions",
            new { householdId = household, mode = "Read", reasonCategory = "Other", reasonNote = SupportHost.Note, isSystemAdmin = true, role = "SystemAdmin" },
            headers);
        await inBody.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var createHousehold = await user.Client.PostAsync("/api/households", new { name = "Mine", isSystemAdmin = true, role = "SystemAdmin" });
        Assert.Equal(HttpStatusCode.Created, createHousehold.StatusCode);

        // Registering the allow-listed address, or an invite carrying it, gives nothing: the address is not confirmed by whoever types it.
        using var register = await host.CreateClient().PostAsync("/api/auth/register", new { email = listed, isSystemAdmin = true });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        Assert.False(await IsAdminAsync(user.Client));
        Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE AdminUserId = @id", ("@id", user.User.Id)));
    }

    [Fact]
    public async Task Revoking_admin_takes_effect_on_next_request_and_adding_it_back_needs_no_new_login()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id);
        using var before = await admin.Client.SupportGetAsync(target.Id, "/members");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var configuration = (IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>();
        configuration["Support:SystemAdminEmails:0"] = string.Empty;

        // Same cookie: every support route, the open session and /me all answer as for a stranger.
        using var members = await admin.Client.SupportGetAsync(target.Id, "/members");
        await members.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var search = await admin.Client.GetAsync("/api/support/households");
        await search.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var current = await admin.Client.GetAsync("/api/support/sessions/current");
        await current.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.False(await IsAdminAsync(admin.Client));

        // Granted again: the cookie still works and the (unexpired) session is usable again.
        configuration["Support:SystemAdminEmails:0"] = email;
        Assert.True(await IsAdminAsync(admin.Client));
        using var again = await admin.Client.SupportGetAsync(target.Id, "/members");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Admin_in_kid_mode_has_no_admin_powers_on_any_support_endpoint()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var admin = await host.SignInAsAsync(email);
        var household = await admin.Client.CreateHouseholdAsync();
        await admin.Client.SetPinAsync();
        var profile = await admin.Client.CreateProfileAsync(household, "Mia", "fox");
        Assert.True(await IsAdminAsync(admin.Client));

        await admin.Client.EnterKidModeAsync(household, profile);

        Assert.False(await IsAdminAsync(admin.Client));
        foreach (var call in SupportCalls(host, household))
        {
            using var response = await admin.Client.SendAsync(call.Method, call.Url);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Non_admin_gets_403_on_every_support_endpoint_and_anonymous_gets_401_and_nothing_is_written()
    {
        var listed = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, listed);
        var target = await host.NewHouseholdAsync();
        var kid = await host.NewScenarioAsync();
        await kid.Owner.Client.EnterKidModeAsync(kid.Household, kid.Profile1);
        var stranger = await host.SignInAsync();

        var calls = SupportCalls(host, target.Id).ToList();
        Assert.True(calls.Count >= 17, $"found {calls.Count} support endpoints"); // not vacuous

        foreach (var caller in new[] { target.Owner, target.Adult, target.Child, kid.Owner, stranger })
        {
            foreach (var call in calls)
            {
                using var response = await caller.Client.SendAsync(call.Method, call.Url);
                await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }

            Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM SupportSessions WHERE AdminUserId = @id", ("@id", caller.User.Id)));
            Assert.Empty(await SupportHost.AuditRowsAsync(db.ConnectionString, caller.User.Id));
        }

        using var anonymous = host.CreateClient();
        foreach (var call in calls)
        {
            using var response = await anonymous.SendAsync(call.Method, call.Url);
            await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        }
    }

    [Fact]
    public async Task Admin_without_membership_cannot_use_household_routes_with_or_without_a_session()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);

        async Task AssertRefusedAsync()
        {
            foreach (var path in new[] { "/recipes", $"/meal-plans/{target.Week}", "/members", "/profiles", "/invites" })
            {
                using var response = await admin.Client.GetAsync($"/api/households/{target.Id}{path}");
                await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }

            using var delete = await admin.Client.DeleteAsync($"/api/households/{target.Id}");
            await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            Assert.DoesNotContain(await admin.Client.ListHouseholdsAsync(), h => h.Id == target.Id);
        }

        await AssertRefusedAsync();
        await admin.Client.StartOkAsync(target.Id);
        await AssertRefusedAsync();
        await admin.Client.StartOkAsync(target.Id, "Write");
        await AssertRefusedAsync();

        Assert.Equal(1, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM Households WHERE Id = @id", ("@id", target.Id)));
    }

    [Fact]
    public async Task Support_session_does_not_grant_membership_policies()
    {
        var email = SupportHost.NewAdminEmail();
        await using var host = SupportHost.Create(db.ConnectionString, email);
        var target = await host.NewHouseholdAsync();
        var admin = await host.SignInAsAsync(email);
        await admin.Client.StartOkAsync(target.Id, "Write");

        foreach (var policy in new[] { "member", "adult", "owner", "unprotected" })
        {
            using var response = await admin.Client.GetAsync($"/api/households/{target.Id}/test/{policy}");
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // The real members of the same household are admitted by the same policies.
        using var ok = await target.Owner.Client.GetAsync($"/api/households/{target.Id}/test/owner");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public void More_than_five_admin_addresses_fail_startup_and_five_start()
    {
        static void Start(int count)
        {
            using var factory = new TestApiFactory().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    Enumerable.Range(0, count).ToDictionary(i => $"Support:SystemAdminEmails:{i}", i => (string?)$"admin{i}@example.test"))));
            using var client = factory.CreateClient();
        }

        var failure = Assert.ThrowsAny<Exception>(() => Start(6));
        Assert.Contains("SystemAdminEmails", string.Join(" | ", Chain(failure).Select(e => e.Message)), StringComparison.Ordinal);
        Start(5);
    }

    [Fact]
    public void The_repository_configuration_names_no_admin_address()
    {
        var root = Architecture.FindRoot();
        var settings = Directory.GetFiles(Path.Combine(root, "src", "Api"), "appsettings*.json");
        Assert.NotEmpty(settings);
        foreach (var file in settings)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (json.RootElement.TryGetProperty("Support", out var support) && support.TryGetProperty("SystemAdminEmails", out var list))
            {
                Assert.Equal(0, list.GetArrayLength());
            }
        }

        // The infrastructure parameters, if any, may only reference a Key Vault secret for the list.
        foreach (var file in Directory.EnumerateFiles(root, "*.bicepparam", SearchOption.AllDirectories).Where(f => !f.Contains("node_modules", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(@"SystemAdminEmails[^\n]*@", File.ReadAllText(file));
        }
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            yield return exception;
        }
    }

    /// <summary>Every endpoint under /api/support with a URL a request can be sent to (ids are made up, which is fine for a refusal).</summary>
    internal static List<(HttpMethod Method, string Url)> SupportCalls(AuthHost host, Guid household)
    {
        var calls = new List<(HttpMethod, string)>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!raw.StartsWith("/api/support", StringComparison.Ordinal))
            {
                continue;
            }

            var url = System.Text.RegularExpressions.Regex.Replace(raw, @"\{(\w+)[^}]*\}", m => m.Groups[1].Value switch
            {
                "weekStart" => "2026-03-02",
                "targetHouseholdId" => household.ToString(),
                _ => Guid.NewGuid().ToString(),
            });
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                calls.Add((new HttpMethod(method), url));
            }
        }

        return calls;
    }
}

/// <summary>Finds the repository root from the test output folder.</summary>
internal static class Architecture
{
    public static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foodplanner.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }
}
